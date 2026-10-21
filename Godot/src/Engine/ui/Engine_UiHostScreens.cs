using System.Collections.Generic;
using System.Globalization;

namespace Refii.Ui;

/// <summary>
/// THE OPEN-SCREEN VERB, <c>Screen:open:&lt;id&gt;</c>: a host-owned screen by id. A screen with a
/// behaviour of its own registers an opener (the multiplayer book); every other host screen is a
/// <c>screens</c> record — the game's achievements (1013) or a mod's, id
/// <see cref="Engine_UiBookSet.HostOnlyScreenId"/> and up, stating a <c>menu</c> and a
/// <c>presentation</c> — stood by <see cref="Engine_UiHostScreen"/> through the
/// <see cref="Books"/> opener the owner of the in-game UI installs.
/// </summary>
public static class Engine_UiHostScreens
{
	public const string OpenPrefix = "Screen:open:";

	/// <summary>The command a row states to open screen <paramref name="screenId"/>.</summary>
	public static string OpenCommand(uint screenId) => OpenPrefix + screenId.ToString(CultureInfo.InvariantCulture);

	/// <summary>Opens a screen for <paramref name="player"/> (1 or 2; 0 for whoever owns the menus here).</summary>
	public delegate Engine_UiCommandResult Opener(uint screenId, int player, Engine_UiCommandContext context);

	private static readonly Dictionary<uint, Opener> Openers = new();

	/// <summary>Registers the opener of a screen with a behaviour of its own.</summary>
	public static void Register(uint screenId, Opener open) => Openers[screenId] = open;

	/// <summary>
	/// Stands a template book screen (<see cref="Engine_UiHostScreen"/>); null while no in-game UI is
	/// up to stand one.
	/// </summary>
	public static Opener? Books { get; set; }

	/// <summary>The id a <c>Screen:open:</c> argument names, or null.</summary>
	public static uint? ParseId(string argument) =>
		uint.TryParse(argument.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ? id : null;

	/// <summary>The verb's handler.</summary>
	public static Engine_UiCommandResult Open(string argument, Engine_UiCommandContext context)
	{
		if (ParseId(argument) is not { } id)
		{
			return Engine_UiCommandResult.Refused($"'{context.Command}' names no screen id (Screen:open:<id>)");
		}

		if (Openers.TryGetValue(id, out Opener? opener))
		{
			return opener(id, context.Player, context);
		}

		if (id < Engine_UiBookSet.HostOnlyScreenId)
		{
			return Engine_UiCommandResult.Refused($"screen {id} is the guest's — the guest opens its own screens");
		}

		if (Engine_UiTemplates.Screen(id) is not { } record)
		{
			return Engine_UiCommandResult.Refused($"no screen {id} in any ui/templates.json");
		}

		if (record.Menu.Length == 0 || record.Presentation.Length == 0)
		{
			return Engine_UiCommandResult.Refused($"screen {id} states no menu or no presentation, so there is nothing to stand");
		}

		return Books is { } books
			? books(id, context.Player, context)
			: Engine_UiCommandResult.Refused($"screen {id} stands in a game, over the pause book; no game UI is up");
	}
}
