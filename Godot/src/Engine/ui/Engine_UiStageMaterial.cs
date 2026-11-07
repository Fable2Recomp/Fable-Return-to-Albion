using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// HOW A MODEL IS DRESSED ON A GUI STAGE (Engine_UiFigureStage): the co-op screen's figures and
/// every menu's item preview, one builder for both.
/// </summary>
/// <remarks>
/// The world's own shaders read the level's lighting state — lightmaps, the light table, the
/// interior and night terms — as global uniforms, which a stage in its own world cannot give
/// values of its own. The original lights its GUI world with that world's own key and rim over a
/// grey ambient (Engine_UiFigureStage), so each surface takes a lit Godot material carrying what
/// its world material states (Engine_MdlMaterial / Engine_ClassicMaterial, by the same texture
/// slots): the diffuse, the normal map (handed over whole, as the world shader does — Godot
/// rebuilds z from xy exactly as the guest's `sqrt(1 − x² − y²)`), the self-illumination, and how
/// it covers — a translucent diffuse (glass, panes; texture flag 0x80) carries a blended NEXT PASS
/// and draws see-through, a cutout is alpha-tested at its own threshold.
/// </remarks>
public static class Engine_UiStageMaterial
{
	private static readonly StringName AlphaScissorOn = "alpha_scissor_on";
	private static readonly StringName AlphaScissorThreshold = "alpha_scissor_threshold";
	private static readonly StringName HasNormal = "has_normal";

	/// <summary>
	/// Gives each surface of <paramref name="mesh"/> on <paramref name="target"/> its stage
	/// material. Returns how many carry a diffuse; <paramref name="notes"/>, when given, takes one
	/// `s{n}:{cover}:{texture names}` line per surface for the caller's report.
	/// </summary>
	public static int Dress(MeshInstance3D target, Mesh mesh, List<string>? notes = null)
	{
		int textured = 0;
		for (int surface = 0; surface < mesh.GetSurfaceCount(); ++surface)
		{
			Material? stated = mesh.SurfaceGetMaterial(surface);
			var material = new StandardMaterial3D
			{
				Roughness = 0.85f,

				// The anchor's basis can be mirrored and hair cards and capes are single sheets.
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				AlbedoColor = new Color(0.62f, 0.60f, 0.56f),
			};

			string cover = "solid";
			bool flat = false;
			if (stated is ShaderMaterial source)
			{
				if (Texture(source, 0) is { } albedo)
				{
					material.AlbedoTexture = albedo;
					material.AlbedoColor = Colors.White;
					++textured;
				}

				// A slot is bound only when the material has that texture (both families). The normal
				// map also needs the surface's tangent frame and the material applying it: with no
				// TANGENT array Godot reads the next vertex's normal as the tangent and bends the
				// normal along it, which is why the world material states `has_normal` false there
				// (Engine_MdlMaterial.TryCreateShared).
				if (Texture(source, 1) is { } normal)
				{
					if (HasTangents(mesh, surface) && !StatedOff(source, HasNormal))
					{
						material.NormalEnabled = true;
						material.NormalTexture = normal;
					}
					else
					{
						flat = true;
					}
				}

				if (Texture(source, 3) is { } emission)
				{
					material.EmissionEnabled = true;
					material.EmissionTexture = emission;
				}

				if (source.NextPass is ShaderMaterial)
				{
					material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
					material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
					cover = "translucent";
				}
				else if (Flag(source, AlphaScissorOn))
				{
					material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
					Variant threshold = source.GetShaderParameter(AlphaScissorThreshold);
					material.AlphaScissorThreshold = threshold.VariantType == Variant.Type.Float
						? threshold.AsSingle()
						: 0.5f;
					cover = "cutout";
				}
			}

			if (notes is not null)
			{
				string names = stated is not null && stated.HasMeta(Refii.Models.Engine_MdlMaterial.TextureNamesMeta)
					? string.Join("/", stated.GetMeta(Refii.Models.Engine_MdlMaterial.TextureNamesMeta).AsStringArray())
					: "?";
				notes.Add($"s{surface}:{cover}{(flat ? "+flat" : string.Empty)}:{names}");
			}

			target.SetSurfaceOverrideMaterial(surface, material);
		}

		return textured;
	}

	/// <summary>A texture slot (diffuse, normal, specular, self-illumination) as the material binds it.</summary>
	private static Texture2D? Texture(ShaderMaterial material, int slot) =>
		material.GetShaderParameter(Refii.Models.Engine_ClassicMaterial.TextureParameter(material, slot)).As<Texture2D>();

	private static bool Flag(ShaderMaterial material, StringName name)
	{
		Variant value = material.GetShaderParameter(name);
		return value.VariantType == Variant.Type.Bool && value.AsBool();
	}

	/// <summary>True when the material states <paramref name="name"/> as false; unstated is not off.</summary>
	private static bool StatedOff(ShaderMaterial material, StringName name)
	{
		Variant value = material.GetShaderParameter(name);
		return value.VariantType == Variant.Type.Bool && !value.AsBool();
	}

	/// <summary>True when the surface carries a TANGENT array; a non-array mesh is taken as carrying one.</summary>
	private static bool HasTangents(Mesh mesh, int surface) =>
		mesh is not ArrayMesh array || (array.SurfaceGetFormat(surface) & Mesh.ArrayFormat.FormatTangent) != 0;
}
