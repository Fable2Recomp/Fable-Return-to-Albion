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
	/// <summary>Resolves a lowercase resource hash to a texture, or null.</summary>
	public Func<uint, Texture2D?>? ResolveTexture { get; set; }

	public Engine_UiTheme? Theme { get; set; }

	/// <summary>One live icon, keyed on the guest's object identity for the pool.</summary>
	public readonly record struct Sprite(
		uint Type, uint TextureHash, Color Colour, uint Entity, Vector3 Anchor, float Scale,
		string Title, string Opinion);

	private sealed class Drawn
	{
		public required Node3D Node { get; init; }
		public required MeshInstance3D Icon { get; init; }
		public required MeshInstance3D Title { get; init; }
		public required MeshInstance3D Opinion { get; init; }
		public uint ShownTexture { get; set; }
		public string ShownTitle { get; set; } = string.Empty;
		public string ShownOpinion { get; set; } = string.Empty;
		public Color ShownColour { get; set; } = Colors.Transparent;
		public bool Used { get; set; }
	}

	private readonly Dictionary<(uint Type, uint Entity), Drawn> _drawn = new();

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
	private readonly List<(uint, uint)> _stale = new();

	/// <summary>The first placed sprite this frame, for the report only.</summary>
	private (Vector3 Anchor, Vector2 Canvas, float Scale, Color Colour)? _lastCanvas;
	private string _reported = string.Empty;

	/// <summary>The icon square at size 1 — `sub_822782C8` lays it out `50 * size` wide.</summary>
	public const float IconSize = 50f;

	/// <summary>The two text lines' point size.</summary>
	private const int TextSize = 18;

	/// <summary>
	/// Draws this frame's icons. <paramref name="project"/> maps a guest world point to canvas
	/// units, or null when it is behind the camera.
	/// </summary>
	public void Feed(IReadOnlyList<Sprite> sprites, Func<Vector3, Vector2?> project)
	{
		foreach (Drawn drawn in _drawn.Values)
		{
			drawn.Used = false;
		}

		_lastCanvas = null;

		int shown = 0;
		foreach (Sprite sprite in sprites)
		{
			var key = (sprite.Type, sprite.Entity);
			if (!_drawn.TryGetValue(key, out Drawn? drawn))
			{
				drawn = Build(sprite);
				_drawn[key] = drawn;
			}

			drawn.Used = true;
			Vector2? at = sprite.Scale > 0.001f && sprite.Colour.A > 0.004f
				? project(sprite.Anchor)
				: null;
			if (at is not { } canvas)
			{
				drawn.Node.Visible = false;
				continue;
			}

			++shown;
			_lastCanvas ??= (sprite.Anchor, canvas, sprite.Scale, sprite.Colour);
			drawn.Node.Visible = true;
			drawn.Node.Position = new Vector3(canvas.X, canvas.Y, 0f);
			// The guest's stack, bottom up: the title sits on the anchor, the icon square is
			// centred above the title, and the amount hangs off the icon's right edge, centred
			// on it (`sub_821DEB48` / `sub_822782C8` / `sub_821D6078`). The quad is drawn grey
			// 0.7 at the fade alpha; the text carries the stated colour at the same alpha.
			float size = IconSize * sprite.Scale;
			drawn.Icon.Scale = new Vector3(Mathf.Max(size, 0.001f), Mathf.Max(size, 0.001f), 1f);
			float titleHeight = sprite.Title.Length > 0 ? TextSize : 0f;
			drawn.Icon.Position = new Vector3(0f, titleHeight + (size * 0.5f), 0f);
			if (drawn.Icon.MaterialOverride is StandardMaterial3D material)
			{
				material.AlbedoColor = new Color(0.7f, 0.7f, 0.7f, sprite.Colour.A);
				if (sprite.TextureHash != drawn.ShownTexture &&
					TextureFor(sprite.TextureHash) is { } texture)
				{
					drawn.ShownTexture = sprite.TextureHash;
					material.AlbedoTexture = texture;
				}
			}

			drawn.Title.Position = new Vector3(0f, titleHeight * 0.5f, 1f);
			drawn.Opinion.Position = new Vector3(size * 0.5f + 4f, titleHeight + (size * 0.5f), 1f);
			var colour = new Color(sprite.Colour.R, sprite.Colour.G, sprite.Colour.B, sprite.Colour.A);
			if ((sprite.Title != drawn.ShownTitle || colour != drawn.ShownColour) && Theme is not null)
			{
				drawn.ShownTitle = sprite.Title;
				Theme.Paint(drawn.Title, sprite.Title, TextSize, colour,
					horizontalAlign: 1, verticalAlign: 1, boxWidth: 0f, boxHeight: 0f, wrapMode: 0);
			}

			if ((sprite.Opinion != drawn.ShownOpinion || colour != drawn.ShownColour) && Theme is not null)
			{
				drawn.ShownOpinion = sprite.Opinion;
				Theme.Paint(drawn.Opinion, sprite.Opinion, TextSize, colour,
					horizontalAlign: 0, verticalAlign: 1, boxWidth: 0f, boxHeight: 0f, wrapMode: 0);
			}

			drawn.ShownColour = colour;
		}

		// An icon the guest has dropped goes with it — the vector is its lifetime.
		_stale.Clear();
		foreach ((var key, Drawn drawn) in _drawn)
		{
			if (!drawn.Used)
			{
				drawn.Node.QueueFree();
				_stale.Add(key);
			}
		}

		foreach (var key in _stale)
		{
			_drawn.Remove(key);
		}

		Report(sprites, shown);
	}

	private Drawn Build(Sprite sprite)
	{
		var node = new Node3D { Name = $"Sprite{sprite.Type}_{sprite.Entity:X8}", Visible = false };
		AddChild(node);

		var icon = new MeshInstance3D
		{
			Name = "Icon",
			Mesh = new QuadMesh { Size = Vector2.One },
			MaterialOverride = new StandardMaterial3D
			{
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				AlbedoTexture = TextureFor(sprite.TextureHash),
			},
		};
		node.AddChild(icon);

		var title = new MeshInstance3D { Name = "Title" };
		node.AddChild(title);
		var opinion = new MeshInstance3D { Name = "Opinion" };
		node.AddChild(opinion);

		return new Drawn
		{
			Node = node, Icon = icon, Title = title, Opinion = opinion,
			ShownTexture = sprite.TextureHash,
		};
	}

	/// <summary>Names the live set when it changes — type, texture, whether it resolved. Capped.</summary>
	/// <summary>When the sprite report last ran, so it cannot run every frame.</summary>
	private ulong _reportedAtMsec;

	/// <summary>Shortest gap between sprite reports; this is a diagnostic, not a feed.</summary>
	private const ulong ReportGapMsec = 1000;

	private void Report(IReadOnlyList<Sprite> sprites, int shown)
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
		foreach (Sprite sprite in sprites)
		{
			counts[sprite.Type] = counts.TryGetValue(sprite.Type, out int n) ? n + 1 : 1;
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
