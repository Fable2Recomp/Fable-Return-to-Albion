namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Probe = Refii.Titles.Fable3.Fable3ScreenModel.Probe;

/// <summary>
/// The box-manager screens past the first five, from a stated set: the cursor states, saved
/// settings, hidden rows and queries of the join/visibility, chat, bed, wedding, treasury and
/// save-menu classes (Fable3BoxScreenModel's citations), and the kind-to-root routing.
/// </summary>
[TestSuite]
public class Fable3BoxScreenModelTests
{
	private static string Resolve(string tag) => "<" + tag + ">";

	private static Probe State(int state) => new(state, false, null, null);

	private static Probe Text(string text) => new(null, false, text, null);

	[TestCase]
	public void KindsRouteToTheirElementRoots()
	{
		AssertThat(Fable3ScreenModel.RootOf("screen:join")).IsEqual("OrbEmote");
		AssertThat(Fable3ScreenModel.RootOf("screen:visibility")).IsEqual("Visibilities");
		AssertThat(Fable3ScreenModel.RootOf("screen:proposal")).IsEqual("InfoBox.MarriageProposal");
		AssertThat(Fable3ScreenModel.RootOf("screen:herostatus")).IsEqual("HeroStatus");
		AssertThat(Fable3ScreenModel.RootOf("screen:options")).IsNull();
	}

	[TestCase]
	public void JoinOptionsCursorIsTheRootAndTheTickIsTheSavedSetting()
	{
		var stated = new Dictionary<string, Probe>
		{
			["OrbEmote"] = State(3),
			["OrbEmote.Emotes"] = State(2),
			["OrbEmote.ThisEmote"] = Text("Invite Only"),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose("OrbEmote", stated, Resolve)!.Value;
		AssertThat(page.Title).IsEqual("<TEXT_GUI_JOIN_OPTIONS_TITLE>");
		AssertThat(page.Rows.Count).IsEqual(3);
		AssertThat(page.Selected).IsEqual(2);
		AssertThat(page.Rows[1].Line).IsEqual("<TEXT_GUI_JOIN_OPTIONS_INVITE_TITLE>    ✓");
		AssertThat(page.Rows[2].Description).IsEqual("TEXT_GUI_JOIN_OPTIONS_OFF_DESC");
		AssertThat(page.Cursor).IsTrue();
	}

	[TestCase]
	public void ChatChannelValueFollowsTheOnOffState()
	{
		var stated = new Dictionary<string, Probe>
		{
			["ChatChannel.OnOff"] = State(2),
			["ChatChannel.CurrSetting"] = Text("Audible"),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose("ChatChannel", stated, Resolve)!.Value;
		AssertThat(page.Rows[0].Line).Contains("<TEXT_GUI_AUDIO_CHAT_MUTED>");
		AssertThat(page.Description).IsEqual("Currently set to: Audible");
	}

	[TestCase]
	public void BedMenuSkipsTheHiddenRowAndHighlightsStateTwo()
	{
		var stated = new Dictionary<string, Probe>
		{
			["BedMenu.Sleep"] = State(2),
			["BedMenu.Sleep.Title"] = Text("Sleep"),
			["BedMenu.Sleep.Widget.Text"] = Text("Midday"),
			["BedMenu.Sleep.Description"] = Text("Sleep until midday"),
			["BedMenu.Sex"] = new(1, true, null, null),
			["BedMenu.Sex.Title"] = Text("Sex"),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose("BedMenu", stated, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(1);
		AssertThat(page.Selected).IsEqual(0);
		AssertThat(page.Rows[0].Line).IsEqual("Sleep    ◀ Midday ▶");
		AssertThat(page.Rows[0].Description).IsEqual("Sleep until midday");
	}

	[TestCase]
	public void WeddingPickerRowsAndTier()
	{
		var stated = new Dictionary<string, Probe>
		{
			["WeddingPicker.Title"] = Text("Brightwall"),
			["WeddingPicker.Details.WeddingDetails0"] = State(1),
			["WeddingPicker.Details.WeddingDetails0.Group.Title"] = Text("Poor"),
			["WeddingPicker.Details.WeddingDetails1"] = State(2),
			["WeddingPicker.Details.WeddingDetails1.Group.Title"] = Text("Standard"),
			["WeddingPicker.Details.WeddingDetails1.Group.Cost"] = Text("2000"),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose("WeddingPicker", stated, Resolve)!.Value;
		AssertThat(page.Title).IsEqual("Brightwall");
		AssertThat(page.Selected).IsEqual(1);
		AssertThat(page.Rows[1].Line).IsEqual("Standard    2000");
	}

	[TestCase]
	public void TreasuryQueryStandsOverThePage()
	{
		var stated = new Dictionary<string, Probe>
		{
			["TreasureAccounts"] = State(5),
			["TreasureAccounts.Personal.Personal Balance Value"] = Text("1200"),
			["TreasureAccounts.QueryBox.Group.QueryBox.Text"] = Text("Transfer 100?"),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose("TreasureAccounts", stated, Resolve)!.Value;
		AssertThat(page.Query).IsNotNull();
		AssertThat(page.Query!.Text).IsEqual("Transfer 100?");
		AssertThat(page.Rows[0].Line).IsEqual("Personal Balance    1200");
	}

	[TestCase]
	public void SaveMenuIsOpenWhileItsRootStatesARow()
	{
		var stated = new Dictionary<string, Probe>
		{
			["SaveMenu"] = State(4),
			["SaveMenu.Foreground.Text.Save"] = State(2),
			["SaveMenu.Foreground.Text.CharacterManagement.Layout"] = State(2),
		};

		AssertThat(Fable3BoxScreenModel.SaveMenuOpen(stated)).IsTrue();
		Fable3ScreenPage page = Fable3ScreenModel.Compose("SaveMenu", stated, Resolve)!.Value;
		AssertThat(page.Selected).IsEqual(2);
		AssertThat(page.Rows[0].Ghosted).IsTrue();

		stated["SaveMenu"] = State(5);
		AssertThat(Fable3BoxScreenModel.SaveMenuOpen(stated)).IsFalse();
	}
}
