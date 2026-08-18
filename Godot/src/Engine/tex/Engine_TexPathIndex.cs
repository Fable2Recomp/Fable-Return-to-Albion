using System;
using System.Collections.Generic;

namespace Refii.Textures;

public static class Engine_TexPathIndex
{
    public const uint NullHash = 0x811C9DC5u;

    private static readonly object IndexLock = new();

    private static Func<IEnumerable<string>>? _paths;

    private static Dictionary<uint, string>? _byPathHash;

    public static void SetPathSource(Func<IEnumerable<string>>? source)
    {
        lock (IndexLock)
        {
            _paths = source;
            _byPathHash = null;
        }
    }

    public static void Invalidate()
    {
        lock (IndexLock)
        {
            _byPathHash = null;
        }
    }

    public static uint PathHash(string path)
    {
        uint hash = NullHash;
        foreach (char c in path.ToLowerInvariant())
        {
            byte b = (byte)(c == '/' ? '\\' : c);
            hash *= 0x01000193u;
            hash ^= b;
        }

        return hash;
    }

    public static int Count
    {
        get
        {
            lock (IndexLock)
            {
                BuildLocked();
                return _byPathHash?.Count ?? 0;
            }
        }
    }

    public static string? Resolve(uint pathHash)
    {
        if (pathHash == 0 || pathHash == NullHash)
        {
            return null;
        }

        lock (IndexLock)
        {
            BuildLocked();

            return _byPathHash is not null &&
                _byPathHash.TryGetValue(pathHash, out string? path)
                    ? path
                    : null;
        }
    }

    private static void BuildLocked()
    {
        if (_byPathHash is not null)
        {
            return;
        }

        var index = new Dictionary<uint, string>();

        if (_paths is not null)
        {
            foreach (string path in _paths())
            {
                index.TryAdd(PathHash(path), path);
            }
        }

        _byPathHash = index;
    }
}
