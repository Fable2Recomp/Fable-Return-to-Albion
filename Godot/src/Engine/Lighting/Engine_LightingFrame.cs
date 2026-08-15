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

	// NO FOG HERE. It is not a lighting-frame value: the theme's Fogging record drives the
	// atmosphere, which reaches the world through global shader parameters rather than through
	// Godot's Environment. See `Engine_SkyAtmosphere.SolveFogCurve`.
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

		// THE RIG, NOT THE DISC. `sub_821DF670` sends the main light's direction to the same
		// object as MainLightColour and MainLightShadowed (`lwz r3, 8(r31)` three times), while
		// the sun and moon axes go to the sky object at `*(a1+0x10)`. Taking SkyFrame.SunDirection
		// here — which is the disc — aimed the shadows at the wrong point of the sky by
		// SunAxisXYRotationOffset, and gave them the orbit's height instead of the authored
		// elevation arc.
		Array.Copy(frame.MainLightDirection, result.SunDirection, 3);

		float[] sunRgb = lighting.HasMainLightColour ? lighting.MainLightColour : frame.MainLightColour;
		Split(sunRgb, result.SunColour, out float sunEnergy);
		result.SunEnergy = sunEnergy;
		// STRICTLY GREATER THAN 0.5, and NOT true-when-unstated. The guest turns the theme's float
		// into a bool with `fcmpu cr6, f3, f28` against 0.5 followed by `bgt` (0x821DFC34), and it
		// never defaults the slot — so an unstated field is the zero the theme was constructed
		// with, and zero means no shadow. See the remark on LightingTheme.MainLightShadowed.
		result.SunShadow = lighting.MainLightShadowed > 0.5f;

		if (lighting.HasAmbientColour)
		{
			Split(lighting.AmbientColour, result.AmbientColour, out float ambientEnergy);
			result.AmbientEnergy = ambientEnergy;
			result.HasAmbient = ambientEnergy > 0f;
		}

		Array.Copy(lighting.LightmapColour, result.LightmapColour, 3);
		Array.Copy(lighting.DarkeningColour, result.DarkeningColour, 3);

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
