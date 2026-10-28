using System;
using System.Collections.Generic;

namespace Refii.Core;

/// <summary>
/// What the managed heap holds, for the debug channel's <c>heapcensus</c> op: the GC's own split
/// by generation, and the known long-lived caches counted entry by entry.
/// </summary>
/// <remarks>
/// NativeAOT has no heap walker and no by-type census, so the caches are counted from their own
/// tables. Each figure is the bytes of the arrays a cache references, not the object headers
/// and dictionaries around them, and a cache that shares arrays with a live owner is counted
/// anyway. The sum is a floor on what those owners hold.
///
/// THE GC'S SIZES INCLUDE THE DEAD. `SizeAfterBytes` of gen2 and the large object heap is what
/// the last collection of that generation left plus everything promoted or allocated there
/// since, garbage included, and the large object heap is not compacted unless asked. `collect`
/// runs one blocking, compacting gen2 with the large object heap compacted too and reports the
/// split again: that second split is the live heap. It stops the world for hundreds of
/// milliseconds on a phone, so it runs only when asked.
/// </remarks>
public static class Engine_HeapCensus
{
	private const double Mb = 1048576.0;

	/// <summary>The GC split, the cache census and, with <paramref name="collect"/>, the live split.</summary>
	public static Dictionary<string, object?> Describe(bool collect)
	{
		var reply = new Dictionary<string, object?>
		{
			["gc"] = Split(GC.GetGCMemoryInfo(GCKind.Any)),
			["lastFullBlocking"] = Split(GC.GetGCMemoryInfo(GCKind.FullBlocking)),
		};

		if (collect)
		{
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
				System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
			GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
			reply["collectMs"] = Math.Round(
				System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1);
			reply["live"] = Split(GC.GetGCMemoryInfo(GCKind.FullBlocking));
		}

		reply["caches"] = Caches();
		return reply;
	}

	private static Dictionary<string, object?> Split(GCMemoryInfo info)
	{
		string[] names = { "gen0", "gen1", "gen2", "loh", "poh" };
		var generations = new Dictionary<string, object?>();
		ReadOnlySpan<GCGenerationInfo> each = info.GenerationInfo;
		for (int i = 0; i < each.Length && i < names.Length; i++)
		{
			generations[names[i]] = new Dictionary<string, object?>
			{
				["beforeMB"] = Round(each[i].SizeBeforeBytes),
				["afterMB"] = Round(each[i].SizeAfterBytes),
				["fragmentedAfterMB"] = Round(each[i].FragmentationAfterBytes),
			};
		}

		return new Dictionary<string, object?>
		{
			["index"] = info.Index,
			["generation"] = info.Generation,
			["compacted"] = info.Compacted,
			["concurrent"] = info.Concurrent,
			["heapMB"] = Round(info.HeapSizeBytes),
			["fragmentedMB"] = Round(info.FragmentedBytes),
			["committedMB"] = Round(info.TotalCommittedBytes),
			["promotedMB"] = Round(info.PromotedBytes),
			["memoryLoadMB"] = Round(info.MemoryLoadBytes),
			["highLoadThresholdMB"] = Round(info.HighMemoryLoadThresholdBytes),
			["availableMB"] = Round(info.TotalAvailableMemoryBytes),
			["pinned"] = info.PinnedObjectsCount,
			["pauseMs"] = info.PauseDurations.Length > 0
				? Math.Round(info.PauseDurations[0].TotalMilliseconds, 1)
				: 0,
			["generations"] = generations,
		};
	}

	/// <summary>Each known cache's entries and referenced bytes. Reads only; builds nothing.</summary>
	private static Dictionary<string, object?> Caches()
	{
		var caches = new Dictionary<string, object?>();
		void Add(string name, Func<(long Entries, long Bytes)> census)
		{
			try
			{
				(long entries, long bytes) = census();
				caches[name] = new Dictionary<string, object?> { ["entries"] = entries, ["MB"] = Round(bytes) };
			}
			catch (Exception exception)
			{
				caches[name] = new Dictionary<string, object?> { ["error"] = exception.Message };
			}
		}

		Add("anim.sharedClips", Refii.Animations.Engine_ClipAnimator.SharedCensus);
		Add("anim.dataFile", Refii.Animations.Engine_AnimCatalog.DataCensus);
		Add("terrain.splatSources", Refii.Level.Engine_TerrainSplatMaterial.SharedCensus);
		Add("hero.textureMorphSources", () =>
		{
			Refii.Hero.Engine_HeroTextureMorph.CacheSnapshot hero = Refii.Hero.Engine_HeroTextureMorph.CacheMetrics;
			return (hero.DecodedEntries, hero.DecodedRgbaBytes);
		});
		Add("gdb.liveDatabases", Refii.Gdb.GdbMulti.LiveCensus);
		Add("text.banks", Refii.Gdb.Engine_BnkTextBankLoader.Census);
		Add("lighting.lightmapContainers", Refii.Lighting.Engine_LevelBakedLighting.ContainerCensus);
		Add("lighting.staticShadowAtlas", Refii.Lighting.Engine_LevelShadowMaps.AtlasCensus);
		Add("lighting.shadowBakeTriangles", Refii.Lighting.Engine_LevelShadowMaps.TriangleCensus);
		Add("physics.clothIndex", Refii.Physics.Engine_PhysicsIndex.Census);
		Add("bnk.nestedContainers", () => (
			System.Threading.Interlocked.Read(ref Refii.Bnk.BnkFileSystem.NestedLoads),
			System.Threading.Interlocked.Read(ref Refii.Bnk.BnkFileSystem.NestedResidentBytes)));
		Add("bnk.mountedFiles", () => (Refii.Assets.Engine_GameAssets.Instance?.FileCount ?? 0, 0));
		Add("level.lastBuildReads", () =>
			Refii.Level.LevelReads.LastCompleted is { } reads ? (reads.Entries, reads.HeldBytes) : (0, 0));
		// Godot Variant arrays kept as mesh metadata: NATIVE memory, listed for scale only.
		Add("native.surfaceArraysRecorded", () => (
			Refii.Models.Engine_MdlSurfaceArrays.Readbacks,
			Refii.Models.Engine_MdlSurfaceArrays.RecordedBytes));
		return caches;
	}

	private static double Round(long bytes) => Math.Round(bytes / Mb, 1);
}
