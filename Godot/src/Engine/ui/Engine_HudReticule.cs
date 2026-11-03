using System;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The aim reticule — `target` petals per player that close in on the aim point as a lock builds.
/// Not a Gameface element: each title's guest pushes its petals as 2D sprites into the command
/// stream from a per-player record, so the HUD rebuild has nothing to follow. Godot draws it here
/// from that record and the aim point, by the title's own draw (<see cref="Look"/>): Fable 2's
/// sub_82179778 (below), Fable 3's sub_822906A8 (one petal on the point, s = (1 − t)·8 + 8).
///
/// Fable 2's petal geometry is the guest's draw, in its own 1280x720 pixels (0x82179778..0x82179a90):
/// <code>
///   t      = clamp(counter / 4, 0, 1)                 the four-frame intro
///   m      = min(progress, t)
///   angle  = i·π/2 + t·π/2 + phase                    phase 0 for player one, π/4 for two
///   r      = (1 − m)·18 + 8                           petal distance from the aim point
///   centre = aim + (sin angle, cos angle)·r           screen space, y down
///   s      = (1 − t)·12 + 12,  k = (1 − t)·1.25 + 1  half extents k·s by s
///   rot    = angle + π,  colour = record RGBA, alpha × t
/// </code>
/// The aim point is placed by Godot's camera, not the guest's: the guest believes 4:3, so its
/// own projection (the record's +4/+8) is not a screen position here. It keeps the guest's clamp
/// to ±0.8 of the screen.
/// </summary>
public sealed partial class Engine_HudReticule : Node3D
{
	private static readonly StringName Param_tint = "tint";
	private static readonly StringName Param_tex = "tex";

	private static readonly Shader MarkerShader =
		GD.Load<Shader>("res://src/Engine/ui/ui_world_marker.gdshader");

	/// <summary>One player's reticule as the guest states it this frame.</summary>
	/// <param name="Anchor">The aim point, guest world space (`object+0x90`).</param>
	/// <param name="Progress">Lock progress, record +12.</param>
	/// <param name="Colour">Record +16, RGBA.</param>
	/// <param name="Mode">Record +32: 0 picks `targeta`, 1 and 2 `targetb`, above that `target`.</param>
	/// <param name="Counter">Record +52, the frames since the mode last changed.</param>
	/// <param name="Screen">The point as the guest stated it on screen (its pixels from the centre, y up), for a
	/// reticule whose world point the guest never keeps; null to place <paramref name="Anchor"/> through <see cref="Place"/>.</param>
	public readonly record struct Aim(
		Vector3 Anchor, float Progress, Color Colour, uint Mode, int Counter, Vector2? Screen = null);

	/// <summary>A title's reticule draw in its guest pixels: r = (1 − m)·RadiusSpan + RadiusBase,
	/// s = (1 − t)·SizeSpan + SizeBase, each player's Phase, the clamp fraction (null: none), the art by mode.</summary>
	public sealed record Style(int Petals, float RadiusSpan, float RadiusBase, float SizeSpan, float SizeBase,
		float[] Phase, float? Clamp, Func<uint, string> ArtFor);

	/// <summary>The title's draw; set before the node enters the tree.</summary>
	public Style Look { get; init; } = null!;

	/// <summary>Resolves a lowercase resource hash to a texture, or null.</summary>
	public Func<uint, Texture2D?>? ResolveTexture { get; set; }

	/// <summary>A guest world point on the HUD canvas, or null behind the camera. Asked from this
	/// node's own late <see cref="_Process"/>, after the camera has moved.</summary>
	public Func<Vector3, Vector2?>? Place { get; set; }

	/// <summary>HUD canvas units per guest pixel (the guest's backbuffer is 720 tall).</summary>
	public float UnitsPerGuestPixel { get; set; } = 1f;

	/// <summary>Half the HUD canvas, for the guest's ±0.8 clamp.</summary>
	public Vector2 CanvasHalfExtent { get; set; }

	/// <summary>
	/// The one player (0 or 1) this layer draws, or −1 for both. On split screen each player's
	/// petals belong in their own half, so a layer per half draws one player each, its node
	/// carrying that half's seat transform (<see cref="Engine_UiSeats.Frame.Transform"/>).
	/// </summary>
	public int OnlyPlayer { get; set; } = -1;

	private readonly Aim?[] _aims = new Aim?[2];
	private MeshInstance3D[,] _quads = null!;
	private ShaderMaterial[,] _materials = null!;
	private Color[,] _lastTint = null!;
	private Texture2D?[,] _lastTexture = null!;
	private readonly bool[] _hidden = { true, true };
	private readonly System.Collections.Generic.Dictionary<string, Texture2D?> _textures = new();

	private int Petals => Look.Petals;
	private readonly Vector2?[] _last = new Vector2?[2];
	private readonly float[] _drawnIntro = new float[2];
	private readonly float[] _drawnProgress = new float[2];

	public override void _Ready()
	{
		// After the camera, as the world sprites are — see Engine_HudWorldSprites._Ready.
		ProcessPriority = 880;
		ArgumentNullException.ThrowIfNull(Look);
		_quads = new MeshInstance3D[2, Petals];
		_materials = new ShaderMaterial[2, Petals];
		_lastTint = new Color[2, Petals];
		_lastTexture = new Texture2D?[2, Petals];
		for (int player = 0; player < 2; ++player)
		{
			for (int i = 0; i < Petals; ++i)
			{
				var material = new ShaderMaterial { Shader = MarkerShader };
				var quad = new MeshInstance3D
				{
					Name = $"Petal{player}_{i}",
					Mesh = new QuadMesh { Size = Vector2.One },
					MaterialOverride = material,
					CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
					Visible = false,
				};
				AddChild(quad);
				_quads[player, i] = quad;
				_materials[player, i] = material;
			}
		}
	}

	/// <summary>States one player's reticule, null when the guest shows none.</summary>
	public void Feed(int player, Aim? aim)
	{
		if (player is 0 or 1)
		{
			_aims[player] = aim;
		}
	}

	private Texture2D? TextureFor(uint mode)
	{
		string name = Look.ArtFor(mode);
		if (!_textures.TryGetValue(name, out Texture2D? texture))
		{
			texture = ResolveTexture?.Invoke(Refii.Core.Engine_GameHash.ResourcePath(name));
			_textures[name] = texture;
			if (texture is null)
			{
				GD.Print($"[hud-reticule] '{name}' did not resolve — the petals draw untextured");
			}
		}

		return texture;
	}

	/// <summary>
	/// A mod's size for this widget (<c>ui/hud.json</c> <c>scale</c>, <see cref="Engine_HudWidgets"/>):
	/// each mark is drawn this much larger about its own point; 1 is the game's.
	/// </summary>
	public float PieceScale { get; set; } = 1f;

	public override void _Process(double delta)
	{
		for (int player = 0; player < 2; ++player)
		{
			if (_aims[player] is not { } aim || (OnlyPlayer >= 0 && player != OnlyPlayer))
			{
				_last[player] = null;
				_drawnIntro[player] = 0f;
				_drawnProgress[player] = 0f;
				Hide(player);
				continue;
			}

			// Behind the camera the guest keeps its last point; so does this.
			Vector2? placed = aim.Screen is { } stated ? stated * UnitsPerGuestPixel : Place?.Invoke(aim.Anchor);
			if (placed is { } fresh)
			{
				_last[player] = fresh;
				if (Look.Clamp is { } clamp)
				{
					Vector2 limit = CanvasHalfExtent * clamp;
					_last[player] = new Vector2(
						Mathf.Clamp(fresh.X, -limit.X, limit.X), Mathf.Clamp(fresh.Y, -limit.Y, limit.Y));
				}
			}

			if (_last[player] is not { } centre)
			{
				Hide(player);
				continue;
			}

			float targetT = Mathf.Clamp(aim.Counter * 0.25f, 0f, 1f);
			_drawnIntro[player] = Mathf.MoveToward(_drawnIntro[player], targetT, (float)delta * 4f);
			float t = _drawnIntro[player];

			_drawnProgress[player] = Mathf.MoveToward(_drawnProgress[player], aim.Progress, (float)delta * 2.5f);
			float m = Mathf.Min(_drawnProgress[player], t);
			float r = ((1f - m) * Look.RadiusSpan) + Look.RadiusBase;
			float s = ((1f - t) * Look.SizeSpan) + Look.SizeBase;
			float k = ((1f - t) * 1.25f) + 1f;
			float scale = UnitsPerGuestPixel * PieceScale;
			var tint = new Color(aim.Colour.R, aim.Colour.G, aim.Colour.B, aim.Colour.A * t);
			Texture2D? texture = TextureFor(aim.Mode);
			bool visible = tint.A > 0.004f;
			_hidden[player] = !visible;

			for (int i = 0; i < Petals; ++i)
			{
				float angle = (i * Mathf.Pi * 0.5f) + (t * Mathf.Pi * 0.5f) + Look.Phase[player];

				// Guest screen space is y down; the canvas is y up, so y and the turn both flip.
				var offset = new Vector2(Mathf.Sin(angle) * r, -Mathf.Cos(angle) * r) * scale;
				MeshInstance3D quad = _quads[player, i];
				quad.Position = new Vector3(centre.X + offset.X, centre.Y + offset.Y, 0f);
				quad.Rotation = new Vector3(0f, 0f, -(angle + Mathf.Pi));
				quad.Scale = new Vector3(2f * k * s * scale, 2f * s * scale, 1f);
				if (quad.Visible != visible)
				{
					quad.Visible = visible;
				}

				ShaderMaterial material = _materials[player, i];
				if (!_lastTint[player, i].IsEqualApprox(tint))
				{
					_lastTint[player, i] = tint;
					material.SetShaderParameter(Param_tint, tint);
				}
				if (texture is not null && !ReferenceEquals(_lastTexture[player, i], texture))
				{
					_lastTexture[player, i] = texture;
					material.SetShaderParameter(Param_tex, texture);
				}
			}
		}
	}

	private void Hide(int player)
	{
		if (_hidden[player])
		{
			return;
		}
		_hidden[player] = true;
		for (int i = 0; i < Petals; ++i)
		{
			_quads[player, i].Visible = false;
		}
	}
}
