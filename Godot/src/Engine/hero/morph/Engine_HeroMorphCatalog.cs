using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Hero;

public sealed class HeroMorphSet
{
    public string NeutralPath { get; init; } = string.Empty;

    /// <summary>Every MorphTargets record whose OriginalModel is this part, with its MorphType.</summary>
    public List<(uint Type, string Path)> Targets { get; } = new();

    public bool Any => Targets.Count != 0;
}

/// <summary>
/// A part's morph targets, found the way the guest finds them: by equality between the part's
/// model hash and <c>OriginalModel</c> in the MorphTargets records (<c>sub_8220DE88</c>).
/// </summary>
public static class Engine_HeroMorphCatalog
{
    private static readonly object CatalogLock = new();

    private static readonly Dictionary<string, HeroMorphSet> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static Func<string, byte[]?>? _resolver;

    private static List<MorphTargetPair>? _pairs;

    public static void SetResolver(Func<string, byte[]?>? resolver)
    {
        lock (CatalogLock)
        {
            _resolver = resolver;
            _pairs = null;
            Cache.Clear();
        }
    }

    public static void Invalidate()
    {
        lock (CatalogLock)
        {
            _pairs = null;
            Cache.Clear();
        }
    }

    public static HeroMorphSet? For(string neutralPath)
    {
        if (neutralPath.Length == 0)
        {
            return null;
        }

        lock (CatalogLock)
        {
            if (Cache.TryGetValue(neutralPath, out HeroMorphSet? cached))
            {
                return cached.Any ? cached : null;
            }
        }

        HeroMorphSet built = Discover(neutralPath);

        lock (CatalogLock)
        {
            Cache[neutralPath] = built;
        }

        return built.Any ? built : null;
    }

    private static HeroMorphSet Discover(string neutralPath)
    {
        var set = new HeroMorphSet { NeutralPath = neutralPath };

        string? original = Engine_PropModelIndex.ResolveByPath(neutralPath);
        uint neutralHash = Engine_PropModelIndex.PathHash(original ?? neutralPath);

        foreach (MorphTargetPair pair in Pairs())
        {
            if (pair.OriginalModelHash != neutralHash)
            {
                continue;
            }

            if (Engine_PropModelIndex.Resolve(
                    pair.TargetModelHash, Array.Empty<string>()) is string target)
            {
                set.Targets.Add((pair.MorphType, target));
            }
        }

        return set;
    }

    private static List<MorphTargetPair> Pairs()
    {
        lock (CatalogLock)
        {
            if (_pairs is not null)
            {
                return _pairs;
            }

            if (_resolver is null)
            {
                _pairs = new List<MorphTargetPair>();
                return _pairs;
            }
        }

        var blobs = new List<byte[]?>();
        foreach (string path in Engine_LevelProps.SupplementalGdbPaths)
        {
            blobs.Add(_resolver(path) ?? _resolver("data/" + path));
        }

        List<MorphTargetPair> built = Engine_GdbMorphTargets.Build(GdbMulti.Build(blobs));
        GD.Print($"[morph] {built.Count} authored morph target pair(s)");

        lock (CatalogLock)
        {
            _pairs = built;
        }

        return built;
    }
}
