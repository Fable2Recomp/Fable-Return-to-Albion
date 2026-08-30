using System;
using Godot;
using Refii.Level;

namespace Refii.Lighting;

public sealed class LightingFrame
{
	public float TimeOfDay { get; set; }

	public float[] SunDirection { get; } = new float[3];

	/// <summary>
	/// What the scene's directional light is aimed along — the sun while it is up, the moon
	/// once it is not. Separate from <see cref="SunDirection"/>, which stays the SUN: the
	/// post-FX places the sun disc and the ray centre from that, and must not follow the moon.
	/// </summary>
	public float[] LightDirection { get; } = new float[3];

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

	/// <summary>Sine of the elevation over which a body fades in at the horizon (~4.6 degrees).</summary>
	private const float HorizonFade = 0.08f;

	/// <summary>
	/// Sky-to-ambient conversion for the modern model, calibrated against the authored data
	/// (ThemeSweep calibration columns, chapter2posh). Two constraints
	/// fix it: at NIGHT the modern ambient must land on the guest's authored night ambient —
	/// at 0.2, (0.05, 0.10, 0.33) against the authored (0.065, 0.075, 0.35) — and by DAY every
	/// channel of the ambient half must sit at or below the sun's so shadows read; the worst
	/// case (blue at 09h, blue-heavy bake under a blue sky) reaches parity at 0.2 and inverts
	/// above it. 0.3 was the pre-measurement guess.
	/// </summary>
	public const float ModernAmbientLevel = 0.2f;

	/// <summary>
	/// How far every exterior bake (probes, vertex bakes, the terrain lightmap) is pulled
	/// toward the live sky ambient in the modern model (`fable_sky_ambient.w`). Bakes keep
	/// 1-this of their authored locality while the sky sets the level and the tint. 0.6 is
	/// measured: at midday in chapter2posh the theme's LightmapColour factor is (4.6, 5.1, 7.1),
	/// and below 0.6 the terrain's ambient blue sits above the sun's, so no shadow can show in
	/// that channel.
	/// </summary>
	public const float ModernProbeSkyBlend = 0.6f;

	private static bool? _modernAmbient;

	/// <summary>
	/// The `lighting_model` switch: "modern" derives ambient from the live sky dome (the
	/// remaster default); "fable" reads the theme's authored day-set ambient exactly. Public
	/// because the model is one decision, not one file's — the light fade reach follows it too.
	/// </summary>
	public static bool ModernModel
	{
		get
		{
			if (_modernAmbient is null)
			{
				try
				{
					_modernAmbient = !Refii.Core.Engine_RefiiConfig.Read(out _)
						.TryGetValue("lighting_model", out string? model) ||
						!model.Trim().Trim('"').Equals(
							"fable", StringComparison.OrdinalIgnoreCase);
				}
				catch (Exception)
				{
					_modernAmbient = true;
				}
			}

			return _modernAmbient.Value;
		}
	}

	private static bool? _sunShadows;

	/// <summary>The launcher's `sun_shadows` setting; on is the remastered default.</summary>
	private static bool SunShadowsModernized
	{
		get
		{
			if (_sunShadows is null)
			{
				try
				{
					_sunShadows = Refii.Core.Engine_RefiiConfig.Bool(
						Refii.Core.Engine_RefiiConfig.Read(out _), "sun_shadows", true);
				}
				catch (Exception)
				{
					_sunShadows = true;
				}
			}

			return _sunShadows.Value;
		}
	}

	public static LightingFrame Evaluate(SkyTheme sky, LightingTheme lighting, SkyFrame frame)
	{
		var result = new LightingFrame
		{
			TimeOfDay = frame.TimeOfDay,
		};

		// THE RIG, NOT THE DISC — in FABLE mode. `sub_821DF670` sends the main light's direction
		// to the same object as MainLightColour and MainLightShadowed (`lwz r3, 8(r31)` three
		// times), while the sun and moon axes go to the sky object at `*(a1+0x10)`. The two are
		// deliberately different in the shipped data: across the themes they disagree on 209 of
		// 348 samples, worst case 156.5 degrees — invisible on the 360, whose shadowing was
		// baked, so nothing ever revealed where the rig pointed.
		Array.Copy(frame.MainLightDirection, result.SunDirection, 3);

		// THE MODERN MODEL LIGHTS FROM THE VISIBLE SUN. The remaster's live shadows made the
		// authored rig/disc disagreement legible for the first time — shadows falling away from
		// the sun the player can SEE reads as a bug, whatever the data says.
		//
		// THE VISIBLE disc's world direction is `Engine_SkyFrame.ToGodot(frame.SunDirection)`
		// — proven at the consumer, not assumed: the celestial shader transforms `element_dir`
		// by VIEW_MATRIX alone (celestial.gdshader:16), so the uniform IS a world vector, and
		// the uniform is set from exactly that conversion. ToGodot only negates X ([1] stays
		// the elevation). A first version copied the frame RAW and lit every level from the
		// east/west MIRROR of its visible sun — near-right when the sun's X was small, plainly
		// wrong when it was not, which read as "levels out of sync". UpdateSun consumes this as
		// a GLOBAL transform, the same world space. The rig's authored colour, energy and
		// shadow gate are kept; only the AIM follows the disc.
		if (ModernModel)
		{
			Vector3 disc = Refii.Level.Engine_SkyFrame.ToGodot(frame.SunDirection);
			if (disc.LengthSquared() > 1e-8f)
			{
				disc = disc.Normalized();
				result.SunDirection[0] = disc.X;
				result.SunDirection[1] = disc.Y;
				result.SunDirection[2] = disc.Z;
			}
		}

		float[] sunRgb = lighting.HasMainLightColour ? lighting.MainLightColour : frame.MainLightColour;
		Split(sunRgb, result.SunColour, out float sunEnergy);

		// THE HORIZON GATE, AND THE HANDOVER TO THE MOON.
		//
		// OURS, NOT THE GUEST'S. `sub_821DF670` publishes the rig direction and its negation and
		// a `> 0.5` boolean, but which of those the light object consumes after dark is not
		// established — see the note in Engine_SkyFrame. What IS certain is the requirement: a
		// directional light left on a set sun rakes the terrain from below and lights the
		// undersides of the world. So the sun's contribution is faded out across the last few
		// degrees and is exactly zero once its elevation is negative.
		//
		// Component [1] is the elevation term — the orbit is built as
		// (cos(E)*axisX, sin(E), cos(E)*axisY) — so [1] > 0 is "above the horizon".
		Array.Copy(result.SunDirection, result.LightDirection, 3);
		float sunUp = result.SunDirection[1];
		float moonUp = frame.MoonDirection[1];
		if (sunUp > 0f)
		{
			result.SunEnergy = sunEnergy * MathF.Min(sunUp / HorizonFade, 1f);
		}
		else
		{
			// The moon carries the night on its own orbit and its own intensity. When it is down
			// too, nothing directional remains and the scene is ambient alone, which is what a
			// moonless night is.
			Array.Copy(frame.MoonDirection, result.LightDirection, 3);
			float moonScale = sky.HasMoonParams ? sky.MoonIntensity : 1.0f;
			result.SunEnergy = moonUp > 0f
				? sunEnergy * moonScale * MathF.Min(moonUp / HorizonFade, 1f)
				: 0f;
		}

		// THE THEME FIELD IS THE GUEST'S SHADOW GATE, AND 0 REALLY MEANS NO SUN SHADOW.
		//
		// `MainLightShadowed` is theme field 1, so it sits at themeBlock + 0x10 + 1*0x10.
		// `sub_821DF670` reads it there (`lvx128 v18, r26, 0x20`), thresholds it at 0.5
		// (`fcmpu cr6, f3, f28` / `bgt`, 0x821DFC34) and publishes it as command id 30, whose
		// handler stores the byte at light+336 (`822d88a0 stb r11, 0x150(r31)`). The shadow-caster
		// selection in `sub_821D1CA0` then requires light+336 alongside a non-black colour, so a
		// theme that states 0 casts nothing and its shadowing lives in the baked lightmaps.
		//
		// The colour test is the guest's too: |MainLightColour|^2 > 1e-4 (`vmsum3fp128` at
		// 0x821D1F30). Modernised, we keep that test and drop the authored flag.
		result.SunShadow = sunEnergy > 0.01f
			&& (SunShadowsModernized || (lighting.HasMainLightShadowed
				&& lighting.MainLightShadowed > 0.5f));

		if (ModernModel)
		{
			// THE MODERN MODEL'S AMBIENT: derived live from the sky dome rather than read from
			// the theme's baked-era table. The theme ambient is one authored value per day-set
			// entry — a fossil of the bake — while this tracks whatever the visible sky is
			// doing, so a rock under a golden sunset goes golden with it and every surface
			// (probe-lit, baked or bare) breathes with one source. Cosine-weighted toward the
			// zenith because that is where a horizontal world mostly looks.
			var skyAmbient = new float[3];
			for (int i = 0; i < 3; i++)
			{
				skyAmbient[i] = (frame.SkyTop[i] * 0.65f) + (frame.SkyBottom[i] * 0.35f);
			}

			Split(skyAmbient, result.AmbientColour, out float skyEnergy);
			result.AmbientEnergy = skyEnergy * ModernAmbientLevel;
			result.HasAmbient = result.AmbientEnergy > 0f;
		}
		else if (lighting.HasAmbientColour)
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
