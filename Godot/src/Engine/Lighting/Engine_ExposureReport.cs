using System;
using System.Text;

namespace Refii.Lighting;

/// <summary>
/// Turns one frame's luminance histogram and exposure state into a block a human can read.
///
/// THIS EXISTS TO SPLIT TWO FAULTS THAT LOOK IDENTICAL IN A SCREENSHOT. A region that reads wrong
/// is either being clipped by the tone curve — the top of the frame arriving at the curve already
/// above the point where it maps to 1.0, so detail is thrown away — or it is simply dark before the
/// curve ever sees it, in which case the curve is behaving and the sun term is the thing to look at.
/// Those two want OPPOSITE fixes, and neither the image nor the adapted triple on its own says
/// which is which: an underexposed frame and a clipped frame can report the same exposure scalar.
///
/// What settles it is one number the state buffer does not hold — the CLIP POINT, the scene
/// luminance at which <c>brightness * curve(L)</c> reaches 1.0 — held up against where the frame's
/// pixels actually are. If the clip point sits below the 99th percentile, the curve is throwing
/// away the top of the frame. If it sits well above it, nothing is clipping and the frame is dark
/// on arrival.
///
/// NO GODOT TYPES AND NO GPU, deliberately: the same reason <see cref="Engine_AutoExposure"/> is
/// kept free of them. Every number below can be produced from a synthetic histogram in a headless
/// check, and a measurement that can only be exercised on a GPU is one that gets verified by
/// looking at it.
///
/// EVERY LUMINANCE HERE IS A BUCKET'S LOWER EDGE, not its midpoint, because that is what the loop
/// itself uses — <c>fable_exposure_reduce</c> weights bucket <c>i</c> by <c>i</c> and takes
/// <c>idx * BUCKET_W</c> for its percentiles. Reporting midpoints would put this half a bucket
/// (0.004) above the values in the state buffer beside it and read as a discrepancy.
/// </summary>
public static class Engine_ExposureReport
{
	/// <summary>Floats in the state buffer: min, avg, max, exposure, prev, scale, invW2, brightness.</summary>
	public const int StateFloats = 8;

	/// <summary>Bands the histogram is folded into for display. 16 bands of 32 buckets.</summary>
	private const int Bands = 16;

	/// <summary>
	/// The whole report, ready to print.
	/// </summary>
	/// <param name="bins">The 512 bucket counts, as the histogram pass left them.</param>
	/// <param name="state">The eight-float state buffer the reduce pass writes.</param>
	public static string Format(
		ReadOnlySpan<uint> bins,
		ReadOnlySpan<float> state,
		int width,
		int height,
		float deltaSeconds,
		long frame)
	{
		var text = new StringBuilder();

		text.Append($"[exposure] frame {frame} · {width}x{height} · dt={deltaSeconds * 1000.0f:0.0}ms")
			.Append('\n');

		if (state.Length < StateFloats)
		{
			return text.Append($"[exposure]   state buffer is {state.Length} floats, expected " +
				$"{StateFloats}").ToString();
		}

		float adaptedMin = state[0];
		float adaptedAvg = state[1];
		float adaptedMax = state[2];
		float exposure = state[3];
		float previous = state[4];
		float curveScale = state[5];
		float invWhiteSq = state[6];
		float brightness = state[7];

		// Recovered rather than stored: the reduce pass writes the derived terms, not the two
		// quantities they were derived from, and the key and the white point are what the guest's
		// own decompile talks about. Reporting only `curveScale` would mean translating by hand
		// every time this is read.
		float white = invWhiteSq > 0.0f ? 1.0f / MathF.Sqrt(invWhiteSq) : 0.0f;
		float key = curveScale * MathF.Max(previous, 1e-4f);

		text.Append($"[exposure]   adapted  min={adaptedMin:0.0000} avg={adaptedAvg:0.0000} " +
			$"max={adaptedMax:0.0000}   exposure={exposure:0.000} prev={previous:0.000}").Append('\n');
		text.Append($"[exposure]   curve    key={key:0.000} white={white:0.000} " +
			$"scale={curveScale:0.0000} brightness={brightness:0.000}").Append('\n');

		long total = 0;
		double weighted = 0.0;
		for (int i = 0; i < bins.Length; i++)
		{
			total += bins[i];
			weighted += (double)bins[i] * i;
		}

		if (total == 0)
		{
			return text.Append("[exposure]   histogram is EMPTY — the binning pass wrote nothing, " +
				"so the state above is stale and means nothing").ToString();
		}

		const float w = Engine_AutoExposure.BucketWidth;

		float mean = (float)(weighted / total) * w;
		float p1 = Percentile(bins, total, 0.01f) * w;
		float p50 = Percentile(bins, total, 0.50f) * w;
		float p99 = Percentile(bins, total, 0.99f) * w;

		// The top bucket is a CATCH-ALL, not a measurement: `fable_exposure_histogram` clamps the
		// bucket index to 511, so everything at or above 3.992 lands there together. A large count
		// means the frame runs off the end of the histogram's domain and the measured maximum is a
		// floor rather than a value.
		uint overRange = bins.Length > 511 ? bins[511] : 0u;

		text.Append($"[exposure]   scene    pixels={total:N0} mean={mean:0.0000} " +
			$"p1={p1:0.0000} p50={p50:0.0000} p99={p99:0.0000}   " +
			$"at/over 3.99: {Pct(overRange, total):0.00}%").Append('\n');

		float clip = ClipPoint(curveScale, invWhiteSq, brightness);

		float out50 = Output(p50, curveScale, invWhiteSq, brightness);
		float out99 = Output(p99, curveScale, invWhiteSq, brightness);

		long clipped = CountAtOrAbove(bins, clip);
		long near = CountAtOrAbove(bins, SolveFor(0.95f, curveScale, invWhiteSq, brightness));

		text.Append($"[exposure]   mapped   p50->{out50:0.0000} p99->{out99:0.0000}   " +
			$"clipped(>=1.0)={Pct(clipped, total):0.00}%  near-white(>=0.95)={Pct(near, total):0.00}%")
			.Append('\n');

		// THE LINE THE WHOLE FILE IS FOR.
		string clipText = float.IsPositiveInfinity(clip)
			? "never (the curve does not reach 1.0)"
			: $"L={clip:0.0000}";

		text.Append($"[exposure]   clip point {clipText}");

		if (!float.IsPositiveInfinity(clip) && p99 > 1e-6f)
		{
			float headroom = clip / p99;
			text.Append(headroom >= 1.0f
				? $" — {headroom:0.00}x ABOVE p99, so the curve is not clipping this frame"
				: $" — {1.0f / headroom:0.00}x BELOW p99, so the curve IS throwing away the top of " +
				  "this frame");
		}

		text.Append('\n');
		text.Append($"[exposure]   median maps to {out50 * 100.0f:0.0}% of full white " +
			"(a few per cent with nothing clipping means the frame is dark BEFORE the curve)")
			.Append('\n');

		Bars(text, bins, total);

		return text.ToString();
	}

	/// <summary>The histogram folded to 16 bands, so the shape is visible in a log.</summary>
	private static void Bars(StringBuilder text, ReadOnlySpan<uint> bins, long total)
	{
		int perBand = Engine_AutoExposure.Buckets / Bands;
		float widest = 0.0f;
		var share = new float[Bands];

		for (int band = 0; band < Bands; band++)
		{
			long count = 0;
			for (int i = band * perBand; i < (band + 1) * perBand && i < bins.Length; i++)
			{
				count += bins[i];
			}

			share[band] = (float)((double)count / total);
			widest = MathF.Max(widest, share[band]);
		}

		for (int band = 0; band < Bands; band++)
		{
			// Scaled to the TALLEST band, not to 100%. Almost every real frame puts nine tenths of
			// itself in the first band, and a bar chart against 100% is then one full bar and
			// fifteen empty ones — which hides exactly the tail this is being read for.
			int length = widest > 0.0f ? (int)MathF.Round(share[band] / widest * 40.0f) : 0;
			float low = band * perBand * Engine_AutoExposure.BucketWidth;

			text.Append($"[exposure]   {low,5:0.00}+ {new string('#', length),-40} " +
				$"{share[band] * 100.0f:0.00}%").Append('\n');
		}
	}

	/// <summary>The bucket holding the pixel at that fraction, by the guest's exclusive rule.</summary>
	/// <remarks>
	/// The running sum EXCLUDES the bucket being tested — <c>822aacac cmplw cr6, r10, r4</c> then
	/// <c>822aacdc add r10, r8, r10</c> — and including it shifts every threshold by one bucket.
	/// Same rule as <see cref="Engine_AutoExposure.Advance"/>, so p1 and p99 here are the very
	/// buckets the loop measured its own min and max from.
	/// </remarks>
	public static int Percentile(ReadOnlySpan<uint> bins, long total, float fraction)
	{
		double threshold = fraction * total;
		long cumulative = 0;
		int index = 0;

		for (int i = 0; i < bins.Length && i < Engine_AutoExposure.Buckets; i++)
		{
			if (cumulative < threshold)
			{
				index = i;
			}

			cumulative += bins[i];
		}

		return index;
	}

	/// <summary>The curve, exactly as <c>fable_exposure_apply</c> evaluates it.</summary>
	public static float Curve(float luminance, float curveScale, float invWhiteSq)
	{
		float x = MathF.Max(luminance, 0.0f) * curveScale;
		return ((x * invWhiteSq) + 1.0f) * x / (x + 1.0f);
	}

	/// <summary>What a scene luminance leaves the effect as, before the clamp.</summary>
	public static float Output(float luminance, float curveScale, float invWhiteSq, float brightness) =>
		Curve(luminance, curveScale, invWhiteSq) * brightness;

	/// <summary>
	/// The scene luminance that maps to 1.0 — everything at or above it is clamped to white.
	/// </summary>
	/// <remarks>
	/// Bisected rather than solved. The curve is a quadratic over a linear and inverting it is a
	/// root of a quadratic with two sign cases, and the derivation would have to be re-checked
	/// every time the curve is touched; the bisection is correct for any monotone curve and this
	/// runs once per dump.
	/// </remarks>
	public static float ClipPoint(float curveScale, float invWhiteSq, float brightness) =>
		SolveFor(1.0f, curveScale, invWhiteSq, brightness);

	/// <summary>The scene luminance mapping to <paramref name="target"/>, or +inf if none does.</summary>
	public static float SolveFor(float target, float curveScale, float invWhiteSq, float brightness)
	{
		if (curveScale <= 0.0f || brightness <= 0.0f)
		{
			return float.PositiveInfinity;
		}

		float high = 1.0f;
		while (Output(high, curveScale, invWhiteSq, brightness) < target)
		{
			high *= 2.0f;
			if (high > 1e9f)
			{
				return float.PositiveInfinity;
			}
		}

		float low = 0.0f;
		for (int i = 0; i < 64; i++)
		{
			float mid = (low + high) * 0.5f;
			if (Output(mid, curveScale, invWhiteSq, brightness) < target)
			{
				low = mid;
			}
			else
			{
				high = mid;
			}
		}

		return high;
	}

	/// <summary>Pixels binned at or above a scene luminance.</summary>
	private static long CountAtOrAbove(ReadOnlySpan<uint> bins, float luminance)
	{
		if (float.IsPositiveInfinity(luminance))
		{
			return 0;
		}

		int first = (int)MathF.Ceiling(luminance / Engine_AutoExposure.BucketWidth);
		long count = 0;

		for (int i = Math.Max(first, 0); i < bins.Length; i++)
		{
			count += bins[i];
		}

		return count;
	}

	private static float Pct(long part, long total) => total > 0 ? part * 100.0f / total : 0.0f;
}
