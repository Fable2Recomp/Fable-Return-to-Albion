using System;

namespace Refii.Textures;

/// <summary>
/// What a texture MEANS, taken from the slot the material bound it to.
/// </summary>
/// <remarks>
/// THE SLOT IS THE AUTHORITY, NOT THE FILE NAME. The modernization plan's first design
/// principle is that a texture called "specular" is not automatically a specular map, and the
/// shipped data proves the point twice over: the slot at record+148 was called
/// <c>Metallic</c> in our own reader and is the ENVIRONMENT MAP (bound to s14, published as
/// pixel bool c6), and the slot at record+16 was called <c>Extra</c> and is self illumination
/// (s13, bool c5). Both were settled by decompiling the binder, not by reading names. See
/// <see cref="Refii.Models.MdlMaterial"/> for the addresses.
///
/// So this enum is populated at the BIND SITE — the code that knows it is filling
/// <c>g_NormalTextureSampler</c> — and never inferred from a path.
/// </remarks>
public enum TexSemantic
{
	/// <summary>No slot information. Treated conservatively: decoded and cached, never upscaled.</summary>
	Unknown = 0,

	/// <summary>g_DiffuseTextureSampler. Colour, decoded by squaring (gamma 2.0).</summary>
	Albedo = 1,

	/// <summary>g_NormalTextureSampler. XY only; Z is reconstructed by the shader.</summary>
	Normal = 2,

	/// <summary>g_SpecularTextureSampler. RGB is specular colour, ALPHA is the exponent selector.</summary>
	Specular = 3,

	/// <summary>g_SelfIllumTextureSampler. Emission, added as <c>tex^2 * c24.z</c>.</summary>
	SelfIllumination = 4,

	/// <summary>g_EnvmapSampler. A CUBE map — never treated as a 2D raster.</summary>
	EnvironmentMap = 5,

	/// <summary>Front end and HUD artwork reached through <c>game://</c>.</summary>
	Ui = 6,

	/// <summary>
	/// A packed ORM texture we BUILD: R = AO, G = roughness, B = metallic.
	/// </summary>
	/// <remarks>
	/// Not a slot the material names — no Fable 2 material record has an AO, metallic or
	/// roughness map. This is a derived texture, and it is here so the cache and the policy can
	/// treat it like any other rather than growing a special case for it.
	/// </remarks>
	Orm = 7,
}

/// <summary>
/// How a filter must treat a texture's channels when it resamples it.
/// </summary>
/// <remarks>
/// This exists because §9 of the plan is right that the channels do not all mean the same
/// thing, and a single "upscale" verb applied to all of them is wrong in specific ways: a
/// perceptual model hallucinates detail into a tangent-space normal that then does not
/// renormalize, and it invents intermediate values in a mask whose whole meaning is that a
/// texel is either in or out.
/// </remarks>
public enum TexFilterClass
{
	/// <summary>Do not resample at all.</summary>
	None = 0,

	/// <summary>Perceptual super resolution. Correct for colour rasters and nothing else.</summary>
	Perceptual = 1,

	/// <summary>Resample XY, then renormalize. For tangent-space normals.</summary>
	NormalAware = 2,

	/// <summary>Edge-preserving scalar resample, per channel, no cross-channel mixing.</summary>
	Scalar = 3,
}

/// <summary>What may be done to a texture of one semantic, and how far.</summary>
public sealed class TexPolicy
{
	public required TexFilterClass Filter { get; init; }

	/// <summary>
	/// The largest edge, in texels, the enhanced result may have.
	/// </summary>
	/// <remarks>
	/// Per-semantic rather than global: an albedo at 2048 is worth the VRAM, the same budget
	/// spent on a specular map whose only job is to select an exponent is not.
	/// </remarks>
	public required int MaxEdge { get; init; }

	/// <summary>
	/// Below this edge length the texture is left alone.
	/// </summary>
	/// <remarks>
	/// §9's "tiny utility textures: do not upscale", and the threshold is 128 because that is
	/// where §10's own table starts. It was 32, and the first real texture the end-to-end probe
	/// picked up under that setting was <c>Globals/Engine/Textures/alphastipple.tex</c> at 32px,
	/// which it happily turned into 128px.
	///
	/// A STIPPLE IS A SCREEN-SPACE PATTERN. Its value is that adjacent texels differ; resampling
	/// it averages exactly the alternation that makes it work, and four times the resolution of a
	/// dither pattern is not a better dither pattern. The same is true of the gradient ramps and
	/// lookup tables that live at these sizes — below 128, a Fable 2 texture is usually a
	/// function rather than a picture, and there is no detail in one to recover.
	/// </remarks>
	public required int MinEdge { get; init; }
}

/// <summary>
/// The per-semantic processing policy, and the adaptive scale rule that goes with it.
/// </summary>
public static class Engine_TexSemantic
{
	/// <summary>
	/// Bumped whenever a policy below changes.
	/// </summary>
	/// <remarks>
	/// It is an INPUT TO THE CACHE KEY. Without it, changing a policy leaves every texture
	/// already on disk answering with the result of the old one, and the change looks like it
	/// did nothing — which is exactly the failure §5 asks the key to prevent.
	/// </remarks>
	public const int PolicyVersion = 2;

	private static readonly TexPolicy ColourPolicy = new()
	{
		Filter = TexFilterClass.Perceptual,
		MaxEdge = 2048,
		MinEdge = 128,
	};

	private static readonly TexPolicy NormalPolicy = new()
	{
		Filter = TexFilterClass.NormalAware,
		MaxEdge = 2048,
		MinEdge = 128,
	};

	private static readonly TexPolicy ScalarPolicy = new()
	{
		Filter = TexFilterClass.Scalar,
		MaxEdge = 1024,
		MinEdge = 128,
	};

	private static readonly TexPolicy NoPolicy = new()
	{
		Filter = TexFilterClass.None,
		MaxEdge = 0,
		MinEdge = int.MaxValue,
	};

	public static TexPolicy For(TexSemantic semantic) => semantic switch
	{
		TexSemantic.Albedo => ColourPolicy,
		TexSemantic.SelfIllumination => ColourPolicy,
		TexSemantic.Ui => ColourPolicy,
		TexSemantic.Normal => NormalPolicy,

		// SCALAR, not perceptual, and the reason is the alpha channel. The specular map's alpha
		// is the exponent selector — the shipped pixel shader builds
		// `power = lerp(c24.x, c24.y, spec.a)` — so it is a control signal, not an image, and a
		// model trained to invent plausible image detail has no business in it.
		TexSemantic.Specular => ScalarPolicy,

		// SCALAR for the same reason as the specular it is derived from: every channel is a
		// control signal, and a perceptual filter has no business inventing detail in one.
		TexSemantic.Orm => ScalarPolicy,

		// A cube map is six faces with shared edges. Resampling the faces independently opens a
		// visible seam at every edge, so it stays out until the filter is cube aware.
		TexSemantic.EnvironmentMap => NoPolicy,

		_ => NoPolicy,
	};

	/// <summary>
	/// The scale factor to apply to a source of this size, or 1 to leave it alone.
	/// </summary>
	/// <remarks>
	/// §10: the model supporting 4x is not a reason to use 4x. The rule reproduces the plan's
	/// table — 128 goes to 512, everything larger doubles — and then clamps against the
	/// policy's ceiling, so a 2048 albedo is already at its target and is not touched.
	///
	/// Returns a POWER OF TWO because the source is block compressed and the result has to be
	/// re-encoded; a non-power-of-two scale produces a size whose 4x4 blocks do not line up
	/// with the mip chain.
	/// </remarks>
	public static int TargetScale(int width, int height, TexPolicy policy)
	{
		if (policy.Filter == TexFilterClass.None)
		{
			return 1;
		}

		int edge = Math.Max(width, height);
		if (edge < policy.MinEdge || edge >= policy.MaxEdge)
		{
			return 1;
		}

		int scale = edge <= 128 ? 4 : 2;
		while (scale > 1 && edge * scale > policy.MaxEdge)
		{
			scale /= 2;
		}

		return scale;
	}
}
