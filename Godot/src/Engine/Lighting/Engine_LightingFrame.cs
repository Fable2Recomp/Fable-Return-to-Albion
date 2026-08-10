using System;
using Refii.Level;

namespace Refii.Lighting;

public sealed class LightingFrame
{
	public float TimeOfDay { get; set; }

	public float[] SunDirection { get; } = new float[3];

	public float[] SunColour { get; } = { 1.0f, 1.0f, 1.0f };

	public float SunEnergy { get; set; } = 1.0f;

	public bool SunShadow { get; set; } = true;

	public bool HasAmbient { get; set; }

	public float[] AmbientColour { get; } = { 0.0f, 0.0f, 0.0f };

	public float AmbientEnergy { get; set; }

	public float[] LightmapColour { get; } = { 1.0f, 1.0f, 1.0f };

	public float[] DarkeningColour { get; } = { 1.0f, 1.0f, 1.0f };

	public bool HasFog { get; set; }

	public float[] FogColour { get; } = { 0.48f, 0.55f, 0.62f };

	public float FogBegin { get; set; }

	public float FogEnd { get; set; }

	/// <summary>
	/// How much fog the theme asks for AT <see cref="FogEnd"/>, in 0..1.
	///
	/// The guest states a near and a far pair — a distance AND a density at each — so the far
	/// density is a CEILING, not an implied 1.0. Dropping it and letting the ramp run to full
	/// occlusion is what turned a stated 40% haze into a wall that swallowed the far half of
	/// every region.
	/// </summary>
	public float FogDensity { get; set; } = 1.0f;

	/// <summary>Density at <see cref="FogBegin"/>, in 0..1. Usually zero.</summary>
	public float FogNearDensity { get; set; }
}

public static class Engine_LightingFrame
{
	public const float EnergyCeiling = 16.0f;

	public static LightingFrame Evaluate(SkyTheme sky, LightingTheme lighting, SkyFrame frame)
	{
		var result = new LightingFrame
		{
			TimeOfDay = frame.TimeOfDay,
		};

		Array.Copy(frame.SunDirection, result.SunDirection, 3);

		float[] sunRgb = lighting.HasMainLightColour ? lighting.MainLightColour : frame.MainLightColour;
		Split(sunRgb, result.SunColour, out float sunEnergy);
		result.SunEnergy = sunEnergy;
		result.SunShadow = !lighting.HasMainLightShadowed || lighting.MainLightShadowed > 0.5f;

		if (lighting.HasAmbientColour)
		{
			Split(lighting.AmbientColour, result.AmbientColour, out float ambientEnergy);
			result.AmbientEnergy = ambientEnergy;
			result.HasAmbient = ambientEnergy > 0f;
		}

		Array.Copy(lighting.LightmapColour, result.LightmapColour, 3);
		Array.Copy(lighting.DarkeningColour, result.DarkeningColour, 3);

		bool fogDistances = sky.HasNearFog && sky.HasFarFog &&
			float.IsFinite(sky.NearDistance) && float.IsFinite(sky.FarDistance) &&
			sky.FarDistance > sky.NearDistance && sky.NearDistance >= 0f;

		if (fogDistances)
		{
			result.HasFog = true;
			Array.Copy(sky.FogColour, result.FogColour, 3);
			result.FogBegin = sky.NearDistance;
			result.FogEnd = sky.FarDistance;
			result.FogNearDensity = Math.Clamp(sky.NearDensity, 0f, 1f);
			result.FogDensity = float.IsFinite(sky.FarDensity) && sky.FarDensity > 0f
				? Math.Clamp(sky.FarDensity, 0f, 1f)
				: 1.0f;
		}

		return result;
	}

	private static void Split(float[] rgb, float[] outColour, out float energy)
	{
		float r = MathF.Max(rgb[0], 0f);
		float g = MathF.Max(rgb[1], 0f);
		float b = MathF.Max(rgb[2], 0f);
		float m = MathF.Max(MathF.Max(r, g), b);

		if (m <= 1e-4f)
		{
			outColour[0] = 0f;
			outColour[1] = 0f;
			outColour[2] = 0f;
			energy = 0f;
			return;
		}

		float scale = MathF.Min(m, EnergyCeiling);
		outColour[0] = Math.Clamp(r / m, 0f, 1f);
		outColour[1] = Math.Clamp(g / m, 0f, 1f);
		outColour[2] = Math.Clamp(b / m, 0f, 1f);
		energy = scale;
	}
}
