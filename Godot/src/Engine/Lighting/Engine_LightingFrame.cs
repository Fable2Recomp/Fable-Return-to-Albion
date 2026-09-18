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

	/// <summary>The strongest painted layer's weight; 0 outside any painted cell.</summary>
	public float PaintedWeight { get; set; }

	/// <summary>
	/// How much the sky may say about the ambient here: 1 under an open sky, 0 where the theme
	/// states no main light (a cave) or a painted day set holds. Scales the bake's sky tether.
	/// </summary>
	public float SkylightWeight { get; set; } = 1.0f;

	/// <summary>
	/// Whether this theme states a main light at all. Separate from <see cref="SkylightWeight"/>,
	/// which also folds in the PAINTED weight — a painted day set is a mood the level authors
	/// over OUTDOOR cells, not a roof.
	/// </summary>
	public bool HasSky { get; set; } = true;

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
	/// The interior lift, applied to a normalised ambient colour IN PLACE and returning the
	/// lifted energy. The added light is pulled toward neutral by `interior_ambient_neutral`:
	/// the cave themes state a saturated orange, and multiplying that by the lift paints every
	/// surface with it (the guild hall came out monochrome orange at 24x), where the 360's
	/// stone reads warm-neutral. 0 keeps the theme's chroma, 1 lifts with pure white.
	/// </summary>
	private static float LiftInteriorAmbient(float[] colour, float energy)
	{
		float neutral = InteriorAmbientNeutral;
		float lum = (0.2125f * colour[0]) + (0.7154f * colour[1]) + (0.0721f * colour[2]);
		for (int i = 0; i < 3; i++)
		{
			colour[i] += (lum - colour[i]) * neutral;
		}

		return energy * InteriorAmbientScale;
	}

	/// <summary>
	/// The interior cvars are read LIVE — every lighting re-evaluate re-reads the config — so a
	/// cave can be tuned in the running game by editing refii.toml, without a rebuild. The env
	/// form (`REFII_INTERIOR_*`) wins when set, for instrument runs.
	/// </summary>
	private static readonly System.Collections.Generic.Dictionary<string, (double At, float Value)> _cvarCache = new();

	private static float InteriorCvar(string name, string env, float fallback, float min, float max)
	{
		// One config read per second at most: the post-effect asks every frame. Two threads
		// come through here — the main thread via ConfigureEnvironment and the render thread
		// via the post-effect's DarkLift — so the cache is read and written under its lock.
		double now = Time.GetTicksMsec() / 1000.0;
		lock (_cvarCache)
		{
			if (_cvarCache.TryGetValue(name, out (double At, float Value) hit) && now - hit.At < 1.0)
			{
				return hit.Value;
			}
		}

		float value = fallback;
		try
		{
			value = Refii.Core.Engine_RefiiConfig.Float(
				Refii.Core.Engine_RefiiConfig.Read(out _), name, fallback);
		}
		catch (Exception)
		{
		}

		if (float.TryParse(Godot.OS.GetEnvironment(env),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float forced) &&
			forced >= 0f)
		{
			value = forced;
		}

		float clamped = Math.Clamp(value, min, max);
		lock (_cvarCache)
		{
			_cvarCache[name] = (now, clamped);
		}

		return clamped;
	}

	/// <summary>
	/// Whether the level in view has no sky, as the lighting frame last published it. The
	/// post-effect reads it to pick the interior dark lift over the outdoor one.
	/// </summary>
	public static bool CurrentInterior { get; set; }

	/// <summary>
	/// THE INTERIOR DARK LIFT (`interior_dark_lift`): the tone floor divisor in levels with no
	/// sky. The guest clamps the histogram average to 0.15 raw before forming the curve's key,
	/// so a room whose raw mean sits far under that is rendered dark by design; a lift of N lets
	/// the floor fall to 0.15/N for such a frame. Outdoors keeps `modern_dark_lift`.
	/// </summary>
	public static float InteriorDarkLift =>
		InteriorCvar("interior_dark_lift", "REFII_INTERIOR_DARK_LIFT", 16f, 1f, 16f);

	/// <summary>How far the interior ambient lift is pulled toward neutral (0 = theme chroma).</summary>
	public static float InteriorAmbientNeutral =>
		InteriorCvar("interior_ambient_neutral", "REFII_INTERIOR_NEUTRAL", 0f, 0f, 1f);

	/// <summary>The interior BAKE lift, published as the global `fable_interior_bake`.</summary>
	public static float InteriorBakeScale =>
		InteriorCvar("interior_bake_scale", "REFII_INTERIOR_BAKE", 1f, 0.1f, 16f);

	/// <summary>
	/// The interior FOG scale on the guest's blend factor. The cave records state both fog
	/// distances as zero, which the guest's LUT bake floors to 1 and 10 m — 86% fog at 50 m
	/// toward a near-black brown — and the 360 reference shows no such wall.
	/// </summary>
	public static float InteriorFogScale =>
		InteriorCvar("interior_fog_scale", "REFII_INTERIOR_FOG", 0.2f, 0f, 1f);

	/// <summary>
	/// THE REMASTER'S INTERIOR AMBIENT (`interior_ambient_scale`; 1.0 = shipped), on the theme's
	/// flat ambient in levels with no sky, modern model only. Measured 2026-09-06 in thagscave:
	/// the raw scene averaged 0.006 linear with the exposure pinned at the guest's cap of 16, and
	/// every guest term was verified equal to ours — c20.xyz is the theme AmbientColour verbatim
	/// (`sub_821DF670` -> kind-11 light +0xD0), c20.w the adapted exposure, material colour and
	/// ambient scale literal 1, bake decode 2.2 x 1.3 — so this lift is a remaster choice against
	/// the 360 reference, not a recovered constant.
	/// </summary>
	public static float InteriorAmbientScale =>
		InteriorCvar("interior_ambient_scale", "REFII_INTERIOR_AMBIENT", 1f, 0.1f, 64f);

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

		// WHETHER THERE IS A SKY AT ALL. The threshold is OURS: `sub_821D1CA0` at 0x821D1F30 runs
		// |colour|^2 > 1e-4 (`flt_82099A80` = 0x38D1B717) on the LIVE light object to decide
		// shadow-caster admission (+0x150/+0x151/+0x152, winner stored to `dword_83496E98`); the
		// guest never asks whether a level has a sky. Here it stands in for "the theme states no
		// main light", and nothing more.
		//
		// Thag's cave states
		// (1.0, 0.58, 0.0) x 0.00 — a hue with no magnitude — and deriving an ambient from its
		// dome, then tethering every probe 60% toward it, painted the whole cave orange (b175).
		// A level with no main light has no skylight to model; its ambient is what it states,
		// or its probes.
		float mainLightSq = (sunRgb[0] * sunRgb[0]) + (sunRgb[1] * sunRgb[1]) + (sunRgb[2] * sunRgb[2]);
		bool hasSky = mainLightSq > 1e-4f;
		result.HasSky = hasSky;

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

		// THE GATE IS THE RIG, NOT THE DISC — and they are not the same body.
		//
		// `result.SunDirection` was overwritten with the VISIBLE DISC above (modern mode only),
		// so reading it here silently turned the horizon test into a disc test. The disc sets;
		// the guest's light does not. `MainLightElevation` is four authored keyframes and EVERY
		// ONE OF THE 53 RECORDS in `environmentthemes.gdb` that states them is positive — the
		// range across the whole game is 0 to 90 degrees, the constructor's own defaults being
		// 50/20/70/20. The rig is up all night, at 20-70 degrees.
		//
		// So handing the night to the moon axis put a dusk-warm light raking the terrain at
		// 3-14 degrees where the game states a high, even fill. That is the warm pool on the
		// ground: only slopes facing that low light catch it, and no cascade holds a grazing
		// light, so it reads as the sun shining through the floor. The theme states no moon
		// COLOUR at all — only disc parameters — which is the tell that the moon was never
		// meant to drive this light.
		float discUp = result.SunDirection[1];
		float rigUp = frame.MainLightDirection[1];
		if (discUp > 0f)
		{
			result.SunEnergy = sunEnergy * MathF.Min(discUp / HorizonFade, 1f);
		}
		else
		{
			Array.Copy(frame.MainLightDirection, result.LightDirection, 3);
			result.SunEnergy = rigUp > 0f
				? sunEnergy * MathF.Min(rigUp / HorizonFade, 1f)
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

		if (ModernModel && hasSky)
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

			// A BLACK SKY IS NO SKY. Thag's cave states a main light (so the test above passes)
			// and sky colours of exactly zero: deriving its ambient from that dome gave it none
			// at all, and the Environment then kept whatever the previous region had left
			// there — the cemetery's sunset, which is what painted the cave orange (b175).
			// Where the dome has no energy the theme's own ambient stands, as in fable mode.
			if (!result.HasAmbient)
			{
				hasSky = false;
				if (lighting.HasAmbientColour)
				{
					Split(lighting.AmbientColour, result.AmbientColour, out float authoredEnergy);
					result.AmbientEnergy = LiftInteriorAmbient(result.AmbientColour, authoredEnergy);
					result.HasAmbient = result.AmbientEnergy > 0f;
				}
			}

			// A PAINTED DAY SET DESCRIBES WHERE THE HERO STANDS, and the sky does not: a tavern
			// or a cave is painted precisely because its ambient is not the sky's. Its authored
			// ambient takes over by the painted weight, so the fade the guest runs is the fade
			// the room lights up with.
			if (lighting.PaintedWeight > 0f && lighting.HasAmbientColour)
			{
				var authored = new float[3];
				Split(lighting.AmbientColour, authored, out float authoredEnergy);
				float w = MathF.Min(lighting.PaintedWeight, 1f);
				for (int i = 0; i < 3; i++)
				{
					result.AmbientColour[i] += (authored[i] - result.AmbientColour[i]) * w;
				}

				result.AmbientEnergy += (authoredEnergy - result.AmbientEnergy) * w;
				result.HasAmbient = result.AmbientEnergy > 0f;
			}
		}
		else if (lighting.HasAmbientColour)
		{
			Split(lighting.AmbientColour, result.AmbientColour, out float ambientEnergy);
			// No sky in the modern model: the flat ambient is the room's light and takes the
			// interior lift; the fable model keeps the theme's value.
			result.AmbientEnergy = ModernModel
				? LiftInteriorAmbient(result.AmbientColour, ambientEnergy)
				: ambientEnergy;
			result.HasAmbient = result.AmbientEnergy > 0f;
		}

		Array.Copy(lighting.LightmapColour, result.LightmapColour, 3);
		Array.Copy(lighting.DarkeningColour, result.DarkeningColour, 3);

		result.PaintedWeight = lighting.PaintedWeight;
		result.SkylightWeight = hasSky ? 1f - Math.Clamp(lighting.PaintedWeight, 0f, 1f) : 0f;
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
