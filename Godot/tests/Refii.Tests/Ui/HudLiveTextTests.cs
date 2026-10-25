namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The HUD's live-text paint rule (<see cref="Engine_HudView.LiveTextToPaint"/>): the text stated
/// for the element's name wins, an element still holding the string the file authors was never
/// written and paints blank, and `TargetedQueryButton` keeps its authored text. Pure model code —
/// no Godot type is built.
/// </summary>
[TestSuite]
public class HudLiveTextTests
{
	private const string QuestBox = "group.QuestBox.TopBox";

	[TestCase]
	public void TheAuthoredPlaceholderIsUnwrittenAndPaintsBlank()
	{
		// The quest box the guest never wrote in this HUD instance: its elements hold the file's
		// "Text", and the slab used to say so.
		AssertThat(Engine_HudView.LiveTextToPaint(QuestBox, "Text", "Text", null)).IsEqual(string.Empty);
		AssertThat(Engine_HudView.LiveTextToPaint(QuestBox, null, "Text", null)).IsEqual(string.Empty);
		AssertThat(Engine_HudView.LiveTextToPaint(QuestBox, string.Empty, "Text", null)).IsEqual(string.Empty);
	}

	[TestCase]
	public void TheGuestsOwnTextPaints()
	{
		AssertThat(Engine_HudView.LiveTextToPaint(QuestBox, "Find the abbot in Oakfield.", "Text", null))
			.IsEqual("Find the abbot in Oakfield.");
		// No authored string to compare against: the element's text as it is.
		AssertThat(Engine_HudView.LiveTextToPaint(QuestBox, "Text", null, null)).IsEqual("Text");
		AssertThat(Engine_HudView.LiveTextToPaint(QuestBox, "Text", string.Empty, null)).IsEqual("Text");
	}

	[TestCase]
	public void TheStatedTextWinsAndAnEmptyStatementClears()
	{
		// The host's TopBox body for TopMain (Engine_UiHudTopBox.Publish) over the element's text.
		AssertThat(Engine_HudView.LiveTextToPaint(QuestBox, "Text", "Text", "Poppy the Stall Vendor"))
			.IsEqual("Poppy the Stall Vendor");
		AssertThat(Engine_HudView.LiveTextToPaint(QuestBox, "An old body", "Text", string.Empty))
			.IsEqual(string.Empty);
	}

	[TestCase]
	public void TargetedQueryButtonKeepsItsAuthoredText()
	{
		const string piece = "group.TargetedQuery.TargetedQueryButton.More";
		AssertThat(Engine_HudView.LiveTextToPaint(piece, null, "More Info", null)).IsEqual("More Info");
		AssertThat(Engine_HudView.LiveTextToPaint(piece, "More Info", "More Info", null)).IsEqual("More Info");
		// A text the guest did write to it still paints.
		AssertThat(Engine_HudView.LiveTextToPaint(piece, "Accept", "More Info", null)).IsEqual("Accept");
	}
}
