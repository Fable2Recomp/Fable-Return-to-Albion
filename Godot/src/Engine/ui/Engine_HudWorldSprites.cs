using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The world-space icon class — property signs, income and gift gold, opinion splashes, quest
/// markers, dog points, NPC status — which the guest draws through its renderer rather than on
/// a HUD element (`sub_823DDF90` files them separately from the pooled kinds; `sub_82261B70`
/// submits three primitives per icon). Godot draws them here from the guest's own objects: the
/// cached world anchor, the tweened scale, the RGBA the fade writes, the texture hash and the
/// two text lines. Layout per the guest's draw: title on the anchor, icon above it, amount
/// to the icon's right.
/// </summary>
public sealed partial class Engine_HudWorldSprites : Node3D
{
	// Static names: a string literal passed as a StringName is a new finalizable object per call,
	// and these are written for every sprite every frame.
	private static readonly StringName Param_tint = "tint";
	private static readonly StringName Param_sight = "sight";
	private static readonly StringName Param_tex = "tex";
	private static readonly StringName Param_glyphs = "glyphs";
	private static readonly StringName Param_atlas = "atlas";
	private static readonly StringName Param_sight_view = "sight_view";

	/// <summary>Resolves a lowercase resource hash to a texture, or null.</summary>
	public Func<uint, Texture2D?>? ResolveTexture { get; set; }

	public Engine_UiTheme? Theme { get; set; }

	/// <summary>
	/// The view the icons are projected through, for the line-of-sight depth: 0 the window's or
	/// player one's, 1 player two's on split screen. Set before the first feed.
	/// </summary>
	public int SightView { get; set; }

	/// <summary>One live icon.</summary>
	/// <param name="Object">
	/// The guest's sprite object — the icon's identity for its lifetime, and the pool's key. The
	/// pool used to key on (type, entity) with an "entity" that was the same word on every
	/// sprite, so twenty hearts shared one node and the last in the vector was the one drawn.
	/// </param>
	/// <param name="Entity">The entity the icon is anchored on, or 0.</param>
	/// <param name="Badge">
	/// A mark drawn inside the icon: the player a relationship heart is for ("1" or "2") when both
	/// players' hearts share one screen. Empty for none.
	/// </param>
	/// <param name="Texture">
	/// An icon's own texture, in place of one resolved by hash: a mod's marker
	/// (<see cref="Engine_HudModMarkers"/>). Null for the hash's.
	/// </param>
	public readonly record struct Sprite(
		uint Type, uint TextureHash, Color Colour, uint Entity, Vector3 Anchor, float Scale,
		string Title, string Opinion, uint Object = 0, string Badge = "", Texture2D? Texture = null);

	/// <summary>Where one icon falls this frame.</summary>
	/// <param name="Canvas">HUD canvas units.</param>
	/// <param name="Sight">The anchor on the WORLD viewport — uv.xy and its depth there — for
	/// the marker shader's line-of-sight test.</param>
	public readonly record struct Placed(Vector2 Canvas, Vector3 Sight);

	/// <summary>
	/// Places a guest world anchor, or null when it is behind the camera. Asked from this
	/// node's own late <see cref="_Process"/>, so it must read the camera when called.
	/// </summary>
	public Func<Vector3, uint, Placed?>? Place { get; set; }

	private sealed class Drawn
	{
		public required Node3D Node { get; init; }
		public required MeshInstance3D Icon { get; init; }
		public required ShaderMaterial IconMaterial { get; init; }
		public required MeshInstance3D Title { get; init; }
		public required MeshInstance3D Opinion { get; init; }
		public required MeshInstance3D Badge { get; init; }
		public ShaderMaterial? TitleMaterial { get; set; }
		public ShaderMaterial? OpinionMaterial { get; set; }
		public ShaderMaterial? BadgeMaterial { get; set; }
		public string? ShownBadge { get; set; }
		public Sprite Stated { get; set; }
		public uint ShownTexture { get; set; }

		/// <summary>The sprite's own texture put on the quad (<see cref="Sprite.Texture"/>).</summary>
		public Texture2D? ShownOwn { get; set; }
		public string? ShownTitle { get; set; }
		public string? ShownOpinion { get; set; }
		public Color ShownRgb { get; set; } = Colors.Transparent;
		public bool Used { get; set; }
		public Vector2 LastCanvas { get; set; } = new(float.NaN, float.NaN);
		public Vector3 LastSight { get; set; } = new(float.NaN, float.NaN, float.NaN);
		public float LastPieceScale { get; set; } = float.NaN;
		public float LastSize { get; set; } = float.NaN;
		public float LastTitleHeight { get; set; } = float.NaN;
		public Color LastColour { get; set; } = new(float.NaN, float.NaN, float.NaN, float.NaN);
	}

	private readonly Dictionary<uint, Drawn> _drawn = new();

	/// <summary>Number of currently live world sprites tracked by this layer.</summary>
	public int ActiveCount => _drawn.Count;

	private static readonly Shader MarkerShader =
		GD.Load<Shader>("res://src/Engine/ui/ui_world_marker.gdshader");

	/// <summary>
	/// AFTER THE CAMERA. The world camera is written in GameWorld's process, which runs after
	/// the UI's; icons projected from the UI's own process were a frame behind every turn of
	/// the camera and swam against the world. The additive layer follows the camera the same
	/// way, at 900.
	/// </summary>
	public override void _Ready() => ProcessPriority = 880;

	/// <summary>Resolved once per hash — a miss is remembered too, so it is never retried per frame.</summary>
	private readonly Dictionary<uint, Texture2D?> _textures = new();

	private Texture2D? TextureFor(uint hash)
	{
		if (hash == 0)
		{
			return null;
		}

		if (!_textures.TryGetValue(hash, out Texture2D? texture))
		{
			texture = ResolveTexture?.Invoke(hash);
			_textures[hash] = texture;
		}

		return texture;
	}
	private readonly List<uint> _stale = new();

	/// <summary>The first placed sprite this frame, for the report only.</summary>
	private (Vector3 Anchor, Vector2 Canvas, float Scale, Color Colour)? _lastCanvas;
	private string _reported = string.Empty;

	/// <summary>The icon square at size 1 — `sub_822782C8` lays it out `50 * size` wide.</summary>
	public const float IconSize = 50f;

	/// <summary>The two text lines' point size.</summary>
	private const int TextSize = 18;

	/// <summary>
	/// Takes this frame's icons: builds, re-texts and retires. Placement is NOT done here — see
	/// <see cref="_Process"/>.
	/// </summary>
	public void Feed(IReadOnlyList<Sprite> sprites)
	{
		foreach (Drawn drawn in _drawn.Values)
		{
			drawn.Used = false;
		}

		foreach (Sprite sprite in sprites)
		{
			// An icon with no object word (an older bridge) still gets a key of its own.
			uint key = sprite.Object != 0u
				? sprite.Object
				: unchecked((sprite.Type * 0x9E3779B1u) ^ sprite.Entity ^ (uint)sprite.Anchor.GetHashCode());
			if (!_drawn.TryGetValue(key, out Drawn? drawn))
			{
				drawn = Build(sprite, key);
				_drawn[key] = drawn;
			}

			drawn.Used = true;
			drawn.Stated = sprite;

			if (sprite.Texture is { } own)
			{
				if (!ReferenceEquals(own, drawn.ShownOwn))
				{
					drawn.ShownOwn = own;
					drawn.IconMaterial.SetShaderParameter(Param_tex, own);
				}
			}
			else if (sprite.TextureHash != drawn.ShownTexture && TextureFor(sprite.TextureHash) is { } texture)
			{
				drawn.ShownTexture = sprite.TextureHash;
				drawn.IconMaterial.SetShaderParameter(Param_tex, texture);
			}

			// Re-meshed on a change of STRING or RGB only. The fade is the tint's alpha, written
			// per frame below; repainting on every alpha step re-laid-out twenty labels a frame
			// whenever a crowd's icons faded together.
			var rgb = new Color(sprite.Colour.R, sprite.Colour.G, sprite.Colour.B, 1f);
			if (Theme is not null && (sprite.Title != drawn.ShownTitle || rgb != drawn.ShownRgb))
			{
				drawn.ShownTitle = sprite.Title;
				drawn.TitleMaterial = Text(drawn.Title, sprite.Title, rgb, horizontalAlign: 1);
			}

			if (Theme is not null && (sprite.Opinion != drawn.ShownOpinion || rgb != drawn.ShownRgb))
			{
				drawn.ShownOpinion = sprite.Opinion;
				drawn.OpinionMaterial = Text(drawn.Opinion, sprite.Opinion, rgb, horizontalAlign: 0);
			}

			if (Theme is not null && sprite.Badge != drawn.ShownBadge)
			{
				drawn.ShownBadge = sprite.Badge;
				drawn.BadgeMaterial = Text(drawn.Badge, sprite.Badge, Colors.White, horizontalAlign: 1);
			}

			drawn.ShownRgb = rgb;
		}

		// An icon the guest has dropped goes with it — the vector is its lifetime.
		_stale.Clear();
		foreach ((uint key, Drawn drawn) in _drawn)
		{
			if (!drawn.Used)
			{
				drawn.Node.QueueFree();
				_stale.Add(key);
			}
		}

		foreach (uint key in _stale)
		{
			_drawn.Remove(key);
		}
	}

	/// <summary>Paints one line and hands it the marker material, so text is hidden with its icon.</summary>
	private ShaderMaterial? Text(MeshInstance3D label, string value, Color rgb, int horizontalAlign)
	{
		Theme!.Paint(label, value, TextSize, rgb,
			horizontalAlign: horizontalAlign, verticalAlign: 1, boxWidth: 0f, boxHeight: 0f, wrapMode: 0);
		if (label.Mesh is null || label.MaterialOverride is not ShaderMaterial painted)
		{
			return null;
		}

		var material = new ShaderMaterial { Shader = MarkerShader };
		material.SetShaderParameter(Param_tex, painted.GetShaderParameter(Param_atlas));
		material.SetShaderParameter(Param_glyphs, true);
		material.SetShaderParameter(Param_sight_view, SightView);
		label.MaterialOverride = material;
		return material;
	}

	/// <summary>
	/// Places every live icon against the camera as it stands NOW, and states each one's line of
	/// sight. The guest's stack, bottom up: the title sits on the anchor, the icon square is
	/// centred above the title, and the amount hangs off the icon's right edge, centred on it
	/// (`sub_821DEB48` / `sub_822782C8` / `sub_821D6078`). The quad is drawn grey 0.7 at the fade
	/// alpha; the text carries the stated colour at the same alpha.
	/// </summary>
	/// <summary>
	/// A mod's size for this widget (<c>ui/hud.json</c> <c>scale</c>, <see cref="Engine_HudWidgets"/>):
	/// each mark is drawn this much larger about its own point; 1 is the game's.
	/// </summary>
	public float PieceScale { get; set; } = 1f;

	public override void _Process(double delta)
	{
		if (_drawn.Count == 0)
		{
			if (_reported.Length != 0 && _reported != "none")
			{
				Report(0);
			}
			return;
		}

		_lastCanvas = null;
		int shown = 0;
		foreach (Drawn drawn in _drawn.Values)
		{
			Sprite sprite = drawn.Stated;
			Placed? placed = sprite.Scale > 0.001f && sprite.Colour.A > 0.004f
				? Place?.Invoke(sprite.Anchor, sprite.Entity)
				: null;
			if (placed is not { } at)
			{
				if (drawn.Node.Visible)
				{
					drawn.Node.Visible = false;
				}
				continue;
			}

			++shown;
			_lastCanvas ??= (sprite.Anchor, at.Canvas, sprite.Scale, sprite.Colour);
			if (!drawn.Node.Visible)
			{
				drawn.Node.Visible = true;
			}
			if (!drawn.LastCanvas.IsEqualApprox(at.Canvas))
			{
				drawn.LastCanvas = at.Canvas;
				drawn.Node.Position = new Vector3(at.Canvas.X, at.Canvas.Y, 0f);
			}
			if (!Mathf.IsEqualApprox(drawn.LastPieceScale, PieceScale))
			{
				drawn.LastPieceScale = PieceScale;
				drawn.Node.Scale = new Vector3(PieceScale, PieceScale, 1f);
			}

			bool iconVisible = sprite.Texture is not null || sprite.TextureHash != 0u;
			if (drawn.Icon.Visible != iconVisible)
			{
				drawn.Icon.Visible = iconVisible;
			}

			float size = Mathf.Max(IconSize * sprite.Scale, 0.001f);
			float titleHeight = sprite.Title.Length > 0 ? TextSize : 0f;
			bool badgeVisible = sprite.Badge.Length != 0;
			if (drawn.Badge.Visible != badgeVisible)
			{
				drawn.Badge.Visible = badgeVisible;
			}

			if (!Mathf.IsEqualApprox(drawn.LastSize, size) || !Mathf.IsEqualApprox(drawn.LastTitleHeight, titleHeight))
			{
				drawn.LastSize = size;
				drawn.LastTitleHeight = titleHeight;
				drawn.Icon.Scale = new Vector3(size, size, 1f);
				drawn.Icon.Position = new Vector3(0f, titleHeight + (size * 0.5f), 0f);
				drawn.Title.Position = new Vector3(0f, titleHeight * 0.5f, 1f);
				drawn.Opinion.Position = new Vector3((size * 0.5f) + 4f, titleHeight + (size * 0.5f), 1f);
				// The badge sits in the icon's middle, sized with it.
				float badgeScale = size / IconSize;
				drawn.Badge.Position = new Vector3(0f, titleHeight + (size * 0.5f), 2f);
				drawn.Badge.Scale = new Vector3(badgeScale, badgeScale, 1f);
			}

			bool colourChanged = !drawn.LastColour.IsEqualApprox(sprite.Colour);
			bool sightChanged = !drawn.LastSight.IsEqualApprox(at.Sight);
			if (colourChanged)
			{
				drawn.LastColour = sprite.Colour;
				drawn.IconMaterial.SetShaderParameter(Param_tint, new Color(0.7f, 0.7f, 0.7f, sprite.Colour.A));
			}
			if (sightChanged)
			{
				drawn.LastSight = at.Sight;
				drawn.IconMaterial.SetShaderParameter(Param_sight, at.Sight);
			}

			// The marker material carries the text's colour: `Paint`'s own material, which held
			// it, was replaced.
			Color ink = sprite.Colour;
			if (drawn.TitleMaterial is { } title)
			{
				if (colourChanged) title.SetShaderParameter(Param_tint, ink);
				if (sightChanged) title.SetShaderParameter(Param_sight, at.Sight);
			}

			if (drawn.OpinionMaterial is { } opinion)
			{
				if (colourChanged) opinion.SetShaderParameter(Param_tint, ink);
				if (sightChanged) opinion.SetShaderParameter(Param_sight, at.Sight);
			}

			if (drawn.BadgeMaterial is { } badge)
			{
				if (colourChanged) badge.SetShaderParameter(Param_tint, new Color(1f, 1f, 1f, sprite.Colour.A));
				if (sightChanged) badge.SetShaderParameter(Param_sight, at.Sight);
			}
		}

		Report(shown);
	}

	private Drawn Build(Sprite sprite, uint key)
	{
		var node = new Node3D { Name = $"Sprite{sprite.Type}_{key:X8}", Visible = false };
		AddChild(node);

		var material = new ShaderMaterial { Shader = MarkerShader };
		material.SetShaderParameter(Param_sight_view, SightView);
		if (TextureFor(sprite.TextureHash) is { } texture)
		{
			material.SetShaderParameter(Param_tex, texture);
		}

		// HUD markers cast no shadow.
		var icon = new MeshInstance3D
		{
			Name = "Icon",
			Mesh = new QuadMesh { Size = Vector2.One },
			MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		node.AddChild(icon);

		var title = new MeshInstance3D { Name = "Title", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
		node.AddChild(title);
		var opinion = new MeshInstance3D { Name = "Opinion", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
		node.AddChild(opinion);
		var badge = new MeshInstance3D { Name = "Badge", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
		node.AddChild(badge);

		return new Drawn
		{
			Node = node, Icon = icon, IconMaterial = material, Title = title, Opinion = opinion, Badge = badge,
			ShownTexture = sprite.TextureHash,
		};
	}

	/// <summary>Names the live set when it changes — type, texture, whether it resolved. Capped.</summary>
	/// <summary>When the sprite report last ran, so it cannot run every frame.</summary>
	private ulong _reportedAtMsec;

	/// <summary>Shortest gap between sprite reports; this is a diagnostic, not a feed.</summary>
	private const ulong ReportGapMsec = 1000;

	private void Report(int shown)
	{
		// Time-gated before the line is built: the dedup below compares the finished string,
		// which embeds a camera-dependent canvas position, so without the gate this allocated
		// (and usually printed) every frame. Allocation rate drives GC pauses (see
		// refii-launcher.csproj).
		ulong now = Godot.Time.GetTicksMsec();
		if (_reportedAtMsec != 0 && now - _reportedAtMsec < ReportGapMsec)
		{
			return;
		}

		_reportedAtMsec = now;

		var counts = new SortedDictionary<uint, int>();
		var sprites = new List<Sprite>(_drawn.Count);
		foreach (Drawn drawn in _drawn.Values)
		{
			sprites.Add(drawn.Stated);
			counts[drawn.Stated.Type] = counts.TryGetValue(drawn.Stated.Type, out int n) ? n + 1 : 1;
		}

		var parts = new List<string>();
		foreach ((uint type, int n) in counts)
		{
			parts.Add($"type {type} x{n}");
		}

		int shownCap = 6;
		foreach (Sprite sprite in sprites)
		{
			if (shownCap-- <= 0)
			{
				break;
			}

			bool resolved = _textures.TryGetValue(sprite.TextureHash, out Texture2D? t) && t is not null;
			parts.Add($"[{sprite.Type} tex {sprite.TextureHash:X8}{(resolved ? string.Empty : " UNRESOLVED")}" +
				(sprite.Title.Length > 0 ? $" '{sprite.Title}'" : string.Empty) +
				(sprite.Opinion.Length > 0 ? $" ({sprite.Opinion})" : string.Empty) + "]");
		}

		if (_lastCanvas is { } first)
		{
			parts.Add($"first: anchor ({first.Anchor.X:F1}, {first.Anchor.Y:F1}, {first.Anchor.Z:F1}) " +
				$"canvas ({first.Canvas.X:F0}, {first.Canvas.Y:F0}) scale {first.Scale:F2} " +
				$"rgba ({first.Colour.R:F2}, {first.Colour.G:F2}, {first.Colour.B:F2}, {first.Colour.A:F2})");
		}

		string line = $"{sprites.Count} live, {shown} on screen: {string.Join(" ", parts)}";
		if (line == _reported)
		{
			return;
		}

		_reported = line;
		GD.Print($"[world-sprites] {line}");
	}
}
