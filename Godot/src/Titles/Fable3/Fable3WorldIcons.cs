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

	/// <summary>The guest's ring radius, in its 720-line pixels (sub_82979188: `* 14.0`, 0x8297935C).</summary>
	private const float RingRadius = 14f;

	/// <summary>The guest's icon sizes, in its 720-line pixels: 64 for the opinion, status, quest,
	/// reward, dog and property kinds, 32 for the rest (sub_82B51678), against the shared
	/// drawer's 50-unit square.</summary>
	private static float SizeFor(uint type) =>
		(type is >= 1u and <= 31u || type is >= 36u and <= 51u || type == 63u || type == 65u || type is >= 73u and <= 75u ? 64f : 32f) /
		Engine_HudWorldSprites.IconSize;

	private SubViewport _viewport = null!;
	private Camera3D _canvasCamera = null!;
	private Engine_HudWorldSprites _sprites = null!;
	private Vector2 _fittedTo;
	private bool _placed;
	private bool _probed;
	private string _culture = string.Empty;
	private readonly List<Engine_HudWorldSprites.Sprite> _list = new();
	/// <summary>The mount path for each texture id the guest can hand an icon: the type table's at
	/// start, the emotion records' as their choices appear.</summary>
	private readonly Dictionary<uint, string> _pathsByHash = new();


	/// <summary>The ring of every held choice this frame, in window pixels: centre, radius scale, progress, complete.</summary>
	private readonly List<(Vector2 Centre, float Progress, bool Complete)> _rings = new();

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
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
				Fonts = Engine_UiFontLibrary.Load(Refii.Core.Engine_GameData.Resolve(), _culture),
				Strings = Fable3Strings.Current,
				Textures = Fable3Art.Load,
			};
		}

		// THE HUD ROOT'S STATE IS THE GUEST'S GATE: the icons are HUD elements, and the HUD screen's
		// tick sub_825C3A78 sets its presentation root to state 2 while a box-manager screen shows
		// (sub_82277308 at 0x825c3d20 -> sub_8227DA00(root, pres, 2) at 0x825c3d5c; back to 1 at
		// 0x825c3d8c) — the Options screen among them — so nothing of the HUD is drawn then.
		_list.Clear();
		foreach (HostBridge.WorldSprite sprite in HostBridge.GetWorldSprites())
		{
			if (Fable3Elements.HudHidden)
			{
				break;
			}

			// The off-screen player markers (52/53) are pinned inside the screen edge by the
			// guest's own clamp, which writes their HUD element's position (sub_82B51B58 at
			// 0x82B51FC8): the shared HUD view draws them there (Fable3Hud, `HeroOffScreenWorldIcon`).
			if (sprite.Type is 52u or 53u)
			{
				continue;
			}

			string badge = sprite.Pooled ? Fable3IconArt.ButtonFor(sprite.Type) : string.Empty;
			_list.Add(new Engine_HudWorldSprites.Sprite(
				sprite.Type, sprite.Pooled ? 0u : sprite.TextureHash, sprite.Colour, sprite.Entity, sprite.Anchor,
				sprite.Scale * SizeFor(sprite.Type), sprite.Title, sprite.Opinion, sprite.Object, badge));
		}

		_sprites.Feed(_list);

		// Ground truth once a sprite was placed on screen (last frame's placement): the log's
		// `1 on screen` is the drawer's count, not pixels (r8 shows none of them).
		if (_placed && !_probed)
		{
			_probed = true;
			Fable3CanvasProbe.Report("world-sprites", this, _viewport, _canvasCamera, _sprites);
		}

		_placed = false;

		_rings.Clear();
		Camera3D? camera = GetWindow().GetCamera3D();
		if (camera is not null && GodotObject.IsInstanceValid(camera) && !Fable3Elements.HudHidden)
		{
			foreach (EmotionChoice choice in GetEmotionChoices())
			{
				if (!choice.RingOn)
				{
					continue;
				}

				Vector3 world = GameWorld.GuestToGodot(choice.Anchor);
				if (camera.IsPositionBehind(world))
				{
					continue;
				}

				_rings.Add((camera.UnprojectPosition(world), choice.Progress, choice.Complete));
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

		float scale = GetViewportRect().Size.Y / CanvasHeight;
		float radius = RingRadius * scale * 2f;
		foreach ((Vector2 centre, float progress, bool complete) in _rings)
		{
			DrawArc(centre, radius, 0f, Mathf.Tau, 48, new Color(1f, 1f, 1f, 0.25f), 2f * scale, true);
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
