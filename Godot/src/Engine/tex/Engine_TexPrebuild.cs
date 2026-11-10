using System;
using System.Collections.Generic;
using System.Linq;
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

		// PUT BACK ON THE WAY OUT. The resolver is global; this one reads a bank system the
		// `using` above disposes, and the launcher runs this sweep in its own process — a sky,
		// weather or terrain read in the editor afterwards would have gone to a dead bank.
		Func<string, byte[]?>? previous = Engine_BnkMeshLoader.Resolver;
		Func<string, int, byte[]?>? previousHead = Engine_BnkMeshLoader.HeadResolver;
		Engine_BnkMeshLoader.SetResolver(read);
		try
		{
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
		if (!cancel.IsCancellationRequested && total > 0)
		{
			WriteMarker(final);
		}
		report?.Invoke(final);
		return final;
		}
		finally
		{
			Engine_BnkMeshLoader.SetResolver(previous, previousHead);
		}
	}

	// ------------------------------------------------------------------ the completion marker

	/// <summary>
	/// What a finished sweep was built for: the settings that enter the cache keys. A marker
	/// whose fingerprint is not this one belongs to another configuration and does not count.
	/// </summary>
	public static string Fingerprint() =>
		$"model={Engine_TexSettings.Model};weights={Engine_TexSettings.Weights};" +
		$"upscale={Engine_TexSettings.UpscaleEnabled};orm={Engine_TexSettings.OrmEnabled};" +
		$"ao={Engine_TexSettings.OrmAoStrength:F2};metal={Engine_TexSettings.OrmMetallicStrength:F2};" +
		$"height={Engine_TexSettings.OrmHeight};converter={Engine_TexCache.ConverterVersion};" +
		$"policy={Engine_TexSemantic.PolicyVersion}";

	/// <summary>Beside the cache it describes, so clearing the cache clears the claim.</summary>
	public static string MarkerPath =>
		System.IO.Path.Combine(Engine_TexCache.CacheRoot, "prebuild.txt");

	/// <summary>Fast boot marker for the ORM-only sweep, keyed by fingerprint and mounted file count.</summary>
	public static string OrmMarkerPath =>
		System.IO.Path.Combine(Engine_TexCache.CacheRoot, "orm_prebuild.txt");

	/// <summary>Fast boot marker for the texture upscale sweep, keyed by fingerprint and mounted file count.</summary>
	public static string TexturesMarkerPath =>
		System.IO.Path.Combine(Engine_TexCache.CacheRoot, "textures_prebuild.txt");

	private static string BootFingerprint(int fileCount) =>
		$"{Fingerprint()};files={fileCount}";

	/// <summary>
	/// Checks whether a boot prebuild marker matches the current settings and mounted file count,
	/// allowing warm boots to skip re-reading and hashing thousands of <c>.mdl</c> and <c>.tex</c> files.
	/// </summary>
	public static bool TryReadFastMarker(
		string markerPath,
		int fileCount,
		out int built,
		out int total,
		out int failed)
	{
		built = 0;
		total = 0;
		failed = 0;
		if (!System.IO.File.Exists(markerPath) || !System.IO.Directory.Exists(Engine_TexCache.CacheRoot))
		{
			return false;
		}

		try
		{
			string? fp = null;
			foreach (string line in System.IO.File.ReadAllLines(markerPath))
			{
				int eq = line.IndexOf('=');
				if (eq <= 0)
				{
					continue;
				}

				string key = line[..eq];
				string val = line[(eq + 1)..];
				switch (key)
				{
					case "fingerprint":
						fp = val;
						break;
					case "built":
						int.TryParse(val, out built);
						break;
					case "total":
						int.TryParse(val, out total);
						break;
					case "failed":
						int.TryParse(val, out failed);
						break;
				}
			}

			return total > 0 && string.Equals(fp, BootFingerprint(fileCount), StringComparison.Ordinal);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Writes a fast boot marker keyed by fingerprint and mounted file count.</summary>
	public static void WriteFastMarker(string markerPath, int fileCount, int built, int total, int failed)
	{
		try
		{
			System.IO.Directory.CreateDirectory(Engine_TexCache.CacheRoot);
			System.IO.File.WriteAllText(markerPath,
				$"fingerprint={BootFingerprint(fileCount)}\n" +
				$"built={built}\ntotal={total}\nfailed={failed}\n" +
				$"when={DateTime.Now:yyyy-MM-dd HH:mm}\n");
		}
		catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
		{
			GD.PushWarning($"[tex] fast prebuild marker not written: {e.Message}");
		}
	}

	/// <summary>
	/// Records a COMPLETED sweep (not a cancelled one): the fingerprint and the counts. The
	/// launcher's Install tab ticks the row on this, never on "something is cached" — the cache
	/// fills during play as well, and one texture would have read as complete.
	/// </summary>
	private static void WriteMarker(Progress final)
	{
		try
		{
			System.IO.Directory.CreateDirectory(Engine_TexCache.CacheRoot);
			System.IO.File.WriteAllText(MarkerPath,
				$"fingerprint={Fingerprint()}\n" +
				$"built={final.Built}\ntotal={final.Total}\nfailed={final.Failed}\n" +
				$"when={DateTime.Now:yyyy-MM-dd HH:mm}\n");
		}
		catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
		{
			GD.PushWarning($"[tex] prebuild marker not written: {e.Message}");
		}
	}

	/// <summary>
	/// Whether a completed sweep for the CURRENT settings is on record, and what it built.
	/// False with an empty detail when there is none; false with the detail when the marker is
	/// for other settings, so the row can say why it does not count.
	/// </summary>
	public static bool IsCurrent(out string detail)
	{
		detail = string.Empty;
		string path = MarkerPath;
		if (!System.IO.File.Exists(path))
		{
			return false;
		}
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		try
		{
			foreach (string line in System.IO.File.ReadAllLines(path))
			{
				int eq = line.IndexOf('=');
				if (eq > 0)
				{
					values[line[..eq]] = line[(eq + 1)..];
				}
			}
		}
		catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
		{
			return false;
		}
		values.TryGetValue("built", out string? built);
		values.TryGetValue("total", out string? total);
		values.TryGetValue("when", out string? when);
		values.TryGetValue("fingerprint", out string? fingerprint);
		bool current = string.Equals(fingerprint, Fingerprint(), StringComparison.Ordinal);
		detail = current
			? $"Built {built ?? "?"} of {total ?? "?"} on {when ?? "?"}. The game's boot sweep finds them ready."
			: $"Built on {when ?? "?"} for other settings; build again for the current ones.";
		return current;
	}

	/// <summary>
	/// The TEXTURE sweep alone — every material texture decoded and enhanced into the cache,
	/// boot-safe: unlike <see cref="Run"/> it never touches the global resolver
	/// (<see cref="Engine_TexLibrary.PrebuildTexture"/> reads only through the delegate).
	/// Runs only when enhancement is on — with it off, the base decodes are cheap enough to
	/// leave to the live path, and a boot spent decoding for nothing is a boot the player
	/// waits on.
	/// </summary>
	public static Progress RunTextures(
		string gameDirectory,
		Action<Progress>? report = null,
		CancellationToken cancel = default,
		Lazy<BnkFileSystem>? mount = null)
	{
		if (gameDirectory.Length == 0 || !System.IO.Directory.Exists(gameDirectory))
		{
			return new Progress(0, 0, 0, 0, "no game data");
		}

		if (!Engine_TexEnhance.Enabled)
		{
			return new Progress(0, 0, 0, 0, "enhancement off");
		}

		using BnkFileSystem? owned = mount is null ? BnkFileSystem.Mount(gameDirectory) : null;
		BnkFileSystem banks = mount?.Value ?? owned!;
		if (TryReadFastMarker(TexturesMarkerPath, banks.FileCount, out int cachedBuilt, out int cachedTotal, out int cachedFailed))
		{
			Progress fast = new(cachedTotal, cachedTotal, cachedBuilt, cachedFailed, string.Empty);
			report?.Invoke(fast);
			return fast;
		}

		Func<string, byte[]?> read =
			path => banks.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null;

		List<Item> items = EnumerateOnce(banks, out _);
		int total = items.Count;

		var done = 0;
		var built = 0;
		var failed = 0;
		string current = string.Empty;

		var options = new System.Threading.Tasks.ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max(1, Engine_TexSettings.Workers),
			CancellationToken = CancellationToken.None,
		};

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
				// One unreadable asset must not end the sweep.
			}

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

				current = item.Path;
			}

			if (report is not null && done % 32 == 0)
			{
				report(Snapshot(done, total, built, failed, current));
			}
		});

		Progress final = Snapshot(done, total, built, failed, current);
		if (!cancel.IsCancellationRequested && total > 0)
		{
			WriteFastMarker(TexturesMarkerPath, banks.FileCount, final.Built, final.Total, final.Failed);
		}
		report?.Invoke(final);
		return final;
	}

	/// <summary>
	/// The ORM sweep alone — the boot screen's share of the prebuild.
	/// </summary>
	/// <remarks>
	/// The full <see cref="Run"/> is the launcher's deliberate build-everything offer; this is
	/// the piece the game cannot healthily defer. An ORM built on demand is a Poisson solve on
	/// a streaming worker, and a cache-version bump means EVERY material in a region pays it in
	/// the same minute — which presents as the game freezing right after a level loads. Run once
	/// behind the warm-up screen it is the same cost made visible, and on every later boot the
	/// sweep is enumerate-and-hit in seconds.
	///
	/// IT NEVER TOUCHES THE GLOBAL RESOLVER. <see cref="Run"/> repoints
	/// <see cref="Engine_BnkMeshLoader"/> at a bank filesystem it disposes on return, which is
	/// survivable from the launcher but poison mid-boot — the game's own mount must keep the
	/// resolver it installed.
	/// </remarks>
	public static Progress RunOrm(
		string gameDirectory,
		Action<Progress>? report = null,
		CancellationToken cancel = default,
		Lazy<BnkFileSystem>? mount = null)
	{
		if (gameDirectory.Length == 0 || !System.IO.Directory.Exists(gameDirectory))
		{
			return new Progress(0, 0, 0, 0, "no game data");
		}

		if (!Engine_TexSettings.OrmEnabled)
		{
			return new Progress(0, 0, 0, 0, "orm disabled");
		}

		using BnkFileSystem? owned = mount is null ? BnkFileSystem.Mount(gameDirectory) : null;
		BnkFileSystem banks = mount?.Value ?? owned!;
		if (TryReadFastMarker(OrmMarkerPath, banks.FileCount, out int cachedBuilt, out int cachedTotal, out int cachedFailed))
		{
			Progress fast = new(cachedTotal, cachedTotal, cachedBuilt, cachedFailed, string.Empty);
			report?.Invoke(fast);
			return fast;
		}

		Func<string, byte[]?> read =
			path => banks.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null;

		EnumerateOnce(banks, out List<OrmItem> orm);
		int total = orm.Count;

		var done = 0;
		var built = 0;
		var failed = 0;
		string current = string.Empty;

		var options = new System.Threading.Tasks.ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max(1, Engine_TexSettings.Workers),
			CancellationToken = CancellationToken.None,
		};

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
				// One unreadable asset must not end the sweep.
			}

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

				current = item.Specular.Length != 0 ? item.Specular : item.Normal;
			}

			if (report is not null && done % 16 == 0)
			{
				report(Snapshot(done, total, built, failed, current));
			}
		});

		Progress final = Snapshot(done, total, built, failed, current);
		if (!cancel.IsCancellationRequested && total > 0 && final.Failed == 0)
		{
			WriteFastMarker(OrmMarkerPath, banks.FileCount, final.Built, final.Total, final.Failed);
		}
		report?.Invoke(final);
		return final;
	}

	private static readonly object Gate = new();
	private static WeakReference<BnkFileSystem>? _cachedEnumBanks;
	private static List<Item>? _cachedEnumItems;
	private static List<OrmItem>? _cachedEnumOrms;

	private static List<Item> GetOrEnumerate(BnkFileSystem banks, out List<OrmItem> orm)
	{
		lock (Gate)
		{
			if (_cachedEnumBanks is not null &&
				_cachedEnumBanks.TryGetTarget(out BnkFileSystem? target) &&
				ReferenceEquals(target, banks) &&
				_cachedEnumItems is not null &&
				_cachedEnumOrms is not null)
			{
				orm = _cachedEnumOrms;
				return _cachedEnumItems;
			}
		}

		List<Item> items = Enumerate(banks, out orm);
		lock (Gate)
		{
			_cachedEnumBanks = new WeakReference<BnkFileSystem>(banks);
			_cachedEnumItems = items;
			_cachedEnumOrms = orm;
		}

		return items;
	}

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
	private sealed record Enumeration(List<Item> Items, List<OrmItem> Orm);

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BnkFileSystem, Enumeration> Enumerations = new();

	/// <summary>The work list of a mount, read once: it opens every model in the game.</summary>
	private static List<Item> EnumerateOnce(BnkFileSystem banks, out List<OrmItem> orm)
	{
		Enumeration enumeration = Enumerations.GetValue(banks, mounted =>
		{
			List<Item> items = Enumerate(mounted, out List<OrmItem> found);
			return new Enumeration(items, found);
		});
		orm = enumeration.Orm;
		return enumeration.Items;
	}

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
			// UI ART BY PATH. Nothing references it the way a mesh references its albedo — a
			// .bgf names its own pictures — so the material-slot walk below reaches none of it.
			// Without this the UI is enhanced only lazily, by Engine_TexLibrary.TrackUi as each
			// screen opens.
			if (IsUiArt(file.VirtualPath))
			{
				Add(file.VirtualPath, TexSemantic.Ui);
				continue;
			}

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

					// A normal map alone still packs an ORM (AO and height derive from it), so
					// the prebuild sweeps the same set the live path now builds.
					if (!Engine_TexSettings.OrmEnabled ||
						(string.IsNullOrWhiteSpace(material.Specular) &&
						string.IsNullOrWhiteSpace(material.Normal)))
					{
						continue;
					}

					var pair = new OrmItem(
						material.Specular ?? string.Empty,
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

		// UI FIRST: it is on screen during the prebuild itself. OrderBy, not List.Sort — the
		// latter is an unstable introsort and would scramble the bank order of the rest.
		return items.OrderBy(item => item.Semantic == TexSemantic.Ui ? 0 : 1).ToList();
	}

	/// <summary>
	/// Is this the art a Gameface screen, a book or the HUD draws from?
	/// </summary>
	/// <remarks>
	/// By directory, matching how Engine_BnkTextureLoader binds TexSemantic.Ui for the whole
	/// <c>game://</c> route — the same statement, made ahead of the load rather than during it.
	/// </remarks>
	private static bool IsUiArt(string virtualPath) =>
		virtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) &&
		virtualPath.Replace('\\', '/').StartsWith("art/gui/", StringComparison.OrdinalIgnoreCase);
}
