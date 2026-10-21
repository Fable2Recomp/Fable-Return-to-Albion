using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using Godot;
using Refii.Assets;
using Refii.Bnk;
using Refii.Core;
using Refii.Level;

namespace Refii.Lighting;

/// <summary>
/// Warmup-screen background prebuild pass for static octahedral shadow depth atlases (<c>.shadows</c>).
/// Verifies a persistent manifest file (<c>&lt;app&gt;/cache/shadows/shadow_bake_manifest.json</c>) to
/// avoid re-baking when CVar reach scales and shadow resolution are unchanged.
/// </summary>
public static class Engine_LevelShadowPrebuild
{
	public readonly record struct Progress(int Done, int Total, int Built, int UpToDate, int Failed, string Current);

	public sealed class ShadowBakeManifest
	{
		public int Version { get; set; } = 1;
		public int Resolution { get; set; }
		public float PlacedReachScale { get; set; }
		public float BuildingInteriorReachScale { get; set; }
		public uint ShadowMapVersion { get; set; }
	}

	public static string ManifestPath =>
		Path.Combine(Engine_LevelShadowMaps.CacheShadowsDirectory, "shadow_bake_manifest.json");

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
		float placedReach = Engine_LevelLights.PlacedRange;
		float interiorReach = Engine_LevelLights.BuildingInteriorReachScale;

		bool reachValid = IsReachScaleValid(placedReach, interiorReach);

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

		// Fast check: if reach scale is valid and all scenario files exist with matching resolution in header, early exit
		if (reachValid)
		{
			bool allExistAndValid = true;
			foreach ((LevelEntry level, string scenario) in scenarios)
			{
				var entry = CloneWithScenario(level, scenario);
				string diskPath = Engine_LevelShadowMaps.GetShadowMapDiskPath(entry);
				if (!Engine_LevelShadowMaps.IsFileValid(diskPath, resolution))
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

			if (reachValid && Engine_LevelShadowMaps.IsFileValid(diskPath, resolution))
			{
				done++;
				upToDate++;
				report?.Invoke(new Progress(done, total, built, upToDate, failed, scenarioEntry.Name));
				continue;
			}

			report?.Invoke(new Progress(done, total, built, upToDate, failed, $"Baking {scenarioEntry.Name} ({scenario})..."));

			string geoSig = $"{scenarioEntry.HeightfieldPath}|{gdbPath}|{resolution}";
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
					out StaticShadowMapContainer? baked,
					out string resolvedGdb,
					out int tris,
					out string _) && baked is not null)
				{
					container = baked;
					if (resolvedGdb.Length != 0)
					{
						bakedGeometryCache[$"{scenarioEntry.HeightfieldPath}|{resolvedGdb}|{resolution}"] =
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

		// Write new manifest if not cancelled
		if (!cancel.IsCancellationRequested && failed == 0)
		{
			WriteManifest(resolution, placedReach, interiorReach);
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

	public static bool IsReachScaleValid(float placedReach, float interiorReach)
	{
		try
		{
			if (!File.Exists(ManifestPath))
			{
				return true;
			}

			string json = File.ReadAllText(ManifestPath);
			ShadowBakeManifest? manifest = JsonSerializer.Deserialize<ShadowBakeManifest>(json);
			if (manifest is null)
			{
				return true;
			}

			return MathF.Abs(manifest.PlacedReachScale - placedReach) < 1e-3f &&
			       MathF.Abs(manifest.BuildingInteriorReachScale - interiorReach) < 1e-3f;
		}
		catch (Exception)
		{
			return true;
		}
	}

	public static bool IsManifestValid(int resolution, float placedReach, float interiorReach)
	{
		try
		{
			if (!File.Exists(ManifestPath))
			{
				return false;
			}

			string json = File.ReadAllText(ManifestPath);
			ShadowBakeManifest? manifest = JsonSerializer.Deserialize<ShadowBakeManifest>(json);
			if (manifest is null)
			{
				return false;
			}

			return manifest.Resolution == resolution &&
			       manifest.ShadowMapVersion == Engine_LevelShadowMaps.Version &&
			       MathF.Abs(manifest.PlacedReachScale - placedReach) < 1e-3f &&
			       MathF.Abs(manifest.BuildingInteriorReachScale - interiorReach) < 1e-3f;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static void WriteManifest(int resolution, float placedReach, float interiorReach)
	{
		try
		{
			string? dir = Path.GetDirectoryName(ManifestPath);
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}

			var manifest = new ShadowBakeManifest
			{
				Version = 1,
				Resolution = resolution,
				PlacedReachScale = placedReach,
				BuildingInteriorReachScale = interiorReach,
				ShadowMapVersion = Engine_LevelShadowMaps.Version,
			};

			string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(ManifestPath, json);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[shadow-prebuild] failed to write manifest: {ex.Message}");
		}
	}
}
