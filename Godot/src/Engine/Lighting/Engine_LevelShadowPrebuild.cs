using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Threading;
using Godot;
using Refii.Assets;
using Refii.Bnk;
using Refii.Core;
using Refii.Level;
using Refii.Models;

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
		CancellationToken cancel = default,
		Lazy<BnkFileSystem>? mount = null)
	{
		if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory))
		{
			return new Progress(0, 0, 0, 0, 0, "no game data");
		}

		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			return new Progress(0, 0, 0, 0, 0, "classic lighting active (shadow maps disabled)");
		}

		using BnkFileSystem? owned = mount is null ? BnkFileSystem.Mount(gameDirectory) : null;
		BnkFileSystem banks = mount?.Value ?? owned!;
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
		bool bakeAo = Engine_LevelAmbientOcclusion.BakedAoEnabled;

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

		report?.Invoke(new Progress(0, total, 0, 0, 0, "Checking static shadows & AO..."));

		// Fast check: verify every scenario file exists and matches current settings directly in its header
		bool allExistAndValid = true;
		if (bakeAo)
		{
			try
			{
				if (!Directory.Exists(Engine_ModelSelfAo.CacheModelsAoDirectory) ||
				    Directory.GetFiles(Engine_ModelSelfAo.CacheModelsAoDirectory, "*.selfao").Length == 0)
				{
					allExistAndValid = false;
				}
			}
			catch (Exception)
			{
				allExistAndValid = false;
			}
		}

		if (allExistAndValid)
		{
			foreach ((LevelEntry level, string scenario) in scenarios)
			{
				var entry = CloneWithScenario(level, scenario);
				Engine_LevelGdb.TryRead(entry, read, out string gdbPath);
				string diskPath = Engine_LevelShadowMaps.GetShadowMapDiskPath(entry, gdbPath);
				string aoDiskPath = Engine_LevelAmbientOcclusion.GetScenarioAoDiskPath(entry, gdbPath);
				if (!Engine_LevelShadowMaps.IsFileValid(diskPath, resolution, lightsPerMap, placedReach, interiorReach) ||
				    (bakeAo && !Engine_LevelAmbientOcclusion.IsFileValid(aoDiskPath)))
				{
					allExistAndValid = false;
					break;
				}
			}
		}

		if (allExistAndValid)
		{
			upToDate = total;
			done = total;
			Progress fast = new(done, total, built, upToDate, failed, "All static shadows & AO up to date");
			report?.Invoke(fast);
			return fast;
		}

		// Ensure cache directories exist
		try
		{
			Directory.CreateDirectory(Engine_LevelShadowMaps.CacheShadowsDirectory);
			if (bakeAo)
			{
				Directory.CreateDirectory(Engine_LevelAmbientOcclusion.CacheAoDirectory);
			}
		}
		catch (Exception)
		{
		}

		var bakedShadowPathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var bakedAoPathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string lastLevelPath = string.Empty;

		try
		{
			for (int i = 0; i < scenarios.Count; i++)
			{
				if (cancel.IsCancellationRequested)
				{
					break;
				}

				(LevelEntry level, string scenario) = scenarios[i];
				if (!string.Equals(lastLevelPath, level.LevelPath, StringComparison.OrdinalIgnoreCase))
				{
					lastLevelPath = level.LevelPath;
					Engine_LevelShadowMaps.ClearBakeCaches();
					Engine_LevelAmbientOcclusion.ClearBakeCaches();
					Engine_ModelSelfAo.ClearMemoryCache();
				}

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
				string aoDiskPath = Engine_LevelAmbientOcclusion.GetScenarioAoDiskPath(scenarioEntry, gdbPath);
				string shadowSig = $"{scenarioEntry.HeightfieldPath}|{gdbPath}|{resolution}|{lightsPerMap}";
				string aoSig = $"{scenarioEntry.HeightfieldPath}|{gdbPath}";

				bool shadowValid = Engine_LevelShadowMaps.IsFileValid(diskPath, resolution, lightsPerMap, placedReach, interiorReach);
				bool aoValid = !bakeAo || Engine_LevelAmbientOcclusion.IsFileValid(aoDiskPath);
				if (shadowValid && gdbPath.Length != 0)
				{
					bakedShadowPathCache[shadowSig] = diskPath;
				}

				if (aoValid && bakeAo && gdbPath.Length != 0)
				{
					bakedAoPathCache[aoSig] = aoDiskPath;
				}

				if (shadowValid && aoValid)
				{
					done++;
					upToDate++;
					report?.Invoke(new Progress(done, total, built, upToDate, failed, scenarioEntry.Name));
					continue;
				}

				report?.Invoke(new Progress(done, total, built, upToDate, failed, $"Baking {scenarioEntry.Name} ({scenario})..."));

				if (!shadowValid && gdbPath.Length != 0 &&
				    bakedShadowPathCache.TryGetValue(shadowSig, out string? existingShadowPath) &&
				    !string.Equals(existingShadowPath, diskPath, StringComparison.OrdinalIgnoreCase) &&
				    File.Exists(existingShadowPath))
				{
					try
					{
						string? dir = Path.GetDirectoryName(diskPath);
						if (!string.IsNullOrEmpty(dir))
						{
							Directory.CreateDirectory(dir);
						}

						File.Copy(existingShadowPath, diskPath, overwrite: true);
						shadowValid = true;
					}
					catch (Exception)
					{
					}
				}

				if (!aoValid && gdbPath.Length != 0 &&
				    bakedAoPathCache.TryGetValue(aoSig, out string? existingAoPath) &&
				    !string.Equals(existingAoPath, aoDiskPath, StringComparison.OrdinalIgnoreCase) &&
				    File.Exists(existingAoPath))
				{
					try
					{
						string? dir = Path.GetDirectoryName(aoDiskPath);
						if (!string.IsNullOrEmpty(dir))
						{
							Directory.CreateDirectory(dir);
						}

						File.Copy(existingAoPath, aoDiskPath, overwrite: true);
						aoValid = true;
					}
					catch (Exception)
					{
					}
				}

				StaticShadowMapContainer? shadowContainer = null;
				StaticAoContainer? aoContainer = null;
				string resolvedGdb = gdbPath;

				if (!shadowValid || !aoValid)
				{
					if (Engine_LevelShadowMaps.TryBuildWorldGeometry(
						scenarioEntry,
						read,
						out Engine_LevelShadowMaps.BvhTree? worldBvh,
						out List<Engine_LevelProps.StaticShadowPropBlock> propBlocks,
						out HashSet<uint> lightHosts,
						out HashSet<string> fixtureModelPaths,
						out List<Engine_LevelShadowMaps.BakeLightSpec> bakeLights,
						out List<GhfHeights> terrainHeights,
						out resolvedGdb,
						out int _,
						out string _) && worldBvh is not null)
					{
						if (!shadowValid)
						{
							shadowContainer = Engine_LevelShadowMaps.BakeFromBvh(
								worldBvh, bakeLights, resolution, lightsPerMap, placedReach, interiorReach);
						}

						if (!aoValid)
						{
							GhfHeights? primaryTerrain = Engine_LevelAmbientOcclusion.SelectPrimaryTerrain(terrainHeights);
							aoContainer = Engine_LevelAmbientOcclusion.BakeFromBvh(
								worldBvh, propBlocks, lightHosts, fixtureModelPaths, primaryTerrain, read);
						}
					}
				}

				bool scenarioSucceeded = true;

				if (!shadowValid)
				{
					if (shadowContainer is not null)
					{
						try
						{
							string? dir = Path.GetDirectoryName(diskPath);
							if (!string.IsNullOrEmpty(dir))
							{
								Directory.CreateDirectory(dir);
							}

							byte[] payload = Engine_LevelShadowMaps.Serialize(shadowContainer);
							shadowContainer.Texels = Array.Empty<ushort>();
							File.WriteAllBytes(diskPath, payload);
							if (resolvedGdb.Length != 0)
							{
								bakedShadowPathCache[$"{scenarioEntry.HeightfieldPath}|{resolvedGdb}|{resolution}|{lightsPerMap}"] = diskPath;
							}
						}
						catch (Exception ex)
						{
							GD.PrintErr($"[shadow-prebuild] failed to write {diskPath}: {ex.Message}");
							scenarioSucceeded = false;
						}
					}
					else
					{
						scenarioSucceeded = false;
					}
				}

				if (!aoValid)
				{
					if (aoContainer is not null)
					{
						try
						{
							string? dir = Path.GetDirectoryName(aoDiskPath);
							if (!string.IsNullOrEmpty(dir))
							{
								Directory.CreateDirectory(dir);
							}

							byte[] payload = Engine_LevelAmbientOcclusion.Serialize(aoContainer);
							aoContainer.PropAoBytes = Array.Empty<byte>();
							aoContainer.TerrainAoBytes = Array.Empty<byte>();
							File.WriteAllBytes(aoDiskPath, payload);
							if (resolvedGdb.Length != 0)
							{
								bakedAoPathCache[$"{scenarioEntry.HeightfieldPath}|{resolvedGdb}"] = aoDiskPath;
							}
						}
						catch (Exception ex)
						{
							GD.PrintErr($"[ao-prebuild] failed to write {aoDiskPath}: {ex.Message}");
							scenarioSucceeded = false;
						}
					}
					else
					{
						scenarioSucceeded = false;
					}
				}

				if (scenarioSucceeded)
				{
					built++;
				}
				else
				{
					failed++;
				}

				Refii.AndroidRuntime.AndroidBakedShadows.AfterArea();
				done++;
				report?.Invoke(new Progress(done, total, built, upToDate, failed, scenarioEntry.Name));
			}
		}
		finally
		{
			Engine_LevelShadowMaps.ClearBakeCaches();
			Engine_LevelAmbientOcclusion.ClearBakeCaches();
			Engine_ModelSelfAo.ClearMemoryCache();
			if (built > 0)
			{
				GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
				GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
			}
		}

		Progress result = new(done, total, built, upToDate, failed, "Static shadows & AO prebuild complete");
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
