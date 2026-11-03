namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// Fable 3's in-game HUD on the SHARED engine: one presentation host over the title's banks and
/// one <see cref="Fable3HudDocument"/> per presentation the HUD draws — `hud.bgf` (the widgets
/// below), `interactionmode.bgf` (the interaction mode) and the end screens
/// (<see cref="Fable3EndScreens"/>) — each on the shared
/// <see cref="Engine_HudView"/>, driven by what the guest states. The same view, layout, art and
/// resolve draw Fable 2's HUD; this is the title's feed into them.
/// </summary>
/// <remarks>
/// THE HUD'S VISIBILITY IS ITS ROOT'S SLIDE, as Fable 2's is: the screen tick writes `Scene` 1
/// shown, 2 hidden, 3 for an ambient cutscene (sub_825C3A78), and the root's authored slides
/// (`PauseMenuToGame`, `GameToPauseMenu`, `ACS`) fade the widgets beneath — so no host gate.
///
/// THE WIDGETS ARE CUT WHOLE (<see cref="Groups"/>): each component is one piece following the
/// guest's state for every element beneath it, placed by where its art sits so an edge widget
/// stays on its edge at any aspect.
/// </remarks>
public partial class Fable3Hud : Fable3Canvas
{
	/// <summary>
	/// The widgets of `hud.bgf` cut whole, by component name under `Layer` (or the container
	/// named). Every component under `Layer` with art is here or in <see cref="Owned"/>; a new one
	/// is reported at build (`[fable3-hud] not cut`).
	/// </summary>
	public static readonly Engine_HudLayout.GroupSubtree[] Groups =
	{
		// The quest / info box: band, title, body, hold buttons. Its lines are placed by the guest's
		// box layout (sub_82920610 -> sub_82818580 at 0x829207DC) — the stated positions.
		new("TopBoxRefined", "TopBox", null, 0f, true),
		new("Subtitles", "Subtitles", null, 0f, true),
		// The counters and timers: the counter tick sub_825E7C28 toggles `Counter%d` (registry 9,
		// 0x825e7c74) and lays the joined text into the `Corner` box (0x825e7cdc).
		new("CornerBox", "Counters", null, 0f, true),
		// The d-pad and player two's: ring, four slots (DPad* toggles), counters (DP*Counter sets);
		// `dpad.dpad.dpadmenu` slides out (1) and in (2) with SetDPadEnabled.
		new("dpad", "Dpad", null, 0f, true),
		new("dpad2", "Dpad2", null, 0f, true),
		new("ReceivedItems", "ReceivedItems", null, 0f, true),
		new("MoneyAlert", "MoneyAlert", null, 0f, true),
		new("MoneyAlertSecondPlayer", "MoneyAlertSecondPlayer", null, 0f, true),
		new("FollowerCollection", "FollowerCollection", null, 0f, true),
		new("FollowerCollection2", "FollowerCollection2", null, 0f, true),
		new("ObjectiveList", "ObjectiveList", null, 0f, true),
		new("RelationshipBar", "RelationshipBar", null, 0f, true),
		new("TargetNames", "TargetNames", null, 0f, true),
		new("JudgementConsequences", "JudgementConsequences", null, 0f, true),
		new("CrimeTimer", "CrimeTimer", null, 0f, true),
		new("JobMultiplier", "JobMultiplier", null, 0.5f, true),
		new("CrucibleMeter", "CrucibleMeter", null, 0f, true),
		new("ShardHealth", "ShardHealth", null, 0f, true),
		new("LookAt", "LookAt", null, 0f, true),
		// The screen-edge damage frame, scrubbed by its root's `LowHealth` meter.
		new("DamageTaken", "DamageTaken", null, -0.5f, true),
		new("AbilitiesBox", "AbilitiesBox", null, 0f, true, "ExperianceDisplay"),
		// The co-op partners' off-screen markers (types 52/53, `icon_player1/2`). sub_82B52440 gives
		// a type-52 icon the record 0x832A1D70 (53: 0x832A1DB0) at icon+144 (0x82B52520), built by
		// sub_82B522A0 from this element (0x82B523E0 / 0x82B52420; read in r7: record+0 =
		// 0x47E57194 = the HUD table's +40, resolved by name at 0x82675BEC). The screen-edge clamp
		// (sub_82B519C8) writes the root's position through sub_82818580 (0x82B51FC8) in the
		// canvas's centre-origin units, so the root hangs from the centre; authored at y -800.
		new("HeroOffScreenWorldIcon", "HeroOffScreen", Engine_HudLayout.Anchor.Centre, 0.6f),
		new("HenchmanOffScreenWorldIcon", "HenchmanOffScreen", Engine_HudLayout.Anchor.Centre, 0.6f),
	};

	/// <summary>
	/// `hud.bgf` widgets another Fable 3 surface draws (<see cref="Fable3Title.HudOwnedSubtrees"/>):
	/// the pooled world icons and the hold-fill prompt, which the guest places in world space
	/// every frame (sub_82B51B58) and <see cref="Fable3WorldIcons"/> draws from the icon feed
	/// through the shared Engine_HudWorldSprites.
	/// </summary>
	public static readonly string[] Owned = { "WorldIcons", "FillWorldIcon" };

	/// <summary>
	/// The interaction mode's widgets in `interactionmode.bgf`, each cut whole and driven by element
	/// statements. The buttons, `LowerElements.InteractionMenuRight`: every interaction class hides
	/// Button1..4 (sub_82819AE0(button, pres, 0), e.g. sub_829651F8 at 0x82965324, sub_82B23C58 at 0x82B23D78) and shows the ones it offers through
	/// sub_82961340 — opacity 100 enabled / 50 not (0x8296139C), the `Title` label (0x829613E4),
	/// the glyph's `_text`/`_orb` textures (0x82961464, sub_829614A0 at 0x82961644), shown
	/// (0x82961494). All of it is element statements.
	/// </summary>
	public static readonly Engine_HudLayout.GroupSubtree[] InteractionGroups =
	{
		new("InteractionMenuRight", "InteractionButtons", null, 0f, true, "LowerElements"),

		// The option list: a three-row window over the guest's list. sub_82963DE0 writes the row at
		// the cursor (mode+116) into `Buttons.Button3` (sub_82961340 at 0x82963ED8), the next two into
		// Button2 / Button1 (0x82963F5C / 0x82964050) and the leaving one into DissapearingButton —
		// hiding a row past the end (sub_82819AE0 at 0x82963FEC / 0x829640E0 / 0x8296423C) — so the
		// cursor IS Button3, the only row with `Selection_Box_Glow`. A scroll moves the cursor and
		// plays the list root's slide 3 (down, sub_82963D18 at 0x82963D9C) or 4 (up, sub_82964248 at
		// 0x82964520) through sub_8281A500 -> sub_8281A490 -> SetState (0x8281A4AC); a refresh sets 2
		// (sub_82963BE0 at 0x82963C28).
		new("InteractionMenuScrollableList", "InteractionList", null, 0f, true, "LowerElements"),

		// The info box: one component whose slide is the panel. The panel id is staged by
		// sub_82963040 (handle+0x40, dirty byte +9, 0x8296307C / 0x82963078) and written by the
		// screen tick as `LowerElements.InfoBox`'s state (sub_825C3A78: sub_8227DA00 at 0x825C3B14 with
		// `lwz r5,0x40(r31)`) — 5 NPCInfo, 6 WeaponInfo, 9 SaveGameInfo, 11 DogInfo, 1 closed; the
		// panels' texts are element writes beneath it (`NPCInfo.Contents.Name` …, the joined
		// interaction text `TextBox.Contents.Text1`, sub_82BEF428 at 0x82BEF5B4), as are the map's
		// `WorldMapRegion` and the shops' `BuildingInfo`.
		new("InfoBox", "InteractionInfo", null, 0f, true, "LowerElements"),

		// The trigger and bumper bars: the mode's IM+36 (vtable 0x820D1D50, sub_82B19150) and IM+32
		// (vtable 0x820D1CD8, sub_82B18EF0), stored by sub_825C3F00 — label text (sub_82B3EB28, commit
		// 0x82B3EB58), LT/RT/LB/RB `_Complete` shown (sub_82B3EB60 at 0x82B3EB9C), root state
		// (sub_82B3EA18 at 0x82B3EAF0).
		new("TriggerButtons", "InteractionTriggers", null, 0f, true, "UpperElements"),
		new("BumperButtons", "InteractionBumpers", null, 0f, true, "UpperElements"),

		// The dye shop's bar: `ShowDyingBar(r, g, b, value, [entity], [min], [max])` (0x8251F2C0 ->
		// sub_8295C940) toggles the `DyeingBar` slide-toggle (sub_825E4120 -> sub_82921200 ->
		// sub_8227DA00 at 0x8281A4AC) and sets the `DyeingBar` meter to (value - min) / (max - min)
		// (sub_825E3F28 -> sub_8291F988), which the meter tick states through sub_8281A5D8
		// (0x8291F738); `HideDyingBar` toggles it off (sub_8295CA98). One unit, so the `Arrows`
		// meter stays on its hue bar.
		new("DyeingBar", "DyeingBar", null, 0f, true, "UpperElements"),

		// The weapon side panel: `ShowWeaponPanel` (sub_8295C500 kind 1) toggles
		// `WeaponPanelToggle` on through the same toggle registry and fills `WeaponsInformation`
		// (sub_829590A8: registry slides, texts and meters); Clothes / Augment / HideSideInfoPanel
		// toggle it off.
		new("WeaponInfoPanel", "WeaponPanel", null, 0f, true, "MiddleElements"),
	};

	private Fable3Presentations? _host;
	private Engine_UiScreenFade _fade = null!;
	private readonly List<Fable3HudDocument> _documents = new();

	protected override void Build()
	{
		_fade = new Engine_UiScreenFade { Name = "ScreenFade" };
		AddChild(_fade);

		_documents.Add(new Fable3HudDocument("hud", Groups, () => Fable3Elements.HudPresentation, layouts: true));
		_documents.Add(new Fable3HudDocument(
			"interactionmode", InteractionGroups, () => Fable3Elements.LatestPresentationOf("LowerElements"), layouts: false));
		_documents.AddRange(Fable3EndScreens.Documents());

		_host = new Fable3Presentations { Name = "Presentations", LoadsInBackground = true };
		_host.Loaded += Stand;
		Root.AddChild(_host);
	}

	/// <summary>Builds every document's view once the banks are indexed.</summary>
	private void Stand()
	{
		if (_host is null)
		{
			return;
		}

		foreach (Fable3HudDocument document in _documents)
		{
			if (document.View is null && document.Stand(_host, Root) && document.Document == "hud" &&
				_host.DocumentFor(document.Document) is { } screen)
			{
				ReportUncut(screen);
			}
		}
	}

	protected override void Frame(double delta)
	{
		_fade.Drive();
		Vector2 window = GetViewportRect().Size;
		foreach (Fable3HudDocument document in _documents)
		{
			document.Frame(window);
			Fable3EndScreens.Gate(document);
		}
	}

	/// <summary>Names every component under `Layer` with art that is neither cut nor owned: a widget the file added.</summary>
	private static void ReportUncut(Engine_UiScreen screen)
	{
		var named = new HashSet<string>(System.StringComparer.Ordinal);
		foreach (Engine_HudLayout.GroupSubtree group in Groups)
		{
			named.Add(group.Name);
		}

		named.UnionWith(Owned);
		var uncut = new List<string>();
		foreach (UiNode node in screen.Nodes)
		{
			if (node.Parent?.Name != Fable3HudStatements.LayerSegment || node.Type != BgfElementType.Component ||
				named.Contains(node.Name) || !HasArt(node))
			{
				continue;
			}

			uncut.Add(node.Name);
		}

		if (uncut.Count != 0)
		{
			GD.Print($"[fable3-hud] not cut (drawn by no piece): {string.Join(", ", uncut)}");
		}
	}

	private static bool HasArt(UiNode node)
	{
		if (node.Quad is not null || node.Text is not null)
		{
			return true;
		}

		foreach (UiNode child in node.Children)
		{
			if (HasArt(child))
			{
				return true;
			}
		}

		return false;
	}
}
