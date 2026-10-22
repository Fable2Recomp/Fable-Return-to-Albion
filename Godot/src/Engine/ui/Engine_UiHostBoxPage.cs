using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// THE HOST PAGE BOOK — a host question asked on a page (<see cref="Engine_UiHostBoxes.AskPage"/>):
/// the same list book the multiplayer and Active Mods screens stand on
/// (<see cref="Engine_UiListBookScreen"/> over <c>logbookscreen</c>), its rows Continue and Back,
/// its page the question's title and body in the book's scroll region. For a body whose
/// length is not ours to bound — the mods a save was made with — which the dialog's fixed text
/// box would run off; a page declares its scroll region and clips to <c>Mask</c>.
/// </summary>
/// <remarks>
/// The rows are commands: A on Continue is the question's yes, A on Back or B anywhere its no,
/// both through <see cref="Engine_UiHostBoxes.Answer"/>. The page reads the question channel
/// itself, so nothing is copied to show it.
/// </remarks>
public static class Engine_UiHostBoxPage
{
	/// <summary>Above <see cref="Engine_UiBookSet.HostOnlyScreenId"/>, beside the Active Mods book.</summary>
	public const uint ScreenId = 1016u;

	public const string MenuName = "hostbox-root";

	/// <summary>The rows' commands, dispatched by the book's <see cref="Engine_UiListBookScreen.Action"/>.</summary>
	public const string AcceptCommand = "HostBox:accept";

	public const string CancelCommand = "HostBox:cancel";

	private const string Title = "ExpressionsPage.Title";
	private const string Description = "ExpressionsPage.Contents.Description";

	private static readonly Dictionary<string, string> NoValues = new();

	/// <summary>The page: the question's title and body, as they stand.</summary>
	public static IReadOnlyDictionary<string, string> Values(Engine_UiExpandableMenu.Item? row)
	{
		if (Engine_UiHostBoxes.Question is not { } body)
		{
			return NoValues;
		}

		return new Dictionary<string, string>
		{
			[Title] = Engine_UiHostBoxes.PageTitle ?? string.Empty,
			[Description] = body,
		};
	}

	/// <summary>Our page on, every other subject page off — the borrowed page's own switch map.</summary>
	public static IReadOnlyDictionary<string, bool> Active(Engine_UiExpandableMenu.Item? row) =>
		Engine_UiMultiplayerPage.Active(row);
}
