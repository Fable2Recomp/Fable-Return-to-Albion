using System.Collections.Generic;
using GdUnit4;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests.Ui;

/// <summary>
/// The screens stand without a guest sample (piece D of the UI cut): a headless presentation is
/// joined to its stated screen by the manager's slot, the existence-checked SetOpacity's raw
/// argument is scaled by the guest's own rule host-side, decorated labels come from the stated
/// g_Menu rows at every depth, and a presentation's one live text is the one it was told.
/// </summary>
[TestSuite]
public sealed class HeadlessStandTests
{
	/// <summary>`sub_82426078`: ×1024 when the element's current opacity is below 0.09765625 (`flt_83319B60`).</summary>
	[TestCase]
	public void SetOpacityScalesOnlyBelowTheThreshold()
	{
		AssertThat(Engine_UiSlideStatements.SetOpacityValue(0.5f, 0f)).IsEqual(512f);
		AssertThat(Engine_UiSlideStatements.SetOpacityValue(0.05f, 0.09f)).IsEqual(0.05f * 1024f);
		AssertThat(Engine_UiSlideStatements.SetOpacityValue(40f, 0.09765625f)).IsEqual(40f);
		AssertThat(Engine_UiSlideStatements.SetOpacityValue(40f, 100f)).IsEqual(40f);
	}

	/// <summary>A stated screen stands as a showing, contributing registry entry.</summary>
	[TestCase]
	public void StatedEntryIsShowing()
	{
		Engine_UiModel.Presentation entry = Engine_UiStatedScreens.Entry(0x4C200000u, "shopscreen");
		AssertThat(entry.IsShowing).IsTrue();
		AssertThat(entry.ActiveElements).IsEqual(1u);
		AssertThat(entry.Is("shopscreen.bgf")).IsTrue();
	}

	/// <summary>A headless presentation parked by its draw byte (vf +92, +872) is not showing.</summary>
	[TestCase]
	public void ParkedHeadlessPresentationIsNotShowing()
	{
		AssertThat(new Engine_UiModel.Presentation(1u, 1u, "a.bgf", 9u | 4u, 0u).IsShowing).IsFalse();
	}

	/// <summary>The shop's slot names its presentation; a pause tab without its own slot takes the shared file's.</summary>
	[TestCase]
	public void StatedPresentationJoinsByIdThenByFile()
	{
		var files = new Dictionary<uint, string> { [0] = "pausemainmenu", [17] = "pausemainmenu", [22] = "shopscreen" };
		string? FileOf(uint id) => files.TryGetValue(id, out string? f) ? f : null;
		var owned = new Dictionary<uint, int> { [0x4C300000u] = 22, [0x4C400000u] = 0 };

		AssertThat(Engine_UiStatedScreens.Presentation(owned, 22, FileOf)?.Address).IsEqual(0x4C300000u);
		AssertThat(Engine_UiStatedScreens.Presentation(owned, 22, FileOf)?.File).IsEqual("shopscreen");
		AssertThat(Engine_UiStatedScreens.Presentation(owned, 17, FileOf)?.Address).IsEqual(0x4C400000u);
		AssertThat(Engine_UiStatedScreens.Presentation(owned, 23, FileOf)).IsNull();
		AssertThat(Engine_UiStatedScreens.Presentation(new Dictionary<uint, int>(), 22, FileOf)).IsNull();
	}

	/// <summary>`SpawnButton` / `RefreshButtonText` labels land on rows below the root by name; roots are left alone.</summary>
	[TestCase]
	public void StatedLabelsJoinBelowTheRoot()
	{
		var root = new Engine_UiExpandableMenu.Item("Weapons", "TEXT_WEAPONS");
		var child = new Engine_UiExpandableMenu.Item("Sword", "TEXT_SWORD") { Parent = root };
		root.Children.Add(child);
		var stated = new List<Engine_UiMenuModel.Row>
		{
			new() { Name = "Weapons", Parent = "Root", Label = "Weapons (1)" },
			new() { Name = "Sword", Parent = "Weapons", Label = "Sword<exclamation.img>" },
		};

		AssertThat(Engine_UiScreenMenu.JoinStatedLabels(new[] { root, child }, stated)).IsTrue();
		AssertThat(child.StatedLabel).IsEqual("Sword<exclamation.img>");
		AssertThat(root.StatedLabel).IsNull();
		AssertThat(Engine_UiScreenMenu.JoinStatedLabels(new[] { root, child }, stated)).IsFalse();
	}

	/// <summary>The menu cycler's banner is the latest non-empty text told to its presentation.</summary>
	[TestCase]
	public void LatestToldTextWins()
	{
		const uint cycler = 0x4C500000u;
		Engine_UiSlideStatements.ApplyText(cycler, 0x11u, 3u, "Paused");
		Engine_UiSlideStatements.ApplyText(cycler, 0x11u, 4u, string.Empty);
		Engine_UiSlideStatements.ApplyText(cycler, 0x11u, 3u, "Abilities");
		AssertThat(Engine_UiSlideStatements.LatestTextOf(cycler)).IsEqual("Abilities");
		AssertThat(Engine_UiSlideStatements.LatestTextOf(0x4C5F0000u)).IsNull();
	}
}
