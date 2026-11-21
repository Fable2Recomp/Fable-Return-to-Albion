using System;
using System.IO;
using System.Linq;
using GdUnit4;
using Refii.Bnk;
using Refii.Core;
using Refii.Ui;
using Refii.Titles.Fable2;
using static GdUnit4.Assertions;

namespace Refii.Tests.Ui;

/// <summary>
/// The HUD stands on its file and what the guest tells it (<see cref="Engine_UiHudStand"/>): the
/// headless `hud.bgf` has no element tree to sample, so the draw bits, texts, textures and the
/// attribute store come from the statements keyed to the file's ordinals. No Godot type is built.
/// </summary>
[TestSuite]
public sealed class HudStandTests
{
	/// <summary>Scene(0) → Layer(1) → { Badge(2), Caption(3) }.</summary>
	private static Engine_UiScreen Synthetic()
	{
		Engine_BgfDocument doc = Engine_BgfEdit.NewDocument("Scene", 1280, 720, true);
		int layer = Engine_BgfEdit.AddChild(doc, 0, "Layer");
		Engine_BgfEdit.AddChild(doc, layer, "Badge", 10f, 20f);
		Engine_BgfEdit.AddChild(doc, layer, "Caption");
		AssertThat(Engine_BgfWriter.TryWrite(doc, out byte[] bytes, out string error)).OverrideFailureMessage(error.Length > 0 ? error : "the synthetic HUD did not write").IsTrue();
		return Engine_UiScreen.Parse("synthetic", bytes, null);
	}

	private static uint Root(Engine_UiScreen screen) => screen.Nodes[0].Element.NameHash;

	/// <summary>Told `active`, text, texture and attribute writes land on their file ordinals.</summary>
	[TestCase]
	public void ToldStatementsStandTheElements()
	{
		const uint Presentation = 0x4D110000u;
		Engine_UiScreen screen = Synthetic();
		uint root = Root(screen);
		var stand = new Engine_UiHudStand(screen);

		Engine_UiSlideStatements.Apply(4u, Presentation, root, 2u, 0u, 0u);
		Engine_UiSlideStatements.Apply(4u, Presentation, root, 3u, 1u, 0u);
		Engine_UiSlideStatements.ApplyText(Presentation, root, 3u, "1,250");
		Engine_UiSlideStatements.Apply(5u, Presentation, root, 2u, 0xABCDEF01u, 0u);
		Engine_UiSlideStatements.Apply(3u, Presentation, root, 2u,
			Engine_GameHash.AttributeId("position.x"), BitConverter.SingleToUInt32Bits(42f));

		stand.Stand(Presentation, null, null, 0);

		AssertThat(stand.Active[2]).IsFalse();
		AssertThat(stand.Active[3]).IsTrue();
		AssertThat(stand.SwitchedOn[3]).IsTrue();
		AssertThat(stand.SwitchedOn[2]).IsFalse();
		AssertThat(stand.Texts[3]).IsEqual("1,250");
		AssertThat(stand.Textures[2]).IsEqual(0xABCDEF01u);
		AssertThat(stand.Transform[2]).IsNotNull();
		AssertThat(stand.Transform[2]![0]).IsEqual(42f);
		AssertThat(stand.Transform[2]![3]).IsNull();
		AssertThat(stand.Transform[3]).IsNull();
	}

	/// <summary>
	/// An element whose text a native's arguments state takes Godot's text: the guest's own writes
	/// to it are dropped, before and after, and every other element still takes the guest's.
	/// </summary>
	[TestCase]
	public void AStatedTextDropsTheGuestsWrites()
	{
		const uint Presentation = 0x4D120000u;
		Engine_UiScreen screen = Synthetic();
		uint root = Root(screen);
		var stand = new Engine_UiHudStand(screen);
		int caption = stand.Find("Layer.Caption");
		AssertThat(caption).IsEqual(3);
		AssertThat(stand.Find("Layer.Missing")).IsEqual(-1);

		stand.OwnText(caption);
		Engine_UiSlideStatements.ApplyText(Presentation, root, 3u, "guest");
		Engine_UiSlideStatements.ApplyText(Presentation, root, 2u, "badge");
		stand.StateText(caption, "<gold_img>5");
		stand.Stand(Presentation, null, null, 0);
		AssertThat(stand.Texts[3]).IsEqual("<gold_img>5");
		AssertThat(stand.Texts[2]).IsEqual("badge");

		Engine_UiSlideStatements.ApplyText(Presentation, root, 3u, "guest again");
		stand.Stand(Presentation, null, null, 16);
		AssertThat(stand.Texts[3]).IsEqual("<gold_img>5");
	}

	/// <summary>A presentation first seen takes everything told to it; another starts clean.</summary>
	[TestCase]
	public void ANewPresentationStartsAfresh()
	{
		const uint First = 0x4D130000u;
		const uint Second = 0x4D140000u;
		Engine_UiScreen screen = Synthetic();
		uint root = Root(screen);
		var stand = new Engine_UiHudStand(screen);

		Engine_UiSlideStatements.ApplyText(First, root, 3u, "told before the stand saw it");
		stand.Stand(First, null, null, 0);
		AssertThat(stand.Texts[3]).IsEqual("told before the stand saw it");
		ulong generation = stand.Generation;

		stand.Stand(Second, null, null, 16);
		AssertThat(stand.Texts[3]).IsNull();
		AssertThat(stand.Active.All(on => on)).IsTrue();

		Engine_UiSlideStatements.ApplyText(Second, root, 3u, "second");
		stand.Stand(Second, null, null, 32);
		AssertThat(stand.Texts[3]).IsEqual("second");
		AssertThat(stand.Generation).IsGreater(generation);
	}

	/// <summary>
	/// Meters are played on their components; layout meters (`TopBoxX`/`TopBoxY`, `Corner*`,
	/// `Subtitles*`) are not — the view places those widgets, as its own scrub does.
	/// </summary>
	[TestCase]
	public void TheStandPlaysMetersButNotLayoutMeters()
	{
		static Engine_UiBindings.Binding Bound(Engine_UiBindings.Kind kind, string name) =>
			new(1, "Bar", kind, name, Engine_GameHash.Fnv1(name));

		AssertThat(Engine_UiHudStand.PlaysMeter(Bound(Engine_UiBindings.Kind.Meter, "PlayerHealth"))).IsTrue();
		AssertThat(Engine_UiHudStand.PlaysMeter(Bound(Engine_UiBindings.Kind.Meter, "PlayerHealthMax"))).IsTrue();
		AssertThat(Engine_UiHudStand.PlaysMeter(Bound(Engine_UiBindings.Kind.Meter, "TopBoxY"))).IsFalse();
		AssertThat(Engine_UiHudStand.PlaysMeter(Bound(Engine_UiBindings.Kind.Meter, "TopBoxX"))).IsFalse();
		AssertThat(Engine_UiHudStand.PlaysMeter(Bound(Engine_UiBindings.Kind.Meter, "SubtitlesY"))).IsFalse();
		AssertThat(Engine_UiHudStand.PlaysMeter(Bound(Engine_UiBindings.Kind.Pulser, "PlayerLowHealthPulser"))).IsFalse();
	}

	/// <summary>
	/// The shipped health bar at its meters' times (`goToTime(trunc(pct × 10))`, `sub_823FDFF8`):
	/// `Bar`'s `BarMiddle` grows along its own scale.x track (50:0 → 1000:4) and `BarEnd` rides its
	/// position.x track (50:0 → 1000:400); `BarMax` the same on its own value. Each meter moves only
	/// its own component.
	/// </summary>
	[TestCase]
	public void TheShippedHealthBarStandsAtItsMetersTimes()
	{
		if (Hud() is not { } screen)
		{
			Console.WriteLine("[hud-stand] no packaged install beside the tree; the shipped health bar is not checked here");
			return;
		}

		Engine_UiBindings bindings = Engine_UiBindings.Read(screen.Document);
		int bar = bindings.Elements(Engine_UiBindings.Kind.Meter, "PlayerHealth")[0];
		int max = bindings.Elements(Engine_UiBindings.Kind.Meter, "PlayerHealthMax")[0];
		int Child(int parent, string name) => screen.Nodes[parent].Children.First(n => n.Name == name).Ordinal;
		int fill = Child(bar, Engine_UiBindings.FillName);
		int cap = Child(bar, Engine_UiBindings.FillCapName);
		int maxFill = Child(max, Engine_UiBindings.FillName);

		var stand = new Engine_UiHudStand(screen);
		var meters = new System.Collections.Generic.List<(int, float)> { (bar, 100f), (max, 50f) };
		stand.Stand(0x4D160000u, null, null, 0, null, meters);
		stand.Stand(0x4D160000u, null, null, 33, null, meters);

		// Full health: the whole authored length, the cap at the fill's end.
		AssertThat(stand.Transform[fill]![3]!.Value).IsEqualApprox(4f, 0.01f);
		AssertThat(stand.Transform[cap]![0]!.Value).IsEqualApprox(400f, 0.5f);

		// The max bar at half: its own track at t = 500, not the health bar's.
		float half = screen.Document.Animations
			.First(a => a.ElementOrdinal == maxFill && a.AttributeId == Engine_GameHash.AttributeId("scale.x"))
			.Evaluate(500f);
		AssertThat(stand.Transform[maxFill]![3]!.Value).IsEqualApprox(half, 0.01f);
		AssertThat(half).IsLess(4f);
	}

	/// <summary>The bank holding the presentations, beside the tree's packaged install; null without one.</summary>
	private static string? ShippedBank()
	{
		string? env = Environment.GetEnvironmentVariable(Engine_GameData.EnvironmentVariable);
		if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "data", "guiscripts.bnk")))
		{
			return Path.Combine(env, "data", "guiscripts.bnk");
		}

		for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
		{
			string direct = Path.Combine(at, "assets", "Fable2", "game", "data", "guiscripts.bnk");
			if (File.Exists(direct))
			{
				return direct;
			}
		}

		return null;
	}

	private static Engine_UiScreen? Hud()
	{
		string? bank = ShippedBank();
		if (bank is null || !BnkArchive.TryOpen(bank, out BnkArchive? archive, out _))
		{
			return null;
		}

		using (archive)
		{
			foreach (BnkEntry entry in archive!.Entries)
			{
				if (string.Equals(Path.GetFileName(entry.RelativePath.Replace('\\', '/')), "hud.bgf", StringComparison.OrdinalIgnoreCase))
				{
					return Engine_UiScreen.Parse("hud", archive.ReadEntryBytes(entry), null);
				}
			}
		}

		return null;
	}

	/// <summary>
	/// The shipped HUD: `WorldIcons` carries exactly the 16 children the world icons' pool arms on
	/// (`sub_828254D8` requires a count of 16, 0x82825544), and with nothing told the stand rests every
	/// component on its file's rest slide.
	/// </summary>
	[TestCase]
	public void TheShippedHudRestsOnItsFile()
	{
		if (Hud() is not { } screen)
		{
			Console.WriteLine("[hud-stand] no packaged install beside the tree; the shipped HUD is not checked here");
			return;
		}

		// `sub_827F1E18` names it under Layer (its a2), as the file carries it.
		UiNode icons = screen.Nodes.First(n => n.Name == "WorldIcons" && n.Parent?.Name == "Layer");
		AssertThat(icons.Children.Count).IsEqual(16);

		var stand = new Engine_UiHudStand(screen);
		stand.Stand(0x4D150000u, null, null, 0);
		AssertThat(stand.Slides.Take(stand.Count).ToArray()).IsEqual(Engine_UiRestState.Slides(screen));
		AssertThat(stand.Active.All(on => on)).IsTrue();
	}
}
