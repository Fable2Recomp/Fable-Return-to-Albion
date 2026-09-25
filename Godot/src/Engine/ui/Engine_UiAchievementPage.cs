using System.Collections.Generic;
using Refii.Achievements;

namespace Refii.Ui;

/// <summary>
/// THE ACHIEVEMENTS SCREEN — its own surface, on the logbook's furniture.
///
/// The guest has no screen for these. Its answer to the logbook's `Achievements` row is
/// `XamShowAchievementsUI` (`sub_826D9620` case 150), the console's guide blade, which on this
/// platform opens nothing — so there is no guest screen id and no tab for `sub_826B89E0`'s
/// switch. The list is host-owned end to end and reached from its own pause tab.
///
/// It stands on `logbookscreen.bgf` because the shape already fits: a full-height row list and a
/// subject page laying out a title, a 64-unit picture and a paragraph, which is exactly an
/// achievement. One row per achievement out of <see cref="Engine_Achievements"/>, locked ones
/// ghosted and reading their locked text, with an authored summary row carrying the tally.
///
/// It used to unfold inside the logbook instead, under the guest's own row. That put a second
/// list inside a screen the guest also drives, and there are far too many achievements for one
/// fold — see <see cref="ScreenId"/> for where they went.
/// </summary>
public static class Engine_UiAchievementPage
{
	public const int EventType = 150;

	public const string SourceName = "achievements";

	/// <summary>
	/// The host-only screen id this list stands as — past
	/// <see cref="Engine_UiBookSet.HostOnlyScreenId"/>, so the guest's manager never reports it
	/// and the registry walk never tries to find a live presentation for it.
	/// </summary>
	public const uint ScreenId = 1013u;

	/// <summary>The screen's list, as <see cref="Engine_UiTemplates"/> names it.</summary>
	public const string MenuName = "achievements-root";

	/// <summary>The authored summary row — the tally, and where the cursor starts.</summary>
	public const string FolderRowName = "Achievements";

	private const string PageRoot = "Foreground.Book.Pages.ExpressionsPage";
	private const string Title = "ExpressionsPage.Title";
	private const string Icon = "ExpressionsPage.Contents.Icon";
	private const string Description = "ExpressionsPage.Contents.Description";
	private const string Effects = "ExpressionsPage.Contents.Effects";

	/// <summary>Every other subject page on the screen, shut while an achievement is up.</summary>
	private static readonly string[] OtherPages =
	{
		"Foreground.Book.Pages.MoralityPage", "Foreground.Book.Pages.CompletedQuestsPage",
		"Foreground.Book.Pages.RenownPage", "Foreground.Book.Pages.AttractivenessPage",
		"Foreground.Book.Pages.StatsPage", "Foreground.Book.Pages.DogPage",
		"Foreground.Book.Pages.HelpPage",
	};

	private static readonly Dictionary<string, string> NoValues = new();
	private static readonly Dictionary<string, uint> NoTextures = new();
	private static readonly Dictionary<string, bool> NoActive = new();

	/// <summary>One row per achievement, in the title's order. Locked rows are ghosted.</summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Rows(string argument)
	{
		var rows = new List<Engine_UiMenuSources.Row>(Engine_Achievements.TotalCount);
		foreach (AchievementDefinition definition in Engine_Achievements.Definitions)
		{
			// Locked or earned is the tab face — slate for a locked row, leather for an earned one
			// (Engine_FrontEndMenuView.LockedRowsSlate).
			string name = definition.Name.Length == 0 ? $"Achievement {definition.Id}" : definition.Name;
			rows.Add(new Engine_UiMenuSources.Row(
				Name: $"Achievement:{definition.Id}",
				Label: name,
				Icon: string.Empty,
				EventType: EventType,
				Id: definition.Id,
				Rating: 0,
				Committable: false,
				Ghosted: !Engine_Achievements.IsUnlocked(definition.Id)));
		}

		return rows;
	}

	/// <summary>True for the folder row and every achievement row under it.</summary>
	public static bool Owns(Engine_UiExpandableMenu.Item? row) =>
		row is { EventType: EventType };

	private static AchievementDefinition? DefinitionOf(Engine_UiExpandableMenu.Item row) =>
		row.Name.StartsWith("Achievement:", System.StringComparison.Ordinal)
			? Engine_Achievements.Definition(((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu))
			: null;

	public static IReadOnlyDictionary<string, string> Values(Engine_UiExpandableMenu.Item? row)
	{
		if (!Owns(row))
		{
			return NoValues;
		}

		var values = new Dictionary<string, string>();
		if (DefinitionOf(row!) is not { } definition)
		{
			// The folder itself: the tally.
			values[Title] = "Achievements";
			values[Description] = Engine_Achievements.DefinitionsLoaded
				? $"{Engine_Achievements.UnlockedCount} of {Engine_Achievements.TotalCount} unlocked\n" +
				  $"{Engine_Achievements.UnlockedGamerscore} of {Engine_Achievements.TotalGamerscore} gamerscore"
				: "The title's achievement list has not arrived yet.";
			return values;
		}

		bool unlocked = Engine_Achievements.IsUnlocked(definition.Id);
		string text = unlocked || definition.LockedDescription.Length == 0
			? definition.UnlockedDescription
			: definition.LockedDescription;
		string status = unlocked
			? Engine_Achievements.UnlockedAt(definition.Id) is { } when
				? $"Unlocked {when:d MMMM yyyy}"
				: "Unlocked"
			: "Locked";

		values[Title] = definition.Name;
		// Two lines, not one joined by a middle dot: the shipped bitmap font draws U+00B7 as `»`.
		values[Description] = $"{text}\n\n{definition.Gamerscore} G\n{status}";
		return values;
	}

	/// <summary>The icon quad, textured with the achievement's PNG through the host registry.</summary>
	public static IReadOnlyDictionary<string, uint> Textures(Engine_UiExpandableMenu.Item? row)
	{
		if (!Owns(row) || DefinitionOf(row!) is not { } definition ||
			Engine_Achievements.Icon(definition.Id) is not { } icon)
		{
			return NoTextures;
		}

		return new Dictionary<string, uint> { [Icon] = Engine_UiHostTextures.Register(icon) };
	}

	/// <summary>
	/// THIS SCREEN STATES NO SLIDES, and that is the statement, not an omission.
	///
	/// A host slide is not a patch onto the guest's array — `Engine_UiOwnedBook.Slid` builds the
	/// whole array the moment the host names one element, filling every OTHER ordinal with 0.
	/// Zero is "no slide selected", which resolves each owner to its master baseline, and
	/// `logbookscreen`'s master hides nearly everything: naming `Foreground.Book` alone took the
	/// book from drawing to `0/677 node(s) drawing` — the guest's own logbook draws 359 of the
	/// same 677.
	///
	/// With no slide array at all the resolve skips its slide pass and every element stays
	/// unhidden, which is the right baseline for a screen with no live sample: the one thing
	/// that must be shut is the seven subject pages we are not on, and
	/// <see cref="Active"/> shuts those by name.
	/// </summary>
	/// <summary>
	/// The subject page the guest would have selected had it known the row: the expressions page
	/// on, the other seven off, and the page's expression-effects block off — its stats table
	/// has nothing to say about an achievement.
	/// </summary>
	public static IReadOnlyDictionary<string, bool> Active(Engine_UiExpandableMenu.Item? row)
	{
		if (!Owns(row))
		{
			return NoActive;
		}

		var active = new Dictionary<string, bool> { [PageRoot] = true, [Effects] = false };
		foreach (string page in OtherPages)
		{
			active[page] = false;
		}

		// Without an icon the placeholder art (an expression's) would show; hide the quad.
		if (DefinitionOf(row!) is not { } definition || Engine_Achievements.Icon(definition.Id) is null)
		{
			active[Icon] = false;
		}

		return active;
	}
}
