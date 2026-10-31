using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Refii.Level;

/// <summary>Caches decoded assets only for one level build, with bounded retention and read concurrency.</summary>
/// <remarks>The retained-byte limit does not bound a required asset's size, gzip scratch space,
/// references held by consumers, or the underlying bank-container cache.</remarks>
public sealed class LevelReads
{
    /// <summary>Immutable counters; completed-build snapshots retain no assets or cache owner.</summary>
    /// <param name="Id">The process-local level-read instance identity.</param>
    /// <param name="LimitBytes">The configured maximum bytes retained by this cache.</param>
    /// <param name="HeldBytes">Bytes referenced by cache entries when sampled.</param>
    /// <param name="PeakHeldBytes">The greatest admitted cache size.</param>
    /// <param name="Entries">Cached present and absent paths.</param>
    /// <param name="CacheHits">Reads satisfied by an already cached entry.</param>
    /// <param name="CoalescedReads">Callers sharing an overlapping read of the same canonical path.</param>
    /// <param name="SourceLoads">Logical reads which entered the underlying reader.</param>
    /// <param name="SourceCalls">Underlying reader calls, including alternate-path probes.</param>
    /// <param name="SourceBytes">Total bytes returned by the underlying reader before gzip decoding.</param>
    /// <param name="DecodedBytes">Total bytes returned after optional gzip decoding.</param>
    /// <param name="LargestDecodedBytes">The largest individual decoded asset.</param>
    /// <param name="Inflations">Successful gzip decodes.</param>
    /// <param name="ActiveReaders">Threads currently owning an underlying-read scope.</param>
    /// <param name="PeakReaders">The greatest number of simultaneous underlying-reader threads.</param>
    /// <param name="PendingPaths">Canonical paths with an active or waiting single-flight request.</param>
    /// <param name="OversizeSkips">Present assets not cached because they exceed the per-file limit.</param>
    /// <param name="BudgetSkips">Present assets not cached because their complete size would exceed the budget.</param>
    /// <param name="Failures">Underlying read or decode exceptions; failed reads remain retryable.</param>
    public readonly record struct Statistics(long Id, long LimitBytes, long HeldBytes, long PeakHeldBytes,
        int Entries, long CacheHits, long CoalescedReads, long SourceLoads, long SourceCalls,
        long SourceBytes, long DecodedBytes, long LargestDecodedBytes, long Inflations,
        int ActiveReaders, int PeakReaders, int PendingPaths, long OversizeSkips, long BudgetSkips, long Failures)
    {
        /// <summary>Formats bounded aggregate diagnostics without asset names or per-file logging.</summary>
        public override string ToString() =>
            $"id={Id} held={HeldBytes}/{LimitBytes}B peak={PeakHeldBytes}B entries={Entries} " +
            $"hits={CacheHits} shared={CoalescedReads} loads={SourceLoads} calls={SourceCalls} " +
            $"source={SourceBytes}B decoded={DecodedBytes}B largest={LargestDecodedBytes}B " +
            $"gzip={Inflations} readers={ActiveReaders}/{PeakReaders} pending={PendingPaths} " +
            $"oversize={OversizeSkips} budget={BudgetSkips} failures={Failures}";
    }

    private static long _nextId;
    private static readonly object CompletedLock = new();
    private static Statistics? _lastCompleted;
    [ThreadStatic] private static HashSet<LevelReads>? _ownedReadScopes;
    [ThreadStatic] private static HashSet<(LevelReads Owner, string Path)>? _ownedReadPaths;
    private readonly long _id = Interlocked.Increment(ref _nextId);
    private readonly Func<string, byte[]?> _raw;
    private readonly long _fileLimit, _totalLimit;
    private readonly int _prefetchWorkers;
    private readonly SemaphoreSlim? _readSlots;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, byte[]?> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<byte[]?>> _pending = new(StringComparer.Ordinal);
    private long _held, _peakHeld, _hits, _coalesced, _loads, _calls, _sourceBytes,
        _decodedBytes, _largestDecoded, _inflations, _oversize, _budget, _failures;
    private int _activeReaders, _peakReaders;
    private bool _released;

    /// <summary>Uses smaller retention and decode concurrency on Android; desktop defaults remain unchanged.</summary>
    /// <param name="raw">Reads one logical asset from the committed mount.</param>
    public LevelReads(Func<string, byte[]?> raw) : this(raw, Refii.Core.Engine_Platform.IsAndroid) { }

    /// <summary>Selects the production platform policy without requiring a Godot runtime in tests.</summary>
    /// <param name="raw">Reads one logical asset from the committed mount.</param>
    /// <param name="android">Uses Android retention and source-worker bounds when true.</param>
    internal LevelReads(Func<string, byte[]?> raw, bool android)
        : this(raw, android ? 256L << 20 : 512L << 20, 64L << 20,
            android ? 2 : Math.Min(Math.Max(2, Environment.ProcessorCount - 1), 8), android ? 2 : 0) { }

    /// <summary>Supplies deterministic bounds for pure tests and specialized build readers.</summary>
    /// <param name="raw">The underlying synchronous asset reader.</param>
    /// <param name="totalLimit">Maximum retained decoded bytes; zero disables positive-size retention.</param>
    /// <param name="fileLimit">Maximum size of one retained decoded asset.</param>
    /// <param name="prefetchWorkers">Maximum workers used by one prefetch operation.</param>
    /// <param name="concurrentReads">Maximum underlying-reader threads, or zero for the existing desktop behavior.</param>
    internal LevelReads(Func<string, byte[]?> raw, long totalLimit, long fileLimit,
        int prefetchWorkers, int concurrentReads)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (totalLimit < 0 || fileLimit < 0 || prefetchWorkers < 1 || concurrentReads < 0)
            throw new ArgumentOutOfRangeException(nameof(totalLimit), "Cache bounds must be nonnegative and workers positive");
        _raw = raw;
        _totalLimit = totalLimit;
        _fileLimit = fileLimit;
        _prefetchWorkers = prefetchWorkers;
        _readSlots = concurrentReads == 0 ? null : new SemaphoreSlim(concurrentReads, concurrentReads);
    }

    /// <summary>Returns the latest completed build's counters, not a measurement of currently live arrays.</summary>
    public static Statistics? LastCompleted { get { lock (CompletedLock) return _lastCompleted; } }

    /// <summary>Samples counters without transferring cache or array ownership.</summary>
    public Statistics Snapshot()
    {
        lock (_cacheLock)
            return new(_id, _totalLimit, _held, _peakHeld, _cache.Count,
                Interlocked.Read(ref _hits), Interlocked.Read(ref _coalesced), Interlocked.Read(ref _loads),
                Interlocked.Read(ref _calls), Interlocked.Read(ref _sourceBytes), Interlocked.Read(ref _decodedBytes),
                Interlocked.Read(ref _largestDecoded), Interlocked.Read(ref _inflations),
                Volatile.Read(ref _activeReaders), Volatile.Read(ref _peakReaders), _pending.Count,
                Interlocked.Read(ref _oversize), Interlocked.Read(ref _budget), Interlocked.Read(ref _failures));
    }

    /// <summary>Publishes scalar diagnostics after build consumers have joined, then drops the cached assets.</summary>
    /// <remarks>
    /// The cache is the build's: up to 256 MB on Android of whole decoded files, most of them on
    /// the large object heap. Anything still holding this reader after the build (a closure over
    /// <see cref="Read"/>) would keep all of it alive, so the build's end empties it, and a
    /// later read goes to the source and is not retained.
    /// </remarks>
    /// <returns>The completed build's immutable counters.</returns>
    public Statistics Complete()
    {
        Statistics stats = Snapshot();
        lock (CompletedLock) _lastCompleted = stats;
        lock (_cacheLock)
        {
            _released = true;
            _cache.Clear();
            _cache.TrimExcess();
            _held = 0;
        }

        return stats;
    }

    /// <summary>Reads and optionally inflates one asset, coalescing overlapping requests for its canonical path.</summary>
    /// <param name="path">A logical path, optionally prefixed by data/.</param>
    /// <returns>Decoded bytes or a cached absence; read exceptions propagate and may be retried.</returns>
    public byte[]? Read(string path)
    {
        string key = Canonical(path);
        if (TryCached(key, out byte[]? cached)) return cached;
        // Do not wait for another thread's Lazy while holding a slot that thread may need.
        // Nested callbacks share their thread's slot and may duplicate an overlapping load;
        // ordinary callers still use single flight. Production mount readers do not reenter.
        if (_ownedReadScopes?.Contains(this) == true) return Load(path, key);
        var created = new Lazy<byte[]?>(() => Load(path, key), LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<byte[]?> pending = _pending.GetOrAdd(key, created);
        if (!ReferenceEquals(created, pending)) Interlocked.Increment(ref _coalesced);
        try { return pending.Value; }
        finally
        {
            // Conditional removal matters: a late waiter must not remove a newer retry's entry.
            ((ICollection<KeyValuePair<string, Lazy<byte[]?>>>)_pending).Remove(new(key, pending));
        }
    }

    /// <summary>Looks up cached bytes and missing paths under the same gate used for size-aware admission.</summary>
    private bool TryCached(string key, out byte[]? bytes)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(key, out bytes)) return false;
            Interlocked.Increment(ref _hits);
            return true;
        }
    }

    /// <summary>Owns one logical source read and admits only complete decoded assets within the retained budget.</summary>
    private byte[]? Load(string path, string key)
    {
        // A previous read can finish between the caller's lookup and its pending-entry insertion.
        if (TryCached(key, out byte[]? bytes)) return bytes;
        var paths = _ownedReadPaths ??= new();
        if (!paths.Add((this, key)))
            throw new InvalidOperationException("Recursive level read of the same canonical path");
        bool ownsScope = false;
        try
        {
            ownsScope = EnterReadScope();
            // A nested owner may have supplied this path while this thread waited for a slot.
            if (TryCached(key, out bytes)) return bytes;
            Interlocked.Increment(ref _loads);
            bytes = ReadSource(path);
            if (bytes is null)
            {
                string alternate = path.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
                    ? path[5..] : "data/" + path;
                bytes = ReadSource(alternate);
            }
            if (bytes is not null && Refii.Core.Engine_GameGzip.MaybeGunzip(ref bytes))
                Interlocked.Increment(ref _inflations);
            long size = bytes?.LongLength ?? 0;
            Interlocked.Add(ref _decodedBytes, size);
            SetMaximum(ref _largestDecoded, size);
            lock (_cacheLock)
            {
                if (_released) { }
                else if (size > _fileLimit) Interlocked.Increment(ref _oversize);
                else if (size > _totalLimit - _held) Interlocked.Increment(ref _budget);
                else if (!_cache.ContainsKey(key))
                {
                    _cache.Add(key, bytes);
                    _held += size;
                    _peakHeld = Math.Max(_peakHeld, _held);
                }
            }
            return bytes;
        }
        catch
        {
            Interlocked.Increment(ref _failures);
            throw;
        }
        finally
        {
            paths.Remove((this, key));
            if (ownsScope) ExitReadScope();
        }
    }

    /// <summary>Measures source bytes before optional gzip expansion.</summary>
    private byte[]? ReadSource(string path)
    {
        Interlocked.Increment(ref _calls);
        byte[]? bytes = _raw(path);
        Interlocked.Add(ref _sourceBytes, bytes?.LongLength ?? 0);
        return bytes;
    }

    /// <summary>Limits read/decode worker threads while allowing a synchronous nested reader to reuse its slot.</summary>
    /// <remarks>The slot spans source access and gzip decoding. A nested read on the same thread does
    /// not wait for a second slot or another pending reader; the path recursion guard rejects a
    /// cycle instead of deadlocking. Production mount readers are synchronous.</remarks>
    private bool EnterReadScope()
    {
        var owned = _ownedReadScopes ??= new();
        if (owned.Contains(this)) return false;
        _readSlots?.Wait();
        owned.Add(this);
        int active = Interlocked.Increment(ref _activeReaders);
        int current;
        while (active > (current = Volatile.Read(ref _peakReaders)) &&
               Interlocked.CompareExchange(ref _peakReaders, active, current) != current) { }
        return true;
    }

    /// <summary>Releases the thread's single underlying-reader slot after success or failure.</summary>
    private void ExitReadScope()
    {
        _ownedReadScopes!.Remove(this);
        Interlocked.Decrement(ref _activeReaders);
        _readSlots?.Release();
    }

    /// <summary>Publishes a monotonic aggregate maximum without serializing payload work.</summary>
    private static void SetMaximum(ref long location, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref location)) &&
               Interlocked.CompareExchange(ref location, value, current) != current) { }
    }

    /// <summary>Warms manifest paths with bounded workers; missing and failed paths are returned for one summary.</summary>
    /// <param name="paths">The requested logical paths.</param>
    /// <returns>The number found and the paths absent or unreadable during this prefetch.</returns>
    public (int Fetched, List<string> Missing) Prefetch(IReadOnlyList<string> paths)
    {
        var missing = new List<string>();
        if (paths.Count == 0) return (0, missing);
        int workers = Math.Min(_prefetchWorkers, paths.Count);
        int cursor = -1, fetched = 0;
        var missingBag = new ConcurrentBag<string>();
        var pool = new Task[workers];
        for (int w = 0; w < workers; ++w)
            pool[w] = Task.Run(() =>
            {
                for (;;)
                {
                    int i = Interlocked.Increment(ref cursor);
                    if (i >= paths.Count) return;
                    byte[]? bytes;
                    try { bytes = Read(paths[i]); }
                    catch (Exception) { bytes = null; }
                    if (bytes is not null) Interlocked.Increment(ref fetched);
                    else missingBag.Add(paths[i]);
                }
            });
        Task.WaitAll(pool);
        missing.AddRange(missingBag);
        return (fetched, missing);
    }

    /// <summary>Shares data/ aliases, slash styles and case through one cache and in-flight key.</summary>
    private static string Canonical(string path)
    {
        string key = path.Replace('\\', '/').ToLowerInvariant().TrimStart('/');
        return key.StartsWith("data/", StringComparison.Ordinal) ? key[5..] : key;
    }
}
