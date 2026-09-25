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

	private static long _sequence;

	private static bool _workersStarted;

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
		hasAlpha = false;

		if (string.IsNullOrWhiteSpace(virtualPath))
		{
			return null;
		}

		string path = Normalise(virtualPath);
		string memKey = MemKey(path, semantic);

		// Locked, and the decode stays outside it. A level change has the outgoing region paging
		// props in on the main thread while the incoming one builds on a worker, so both reach
		// this map. A Dictionary torn by a concurrent resize does not lose an entry — it corrupts
		// its buckets, and the next lookup never returns. Two threads racing to decode the same
		// texture costs one redundant decode and nothing else.
		lock (Gate)
		{
			if (Live.TryGetValue(memKey, out Entry? cached))
			{
				hasAlpha = cached.HasAlpha;
				return cached.Texture;
			}

			// A recent failure is not retried on every ask, but it is not final either.
			if (FailedUntil.TryGetValue(memKey, out long retryAt) &&
				System.Diagnostics.Stopwatch.GetTimestamp() < retryAt)
			{
				return null;
			}
		}

		Image? image = Decode(path, semantic, read, out bool decodedAlpha, out byte[]? source);

		// A failed decode is not cached: a read can fail transiently (a bank not yet readable
		// during a region change), and a material with no albedo samples the shader's default,
		// white. Retried after FailedRetrySeconds instead.
		if (image is null)
		{
			lock (Gate)
			{
				FailedDecodes++;
				FailedUntil[memKey] = System.Diagnostics.Stopwatch.GetTimestamp() +
					System.Diagnostics.Stopwatch.Frequency * FailedRetrySeconds;
			}

			return null;
		}

		// AN ENHANCEMENT ALREADY GENERATED IS THE TEXTURE, from the first frame. However it was made
		// — the boot sweep, during play, a prebuild — it is on disk under its own key, and handing
		// out the original first only to swap it moments later uploaded every texture twice and
		// changed its size under whatever held it. Only a texture with nothing generated yet
		// starts as the original and takes the in-place swap when its enhancement lands.
		bool enhanced = false;
		if (image is not null && source is not null && Engine_TexEnhance.Enabled &&
			Engine_TexSemantic.For(semantic).Filter != TexFilterClass.None &&
			EnhanceDecoded(source, image, semantic, decodedAlpha, path, compress: true, cacheOnly: true)
				is { } generated)
		{
			image = generated;
			enhanced = true;
		}

		ImageTexture? texture = image is null ? null : ImageTexture.CreateFromImage(image);

		lock (Gate)
		{
			// Another thread may have finished the same decode while this one ran. Keep whichever
			// entry landed first so that one Texture2D instance answers for one texture — which
			// matters more now than it did for memory alone, because the in-place swap updates
			// whatever instance is in this map and a second instance would never be updated.
			if (Live.TryGetValue(memKey, out Entry? raced))
			{
				hasAlpha = raced.HasAlpha;
				return raced.Texture;
			}

			FailedUntil.Remove(memKey);
			Live[memKey] = new Entry
			{
				Texture = texture,
				HasAlpha = decodedAlpha,
				Path = path,
				Semantic = semantic,
				Read = read,
				State = enhanced ? EnhancementState.Enhanced : EnhancementState.None,
			};
		}

		hasAlpha = decodedAlpha;
		return texture;
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
	public static int Pump(int budget = 8)
	{
		int applied = 0;
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
		string virtualPath, ImageTexture texture, Func<string, byte[]?> read)
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
		Image? original = Engine_TexCache.TryLoad(baseKey, out bool hasAlpha);
		if (original is null)
		{
			try
			{
				original = Engine_TexImage.TryCreateImage(source, out hasAlpha, out _);
			}
			catch (Exception)
			{
				return false;
			}

			if (original is null)
			{
				return false;
			}

			Engine_TexCache.Store(baseKey, original, hasAlpha);
		}

		// EnhanceDecoded is a no-op when the setting is off or the policy declines, and it stores
		// its own result, so there is nothing to do with what it returns.
		EnhanceDecoded(source, original, semantic, hasAlpha, path);
		return true;
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

		return BuildOrmImage(
			path, normal, id, powerMin, powerMax, aoStrength, metallicStrength, withHeight, read,
			out _)
			is not null;
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

	private static readonly Dictionary<string, (ImageTexture? Texture, bool HasHeight)> LiveOrm =
		new(StringComparer.Ordinal);

	/// <summary>
	/// Builds, caches and serves the packed ORM texture for a material's specular map.
	/// </summary>
	/// <remarks>
	/// Keyed on the specular source AND the exponent range, because the packing depends on both.
	/// Every shipped record states (1, 100) so in practice the range is one value, but a material
	/// that varied it would otherwise be handed another material's roughness — and a cache that
	/// can serve the wrong answer for a reason nobody wrote down is worse than no cache.
	///
	/// Its own map, like the cubes, because it is a derived texture: nothing else names it, so
	/// keying it by the specular path plus the range keeps it out of the slot-keyed live map where
	/// a plain path would collide with the specular itself.
	/// </remarks>
	public static ImageTexture? GetOrm(
		string specularPath,
		string normalPath,
		float powerMin,
		float powerMax,
		Func<string, byte[]?> read,
		out bool hasHeight)
	{
		hasHeight = false;

		// A specular OR a normal map is enough — AO and height derive from the normal alone,
		// and roughly a third of the asset set names a normal without a specular.
		if (!Engine_TexSettings.OrmEnabled ||
			(string.IsNullOrWhiteSpace(specularPath) && string.IsNullOrWhiteSpace(normalPath)))
		{
			return null;
		}

		string path = Normalise(specularPath);
		string normal = Normalise(normalPath);
		float aoStrength = Engine_TexSettings.OrmAoStrength;
		float metallicStrength = Engine_TexSettings.OrmMetallicStrength;
		bool withHeight = Engine_TexSettings.OrmHeight;

		// The NORMAL and the strength are part of the identity because the red channel is derived
		// from them. Leaving them out would serve a cavity map built from another material's
		// normals, or at another strength, and nothing would report it.
		// The metallic strength joins the identity for the same reason the AO strength did: it
		// changes the blue channel, so an entry packed at one strength must not answer for another.
		string id = $"{Engine_TexOrm.CacheId(powerMin, powerMax)}-ao{aoStrength:F2}-" +
			$"me{metallicStrength:F2}-h{(withHeight ? 1 : 0)}-{normal}";
		string memKey = $"{path} {id}";

		lock (Gate)
		{
			if (LiveOrm.TryGetValue(memKey, out (ImageTexture? Texture, bool HasHeight) cached))
			{
				hasHeight = cached.HasHeight;
				return cached.Texture;
			}
		}

		Image? packed = BuildOrmImage(
			path, normal, id, powerMin, powerMax, aoStrength, metallicStrength, withHeight,
			read, out bool packedHeight);
		ImageTexture? built = packed is null ? null : ImageTexture.CreateFromImage(packed);

		lock (Gate)
		{
			if (LiveOrm.TryGetValue(memKey, out (ImageTexture? Texture, bool HasHeight) raced))
			{
				hasHeight = raced.HasHeight;
				return raced.Texture;
			}

			LiveOrm[memKey] = (built, packedHeight);
		}

		hasHeight = packedHeight;
		return built;
	}

	/// <summary>
	/// Packs an ORM straight from already-resolved textures, bypassing the path-keyed cache.
	/// </summary>
	/// <remarks>
	/// For sources that do not live in a bank — the hero's morphed specular and normal. The
	/// path-keyed cache would pack the UNMORPHED bytes under the same identity, so a morphed
	/// material would bind maps and an ORM that disagree. Uncached on purpose: the hero is a
	/// handful of materials rebuilt when the morph changes, which is exactly when the ORM must
	/// change too.
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

		Image? spec = specular?.GetImage();
		Image? norm = normal?.GetImage();
		if (spec is null && norm is null)
		{
			return null;
		}

		Image? packed = Engine_TexOrm.TryPack(
			spec, norm, powerMin, powerMax,
			Engine_TexSettings.OrmAoStrength, Engine_TexSettings.OrmMetallicStrength,
			Engine_TexSettings.OrmHeight, out bool packedHeight);
		if (packed is null)
		{
			return null;
		}

		HalvePackedOrm(packed);
		packed.GenerateMipmaps();
		CompressOrReport(packed, isNormalMap: false, "orm");
		hasHeight = packedHeight;
		return ImageTexture.CreateFromImage(packed);
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

		Image? specular = source is null
			? null
			: Engine_TexImage.TryCreateImage(source, out _, out _);
		if (specular is null && source is not null)
		{
			return null;
		}

		Image? normal = normalBytes is null
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

			lock (Gate)
			{
				InFlight.Remove(job.MemKey);
			}

			if (produced is null)
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
			Completions.Enqueue(() =>
			{
				ImageTexture? target = entry.Target;
				if (target is null || !GodotObject.IsInstanceValid(target))
				{
					// The UI texture was freed by Godot between the request and the apply. Not a
					// fault: the work is in the cache, so if it is loaded again the re-request
					// costs a disk read rather than another resample.
					return;
				}

				// The swap, and the reason nothing had to register a callback: every material
				// holding this Texture2D is holding THIS object, so replacing its image updates
				// all of them at once. SetImage rather than Update because the size changes.
				target.SetImage(image);
				lock (Gate)
				{
					entry.State = kind == JobKind.Enhance
						? EnhancementState.Enhanced
						: EnhancementState.None;
				}
			});
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

		Image? original = Engine_TexImage.TryCreateImage(source, out bool hasAlpha, out _);
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
		if (result is not null || cacheOnly)
		{
			return result;
		}

		// The backend is handed a plain RGBA8 surface with no mip chain, because every backend
		// would otherwise have to undo the block compression itself and they would each get it
		// slightly differently.
		var work = (Image)original.Duplicate();
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
}
