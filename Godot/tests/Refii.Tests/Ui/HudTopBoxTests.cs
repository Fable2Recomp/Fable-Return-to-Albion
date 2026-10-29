namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Host;
using Refii.Ui;
using Refii.Titles.Fable2;

/// <summary>
/// <see cref="Engine_UiHudTopBox.Publish"/>'s visibility rule: the box follows the guest's own
/// end condition — its record present in a readable, unhidden manager — and every toggle inside
/// the box is restated on every front record, so a hold prompt's button never outlives its
/// record. Pure dictionary writes on <see cref="Engine_HudValues"/>; no Godot type is built.
/// </summary>
[TestSuite]
public class HudTopBoxTests
{
	private const uint QuestObjective = 0xCACB859Eu;

	private static readonly Fable2Bridge.TopBoxFront NoManager = default;

	private static readonly Fable2Bridge.TopBoxFront Empty = new(
		Known: true, Present: false, TitleTag: 0u, ShowA: false, HoldA: false, ShowY: false,
		HoldY: false, Channel: 0, NameHash: 0u, LifetimeMs: 0);

	/// <summary>`DisplayInfoBoxParams{DisplayBoxStyle=2, IsHoldAButton=true}` — a quest offer.</summary>
	private static readonly Fable2Bridge.TopBoxFront HoldToAccept = new(
		Known: true, Present: true, TitleTag: 0x1234u, ShowA: true, HoldA: true, ShowY: false,
		HoldY: false, Channel: 2, NameHash: 0x8D577C0Du, LifetimeMs: -1000);

	/// <summary>`sub_8251D538`'s record: channel 10, 20 s, no buttons.</summary>
	private static readonly Fable2Bridge.TopBoxFront Objective = new(
		Known: true, Present: true, TitleTag: 0x5678u, ShowA: false, HoldA: false, ShowY: false,
		HoldY: false, Channel: 10, NameHash: QuestObjective, LifetimeMs: 20000);

	/// <summary>`sub_826B50B8`'s record: channel 10, never expires, the spell blade's word.</summary>
	private static readonly Fable2Bridge.TopBoxFront WillName = new(
		Known: true, Present: true, TitleTag: 0u, ShowA: false, HoldA: false, ShowY: false,
		HoldY: false, Channel: 10, NameHash: Engine_UiHudTopBox.SpellAssignerInfoHash, LifetimeMs: 0);

	private static readonly Fable2Bridge.TopBoxBody NoBody = default;

	private static bool? Toggle(Engine_HudValues values, string name) =>
		values.Visible(Engine_HudValues.Handle.For(name));

	private static void Publish(Engine_HudValues values, Fable2Bridge.TopBoxFront front, bool aHeld = false) =>
		Engine_UiHudTopBox.Publish(front, aHeld, false, values, null, null, () => NoBody);

	[Before]
	public void Silence() => Engine_UiHudTopBox.Log = _ => { };

	[TestCase]
	public void TheHashesAreTheGuestsSeeds()
	{
		AssertThat(Refii.Core.Engine_GameHash.Fnv1("QuestObjective")).IsEqual(QuestObjective);
		AssertThat(Refii.Core.Engine_GameHash.Fnv1("SpellAssignerInfo"))
			.IsEqual(Engine_UiHudTopBox.SpellAssignerInfoHash);
	}

	[TestCase]
	public void AHoldPromptRaisesTheBoxAndItsButton()
	{
		var values = new Engine_HudValues();
		Publish(values, HoldToAccept, aHeld: true);
		AssertThat(Toggle(values, "TopBox")).IsEqual(true);
		AssertThat(Toggle(values, "RightTopButtonVisible")).IsEqual(true);
		AssertThat(Toggle(values, "RightTopButtonMeterVisible")).IsEqual(true);
		AssertThat(Toggle(values, "RightTopButtonHeld")).IsEqual(true);
		AssertThat(Toggle(values, "LeftTopButtonVisible")).IsEqual(false);
	}

	[TestCase]
	public void TheRecordGoneLowersEverything()
	{
		var values = new Engine_HudValues();
		Publish(values, HoldToAccept, aHeld: true);
		Publish(values, Empty);
		AssertThat(Toggle(values, "TopBox")).IsEqual(false);
		AssertThat(Toggle(values, "RightTopButtonVisible")).IsEqual(false);
		AssertThat(Toggle(values, "RightTopButtonMeterVisible")).IsEqual(false);
		AssertThat(Toggle(values, "RightTopButtonHeld")).IsEqual(false);
		AssertThat(Toggle(values, "TopMain")).IsEqual(false);
	}

	[TestCase]
	public void NoManagerStatesTheBoxDownRatherThanFreezingIt()
	{
		var values = new Engine_HudValues();
		Publish(values, HoldToAccept, aHeld: true);
		Publish(values, NoManager);
		AssertThat(Toggle(values, "TopBox")).IsEqual(false);
		AssertThat(Toggle(values, "RightTopButtonVisible")).IsEqual(false);
		AssertThat(Toggle(values, "RightTopButtonMeterVisible")).IsEqual(false);
	}

	[TestCase]
	public void TheQuestObjectiveOnChannelTenIsABoxAndLowersThePreviousButtons()
	{
		var values = new Engine_HudValues();
		Publish(values, HoldToAccept, aHeld: true);
		Publish(values, Objective);
		AssertThat(Toggle(values, "TopBox")).IsEqual(true);
		AssertThat(Toggle(values, "RightTopButtonVisible")).IsEqual(false);
		AssertThat(Toggle(values, "RightTopButtonMeterVisible")).IsEqual(false);
		AssertThat(Toggle(values, "RightTopButtonHeld")).IsEqual(false);
		AssertThat(Toggle(values, "LeftTopButtonVisible")).IsEqual(false);
	}

	[TestCase]
	public void TheWillNameKeepsTheBannerDownAndTakesTheButtonsWithIt()
	{
		var values = new Engine_HudValues();
		Publish(values, HoldToAccept, aHeld: true);
		Publish(values, WillName);
		AssertThat(Toggle(values, "TopBox")).IsEqual(false);
		AssertThat(Toggle(values, "RightTopButtonVisible")).IsEqual(false);
		AssertThat(Toggle(values, "RightTopButtonMeterVisible")).IsEqual(false);
		AssertThat(Toggle(values, "TopMain")).IsEqual(false);
	}

	[TestCase]
	public void TheInteractionPromptLowersEveryButtonToggle()
	{
		var values = new Engine_HudValues();
		Publish(values, HoldToAccept, aHeld: true);
		var prompt = HoldToAccept with { Channel = Engine_UiHudTopBox.InteractionPromptChannel, NameHash = 0x60EA1u };
		Publish(values, prompt, aHeld: true);
		AssertThat(Toggle(values, "TopBox")).IsEqual(true);
		AssertThat(Toggle(values, "RightTopButtonVisible")).IsEqual(false);
		AssertThat(Toggle(values, "RightTopButtonHeld")).IsEqual(false);
		AssertThat(Toggle(values, "ButtonOnly")).IsEqual(false);
	}

	[TestCase]
	public void AHostNoticeShowsOnlyWhileTheGuestsBoxIsDown()
	{
		var values = new Engine_HudValues();
		Engine_UiHudTopBox.Publish(Empty, false, false, values, null, "A partner has joined.", () => NoBody);
		AssertThat(Toggle(values, "TopBox")).IsEqual(true);
		AssertThat(Toggle(values, "TopMain")).IsEqual(true);
		AssertThat(Toggle(values, "RightTopButtonVisible")).IsEqual(false);

		Engine_UiHudTopBox.Publish(NoManager, false, false, values, null, "A partner has joined.", () => NoBody);
		AssertThat(Toggle(values, "TopBox")).IsEqual(false);
	}
}
