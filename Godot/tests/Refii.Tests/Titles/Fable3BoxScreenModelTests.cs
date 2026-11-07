namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;
using Probe = Refii.Titles.Fable3.Fable3ScreenModel.Probe;

/// <summary>
/// The box-manager screens past the first five, from a stated set: the cursor fields, saved
/// settings, opener arguments, row records and queries of the join/visibility, chat, bed,
/// wedding, partners, treasury and save-menu classes (Fable3BoxScreenModel's citations), the
/// screen statements' per-box record, and the kind-to-root routing.
/// </summary>
[TestSuite]
public class Fable3BoxScreenModelTests
{
	private static string Resolve(string tag) => "<" + tag + ">";

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
	public void JoinOptionsCursorIsTheFieldAndTheTickIsTheShowsField()
	{
		var stated = new Dictionary<string, Probe>
		{
			["OrbEmote.ThisEmote"] = Text("Invite Only"),
		};
		var screen = new Fable3ScreenState { Kind = "join", Shown = new[] { 18, 2 }, Fields = new[] { 18, 3 } };

		Fable3ScreenPage page = Fable3ScreenModel.Compose("OrbEmote", stated, screen, Resolve)!.Value;
		AssertThat(page.Title).IsEqual("<TEXT_GUI_JOIN_OPTIONS_TITLE>");
		AssertThat(page.Rows.Count).IsEqual(3);
		AssertThat(page.Selected).IsEqual(2);
		AssertThat(page.Rows[1].Line).IsEqual("<TEXT_GUI_JOIN_OPTIONS_INVITE_TITLE>    ✓");
		AssertThat(page.Rows[2].Description).IsEqual("TEXT_GUI_JOIN_OPTIONS_OFF_DESC");
		AssertThat(page.Description).IsEqual("Invite Only");
		AssertThat(page.Cursor).IsTrue();
	}

	[TestCase]
	public void ChatChannelValueFollowsTheMuteField()
	{
		var stated = new Dictionary<string, Probe>
		{
			["ChatChannel.CurrSetting"] = Text("Audible"),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose("ChatChannel", stated, new Fable3ScreenState { Fields = new[] { 1 } }, Resolve)!.Value;
		AssertThat(page.Rows[0].Line).Contains("<TEXT_GUI_AUDIO_CHAT_MUTED>");
		AssertThat(page.Description).IsEqual("Currently set to: Audible");
	}

	[TestCase]
	public void BedMenuGhostsSexUnlessOfferedAndHighlightsTheRowField()
	{
		var stated = new Dictionary<string, Probe>
		{
			["BedMenu.Sleep.Title"] = Text("Sleep"),
			["BedMenu.Sleep.Widget.Text"] = Text("Midday"),
			["BedMenu.Sleep.Description"] = Text("Sleep until midday"),
			["BedMenu.Sex.Title"] = Text("Sex"),
		};
		var screen = new Fable3ScreenState { Fields = new[] { 0, 1, 0 }, Opened = new[] { 0, 0, 0 } };

		Fable3ScreenPage page = Fable3ScreenModel.Compose("BedMenu", stated, screen, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(2);
		AssertThat(page.Selected).IsEqual(0);
		AssertThat(page.Rows[0].Line).IsEqual("Sleep    ◀ Midday ▶");
		AssertThat(page.Rows[0].Description).IsEqual("Sleep until midday");
		AssertThat(page.Rows[0].Ghosted).IsFalse();
		AssertThat(page.Rows[1].Ghosted).IsTrue();

		screen.Opened = new[] { 1, 0, 1 };
		screen.Fields = new[] { 1, 1, 0 };
		page = Fable3ScreenModel.Compose("BedMenu", stated, screen, Resolve)!.Value;
		AssertThat(page.Rows[1].Ghosted).IsFalse();
		AssertThat(page.Selected).IsEqual(1);
	}

	[TestCase]
	public void WeddingPickerTierIsTheFieldAndUnaffordableTiersAreGhosted()
	{
		var stated = new Dictionary<string, Probe>
		{
			["WeddingPicker.Title"] = Text("Brightwall"),
			["WeddingPicker.Details.WeddingDetails0.Group.Title"] = Text("Poor"),
			["WeddingPicker.Details.WeddingDetails1.Group.Title"] = Text("Standard"),
			["WeddingPicker.Details.WeddingDetails1.Group.Cost"] = Text("2000"),
		};
		var screen = new Fable3ScreenState { Fields = new[] { 1 }, Opened = new[] { 1, 0, 0 } };

		Fable3ScreenPage page = Fable3ScreenModel.Compose("WeddingPicker", stated, screen, Resolve)!.Value;
		AssertThat(page.Title).IsEqual("Brightwall");
		AssertThat(page.Selected).IsEqual(1);
		AssertThat(page.Rows[1].Line).IsEqual("Standard    2000");
		AssertThat(page.Rows[0].Ghosted).IsFalse();
		AssertThat(page.Rows[1].Ghosted).IsTrue();
	}

	[TestCase]
	public void PartnersRowsAreTheRowWritersRecords()
	{
		const string row0 = "CoopPartnersInfoBox.Details.PartnerDetails0";
		const string row1 = "CoopPartnersInfoBox.Details.PartnerDetails1";
		var stated = new Dictionary<string, Probe>
		{
			[row0 + ".GamerTag"] = Text("Alice"),
			[row0 + ".NumChildren"] = Text("2"),
			[row1 + ".GamerTag"] = Text("Bob"),
			[row1 + ".NumChildren"] = Text("stale"),
		};
		var screen = new Fable3ScreenState { Fields = new[] { 1, 0 } };
		screen.PartnerRows[row0] = (true, true, false);
		screen.PartnerRows[row1] = (false, false, true);

		Fable3ScreenPage page = Fable3ScreenModel.Compose("CoopPartnersInfoBox", stated, screen, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(2);
		AssertThat(page.Rows[0].Line).IsEqual("Alice    2    Online");
		AssertThat(page.Rows[1].Line).IsEqual("Bob    Offline");
		AssertThat(page.Selected).IsEqual(1);
		AssertThat(page.Prompts[0].Button).IsEqual("A");
	}

	[TestCase]
	public void TreasuryQueryStandsOverThePage()
	{
		var stated = new Dictionary<string, Probe>
		{
			["TreasureAccounts.Personal.Personal Balance Value"] = Text("1200"),
			["TreasureAccounts.QueryBox.Group.QueryBox.Text"] = Text("Transfer 100?"),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose("TreasureAccounts", stated, new Fable3ScreenState { Fields = new[] { 5, 0, 100 } }, Resolve)!.Value;
		AssertThat(page.Query).IsNotNull();
		AssertThat(page.Query!.Text).IsEqual("Transfer 100?");
		AssertThat(page.Rows[0].Line).IsEqual("Personal Balance    1200");
	}

	[TestCase]
	public void SaveMenuRowAndGhostingAreItsOpensStatements()
	{
		var screen = new Fable3ScreenState { SaveRow = 4, SaveAllowed = false };
		Fable3ScreenPage page = Fable3ScreenModel.Compose("SaveMenu", new Dictionary<string, Probe>(), screen, Resolve)!.Value;
		AssertThat(page.Selected).IsEqual(2);
		AssertThat(page.Rows[0].Ghosted).IsTrue();
		AssertThat(page.Rows[1].Ghosted).IsTrue();
		AssertThat(page.Rows[3].Ghosted).IsFalse();

		screen.SaveRow = 3;
		screen.SaveAllowed = true;
		page = Fable3ScreenModel.Compose("SaveMenu", new Dictionary<string, Probe>(), screen, Resolve)!.Value;
		AssertThat(page.Selected).IsEqual(3);
		AssertThat(page.Rows[0].Ghosted).IsFalse();
	}

	[TestCase]
	public void ScreenStatementsKeepEachBoxsFieldsUntilItCloses()
	{
		Fable3ScreenStatements.Log = _ => { };
		Fable3ScreenStatements.Clear();
		Fable3ScreenStatements.Take(Call("@Screen.Opened", S("40001000"), S("bed"), N(1f), N(0f), N(1f)));
		Fable3ScreenStatements.Take(Call("@Screen.Fields", S("40001000"), S("bed"), B(true), N(0f), N(2f), N(0f)));
		Fable3ScreenStatements.Take(Call("@Screen.Fields", S("40001000"), S("bed"), B(false), N(1f), N(2f), N(0f)));

		Fable3ScreenState state = Fable3ScreenStatements.Of(0x40001000u);
		AssertThat(state.Kind).IsEqual("bed");
		AssertThat(state.Field(0, -1)).IsEqual(1);
		AssertThat(state.ShownField(0, -1)).IsEqual(0);
		AssertThat(state.OpenedFlag(0, false)).IsTrue();
		AssertThat(state.OpenedFlag(1, true)).IsFalse();

		Fable3ScreenStatements.Forget(0x40001000u);
		AssertThat(Fable3ScreenStatements.Of(0x40001000u).Fields.Length).IsEqual(0);
	}

	private static Engine_UiModel.GuiNativeCall Call(string name, params Engine_UiModel.GuiNativeArgument[] arguments) => new(name, arguments);

	private static Engine_UiModel.GuiNativeArgument S(string text) => new(Engine_UiModel.GuiNativeArgumentKind.String, text);

	private static Engine_UiModel.GuiNativeArgument N(float value) =>
		new(Engine_UiModel.GuiNativeArgumentKind.Number, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

	private static Engine_UiModel.GuiNativeArgument B(bool value) => new(Engine_UiModel.GuiNativeArgumentKind.Boolean, value ? "1" : "0");
}
