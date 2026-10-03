using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The minimap's markers — the hero, the dog, the quest targets — drawn as the guest draws them:
/// 24-unit textured quads riding `Page2.Contents` (`sub_829AE1C8` builds each from ±12 corners
/// and `sub_829AE668` re-submits them at `pos + Contents.position` every tick). Standing as a
/// child of the live `Contents` node, so the page's own slide and fold carry them.
/// </summary>
public sealed partial class Engine_UiMinimapMarkers : Node3D
{
	private static readonly StringName Param_tint = "tint";
	private static readonly StringName Param_tex = "tex";

	private static readonly Shader MarkerShader =
		GD.Load<Shader>("res://src/Engine/ui/ui_world_marker.gdshader");

	/// <summary>Resolves a resource-path hash to a texture, or null.</summary>
	public System.Func<uint, Texture2D?>? ResolveTexture { get; set; }

	/// <summary>How far in front of the map image the markers stand, in element z units — the
	/// guest's depth is `(d + 3990)·0.01` on its GUI list, in front of the page's own quads.</summary>
	public float Lift { get; set; } = 2f;

	private readonly List<MeshInstance3D> _quads = new();
	private readonly List<ShaderMaterial> _materials = new();
	private readonly Dictionary<uint, Texture2D?> _textures = new();

	/// <summary>States this frame's markers; an empty list hides them all.</summary>
	public void Feed(IReadOnlyList<Engine_UiMinimapPage.Marker> markers)
	{
		while (_quads.Count < markers.Count)
		{
			var material = new ShaderMaterial { Shader = MarkerShader };
			var quad = new MeshInstance3D
			{
				Name = $"Marker{_quads.Count}",
				Mesh = new QuadMesh { Size = Vector2.One },
				MaterialOverride = material,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				Visible = false,
			};
			AddChild(quad);
			_quads.Add(quad);
			_materials.Add(material);
		}

		for (int i = 0; i < _quads.Count; ++i)
		{
			MeshInstance3D quad = _quads[i];
			if (i >= markers.Count)
			{
				quad.Visible = false;
				continue;
			}

			Engine_UiMinimapPage.Marker marker = markers[i];
			float size = 2f * Engine_UiMinimapPage.MarkerHalfExtent;
			quad.Position = new Vector3(
				marker.Position.X, marker.Position.Y,
				Engine_UiSceneBuilder.ToGodotZ(-(Lift + marker.Depth)));
			quad.Rotation = new Vector3(0f, 0f, marker.Rotation);
			quad.Scale = new Vector3(size, size, 1f);
			quad.Visible = true;

			ShaderMaterial material = _materials[i];
			material.SetShaderParameter(Param_tint, Colors.White);
			if (TextureFor(marker.Texture) is { } texture)
			{
				material.SetShaderParameter(Param_tex, texture);
			}
		}
	}

	private Texture2D? TextureFor(uint hash)
	{
		if (!_textures.TryGetValue(hash, out Texture2D? texture))
		{
			texture = ResolveTexture?.Invoke(hash);
			_textures[hash] = texture;
			if (texture is null)
			{
				GD.Print($"[minimap] marker texture {hash:X8} did not resolve — the marker draws untextured");
			}
		}

		return texture;
	}
}
