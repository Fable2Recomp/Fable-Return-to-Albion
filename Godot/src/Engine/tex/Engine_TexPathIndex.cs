using System;
using System.Collections.Generic;

namespace Refii.Textures;

/// <summary>
/// Texture path by the guest's texture id. The id is <see cref="Refii.Core.Engine_GameHash.ResourcePath"/>
/// of the bank entry's name (Fable 2 `sub_822CA588`, Fable 3 `sub_8312F3D8`: FNV-1 over the
/// lower-cased, backslashed `art\…\x.tex`), and a format-4 bank keys its records by that same hash,
/// so the mount's entry names ARE the table: <c>Engine_GameAssets</c> feeds every <c>.tex</c> of the
/// mounted banks for whichever title is active, and a lookup resolves either title's ids through
/// this one index.
/// </summary>
public static class Engine_TexPathIndex
{
    public const uint NullHash = 0x811C9DC5u;

    private static readonly object IndexLock = new();

    private static Func<IEnumerable<string>>? _paths;

    private static Dictionary<uint, string>? _byPathHash;

    private static readonly Dictionary<uint, string> _registeredPaths = new();

    public static void SetPathSource(Func<IEnumerable<string>>? source)
    {
        lock (IndexLock)
        {
            _paths = source;
            if (source is null)
            {
                _registeredPaths.Clear();
            }
            _byPathHash = null;
        }
    }

    public static void RegisterPath(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            return;
        }

        string normalized = virtualPath.Trim().Replace('/', '\\');
        uint hash = PathHash(normalized);

        lock (IndexLock)
        {
            _registeredPaths[hash] = normalized;
            if (_byPathHash is not null)
            {
                _byPathHash[hash] = normalized;
            }
        }
    }

    public static void Invalidate()
    {
        lock (IndexLock)
        {
            _byPathHash = null;
        }
    }

    /// <summary>The guest's id for a texture path: FNV-1 of its lower-cased, backslashed spelling.</summary>
    public static uint PathHash(string path) => Refii.Core.Engine_GameHash.ResourcePath(path);

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

        foreach ((uint regHash, string regPath) in _registeredPaths)
        {
            index[regHash] = regPath;
        }

        _byPathHash = index;
    }
}
