using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Godot;
using Refii.Assets;
using Refii.Bnk;
using Refii.Core;
using Refii.Level;

namespace Refii.Lighting;

/// <summary>
/// Warmup-screen background prebuild pass for static octahedral shadow depth atlases (<c>.shadows</c>).
/// Inspects the self-contained binary header of each scenario's <c>.shadows</c> file to
/// avoid re-baking when resolution, lights per map, and CVar reach scales are unchanged.
/// </summary>
public static class Engine_LevelShadowPrebuild
{
	internal sealed class ShadowBakeManifest
	{
		public int Resolution { get; set; }
		public int LightsPerMap { get; set; }
		public float PlacedReachScale { get; set; }
		public float InteriorReachScale { get; set; }
		public int ShadowMapVersion { get; set; }
	}

	public readonly record struct Progress(int Done, int Total, int Built, int UpToDate, int Failed, string Current);

	/// <summary>
	/// Runs the static shadow prebuild sweep across all discovered levels and scenarios.
	/// Blocking; must be called from a background worker thread.
	/// </summary>
	public static Progress Run(
		string gameDirectory,
		Action<Progress>? report = null,
		CancellationToken cancel = default)
	{
		if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory))
		{
			return new Progress(0, 0, 0, 0, 0, "no game data");
		}

		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			return new Progress(0, 0, 0, 0, 0, "classic lighting active (shadow maps disabled)");
		}

		using BnkFileSystem banks = BnkFileSystem.Mount(gameDirectory);
		Func<string, byte[]?> read = path => banks.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null;

		List<LevelEntry> levels = Engine_LevelIndex.Discover(banks);
		if (levels.Count == 0)
		{
			return new Progress(0, 0, 0, 0, 0, "no levels discovered");
		}

		int resolution = Engine_LevelShadowMaps.ConfiguredAtlasResolution;
		int lightsPerMap = Engine_LevelShadowMaps.ConfiguredLightsPerMap;
		float placedReach = Engine_LevelLights.PlacedRange;
		float interiorReach = Engine_LevelLights.BuildingInteriorReachScale;

		// Clean up any legacy manifest file if present
		try
		{
			string legacyManifest = Path.Combine(Engine_LevelShadowMaps.CacheShadowsDirectory, "shadow_bake_manifest.json");
			if (File.Exists(legacyManifest))
			{
				File.Delete(legacyManifest);
			}
		}
		catch (Exception)
		{
		}

		// Collect all valid level scenario work items
		var scenarios = new List<(LevelEntry Entry, string ScenarioName)>();
		foreach (LevelEntry level in levels)
		{
			IReadOnlyList<string> list = level.Scenarios;
			if (list.Count == 0)
			{
				scenarios.Add((level, level.Scenario.Length != 0 ? level.Scenario : "defaultscenario"));
				continue;
			}

			for (int s = 0; s < list.Count; s++)
			{
				scenarios.Add((level, list[s]));
			}
		}

		int total = scenarios.Count;
		int done = 0;
		int built = 0;
		int upToDate = 0;
		int failed = 0;

		report?.Invoke(new Progress(0, total, 0, 0, 0, "Checking static shadows..."));

		// Fast check: verify every scenario file exists and matches current settings directly in its header
		bool allExistAndValid = true;
		foreach ((LevelEntry level, string scenario) in scenarios)
		{
			var entry = CloneWithScenario(level, scenario);
			Engine_LevelGdb.TryRead(entry, read, out string gdbPath);
			string diskPath = Engine_LevelShadowMaps.GetShadowMapDiskPath(entry, gdbPath);
			if (!Engine_LevelShadowMaps.IsFileValid(diskPath, resolution, lightsPerMap, placedReach, interiorReach))
			{
				allExistAndValid = false;
				break;
			}
		}

		if (allExistAndValid)
		{
			upToDate = total;
			done = total;
			Progress fast = new(done, total, built, upToDate, failed, "All static shadows up to date");
			report?.Invoke(fast);
			return fast;
		}

		// Ensure cache shadows directory exists
		try
		{
			Directory.CreateDirectory(Engine_LevelShadowMaps.CacheShadowsDirectory);
		}
		catch (Exception)
		{
		}

		var bakedGeometryCache = new ConcurrentDictionary<string, (StaticShadowMapContainer Container, int Tris)>(
			StringComparer.OrdinalIgnoreCase);

		for (int i = 0; i < scenarios.Count; i++)
		{
			if (cancel.IsCancellationRequested)
			{
				break;
			}

			(LevelEntry level, string scenario) = scenarios[i];
			var scenarioEntry = CloneWithScenario(level, scenario);

			if (!Engine_LevelManifest.TryResolve(
				scenarioEntry, read, out _, out _, out string scenarioUsed))
			{
				done++;
				failed++;
				continue;
			}

			if (scenarioEntry.Scenario.Length == 0)
			{
				scenarioEntry.Scenario = scenarioUsed;
			}

			Engine_LevelGdb.TryRead(scenarioEntry, read, out string gdbPath);
			string diskPath = Engine_LevelShadowMaps.GetShadowMapDiskPath(scenarioEntry, gdbPath);

			if (Engine_LevelShadowMaps.IsFileValid(diskPath, resolution, lightsPerMap, placedReach, interiorReach))
			{
				done++;
				upToDate++;
				report?.Invoke(new Progress(done, total, built, upToDate, failed, scenarioEntry.Name));
				continue;
			}

			report?.Invoke(new Progress(done, total, built, upToDate, failed, $"Baking {scenarioEntry.Name} ({scenario})..."));

			string geoSig = $"{scenarioEntry.HeightfieldPath}|{gdbPath}|{resolution}|{lightsPerMap}";
			StaticShadowMapContainer? container = null;

			if (gdbPath.Length != 0 && bakedGeometryCache.TryGetValue(geoSig, out var cached))
			{
				container = cached.Container;
			}
			else
			{
				if (Engine_LevelShadowMaps.BakeScenario(
					scenarioEntry,
					read,
					resolution,
					lightsPerMap,
					placedReach,
					interiorReach,
					out StaticShadowMapContainer? baked,
					out string resolvedGdb,
					out int tris,
					out string _) && baked is not null)
				{
					container = baked;
					if (resolvedGdb.Length != 0)
					{
						bakedGeometryCache[$"{scenarioEntry.HeightfieldPath}|{resolvedGdb}|{resolution}|{lightsPerMap}"] =
							(container, tris);
					}
				}
			}

			if (container is not null)
			{
				try
				{
					string? dir = Path.GetDirectoryName(diskPath);
					if (!string.IsNullOrEmpty(dir))
					{
						Directory.CreateDirectory(dir);
					}

					byte[] payload = Engine_LevelShadowMaps.Serialize(container);
					File.WriteAllBytes(diskPath, payload);
					built++;
				}
				catch (Exception ex)
				{
					GD.PrintErr($"[shadow-prebuild] failed to write {diskPath}: {ex.Message}");
					failed++;
				}
			}
			else
			{
				failed++;
			}

			done++;
			report?.Invoke(new Progress(done, total, built, upToDate, failed, scenarioEntry.Name));
		}

		Progress result = new(done, total, built, upToDate, failed, "Static shadows prebuild complete");
		report?.Invoke(result);
		return result;
	}

	public static LevelEntry CloneWithScenario(LevelEntry source, string scenario) =>
		new()
		{
			World = source.World,
			LevelPath = source.LevelPath,
			Group = source.Group,
			Name = source.Name,
			Scenarios = source.Scenarios,
			Scenario = scenario,
			HeightfieldPath = source.HeightfieldPath,
			HeightfieldSize = source.HeightfieldSize,
			AdjacentEhfPaths = source.AdjacentEhfPaths,
			MainEhfPath = source.MainEhfPath,
			Variant = source.Variant,
		};
}
