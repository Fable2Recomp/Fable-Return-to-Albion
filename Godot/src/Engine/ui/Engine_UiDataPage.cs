using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// THE DATA PROVIDER (<see cref="Engine_UiPageProviders.Data"/>): a screen's page filled from its
/// own record — <c>fields</c> (element path to a binding, the text that element says) and
/// <c>switches</c> (element path to a binding that turns it on or off) — through the one resolver
/// (<see cref="Engine_UiValueSource"/>). The highlighted row and the menu's player are the page's,
/// so a co-op player's screen reads their own <c>player:</c> values.
/// </summary>
/// <example>
/// <code>
/// "screens": { "1050": {
///   "name": "bestiary", "ownsList": true, "presentation": "logbookscreen", "menu": "mymod-bestiary",
///   "provider": "data", "chrome": [ "Foreground" ], "ownedElsewhere": [ "ExpandableMenu.Buttons", "MenuHighlight" ],
///   "fields": { "Foreground.Book.Pages.HelpPage.Title": "row:label",
///               "Foreground.Book.Pages.HelpPage.Text": "mymod.bestiary.{row}.text",
///               "Foreground.Book.Pages.HelpPage.Kills": "player:mymod.kills.{row}|0" },
///   "switches": { "Foreground.Book.Pages.HelpPage": "true", "Foreground.Book.Pages.StatsPage": "false" } } }
/// </code>
/// A pause row opens it with <c>Screen:open:1050</c>.
/// </example>
public sealed class Engine_UiDataPage : Engine_UiPageProvider
{
	public IReadOnlyDictionary<string, string> Values(in Engine_UiPageContext page)
	{
		if (Engine_UiTemplates.Screen(page.ScreenId) is not { Fields.Count: > 0 } record)
		{
			return Engine_UiPageProviders.NoValues;
		}

		Engine_UiValueSource.Lookups lookups = Lookups(page);
		return Fill(record.Fields, lookups);
	}

	public IReadOnlyDictionary<string, bool> Active(in Engine_UiPageContext page)
	{
		if (Engine_UiTemplates.Screen(page.ScreenId) is not { Switches.Count: > 0 } record)
		{
			return Engine_UiPageProviders.NoActive;
		}

		Engine_UiValueSource.Lookups lookups = Lookups(page);
		return Switch(record.Switches, lookups);
	}

	/// <summary>Every field that says something now; an unstated value is left out, so the element keeps its own text.</summary>
	public static Dictionary<string, string> Fill(IReadOnlyDictionary<string, string> fields, in Engine_UiValueSource.Lookups lookups)
	{
		var values = new Dictionary<string, string>(System.StringComparer.Ordinal);
		foreach ((string path, string binding) in fields)
		{
			if (Engine_UiValueSource.Resolve(binding, lookups) is { } text)
			{
				values[path] = text;
			}
		}

		return values;
	}

	/// <summary>Every switch, on or off.</summary>
	public static Dictionary<string, bool> Switch(IReadOnlyDictionary<string, string> switches, in Engine_UiValueSource.Lookups lookups)
	{
		var active = new Dictionary<string, bool>(System.StringComparer.Ordinal);
		foreach ((string path, string binding) in switches)
		{
			active[path] = Engine_UiValueSource.Truth(binding, lookups);
		}

		return active;
	}

	/// <summary>The live lookups: published mod values, the menu player's store, the page's string book.</summary>
	private static Engine_UiValueSource.Lookups Lookups(in Engine_UiPageContext page)
	{
		Refii.Text.Engine_BabelDatabase? strings = page.Strings;
		int player = Engine_UiModMenus.MenuPlayer;
		Engine_UiExpandableMenu.Item? row = page.Row;
		return new Engine_UiValueSource.Lookups(
			Values: Engine_UiValueSource.Live(player),
			Tag: tag => strings is not null && strings.TryGetString(tag, out string text) && text.Length != 0 ? text : null,
			RowName: row?.Name ?? string.Empty,
			RowLabel: row?.StatedLabel ?? string.Empty);
	}
}
