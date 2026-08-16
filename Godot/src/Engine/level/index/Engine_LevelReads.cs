using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Refii.Level;

public sealed class LevelReads
{
    private const long FileLimit = 64L << 20;

    private const long TotalLimit = 512L << 20;

    private readonly Func<string, byte[]?> _raw;

    private readonly ConcurrentDictionary<string, byte[]?> _cache = new(StringComparer.Ordinal);

    private long _held;

    public LevelReads(Func<string, byte[]?> raw) => _raw = raw;

    public byte[]? Read(string path)
    {
        string key = Canonical(path);
        if (_cache.TryGetValue(key, out byte[]? cached))
        {
            return cached;
        }

        byte[]? bytes = _raw(path);
        if (bytes is null)
        {
            string alternate = path.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
                ? path[5..]
                : "data/" + path;
            bytes = _raw(alternate);
        }

        if (bytes is not null)
        {
            Refii.Core.Engine_GameGzip.MaybeGunzip(ref bytes);
        }

        long size = bytes?.LongLength ?? 0;
        if (size <= FileLimit &&
            Interlocked.Read(ref _held) < TotalLimit &&
            _cache.TryAdd(key, bytes))
        {
            Interlocked.Add(ref _held, size);
        }

        return bytes;
    }

    public (int Fetched, List<string> Missing) Prefetch(IReadOnlyList<string> paths)
    {
        var missing = new List<string>();
        if (paths.Count == 0)
        {
            return (0, missing);
        }

        int workers = Math.Min(Math.Max(2, Environment.ProcessorCount - 1), 8);
        workers = Math.Min(workers, paths.Count);

        int cursor = -1;
        int fetched = 0;
        var missingBag = new ConcurrentBag<string>();

        var pool = new Task[workers];
        for (int w = 0; w < workers; ++w)
        {
            pool[w] = Task.Run(() =>
            {
                for (;;)
                {
                    int i = Interlocked.Increment(ref cursor);
                    if (i >= paths.Count)
                    {
                        return;
                    }

                    byte[]? bytes;
                    try
                    {
                        bytes = Read(paths[i]);
                    }
                    catch (Exception)
                    {
                        bytes = null;
                    }

                    if (bytes is not null)
                    {
                        Interlocked.Increment(ref fetched);
                    }
                    else
                    {
                        missingBag.Add(paths[i]);
                    }
                }
            });
        }

        Task.WaitAll(pool);
        missing.AddRange(missingBag);
        return (fetched, missing);
    }

    private static string Canonical(string path)
    {
        string key = path.Replace('\\', '/').ToLowerInvariant().TrimStart('/');
        return key.StartsWith("data/", StringComparison.Ordinal) ? key[5..] : key;
    }
}
