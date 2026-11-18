using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace Refii.Textures;

/// <summary>
/// How soon a texture's enhancement is wanted. §7 of the modernization plan.
/// </summary>
/// <remarks>
/// Lower is sooner, and the ordering is by what the player is looking at rather than by what
/// the loader happened to reach first. A region build enumerates thousands of props in bank
/// order, which correlates with nothing on screen.
/// </remarks>
public enum TexPriority
{
	/// <summary>HUD, menus, and front-end artwork that is on screen now.</summary>
	Ui = 0,

	/// <summary>Materials of the scene being rendered.</summary>
	Scene = 1,

	/// <summary>Referenced by the current level but not yet visible.</summary>
	Level = 2,

	/// <summary>Speculative.</summary>
	Background = 3,
}

/// <summary>
/// The one way a Fable 2 texture becomes a Godot texture.
/// </summary>
/// <remarks>
/// Three tiers, in order: a live map of what this process has already built, the persistent
/// <see cref="Engine_TexCache"/> under <c>user://</c>, and finally the decoder. Every caller
/// gets the same three, which is the point — the material path used to own a private
/// <c>Dictionary</c> that died with the process while the UI path went through the
/// <c>game://</c> loader, so the same <c>.tex</c> reached the screen two different ways and
/// only one of them could ever be made faster.
///
/// Enhancement is separate from resolution. <see cref="Get"/> always returns something usable
/// immediately — the decoded original — and never waits on a model. <see cref="RequestEnhanced"/>
/// is the §6 path: it returns at once and does the work on a background worker in priority order.
///
/// The swap is in place. When an enhancement finishes, the same <see cref="ImageTexture"/> the
/// caller was already given has its image replaced, on the main thread. Nothing has to register a
/// callback, hold a weak reference to a material, or be walked to be updated — every material and
/// every node already pointing at that texture shows the new one because it is the same object.
/// That is also what makes <see cref="ApplyEnhancementSetting"/> able to put the world back the
/// way it was when a player turns the setting off mid-session.
/// </remarks>
public static class Engine_TexLibrary
{
	/// <summary>What has been decided about one texture's enhancement.</summary>
	private enum EnhancementState
	{
		/// <summary>Not attempted, or reverted. A candidate.</summary>
		None = 0,

		/// <summary>Attempted and produced nothing. Never retried.</summary>
		Declined = 1,

		/// <summary>Currently showing an enhanced image.</summary>
		Enhanced = 2,
	}

	/// <summary>Decodes that returned nothing this run (each retry counts again).</summary>
	public static long FailedDecodes;

	private const long FailedRetrySeconds = 5;

	/// <summary>When a failed texture may be tried again, by memory key. Guarded by Gate.</summary>
	private static readonly Dictionary<string, long> FailedUntil = new();

	private sealed class Entry
	{
		/// <summary>Held strongly for the material path, which owns its textures.</summary>
		public ImageTexture? Texture;

		/// <summary>
		/// Held weakly for the <c>game://</c> path, which does not.
		/// </summary>
		/// <remarks>
		/// Godot's ResourceLoader caches those textures and frees them, so a strong reference here
		/// would pin every menu frame and font page for the life of the process behind Godot's
		/// back — memory unreachable from anything that reports on it. A weak one lets the
		/// enhancement land if the texture is still alive and quietly do nothing if it is not.
		/// </remarks>
		public WeakReference<ImageTexture>? WeakTexture;

		/// <summary>Whichever reference is in use, if it is still alive.</summary>
		public ImageTexture? Target
		{
			get
			{
				if (Texture is not null)
				{
					return Texture;
				}

				return WeakTexture is not null && WeakTexture.TryGetTarget(out ImageTexture? live)
					? live
					: null;
			}
		}

		public bool HasAlpha;

		public EnhancementState State;

		public long Version;

		public long ApproxBytes;

		public long LastAccess;

		public ulong TracedRidId;

		/// <summary>Everything needed to redo, or undo, the enhancement without a caller.</summary>
		public required string Path { get; init; }

		public required TexSemantic Semantic { get; init; }

		public required Func<string, byte[]?> Read { get; init; }
	}

	private enum JobKind
	{
		Enhance = 0,
		Revert = 1,
	}

	private sealed class Job
	{
		public required string MemKey { get; init; }

		public required Entry Entry { get; init; }

		public required JobKind Kind { get; init; }
	}

	private static readonly Dictionary<string, Entry> Live = new(StringComparer.Ordinal);

	private static readonly Dictionary<string, Job> InFlight = new(StringComparer.Ordinal);

	private static readonly PriorityQueue<Job, long> Queue = new();

	private static readonly ConcurrentQueue<Action> Completions = new();

	private static readonly object Gate = new();

	private static readonly Dictionary<string, object> ComposedOrmFlights =
		new(StringComparer.Ordinal);

	private sealed class SingleFlightBox<T>
	{
		public readonly ManualResetEventSlim Done = new(false);
		public T? Value;
		public Exception? Error;
	}

	internal sealed record SharedOrmPayload(
		byte[] Data, int Width, int Height, bool UseMipmaps, Image.Format Format, bool HasHeight);

	/// <summary>
	/// Deduplicates concurrent CPU work for identical <paramref name="key"/> so only one leader
	/// executes <paramref name="producer"/> while concurrent followers await its result.
	/// </summary>
	internal static T RunKeyedSingleFlight<T>(
		object gate,
		Dictionary<string, object> flights,
		string key,
		Func<T> producer,
		out bool wasLeader)
	{
		SingleFlightBox<T>? waiter = null;
		SingleFlightBox<T>? ownBox = null;
		lock (gate)
		{
			if (flights.TryGetValue(key, out object? existing) && existing is SingleFlightBox<T> active)
			{
				waiter = active;
			}
			else
			{
				ownBox = new SingleFlightBox<T>();
				flights[key] = ownBox;
			}
		}

		if (waiter is not null)
		{
			wasLeader = false;
			waiter.Done.Wait();
			if (waiter.Error is not null)
			{
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(waiter.Error).Throw();
			}

			return waiter.Value!;
		}

		wasLeader = true;
		try
		{
			T result = producer();
			ownBox!.Value = result;
			return result;
		}
		catch (Exception ex)
		{
			ownBox!.Error = ex;
			throw;
		}
		finally
		{
			lock (gate)
			{
				flights.Remove(key);
			}

			ownBox!.Done.Set();
		}
	}

	internal static Texture2D? TryPeekLiveTexture(string virtualPath, TexSemantic semantic)
	{
		if (string.IsNullOrWhiteSpace(virtualPath))
		{
			return null;
		}

		string memKey = MemKey(Normalise(virtualPath), semantic);
		lock (Gate)
		{
			if (Live.TryGetValue(memKey, out Entry? entry) && entry.Texture is { } tex)
			{
				entry.LastAccess = NextAccess();
				return tex;
			}
		}

		return null;
	}

	private static long _sequence;

	private static long _accessClock;

	private static bool _workersStarted;

	private static long NextAccess() => Interlocked.Increment(ref _accessClock);

	/// <summary>
	/// Selects the least-recently-used cache keys to evict so remaining bytes stay within
	/// <paramref name="budgetBytes"/>. Pure helper for deterministic unit tests and swap trimming.
	/// </summary>
	internal static List<string> SelectLruKeysToEvict(
		IReadOnlyList<(string Key, long Bytes, long LastAccess)> items,
		long budgetBytes)
	{
		var toEvict = new List<string>();
		if (items is null || items.Count == 0)
		{
			return toEvict;
		}

		if (budgetBytes <= 0)
		{
			for (int i = 0; i < items.Count; i++)
			{
				toEvict.Add(items[i].Key);
			}

			return toEvict;
		}

		long totalBytes = 0;
		for (int i = 0; i < items.Count; i++)
		{
			totalBytes += Math.Max(0L, items[i].Bytes);
		}

		if (totalBytes <= budgetBytes)
		{
			return toEvict;
		}

		var ordered = new List<(string Key, long Bytes, long LastAccess)>(items);
		ordered.Sort(static (a, b) => a.LastAccess.CompareTo(b.LastAccess));
		for (int i = 0; i < ordered.Count && totalBytes > budgetBytes; i++)
		{
			toEvict.Add(ordered[i].Key);
			totalBytes -= Math.Max(0L, ordered[i].Bytes);
		}

		return toEvict;
	}

	/// <summary>
	/// Prepared CPU-side result for <see cref="PrepareGet"/> so multiple textures of a material
	/// can be decoded off-thread first and uploaded together inside one main-thread <c>RunGpuUpload</c>.
	/// </summary>
	internal sealed class PreparedTexture : IDisposable
	{
		public Texture2D? Ready { get; private set; }

		public bool HasAlpha { get; }

		public long EstimatedBytes => _cpuImage is null || _cpuImage.IsEmpty() ? 0L : Math.Max(4096L, _cpuImage.GetDataSize());

		private Image? _cpuImage;
		private readonly string? _memKey;
		private readonly string? _path;
		private readonly TexSemantic _semantic;
		private readonly Func<string, byte[]?>? _read;
		private readonly bool _enhanced;
		private Engine_TextureTemporaries? _temporaries;
		private bool _disposed;

		internal PreparedTexture(Texture2D? ready, bool hasAlpha)
		{
			Ready = ready;
			HasAlpha = hasAlpha;
		}

		internal PreparedTexture(
			Image cpuImage,
			bool hasAlpha,
			string memKey,
			string path,
			TexSemantic semantic,
			Func<string, byte[]?> read,
			bool enhanced,
			Engine_TextureTemporaries temporaries)
		{
			_cpuImage = cpuImage;
			HasAlpha = hasAlpha;
			_memKey = memKey;
			_path = path;
			_semantic = semantic;
			_read = read;
			_enhanced = enhanced;
			_temporaries = temporaries;
		}

		/// <summary>Commits the prepared texture on the main thread and returns the live <see cref="Texture2D"/>.</summary>
		public Texture2D? CommitOnMainThread()
		{
			if (Ready is not null || _cpuImage is null || _cpuImage.IsEmpty() || _memKey is null)
			{
				return Ready;
			}

			lock (Gate)
			{
				if (Live.TryGetValue(_memKey, out Entry? existing))
				{
					existing.LastAccess = NextAccess();
					Ready = existing.Texture;
					Dispose();
					return Ready;
				}
			}

			ImageTexture texture = Engine_TextureUpload.CreateTextureOnMainThread(_cpuImage, world: true);
			long dataBytes = Math.Max(256L, _cpuImage.GetDataSize());
			ulong tracedId = texture.GetRid().Id;

			lock (Gate)
			{
				if (Live.TryGetValue(_memKey, out Entry? raced))
				{
					raced.LastAccess = NextAccess();
					texture.Dispose();
					Ready = raced.Texture;
					Dispose();
					return Ready;
				}

				FailedUntil.Remove(_memKey);
				Held(texture, _cpuImage);
				var createdEntry = new Entry
				{
					Texture = texture,
					HasAlpha = HasAlpha,
					Path = _path!,
					Semantic = _semantic,
					Read = _read!,
					State = _enhanced ? EnhancementState.Enhanced : EnhancementState.None,
					ApproxBytes = dataBytes,
					LastAccess = NextAccess(),
					TracedRidId = tracedId,
				};
				Live[_memKey] = createdEntry;
				LibraryEntries.AddOrUpdate(texture, createdEntry);
			}

			Ready = texture;
			Dispose();
			return Ready;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_cpuImage = null;
			_temporaries?.Dispose();
			_temporaries = null;
		}
	}

	internal static PreparedTexture PrepareGet(
		string virtualPath, TexSemantic semantic, Func<string, byte[]?> read)
	{
		if (string.IsNullOrWhiteSpace(virtualPath))
		{
			return new PreparedTexture(null, false);
		}

		string path = Normalise(virtualPath);
		string memKey = MemKey(path, semantic);

		lock (Gate)
		{
			if (Live.TryGetValue(memKey, out Entry? cached))
			{
				cached.LastAccess = NextAccess();
				return new PreparedTexture(cached.Texture, cached.HasAlpha);
			}

			if (FailedUntil.TryGetValue(memKey, out long retryAt) &&
				System.Diagnostics.Stopwatch.GetTimestamp() < retryAt)
			{
				return new PreparedTexture(null, false);
			}
		}

		var temporary = new Engine_TextureTemporaries();
		try
		{
			byte[]? source = null;
			Image? image = temporary.Own(AndroidTexUploadCache.TryServe(path, read, out AndroidTexSourceKeys.Ticket upload, out bool decodedAlpha));
			image ??= temporary.Own(Decode(path, semantic, read, out decodedAlpha, out source));

			if (image is null)
			{
				temporary.Dispose();
				lock (Gate)
				{
					FailedDecodes++;
					FailedUntil[memKey] = System.Diagnostics.Stopwatch.GetTimestamp() +
						System.Diagnostics.Stopwatch.Frequency * FailedRetrySeconds;
				}

				return new PreparedTexture(null, false);
			}

			bool enhanced = false;
			if (image is not null && source is not null && Engine_TexEnhance.Enabled &&
				Engine_TexSemantic.For(semantic).Filter != TexFilterClass.None &&
				EnhanceDecoded(source, image, semantic, decodedAlpha, path, compress: true, cacheOnly: true)
					is { } generated)
			{
				image = temporary.Own(generated);
				enhanced = true;
			}

			image = temporary.Own(AndroidTexUploadCache.Remember(upload, image, decodedAlpha));
			if (image is null || image.IsEmpty())
			{
				temporary.Dispose();
				return new PreparedTexture(null, decodedAlpha);
			}

			return new PreparedTexture(image, decodedAlpha, memKey, path, semantic, read, enhanced, temporary);
		}
		catch
		{
			temporary.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Resolves a texture, decoding it only if neither cache has it.
	/// </summary>
	/// <remarks>
	/// <paramref name="read"/> is passed in rather than resolved here because the two callers
	/// reach the banks by different routes — the material path through
	/// <see cref="Refii.Models.Engine_BnkMeshLoader"/>, the <c>game://</c> loader through its own
	/// resolver — and unifying those is a separate change from unifying the cache.
	///
	/// A miss is remembered. A material naming a texture that is not in any bank asks for it
	/// once per surface that uses it, and without the negative entry each of those is a fresh
	/// failed bank walk.
	/// </remarks>
	public static Texture2D? Get(
		string virtualPath, TexSemantic semantic, Func<string, byte[]?> read, out bool hasAlpha)
	{
		using PreparedTexture prepared = PrepareGet(virtualPath, semantic, read);
		hasAlpha = prepared.HasAlpha;
		if (prepared.Ready is not null || prepared.EstimatedBytes == 0)
		{
			return prepared.Ready;
		}

		return Refii.Core.Engine_ServerSyncs.RunGpuUpload(
			prepared.EstimatedBytes, () => prepared.CommitOnMainThread());
	}

	/// <summary>
	/// Asks for an enhanced version of a texture <see cref="Get"/> has already returned.
	/// </summary>
	/// <remarks>
	/// Returns immediately and always, and takes no callback — the result lands in the texture
	/// the caller is already holding. Does nothing at all when the player has not asked for
	/// enhancement, when the semantic's policy declines it, or in the editor.
	/// </remarks>
	public static void RequestEnhanced(
		string virtualPath,
		TexSemantic semantic,
		TexPriority priority,
		Func<string, byte[]?> read)
	{
		if (!Engine_TexEnhance.Enabled || string.IsNullOrWhiteSpace(virtualPath))
		{
			return;
		}

		if (Engine_TexSemantic.For(semantic).Filter == TexFilterClass.None)
		{
			return;
		}

		string memKey = MemKey(Normalise(virtualPath), semantic);

		lock (Gate)
		{
			if (!Live.TryGetValue(memKey, out Entry? entry) ||
				entry.Target is null ||
				entry.State != EnhancementState.None)
			{
				return;
			}

			Enqueue(memKey, entry, JobKind.Enhance, priority);
		}
	}

	/// <summary>
	/// Turns enhancement on or off for everything already on screen.
	/// </summary>
	/// <remarks>
	/// This IS what makes the setting A Switch rather than A Startup flag. Clearing the backend
	/// alone would only stop future work; every texture already swapped in would keep its
	/// enhanced image until something happened to rebuild it, so "off" would not visibly mean
	/// off until the next level change.
	///
	/// Turning it back on is cheap after the first time — the results are on disk, so the
	/// workers re-apply from the cache instead of re-running the backend.
	///
	/// The revert reloads the original from the cache rather than keeping it in memory. Holding
	/// the base <see cref="Image"/> of every texture alive against the chance that somebody
	/// toggles a setting would roughly double the resident texture memory of a region for the
	/// whole session.
	/// </remarks>
	public static void ApplyEnhancementSetting(bool enabled)
	{
		var queued = 0;
		lock (Gate)
		{
			foreach ((string memKey, Entry entry) in Live)
			{
				if (entry.Target is null)
				{
					continue;
				}

				if (enabled && entry.State == EnhancementState.None)
				{
					// Ui first: it is what the player is looking at while they change a setting.
					Enqueue(
						memKey, entry, JobKind.Enhance,
						entry.Semantic == TexSemantic.Ui ? TexPriority.Ui : TexPriority.Scene);
					++queued;
				}
				else if (!enabled && entry.State == EnhancementState.Enhanced)
				{
					Enqueue(memKey, entry, JobKind.Revert, TexPriority.Ui);
					++queued;
				}
			}
		}

		GD.Print($"[tex] enhancement {(enabled ? "on" : "off")}: {queued:N0} texture(s) queued");
	}

	/// <summary>
	/// Applies finished work. Call once per frame from the main thread.
	/// </summary>
	/// <returns>How many were applied.</returns>
	/// <remarks>
	/// Budgeted. A warm cache can complete hundreds of entries in the time one frame takes, and
	/// applying all of them in that frame trades a background cost for a visible hitch — which is
	/// the exact thing §6 exists to avoid.
	/// </remarks>
	private const long MaxSyncUiEnhancedBytesPerFrame = 16L * 1024 * 1024;
	private const long MaxPumpBytesPerFrame = 16L * 1024 * 1024;
	private static long _syncUiEnhancedBytesThisFrame;
	private static long _pumpedBytesThisFrame;

	public static int Pump(int budget = 2)
	{
		Interlocked.Exchange(ref _syncUiEnhancedBytesThisFrame, 0);
		Interlocked.Exchange(ref _pumpedBytesThisFrame, 0);
		int applied = 0;
		long startStamp = System.Diagnostics.Stopwatch.GetTimestamp();
		long maxTicks = (long)(1.5 * (System.Diagnostics.Stopwatch.Frequency / 1000.0));
		bool enforceTimeBudget = budget <= 8;
		while (applied < budget && Completions.TryDequeue(out Action? action))
		{
			try
			{
				action();
			}
			catch (Exception e)
			{
				GD.PushWarning($"[tex] apply failed: {e.Message}");
			}

			++applied;
			if (enforceTimeBudget && (
				(System.Diagnostics.Stopwatch.GetTimestamp() - startStamp) >= maxTicks ||
				Interlocked.Read(ref _pumpedBytesThisFrame) >= MaxPumpBytesPerFrame))
			{
				break;
			}
		}

		return applied;
	}

	/// <summary>Drops the live maps. The disk cache is untouched.</summary>
	/// <remarks>
	/// BOTH maps. The cube map was added as a second dictionary and left out of this, which pinned
	/// every environment cube for the life of the process and — more sharply — made a cache test
	/// pass while reading from memory: the "warm" lookup returned a live entry, so the disk path
	/// it was meant to exercise never ran and reported zero hits.
	/// </remarks>
	public static void Clear()
	{
		lock (Gate)
		{
			Live.Clear();
			LiveCubes.Clear();
			LiveOrm.Clear();
			FailedUntil.Clear();
			ComposedOrms.Clear();
			ComposedOrmsOverLimit.Clear();
		}

		FlagsByPath.Clear();
		Refii.Core.Engine_TracedMemory.FreeAll(TracedPool);
	}

	/// <summary>
	/// Drops transient failure/composite maps and trims live textures and ORMs down to
	/// <paramref name="maxBytes"/> on a least-recently-used basis so repeated hub swaps on
	/// desktop keep hot shared assets resident without unbounded VRAM growth.
	/// </summary>
	public static void TrimToBudget(long maxBytes = 768L * 1024L * 1024L)
	{
		if (maxBytes <= 0)
		{
			Clear();
			return;
		}

		var freedIds = new List<ulong>();
		lock (Gate)
		{
			FailedUntil.Clear();
			ComposedOrms.Clear();
			ComposedOrmsOverLimit.Clear();

			var items = new List<(string Key, long Bytes, long LastAccess)>(Live.Count + LiveOrm.Count);
			foreach ((string memKey, Entry entry) in Live)
			{
				if (entry.Texture is not null)
				{
					items.Add(("T:" + memKey, entry.ApproxBytes, entry.LastAccess));
				}
			}

			foreach ((string ormKey, OrmEntry ormEntry) in LiveOrm)
			{
				if (ormEntry.Texture is not null)
				{
					items.Add(("O:" + ormKey, ormEntry.ApproxBytes, ormEntry.LastAccess));
				}
			}

			List<string> evict = SelectLruKeysToEvict(items, maxBytes);
			for (int i = 0; i < evict.Count; i++)
			{
				string tagged = evict[i];
				if (tagged.StartsWith("T:", StringComparison.Ordinal))
				{
					string k = tagged[2..];
					if (Live.Remove(k, out Entry? removed))
					{
						if (removed.TracedRidId != 0)
						{
							freedIds.Add(removed.TracedRidId);
						}

						if (removed.Texture is { } tex)
						{
							CpuImages.Remove(tex);
							LibraryEntries.Remove(tex);
						}
					}
				}
				else if (tagged.StartsWith("O:", StringComparison.Ordinal))
				{
					string k = tagged[2..];
					if (LiveOrm.Remove(k, out OrmEntry? removedOrm) && removedOrm.TracedRidId != 0)
					{
						freedIds.Add(removedOrm.TracedRidId);
					}
				}
			}
		}

		for (int i = 0; i < freedIds.Count; i++)
		{
			Refii.Core.Engine_TracedMemory.Free(TracedPool, freedIds[i]);
		}
	}

	/// <summary>
	/// Decodes <paramref name="virtualPaths"/> again through each live entry's own reader and puts
	/// the image into the texture already handed out, in place, so every material and menu that
	/// holds it shows the new bytes. The ORM packs and cubes built from those paths, and their
	/// remembered failures, are dropped and rebuilt on their next request; a material keeps the
	/// pack it already has. Main thread: it uploads. Returns the textures refreshed.
	/// </summary>
	public static List<ImageTexture> Refresh(IEnumerable<string> virtualPaths)
	{
		// Ignoring case: a material names its texture as the record spells it, the mount as the
		// bank does, and both resolve to the same file.
		var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string path in virtualPaths)
		{
			if (!string.IsNullOrWhiteSpace(path))
			{
				wanted.Add(Normalise(path));
			}
		}

		var refreshed = new List<ImageTexture>();
		if (wanted.Count == 0)
		{
			return refreshed;
		}

		bool Names(string key)
		{
			foreach (string path in wanted)
			{
				if (key.StartsWith(path + " ", StringComparison.OrdinalIgnoreCase) ||
					key.EndsWith("-" + path, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		var targets = new List<Entry>();
		lock (Gate)
		{
			foreach (Entry entry in Live.Values)
			{
				if (wanted.Contains(entry.Path))
				{
					targets.Add(entry);
				}
			}

			var dropped = new List<string>();
			foreach (string key in FailedUntil.Keys)
			{
				if (Names(key))
				{
					dropped.Add(key);
				}
			}

			foreach (string key in dropped)
			{
				FailedUntil.Remove(key);
			}

			dropped.Clear();
			foreach (string key in LiveOrm.Keys)
			{
				if (Names(key))
				{
					dropped.Add(key);
				}
			}

			foreach (string key in dropped)
			{
				LiveOrm.Remove(key);
			}

			dropped.Clear();
			foreach (string key in LiveCubes.Keys)
			{
				if (wanted.Contains(key))
				{
					dropped.Add(key);
				}
			}

			foreach (string key in dropped)
			{
				LiveCubes.Remove(key);
			}
		}

		foreach (Entry entry in targets)
		{
			if (entry.Target is not { } texture)
			{
				continue;
			}

			Image? image = Decode(entry.Path, entry.Semantic, entry.Read, out bool hasAlpha);
			if (image is null || image.IsEmpty())
			{
				continue;
			}

			Engine_TextureUpload.SetImage(texture, image, world: true, allowD3D12Replace: false);
			lock (Gate)
			{
				entry.HasAlpha = hasAlpha;
				entry.State = EnhancementState.None;
				Interlocked.Increment(ref entry.Version);
			}

			refreshed.Add(texture);
			RequestEnhanced(entry.Path, entry.Semantic,
				entry.Semantic == TexSemantic.Ui ? TexPriority.Ui : TexPriority.Scene, entry.Read);
		}

		return refreshed;
	}

	/// <summary>The in-game Tracy capture's name for what this cache holds strongly.</summary>
	private const string TracedPool = "texture cache";

	/// <summary>A texture the cache now holds, for Tracy's memory view: its RID and decoded size.</summary>
	private static void Held(ImageTexture? texture, Image? image)
	{
		if (texture is not null && image is not null)
		{
			Refii.Core.Engine_TracedMemory.Alloc(TracedPool, texture.GetRid().Id, (ulong)image.GetDataSize());
		}
	}

	private static long _lastReported = -1;

	/// <summary>
	/// Reports only when the counters have actually moved since the last time.
	/// </summary>
	/// <remarks>
	/// Called on a frame interval so a run that ends in a crash or a kill still leaves evidence,
	/// rather than only a clean exit doing so — and gated on change so a settled session does not
	/// print the same line forever.
	///
	/// The first version of this cache shipped printing nothing on a good run. It moved 359 MB
	/// through 1,385 entries on its first real session and the log had not one line about it, so
	/// "the cache worked" was a claim with no evidence behind it either way.
	/// </remarks>
	public static void ReportIfChanged()
	{
		long fingerprint = Engine_TexCache.Hits + Engine_TexCache.Misses + Engine_TexCache.Stores;
		if (fingerprint == _lastReported)
		{
			return;
		}

		_lastReported = fingerprint;
		Report();
	}

	/// <summary>Live entries, queued jobs, and the disk cache's own line.</summary>
	public static void Report()
	{
		int live;
		int queued;
		int enhanced;
		lock (Gate)
		{
			live = Live.Count;
			queued = Queue.Count;
			enhanced = 0;
			foreach (Entry entry in Live.Values)
			{
				if (entry.State == EnhancementState.Enhanced)
				{
					++enhanced;
				}
			}
		}

		// THE ORM AND CUBE MAPS ARE COUNTED TOO. They are separate dictionaries with the same
		// process lifetime the main one had, and the ORM set was the one that gave the leak
		// away: 1,914 of the game's 1,935 packed maps resident at once is not a working set,
		// it is every region ever visited. A count that never falls across a level change means
		// `Engine_LevelCaches.ReleaseRegion` is not running.
		int orm;
		int cubes;
		lock (Gate)
		{
			orm = LiveOrm.Count;
			cubes = LiveCubes.Count;
		}

		GD.Print(
			$"[tex] library {live:N0} live, {enhanced:N0} enhanced, {orm:N0} orm, " +
			$"{cubes:N0} cube(s), {queued:N0} queued, " +
			$"{System.Threading.Volatile.Read(ref _compressFailures):N0} uncompressed " +
			$"(BC unavailable), backend {Engine_TexEnhance.BackendId}");
		Engine_TexCache.Report();
		AndroidTexSourceKeys.Report();
		AndroidTexUploadCache.Report();
	}

	/// <summary>
	/// Tiers two and three only: the disk cache, then the decoder. No live map.
	/// </summary>
	/// <remarks>
	/// THE <c>game://</c> Loader uses this one, not <see cref="Get"/>, and the difference is
	/// lifetime. Godot's ResourceLoader already caches what it loads, and it also frees it —
	/// putting those textures in the live map as well would pin every menu frame and every
	/// font page for the life of the process behind Godot's back, and the memory would be
	/// unreachable from anything that reports on it.
	///
	/// It also means the loader NEVER gets an enhanced texture, which is what the editor's
	/// viewers want: they exist to show what is in the player's game data, and a resample of an
	/// asset is a picture of our pipeline rather than of the asset.
	/// </remarks>
	public static Image? Decode(
		string virtualPath, TexSemantic semantic, Func<string, byte[]?> read, out bool hasAlpha) =>
		Decode(virtualPath, semantic, read, out hasAlpha, out _);

	/// <summary>The same decode, handing back the bytes it was decoded from: they are what an enhancement is keyed on.</summary>
	private static Image? Decode(
		string virtualPath, TexSemantic semantic, Func<string, byte[]?> read, out bool hasAlpha,
		out byte[]? source)
	{
		hasAlpha = false;
		source = null;

		string path = Normalise(virtualPath);

		if (AndroidTexSourceKeys.TryServe(path, read, out AndroidTexSourceKeys.Ticket ticket, out hasAlpha) is { } known)
		{
			return known;
		}

		try
		{
			source = read(path);
		}
		catch (Exception e)
		{
			// A bank read can throw on a pairing fault; without this the caller only sees
			// Godot's bare "Cannot open file".
			GD.PushWarning($"[tex] {path}: read threw {e.GetType().Name}: {e.Message}");
			return null;
		}

		if (source is null)
		{
			GD.PushWarning($"[tex] not found: {path}");
			return null;
		}

		// The Decoded original, keyed on the source alone. It is a different artefact from any
		// enhancement of it and has to survive a backend being registered, swapped or turned off
		// — and it is the same bytes whichever slot asked, so the semantic stays out of the key.
		string key = Engine_TexCache.BaseKey(source);
		AndroidTexSourceKeys.Learn(ticket, key, source);

		Image? cached = Engine_TexCache.TryLoad(key, out hasAlpha);
		if (cached is not null)
		{
			return cached;
		}

		Image? image;
		try
		{
			image = Engine_TexImage.TryCreateImage(source, out hasAlpha, out string error);
			if (image is null)
			{
				GD.PushWarning($"[tex] {path}: {error}");
				return null;
			}
		}
		catch (Exception e)
		{
			GD.PushWarning($"[tex] {path}: {e.Message}");
			return null;
		}

		Engine_TexCache.Store(key, image, hasAlpha);
		return image;
	}

	/// <summary>
	/// Decodes a <c>game://</c> UI texture, preferring an already-cached enhanced image on disk
	/// when running in-game so the texture is uploaded to the GPU once at its final resolution
	/// instead of uploading 1x and immediately destroying/reallocating via <c>SetImage</c>.
	/// </summary>
	public static Image? DecodeUi(
		string virtualPath, Func<string, byte[]?> read, out bool enhanced, out Vector2I baseSize)
	{
		enhanced = false;
		baseSize = Vector2I.One;
		string path = Normalise(virtualPath);

		TexPolicy uiPolicy = Engine_TexSemantic.For(TexSemantic.Ui);
		if (!Engine.IsEditorHint() && Engine_TexEnhance.Enabled &&
			Engine_TexEnhance.Backend is { } backend &&
			uiPolicy.Filter != TexFilterClass.None)
		{
			byte[]? earlySource = null;
			try
			{
				earlySource = read(path);
			}
			catch (Exception)
			{
				earlySource = null;
			}

			if (earlySource is not null)
			{
				string baseKey = Engine_TexCache.BaseKey(earlySource);
				if (Engine_TexCache.TryReadHeader(baseKey, out int baseW, out int baseH, out _) &&
					baseW > 0 && baseH > 0)
				{
					baseSize = new Vector2I(baseW, baseH);
					int scale = Engine_TexSemantic.TargetScale(baseW, baseH, uiPolicy);
					if (scale > 1)
					{
						string enhKey = Engine_TexCache.Key(earlySource, TexSemantic.Ui, backend.Id, scale);
						if (Engine_TexCache.TryLoad(enhKey, out _) is { } cachedEnhanced)
						{
							Interlocked.Add(ref _syncUiEnhancedBytesThisFrame, cachedEnhanced.GetDataSize());
							enhanced = true;
							return cachedEnhanced;
						}
					}
				}
			}
		}

		Image? image = Decode(path, TexSemantic.Ui, read, out bool decodedAlpha, out byte[]? source);
		if (image is null)
		{
			return null;
		}

		baseSize = new Vector2I(Math.Max(image.GetWidth(), 1), Math.Max(image.GetHeight(), 1));
		if (source is not null && !Engine.IsEditorHint() && Engine_TexEnhance.Enabled &&
			uiPolicy.Filter != TexFilterClass.None &&
			EnhanceDecoded(source, image, TexSemantic.Ui, decodedAlpha, path, compress: true, cacheOnly: true)
				is { } generated)
		{
			Interlocked.Add(ref _syncUiEnhancedBytesThisFrame, generated.GetDataSize());
			image.Dispose();
			image = generated;
			enhanced = true;
		}

		return image;
	}

	/// <summary>
	/// Registers a <c>game://</c> texture so §7's priority-0 UI can be enhanced too.
	/// </summary>
	/// <remarks>
	/// The plan puts the HUD, the menus and the front end at the TOP of the queue, and they were
	/// the one path that could never be enhanced at all — the loader hands its texture to Godot
	/// and forgets it, so there was nothing to swap.
	///
	/// Tracked weakly rather than pinned (see <see cref="Entry.WeakTexture"/>). Called on every
	/// load, not just the first, because Godot frees and reloads these: a second load produces a
	/// NEW ImageTexture holding the base image, so the entry has to be re-pointed at it and
	/// re-requested. That re-request is cheap — the enhanced result is already on disk.
	/// </remarks>
	public static void TrackUi(
		string virtualPath, ImageTexture texture, Func<string, byte[]?> read, bool alreadyEnhanced = false)
	{
		if (!Engine_TexEnhance.Enabled || string.IsNullOrWhiteSpace(virtualPath))
		{
			return;
		}

		if (Engine_TexSemantic.For(TexSemantic.Ui).Filter == TexFilterClass.None)
		{
			return;
		}

		string path = Normalise(virtualPath);
		string memKey = MemKey(path, TexSemantic.Ui);

		lock (Gate)
		{
			if (!Live.TryGetValue(memKey, out Entry? entry))
			{
				entry = new Entry
				{
					Path = path,
					Semantic = TexSemantic.Ui,
					Read = read,
				};
				Live[memKey] = entry;
			}

			// NEVER a strong reference on this path, even if an earlier entry had one.
			entry.Texture = null;
			entry.WeakTexture = new WeakReference<ImageTexture>(texture);

			if (alreadyEnhanced)
			{
				entry.State = EnhancementState.Enhanced;
				return;
			}

			// Enhanced means "the instance we last swapped was enhanced", and this is a different
			// instance carrying the base image, so the state no longer describes what is on
			// screen. Declined stays declined — the policy's answer does not change per instance.
			if (entry.State == EnhancementState.Enhanced)
			{
				entry.State = EnhancementState.None;
			}

			if (entry.State == EnhancementState.None)
			{
				Enqueue(memKey, entry, JobKind.Enhance, TexPriority.Ui);
			}
		}
	}

	/// <summary>
	/// Fills the disk cache for one texture without building a <see cref="Texture2D"/>.
	/// </summary>
	/// <remarks>
	/// For the prebuild, which does the whole game up front instead of paying per asset during
	/// play. The expensive half is the decode and the enhancement, and both land in
	/// <see cref="Engine_TexCache"/>; the Texture2D is the cheap half and is the half that has to
	/// happen on the main thread. Building one here would either force the prebuild onto the main
	/// thread or create Godot resources from a worker for no reason — and then immediately drop
	/// them, because nothing is on screen to hold them.
	///
	/// It also deliberately does NOT touch the live map. A prebuilt entry that pinned itself would
	/// leave the whole game's textures resident before the player had loaded a level.
	/// </remarks>
	/// <returns>True when something was decoded or enhanced; false when the source is unreadable.</returns>
	public static bool PrebuildTexture(
		string virtualPath, TexSemantic semantic, Func<string, byte[]?> read)
	{
		if (string.IsNullOrWhiteSpace(virtualPath))
		{
			return false;
		}

		string path = Normalise(virtualPath);

		byte[]? source;
		try
		{
			source = read(path);
		}
		catch (Exception)
		{
			return false;
		}

		if (source is null)
		{
			return false;
		}

		// The base decode first, and it is the half that pays off even with enhancement off — the
		// cache serves it to every later load whatever the player's settings say.
		string baseKey = Engine_TexCache.BaseKey(source);
		// Already built, as nearly every texture is on a boot after the first: no need for the pixels.
		if (Engine_TexCache.TryPeek(baseKey, out int width, out int height) &&
			EnhancementIsCached(source, width, height, semantic))
		{
			return true;
		}

		using Image? original = Engine_TexCache.TryLoad(baseKey, out bool hasAlpha)
			?? TryDecodeAndStoreBase(source, baseKey, out hasAlpha);
		if (original is null)
		{
			return false;
		}

		// EnhanceDecoded is a no-op when the setting is off or the policy declines, and it stores
		// its own result, so dispose whatever temporary Image it returns immediately.
		using Image? enhanced = EnhanceDecoded(source, original, semantic, hasAlpha, path);
		return true;
	}

	private static Image? TryDecodeAndStoreBase(byte[] source, string baseKey, out bool hasAlpha)
	{
		hasAlpha = false;
		Image? original;
		try
		{
			original = Engine_TexImage.TryCreateImage(source, out hasAlpha, out _);
		}
		catch (Exception)
		{
			return null;
		}

		if (original is null)
		{
			return null;
		}

		Engine_TexCache.Store(baseKey, original, hasAlpha);
		return original;
	}

	/// <summary>
	/// Fills the disk cache for one packed ORM texture, likewise without building a texture.
	/// </summary>
	public static bool PrebuildOrm(
		string specularPath,
		string normalPath,
		float powerMin,
		float powerMax,
		Func<string, byte[]?> read)
	{
		if (!Engine_TexSettings.OrmEnabled ||
			(string.IsNullOrWhiteSpace(specularPath) && string.IsNullOrWhiteSpace(normalPath)))
		{
			return false;
		}

		string path = Normalise(specularPath);
		string normal = Normalise(normalPath);
		float aoStrength = Engine_TexSettings.OrmAoStrength;
		float metallicStrength = Engine_TexSettings.OrmMetallicStrength;
		bool withHeight = Engine_TexSettings.OrmHeight;

		// The same identity the live path builds, character for character. A prebuild that keyed
		// its entries even slightly differently would fill the cache with things the game then
		// missed on — the most expensive possible way to achieve nothing.
		string id = $"{Engine_TexOrm.CacheId(powerMin, powerMax)}-ao{aoStrength:F2}-" +
			$"me{metallicStrength:F2}-h{(withHeight ? 1 : 0)}-{normal}";

		byte[]? source = null;
		byte[]? normalBytes = null;
		try
		{
			if (path.Length > 0)
			{
				source = read(path);
			}

			if ((aoStrength > 0.0f || withHeight) && normal.Length > 0)
			{
				normalBytes = read(normal);
			}
		}
		catch (Exception)
		{
			return false;
		}

		byte[]? keySource = source ?? normalBytes;
		if (keySource is null)
		{
			return false;
		}

		string key = Engine_TexCache.Key(keySource, TexSemantic.Orm, id, 2);
		if (Engine_TexCache.TryReadHeader(key, out _, out _, out _))
		{
			return true;
		}

		using Image? built = BuildOrmImage(
			path, normal, id, powerMin, powerMax, aoStrength, metallicStrength, withHeight, read,
			out _);
		return built is not null;
	}


	/// <summary>
	/// Halves a packed ORM before its mip chain is built.
	/// </summary>
	/// <remarks>
	/// EVERY CHANNEL HERE IS A CONTROL SIGNAL, NOT AN IMAGE: occlusion from the source normal's
	/// divergence, roughness from a `sqrt(2/(power+2))` ramp, metallic from a smoothstep mask,
	/// height from a Poisson solve. None carries texture-scale detail, and the shader samples
	/// them beside an albedo that keeps its full size, so the frequency they are stored at is
	/// not the frequency the surface reads at.
	///
	/// Measured on the built cache: 1,856 of 1,935 packed maps take the larger block format
	/// because the height channel varies their alpha, and ~99% of the game's set is resident at
	/// once — so a quarter of the pixels is close to a pure live saving rather than a
	/// whole-set one.
	///
	/// BEFORE the mip chain, so the chain is generated for the size actually stored. A map with
	/// an edge of one has nothing to halve.
	/// </remarks>
	private static void HalvePackedOrm(Image packed)
	{
		int width = packed.GetWidth();
		int height = packed.GetHeight();
		if (width <= 1 || height <= 1)
		{
			return;
		}

		packed.Resize(
			System.Math.Max(1, width / 2),
			System.Math.Max(1, height / 2),
			Image.Interpolation.Bilinear);
	}

	private sealed class OrmEntry
	{
		public ImageTexture? Texture;
		public bool HasHeight;
		public long ApproxBytes;
		public long LastAccess;
		public ulong TracedRidId;
	}

	private static readonly Dictionary<string, OrmEntry> LiveOrm =
		new(StringComparer.Ordinal);

	/// <summary>
	/// Prepared CPU-side ORM result from <see cref="PrepareOrm"/> or <see cref="PrepareComposedOrm"/>
	/// so all maps and ORM upload inside one main-thread <c>RunGpuUpload</c>.
	/// </summary>
	internal sealed class PreparedOrm : IDisposable
	{
		public ImageTexture? Ready { get; private set; }

		public bool HasHeight { get; private set; }

		public bool IsAuthored { get; }

		public long EstimatedBytes =>
			_sharedPayload is not null ? Math.Max(4096L, _sharedPayload.Data.LongLength)
			: _cpuImage is null || _cpuImage.IsEmpty() ? 0L
			: Math.Max(4096L, _cpuImage.GetDataSize());

		private Image? _cpuImage;
		private SharedOrmPayload? _sharedPayload;
		private readonly string? _ormCacheKey;
		private readonly string? _composedByIdentity;
		private readonly string? _composedByContent;
		private readonly bool _cacheNullOrmResult;
		private readonly bool _noteComposedStats;
		private readonly long _composedStartTicks;
		private bool _disposed;

		internal PreparedOrm(ImageTexture? ready, bool hasHeight, bool isAuthored = false)
		{
			Ready = ready;
			HasHeight = hasHeight;
			IsAuthored = isAuthored;
		}

		internal PreparedOrm(
			Image? cpuImage,
			bool hasHeight,
			bool isAuthored,
			string? ormCacheKey,
			bool cacheNullOrmResult = false,
			string? composedByIdentity = null,
			string? composedByContent = null,
			bool noteComposedStats = false,
			long composedStartTicks = 0)
		{
			_cpuImage = cpuImage;
			HasHeight = hasHeight;
			IsAuthored = isAuthored;
			_ormCacheKey = ormCacheKey;
			_cacheNullOrmResult = cacheNullOrmResult;
			_composedByIdentity = composedByIdentity;
			_composedByContent = composedByContent;
			_noteComposedStats = noteComposedStats;
			_composedStartTicks = composedStartTicks;
		}

		internal PreparedOrm(
			SharedOrmPayload sharedPayload,
			string? composedByIdentity,
			string composedByContent,
			bool noteComposedStats,
			long composedStartTicks)
		{
			_sharedPayload = sharedPayload;
			HasHeight = sharedPayload.HasHeight;
			_composedByIdentity = composedByIdentity;
			_composedByContent = composedByContent;
			_noteComposedStats = noteComposedStats;
			_composedStartTicks = composedStartTicks;
		}

		public ImageTexture? CommitOnMainThread()
		{
			if (Ready is not null)
			{
				return Ready;
			}

			if (_composedByContent is not null)
			{
				lock (Gate)
				{
					if (TryComposedLocked(_composedByContent, out ImageTexture? raced, out bool racedHeight))
					{
						Interlocked.Increment(ref Engine_TexLibrary._composedByContent);
						if (_composedByIdentity is not null)
						{
							KeepLocked(_composedByIdentity, raced, racedHeight);
						}

						if (_noteComposedStats)
						{
							Refii.Entities.Engine_LoadEntityBuilds.NoteOrm(reused: true, 0);
						}

						Ready = raced;
						HasHeight = racedHeight;
						Dispose();
						return Ready;
					}
				}

				if (_sharedPayload is null && (_cpuImage is null || _cpuImage.IsEmpty()))
				{
					return null;
				}

				Image? createdFromPayload = _sharedPayload is not null
					? Image.CreateFromData(
						_sharedPayload.Width, _sharedPayload.Height, _sharedPayload.UseMipmaps, _sharedPayload.Format, _sharedPayload.Data)
					: null;
				Image uploadSource = createdFromPayload ?? _cpuImage!;
				ImageTexture created;
				try
				{
					created = Engine_TextureUpload.CreateTextureOnMainThread(uploadSource);
				}
				finally
				{
					createdFromPayload?.Dispose();
				}

				bool height = HasHeight;
				bool storedFresh = false;
				lock (Gate)
				{
					if (TryComposedLocked(_composedByContent, out ImageTexture? raced, out bool racedHeight))
					{
						created.Dispose();
						Ready = raced;
						HasHeight = racedHeight;
					}
					else
					{
						KeepLocked(_composedByContent, created, height);
						Ready = created;
						storedFresh = true;
					}

					if (_composedByIdentity is not null)
					{
						KeepLocked(_composedByIdentity, Ready, HasHeight);
					}
				}

				if (storedFresh)
				{
					Interlocked.Increment(ref _composedPacked);
				}
				else
				{
					Interlocked.Increment(ref Engine_TexLibrary._composedByContent);
				}

				if (_noteComposedStats)
				{
					Refii.Entities.Engine_LoadEntityBuilds.NoteOrm(
						reused: !storedFresh,
						storedFresh ? System.Diagnostics.Stopwatch.GetTimestamp() - _composedStartTicks : 0L);
				}

				Dispose();
				return Ready;
			}

			if (_ormCacheKey is null)
			{
				return null;
			}

			lock (Gate)
			{
				if (LiveOrm.TryGetValue(_ormCacheKey, out OrmEntry? existing))
				{
					existing.LastAccess = NextAccess();
					HasHeight = existing.HasHeight;
					Ready = existing.Texture;
					Dispose();
					return Ready;
				}
			}

			ImageTexture? built = _cpuImage is null || _cpuImage.IsEmpty()
				? null
				: Engine_TextureUpload.CreateTextureOnMainThread(_cpuImage);
			if (built is null && !_cacheNullOrmResult)
			{
				Dispose();
				return null;
			}

			long dataBytes = _cpuImage is not null && built is not null ? Math.Max(256L, _cpuImage.GetDataSize()) : 0L;
			ulong tracedId = built is not null ? built.GetRid().Id : 0UL;

			lock (Gate)
			{
				if (LiveOrm.TryGetValue(_ormCacheKey, out OrmEntry? raced))
				{
					raced.LastAccess = NextAccess();
					built?.Dispose();
					HasHeight = raced.HasHeight;
					Ready = raced.Texture;
					Dispose();
					return Ready;
				}

				Held(built, _cpuImage);
				LiveOrm[_ormCacheKey] = new OrmEntry
				{
					Texture = built,
					HasHeight = HasHeight,
					ApproxBytes = dataBytes,
					LastAccess = NextAccess(),
					TracedRidId = tracedId,
				};
			}

			Ready = built;
			Dispose();
			return Ready;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_cpuImage?.Dispose();
			_cpuImage = null;
			_sharedPayload = null;
		}
	}

	/// <summary>
	/// Suffixes checked when probing for an artist-authored ORM (Ambient Occlusion, Roughness, Metallic) texture.
	/// </summary>
	private static readonly string[] OrmCandidates = new[]
	{
		"_orm.png",
		".orm.png",
		"_orm.tex.png",
		"_orm.tex",
		"_orm",
		".tex.orm",
		".orm",
	};

	/// <summary>
	/// Suffixes stripped from diffuse/albedo texture names when probing for companion ORM maps (e.g. `stone_wall_d.png` -> `stone_wall_orm.png`).
	/// </summary>
	private static readonly string[] DiffuseSuffixes = new[]
	{
		"_d", "_diff", "_diffuse", "_basecolor", "_albedo", "_col"
	};

	private static bool TryPrepareAuthoredOrm(
		string diffusePath,
		string specularPath,
		string normalPath,
		Func<string, byte[]?> read,
		out PreparedOrm? prepared)
	{
		prepared = null;

		string[] inputPaths = { diffusePath, normalPath, specularPath };
		foreach (string rawPath in inputPaths)
		{
			if (string.IsNullOrWhiteSpace(rawPath))
			{
				continue;
			}

			string normalized = Normalise(rawPath);
			int extIdx = normalized.LastIndexOf('.');
			string stem = extIdx > 0 ? normalized[..extIdx] : normalized;

			var candidates = new List<string>(OrmCandidates.Length * 2);
			foreach (string suffix in OrmCandidates)
			{
				candidates.Add(stem + suffix);
			}

			foreach (string diffSuffix in DiffuseSuffixes)
			{
				if (stem.EndsWith(diffSuffix, StringComparison.OrdinalIgnoreCase))
				{
					string baseStem = stem[..^diffSuffix.Length];
					foreach (string suffix in OrmCandidates)
					{
						candidates.Add(baseStem + suffix);
					}
					break;
				}
			}

			foreach (string candidate in candidates)
			{
				string cacheKey = "authored:" + candidate;
				lock (Gate)
				{
					if (LiveOrm.TryGetValue(cacheKey, out OrmEntry? cached) && cached.Texture is not null)
					{
						cached.LastAccess = NextAccess();
						prepared = new PreparedOrm(cached.Texture, cached.HasHeight, isAuthored: true);
						return true;
					}
				}

				byte[]? bytes = null;
				try
				{
					bytes = read(candidate);
				}
				catch
				{
					bytes = null;
				}

				if (bytes is null || bytes.Length == 0)
				{
					continue;
				}

				Image? img = null;
				if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
				{
					img = new Image();
					if (img.LoadPngFromBuffer(bytes) != Error.Ok)
					{
						img = null;
					}
				}
				else
				{
					img = Engine_TexImage.TryCreateImage(bytes, out _, out _);
				}

				if (img is null)
				{
					continue;
				}

				if (img.GetFormat() != Image.Format.Rgba8)
				{
					img.Convert(Image.Format.Rgba8);
				}

				bool height = CheckHasHeight(img);
				img.GenerateMipmaps();
				Engine_TexImage.CompressBlocks(img, isNormalMap: false);
				if (img.IsEmpty())
				{
					img.Dispose();
					return false;
				}

				prepared = new PreparedOrm(img, height, isAuthored: true, cacheKey, cacheNullOrmResult: false);
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Probes for artist-authored ORM textures matching the material's diffuse, normal, or specular names.
	/// </summary>
	private static bool TryResolveAuthoredOrm(
		string diffusePath,
		string specularPath,
		string normalPath,
		Func<string, byte[]?> read,
		out ImageTexture? texture,
		out bool hasHeight)
	{
		if (!TryPrepareAuthoredOrm(diffusePath, specularPath, normalPath, read, out PreparedOrm? prep) || prep is null)
		{
			texture = null;
			hasHeight = false;
			return false;
		}

		using (prep)
		{
			hasHeight = prep.HasHeight;
			texture = prep.Ready ?? Refii.Core.Engine_ServerSyncs.RunGpuUpload(
				prep.EstimatedBytes, () => prep.CommitOnMainThread());
			return texture is not null;
		}
	}

	/// <summary>
	/// Determines if an authored ORM texture contains valid height data in its alpha channel.
	/// </summary>
	/// <remarks>
	/// If all alpha pixels share identical values (e.g. solid 255 or solid 0), the channel represents a dummy/default alpha
	/// and height parallax is suppressed. If alpha values vary, active surface displacement is enabled.
	/// </remarks>
	private static bool CheckHasHeight(Image img)
	{
		if (img.GetFormat() != Image.Format.Rgba8)
		{
			return false;
		}

		byte[] data = img.GetData();
		if (data.Length < 4)
		{
			return false;
		}

		byte firstA = data[3];
		for (int i = 7; i < data.Length; i += 4)
		{
			if (data[i] != firstA)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Legacy overload of <see cref="GetOrm"/> omitting diffuse path and authored PBR flag.
	/// </summary>
	public static ImageTexture? GetOrm(
		string specularPath,
		string normalPath,
		float powerMin,
		float powerMax,
		Func<string, byte[]?> read,
		out bool hasHeight) =>
		GetOrm(string.Empty, specularPath, normalPath, powerMin, powerMax, read, out hasHeight, out _);

	internal static PreparedOrm PrepareOrm(
		string diffusePath,
		string specularPath,
		string normalPath,
		float powerMin,
		float powerMax,
		Func<string, byte[]?> read)
	{
		if (TryPrepareAuthoredOrm(diffusePath, specularPath, normalPath, read, out PreparedOrm? authoredPrep) &&
			authoredPrep is not null)
		{
			return authoredPrep;
		}

		if (!Engine_TexSettings.OrmEnabled ||
			(string.IsNullOrWhiteSpace(specularPath) && string.IsNullOrWhiteSpace(normalPath)))
		{
			return new PreparedOrm(null, false, isAuthored: false);
		}

		string path = Normalise(specularPath);
		string normal = Normalise(normalPath);
		float aoStrength = Engine_TexSettings.OrmAoStrength;
		float metallicStrength = Engine_TexSettings.OrmMetallicStrength;
		bool withHeight = Engine_TexSettings.OrmHeight;

		string id = $"{Engine_TexOrm.CacheId(powerMin, powerMax)}-ao{aoStrength:F2}-" +
			$"me{metallicStrength:F2}-h{(withHeight ? 1 : 0)}-{normal}";
		string memKey = $"{path} {id}";

		lock (Gate)
		{
			if (LiveOrm.TryGetValue(memKey, out OrmEntry? cached))
			{
				cached.LastAccess = NextAccess();
				return new PreparedOrm(cached.Texture, cached.HasHeight, isAuthored: false);
			}
		}

		Image? packed = BuildOrmImage(
			path, normal, id, powerMin, powerMax, aoStrength, metallicStrength, withHeight,
			read, out bool packedHeight);
		if (packed is null || packed.IsEmpty())
		{
			packed?.Dispose();
			lock (Gate)
			{
				if (!LiveOrm.ContainsKey(memKey))
				{
					LiveOrm[memKey] = new OrmEntry
					{
						Texture = null,
						HasHeight = false,
						LastAccess = NextAccess(),
					};
				}
			}

			return new PreparedOrm(null, packedHeight, isAuthored: false);
		}

		return new PreparedOrm(packed, packedHeight, isAuthored: false, memKey, cacheNullOrmResult: true);
	}

	/// <summary>
	/// Resolves, caches and returns an ORM (Occlusion-Roughness-Metallic) texture for a material.
	/// </summary>
	public static ImageTexture? GetOrm(
		string diffusePath,
		string specularPath,
		string normalPath,
		float powerMin,
		float powerMax,
		Func<string, byte[]?> read,
		out bool hasHeight,
		out bool isAuthored)
	{
		using PreparedOrm prep = PrepareOrm(diffusePath, specularPath, normalPath, powerMin, powerMax, read);
		hasHeight = prep.HasHeight;
		isAuthored = prep.IsAuthored;
		if (prep.Ready is not null || prep.EstimatedBytes == 0)
		{
			return prep.Ready;
		}

		ImageTexture? result = Refii.Core.Engine_ServerSyncs.RunGpuUpload(
			prep.EstimatedBytes, () => prep.CommitOnMainThread());
		hasHeight = prep.HasHeight;
		return result;
	}

	internal static Image? TryGetOrmSourceCpuImageByPath(
		string virtualPath,
		TexSemantic semantic,
		Func<string, byte[]?> read,
		out bool dispose,
		Texture2D? texture = null)
	{
		dispose = false;
		if (texture is not null &&
			CpuImages.TryGetValue(texture, out Image? directCpu) &&
			GodotObject.IsInstanceValid(directCpu) &&
			!directCpu.IsEmpty())
		{
			return directCpu;
		}

		if (string.IsNullOrWhiteSpace(virtualPath))
		{
			return null;
		}

		string memKey = MemKey(Normalise(virtualPath), semantic);
		Texture2D? cachedTex = null;
		lock (Gate)
		{
			if (Live.TryGetValue(memKey, out Entry? entry))
			{
				entry.LastAccess = NextAccess();
				cachedTex = entry.Texture;
			}
		}

		if (cachedTex is not null &&
			CpuImages.TryGetValue(cachedTex, out Image? cpu) &&
			GodotObject.IsInstanceValid(cpu) &&
			!cpu.IsEmpty())
		{
			return cpu;
		}

		Image? decoded = Decode(Normalise(virtualPath), semantic, read, out _);
		if (decoded is not null && !decoded.IsEmpty())
		{
			dispose = true;
			return decoded;
		}

		decoded?.Dispose();
		return null;
	}

	private static readonly object ComposedOrmFlightGate = new();

	internal static bool TryGetReadyComposedOrmByIdentity(
		Texture2D? identitySpec,
		Texture2D? identityNorm,
		float powerMin,
		float powerMax,
		float aoStrength,
		float metallicStrength,
		bool withHeight,
		out PreparedOrm? prepared)
	{
		prepared = null;
		if (identitySpec is null && identityNorm is null)
		{
			return false;
		}

		string recipe = ComposedRecipe(powerMin, powerMax, aoStrength, metallicStrength, withHeight);
		if (IdentityKey(identitySpec, identityNorm, recipe) is not { } byIdentity)
		{
			return false;
		}

		if (TryComposed(byIdentity, out ImageTexture? known, out bool knownHeight))
		{
			Interlocked.Increment(ref _composedByIdentity);
			Refii.Entities.Engine_LoadEntityBuilds.NoteOrm(reused: true, 0);
			prepared = new PreparedOrm(known, knownHeight);
			return true;
		}

		return false;
	}

	internal static PreparedOrm PrepareComposedOrm(
		Image? spec,
		Image? norm,
		Texture2D? identitySpec,
		Texture2D? identityNorm,
		float powerMin,
		float powerMax,
		float aoStrength,
		float metallicStrength,
		bool withHeight)
	{
		if (spec is null && norm is null)
		{
			return new PreparedOrm(null, false);
		}

		long from = System.Diagnostics.Stopwatch.GetTimestamp();
		string recipe = ComposedRecipe(powerMin, powerMax, aoStrength, metallicStrength, withHeight);
		string? byIdentity = identitySpec is not null || identityNorm is not null
			? IdentityKey(identitySpec, identityNorm, recipe)
			: null;
		if (byIdentity is not null && TryComposed(byIdentity, out ImageTexture? known, out bool knownHeight))
		{
			Interlocked.Increment(ref _composedByIdentity);
			Refii.Entities.Engine_LoadEntityBuilds.NoteOrm(reused: true, 0);
			return new PreparedOrm(known, knownHeight);
		}

		string byContent = ContentKey(spec, norm, recipe);
		if (TryComposed(byContent, out known, out knownHeight))
		{
			Interlocked.Increment(ref _composedByContent);
			if (byIdentity is not null)
			{
				Keep(byIdentity, known, knownHeight);
			}

			Refii.Entities.Engine_LoadEntityBuilds.NoteOrm(
				reused: true, System.Diagnostics.Stopwatch.GetTimestamp() - from);
			return new PreparedOrm(known, knownHeight);
		}

		(ImageTexture? Cached, bool CachedHeight, SharedOrmPayload? Payload) outcome = RunKeyedSingleFlight(
			ComposedOrmFlightGate,
			ComposedOrmFlights,
			byContent,
			() =>
			{
				if (TryComposed(byContent, out ImageTexture? racedCached, out bool racedHeight))
				{
					return (racedCached, racedHeight, (SharedOrmPayload?)null);
				}

				Image? packed = Engine_TexOrm.TryPack(
					spec, norm, powerMin, powerMax, aoStrength, metallicStrength, withHeight, out bool packedHeight);
				if (packed is null || packed.IsEmpty())
				{
					packed?.Dispose();
					return ((ImageTexture?)null, false, (SharedOrmPayload?)null);
				}

				try
				{
					HalvePackedOrm(packed);
					packed.GenerateMipmaps();
					CompressOrReport(packed, isNormalMap: false, "orm");
					byte[] bytes;
					using (var raw = new Engine_ImageBytes(packed))
					{
						bytes = raw.Span.ToArray();
					}

					return ((ImageTexture?)null, packedHeight, new SharedOrmPayload(
						bytes, packed.GetWidth(), packed.GetHeight(), packed.HasMipmaps(), packed.GetFormat(), packedHeight));
				}
				finally
				{
					packed.Dispose();
				}
			},
			out bool _);

		if (outcome.Cached is not null)
		{
			Interlocked.Increment(ref _composedByContent);
			if (byIdentity is not null)
			{
				Keep(byIdentity, outcome.Cached, outcome.CachedHeight);
			}

			Refii.Entities.Engine_LoadEntityBuilds.NoteOrm(
				reused: true, System.Diagnostics.Stopwatch.GetTimestamp() - from);
			return new PreparedOrm(outcome.Cached, outcome.CachedHeight);
		}

		if (outcome.Payload is null)
		{
			return new PreparedOrm(null, false);
		}

		if (TryComposed(byContent, out ImageTexture? postWaitCached, out bool postWaitHeight))
		{
			Interlocked.Increment(ref _composedByContent);
			if (byIdentity is not null)
			{
				Keep(byIdentity, postWaitCached, postWaitHeight);
			}

			Refii.Entities.Engine_LoadEntityBuilds.NoteOrm(
				reused: true, System.Diagnostics.Stopwatch.GetTimestamp() - from);
			return new PreparedOrm(postWaitCached, postWaitHeight);
		}

		return new PreparedOrm(
			outcome.Payload,
			composedByIdentity: byIdentity,
			composedByContent: byContent,
			noteComposedStats: true,
			composedStartTicks: from);
	}

	/// <summary>
	/// Packs an ORM straight from already-resolved textures, bypassing the path-keyed cache.
	/// </summary>
	/// <remarks>
	/// For sources that do not live in a bank — the hero's morphed specular and normal. The
	/// path-keyed cache would pack the UNMORPHED bytes under the same identity, so a morphed
	/// material would bind maps and an ORM that disagree.
	///
	/// Kept by what the pack is a function of (<see cref="ComposedOrm"/>): the two textures'
	/// CONTENT as read back, or the identity of a texture that is never rewritten in place
	/// (<see cref="MarkUnchanging"/>), and every parameter. A morph that changes either map changes
	/// its bytes and so its key; a hit is the same bytes a rebuild would pack.
	/// </remarks>
	public static ImageTexture? BuildOrmFromTextures(
		Texture2D? specular,
		Texture2D? normal,
		float powerMin,
		float powerMax,
		out bool hasHeight)
	{
		hasHeight = false;
		if (!Engine_TexSettings.OrmEnabled)
		{
			return null;
		}

		return ComposedOrm(
			specular, normal, powerMin, powerMax,
			Engine_TexSettings.OrmAoStrength, Engine_TexSettings.OrmMetallicStrength,
			Engine_TexSettings.OrmHeight, out hasHeight);
	}

	/// <summary>
	/// Packs an ORM directly from CPU <see cref="Image"/> buffers without issuing GPU readbacks.
	/// </summary>
	public static ImageTexture? BuildOrmFromImages(
		Image? spec,
		Image? norm,
		float powerMin,
		float powerMax,
		out bool hasHeight)
	{
		hasHeight = false;
		if (!Engine_TexSettings.OrmEnabled)
		{
			return null;
		}

		return PackComposedOrm(
			spec, norm, powerMin, powerMax,
			Engine_TexSettings.OrmAoStrength, Engine_TexSettings.OrmMetallicStrength,
			Engine_TexSettings.OrmHeight, out hasHeight);
	}

	/// <summary>Composed ORMs kept strongly before the list starts again; matches the composites' own limit.</summary>
	internal const int ComposedOrmLimit = Refii.Hero.Engine_HeroTextureMorph.CompositedLimit;

	private static readonly Dictionary<string, (ImageTexture? Texture, bool HasHeight)> ComposedOrms =
		new(StringComparer.Ordinal);

	/// <summary>Composed ORMs past the limit, for as long as a material still holds them.</summary>
	private static readonly Dictionary<string, (WeakReference<ImageTexture> Texture, bool HasHeight)> ComposedOrmsOverLimit =
		new(StringComparer.Ordinal);

	/// <summary>Textures whose content is fixed for their lifetime, each with an id never reused.</summary>
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture2D, object> Unchanging = new();

	/// <summary>CPU images retained alongside composite textures so ORM packing never needs a GPU readback.</summary>
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture2D, Image> CpuImages = new();

	/// <summary>Live library entries by texture instance so mixed composite+library ORM pairs can be keyed by identity and decoded from disk cache without GPU readback.</summary>
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture2D, Entry> LibraryEntries = new();

	private static long _unchangingIds;

	/// <summary>Associates a CPU <see cref="Image"/> with <paramref name="texture"/> so <see cref="ComposedOrm"/> never reads it back from the GPU.</summary>
	public static void RegisterCpuImage(Texture2D? texture, Image? image)
	{
		if (texture is not null && image is not null && !image.IsEmpty())
		{
			CpuImages.AddOrUpdate(texture, image);
		}
	}

	private static Image? ResolveOrmSourceImage(Texture2D? texture, out bool dispose)
	{
		dispose = false;
		if (texture is null)
		{
			return null;
		}

		if (CpuImages.TryGetValue(texture, out Image? cpuImage) &&
			GodotObject.IsInstanceValid(cpuImage) &&
			!cpuImage.IsEmpty())
		{
			return cpuImage;
		}

		if (LibraryEntries.TryGetValue(texture, out Entry? entry) && entry is not null)
		{
			Image? decoded = Decode(entry.Path, entry.Semantic, entry.Read, out _);
			if (decoded is not null)
			{
				dispose = true;
				return decoded;
			}
		}

		dispose = true;
		return Refii.Core.Engine_ServerSyncs.Image(texture);
	}

	/// <summary>Run totals: composed ORMs packed, answered by identity, answered by content.</summary>
	public static (long Packed, long ByIdentity, long ByContent) ComposedOrmWork => (
		System.Threading.Interlocked.Read(ref _composedPacked),
		System.Threading.Interlocked.Read(ref _composedByIdentity),
		System.Threading.Interlocked.Read(ref _composedByContent));

	private static long _composedPacked;
	private static long _composedByIdentity;
	private static long _composedByContent;

	/// <summary>
	/// States that <paramref name="texture"/>'s content never changes after this call, so a
	/// composed ORM can be found by its identity without reading it back. Only for textures the
	/// caller made and hands out read-only (the character composites): the library's own live
	/// maps are swapped in place (<see cref="Refresh"/>, the enhancement completions,
	/// Engine_ModRemount) and are never marked.
	/// </summary>
	public static void MarkUnchanging(Texture2D? texture)
	{
		if (texture is not null)
		{
			Unchanging.AddOrUpdate(texture, System.Threading.Interlocked.Increment(ref _unchangingIds));
		}
	}

	/// <summary>Drops the composed ORMs' strong references (a region release, a remount). Content-keyed, so nothing goes stale.</summary>
	public static void ForgetComposedOrms()
	{
		lock (Gate)
		{
			ComposedOrms.Clear();
			ComposedOrmsOverLimit.Clear();
		}
	}

	/// <summary>
	/// The composed ORM for these two textures and parameters: kept, or packed and kept.
	/// </summary>
	/// <remarks>
	/// <see cref="PackComposedOrm"/> is a pure function of the two read-back images (size, format,
	/// mip flag, bytes) and the five parameters — serial, no clock, no setting read inside — so a
	/// key over exactly those answers with the bytes a rebuild would produce. An identity key is
	/// used when every texture given is either <see cref="MarkUnchanging"/> or a version-tracked
	/// <see cref="Engine_TexLibrary"/> entry.
	/// </remarks>
	internal static ImageTexture? ComposedOrm(
		Texture2D? specular,
		Texture2D? normal,
		float powerMin,
		float powerMax,
		float aoStrength,
		float metallicStrength,
		bool withHeight,
		out bool hasHeight)
	{
		hasHeight = false;
		if (specular is null && normal is null)
		{
			return null;
		}

		string recipe = ComposedRecipe(powerMin, powerMax, aoStrength, metallicStrength, withHeight);
		string? byIdentity = IdentityKey(specular, normal, recipe);
		if (byIdentity is not null && TryComposed(byIdentity, out ImageTexture? known, out hasHeight))
		{
			System.Threading.Interlocked.Increment(ref _composedByIdentity);
			Refii.Entities.Engine_LoadEntityBuilds.NoteOrm(reused: true, 0);
			return known;
		}

		long from = System.Diagnostics.Stopwatch.GetTimestamp();
		Image? spec = ResolveOrmSourceImage(specular, out bool disposeSpec);
		Image? norm = ResolveOrmSourceImage(normal, out bool disposeNorm);
		try
		{
			if (spec is null && norm is null)
			{
				return null;
			}

			string byContent = ContentKey(spec, norm, recipe);
			if (TryComposed(byContent, out known, out hasHeight))
			{
				System.Threading.Interlocked.Increment(ref _composedByContent);
				if (byIdentity is not null)
				{
					Keep(byIdentity, known, hasHeight);
				}

				Refii.Entities.Engine_LoadEntityBuilds.NoteOrm(reused: true, System.Diagnostics.Stopwatch.GetTimestamp() - from);
				return known;
			}

			ImageTexture? built = PackComposedOrm(
				spec, norm, powerMin, powerMax, aoStrength, metallicStrength, withHeight, out bool packedHeight);
			System.Threading.Interlocked.Increment(ref _composedPacked);

			// Two workers packing the same key produced the same bytes; the first kept answers both,
			// so every material holding this content holds one texture.
			lock (Gate)
			{
				if (TryComposedLocked(byContent, out ImageTexture? raced, out bool racedHeight))
				{
					built = raced;
					packedHeight = racedHeight;
				}
				else
				{
					KeepLocked(byContent, built, packedHeight);
				}

				if (byIdentity is not null)
				{
					KeepLocked(byIdentity, built, packedHeight);
				}
			}

			Refii.Entities.Engine_LoadEntityBuilds.NoteOrm(reused: false, System.Diagnostics.Stopwatch.GetTimestamp() - from);
			hasHeight = packedHeight;
			return built;
		}
		finally
		{
			if (disposeSpec)
			{
				spec?.Dispose();
			}

			if (disposeNorm)
			{
				norm?.Dispose();
			}
		}
	}

	/// <summary>The uncached pack: what <see cref="BuildOrmFromTextures"/> always did with the two read-back images.</summary>
	internal static ImageTexture? PackComposedOrm(
		Image? spec,
		Image? norm,
		float powerMin,
		float powerMax,
		float aoStrength,
		float metallicStrength,
		bool withHeight,
		out bool hasHeight)
	{
		hasHeight = false;
		if (spec is null && norm is null)
		{
			return null;
		}

		Image? packed = Engine_TexOrm.TryPack(
			spec, norm, powerMin, powerMax, aoStrength, metallicStrength, withHeight, out bool packedHeight);
		if (packed is null)
		{
			return null;
		}

		HalvePackedOrm(packed);
		packed.GenerateMipmaps();
		CompressOrReport(packed, isNormalMap: false, "orm");
		hasHeight = packedHeight;
		return packed.IsEmpty() ? null : Engine_TextureUpload.CreateTexture(packed);
	}

	private static string ComposedRecipe(
		float powerMin, float powerMax, float aoStrength, float metallicStrength, bool withHeight) =>
		$"{BitConverter.SingleToUInt32Bits(powerMin):x8}-{BitConverter.SingleToUInt32Bits(powerMax):x8}-" +
		$"{BitConverter.SingleToUInt32Bits(aoStrength):x8}-{BitConverter.SingleToUInt32Bits(metallicStrength):x8}-" +
		(withHeight ? "h" : "f");

	private static bool TryIdentityToken(Texture2D? texture, out string token)
	{
		if (texture is null)
		{
			token = "-";
			return true;
		}

		if (Unchanging.TryGetValue(texture, out object? unchangingId) && unchangingId is not null)
		{
			token = unchangingId.ToString()!;
			return true;
		}

		if (LibraryEntries.TryGetValue(texture, out Entry? entry) && entry is not null)
		{
			token = $"lib:{entry.Path}:{Interlocked.Read(ref entry.Version)}";
			return true;
		}

		token = string.Empty;
		return false;
	}

	/// <summary>Null unless every texture given is unchanging or a tracked library entry.</summary>
	private static string? IdentityKey(Texture2D? specular, Texture2D? normal, string recipe)
	{
		if (!TryIdentityToken(specular, out string specularId) ||
			!TryIdentityToken(normal, out string normalId))
		{
			return null;
		}

		return $"i {specularId} {normalId} {recipe}";
	}

	/// <summary>SHA-256 over each image's presence, size, format, mip flag and bytes, then the recipe.</summary>
	internal static string ContentKey(Image? spec, Image? norm, string recipe)
	{
		using Refii.Core.Engine_Hash.Incremental sha = Refii.Core.Engine_Hash.CreateIncrementalSha256();
		Span<byte> header = stackalloc byte[17];
		foreach (Image? image in new[] { spec, norm })
		{
			header.Clear();
			if (image is not null)
			{
				header[0] = 1;
				BitConverter.TryWriteBytes(header[1..], image.GetWidth());
				BitConverter.TryWriteBytes(header[5..], image.GetHeight());
				BitConverter.TryWriteBytes(header[9..], (int)image.GetFormat());
				BitConverter.TryWriteBytes(header[13..], image.HasMipmaps() ? 1 : 0);
			}

			sha.AppendData(header);
			if (image is not null)
			{
				var bytes = new Engine_ImageBytes(image);
				try
				{
					BitConverter.TryWriteBytes(header[..4], bytes.Span.Length);
					sha.AppendData(header[..4]);
					sha.AppendData(bytes.Span);
				}
				finally
				{
					bytes.Dispose();
				}
			}
		}

		sha.AppendData(System.Text.Encoding.UTF8.GetBytes(recipe));
		return "c " + Convert.ToHexString(sha.GetHashAndReset());
	}

	private static bool TryComposed(string key, out ImageTexture? texture, out bool hasHeight)
	{
		lock (Gate)
		{
			return TryComposedLocked(key, out texture, out hasHeight);
		}
	}

	/// <summary>Under <see cref="Gate"/>. A null pack is an answer too and is kept as one.</summary>
	private static bool TryComposedLocked(string key, out ImageTexture? texture, out bool hasHeight)
	{
		if (ComposedOrms.TryGetValue(key, out (ImageTexture? Texture, bool HasHeight) kept))
		{
			texture = kept.Texture;
			hasHeight = kept.HasHeight;
			return true;
		}

		if (ComposedOrmsOverLimit.TryGetValue(key, out (WeakReference<ImageTexture> Texture, bool HasHeight) held) &&
			held.Texture.TryGetTarget(out ImageTexture? alive) && GodotObject.IsInstanceValid(alive))
		{
			texture = alive;
			hasHeight = held.HasHeight;
			return true;
		}

		texture = null;
		hasHeight = false;
		return false;
	}

	private static void Keep(string key, ImageTexture? texture, bool hasHeight)
	{
		lock (Gate)
		{
			KeepLocked(key, texture, hasHeight);
		}
	}

	/// <summary>Under <see cref="Gate"/>: at the limit the strong list starts again, and what it held stays findable while alive.</summary>
	private static void KeepLocked(string key, ImageTexture? texture, bool hasHeight)
	{
		if (ComposedOrms.Count >= ComposedOrmLimit && !ComposedOrms.ContainsKey(key))
		{
			var gone = new List<string>();
			foreach ((string listed, (WeakReference<ImageTexture> Texture, bool HasHeight) held) in ComposedOrmsOverLimit)
			{
				if (!held.Texture.TryGetTarget(out _))
				{
					gone.Add(listed);
				}
			}

			foreach (string listed in gone)
			{
				ComposedOrmsOverLimit.Remove(listed);
			}

			foreach ((string listed, (ImageTexture? Texture, bool HasHeight) kept) in ComposedOrms)
			{
				if (kept.Texture is not null)
				{
					ComposedOrmsOverLimit[listed] = (new WeakReference<ImageTexture>(kept.Texture), kept.HasHeight);
				}
			}

			ComposedOrms.Clear();
		}

		ComposedOrms[key] = (texture, hasHeight);
	}

	/// <summary>
	/// Builds and caches the packed ORM image. The texture, if one is wanted, is the caller's.
	/// </summary>
	/// <remarks>
	/// Split so the prebuild AND the live path cannot diverge. They must agree on the cache key and
	/// on every channel in it, and the surest way to guarantee that is for there to be one
	/// implementation with the <see cref="ImageTexture"/> lifted out of it — the only part the
	/// prebuild does not want.
	/// </remarks>
	private static Image? BuildOrmImage(
		string path,
		string normalPath,
		string id,
		float powerMin,
		float powerMax,
		float aoStrength,
		float metallicStrength,
		bool withHeight,
		Func<string, byte[]?> read,
		out bool hasHeight)
	{
		hasHeight = false;

		byte[]? source = null;
		byte[]? normalBytes = null;
		try
		{
			if (path.Length > 0)
			{
				source = read(path);
			}

			// Read whenever any derivation wants it — the height solve needs it with AO off.
			if ((aoStrength > 0.0f || withHeight) && normalPath.Length > 0)
			{
				normalBytes = read(normalPath);
			}
		}
		catch (Exception e)
		{
			// A bank read can throw on a pairing fault; without this the caller only sees
			// Godot's bare "Cannot open file".
			GD.PushWarning($"[tex] {path} orm: read threw {e.GetType().Name}: {e.Message}");
			return null;
		}

		// The key rides on whichever source actually exists — for a normal-only ORM that is the
		// normal map's bytes. (Version 2: the height channel became mean-anchored and the
		// specular became optional, so version-1 entries answer with the old packing.)
		byte[]? keySource = source ?? normalBytes;
		if (keySource is null)
		{
			return null;
		}

		string key = Engine_TexCache.Key(keySource, TexSemantic.Orm, id, 2);

		Image? cached = Engine_TexCache.TryLoad(key, out bool cachedAlpha);
		if (cached is not null)
		{
			hasHeight = cachedAlpha;
			return cached;
		}

		using Image? specular = source is null
			? null
			: Engine_TexImage.TryCreateImage(source, out _, out _);
		if (specular is null && source is not null)
		{
			return null;
		}

		using Image? normal = normalBytes is null
			? null
			: Engine_TexImage.TryCreateImage(normalBytes, out _, out _);

		Image? packed = Engine_TexOrm.TryPack(
			specular, normal, powerMin, powerMax, aoStrength, metallicStrength, withHeight,
			out bool packedHeight);
		if (packed is null)
		{
			return null;
		}

		HalvePackedOrm(packed);
		packed.GenerateMipmaps();
		CompressOrReport(packed, isNormalMap: false, "orm");

		// The alpha carries the derived height WHEN ONE WAS ACTUALLY SOLVED — `withHeight` is the
		// request, `packedHeight` the outcome, and storing the request lied whenever TrySolve
		// declined. The flag drives the shader's parallax gate, so a wrong true is texture swim.
		// DERIVED: cheaper than an upscale but far dearer than a decode — the height channel alone
		// is a Poisson solve over the whole image.
		Engine_TexCache.Store(key, packed, packedHeight, TexCacheCost.Derived);
		hasHeight = packedHeight;
		return packed;
	}

	/// <summary>Block-compresses in place, and reports it ONCE if it cannot.</summary>
	/// <remarks>
	/// OUR ENCODER, NEVER GODOT'S, and not as a fallback either. Godot's BC compressor is
	/// editor-only: in an exported build `_image_compress_bc_func` is null, `Image.Compress`
	/// fails, and because both callers used to discard its return value every ORM and every
	/// enhanced texture was kept, cached and uploaded as RGBA8 — 2.90 GB of one 3.13 GB cache.
	///
	/// Using Godot's where it exists and ours where it does not would put TWO encoders behind
	/// one cache key, so the same texture would carry different bytes depending on whether the
	/// editor or the shipped game produced it, at the same <see cref="Engine_TexCache"/>
	/// ConverterVersion. One encoder everywhere is the same rule BuildOrmImage already follows
	/// for the prebuild and the live path.
	/// </remarks>
	private static void CompressOrReport(Image image, bool isNormalMap, string what)
	{
		if (Engine_TexImage.CompressBlocks(image, isNormalMap))
		{
			return;
		}

		int n = System.Threading.Interlocked.Increment(ref _compressFailures);
		if (n == 1)
		{
			GD.PushWarning(
				$"[tex] block compression failed ({what} {image.GetWidth()}x{image.GetHeight()} " +
				$"{image.GetFormat()}); it stays uncompressed, which costs 4-8x the memory.");
		}
	}

	private static int _compressFailures;

	private static readonly Dictionary<string, Cubemap?> LiveCubes = new(StringComparer.Ordinal);

	/// <summary>
	/// Resolves a cube map — the environment maps, and nothing else so far.
	/// </summary>
	/// <remarks>
	/// A Separate map because a <see cref="Cubemap"/> is not a <see cref="Texture2D"/>; it is a
	/// TextureLayered, and the live map's whole mechanism is the in-place
	/// <c>ImageTexture.SetImage</c> swap that a layered texture has no equivalent of.
	///
	/// That costs nothing here: the policy for <see cref="TexSemantic.EnvironmentMap"/> is
	/// <see cref="TexFilterClass.None"/>, because resampling the six faces independently seams
	/// every edge. Cube maps are never enhanced, so they never need swapping, so the simpler map
	/// is the whole story rather than a shortcut.
	///
	/// Only 36 distinct cube textures ship, so this map stays small by construction.
	/// </remarks>
	public static Cubemap? GetCubemap(string virtualPath, Func<string, byte[]?> read)
	{
		if (string.IsNullOrWhiteSpace(virtualPath))
		{
			return null;
		}

		string path = Normalise(virtualPath);

		lock (Gate)
		{
			if (LiveCubes.TryGetValue(path, out Cubemap? cached))
			{
				return cached;
			}
		}

		Cubemap? cubemap = ResolveCube(path, read);

		lock (Gate)
		{
			if (LiveCubes.TryGetValue(path, out Cubemap? raced))
			{
				return raced;
			}

			LiveCubes[path] = cubemap;
		}

		return cubemap;
	}

	private static Cubemap? ResolveCube(string path, Func<string, byte[]?> read)
	{
		byte[]? source;
		try
		{
			source = read(path);
		}
		catch (Exception e)
		{
			// A bank read can throw on a pairing fault; without this the caller only sees
			// Godot's bare "Cannot open file".
			GD.PushWarning($"[tex] {path}: read threw {e.GetType().Name}: {e.Message}");
			return null;
		}

		if (source is null)
		{
			GD.PushWarning($"[tex] cube not found: {path}");
			return null;
		}

		string key = Engine_TexCache.BaseKey(source);

		// The six faces live under ONE key, so a cube is a hit or a miss as a whole. Six separate
		// entries could be half-evicted by the budget sweep and leave a cube rendering with four
		// faces.
		if (Engine_TexCache.TryLoadFaces(key, out Image[] cachedFaces, out _) &&
			cachedFaces.Length == Engine_TexCube.FaceCount)
		{
			return Engine_TexImage.CubemapFromImages(cachedFaces, out _);
		}

		try
		{
			Cubemap? built = Engine_TexImage.TryCreateCubemap(
				source, out Image[] faces, out string error);
			if (built is null)
			{
				GD.PushWarning($"[tex] {path}: {error}");
				return null;
			}

			Engine_TexCache.Store(key, faces, hasAlpha: false);
			return built;
		}
		catch (Exception e)
		{
			GD.PushWarning($"[tex] {path}: {e.Message}");
			return null;
		}
	}

	/// <summary>Queues one job. The caller must hold <see cref="Gate"/>.</summary>
	private static void Enqueue(string memKey, Entry entry, JobKind kind, TexPriority priority)
	{
		// Coalesced. One texture is named by many materials — a road surface is named by every
		// road segment — and without this each of them would queue its own run over the same
		// image. A toggle flipped twice quickly would otherwise also queue two opposite jobs for
		// the same texture and land on whichever finished last.
		if (InFlight.ContainsKey(memKey))
		{
			return;
		}

		var job = new Job { MemKey = memKey, Entry = entry, Kind = kind };
		InFlight[memKey] = job;

		// The sequence number is the tie break, so equal priorities stay in request order
		// instead of being reordered arbitrarily by the heap.
		Queue.Enqueue(job, ((long)priority << 32) | (uint)Interlocked.Increment(ref _sequence));

		EnsureWorkers();
		Monitor.PulseAll(Gate);
	}

	private static void EnsureWorkers()
	{
		if (_workersStarted)
		{
			return;
		}

		_workersStarted = true;

		int count = Engine_TexSettings.Workers;
		for (int i = 0; i < count; ++i)
		{
			// Background threads. There is no shutdown handshake, and there should not be one:
			// the only thing a worker holds is a decode in progress, and the process exiting
			// mid-run loses a cache entry that will be rebuilt next time.
			var thread = new Thread(WorkerLoop)
			{
				IsBackground = true,
				Name = $"refii-tex-{i}",
				Priority = ThreadPriority.BelowNormal,
			};
			thread.Start();
		}

		GD.Print($"[tex] {count} enhancement worker(s), backend {Engine_TexEnhance.BackendId}");
	}

	/// <summary>Produces owned CPU images off-thread and transfers cleanup to the main-thread completion.</summary>
	private static void WorkerLoop()
	{
		while (true)
		{
			Job job;
			lock (Gate)
			{
				while (Queue.Count == 0)
				{
					Monitor.Wait(Gate);
				}

				job = Queue.Dequeue();
			}

			Image? produced = null;
			long allocFrom = GC.GetAllocatedBytesForCurrentThread();
			try
			{
				produced = job.Kind == JobKind.Enhance
					? Enhance(job.Entry)
					: Decode(job.Entry.Path, job.Entry.Semantic, job.Entry.Read, out _);
			}
			catch (Exception e)
			{
				GD.PushWarning($"[tex] {job.Kind} failed for {job.Entry.Path}: {e.Message}");
			}

			Refii.Core.Engine_GcFrames.Note(ref Refii.Core.Engine_GcFrames.TextureWorkerBytes,
				GC.GetAllocatedBytesForCurrentThread() - allocFrom);

			lock (Gate)
			{
				InFlight.Remove(job.MemKey);
			}

			if (produced is null || produced.IsEmpty())
			{
				// Settled, so the next material naming this texture does not queue it again. A
				// declined texture would otherwise be a permanent source of work that never
				// produces anything. A failed REVERT is left alone: it is still showing an
				// enhanced image, so the state has to keep saying so.
				if (job.Kind == JobKind.Enhance)
				{
					lock (Gate)
					{
						job.Entry.State = EnhancementState.Declined;
					}
				}

				continue;
			}

			Image image = produced;
			Entry entry = job.Entry;
			JobKind kind = job.Kind;
			Completions.Enqueue(Engine_TextureTemporaries.ReleaseAfter(image, uploaded =>
			{
				ImageTexture? target = entry.Target;
				if (target is null || !GodotObject.IsInstanceValid(target) || image.IsEmpty())
				{
					// The UI texture was freed by Godot between the request and the apply. Not a
					// fault: the work is in the cache, so if it is loaded again the re-request
					// costs a disk read rather than another resample.
					return;
				}

				// Avoid in-place RD::Texture RID replacement (`RS::texture_replace`) on Direct3D 12
				// whenever dimensions, format, or mipmaps differ (or on live UI textures whose dimensions
				// change): replacing an active ImageTexture's underlying ID3D12Resource while bound to
				// live shader descriptor tables can fault/TDR the D3D12 command queue. Matching-layout
				// updates use `ImageTexture.Update` (`RS::texture_2d_update`), and layout-changing
				// enhancements are already persisted to Engine_TexCache on disk for the next load.
				bool skipLiveReplace =
					(Engine_TextureUpload.IsD3D12Driver && !Engine_TextureUpload.CanUpdateInPlace(target, image)) ||
					(entry.Semantic == TexSemantic.Ui &&
						(target.GetWidth() != image.GetWidth() || target.GetHeight() != image.GetHeight()));
				if (skipLiveReplace)
				{
					lock (Gate)
					{
						entry.State = kind == JobKind.Enhance
							? EnhancementState.Enhanced
							: EnhancementState.None;
					}
					return;
				}

				// The swap, and the reason nothing had to register a callback: every material
				// holding this Texture2D is holding THIS object, so replacing its image updates
				// all of them at once. SetImage rather than Update because the size changes.
				if (entry.Semantic == TexSemantic.Ui && Refii.Ui.Engine_UiKeyGlyphs.NeedsSourceImage(entry.Path))
				{
					Refii.Ui.Engine_UiKeyGlyphs.RegisterSourceImage(target, (Image)image.Duplicate());
				}

				Engine_TextureUpload.SetImage(target, image, world: true);
				Interlocked.Add(ref _pumpedBytesThisFrame, image.GetDataSize());
				Refii.Core.Engine_TracedMemory.Resize(TracedPool, target.GetRid().Id, (ulong)image.GetDataSize());
				lock (Gate)
				{
					entry.State = kind == JobKind.Enhance
						? EnhancementState.Enhanced
						: EnhancementState.None;
					Interlocked.Increment(ref entry.Version);
				}
			}));
		}
	}

	/// <summary>
	/// The §4.1 processing chain for one texture, off the main thread.
	/// </summary>
	/// <remarks>
	/// The source is re-read and re-decoded here rather than carried over from <see cref="Get"/>.
	/// Holding the decoded <see cref="Image"/> of every texture alive on the chance that it might
	/// later be enhanced would roughly double the resident texture memory of a region for a path
	/// that runs once per asset in the lifetime of the cache.
	/// </remarks>
	private static Image? Enhance(Entry entry)
	{
		if (Engine_TexEnhance.Backend is null)
		{
			return null;
		}

		byte[]? source = entry.Read(entry.Path);
		if (source is null)
		{
			return null;
		}

		using Image? original = Engine_TexImage.TryCreateImage(source, out bool hasAlpha, out _);
		if (original is null)
		{
			return null;
		}

		return EnhanceDecoded(source, original, entry.Semantic, hasAlpha, entry.Path);
	}

	/// <summary>
	/// Enhances an image the CALLER decoded, keyed on the bytes it decoded it from.
	/// </summary>
	/// <remarks>
	/// For assets that are NOT <c>.tex</c> AND are NOT in A BANK. The pub games are the case that
	/// forced this: they are a separate runtime with their own loose <c>.swf</c> and <c>.dds</c>
	/// files, so nothing about <see cref="Get"/> fits them — there is no virtual path, no bank
	/// reader, and Godot decodes the container rather than <see cref="Engine_TexImage"/>. What DOES
	/// fit is everything after the decode, and that is what this is: one policy, one scale rule,
	/// one cache, one backend. The alternative was a second upscale path living in FlashGames,
	/// which is the pattern the engine rules exist to prevent.
	///
	/// Content addressed, like every other entry. The key is the hash of <paramref name="source"/>,
	/// so a loose file needs no stable name and moving or renaming it does not orphan its cache
	/// entry.
	///
	/// Synchronous, unlike <see cref="RequestEnhanced"/>. The caller here is not showing the
	/// original first and swapping later — the pub-game rasterizer samples the image on the CPU
	/// once, at load, and bakes the result into a canvas, so an image that arrived afterwards
	/// would never be looked at again.
	/// </remarks>
	/// <param name="compress">
	/// False when the result is to be sampled on the CPU rather than by the GPU. Block compression
	/// is a decode cost and a quality loss for a caller that is only going to call
	/// <c>Decompress</c> on it — which is exactly what the SWF rasterizer's sampler does.
	/// </param>
	/// <summary>Whether <see cref="EnhanceDecoded"/>, with its defaults, would have nothing to make.</summary>
	private static bool EnhancementIsCached(byte[] source, int width, int height, TexSemantic semantic)
	{
		ITexUpscaler? backend = Engine_TexEnhance.Backend;
		if (!Engine_TexEnhance.Enabled || backend is null)
		{
			return true;
		}

		int scale = Engine_TexSemantic.TargetScale(width, height, Engine_TexSemantic.For(semantic));
		return scale <= 1 ||
			Engine_TexCache.TryPeek(Engine_TexCache.Key(source, semantic, backend.Id, scale), out _, out _);
	}

	public static Image? EnhanceDecoded(
		byte[] source,
		Image original,
		TexSemantic semantic,
		bool hasAlpha,
		string label,
		bool compress = true,
		bool cacheOnly = false)
	{
		ITexUpscaler? backend = Engine_TexEnhance.Backend;
		if (!Engine_TexEnhance.Enabled || backend is null)
		{
			return null;
		}

		TexPolicy policy = Engine_TexSemantic.For(semantic);
		int scale = Engine_TexSemantic.TargetScale(
			original.GetWidth(), original.GetHeight(), policy);
		if (scale <= 1)
		{
			return null;
		}

		// The compression IS part of the identity. Two callers can hand over the same bytes and the
		// same semantic and want different artefacts back, and a cache that answered a CPU sampler
		// with a block-compressed surface would be serving the wrong one with no way to tell.
		string backendId = compress ? backend.Id : backend.Id + "-raw";
		string key = Engine_TexCache.Key(source, semantic, backendId, scale);

		Image? result = Engine_TexCache.TryLoad(key, out _);
		if (result is null && compress && scale == 2)
		{
			// If a 4x enhanced entry already exists on disk (e.g. from a 4K prebuild), slice its
			// mip 1..N chain directly into the 2x key without re-running the upscaler or BC encoder.
			string key4x = Engine_TexCache.Key(source, semantic, backendId, 4);
			using Image? cached4x = Engine_TexCache.TryLoad(key4x, out _);
			if (cached4x is not null && cached4x.HasMipmaps() && cached4x.GetMipmapCount() >= 2)
			{
				long offset = cached4x.GetMipmapOffset(1);
				byte[] fullData = cached4x.GetData();
				if (offset > 0 && offset < fullData.Length)
				{
					result = Image.CreateFromData(
						Math.Max(1, cached4x.GetWidth() / 2),
						Math.Max(1, cached4x.GetHeight() / 2),
						true,
						cached4x.GetFormat(),
						fullData.AsSpan((int)offset));
					if (result is not null && !result.IsEmpty())
					{
						Engine_TexCache.Store(key, result, hasAlpha, TexCacheCost.Enhanced);
					}
				}
			}
		}

		if (result is not null || cacheOnly)
		{
			return result;
		}

		// The backend is handed a plain RGBA8 surface with no mip chain, because every backend
		// would otherwise have to undo the block compression itself and they would each get it
		// slightly differently.
		using var work = (Image)original.Duplicate();
		work.ClearMipmaps();
		if (work.IsCompressed())
		{
			work.Decompress();
		}

		work.Convert(Image.Format.Rgba8);

		if (!backend.TryUpscale(work, policy.Filter, scale, out Image? upscaled) ||
			upscaled is null)
		{
			return null;
		}

		int expected = original.GetWidth() * scale;
		if (upscaled.GetWidth() != expected)
		{
			GD.PushWarning(
				$"[tex] {backend.Id} returned {upscaled.GetWidth()}px for a {scale}x of " +
				$"{original.GetWidth()}px on {label}");
			return null;
		}

		if (compress)
		{
			upscaled.GenerateMipmaps();

			// A Normal map IS compressed as A Normal map: BC5 keeps two channels at full
			// precision, where DXT's 5:6:5 endpoints put visible banding into exactly the smooth
			// gradients a normal map is made of.
			CompressOrReport(upscaled, semantic == TexSemantic.Normal, "enhanced");
		}

		// Enhanced: hundreds of milliseconds to rebuild, so the sweep keeps it longest.
		Engine_TexCache.Store(key, upscaled, hasAlpha, TexCacheCost.Enhanced);
		return upscaled;
	}

	/// <summary>
	/// Separators are normalised because the same texture is named with backslashes in the
	/// material record and forward slashes in the bank index; a map keyed on the raw string
	/// would hold both and each would miss the other's entry.
	/// </summary>
	private static string Normalise(string virtualPath) =>
		virtualPath.Replace('\\', '/').TrimStart('/');

	private static string MemKey(string path, TexSemantic semantic) =>
		$"{path} {(int)semantic}";


	/// <summary>
	/// The title's translucency marker for a texture: header flag 0x80 (the flags dword the
	/// header parser `sub_82B8BA38` reads into a1[3]; `sub_82AA04A0` turns it into the
	/// texture set's +154 byte, which routes the draw to the blended bucket). Cached per path.
	/// </summary>
	public static bool IsTranslucent(string virtualPath, Func<string, byte[]?> read) =>
		(HeaderFlags(virtualPath, read) & 0x80) != 0;

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FlagsByPath = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// A texture header's flags dword (`sub_82B8BA38`'s a1[3]), whose alpha bits the title turns
	/// into the texture set's draw bytes (`sub_82AA04A0`): +152 alpha test (0x20), +153 cutout
	/// only ((flags &amp; 0xA0) == 0x20, ALPHAREF 128 instead of 254), +154 translucent (0x80).
	/// 0 when the texture does not resolve. Cached per path.
	/// </summary>
	public static int HeaderFlags(string virtualPath, Func<string, byte[]?> read)
	{
		if (string.IsNullOrWhiteSpace(virtualPath))
		{
			return 0;
		}
		return FlagsByPath.GetOrAdd(virtualPath, path =>
			read(path) is { } bytes && TexHeader.TryParse(bytes, out TexHeader header, out _) ? header.Flags : 0);
	}
}
