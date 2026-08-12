using System;
using System.Collections.Generic;
using Godot;
using Refii.Lighting;

namespace Refii.Launcher.Tools;

/// <summary>
/// Does the auto-exposure loop settle where the guest's constants say it should?
///
/// The whole point of this loop is that a constant cannot follow a scene, so a check that asserts
/// one number would be checking the wrong thing. What IS fixed is the behaviour: the average is
/// floored at 0.3 whatever the frame contains, the minimum is capped at 0.1, the white point
/// therefore cannot fall below 0.5, and the loop must converge from its 4.0 start rather than
/// oscillate.
///
/// The case that matters is the one measured in this project: a region whose mean luminance is
/// 0.026, two orders under the floor. If the floor governs, the key lands on the authored bounds
/// and NOT on the scene — and the shipped image is dark by design rather than by accident.
///
///   godot --path Godot --headless res://debug/exposure_check.tscn
/// </summary>
public partial class ExposureCheck : Node
{
	private const float Dt = 1.0f / 30.0f;

	public override void _Ready()
	{
		int failures = 0;

		failures += Scene(
			"dark region (measured mean 0.026)", BuildScene(0.026f, 0.0015f, 0.9f, 0.02f));
		failures += Scene(
			"bright sky over dark street", BuildScene(0.12f, 0.001f, 3.8f, 0.35f));
		failures += Scene("uniform mid grey", BuildScene(0.45f, 0.4f, 0.5f, 0.0f));
		failures += Scene("black frame", BuildScene(0.0f, 0.0f, 0.0f, 0.0f));

		GD.Print(
			failures == 0
				? "[exposure-check] all invariants held"
				: $"[exposure-check] {failures} INVARIANT FAILURE(S)");

		GetTree().Quit(failures == 0 ? 0 : 1);
	}

	/// <summary>
	/// A frame's worth of luminance: a bulk near <paramref name="mean"/> with a bright tail.
	/// </summary>
	private static float[] BuildScene(float mean, float floor, float peak, float brightFraction)
	{
		// Quarter width by quarter height of a 1280x720 target, which is the surface the guest
		// actually bins — `sub_82A7BD68` takes *a2/4 and a2[1]/4.
		const int width = 1280 / 4;
		const int height = 720 / 4;
		var pixels = new float[width * height];
		int bright = (int)(pixels.Length * brightFraction);

		for (int i = 0; i < pixels.Length; i++)
		{
			pixels[i] = i < bright
				? peak
				: floor + ((mean - floor) * ((i % 17) / 16.0f));
		}

		return pixels;
	}

	private static int Scene(string name, float[] pixels)
	{
		var exposure = new Engine_AutoExposure();

		// Long enough to settle: the blend closes 1 - 0.99^1 per 30 Hz tick, so a few hundred
		// ticks is several seconds of game time.
		for (int frame = 0; frame < 600; frame++)
		{
			exposure.Submit(pixels, Dt);
		}

		GD.Print(
			$"[exposure-check] {name}: min={exposure.AdaptedMin:0.0000} " +
			$"avg={exposure.AdaptedAvg:0.0000} max={exposure.AdaptedMax:0.0000} " +
			$"W={exposure.WhitePoint:0.0000} key={exposure.Key:0.0000} " +
			$"E={exposure.Exposure:0.000} scale={exposure.CurveScale:0.0000} " +
			$"net@0.1={exposure.NetGain(0.1f):0.000}");

		int failures = 0;

		// The bounds are the guest's, so these are not tuning — they are the constants.
		failures += Require(
			name, "avg is floored at AVG_LUMINANCE_LOWER_BOUND",
			exposure.AdaptedAvg >= Engine_AutoExposure.AvgLuminanceLowerBound - 1e-3f);
		failures += Require(
			name, "min is capped at MIN_LUMINANCE_UPPER_BOUND",
			exposure.AdaptedMin <= Engine_AutoExposure.MinLuminanceUpperBound + 1e-3f);
		failures += Require(
			name, "max is floored at MAX_LUMINANCE_LOWER_BOUND",
			exposure.AdaptedMax >= Engine_AutoExposure.MaxLuminanceLowerBound - 1e-3f);

		// The two divisors the curve cannot survive losing.
		failures += Require(name, "white point stays positive", exposure.WhitePoint > 0.0f);
		failures += Require(
			name, "key denominator stays positive",
			exposure.AdaptedAvg - exposure.AdaptedMin > 0.0f);

		failures += Require(
			name, "exposure stays inside [1, 16]",
			exposure.Exposure >= Engine_AutoExposure.ExposureMin - 1e-3f &&
			exposure.Exposure <= Engine_AutoExposure.ExposureMax + 1e-3f);

		// THE WHOLE REASON FOR THE REWRITE. Our old fixed white point was 4.0, which is the
		// histogram's DOMAIN BOUND rather than a white point — a sky authored at SunIntensity 4
		// landed exactly on it and clipped to pure white. A measured white point has to come out
		// far below that.
		failures += Require(
			name, "white point is well under the old fixed 4.0", exposure.WhitePoint < 4.0f);

		// THE END-TO-END MULTIPLIER, WHICH IS THE ONE THAT WAS WRONG. Every bound above held while
		// the shipped image came out sixteen times too dark, because the brightness multiply was
		// missing: each part was right and the product was not. A dark scene drives the exposure
		// scalar to its ceiling, the curve divides by it, and only the `brightness` term puts it
		// back — so the net lift on an ordinary pixel must land near the key, never below one.
		float net = exposure.NetGain(0.1f);
		failures += Require(
			name, $"net gain on a mid pixel is near the key (net={net:0.000} key={exposure.Key:0.000})",
			net > exposure.Key * 0.5f && net < exposure.Key * 1.5f);
		// NOT "the net gain never darkens" — that assertion was written here first and was simply
		// wrong. A bright frame is SUPPOSED to be pulled down: the loop drops the exposure to its
		// floor and the key falls with it, so the bright-sky case lands at 0.26 and belongs there.
		// The invariant is that the net multiplier tracks the KEY, whichever side of one that is.

		// The curve must still compress rather than clip: a luminance at the white point should
		// map to about 1.0, not past it.
		float atWhite = exposure.MapLuminance(exposure.WhitePoint / exposure.CurveScale);
		failures += Require(
			name, $"white point maps to ~1.0 (got {atWhite:0.000})",
			atWhite > 0.5f && atWhite < 1.5f);

		return failures;
	}

	private static int Require(string scene, string what, bool held)
	{
		if (held)
		{
			return 0;
		}

		GD.PrintErr($"[exposure-check] {scene}: FAILED — {what}");
		return 1;
	}
}
