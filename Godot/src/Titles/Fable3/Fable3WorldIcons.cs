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
/// <see cref="Engine_HudWorldSprites"/> from the guest's own icon registry, read live
/// (Fable3/src/hostbridge/src/icon_feed.cpp: RefiiHostGetWorldSprites), and the emotion ring
/// drawn over them from the choice records (RefiiHostGetFable3EmotionChoices).
/// </summary>
/// <remarks>
/// THE GUEST OWNS EVERY LIFETIME. An icon appears when the registry's Update accepts its request,
/// fades by the registry's own scale tween and leaves when the registry erases it; the feed is a
/// per-frame read of those containers, so nothing here adds, times or removes an icon. What is
/// ours is the drawing: the anchor is projected through Godot's world camera into a 720-high
/// canvas the sprites layer lives in, the art is the texture the guest names by type, a button
/// prompt (a pooled HUD-element icon with no texture of its own) shows its button letter.
///
/// THE EMOTION RING is the guest's too: the icon's progress (0..300 steps) fills while the
/// choice is held and completes at 300; the ring here is that number as an arc at the icon.
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

		_viewport = new SubViewport
		{
			Name = "Canvas",
			TransparentBg = true,
			OwnWorld3D = true,
			HandleInputLocally = false,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
			Msaa3D = Viewport.Msaa.Disabled,
		};
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

		// THE HUD ROOT'S STATE IS THE GUEST'S GATE: the icons are HUD elements, and the HUD screen's
		// tick sub_825C3A78 sets its presentation root to state 2 while a box-manager screen shows
		// (sub_82277308 at 0x825c3d20 -> sub_8227DA00(root, pres, 2) at 0x825c3d5c; back to 1 at
		// 0x825c3d8c) — the Options screen among them — so nothing of the HUD is drawn then.
		_list.Clear();
		_emotionSquares.Clear();
		foreach (HostBridge.WorldSprite sprite in HostBridge.GetWorldSprites())
		{
			if (Fable3Elements.HudHidden)
			{
				break;
			}

			// The off-screen player markers (52/53) are pinned inside the screen edge by the
			// guest's own clamp, which writes their HUD element's position (sub_82B51B58 at
			// 0x82B51FC8): the shared HUD view draws them there (Fable3Hud, `HeroOffScreenWorldIcon`).
			// The stated position is already in hud.bgf's canvas units: sub_82C403E0 projects the
			// point through the game camera (R+84) and unprojects the pixel through UI camera desc B
			// (R+80: eye (0,-4000,0), fovY 10.2°, sub_82197CA8) on the plane through the origin, and
			// the write swaps Y/Z (f1 x, f2 z, f3 y at 0x82B51FBC..C4) — centre origin, +y up,
			// 4000·tan 5.1° = 357 units per half screen height.
			if (sprite.Type is 52u or 53u)
			{
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

		float canvasHeight = CanvasHeight / PixelsPerUnit;
		foreach (int slot in new[] { 0, 1 })
		{
			Engine_HudReticule reticule = _reticules[slot];
			reticule.CanvasHalfExtent = size.Y > 0f ? new Vector2(size.X / size.Y * canvasHeight, canvasHeight) * 0.5f : Vector2.Zero;
			for (int player = 0; player < 2; ++player)
			{
				float guestHeight = 720f;
				Engine_HudReticule.Aim? aim = Fable3Elements.HudHidden ? null : Fable3Reticles.Read(player, slot, out guestHeight);
				reticule.UnitsPerGuestPixel = canvasHeight / guestHeight;
				reticule.Feed(player, aim);
			}
		}

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

	public override void _Draw()
	{
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

	// ---- The emotion choices, as E holds them (icon_feed.cpp) ----------------------------------

	/// <summary>One choice record and the state of the icon the registry keeps for it.</summary>
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
	/// Whether the guest has emotion choices up (a record on E, read this frame): the face buttons
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
