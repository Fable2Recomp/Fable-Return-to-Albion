using System;
using Godot;
using Refii.Level;

namespace Refii.Lighting;

public static class Engine_LevelLighting
{
	public const string SunNodeName = "Sun";

	public const float SunFarDepth = 120.0f;

	public const float CascadeSplitNear = 35.0f;

	public const float NearSlopeBias = 2.0f;

	public static DirectionalLight3D BuildSun(LightingFrame frame)
	{
		var light = new DirectionalLight3D
		{
			Name = SunNodeName,
			DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
			DirectionalShadowMaxDistance = SunFarDepth,
			DirectionalShadowSplit1 = CascadeSplitNear / SunFarDepth,
			ShadowNormalBias = NearSlopeBias,
			ShadowBias = 0.03f,
		};

		UpdateSun(light, frame);
		return light;
	}

	public static void UpdateSun(DirectionalLight3D light, LightingFrame frame)
	{
		light.LightColor = new Color(frame.SunColour[0], frame.SunColour[1], frame.SunColour[2]);
		light.LightEnergy = frame.SunEnergy;
		light.ShadowEnabled = frame.SunShadow;
		light.Transform = new Transform3D(SunBasis(frame.SunDirection), Vector3.Zero);
	}

	/// <summary>
	/// The luminance that tone mapping maps to white. A STAND-IN — see below.
	///
	/// The guest's curve is a 512-entry 1D LUT that <c>sub_8217E428</c> rebuilds on the CPU
	/// every frame over the input domain [0, 4] — the entries are stored as a ratio against
	/// <c>((i + 0.5) / 512) * 4.0</c> at 0x8217E72C, so the LUT holds a GAIN rather than an
	/// output colour, which is exactly how the tone-map pixel shader uses it:
	///
	///     r0.x   = tex1D(g_ToneMapSampler, luminance * g_RecipMaxLuminance.x).x
	///     colour = saturate(brightness * r0.x * colour)
	///
	/// and <c>g_RecipMaxLuminance</c> is the literal 0.25, so the domain really is [0, 4].
	///
	/// FOUR IS THE DOMAIN BOUND, NOT THE WHITE POINT. The white point is not authored anywhere
	/// — the GDB has no such field — it is derived per frame from the adapted luminance triple:
	///
	///     v3  = *(a1+0x41A0) - *(a1+0x4198)        adaptedMax - adaptedMin
	///     v18 = 1.0 / (v3 * v3)                    1/W^2, at 0x8217E530
	///
	/// Reproducing it therefore needs the triple, which comes from a CONVERTTOLUMINANCE resolve
	/// of the previous frame binned into a CPU histogram (<c>sub_822AA9A0</c>). We do not render
	/// the guest's frame, so there is no triple to read and this stays a constant.
	/// </summary>
	public const float TonemapWhite = 4.0f;

	/// <summary>
	/// A STATIC STAND-IN FOR THE GUEST'S AUTO-EXPOSURE, which Godot 4 cannot reproduce.
	///
	/// The guest's post-process is two stages and the curve is the second one. First
	/// <c>sub_822AA9A0</c> locks the previous frame's luminance surface, bins 1024 histogram
	/// buckets on the CPU and takes percentile thresholds; then exposure walks toward putting
	/// that average on MIDDLE_GRAY before the Reinhard curve sees anything. Shipping the curve
	/// without the exposure is worse than shipping neither — it is designed to receive a signal
	/// that has already been normalised, so everything lands on the toe and the region reads as
	/// dusk at two in the afternoon.
	///
	/// The value comes from two stated defaults rather than from taste. MIDDLE_GRAY is 0.45 and
	/// AVG_LUMINANCE_LOWER_BOUND is 0.15 (both from the theme's default constructor
	/// <c>sub_82A45DD0</c>), and the measured median luminance of this region at midday is
	/// 0.026 — well under that floor, so the guest's own clamp is what would apply.
	///
	/// THE FORM IS NOT QUITE THE GUEST'S, and it is worth being honest about which part is
	/// approximated. The key the curve actually multiplies by is a DIFFERENCE, not a ratio
	/// against the average alone (0x8217E524):
	///
	///     v15 = *(a1+0x2C) / (*(a1+0x419C) - *(a1+0x4198))    middleGray / (avg - min)
	///
	/// so the denominator is the adapted average above the adapted MINIMUM. Both of those are
	/// per-frame histogram outputs, and a static stand-in cannot honour a difference between
	/// two values it does not have — picking floors for them would be inventing the numerator
	/// and denominator both. The ratio below is kept because it produces a working image on the
	/// region it was measured against; it is not the guest's arithmetic.
	///
	/// Also unmodelled: the curve is not pure Reinhard. Each entry is lerped against a contrast
	/// term by <c>*(a1+0x40)</c>, where the contrast is <c>pow(x, *(a1+0x44))</c> with a
	/// squaring fast path when that exponent is 2.0 (0x8217E51C). Do not confuse that squaring
	/// with the colour-texture decode — they are unrelated.
	///
	/// It is a CONSTANT where the guest has a control loop, so a genuinely bright or genuinely
	/// dark region will not be compensated the way the original compensates.
	/// </summary>
	public const float TonemapExposure = 1.0f;

	/// <summary>The middle grey the guest drives the adapted average toward.</summary>
	/// <remarks>
	/// The theme's MIDDLE_GRAY, default 0.45, and the numerator of the curve's key at
	/// 0x8217E524. Stated by the game rather than chosen.
	/// </remarks>
	public const float AutoExposureTargetGrey = 0.45f;

	/// <summary>
	/// How fast exposure follows the scene, as a rate per second.
	/// </summary>
	/// <remarks>
	/// Derived from the guest's own smoother rather than picked. It blends by
	/// <c>1 - (1 - ExposureSpeed)^(dt * 30)</c> with ExposureSpeed 0.01 (0x82276810), so over one
	/// second it closes <c>1 - 0.99^30 = 0.260</c> of the gap. An exponential approach with rate
	/// k closes <c>1 - e^-k</c>, so k = -ln(1 - 0.260) = 0.30.
	/// </remarks>
	public const float AutoExposureSpeed = 0.30f;

	/// <summary>
	/// The guest's auto-exposure, as close as Godot's own control loop gets to it.
	/// </summary>
	/// <remarks>
	/// A LOOP, not a constant. The curve is rebuilt every frame against a luminance histogram,
	/// because the guest compensates for scene brightness continuously: any fixed value is
	/// measured on one scene and wrong on every other, and no amount of tuning it fixes that.
	///
	/// What matches: the target is the theme's stated MIDDLE_GRAY, and the rate is derived from
	/// the guest's own blend. What does not, and is worth knowing before trusting this to the
	/// last stop:
	///
	///   - the guest's key is `middleGray / (adaptedAvg - adaptedMin)`, a difference. Godot
	///     measures an average and has nowhere to put the minimum, so the `- adaptedMin` term is
	///     dropped. That makes our exposure lower than the guest's wherever the darkest part of
	///     the frame is well above black.
	///   - the guest clamps the resulting scalar to [1, 16] (`sub_82191C18`). The sensitivity
	///     bounds below carry that 16:1 range, but in Godot's ISO units rather than the guest's.
	///   - the guest also smooths the whole 512-entry curve per entry, not just the scalar.
	///
	/// With the loop running, <see cref="TonemapWhite"/> stops being a guess about absolute
	/// luminance and becomes a white point in ADAPTED space — the same space the guest's
	/// `adaptedMax - adaptedMin` lives in — which is why it is left at the LUT's domain bound
	/// rather than retuned.
	/// </remarks>
	public static CameraAttributesPractical BuildCameraAttributes() => new()
	{
		// OFF, because we run the guest's loop instead — see `Engine_AutoExposure`. Godot's
		// control loop targets a plain AVERAGE, and on a frame with a large bright sky the average
		// is dominated by the sky, so the exposure stays low and the street underneath never
		// lifts. The guest avoids exactly that by taking PERCENTILE thresholds off a histogram and
		// subtracting the adapted minimum, and by flooring the average at 0.3 whatever the frame
		// contains. Two loops fighting over the same frame is worse than either alone.
		AutoExposureEnabled = false,
	};

	/// <summary>
	/// The compositor that runs the guest's tone curve, or null where compute is unavailable.
	/// </summary>
	/// <remarks>
	/// One per camera. The effect owns GPU resources keyed to a rendering device, so it is built
	/// here rather than shared as a singleton — a level change disposes the camera and the effect
	/// frees its pipelines with it.
	/// </remarks>
	public static Compositor BuildCompositor() => new()
	{
		CompositorEffects = new Godot.Collections.Array<CompositorEffect>
		{
			new Engine_ExposureEffect(),
		},
	};

	public static void ConfigureEnvironment(Godot.Environment environment, LightingFrame frame)
	{
		if (environment is null)
		{
			return;
		}

		// EXTENDED REINHARD, because that is the guest's curve family.
		//
		// sub_8217E428 builds each LUT entry as `((v18*x + 1.0) * x) / (x + 1.0)` at
		// 0x8217E59C — that is `x * (1 + x/W^2) / (1 + x)` with v18 = 1/W^2, extended Reinhard
		// with a white point, which is what Godot's Reinhardt tonemapper and its TonemapWhite
		// are. Chosen from the decompile rather than because it flattered the image.
		//
		// Without it the region is rendered linearly against white = 1.0, and the theme states
		// values well past that — this level's main light alone is 1.58 — so every lit surface
		// clipped while everything else stayed crushed.
		//
		// What is NOT reproduced, and is the reason a scene can still read too dark or too
		// bright: the guest AUTO-EXPOSES, and the curve is rebuilt against that exposure every
		// frame rather than being a fixed transfer. sub_822767E0 resolves the frame through
		// CONVERTTOLUMINANCE, sub_822AA9A0 bins it into a CPU histogram and takes percentile
		// thresholds, and the whole 512-entry curve is then smoothed toward the new one per
		// entry (0x8217E560) at a blend of `1 - (1 - ExposureSpeed)^(dt * 30)` — frame-rate
		// independent, normalised to 30 Hz, with ExposureSpeed 0.01.
		//
		// Both of the curve's own parameters come out of that histogram, so neither survives
		// as a constant: the white point is adaptedMax - adaptedMin and the key is
		// middleGray / (adaptedAvg - adaptedMin). Closing this needs a luminance pass of our
		// own over the rendered frame, not a better constant.
		//
		// THIS IS THE ONLY TONEMAP IN THE RENDERER, and it has to stay that way. The guest
		// tonemaps once for the whole frame, in the post-processing material — every surface
		// shader upstream of it writes linear HDR and applies no transfer at all. That was
		// checked against the shipped microcode across the whole sky and water set: 0111
		// skycolour, 0114 clouds, 0117/0118/0119/0120/0121 for the sun and moon, 0055 and 0057
		// for water. Not one of them contains a divide-by-one-plus, a sqrt, a pow output stage
		// or an sRGB constant; each ends on a single multiply. A surface that tonemaps itself
		// gets compressed twice, once by its own curve and again by this one.
		//
		// AND THE DISPLAY CURVE IS A PURE 2.2 POWER, NOT sRGB. The post material's last act is
		// `pow(colour, g_GlobalGammaAdjustment.x)`, built as log2/multiply/exp2, and it runs in
		// the no-tonemap variant 0499 as well, so it belongs to the output stage rather than to
		// the curve. The exponent is not tunable: `0x821E640C` loads flt_82000C9C = 0x3EE8BA2E,
		// which is bit-exactly `1.0f / 2.2f`, multiplies it by the post object's +0xB0 — set to
		// 1.0 once in the defaults initialiser sub_82A7C6E8 and never written again — and sends
		// it to binding 98, `g_GlobalGammaAdjustment`. The brightness slider is a DIFFERENT
		// uniform, binding 1 `g_ActualFramebufferBrightness` at +0x41AC, which is the one that
		// does change per frame.
		//
		// We do not reproduce that curve, and should not: Godot owns the output transform and
		// encodes sRGB. Pure 2.2 and sRGB agree to well under a percent except in the deep
		// shadows, where sRGB's linear toe lifts black slightly. It is far the smaller of the
		// two divergences on this path — the auto-exposure above is the one that matters.
		// LINEAR, BECAUSE WE TONEMAP OURSELVES NOW. `Engine_ExposureEffect` runs the guest's own
		// curve on the HDR buffer at post-transparent, against a white point and a key MEASURED
		// from the frame's luminance histogram rather than guessed at.
		//
		// Leaving Godot's Reinhardt on would compress the frame a second time, and the fixed
		// numbers it was given are precisely what broke: `TonemapWhite = 4.0` is the histogram's
		// DOMAIN BOUND, not a white point, and the shipped themes author SunIntensity high enough
		// that a perfectly ordinary sky sat on the white point and clipped to paper white, dragging
		// the water that reflects it along with it. The measured white point lands between about
		// 0.6 and 3.8.
		//
		// Measured over the 308 records of environmentthemes.gdb that state SunIntensity: median
		// 3.68, mean 4.49, range 0..30, with 73% at or above 2.0 — enough to put every channel of
		// the dome above 1.0. The per-region value is still unmeasured: theme records in the shared
		// GDB carry no region naming, which lives in the level GDB inside levels.bnk.
		environment.TonemapMode = Godot.Environment.ToneMapper.Linear;

		if (frame.HasAmbient)
		{
			environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
			environment.AmbientLightColor = new Color(
				frame.AmbientColour[0], frame.AmbientColour[1], frame.AmbientColour[2]);
			environment.AmbientLightEnergy = frame.AmbientEnergy;
		}

		if (frame.HasFog)
		{
			environment.FogEnabled = true;
			environment.FogMode = Godot.Environment.FogModeEnum.Depth;
			environment.FogDepthBegin = frame.FogBegin;
			environment.FogDepthEnd = frame.FogEnd;
			environment.FogLightColor = new Color(
				frame.FogColour[0], frame.FogColour[1], frame.FogColour[2]);

			// Godot's depth fog is (ramp ^ curve) * density, so density is the amount reached
			// AT FogDepthEnd — which is exactly what the theme's far density states. Left at
			// Godot's 1.0 the ramp reaches total occlusion instead, and with this region's fog
			// colour of 5/255 that is a black wall rather than haze.
			environment.FogDensity = frame.FogDensity;

			// THE SKY IS NOT FOGGED. Godot blends the sky toward the fog colour like any other
			// surface, and the theme's own sky already paints its horizon haze from the stated
			// Rayleigh and Mie coefficients. Doing both is two atmosphere models fighting, and
			// the loser is visible: a black band between the cloud deck and the rooftops, which
			// is this region's near-black fog colour applied to the entire sky dome.
			environment.FogSkyAffect = 0.0f;
		}
	}

	/// <summary>
	/// Aims the sun so it agrees with the sun DISC in the sky.
	///
	/// <see cref="Engine_SkyFrame"/> builds SunDirection as (groundX, up, groundZ) — component
	/// [1] is the vertical — so it is already Y-up and needs only the X flip that carries it
	/// into the level root's mirrored space. Engine_CelestialElements does exactly that,
	/// <c>(-d0, d1, d2)</c>, to place the visible disc.
	///
	/// A DirectionalLight3D travels along its own -Z, and light travels FROM the sun, so the
	/// forward vector is the NEGATION of the direction to the disc: <c>(d0, -d1, -d2)</c>.
	///
	/// NOT all three: negating X as well puts the sun on one side of the sky and the light on
	/// the other, so shadows fall the wrong way horizontally — subtle enough to read as "the
	/// lighting needs work" rather than as a sign error, especially under a heavily overcast
	/// sky where the disc is hard to locate.
	/// </summary>
	private static Basis SunBasis(float[] direction)
	{
		var forward = new Vector3(direction[0], -direction[1], -direction[2]);
		if (forward.LengthSquared() < 1e-8f)
		{
			forward = Vector3.Down;
		}

		forward = forward.Normalized();
		Vector3 up = MathF.Abs(forward.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
		return Basis.LookingAt(forward, up);
	}
}
