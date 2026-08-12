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
			if (gdbs.Count == 0)
			{
				continue;
			}

			bool printed = false;

			foreach (float t in Hours)
			{
				if (!Engine_ThemeTimeline.TryExtractSkyBlended(gdbs, t, out SkyTheme sky))
				{
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
