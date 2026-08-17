using System;

namespace Refii.Lighting;

/// <summary>
/// The guest's auto-exposure: a luminance histogram, three adapted values, and an exposure scalar.
///
/// THIS IS THE CONTROL LOOP THE RENDERER HAS BEEN MISSING, and its absence is why a region reads
/// as crushed shadows under a blown-white sky. The shipped themes author SunIntensity with a
/// median of 4.09 and a maximum of 30, while a lit surface lands near 0.1 — a hundred to one —
/// and the game handles that range by re-measuring every frame rather than by picking a constant.
/// A fixed exposure and a fixed white point cannot: whatever they are tuned to, the rest of the
/// range falls off one end.
///
/// Every number here was read out of the image rather than taken from a summary. The two that
/// matter most were BOTH wrong in this project's earlier notes: MIDDLE_GRAY is 0.36, not 0.45,
/// and the average's floor is 0.3, not 0.15 — 0.15 is a different field
/// (COLOUR_SENSITIVITY_THRESHOLD, at the same object's +0x30). The floor is the one that governs:
/// this region's measured mean luminance at midday is 0.026, two orders below it, so in practice
/// the key is set by the authored bound and not by the scene at all.
///
/// The pixel side is not here. This is the arithmetic, kept free of Godot types so it can be run
/// and checked without a GPU — see <c>ExposureCheck</c>.
/// </summary>
public sealed class Engine_AutoExposure
{
	/// <summary>Buckets actually used. 1024 are allocated; the loop bound is 512.</summary>
	/// <remarks>
	/// <c>822aa9e8 memset(a3+8, 0, 0x1000)</c> clears 1024 dwords, but the loop reads its bound
	/// from <c>*(X+0x108)</c>, which the constructor sets to 512 at 0x82A7BB38. The spare capacity
	/// is not slack — a fully saturated pixel maps to bucket N and would otherwise run off the end.
	/// </remarks>
	public const int Buckets = 512;

	/// <summary>The luminance the top bucket ends at: <c>*(X+0x10C)</c>, 4.0.</summary>
	/// <remarks>
	/// The same 4.0 the tone-map shader divides by — <c>g_RecipMaxLuminance</c> is the literal
	/// 0.25 — so the histogram's domain and the curve's domain are the same interval by
	/// construction, not by coincidence.
	/// </remarks>
	public const float Range = 4.0f;

	/// <summary>Luminance per bucket. Mapping is LINEAR — no log, no exponent.</summary>
	/// <remarks>
	/// <c>822aa9fc</c>–<c>822aab50</c>: the range is divided by N to get the width, its reciprocal
	/// is splatted, and each texel becomes
	/// <c>vctsxs(vminfp(vmaxfp(L, 0), range) * (N/range))</c> — a clamp, a multiply and a
	/// truncate. With N = 512 over 4.0 the scale is exactly 128.
	/// </remarks>
	public const float BucketWidth = Range / Buckets;

	// The three percentiles, as fractions of the total pixel count. Theme-authored; these are the
	// constructor defaults from `sub_82A7C6E8`, each verified from its own float:
	//   +0x34 flt_8209CB94 = 0x3C23D70A = 0.01
	//   +0x38 flt_820992D4 = 0x3F000000 = 0.5
	//   +0x3C flt_8209B840 = 0x3F7D70A4 = 0.99
	public const float MinPercentile = 0.01f;
	public const float AvgPercentile = 0.5f;
	public const float MaxPercentile = 0.99f;

	/// <summary>
	/// The bound on the measured MINIMUM — a CEILING, not a floor. <c>flt_8209B650</c> = 0.1.
	/// </summary>
	/// <remarks>
	/// THE THREE BOUNDS ARE NOT APPLIED THE SAME WAY, and reading them as three floors gets the
	/// darkest part of the frame badly wrong. The selects in <c>sub_822AA488</c> are asymmetric:
	///
	///     822aa5b4  fsel f6, f0,  f4,  f6    ; MIN: measured &lt; bound ? measured : bound  -> min()
	///     822aa5c0  fsel f3, f13, f12, f3    ; AVG: measured &lt; bound ? bound : measured  -> max()
	///     822aa5c4  fsel f2, f4,  f7,  f2    ; MAX: measured &lt; bound ? bound : measured  -> max()
	///
	/// So the minimum is capped from ABOVE while the average and maximum are floored from BELOW.
	/// That is what keeps `avg - min` and `max - min` strictly positive whatever the frame
	/// contains — the two quantities the curve divides by.
	/// </remarks>
	public const float MinLuminanceUpperBound = 0.1f;

	/// <summary>The floor under the measured MEAN. <c>flt_8209B634</c> = 0.3.</summary>
	public const float AvgLuminanceLowerBound = 0.3f;

	/// <summary>The floor under the measured MAXIMUM. <c>flt_8209BC64</c> = 0.6.</summary>
	public const float MaxLuminanceLowerBound = 0.6f;

	/// <summary>How fast the loop follows the scene: <c>flt_8209CB94</c> = 0.01.</summary>
	public const float ExposureSpeed = 0.01f;

	/// <summary>The reference rate the blend is normalised to: <c>flt_820992A4</c> = 30.0.</summary>
	public const float BlendReferenceRate = 30.0f;

	// The exposure scalar's own bounds and its blend, all from `sub_82191C18`:
	//   flt_82099410 = 16.0, flt_8209B650 = 0.1, flt_8209949C = 0.75,
	//   flt_820992D8 = 0.05, flt_82000A54 = 10.0
	public const float ExposureMin = 1.0f;
	public const float ExposureMax = 16.0f;
	public const float ExposureBlend = 0.05f;
	public const float ExposureRatioMin = 0.1f;
	public const float ExposureRatioMax = 10.0f;
	public const float ExposureTargetScale = 0.75f;

	private readonly int[] _histogram = new int[Buckets + 1];

	private bool _hasHistory;

	/// <summary>Adapted minimum — the guest's <c>X+0x4198</c>.</summary>
	public float AdaptedMin { get; private set; }

	/// <summary>Adapted average — <c>X+0x419C</c>. From the MEAN, not from a percentile.</summary>
	/// <remarks>
	/// <c>sub_822AA488</c> reads <c>822aa558 lfs f12, 0x200C(r29)</c>, and +0x200C is the
	/// histogram's MEAN slot. The average PERCENTILE is computed into the neighbouring +0x2010 and
	/// nothing on this path was found to read it. Feeding the median here instead would track a
	/// different statistic entirely on any frame that is not symmetric — which is every frame with
	/// a sky in it.
	/// </remarks>
	public float AdaptedAvg { get; private set; }

	/// <summary>Adapted maximum — <c>X+0x41A0</c>.</summary>
	public float AdaptedMax { get; private set; }

	/// <summary>
	/// The exposure scalar, <c>X+0x41A8</c>. Constructor default 4.0, and it DIVIDES the measured
	/// luminances on the next frame, which is what closes the loop.
	/// </summary>
	public float Exposure { get; private set; } = 4.0f;

	/// <summary>
	/// LAST frame's exposure scalar — the guest's <c>X+0x41B0</c>, and the divisor the CURVE uses.
	/// </summary>
	/// <remarks>
	/// A ONE-FRAME LAG THAT IS REALLY IN THE BINARY, not an artefact of reading it. `sub_82191C18`
	/// opens by copying the live scalar into this slot — <c>82191c90 stfs f1, 0x41B0(r3)</c> after
	/// <c>82191c8c lfs f1, 0x41A8(r3)</c> — and it runs at the END of the frame, after the curve
	/// has already been built. So the curve is scaled by the PREVIOUS scalar while the statistics
	/// it consumes were normalised by the CURRENT one.
	///
	/// Both start at 4.0 and the loop moves at 5% per frame, so they agree closely in the steady
	/// state and diverge only while the exposure is chasing a change. Reproducing the lag costs
	/// one float and avoids inventing a synchronisation the game does not have.
	/// </remarks>
	public float ExposurePrevious { get; private set; } = 4.0f;

	/// <summary>Bins one frame's luminance and advances the loop.</summary>
	/// <param name="luminance">
	/// The frame's luminance texels. The guest reads a QUARTER-WIDTH by QUARTER-HEIGHT R32F
	/// surface (<c>sub_82A7BD68</c>: <c>v44 = *a2/4</c>, <c>v45 = a2[1]/4</c>) produced by a
	/// CONVERTTOLUMINANCE resolve of the PREVIOUS frame, and walks every texel of it once — no
	/// stride, no sampling. It skips only the tile padding, which is an Xbox memory-layout detail
	/// with no equivalent for us.
	/// </param>
	/// <param name="deltaSeconds">Real seconds since the last call.</param>
	public void Submit(ReadOnlySpan<float> luminance, float deltaSeconds)
	{
		Array.Clear(_histogram);

		float scale = Buckets / Range;
		foreach (float value in luminance)
		{
			float clamped = Math.Clamp(float.IsFinite(value) ? value : 0.0f, 0.0f, Range);
			_histogram[(int)(clamped * scale)]++;
		}

		Advance(luminance.Length, deltaSeconds);
	}

	/// <summary>
	/// Adopts counts binned elsewhere — by the GPU, in the shipping path.
	/// </summary>
	/// <remarks>
	/// The split exists so the ARITHMETIC has no GPU in it. Everything below <see cref="Advance"/>
	/// is the part that decides how the frame looks, and it runs identically whether the counts
	/// came from a compute dispatch or from a synthetic frame in a headless check. A measurement
	/// that can only be exercised on a GPU is a measurement that gets verified by looking at it.
	/// </remarks>
	public void LoadHistogram(ReadOnlySpan<int> counts)
	{
		Array.Clear(_histogram);

		int n = Math.Min(counts.Length, Buckets);
		for (int i = 0; i < n; i++)
		{
			_histogram[i] = counts[i];
		}
	}

	/// <summary>
	/// Derives the three values from the histogram and blends them toward the running ones.
	/// </summary>
	/// <remarks>
	/// Separate from <see cref="Submit"/> so the binning can be done on the GPU and only the
	/// counts read back — the guest bins on the CPU from a locked surface, which is the same
	/// measurement with the readback in a different place.
	/// </remarks>
	public void Advance(int totalPixels, float deltaSeconds)
	{
		// THE PERCENTILE IS THE BUCKET HOLDING THE Nth PIXEL, and the running sum EXCLUDES the
		// bucket being tested — `822aacac cmplw cr6, r10, r4` compares the count of everything
		// BEFORE i, and `822aacdc add r10, r8, r10` adds h[i] afterwards. Including it shifts
		// every threshold by one bucket.
		float minThreshold = MinPercentile * totalPixels;
		float maxThreshold = MaxPercentile * totalPixels;

		long weighted = 0;
		long total = 0;
		int cumulative = 0;
		int idxMin = 0;
		int idxMax = 0;

		for (int i = 0; i < Buckets; i++)
		{
			if (cumulative < minThreshold)
			{
				idxMin = i;
			}

			if (cumulative < maxThreshold)
			{
				idxMax = i;
			}

			cumulative += _histogram[i];
			weighted += (long)_histogram[i] * i;
			total += _histogram[i];
		}

		// `822ab004`: an empty histogram reports a mean of 0.5 rather than dividing by zero.
		float mean = total > 0 ? (float)weighted / total * BucketWidth / Exposure : 0.5f;
		float measuredMin = idxMin * BucketWidth / Exposure;
		float measuredMax = idxMax * BucketWidth / Exposure;

		// The asymmetric bounds — see MinLuminanceUpperBound.
		float targetMin = MathF.Min(measuredMin, MinLuminanceUpperBound);
		float targetAvg = MathF.Max(mean, AvgLuminanceLowerBound);
		float targetMax = MathF.Max(measuredMax, MaxLuminanceLowerBound);

		float k = Blend(deltaSeconds);

		AdaptedMin += (targetMin - AdaptedMin) * k;
		AdaptedAvg += (targetAvg - AdaptedAvg) * k;
		AdaptedMax += (targetMax - AdaptedMax) * k;

		UpdateExposure(measuredMax, mean);
		_hasHistory = true;
	}

	/// <summary>
	/// The frame-rate compensated blend: <c>1 - (1 - ExposureSpeed)^(dt * 30)</c>.
	/// </summary>
	/// <remarks>
	/// <c>82276810</c>–<c>82276890</c>. The first frame takes 1.0 outright — <c>82276898</c>
	/// stores 1.0 when the history flag at <c>X+0x4190</c> is clear — so the loop starts ON the
	/// measurement instead of easing up from zero, which would otherwise open every level on a
	/// black screen and fade in over a second.
	///
	/// The 30 is <c>flt_820992A4</c> = 0x41F00000, the pose clock, so ExposureSpeed 0.01 means one
	/// per cent per 30 Hz tick however fast we are actually running.
	/// </remarks>
	public float Blend(float deltaSeconds)
	{
		if (!_hasHistory || !float.IsFinite(deltaSeconds) || deltaSeconds <= 0.0f)
		{
			return 1.0f;
		}

		float speed = Math.Clamp(ExposureSpeed, 0.0f, 1.0f);
		return 1.0f - MathF.Pow(1.0f - speed, deltaSeconds * BlendReferenceRate);
	}

	/// <summary>
	/// The exposure scalar, which is the part clamped to [1, 16] — not the key and not the white
	/// point.
	/// </summary>
	/// <remarks>
	/// <c>sub_82191C18</c>, and it clamps TWICE: once on the ratio-scaled target (0x82191D3C /
	/// 0x82191D5C) and again after the 0.05 blend (0x82191D84 / 0x82191DA8). The 0.05 is its own
	/// fixed constant — it is NOT the ExposureSpeed blend the three adapted values use, so the
	/// scalar and the values follow the scene at different rates.
	/// </remarks>
	private void UpdateExposure(float measuredMax, float mean)
	{
		// `82191c90`, and it happens FIRST — the previous scalar is published before the new one
		// is computed, which is what creates the one-frame lag the curve runs on.
		ExposurePrevious = Exposure;

		// `82191cb0`: the target luminance is the measured maximum, but never more than sixteen
		// times the mean — a single specular pinprick cannot drag the whole frame dark.
		float top = MathF.Min(measuredMax, mean * ExposureMax) * Exposure;
		top = MathF.Max(top, ExposureRatioMin);

		float ratio = Range * ExposureTargetScale / top;
		ratio = Math.Clamp(ratio, ExposureRatioMin, ExposureRatioMax);

		float target = Math.Clamp(ratio * Exposure, ExposureMin, ExposureMax);
		Exposure = Math.Clamp(
			Exposure + ((target - Exposure) * ExposureBlend), ExposureMin, ExposureMax);
	}

	/// <summary>
	/// The tone curve's key: <c>MIDDLE_GRAY / (adaptedAvg - adaptedMin)</c>.
	/// </summary>
	/// <remarks>
	/// <c>8217E524</c>. MIDDLE_GRAY is <c>*(X+0x2C)</c> = <c>flt_82000FE0</c> = 0x3EB851EC =
	/// <b>0.36</b>, which I read from the image rather than inherit: this project's earlier note
	/// recorded 0.45, and 0.45 belongs to a different object.
	///
	/// The denominator cannot vanish, because the bounds are asymmetric — the average is floored
	/// at 0.3 and the minimum capped at 0.1, so it is at least 0.2 before adaptation.
	/// </remarks>
	public const float MiddleGray = 0.36f;

	public float Key => MiddleGray / MathF.Max(AdaptedAvg - AdaptedMin, 1e-4f);

	/// <summary>
	/// The curve's white point: <c>adaptedMax - adaptedMin</c>.
	/// </summary>
	/// <remarks>
	/// <c>8217E530</c>, and the curve uses its reciprocal square (<c>v18 = 1/W^2</c>). Bounded
	/// below by 0.5 for the same reason the key's denominator is bounded — 0.6 floor against a 0.1
	/// ceiling.
	///
	/// THIS IS THE NUMBER OUR FIXED `TonemapWhite = 4.0` WAS STANDING IN FOR, and 4.0 is the
	/// histogram's domain bound rather than a white point. A sky authored at SunIntensity 4 landed
	/// exactly on it and clipped to pure white; the real white point on a measured frame is
	/// typically nearer 0.5 to 1.5.
	/// </remarks>
	public float WhitePoint => MathF.Max(AdaptedMax - AdaptedMin, 1e-4f);

	/// <summary>
	/// What a luminance is scaled by before the curve sees it: <c>key / previousExposure</c>.
	/// </summary>
	/// <remarks>
	/// Folding the guest's two factors into one. Entry <c>i</c> is built from
	/// <c>x = i * step * key</c> with <c>step = MaxLuminance / (previousExposure * 512)</c>
	/// (<c>8217E4B8</c>, <c>8217E56C</c>), and the shader samples the LUT at
	/// <c>luminance * g_RecipMaxLuminance</c> with that constant a hard-coded 0.25
	/// (<c>Render_SetShaderConstantFloatById_INFERRED(187, 0.25)</c> at 0x821E5FA4). Entry
	/// <c>i</c> therefore stands for <c>luminance = 4i/512</c>, and substituting gives
	/// <c>x = luminance * key / previousExposure</c> — the MaxLuminance cancels.
	/// </remarks>
	public float CurveScale => Key / MathF.Max(ExposurePrevious, 1e-4f);

	/// <summary>
	/// The mapped luminance for an input luminance — the guest's tone curve.
	/// </summary>
	/// <remarks>
	/// EXTENDED REINHARD, and with the shipped defaults that is the WHOLE curve. The stored entry
	/// is <c>R + (pow(c, Exponent) - R) * ContrastEnhancement</c> (<c>8217E61C</c>), where R is
	/// the Reinhard term at <c>8217E59C</c> and <c>c</c> is the frame's own smoothed luminance CDF
	/// clamped into a corridor around a ramp. <b>ContrastEnhancement defaults to 0.0</b>
	/// (<c>flt_82099484</c>, stored at 0x82A7C78C), which lerps the entire histogram-equalisation
	/// branch away and leaves R alone.
	///
	/// So the shipped game tone-maps with extended Reinhard against a MEASURED white point, and
	/// the equalisation machinery is authored-off. That is worth stating plainly, because the
	/// branch is elaborate enough to look load-bearing: it is not, unless a theme turns it on.
	///
	/// NOT MODELLED HERE: the CDF/contrast branch, for exactly that reason. If a shipped theme is
	/// ever found stating a non-zero TONE_MAPPING_CONTRAST_ENHANCEMENT, this is where it goes, and
	/// it needs the per-frame luminance CDF as well as the histogram.
	/// </remarks>
	public float MapLuminance(float luminance)
	{
		float x = MathF.Max(luminance, 0.0f) * CurveScale;
		float invW2 = 1.0f / (WhitePoint * WhitePoint);
		return ((x * invW2) + 1.0f) * x / (x + 1.0f);
	}

	/// <summary>
	/// The gain the shader multiplies the colour by: <c>mapped / luminance</c>.
	/// </summary>
	/// <remarks>
	/// THE CURVE IS A GAIN ON LUMINANCE, NOT A PER-CHANNEL TRANSFER, and that distinction decides
	/// how a bright colour desaturates. The LUT is one channel — R32F, 512x1
	/// (<c>XGSetLineTextureHeader(0x200, 1, …)</c> with format 32_FLOAT) — its entries are stored
	/// already divided by their own input (<c>8217E728</c>), and the recompiled tone-map blob
	/// 0500 samples it once with the frame's luminance and multiplies the whole rgb by the single
	/// result. Luminance itself is Rec.709: <c>dot(rgb, (0.2125, 0.7154, 0.0721))</c>.
	///
	/// A per-channel Reinhard would pull each channel toward white separately and wash the hue out
	/// of every highlight; this keeps the hue and rescales the brightness.
	/// </remarks>
	public float Gain(float luminance) =>
		luminance > 1e-6f ? MapLuminance(luminance) / luminance : 1.0f;

	/// <summary>
	/// The <c>brightness</c> the tone-map shader multiplies by: <c>g_ActualFramebufferBrightness</c>.
	/// </summary>
	/// <remarks>
	/// OMITTING THIS MAKES THE WHOLE FRAME DARKER BY THE EXPOSURE SCALAR, and in a dark interior —
	/// where the loop drives the scalar to its ceiling — that is a factor of sixteen. The curve
	/// divides by the exposure and the shader multiplies it back:
	///
	///     colour = saturate(brightness * LUT(luminance * 0.25) * colour)
	///
	/// so the two cancel for ordinary luminances and the net lift is just the key. Implementing
	/// the divide without the multiply leaves only the divide.
	///
	/// It is published by <c>sub_82191C18</c> at its very last instruction —
	/// <c>82191dbc stfs f0, 0x41AC(r3)</c> with f0 the scalar it just stored to +0x41A8. The other
	/// arm, <c>82191db4</c>, publishes 1.0 when a flag at the object's +0 is clear; WHAT THAT FLAG
	/// IS HAS NOT BEEN ESTABLISHED, so this always reports the scalar. If a case is ever found
	/// where the guest ships 1.0 instead, it belongs here.
	///
	/// WHERE IT IS CONSUMED IS NOT THE TONE-MAP SHADER, and this note said otherwise for a while.
	/// <c>822826b8 lfs f1, 0x41AC(r11)</c> is the argument to <c>sub_821B1958</c>, which permutes
	/// the value into the <b>.w lane of pixel constant c20</b> — <c>g_GlobalAmbientAndBrightness</c>
	/// — at <c>device + 0x18CC</c>. That is the constant every SURFACE shader's tail multiplies by:
	/// <c>oC0.xyz = r0.xyz * g_GlobalAmbientAndBrightness.www</c>, in 356 of the 589 shipped blobs.
	///
	/// THE GUEST HAS TWO BRIGHTNESS MULTIPLIERS AND WE HAVE ONE. Read out of the shipped microcode
	/// rather than argued from the CPU side, because neither is statically resolvable from the
	/// executable — the register behind binding id 1 lives in a runtime-populated table:
	///
	///     0499_pshader_postprocessingmaterial          -> g_GlobalGammaAdjustment only
	///     0500_pshader_postprocessingmaterial_tonemap1 -> g_GlobalGammaAdjustment
	///                                                     g_SaturationBrightnessBaseAndOffset
	///
	/// NEITHER post-process blob references <c>g_ActualFramebufferBrightness</c> or
	/// <c>g_GlobalAmbientAndBrightness</c>. So this scalar is applied in the SURFACE shaders and
	/// nowhere else, while the tone-map's own multiply is a different constant:
	///
	///     0500:78   ps = g_SaturationBrightnessBaseAndOffset.y * LUT(luminance * 0.25);
	///
	/// We apply this scalar in <see cref="Engine_ExposureEffect"/> — the tone-map — which is where
	/// the guest applies the OTHER one, and we apply nothing on surfaces. So the placement is wrong
	/// AND <c>g_SaturationBrightnessBaseAndOffset.y</c> is missing from the pipeline entirely. Its
	/// value has not been recovered; it is a post-object field, and until it is read this is a
	/// known gap rather than a number to guess.
	///
	/// The placement is not merely cosmetic, because the curve is non-linear: the guest's LUT is
	/// addressed with an already-brightened luminance and its entries carry <c>key/prevExposure</c>,
	/// so the brightness and the divide cancel inside the lookup. Our fold performs the same
	/// cancellation outside it. Whether the two are equal to the last stop has NOT been checked.
	/// </remarks>
	public float Brightness => Exposure;

	/// <summary>
	/// The complete multiplier a pixel of this luminance receives — brightness times gain.
	/// </summary>
	/// <remarks>
	/// The end-to-end number, and the one worth asserting on. Checking the loop's internals
	/// separately proved every bound and still missed a missing multiply, because each part was
	/// right and the product was not.
	/// </remarks>
	public float NetGain(float luminance) => Brightness * Gain(luminance);

	/// <summary>Rec.709 luminance, the weights the tone-map blob dots with.</summary>
	public static float Luminance(float r, float g, float b) =>
		(0.2125f * r) + (0.7154f * g) + (0.0721f * b);
}
