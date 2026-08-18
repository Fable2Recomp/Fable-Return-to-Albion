using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Hero;

public enum SkeletalMorphKind
{
    Fat,
    Strong,
    Tall,
}

public sealed class BoneMorphOffset
{
    public float[] Position { get; } = new float[3];

    public float[] Scale { get; } = { 1.0f, 1.0f, 1.0f };

    public bool Any { get; set; }
}

public static class Engine_SkeletalMorphs
{
    public const string BankName = "skeletalmorphs.bnk";

    public const string FemaleMarker = "fhero";

    public const string Extension = ".mof";

    public const float WeightEpsilon = 0.0001f;

    private static readonly object MorphLock = new();

    private static readonly Dictionary<string, List<MofBoneOffset>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static Func<IEnumerable<string>>? _pathSource;

    private static Func<string, byte[]?>? _resolver;

    public static void SetSource(
        Func<IEnumerable<string>>? pathSource, Func<string, byte[]?>? resolver)
    {
        lock (MorphLock)
        {
            _pathSource = pathSource;
            _resolver = resolver;
            Cache.Clear();
        }
    }

    public static void Invalidate()
    {
        lock (MorphLock)
        {
            Cache.Clear();
        }
    }

    public static string TokenOf(SkeletalMorphKind kind) => kind switch
    {
        SkeletalMorphKind.Fat => "fat",
        SkeletalMorphKind.Strong => "strong",
        SkeletalMorphKind.Tall => "tall",
        _ => string.Empty,
    };

    public static HeroMorphKind? VertexKindOf(SkeletalMorphKind kind) => kind switch
    {
        SkeletalMorphKind.Fat => HeroMorphKind.Fat,
        SkeletalMorphKind.Strong => HeroMorphKind.Strong,
        _ => null,
    };

    public static List<MofBoneOffset> Offsets(SkeletalMorphKind kind, bool female)
    {
        string key = $"{TokenOf(kind)}|{(female ? "f" : "m")}";

        lock (MorphLock)
        {
            if (Cache.TryGetValue(key, out List<MofBoneOffset>? cached))
            {
                return cached;
            }
        }

        List<MofBoneOffset> built = Load(kind, female);

        lock (MorphLock)
        {
            Cache[key] = built;
        }

        return built;
    }

    private static List<MofBoneOffset> Load(SkeletalMorphKind kind, bool female)
    {
        Func<IEnumerable<string>>? source;
        Func<string, byte[]?>? resolver;

        lock (MorphLock)
        {
            source = _pathSource;
            resolver = _resolver;
        }

        if (source is null || resolver is null)
        {
            return new List<MofBoneOffset>();
        }

        string token = TokenOf(kind);
        string? chosen = null;

        foreach (string path in source())
        {
            string lower = path.Replace('\\', '/').ToLowerInvariant();

            if (!lower.EndsWith(Extension, StringComparison.Ordinal) ||
                !lower.Contains(token, StringComparison.Ordinal))
            {
                continue;
            }

            bool femaleFile = lower.Contains(FemaleMarker, StringComparison.Ordinal);
            if (femaleFile != female)
            {
                continue;
            }

            chosen = path;
            break;
        }

        if (chosen is null)
        {
            return new List<MofBoneOffset>();
        }

        byte[]? bytes = resolver(chosen);
        if (bytes is null)
        {
            return new List<MofBoneOffset>();
        }

        List<MofBoneOffset> offsets = Engine_MofFile.Parse(bytes);
        GD.Print($"[morph] skeletal {token} ({(female ? "female" : "male")}): " +
            $"{offsets.Count} shadow bone(s) from {chosen}");
        return offsets;
    }

    public static Dictionary<string, BoneMorphOffset> Accumulate(
        HeroMorphWeights weights, bool female)
    {
        var accumulated = new Dictionary<string, BoneMorphOffset>(
            StringComparer.OrdinalIgnoreCase);

        foreach (SkeletalMorphKind kind in Enum.GetValues<SkeletalMorphKind>())
        {
            float weight = Engine_HeroMorphState.SkeletalWeight(weights, kind);
            if (weight <= WeightEpsilon)
            {
                continue;
            }

            foreach (MofBoneOffset offset in Offsets(kind, female))
            {
                if (!accumulated.TryGetValue(offset.Name, out BoneMorphOffset? bone))
                {
                    bone = new BoneMorphOffset();
                    accumulated[offset.Name] = bone;
                }

                bone.Any = true;
                for (int axis = 0; axis < 3; axis++)
                {
                    bone.Position[axis] += offset.Position[axis] * weight;
                    bone.Scale[axis] *= 1.0f + ((offset.Scale[axis] - 1.0f) * weight);
                }
            }
        }

        return accumulated;
    }

    public static float WeightOf(HeroMorphWeights weights, SkeletalMorphKind kind) => kind switch
    {
        SkeletalMorphKind.Fat => weights.Fat,
        SkeletalMorphKind.Strong => weights.Strong,
        SkeletalMorphKind.Tall => weights.Tall,
        _ => 0.0f,
    };
}
