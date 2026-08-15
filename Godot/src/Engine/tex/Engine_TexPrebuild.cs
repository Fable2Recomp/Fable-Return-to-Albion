using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Models;

namespace Refii.Textures;

/// <summary>
/// Builds the whole game's modernized textures up front, instead of paying for them during play.
/// </summary>
/// <remarks>
/// §22 DESCRIBES THE LAZY PATH, and the lazy path is right for a player who just wants to start:
/// original assets appear immediately, enhancement happens on a worker, and the cost is one-time
/// per asset. What it cannot avoid is that the first visit to every area is the expensive one, and
/// on a slower machine that is felt as the game getting sharper a second after you look at
/// something.
///
/// THIS IS THE OTHER OFFER, and it is the one a remaster usually makes: spend the whole cost once,
/// deliberately, while the player is looking at a progress bar rather than at the game. Both paths
/// write the same entries to the same cache with the same keys, so this is purely a question of
/// WHEN — nothing here is a second implementation of the pipeline, and
/// <see cref="Engine_TexLibrary.PrebuildTexture"/> is the same decode and the same enhancement the
/// live path runs.
///
/// THE SLOT IS THE AUTHORITY, as everywhere else. The work list comes from the material records —
/// each texture is enhanced under the semantic of the slot that binds it — and never from guessing
/// at a file name. A texture bound to two different slots is legitimately two cache entries, and
/// the enumeration keeps them separate rather than deduplicating by path and picking one.
///
/// IT DOES NOT TOUCH THE SCENE TREE OR BUILD A SINGLE TEXTURE. Everything it produces is a file in
/// <c>user://</c>, so it is safe to run from a worker, from the launcher before the game starts, or
/// from a headless tool.
/// </remarks>
public static class Engine_TexPrebuild
{
	/// <summary>One texture to build, and the slot that decides how.</summary>
	private readonly record struct Item(string Path, TexSemantic Semantic);

	/// <summary>An ORM pair to pack, keyed by its specular and the normal it derives from.</summary>
	private readonly record struct OrmItem(string Specular, string Normal, float Min, float Max);

	/// <summary>What the caller is told while it runs.</summary>
	/// <param name="Done">Items finished, including failures and skips.</param>
	/// <param name="Total">Items in the work list.</param>
	/// <param name="Built">Items that actually produced or confirmed a cache entry.</param>
	/// <param name="Failed">Items whose source could not be read or decoded.</param>
	/// <param name="Current">The path most recently worked on, for a status line.</param>
	public readonly record struct Progress(
		int Done, int Total, int Built, int Failed, string Current);

	/// <summary>
	/// Walks the banks and builds everything. Blocking; call it from a thread you own.
	/// </summary>
	/// <param name="gameDirectory">A directory <see cref="BnkFileSystem"/> can mount.</param>
	/// <param name="report">
	/// Called as work completes. It is invoked from the calling thread between batches, NOT from a
	/// worker, so a UI callback does not have to be thread safe — but it must be quick, because the
	/// batch is waiting on it.
	/// </param>
	/// <param name="cancel">Checked between items so a long run can be stopped.</param>
	public static Progress Run(
		string gameDirectory,
		Action<Progress>? report = null,
		CancellationToken cancel = default)
	{
		if (gameDirectory.Length == 0 || !System.IO.Directory.Exists(gameDirectory))
		{
			return new Progress(0, 0, 0, 0, "no game data");
		}

		using BnkFileSystem banks = BnkFileSystem.Mount(gameDirectory);
		Func<string, byte[]?> read =
			path => banks.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null;

		Engine_BnkMeshLoader.SetResolver(read);

		List<Item> items = Enumerate(banks, out List<OrmItem> orm);
		int total = items.Count + orm.Count;

		var done = 0;
		var built = 0;
		var failed = 0;
		string current = string.Empty;

		// PARALLEL OVER THE WORK LIST, bounded by the same worker count the live path uses. The
		// per-item cost is a decode and possibly a neural upscale, and both are CPU or GPU bound
		// rather than IO bound, so more threads than that buys queueing rather than throughput.
		var options = new System.Threading.Tasks.ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max(1, Engine_TexSettings.Workers),
			CancellationToken = CancellationToken.None,
		};

		void Tick(bool ok, string path)
		{
			// LOCKED, because these are written from every worker. An unlocked ++ on a counter that
			// drives a progress bar is the kind of race that shows up as a bar that finishes at 97%.
			lock (Gate)
			{
				++done;
				if (ok)
				{
					++built;
				}
				else
				{
					++failed;
				}

				current = path;
			}

			// Reported on a stride rather than per item: the callback repaints a UI, and 20,000 of
			// those costs more than the work being reported on.
			if (report is not null && done % 32 == 0)
			{
				report(Snapshot(done, total, built, failed, current));
			}
		}

		try
		{
			System.Threading.Tasks.Parallel.ForEach(items, options, item =>
			{
				if (cancel.IsCancellationRequested)
				{
					return;
				}

				var ok = false;
				try
				{
					ok = Engine_TexLibrary.PrebuildTexture(item.Path, item.Semantic, read);
				}
				catch (Exception)
				{
					// A single unreadable asset must not end the run. It is counted as a failure
					// and the sweep continues — the alternative is one bad entry costing the
					// player the whole prebuild.
				}

				Tick(ok, item.Path);
			});

			System.Threading.Tasks.Parallel.ForEach(orm, options, item =>
			{
				if (cancel.IsCancellationRequested)
				{
					return;
				}

				var ok = false;
				try
				{
					ok = Engine_TexLibrary.PrebuildOrm(
						item.Specular, item.Normal, item.Min, item.Max, read);
				}
				catch (Exception)
				{
				}

				Tick(ok, item.Specular);
			});
		}
		catch (OperationCanceledException)
		{
		}

		Progress final = Snapshot(done, total, built, failed, current);
		report?.Invoke(final);
		return final;
	}

	private static readonly object Gate = new();

	private static Progress Snapshot(int done, int total, int built, int failed, string current)
	{
		lock (Gate)
		{
			return new Progress(done, total, built, failed, current);
		}
	}

	/// <summary>
	/// The work list, taken from the material records rather than from the file extensions.
	/// </summary>
	/// <remarks>
	/// DEDUPLICATED BY (PATH, SLOT), not by path. The same texture bound as an albedo and as a self
	/// illumination map is two entries in the cache because the policy and the key differ, and
	/// collapsing them would leave the second one to be built during play — which is the cost the
	/// prebuild exists to remove.
	///
	/// Cube maps are skipped: their policy is <see cref="TexFilterClass.None"/>, so the only thing
	/// a prebuild could do for them is the base decode, and the six-face decode wants the live
	/// path's assembly rather than this one's.
	/// </remarks>
	private static List<Item> Enumerate(BnkFileSystem banks, out List<OrmItem> orm)
	{
		var seen = new HashSet<Item>();
		var items = new List<Item>();
		var ormSeen = new HashSet<OrmItem>();
		orm = new List<OrmItem>();

		void Add(string? path, TexSemantic semantic)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return;
			}

			var item = new Item(path!, semantic);
			if (seen.Add(item))
			{
				items.Add(item);
			}
		}

		foreach (BnkFile file in banks.EnumerateAllFiles())
		{
			if (!file.VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			byte[] bytes;
			try
			{
				bytes = file.ReadAllBytes();
			}
			catch (Exception)
			{
				continue;
			}

			if (!Engine_MdlData.TryParseInfo(bytes, file.VirtualPath, out MdlInfo info, out _) ||
				info.IsStub || !info.IsEngineFormat)
			{
				continue;
			}

			foreach (MdlMesh mesh in info.Meshes)
			{
				foreach (MdlMaterial material in mesh.Materials)
				{
					Add(material.Diffuse, TexSemantic.Albedo);
					Add(material.Normal, TexSemantic.Normal);
					Add(material.Specular, TexSemantic.Specular);
					Add(material.SelfIllumination, TexSemantic.SelfIllumination);

					if (!Engine_TexSettings.OrmEnabled ||
						string.IsNullOrWhiteSpace(material.Specular))
					{
						continue;
					}

					var pair = new OrmItem(
						material.Specular,
						material.Normal ?? string.Empty,
						material.SpecularPowerMin,
						material.SpecularPowerMax);

					if (ormSeen.Add(pair))
					{
						orm.Add(pair);
					}
				}
			}
		}

		return items;
	}
}
