namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Refii.Game;
using Refii.Host;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// Fable 3's world icons — opinion splashes, quest and reward markers, NPC status, property
/// signs, button prompts, the emotion choices — drawn by the shared
/// <see cref="Engine_HudWorldSprites"/> from the icons the guest STATES
/// (Fable3/src/hooks/src/world_icon_hooks.cpp into the runtime's stated table;
/// Fable3/src/hostbridge/src/icon_feed.cpp: RefiiHostGetWorldSprites), and the emotion ring drawn
/// over them from the stated choice records (RefiiHostGetFable3EmotionChoices).
/// </summary>
/// <remarks>
/// THE GUEST OWNS EVERY LIFETIME. An icon is stated by its own Init, each Update the registry's
/// tick runs on it (whose `visible` argument decides whether it is drawn this frame) and the
/// emotion icon's ring tick; it leaves when the registry erases it or it is destroyed. Nothing
/// here adds, times or removes an icon. What is ours is the drawing: the anchor is projected
/// through Godot's world camera into a 720-high canvas the sprites layer lives in, the art is the
/// texture the guest names by type, a button prompt (a pooled HUD-element icon with no texture of
/// its own) shows its button glyph.
///
/// THE EMOTION RING is the guest's too: the icon's progress (0..300 steps) fills while the
/// choice is held and completes at 300, both written by its ring tick (sub_8297A400); the ring
/// here is that number as an arc at the icon.
/// </remarks>
public partial class Fable3WorldIcons : SubViewportContainer
{
	private const float CanvasHeight = Engine_UiSceneBuilder.CanvasHeight;
	private const float PixelsPerUnit = Engine_UiSceneBuilder.PixelsPerUnit;

	/// <summary>
	/// The guest's ring radius, in its 720-line pixels: sub_82979188 builds the 301-point path
	/// `(sin a * aspect * 14, -cos a * 14)` for a = i/300 * 2pi (0x8297935C / 0x82979374), the
	/// aspect `(w/h) / (16:9 or 4:3)` (0x82979320) keeping it round on the display, so a circle.
	/// </summary>
	private const float RingRadius = 14f;

	/// <summary>The guest's icon sizes, in its 720-line pixels: 64 for the opinion, status, quest,
	/// reward, dog and property kinds, 32 for the rest (sub_82B51678), against the shared
	/// drawer's 50-unit square.</summary>
	private static float SizeFor(uint type) =>
		(type is >= 1u and <= 31u || type is >= 36u and <= 51u || type == 63u || type == 65u || type is >= 73u and <= 75u ? 64f : 32f) /
		Engine_HudWorldSprites.IconSize;

	/// <summary>The emotion icon's registry type (sub_825E8938 registers it as 77, 0x825E89C0).</summary>
	private const uint EmotionIconType = 77u;

	private SubViewport _viewport = null!;
	private Camera3D _canvasCamera = null!;
	private Engine_HudWorldSprites _sprites = null!;
	private readonly Engine_HudReticule[] _reticules = new Engine_HudReticule[2];
	private Vector2 _fittedTo;
	private bool _placed;
	private bool _probed;
	private string _culture = string.Empty;
	private readonly List<Engine_HudWorldSprites.Sprite> _list = new();
	/// <summary>The mount path for each texture id the guest can hand an icon: the type table's at
	/// start, the emotion records' as their choices appear.</summary>
	private readonly Dictionary<uint, string> _pathsByHash = new();


	/// <summary>The ring of every held choice this frame, in window pixels: centre, radius, progress, complete.</summary>
	private readonly List<(Vector2 Centre, float Radius, float Progress, bool Complete)> _rings = new();

	/// <summary>This frame's emotion icons by the guest's object: whether the drawer stacked a title under the square, and its scale.</summary>
	private readonly Dictionary<uint, (bool Titled, float Scale)> _emotionSquares = new();

	public override void _Ready()
	{
		// Anchors AND offsets, as Fable3Canvas: anchors alone leave the container 0×0 under the
		// CanvasLayer and its stretched viewport 2×2 — nothing rendered.
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
		Stretch = true;

		_viewport = Engine_UiSceneBuilder.CreateCanvasViewport("Canvas", SubViewport.UpdateMode.Disabled);
		AddChild(_viewport);

		var root = new Node3D { Name = "Root" };
		_viewport.AddChild(root);

		_canvasCamera = new Camera3D
		{
			Name = "CanvasCamera",
			Projection = Camera3D.ProjectionType.Orthogonal,
			Position = new Vector3(0f, 0f, 100f),
			Near = 0.1f,
			Far = 500f,
			Current = true,
		};
		root.AddChild(_canvasCamera);

		_sprites = new Engine_HudWorldSprites
		{
			Name = "WorldSprites",
			ResolveTexture = ResolveTexture,
			Place = PlaceSprite,
		};
		root.AddChild(_sprites);

		// The aim reticules (Fable3Reticles), in front of the icons: pushed by the guest as sprites, never elements.
		for (int slot = 0; slot < 2; ++slot)
		{
			_reticules[slot] = new Engine_HudReticule
			{
				Name = slot == 0 ? "SpellReticule" : "RangedReticule",
				Look = Fable3Reticles.Look,
				Position = new Vector3(0f, 0f, 1f),
				ResolveTexture = ResolveTexture,
				Place = guest => PlaceSprite(guest, 0u)?.Canvas,
			};
			root.AddChild(_reticules[slot]);
		}

		foreach ((uint type, string path) in Fable3IconArt.Paths)
		{
			// The guest's texture id is FNV-1 of `art\<path>.tex` LOWER-CASED: sub_82B512C0 (0x82B51304)
			// hands sub_82B530E8's string to sub_8312F3D8, whose string wrapper sub_822D76E0 copies it
			// through sub_82E34768 before sub_822D78B0 hashes the bytes — the live id C51DF0BA of a
			// type-72 icon is FNV-1 of `art\gui_memmount\hudassests\…\guildseal_gold.tex`, not of
			// the mixed-case literal (run r1, 2026-11-01).
			_pathsByHash[Refii.Core.Engine_GameHash.Fnv1(("art\\" + path + ".tex").ToLowerInvariant())] = Fable3IconArt.BankPath(path);
		}
	}

	/// <summary>
	/// A texture by the guest's id: the type table's path, else the mount's own hash index — the
	/// banks name every entry, and the guest's id IS the bank key (FNV-1 of the lower-cased path,
	/// sub_8312F3D8). THE EMOTION ICONS (type 77) come this way: sub_82978558 hands the choice's
	/// RecordName to sub_82978D90, which reads the record's `Texture` field (FNV-1("Texture") =
	/// `dword_8355C6EC`, init 0x831dea80..b8; inherited — `EmotionIconClothingStatue` is an alias
	/// of #d69b1457 whose parent #508b355c holds it) as that hash (0x82979038), and sub_8297A130
	/// stores it at icon+144 (0x8297a148): 342be71b =
	/// `art\gui_memmount\hudassests\version4\buttons\a\24x24_norim\a_complete.tex`. A miss
	/// names what was tried, once per hash.
	/// </summary>
	private Texture2D? ResolveTexture(uint hash)
	{
		if (!_pathsByHash.TryGetValue(hash, out string? path))
		{
			path = Refii.Bnk.AssetHashIndex.PathForHash(hash);
			if (path is null)
			{
				GD.Print($"[world-sprites] texture {hash:X8}: no type-table path and no mount entry hashes to it");
				return null;
			}

			GD.Print($"[world-sprites] texture {hash:X8} is the mount's '{path}'");
		}

		Texture2D? texture = Fable3Art.Load(path);
		if (texture is null)
		{
			GD.Print($"[world-sprites] texture {hash:X8}: the mount has no '{path}'");
		}

		return texture;
	}

	public override void _Process(double delta)
	{
		// The container covers the window: its parent is a CanvasLayer, not a Control, so nothing
		// else resizes it when the window does.
		Vector2 cover = GetViewportRect().Size;
		if (Size != cover && cover.X > 0f && cover.Y > 0f)
		{
			Position = Vector2.Zero;
			Size = cover;
		}

		Vector2 size = GetViewportRect().Size;
		if (size != _fittedTo && size.X > 0f && size.Y > 0f)
		{
			_fittedTo = size;
			Engine_UiSceneBuilder.FitCanvasCamera(_canvasCamera, size);
		}

		if (_sprites.Theme is null || _culture != Engine_GameCulture.Current)
		{
			_culture = Engine_GameCulture.Current;
			_sprites.Theme = new Engine_UiTheme
			{
				Fonts = Fable3Fonts.For(_culture),
				Strings = Fable3Strings.Current,
				Textures = Fable3Art.Load,
			};
		}

		// THE HUD ROOT'S STATE IS THE GUEST'S GATE: the icons are HUD elements, and hud.bgf's root is
		// set to state 2 by sub_825C1CB0 from every box's Show — the Options screen among them — and
		// back to 1 by sub_825C1D28 (`@Hud.Root`, Fable3Elements.HudHidden), so nothing of the HUD is
		// drawn then.
		_list.Clear();
		_markerSprites.Clear();
		_emotionSquares.Clear();
		foreach (HostBridge.WorldSprite sprite in HostBridge.GetWorldSprites())
		{
			if (Fable3Elements.HudHidden)
			{
				break;
			}

			// The co-op partners' markers (52/53) are placed and drawn here from the stated icon
			// alone (PlaceMarker, _Draw), never from their hud.bgf element.
			if (sprite.Type is MarkerHero or MarkerHenchman)
			{
				_markerSprites.Add(sprite);
				continue;
			}

			// A pooled prompt (the interaction mode's A/B/X/Y world icons) is a button: drawn as the
			// shared glyph for it — the pad's in the player's style, the bound key's keycap on the
			// keyboard — as every other prompt is (Engine_UiKeyGlyphs), not as a lettered badge.
			string button = sprite.Pooled ? Fable3IconArt.ButtonFor(sprite.Type) : string.Empty;
			Texture2D? glyph = button.Length > 0 ? Engine_UiKeyGlyphs.KeycapFor(button) : null;
			if (sprite.Type == EmotionIconType)
			{
				_emotionSquares[sprite.Object] = (sprite.Title.Length > 0, sprite.Scale * SizeFor(sprite.Type));
			}

			_list.Add(new Engine_HudWorldSprites.Sprite(
				sprite.Type, sprite.Pooled ? 0u : sprite.TextureHash, sprite.Colour, sprite.Entity, sprite.Anchor,
				sprite.Scale * SizeFor(sprite.Type), sprite.Title, sprite.Opinion, sprite.Object, Texture: glyph));
		}

		_sprites.Feed(_list);

		bool reticuleActive = false;
		float canvasHeight = CanvasHeight / PixelsPerUnit;
		for (int slot = 0; slot < 2; ++slot)
		{
			Engine_HudReticule reticule = _reticules[slot];
			reticule.CanvasHalfExtent = size.Y > 0f ? new Vector2(size.X / size.Y * canvasHeight, canvasHeight) * 0.5f : Vector2.Zero;
			for (int player = 0; player < 2; ++player)
			{
				float guestHeight = 720f;
				Engine_HudReticule.Aim? aim = Fable3Elements.HudHidden ? null : Fable3Reticles.Read(player, slot, out guestHeight);
				if (aim is not null)
				{
					reticuleActive = true;
				}
				reticule.UnitsPerGuestPixel = canvasHeight / guestHeight;
				reticule.Feed(player, aim);
			}
		}

		// Idle the full-window 3D world-icons SubViewport while under loading cover, when the HUD is
		// hidden, or when neither world sprites nor aim reticules are on screen.
		Engine_UiSceneBuilder.SetViewportActive(
			_viewport,
			!Refii.Game.GameWorld.CoverHeld && !Fable3Elements.HudHidden && (_list.Count != 0 || reticuleActive));

		// Ground truth once a sprite was placed on screen (last frame's placement): the log's
		// `1 on screen` is the drawer's count, not pixels (r8 shows none of them).
		if (_placed && !_probed)
		{
			_probed = true;
			Fable3CanvasProbe.Report("world-sprites", this, _viewport, _canvasCamera, _sprites);
		}

		_placed = false;

		// THE RING IS CONCENTRIC WITH THE ICON'S SQUARE. The guest centres its path on the centre of
		// the icon's own quad (sub_8297A400: the bounds sub_82B509F8 at 0x8297A448, halved at
		// 0x8297A480..0x8297A4B4, projected by sub_82287A08 at 0x8297A4C4) and adds each point in
		// screen pixels before unprojecting it (sub_82269260 at 0x8297A7B8). The shared drawer stacks
		// the square above the anchor (title on the anchor), so the centre is lifted by the title
		// and half the square, exactly as it places them.
		_rings.Clear();
		IReadOnlyList<EmotionChoice> choices = GetEmotionChoices();
		ChoicesUp = choices.Count > 0;
		Camera3D? camera = GetWindow().GetCamera3D();
		Vector2 window = GetViewportRect().Size;
		PlaceMarkers(camera, window);
		if (camera is not null && GodotObject.IsInstanceValid(camera) && !Fable3Elements.HudHidden && window.Y > 0f)
		{
			float pixelsPerUnit = window.Y * PixelsPerUnit / CanvasHeight;
			float piece = _sprites.PieceScale;
			foreach (EmotionChoice choice in choices)
			{
				if (!choice.RingOn || !_emotionSquares.TryGetValue(choice.Icon, out (bool Titled, float Scale) square))
				{
					continue;
				}

				Vector3 world = GameWorld.GuestToGodot(choice.Anchor);
				if (camera.IsPositionBehind(world))
				{
					continue;
				}

				float lift = ((square.Titled ? Engine_HudWorldSprites.TextSize : 0f) +
					(Engine_HudWorldSprites.IconSize * square.Scale * 0.5f)) * piece;
				Vector2 centre = camera.UnprojectPosition(world) - new Vector2(0f, lift * pixelsPerUnit);
				_rings.Add((centre, RingRadius * piece * pixelsPerUnit, choice.Progress, choice.Complete));
			}
		}

		QueueRedraw();
	}

	/// <summary>
	/// A guest world anchor on the sprites' canvas: through the window's world camera to a pixel,
	/// then into 720-high canvas units centred on the screen. No line-of-sight depth is captured
	/// for this title, so the marker is left untested (sight.x &lt; 0).
	/// </summary>
	private Engine_HudWorldSprites.Placed? PlaceSprite(Vector3 guest, uint entity)
	{
		Camera3D? camera = GetWindow().GetCamera3D();
		Vector2 size = GetViewportRect().Size;
		if (camera is null || !GodotObject.IsInstanceValid(camera) || !camera.IsInsideTree() || size.X <= 0f || size.Y <= 0f)
		{
			return null;
		}

		Vector3 world = GameWorld.GuestToGodot(guest);
		if (camera.IsPositionBehind(world))
		{
			return null;
		}

		Vector2 pixel = camera.UnprojectPosition(world);
		_placed = true;
		float unitsPerPixel = CanvasHeight / PixelsPerUnit / size.Y;
		var canvas = new Vector2((pixel.X - (size.X * 0.5f)) * unitsPerPixel, ((size.Y * 0.5f) - pixel.Y) * unitsPerPixel);
		return new Engine_HudWorldSprites.Placed(canvas, new Vector3(-1f, 0f, 0f));
	}

	// ---- The co-op partners' markers, types 52/53 ---------------------------------------------
	//
	// THE STATED ICON IS THE MARKER. sub_82B52440 builds a HUD-element icon (vtable 0x820D5EBC,
	// 0x82B52534) on the record 0x832A1D70 for type 52 and 0x832A1DB0 for 53 (icon+144, 0x82B52520),
	// whose elements sub_82B522A0 resolves from `HeroOffScreenWorldIcon` / `HenchmanOffScreenWorldIcon`
	// (0x82B523E0 / 0x82B52420). Its type setter (`vt+8` sub_82B520D0) gives the icon model the
	// type's art from sub_82B530E8 (SetTexture at 0x82B5214C): `icon_player1` / `icon_player2`
	// (Fable3IconArt). Its Update sub_82B51B58 shows the element exactly when its `visible`
	// argument is set (`vt+24` sub_82B52818 at 0x82B51F58), which is when the feed exports the icon,
	// and places it from the icon's world point (`vt+48`, stated as +112):
	//   - projected through the game camera (sub_82287A08 at 0x82B51BF0); OFF SCREEN when the depth
	//     is negative (0x82B51C1C) or the pixel is outside the camera's viewport rect
	//     (+0x8C..+0x98, 0x82B51C24..0x82B51C58);
	//   - BEHIND the camera the point is pushed along the view's forward axis (the view quaternion's
	//     +Y, sub_82195450 at 0x82B51CE8) by max(2 * the view's +0x30, 2 * |its distance from the
	//     camera plane|) (0x82B51D78..0x82B51DB0), projected again and its y set to the viewport's
	//     bottom (+0x48, 0x82B51DC8..0x82B51DD0);
	//   - ON SCREEN the point is first lifted by the icon's world offset +160 (`vaddfp` 0x82B51E94).
	//     The constructor zeroes it and sets +176 (0x82B52540 / 0x82B52548); every Update clears
	//     +176 (0x82B51FCC). The offset is MEASURED ONCE, on an on-screen Update after the first,
	//     while its z is still 0 (0x82B51E34..0x82B51E48): sub_82B529E8 takes the element's bounds
	//     where it was last placed (sub_8281AA20: left, top, right, bottom, y up; the base Update
	//     dirties them every frame, +72 = -1 at 0x8222F55C), maps the corners (left, top) and
	//     (right, bottom) through the HUD camera to pixels and back into the world at the
	//     point's own depth (sub_82C403E0 = project, project, unproject sub_82269260), and the
	//     offset's z is the point's z minus the bottom corner's (`fsubs` 0x82B51E68, stored into
	//     lane z by unk_82065370 at 0x82B51E7C). Placed at the point the frame before, the icon's
	//     bottom edge comes to stand on the point, at the depth it was measured;
	//   - every placement of 52/53 then goes through the edge clamp sub_82B519C8 (0x82B51DDC,
	//     0x82B51EEC): x into [x0 + 10% + 10, x1 - 9% - 10], y into [y0 + 10% + 40, y1 - 9%] of the
	//     viewport rect, in its 720-line pixels (0x82B51A14..0x82B51AF4).
	// Nothing in the guest rotates the element: the Update writes its position alone (0x82B51FC8),
	// and the `Contents.Arrow` it resolves (record+20, 0x82B526C8) is read by no code; hud.bgf
	// makes it active only on the `Clamped` slide (6), which nothing plays. The slides the guest
	// plays are Init's first-show state (1 `Fade In` for these types: sub_82B52068 at 0x82B520AC,
	// played at 0x82B51F28), the death's 2 `Fade Out` (sub_82B51FE0 at 0x82B5204C, with one second
	// left at 0x82B52020) and 7 `Hidden` (0x82B52940).

	/// <summary>The `HeroOffScreenWorldIcon` marker: art `icon_player1`, record 0x832A1D70.</summary>
	private const uint MarkerHero = 52u;

	/// <summary>The `HenchmanOffScreenWorldIcon` marker: art `icon_player2`, record 0x832A1DB0.</summary>
	private const uint MarkerHenchman = 53u;

	/// <summary>`Fade In` and `Fade Out` run the icon's opacity 0..100 and back over 1000 ms (hud.bgf anims 44 / 45).</summary>
	private const float MarkerFadeSeconds = 1f;

	/// <summary>The two markers' authored fades, read from hud.bgf once it stands (<see cref="UseHud"/>).</summary>
	private static Fable3MarkerPlacement.Fades? _heroFades;
	private static Fable3MarkerPlacement.Fades? _henchmanFades;

	/// <summary>Takes the markers' fade tracks from the HUD's own document (Fable3Hud, when hud.bgf stands).</summary>
	internal static void UseHud(Engine_UiScreen screen)
	{
		_heroFades = Fable3MarkerPlacement.FadesOf(screen, "HeroOffScreenWorldIcon");
		_henchmanFades = Fable3MarkerPlacement.FadesOf(screen, "HenchmanOffScreenWorldIcon");
		GD.Print($"[fable3-markers] fade tracks: hero {(_heroFades is null ? "absent" : "read")}, " +
			$"henchman {(_henchmanFades is null ? "absent" : "read")}");
	}

	private readonly List<HostBridge.WorldSprite> _markerSprites = new();

	/// <summary>This frame's markers in window pixels: art, the icon's centre, its side, its opacity.</summary>
	private readonly List<(Texture2D Art, Vector2 Centre, float Side, float Alpha)> _markers = new();

	/// <summary>Each marker icon's stated age (+68) when it was first drawn: `Fade In` runs from there.</summary>
	private readonly Dictionary<uint, float> _markerShownAt = new();

	/// <summary>Each marker icon's root as last placed, window pixels: the guest's previous SetPosition (0x82B51FC8).</summary>
	private readonly Dictionary<uint, Vector2> _markerPlaced = new();

	/// <summary>Each marker icon's measured world lift (+160's z, Godot's up), once measured.</summary>
	private readonly Dictionary<uint, float> _markerLift = new();

	private readonly Dictionary<uint, Texture2D?> _markerArt = new();
	private readonly HashSet<uint> _markersReported = new();

	/// <summary>
	/// The markers the guest states this frame, placed by Godot's own camera as sub_82B51B58
	/// places them (see above) and faded as their slides do: each fade's own hud.bgf opacity track
	/// (<see cref="Fable3MarkerPlacement.Alpha"/>).
	/// </summary>
	private void PlaceMarkers(Camera3D? camera, Vector2 window)
	{
		_markers.Clear();
		if (camera is null || !GodotObject.IsInstanceValid(camera) || !camera.IsInsideTree() ||
			window.X <= 0f || window.Y <= 0f || _markerSprites.Count == 0)
		{
			return;
		}

		float unit = window.Y * PixelsPerUnit / CanvasHeight;
		foreach (HostBridge.WorldSprite sprite in _markerSprites)
		{
			if (!_markerArt.TryGetValue(sprite.Type, out Texture2D? art))
			{
				foreach ((uint type, string path) in Fable3IconArt.Paths)
				{
					if (type == sprite.Type)
					{
						art = Fable3Art.Load(Fable3IconArt.BankPath(path));
						break;
					}
				}

				_markerArt[sprite.Type] = art;
				if (art is null)
				{
					GD.Print($"[fable3-markers] type {sprite.Type}: no art for the type");
				}
			}

			if (art is null)
			{
				continue;
			}

			if (!_markerShownAt.TryGetValue(sprite.Object, out float shownAt) || sprite.Age < shownAt)
			{
				shownAt = sprite.Age;
				_markerShownAt[sprite.Object] = shownAt;
			}

			// Fade Out starts with one second left (0x82B52020); Fade In at the first show.
			Fable3MarkerPlacement.Fades? fades = sprite.Type == MarkerHero ? _heroFades : _henchmanFades;
			float alpha = sprite.Fading
				? Fable3MarkerPlacement.Alpha(fades, true, (MarkerFadeSeconds - sprite.SecondsLeft) / MarkerFadeSeconds)
				: Fable3MarkerPlacement.Alpha(fades, false, (sprite.Age - shownAt) / MarkerFadeSeconds);
			(Vector2 root, string where) = PlaceMarker(camera, window, sprite.Object, sprite.Anchor, unit);
			_markers.Add((art, root - new Vector2(0f, Fable3MarkerPlacement.IconLift * unit), Fable3MarkerPlacement.Side * unit, alpha));
			if (_markersReported.Count < 32 && _markersReported.Add(sprite.Object))
			{
				GD.Print($"[fable3-markers] type {sprite.Type} icon {sprite.Object:X8} entity {sprite.Entity:X8}: " +
					$"drawn {where} at ({root.X:F0},{root.Y:F0}) of {window.X:F0}x{window.Y:F0}");
			}
		}

		// The guest's offset lives as long as its icon; an icon hidden a while keeps it. Forgotten
		// only once more icons were seen than any session has partners.
		if (_markerPlaced.Count > 32)
		{
			var held = new HashSet<uint>();
			foreach (HostBridge.WorldSprite sprite in _markerSprites)
			{
				held.Add(sprite.Object);
			}

			foreach (uint key in new List<uint>(_markerPlaced.Keys))
			{
				if (!held.Contains(key))
				{
					_markerPlaced.Remove(key);
					_markerLift.Remove(key);
				}
			}
		}

		if (_markerShownAt.Count > 8)
		{
			var live = new HashSet<uint>();
			foreach (HostBridge.WorldSprite sprite in _markerSprites)
			{
				live.Add(sprite.Object);
			}

			foreach (uint key in new List<uint>(_markerShownAt.Keys))
			{
				if (!live.Contains(key))
				{
					_markerShownAt.Remove(key);
				}
			}
		}
	}

	/// <summary>
	/// A marker's root in window pixels (y down): the stated world point through Godot's camera,
	/// pushed in front when behind it and dropped to the bottom edge, raised by its measured world
	/// lift when on screen (<see cref="MarkerLift"/>), then held inside sub_82B519C8's margins with
	/// its 10 / 40 pixels scaled from 720 lines to the window. Behind and the push are the guest
	/// camera's (<see cref="Fable3MarkerPlacement.Behind"/>, <see cref="Fable3MarkerPlacement.Push"/>:
	/// its 0.1 near plane, not Godot's camera's). <paramref name="unit"/> is window pixels per hud.bgf unit.
	/// </summary>
	private (Vector2 Root, string Where) PlaceMarker(Camera3D camera, Vector2 window, uint icon, Vector3 guest, float unit)
	{
		Vector3 world = GameWorld.GuestToGodot(guest);
		Vector2 pixel;
		string where;
		Transform3D eye = camera.GlobalTransform;
		Vector3 forward = -eye.Basis.Z.Normalized();
		float distance = forward.Dot(world - eye.Origin);
		if (Fable3MarkerPlacement.Behind(distance))
		{
			pixel = camera.UnprojectPosition(world + (forward * Fable3MarkerPlacement.Push(distance)));
			pixel.Y = window.Y;
			where = "behind";
		}
		else
		{
			pixel = camera.UnprojectPosition(world);
			bool onScreen = pixel.X >= 0f && pixel.X < window.X && pixel.Y >= 0f && pixel.Y < window.Y;
			if (onScreen)
			{
				if (!_markerLift.ContainsKey(icon) && _markerPlaced.TryGetValue(icon, out Vector2 placed) &&
					MarkerLift(camera, world, placed, unit) is { } measured)
				{
					_markerLift[icon] = measured;
				}

				float lift = _markerLift.TryGetValue(icon, out float held) ? held : 0f;
				pixel = camera.UnprojectPosition(world + new Vector3(0f, lift, 0f));
			}

			where = onScreen ? "on screen" : "off screen";
		}

		pixel = Fable3MarkerPlacement.Clamp(pixel, window);
		_markerPlaced[icon] = pixel;
		return (pixel, where);
	}

	/// <summary>
	/// The guest's one measurement of the on-screen lift (sub_82B51B58 0x82B51E04..0x82B51E7C over
	/// sub_82B529E8): the world height from the icon's bottom-right corner as it was last placed
	/// (<see cref="Fable3MarkerPlacement.LiftCorner"/>), unprojected at the point's own depth, down
	/// to <paramref name="world"/>. Null for a lift of 0, which the guest measures again (0x82B51E3C).
	/// </summary>
	private static float? MarkerLift(Camera3D camera, Vector3 world, Vector2 placed, float unit)
	{
		float depth = -(camera.GlobalTransform.AffineInverse() * world).Z;
		if (!(depth > 0f))
		{
			return null;
		}

		float lift = world.Y - camera.ProjectPosition(Fable3MarkerPlacement.LiftCorner(placed, unit), depth).Y;
		return lift != 0f && float.IsFinite(lift) ? lift : null;
	}

	public override void _Draw()
	{
		foreach ((Texture2D art, Vector2 centre, float side, float alpha) in _markers)
		{
			DrawTextureRect(art, new Rect2(centre - (new Vector2(side, side) * 0.5f), new Vector2(side, side)), false,
				new Color(1f, 1f, 1f, alpha));
		}

		if (_rings.Count == 0)
		{
			return;
		}

		// Only the filled part: the guest's path is points 0..progress from twelve o'clock clockwise
		// (the loop to `v28` at 0x8297A7D4), with no track behind it.
		float scale = GetViewportRect().Size.Y / CanvasHeight;
		foreach ((Vector2 centre, float radius, float progress, bool complete) in _rings)
		{
			if (progress > 0f)
			{
				float sweep = Mathf.Tau * Mathf.Clamp(progress, 0f, 1f);
				Color ink = complete ? new Color(1f, 0.86f, 0.55f) : Colors.White;
				DrawArc(centre, radius, -Mathf.Pi * 0.5f, (-Mathf.Pi * 0.5f) + sweep, 48, ink, 3f * scale, true);
			}
		}
	}

	// ---- The emotion choices, as the guest states them (icon_feed.cpp) ------------------------

	/// <summary>One stated choice record and the stated state of its emotion icon.</summary>
	public readonly record struct EmotionChoice(
		int Action, ulong Player, uint Entity, uint IconType, bool Holdable, bool Manual, bool Held, bool RingOn,
		bool Complete, bool Drawn, uint Icon, Vector3 Anchor, float Progress, float Speed, float Scale,
		string Name, string Text, string RecordName);

	private const int ChoiceWords = 8;
	private const int ChoiceValues = 6;
	private const int MaxChoices = 16;
	private static readonly uint[] _choiceWords = new uint[MaxChoices * ChoiceWords];
	private static readonly float[] _choiceValues = new float[MaxChoices * ChoiceValues];
	private static readonly byte[] _choiceText = new byte[8192];
	private static readonly List<EmotionChoice> _choices = new();

	/// <summary>
	/// Whether the guest has emotion choices up (a stated record, read this frame): the face buttons
	/// — and on the keyboard 1-5 and the left mouse button, as Fable III PC binds them — pick them.
	/// </summary>
	public static bool ChoicesUp { get; private set; }

	/// <summary>The choices this frame; the list is reused until the next call.</summary>
	public static IReadOnlyList<EmotionChoice> GetEmotionChoices()
	{
		_choices.Clear();
		if (!HostBridge.IsGuestUp)
		{
			return _choices;
		}

		uint count;
		try
		{
			count = RefiiHostGetFable3EmotionChoices(
				_choiceWords, (uint)_choiceWords.Length, _choiceValues, (uint)_choiceValues.Length,
				_choiceText, (uint)_choiceText.Length, MaxChoices);
		}
		catch (System.EntryPointNotFoundException)
		{
			return _choices;
		}

		int at = 0;
		string Next()
		{
			int end = System.Array.IndexOf(_choiceText, (byte)0, at);
			if (end < 0)
			{
				end = _choiceText.Length;
			}

			string value = System.Text.Encoding.UTF8.GetString(_choiceText, at, end - at);
			at = System.Math.Min(end + 1, _choiceText.Length);
			return value;
		}

		for (int i = 0; i < count; ++i)
		{
			int w = i * ChoiceWords;
			int v = i * ChoiceValues;
			uint flags = _choiceWords[w + 5];
			string name = Next();
			string text = Next();
			string record = Next();
			_choices.Add(new EmotionChoice(
				(int)_choiceWords[w], ((ulong)_choiceWords[w + 2] << 32) | _choiceWords[w + 1], _choiceWords[w + 3],
				_choiceWords[w + 4], (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0,
				(flags & 16) != 0, (flags & 32) != 0, _choiceWords[w + 6],
				new Vector3(_choiceValues[v], _choiceValues[v + 1], _choiceValues[v + 2]),
				_choiceValues[v + 3], _choiceValues[v + 4], _choiceValues[v + 5], name, text, record));
		}

		return _choices;
	}

	[DllImport(HostBridge.Library)]
	private static extern uint RefiiHostGetFable3EmotionChoices(
		[Out] uint[] words, uint wordCapacity, [Out] float[] values, uint valueCapacity,
		[Out] byte[] text, uint textCapacity, uint maxChoices);
}
