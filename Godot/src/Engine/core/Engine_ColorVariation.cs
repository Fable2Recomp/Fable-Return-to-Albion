using System;
using Godot;

namespace Refii.Core;

/// <summary>
/// Pure, zero-allocation scene-referred linear HDR hue-shift and subtle intensity variation
/// helpers for host-side presentation effects. Never clamps upper radiance to 1.0; only guards
/// against negative radiance (<c>max(0, c)</c>).
/// </summary>
public static class Engine_ColorVariation
{
	/// <summary>Default subtle hue rotation half-range (in degrees) for glowing FX particles.</summary>
	public const float DefaultFxMaxHueDegrees = 9f;

	/// <summary>Default warm/cool chroma tilt half-range for near-achromatic glowing FX particles.</summary>
	public const float DefaultFxMaxNeutralChroma = 0.07f;

	/// <summary>Default subtle per-particle HDR intensity variation half-range.</summary>
	public const float DefaultFxMaxIntensityVariation = 0.14f;

	/// <summary>
	/// Rotates the hue of an unclamped linear RGB colour by <paramref name="degrees"/> around
	/// the achromatic axis <c>(1, 1, 1) / sqrt(3)</c> using Rodrigues' rotation formula.
	/// Exact identity for achromatic inputs <c>(v, v, v)</c> and invariant under positive HDR scaling.
	/// </summary>
	public static void ShiftHueLinear(
		float r,
		float g,
		float b,
		float degrees,
		out float outR,
		out float outG,
		out float outB)
	{
		if (!float.IsFinite(degrees) || MathF.Abs(degrees) <= 1e-5f)
		{
			outR = MathF.Max(0f, r);
			outG = MathF.Max(0f, g);
			outB = MathF.Max(0f, b);
			return;
		}

		float angle = degrees * (MathF.PI / 180f);
		float cosA = MathF.Cos(angle);
		float sinA = MathF.Sin(angle);
		float third = (1f - cosA) * (1f / 3f);
		float sqrtThirdSin = 0.57735027f * sinA;
		float diag = cosA + third;
		float plus = third + sqrtThirdSin;
		float minus = third - sqrtThirdSin;

		outR = MathF.Max(0f, (r * diag) + (g * minus) + (b * plus));
		outG = MathF.Max(0f, (r * plus) + (g * diag) + (b * minus));
		outB = MathF.Max(0f, (r * minus) + (g * plus) + (b * diag));
	}

	/// <summary>
	/// Rotates the hue of an unclamped linear HDR <see cref="Color"/> by <paramref name="degrees"/>,
	/// preserving <see cref="Color.A"/>.
	/// </summary>
	public static Color ShiftHueLinear(Color linear, float degrees)
	{
		ShiftHueLinear(linear.R, linear.G, linear.B, degrees, out float r, out float g, out float b);
		return new Color(r, g, b, linear.A);
	}

	/// <summary>
	/// Applies a subtle hue rotation to chromatic linear colours and a subtle warm/cool chroma
	/// tilt to near-achromatic colours from a signed variation sample <paramref name="signedUnit"/>
	/// in <c>[-1, 1]</c>, blending smoothly across low saturation so white-vertex emitters (which
	/// receive colour from a texture) also gain gentle organic warmth/coolness variation.
	/// </summary>
	public static void ApplySubtleChromaShift(
		float r,
		float g,
		float b,
		float signedUnit,
		float maxHueDegrees,
		float maxNeutralChroma,
		out float outR,
		out float outG,
		out float outB)
	{
		float u = float.IsFinite(signedUnit) ? Math.Clamp(signedUnit, -1f, 1f) : 0f;
		if (MathF.Abs(u) <= 1e-5f || (maxHueDegrees <= 0f && maxNeutralChroma <= 0f))
		{
			outR = MathF.Max(0f, r);
			outG = MathF.Max(0f, g);
			outB = MathF.Max(0f, b);
			return;
		}

		float maxC = MathF.Max(r, MathF.Max(g, b));
		float minC = MathF.Min(r, MathF.Min(g, b));
		float sat = maxC > 1e-4f ? Math.Clamp((maxC - minC) / maxC, 0f, 1f) : 0f;
		float chromaWeight = Math.Clamp((sat - 0.05f) * 5f, 0f, 1f);

		float hr = r;
		float hg = g;
		float hb = b;
		if (maxHueDegrees > 0f && chromaWeight > 0f)
		{
			ShiftHueLinear(r, g, b, u * maxHueDegrees * chromaWeight, out hr, out hg, out hb);
		}
		else
		{
			hr = MathF.Max(0f, hr);
			hg = MathF.Max(0f, hg);
			hb = MathF.Max(0f, hb);
		}

		float neutralWeight = 1f - chromaWeight;
		if (maxNeutralChroma > 0f && neutralWeight > 0f)
		{
			float tilt = u * maxNeutralChroma * neutralWeight;
			outR = MathF.Max(0f, hr * (1f + tilt));
			outG = MathF.Max(0f, hg * (1f + (tilt * 0.15f)));
			outB = MathF.Max(0f, hb * (1f - tilt));
			return;
		}

		outR = hr;
		outG = hg;
		outB = hb;
	}

	/// <summary>
	/// Applies <see cref="ApplySubtleChromaShift(float, float, float, float, float, float, out float, out float, out float)"/>
	/// to an unclamped linear HDR <see cref="Color"/>, preserving <see cref="Color.A"/>.
	/// </summary>
	public static Color ApplySubtleChromaShift(
		Color linear,
		float signedUnit,
		float maxHueDegrees = DefaultFxMaxHueDegrees,
		float maxNeutralChroma = DefaultFxMaxNeutralChroma)
	{
		ApplySubtleChromaShift(
			linear.R, linear.G, linear.B, signedUnit, maxHueDegrees, maxNeutralChroma,
			out float r, out float g, out float b);
		return new Color(r, g, b, linear.A);
	}

	/// <summary>
	/// Computes a subtle multiplicative intensity scale in <c>[1 - maxVariation, 1 + maxVariation]</c>
	/// from a signed variation sample <paramref name="signedUnit"/> in <c>[-1, 1]</c>.
	/// </summary>
	public static float ComputeIntensityScale(
		float signedUnit,
		float maxVariation = DefaultFxMaxIntensityVariation)
	{
		if (!float.IsFinite(signedUnit) || !float.IsFinite(maxVariation) || maxVariation <= 0f)
		{
			return 1f;
		}

		return MathF.Max(0f, 1f + (Math.Clamp(signedUnit, -1f, 1f) * maxVariation));
	}
}
