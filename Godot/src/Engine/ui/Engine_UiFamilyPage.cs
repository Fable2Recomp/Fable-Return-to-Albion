using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// THE WORLD MAP'S FAMILY PAGE — the one world-map page that had no provider at all.
///
/// `sub_829B04E0` fills it and is almost entirely a read. The collector `sub_8246F020` builds a
/// 52-byte record per member with the mood bands, the upkeep figures, the event line and the two
/// fast-travel graph answers ALREADY FORMATTED, and the page sets ten elements straight off it
/// with `sub_82425DD0` — the setter that takes finished text. Only the region differs: it is a
/// lookup (`sub_82518490` against the member's level at `record+16`) and is set by TAG with
/// `sub_82425F40`, so it is the only field Babel touches.
///
/// The member is chosen BY ORDINAL into the collector's vector, which is exactly the row id the
/// family list already carries — so no key is needed on either side.
///
/// `Group.Stats` (the portrait) and the `sub_829B08A8` home/travel button pair are NOT here:
/// the first is a texture the guest scales from a live entity field, the second a second
/// prompt decision (`sub_82470A78`) with its own predicates. Both want their own read rather
/// than a guess.
/// </summary>
public static class Engine_UiFamilyPage
{
	private const string Title = "FamilyPage.Contents.PageTitle";

	private const string Children = "FamilyPage.Contents.Group.ChildrenNames";

	private const string Region = "FamilyPage.Contents.Group.Stats.RegionName";

	private const string Distance = "FamilyPage.Contents.Group.Stats.DistanceAmount";

	private const string Travel = "FamilyPage.Contents.Group.Stats.TravelAmount";

	private const string Note = "FamilyPage.Contents.Group.Stats.Note";

	private const string DailyUpkeep = "FamilyPage.Contents.Group.Stats.DailyUpkeepAmount";

	private const string TotalUpkeep = "FamilyPage.Contents.Group.Stats.TotalUpkeepAmount";

	private const string Happiness = "FamilyPage.Contents.Group.Stats.OverallHappinessAmount";

	private const string HomeHappiness = "FamilyPage.Contents.Group.Stats.HomeHappinessAmount";

	private const string UpkeepHappiness = "FamilyPage.Contents.Group.Stats.UpkeepHappinessAmount";

	/// <summary>The A-button holder, which `sub_82426328` shows or hides outright.</summary>
	public const string AButton = "FamilyPage.Contents.AButton";

	/// <summary>Its label — TRAVEL, TRAIL_AND_TRAVEL or TRAIL, picked by the pump.</summary>
	private const string AButtonText = "FamilyPage.Contents.AButton.ButtonText";

	/// <summary>
	/// <summary>Whether the family page's A-button stands for this member.</summary>
	public static bool PromptShown(uint ordinal) =>
		Refii.Host.HostBridge.GetFamilyDetails(ordinal) is { Known: true } member &&
		member.Prompt.Length > 0;

	/// <summary>
	/// The page for the member the row names. An unknown member states nothing rather than a
	/// set of empty lines, so the page keeps whatever it last had instead of blanking mid-walk.
	/// </summary>
	public static void Fill(
		Dictionary<string, string> values, uint ordinal,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		Refii.Host.HostBridge.FamilyDetails member =
			Refii.Host.HostBridge.GetFamilyDetails(ordinal);
		if (!member.Known)
		{
			return;
		}

		values[Title] = member.Name;
		values[Children] = member.Children;
		values[Distance] = member.Distance;
		values[Travel] = member.Travel;
		values[Note] = member.Note;
		values[DailyUpkeep] = member.DailyUpkeep;
		values[TotalUpkeep] = member.TotalUpkeep;
		values[Happiness] = member.Happiness;
		values[HomeHappiness] = member.HomeHappiness;
		values[UpkeepHappiness] = member.UpkeepHappiness;

		// THE ONE TAG ON THE PAGE. A level no region claims resolves to nothing, and the line
		// is blanked rather than left showing the authored placeholder.
		values[Region] = member.Region.Length == 0
			? string.Empty
			: (strings is { } book && book.TryGetString(member.Region, out string resolved)
				? resolved
				: string.Empty);

		// An empty prompt is the guest HIDING the button, not a failed read — two of
		// `sub_82470A78`'s branches return without writing any label.
		values[AButtonText] = member.Prompt.Length == 0
			? string.Empty
			: (strings is { } prompts &&
				prompts.TryGetString(member.Prompt, out string caption)
					? caption
					: string.Empty);
	}
}
