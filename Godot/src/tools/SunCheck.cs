using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Level;

namespace Refii.Launcher.Tools;

/// <summary>
/// Do the sun DISC and the main LIGHT actually point in different directions?
///
/// `sub_821DF670` builds two vectors from one orbit and sends them to different objects — the
/// sun/moon pair to the sky, the light's direction to the same object as MainLightColour and
/// MainLightShadowed. We used to compute only the first and use it for both. Splitting them is
/// only worth anything if the shipped themes actually state the fields that make them differ, so
/// this prints both vectors and the angle between them across a day.
///
/// A run where the separation is 0.0 degrees at every hour means the split changed nothing and the
/// themes leave SunAxisXYRotationOffset and SunAxisZOffset at zero. A run with a real separation
/// is the evidence that the old single vector was aiming something wrongly.
///
/// Nothing is written and nothing is drawn.
///
///   godot --path Godot --headless res://debug/sun_check.tscn
///
/// <c>REFII_SUN_LEVEL</c> picks the level by substring; without it every level is walked.
/// </summary>
public partial class SunCheck : Node
{
	private static readonly float[] Hours = { 0f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f };

	public override void _Ready()
	{
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.PrintErr("[sun-check] no game data; set REFII_GAME_DATA.");
			GetTree().Quit(1);
			return;
		}

		using BnkFileSystem fs = BnkFileSystem.Mount(dataRoot);

		byte[]? Read(string path) =>
			fs.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null;

		string want = OS.GetEnvironment("REFII_SUN_LEVEL");
		List<LevelEntry> levels = Engine_LevelIndex.Discover(fs);
		if (want.Length != 0)
		{
			levels = levels
				.Where(l => l.Name.Contains(want, StringComparison.OrdinalIgnoreCase))
				.ToList();
		}

		GD.Print($"[sun-check] data='{dataRoot}' levels={levels.Count}");

		int compared = 0;
		int differing = 0;
		float worst = 0f;
		string worstAt = string.Empty;
		var seen = new HashSet<string>();

		foreach (LevelEntry level in levels)
		{
			if (!seen.Add(level.Name))
			{
				continue;
			}

			List<byte[]> gdbs = Engine_SkyEnvironment.BuildGdbChain(level, Read);
			GD.Print($"[sun-check] {level.Name}: gdb chain {gdbs.Count}");
			if (gdbs.Count == 0)
			{
				continue;
			}

			bool printed = false;

			foreach (float t in Hours)
			{
				if (!Engine_ThemeTimeline.TryExtractSkyBlended(gdbs, t, out SkyTheme sky))
				{
					GD.Print($"[sun-check] {level.Name} t={t}: no sky theme");
					continue;
				}

				SkyFrame frame = Engine_SkyFrame.Evaluate(sky, t);

				var disc = new Vector3(
					frame.SunDirection[0], frame.SunDirection[1], frame.SunDirection[2]);
				var lightDir = new Vector3(
					frame.MainLightDirection[0],
					frame.MainLightDirection[1],
					frame.MainLightDirection[2]);

				if (disc.LengthSquared() < 1e-6f || lightDir.LengthSquared() < 1e-6f)
				{
					continue;
				}

				if (t == 0.5f)
				{
					AtmosphereState at = Engine_SkyAtmosphere.Compute(
						Engine_SkyAtmosphere.FromFrame(frame, sky));
					float bR = at.BetaRayleigh[0] + at.BetaMie[0];
					float bG = at.BetaRayleigh[1] + at.BetaMie[1];
					float bB = at.BetaRayleigh[2] + at.BetaMie[2];
					var lad = new System.Text.StringBuilder();
					foreach (float m in new[] { 5f, 10f, 25f, 50f, 100f, 200f, 400f })
					{
						float past = m - at.ScatteringMisc[0];
						if (past <= 0f) { lad.Append($" {m:0}=0%"); continue; }
						float tt = MathF.Min(past, Engine_SkyAtmosphere.MaxFogDistance) * at.FogRecipFarRange;
						float depth = at.FogOpticalDepthAtFar * MathF.Pow(tt, at.FogExponent);
						float fogG = Engine_SkyAtmosphere.FogBlendFactor * (1f - MathF.Exp(-bG * depth));
						lad.Append($" {m:0}={fogG * 100f:0}%");
					}

					GD.Print($"[fog] {level.Name} start={at.ScatteringMisc[0]:0.#} " +
						$"depth={at.FogOpticalDepthAtFar:0.##} exp={at.FogExponent:0.##} " +
						$"blend={Engine_SkyAtmosphere.FogBlendFactor:0.##} " +
						$"colourFactor={Engine_SkyAtmosphere.FogColourFactor:0.##} " +
						$"beta=({bR:0.0000},{bG:0.0000},{bB:0.0000}) |{lad}");
				}

				// CELESTIAL SANITY. Azimuth measured as a compass bearing in world space:
				// 0 = -Z, 90 = +X, so a normal day sweeps monotonically one way and the moon
				// sits roughly opposite the sun.
				{
					Vector3 sw = Engine_SkyFrame.ToGodot(frame.SunDirection).Normalized();
					Vector3 mw = Engine_SkyFrame.ToGodot(frame.MoonDirection).Normalized();
					float sunAz = Mathf.RadToDeg(Mathf.Atan2(sw.X, -sw.Z));
					float moonAz = Mathf.RadToDeg(Mathf.Atan2(mw.X, -mw.Z));
					if (sunAz < 0f) { sunAz += 360f; }
					if (moonAz < 0f) { moonAz += 360f; }
					float sunEl = Mathf.RadToDeg(MathF.Asin(Mathf.Clamp(sw.Y, -1f, 1f)));
					float moonEl = Mathf.RadToDeg(MathF.Asin(Mathf.Clamp(mw.Y, -1f, 1f)));
					float apart = Mathf.RadToDeg(sw.AngleTo(mw));
					GD.Print($"[celestial] {level.Name} t={t:0.000} ({t * 24f:00.0}h) " +
						$"sun az={sunAz:000.0} el={sunEl:+00.0;-00.0} | " +
						$"moon az={moonAz:000.0} el={moonEl:+00.0;-00.0} | " +
						$"apart={apart:000.0}deg");
				}

				if (Engine_ThemeTimeline.TryExtractToneMapBlended(gdbs, t, out ToneMapTheme tm))
				{
					float[] v = tm.V;
					GD.Print($"[tonemap] {level.Name} t={t:0.000} " +
						$"midGray={v[(int)ToneMapParam.MiddleGray]:0.###} " +
						$"contrastEnh={v[(int)ToneMapParam.ContrastEnhancement]:0.###} " +
						$"contrastExp={v[(int)ToneMapParam.ContrastEnhancementExponent]:0.###} " +
						$"maxDev={v[(int)ToneMapParam.ContrastMaxDeviation]:0.###} " +
						$"clamping={v[(int)ToneMapParam.ContrastClamping]:0.###} " +
						$"bloomThrAvg={v[(int)ToneMapParam.BloomThresholdAvgFactor]:0.###} " +
						$"bloomFactor={v[(int)ToneMapParam.BloomFactor]:0.###} " +
						$"sat={v[(int)ToneMapParam.Saturation]:0.###} " +
						$"colourSens={v[(int)ToneMapParam.ColourSensitivityThreshold]:0.###} " +
						$"avgLo={v[(int)ToneMapParam.AvgLumLowerBound]:0.###} " +
						$"maxLo={v[(int)ToneMapParam.MaxLumLowerBound]:0.###}");
				}

				// THE THING THE PLAYER SEES: the dome puts the sun at (-x, y, z); the light
				// travels along (x, -y, -z) of the RIG. A correct shadow runs directly away
				// from the sun, so the angle between the shadow's ground direction and the
				// direction away from the sun should be ~0.
				Vector3 domeSun = Engine_SkyFrame.ToGodot(frame.SunDirection);
				Vector3 travel = -Engine_SkyFrame.ToGodot(frame.MainLightDirection);
				var shadowGround = new Vector2(travel.X, travel.Z);
				var awayFromSun = new Vector2(-domeSun.X, -domeSun.Z);
				if (shadowGround.LengthSquared() > 1e-6f && awayFromSun.LengthSquared() > 1e-6f)
				{
					float err = Mathf.RadToDeg(
						shadowGround.Normalized().AngleTo(awayFromSun.Normalized()));
					GD.Print($"[sun-check]     t={t:0.000} shadowVsAwayFromSun={err:0.0}deg " +
						$"(0 = correct, 180 = shadow points AT the sun)");
				}

				float separation = Mathf.RadToDeg(
					MathF.Acos(Math.Clamp(disc.Normalized().Dot(lightDir.Normalized()), -1f, 1f)));

				compared++;
				if (separation > 0.5f)
				{
					differing++;
				}

				if (separation > worst)
				{
					worst = separation;
					worstAt = $"{level.Name} @ {t:0.000}";
				}

				if (!printed)
				{
					GD.Print(
						$"[sun-check] {level.Name}: " +
						$"axisElev={sky.SunAxisElevation:0.0} rigXY={sky.LightRigXyRotation:0.0} " +
						$"moonElev={(sky.HasMoonAxis ? sky.MoonAxisElevation : -1f):0.#} " +
					$"moonXY={sky.MoonAxisXyRotation:0.#} moonZ={sky.MoonAxisZOffset:0.##} " +
					$"discXY={sky.SunAxisXyRotation:0.0} discZ={sky.SunAxisZOffset:0.00} " +
						$"elev=[{string.Join(",", sky.MainLightElevation.Select(e => e.ToString("0")))}]");
					printed = true;
				}

				GD.Print(
					$"[sun-check]     t={t:0.000} " +
					$"disc=({disc.X:0.00},{disc.Y:0.00},{disc.Z:0.00}) " +
					$"light=({lightDir.X:0.00},{lightDir.Y:0.00},{lightDir.Z:0.00}) " +
					$"sep={separation:0.0}deg");
			}
		}

		GD.Print(
			$"[sun-check] TOTAL: {differing} of {compared} samples separate the disc from the " +
			$"light by more than 0.5deg; worst {worst:0.0}deg at {worstAt}");

		GetTree().Quit(0);
	}
}
