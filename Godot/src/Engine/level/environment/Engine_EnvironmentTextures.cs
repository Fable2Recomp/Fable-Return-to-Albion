using System;
using System.Collections.Generic;
using Godot;
using Refii.Textures;

namespace Refii.Level;

public static class Engine_EnvironmentTextures
{
    public const uint NullHash = 0x811C9DC5u;

    private static Func<IEnumerable<string>>? _texturePaths;

    public static void SetTexturePathSource(Func<IEnumerable<string>>? source) =>
        _texturePaths = source;

    public static uint Hash(string value, bool lowercase)
    {
        if (lowercase)
        {
            value = value.ToLowerInvariant();
        }

        uint hash = NullHash;
        foreach (char c in value)
        {
            byte b = (byte)(c == '/' ? '\\' : c);
            hash *= 0x01000193u;
            hash ^= b;
        }

        return hash;
    }

    public static string? ResolvePath(uint hash)
    {
        if (hash == 0 || hash == NullHash || _texturePaths is null)
        {
            return null;
        }
        if (Index(false).TryGetValue(hash, out string? path))
        {
            return path;
        }
        // A bank mounted after the table was built: a miss looks again, at most every 10 s.
        return Index(true).TryGetValue(hash, out path) ? path : null;
    }

    /// <summary>
    /// Every texture path under the four hashes a slot may name it by (full or leaf, lowered or
    /// not), first path wins as the scan did. Built once per path source: the scan hashed the
    /// whole texture list four ways for every lookup, a 500 ms frame whenever a theme slot moved.
    /// </summary>
    private static Dictionary<uint, string> Index(bool missed)
    {
        Func<IEnumerable<string>>? source = _texturePaths;
        lock (IndexGate)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            bool stale = missed && now - _indexBuilt > 10 * System.Diagnostics.Stopwatch.Frequency;
            if (_index is not null && ReferenceEquals(_indexSource, source) && !stale)
            {
                return _index;
            }
            _indexBuilt = now;
            var index = new Dictionary<uint, string>();
            if (source is not null)
            {
                foreach (string path in source())
                {
                    int slash = path.LastIndexOf('/');
                    string leaf = slash < 0 ? path : path[(slash + 1)..];
                    index.TryAdd(Hash(path, true), path);
                    index.TryAdd(Hash(leaf, true), path);
                    index.TryAdd(Hash(path, false), path);
                    index.TryAdd(Hash(leaf, false), path);
                }
            }
            _index = index;
            _indexSource = source;
            return index;
        }
    }

    private static readonly object IndexGate = new();
    private static Dictionary<uint, string>? _index;
    private static Func<IEnumerable<string>>? _indexSource;
    private static long _indexBuilt;

    public static Texture2D? Load(uint hash, Func<string, byte[]?> read, out string path)
    {
        path = string.Empty;

        string? resolved = ResolvePath(hash);
        if (resolved is null)
        {
            return null;
        }

        path = resolved;

        byte[]? bytes = read(resolved);
        if (bytes is null)
        {
            return null;
        }

        return Engine_TexImage.TryCreateTexture(bytes, out _);
    }
}
