using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Gdb;
using Refii.Level;
using Refii.Lighting;

namespace Refii.Launcher.Tools;

/// <summary>
/// Do the shipped levels actually state placed lights, and does our read reach them?
///
/// The contract is settled in the binary — <c>sub_825C0CC0</c> reads the component and
/// <c>sub_825C2108</c> builds the primitive — but a correct contract read through the wrong chain
/// resolves nothing, and nothing is indistinguishable from a region that genuinely has no
/// fixtures. So this counts per level and prints the values it found: a run that reports plausible
/// warm colours and metre-scale ranges is the read working, and a run that reports a uniform
/// 255/255/255 at intensity 16 is the read finding the constructor defaults and nothing else.
///
/// Nothing is written and nothing is drawn. It reads the shipped banks in place.
///
///   godot --path Godot --headless res://debug/light_check.tscn
///
/// <c>REFII_LIGHT_LEVEL</c> picks the level by substring; without it every level is walked, which
/// is the run that matters — one region resolving well says nothing about the set.
/// </summary>
public partial class LightCheck : Node
{
	public override void _Ready()
	{
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.PrintErr("[light-check] no game data; set REFII_GAME_DATA.");
			GetTree().Quit(1);
			return;
		}

		using BnkFileSystem fs = BnkFileSystem.Mount(dataRoot);
		GD.Print($"[light-check] mounted {dataRoot}");

		byte[]? Read(string path) =>
			fs.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null;

		string want = OS.GetEnvironment("REFII_LIGHT_LEVEL");
		List<LevelEntry> levels = Engine_LevelIndex.Discover(fs);
		if (want.Length != 0)
		{
			levels = levels
				.Where(l => l.Name.Contains(want, StringComparison.OrdinalIgnoreCase))
				.ToList();
		}

		if (levels.Count == 0)
		{
			GD.PrintErr($"[light-check] no level matched '{want}'.");
			GetTree().Quit(1);
			return;
		}

		List<GdbView> supplemental = Engine_LevelProps.LoadSupplementalViews(Read);
		GD.Print($"[light-check] {supplemental.Count} supplemental gdb view(s)");

		int totalPlacements = 0;
		int totalLights = 0;
		int totalPoint = 0;
		int totalSpot = 0;
		int totalShadow = 0;
		int totalInactive = 0;
		int totalAtEntity = 0;
		int totalFlickering = 0;
		int worstOverlapAll = 0;
		int levelsOverCap = 0;
		string worstOverlapLevel = string.Empty;
		int levelsWithLights = 0;

		foreach (LevelEntry level in levels)
		{
			byte[]? gdbBytes = Engine_LevelGdb.TryRead(level, Read, out string gdbPath);
			if (gdbBytes is null || !GdbView.TryParse(gdbBytes, out GdbView view, out _))
			{
				continue;
			}

			string savePath = Engine_LevelGdb.SiblingWithExtension(gdbPath, ".save");
			List<KeyValuePair<uint, string>> saveMap =
				Engine_GdbSaveMap.Parse(Read(savePath), out _);
			List<GdbPlacement> placements = Engine_GdbPlacements.Parse(view, saveMap);
			if (placements.Count == 0)
			{
				continue;
			}

			var views = new List<GdbView>(supplemental.Count + 1) { view };
			views.AddRange(supplemental);

			// WHERE THE COMPONENT ACTUALLY IS, before asking whether placements reach it. A zero
			// from the walk has two completely different causes — the data does not state the
			// field in any view we loaded, or it does and the chain does not arrive — and one
			// counter cannot tell them apart.
			int stating = 0;
			var componentDefs = new List<uint>();
			for (int r = 0; r < view.RecordCount; r++)
			{
				if (!view.TryFindLocal(
						r, Engine_LevelLights.GraphicAppearanceLightComponent, -1,
						out GdbField edge))
				{
					continue;
				}

				stating++;
				if (componentDefs.Count < 4)
				{
					componentDefs.Add(edge.Raw);
				}
			}

			GD.Print(
				$"[light-check] {level}: {stating} record(s) in the level gdb state the component");

			// Does the PLACEMENT chain reach any of them? Kept separate from the count above
			// because the two failures are different: data that states no lights, and a walk that
			// cannot reach the lights it states. When this read zero while the count above read
			// 108, the walk was fine and the defect was a field TYPE — LightType is an enum, and
			// asking for an int returned the value that means "no light".
			int reachable = placements.Count(p =>
				Engine_LevelFx.Inherited(
					views, p.Hash, Engine_LevelLights.GraphicAppearanceLightComponent,
					GdbFieldType.Reference) is { Raw: not 0 });
			GD.Print(
				$"[light-check] {level}: {reachable} of {placements.Count} placements reach a " +
				"light component through the parent walk");

			int point = 0, spot = 0, shadow = 0, inactive = 0, atEntity = 0, flickering = 0;
			var lit = new List<(Vector3 Origin, float Range)>();
			var samples = new List<string>();

			foreach (GdbPlacement placement in placements)
			{
				if (!Engine_LevelLights.TryRead(placement.Hash, views, out var light))
				{
					continue;
				}

				if (light.Type == Engine_LevelLights.LightTypePoint)
				{
					point++;
				}
				else
				{
					spot++;
				}

				if (light.CastShadows)
				{
					shadow++;
				}

				if (!light.Active)
				{
					inactive++;
				}

				if (light.Dummy.Length == 0)
				{
					atEntity++;
				}

				// FLICKER NEEDS THE RATE, not just a range. A count of "states a range" would be
				// larger and would not be the number of lights that actually move.
				if (light.FlickerRate > 0.0f &&
					(light.FlickerBrightnessRange != 0.0f || light.FlickerRadiusRange != 0.0f))
				{
					flickering++;
				}

				lit.Add((Engine_LevelProps.BuildTransform(placement).Origin, light.Range));

				// THE VALUES, not just the count. A count proves the walk reached something; only
				// the numbers show it reached the LIGHT rather than a template's defaults.
				if (samples.Count < 6)
				{
					samples.Add(
						$"{placement.EntityName ?? "?"}: " +
						$"{(light.Type == Engine_LevelLights.LightTypePoint ? "point" : "spot")} " +
						$"rgb({light.ColourLinear.X:0.00},{light.ColourLinear.Y:0.00}," +
						$"{light.ColourLinear.Z:0.00}) x{light.Intensity:0.0} " +
						$"range={light.Range:0.0} " +
						$"fade={light.FadeDistance:0.0}+{light.FadeRange:0.0}" +
						(light.Type == Engine_LevelLights.LightTypeSpot
							? $" half={Mathf.RadToDeg(light.HalfAngleRadians):0.0}deg"
							: string.Empty) +
						(light.CastShadows ? " shadow" : string.Empty) +
						(light.Active ? string.Empty : " INACTIVE"));
				}
			}

			// DOES THE GUEST'S SEVEN-LIGHT CAP EVER BIND?
			//
			// This decides whether Godot may own light selection. Fable shades at most 7 lights per
			// surface (`sub_82B1EDC0` clears exactly seven slots) and picks them on the CPU;
			// Godot's clustered renderer picks per-fragment with no such limit. Those two only
			// disagree where more than seven lights actually reach the same point — and because
			// the guest's falloff is `saturate(1 - d/range)`, a light contributes EXACTLY nothing
			// beyond its own range. So the question is a geometry one: how many light spheres
			// overlap?
			//
			// Sampled at each light's own centre, which is where crowding is worst — a point out
			// in the open is covered by fewer spheres than one sitting inside a fixture.
			int worstOverlap = 0;
			for (int i = 0; i < lit.Count; i++)
			{
				int overlap = 0;
				for (int j = 0; j < lit.Count; j++)
				{
					if (lit[i].Origin.DistanceSquaredTo(lit[j].Origin) <= lit[j].Range * lit[j].Range)
					{
						overlap++;
					}
				}

				worstOverlap = Math.Max(worstOverlap, overlap);
			}

			if (worstOverlap > worstOverlapAll)
			{
				worstOverlapAll = worstOverlap;
				worstOverlapLevel = level.Name;
			}

			if (worstOverlap > 7)
			{
				levelsOverCap++;
			}

			int found = point + spot;
			totalPlacements += placements.Count;
			totalLights += found;
			totalPoint += point;
			totalSpot += spot;
			totalShadow += shadow;
			totalInactive += inactive;
			totalAtEntity += atEntity;
			totalFlickering += flickering;

			if (found == 0)
			{
				continue;
			}

			levelsWithLights++;
			GD.Print(
				$"[light-check] {level}: {found} of {placements.Count} placements light " +
				$"({point} point, {spot} spot, {shadow} shadowed, {inactive} inactive, " +
				$"{atEntity} at entity origin, {flickering} flickering, " +
				$"worst overlap {worstOverlap})");

			foreach (string sample in samples)
			{
				GD.Print($"[light-check]     {sample}");
			}
		}

		GD.Print(
			$"[light-check] TOTAL over {levels.Count} level(s): {totalLights} lights from " +
			$"{totalPlacements} placements across {levelsWithLights} lit level(s) — " +
			$"{totalPoint} point, {totalSpot} spot, {totalShadow} shadowed, " +
			$"{totalInactive} inactive, {totalAtEntity} at entity origin, " +
			$"{totalFlickering} flickering. WORST LIGHT OVERLAP {worstOverlapAll} " +
			$"(at {worstOverlapLevel}); {levelsOverCap} level(s) exceed the guest's 7-light cap");

		GetTree().Quit(0);
	}
}
