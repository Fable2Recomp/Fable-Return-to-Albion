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
	/// A uniform enlargement this semantic wants, before the <see cref="MaxEdge"/> clamp.
	/// Zero keeps the size-stepped default (4x at or under 128, 2x above).
	/// </summary>
	/// <remarks>
	/// The stepped default spends its budget where magnification is worst, assuming a small
	/// source is drawn small. That holds for world art, whose texture size tracks the surface it
	/// covers, and not for the UI, where each texture is sized to the thing it draws.
	/// </remarks>
	public int Factor { get; init; }

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
	/// Bumped whenever a policy change would make an ALREADY CACHED artefact wrong.
	/// </summary>
	/// <remarks>
	/// It is an INPUT TO THE CACHE KEY, and it is GLOBAL: a bump orphans every artefact of every
	/// semantic and re-enhances the whole game. That is the price for a change that makes an
	/// existing artefact WRONG, and it buys nothing for one that only changes WHICH scale a
	/// texture is enhanced at — the key already carries the semantic and the scale, so a new
	/// scale is a new key, and a size that was declined never reached
	/// <see cref="Engine_TexCache.Key"/> at all (<c>Engine_TexLibrary.EnhanceDecoded</c> returns
	/// on <c>scale &lt;= 1</c> first). <see cref="UiPolicy"/>'s changes are all of that second
	/// kind; the artefacts it orphans are wasted, not stale.
	/// </remarks>
	public const int PolicyVersion = 3;

	/// <summary>
	/// THE EDGE CAP IS THE MEMORY DIAL. Measured over the whole built cache: 979 artefacts at a
	/// 2048 edge held 3,737 MB, 59% of every enhanced byte in the game, against 4,044 artefacts
	/// at 1024 and below holding the rest. Capping here at 1024 keeps the 4x on everything at or
	/// under 128 and the 2x from 129 to 512 — the great majority of the textures, and the ones a
	/// player is close enough to read — and stops only the tier that was ALREADY 1024 at source
	/// and was being doubled again.
	///
	/// Normals cost twice their albedo partner at the same size (BC5 is a byte a pixel against
	/// BC1's half), which is why they were the single largest class in the cache at 3,478 MB.
	/// They are also smooth by construction and renormalised in the shader, so they lose least
	/// to the cap. The ORM's occlusion and height are solved from the SOURCE normal, never the
	/// enhanced one, so nothing downstream changes.
	/// </summary>
	private const int EnhancedEdgeCap = 1024;

	private static readonly TexPolicy ColourPolicy = new()
	{
		Filter = TexFilterClass.Perceptual,
		MaxEdge = EnhancedEdgeCap,
		MinEdge = 128,
	};

	/// <summary>
	/// UI art is enhanced on a wider band than world colour, at both ends.
	///
	/// MinEdge, because <see cref="ColourPolicy"/>'s 128 floor is a statement about WORLD
	/// textures — below it a Fable 2 texture is usually a function rather than a picture, a
	/// stipple or a ramp or a lookup table, and there is no detail in one to recover. Nothing
	/// under <c>art/gui/</c> is a function: every one of them is a raster that is drawn on
	/// screen and looked at. Measured over the shipped set, that floor declined 69 of 147 GUI
	/// textures — the 32px minimap and controller glyphs and the whole 64px class the
	/// expression wheel, the spell icons and the will icons are drawn from.
	///
	/// MaxEdge, because <see cref="EnhancedEdgeCap"/> is a MEMORY dial sized against the world
	/// cache, where 979 artefacts at a 2048 edge held 3,737 MB and normals were the largest
	/// class. The UI has 147 textures and no normals at all, so the same ceiling costs a
	/// rounding error here while declining the 15 textures that are already 1024 at source —
	/// the front-end backdrops and the book pages, the largest things on the screen and the
	/// ones magnified most by the canvas camera.
	///
	/// That magnification is why this matters more for UI than for anything else. A Gameface
	/// screen is rendered as 720 canvas units of height mapped to the viewport's height
	/// (<c>Engine_UiSceneBuilder.FitCanvasCamera</c>), so on a 1440p display every UI texel
	/// covers two screen pixels before any of this runs.
	/// </summary>
	/// <summary>
	/// UI enhancement targets a texel density, not a size step.
	///
	/// A Gameface screen maps 720 canvas units of height onto the viewport
	/// (<c>Engine_UiSceneBuilder.FitCanvasCamera</c>), so the UI is magnified 2.02x at 1440p and
	/// 3.03x at 2160p. Measured over 1070 shipped picture quads, the density before enhancement
	/// is 0.37..0.90 texels per screen pixel at 1440p — roughly FLAT across source sizes, so the
	/// size-stepped default (which spends less on larger sources) is the wrong shape. Texels per
	/// screen pixel after enhancement:
	///
	///     src    1440p x2   1440p x4   2160p x2   2160p x4
	///      32       1.80       3.60       1.20       2.40
	///      64       1.44       2.88       0.96*      1.92
	///     128       0.98*      1.96       0.65*      1.31
	///     256       0.98*      1.96       0.65*      1.31
	///     512       1.20       2.40       0.80*      1.60
	///    1024       0.74*      1.48       0.49*      0.99
	///
	/// (* below native.) x4 is the smallest uniform factor that clears native at both 1440p and
	/// 4K. MaxEdge 4096 is load-bearing with it: at 2048 the clamp halves the 1024 class — the
	/// full-screen backdrops — back to x2, which is the class furthest from native.
	///
	/// x4 costs no extra inference: the model is x4 and `refii_upscaler_run` downsamples for any
	/// smaller request. The cost is cache, ~640 MB over ~147 textures.
	///
	/// MinEdge 16 rather than <see cref="ColourPolicy"/>'s 128: that floor means "below this a
	/// Fable 2 texture is a function, not a picture" and applies to the ramps and lookup tables
	/// under <c>Globals/Engine/Textures</c>. Nothing under <c>art/gui/</c> is one.
	/// </summary>
	private static readonly TexPolicy UiPolicy = new()
	{
		Filter = TexFilterClass.Perceptual,
		Factor = 4,
		MaxEdge = 4096,
		MinEdge = 16,
	};

	private static readonly TexPolicy NormalPolicy = new()
	{
		Filter = TexFilterClass.NormalAware,
		MaxEdge = EnhancedEdgeCap,
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
		TexSemantic.Ui => UiPolicy,
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

		int scale = policy.Factor > 0 ? policy.Factor : edge <= 128 ? 4 : 2;
		while (scale > 1 && edge * scale > policy.MaxEdge)
		{
			scale /= 2;
		}

		return scale;
	}
}
