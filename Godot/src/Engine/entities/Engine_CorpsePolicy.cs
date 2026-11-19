using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Gdb;
using Refii.Host;

namespace Refii.Entities;

/// <summary>
/// Cross-title classification, retention lifetime, and memory-cap eviction policy for fallen
/// creature and NPC corpses across Fable 2 and Fable 3.
/// </summary>
/// <remarks>
/// Pure logic with zero scene-tree dependencies so every filter, CVar tier, and eviction rule is
/// verified directly in headless Tier-1 unit tests.
/// </remarks>
public static partial class Engine_CorpsePolicy
{
    /// <summary>Hot-reload CVar controlling how many seconds physical corpses persist before fade-out.</summary>
    public const string LifetimeKey = "corpse_lifetime_seconds";

    /// <summary>Hot-reload CVar controlling max active retained corpses (0 = Auto, &lt;0 = Unlimited).</summary>
    public const string MaxCountKey = "corpse_max_count";

    /// <summary>Default desktop retention duration in seconds (Balanced preset).</summary>
    public const float DefaultLifetimeSeconds = 45f;

    /// <summary>Duration of the smooth fade-out once a corpse expires or is evicted.</summary>
    public const float FadeDurationSeconds = 2.0f;

    /// <summary>Opacity threshold below which the retained corpse drops its cast shadow proxy.</summary>
    public const float ShadowDropAlpha = 0.35f;

    /// <summary>Horizontal distance in metres beyond which distant corpses are evicted first.</summary>
    public const float DistantEvictionRadiusMeters = 25.0f;

    private const uint BeetlesBaseHash = 0x88CCE036u;
    private const uint BansheesBaseHash = 0x8E3A9AF8u;
    private const uint BansheeChildHash = 0x19020080u;
    private const uint TrollsBaseHash = 0x5AAACE13u;
    private const uint SpireShardsBaseHash = 0xFDF0DC5Bu;

    private static readonly uint[] ExcludedComponentFieldHashes =
    {
        0x2B1E4BC7u, // ShadowCreatureComponent (BansheeChild, Shadow Bandit/Hobbe/Balverine/Beetle/Troll)
        0x6122274Cu, // BansheeComponent
        0xFD04EFA9u, // BeetleComponent
        0x107B894Cu, // ShardComponent
        0xEA238706u, // HollowmanWispComponent
        0xC5FFF38Bu, // TrollComponent
    };

    private static readonly string[] ExcludedTokens =
    {
        "beetle",
        "banshee",
        "ghost",
        "shadow",
        "wisp",
        "spirit",
        "spectre",
        "phantom",
        "summon",
        "shard",
        "crawler",
        "sentinel",
        "darkness",
        "poltergeist",
        "slime",
    };

    private static readonly object CacheGate = new();
    private static int _cachedGeneration = -1;
    private static float _cachedLifetime = DefaultLifetimeSeconds;
    private static int _cachedConfiguredMax;

    private static GdbMulti? _cachedGdb;
    private static readonly Dictionary<uint, bool> ExcludedRecordCache = new();

    /// <summary>Inputs evaluated to decide whether a retired guest entity should be kept as a corpse.</summary>
    public readonly record struct CorpseCandidate(
        bool IsDeadCreature,
        bool HasSkeleton,
        bool SawVisibleWhileDead,
        bool SawEtherealWhileAlive,
        bool IsPrincipal,
        bool IsCompanion,
        uint RecordHash,
        string? ModelPath,
        bool RecordExcluded);

    /// <summary>Lightweight per-corpse summary passed to <see cref="SelectEvictionIndex"/>.</summary>
    public readonly record struct CorpseSlotSummary(Vector3 Position, float Age, bool FadingOut);

    /// <summary>Configured corpse retention duration in seconds (0 disables retention; no upper cap).</summary>
    public static float LifetimeSeconds
    {
        get
        {
            RefreshFromConfig();
            return _cachedLifetime;
        }
    }

    /// <summary>Configured raw max corpse cap (0 = Auto, &lt;0 = Unlimited, &gt;0 = Explicit count).</summary>
    public static int ConfiguredMaxCount
    {
        get
        {
            RefreshFromConfig();
            return _cachedConfiguredMax;
        }
    }

    /// <summary>True when host corpse persistence is currently active.</summary>
    public static bool Enabled => LifetimeSeconds > 0f;

    static partial void ResolveCompactCap(ref bool compact);

    private static bool UseCompactCap()
    {
        bool compact = false;
        ResolveCompactCap(ref compact);
        return compact;
    }

    /// <summary>Resolved effective region corpse limit for the active config and platform tier.</summary>
    public static int EffectiveMaxCount =>
        ResolveMaxCorpses(LifetimeSeconds, ConfiguredMaxCount, UseCompactCap());

    private static void RefreshFromConfig()
    {
        int generation = Engine_Switches.Generation;
        lock (CacheGate)
        {
            if (_cachedGeneration == generation)
            {
                return;
            }
        }

        IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
        float lifetime = values.TryGetValue(LifetimeKey, out string? rawLife)
            ? ParseLifetimeSeconds(rawLife)
            : DefaultLifetimeSeconds;
        int maxCount = values.TryGetValue(MaxCountKey, out string? rawMax)
            ? ParseConfiguredMaxCount(rawMax)
            : 0;

        lock (CacheGate)
        {
            _cachedGeneration = generation;
            _cachedLifetime = lifetime;
            _cachedConfiguredMax = maxCount;
        }
    }

    /// <summary>
    /// Parses a corpse lifetime CVar string with no upper cap so arbitrary user durations (including
    /// multi-minute or multi-hour settings) are honored verbatim.
    /// </summary>
    public static float ParseLifetimeSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultLifetimeSeconds;
        }

        string trimmed = raw.Trim().Trim('"');
        if (float.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value) &&
            float.IsFinite(value))
        {
            return value <= 0f ? 0f : value;
        }

        return DefaultLifetimeSeconds;
    }

    /// <summary>Parses a <c>corpse_max_count</c> value (0 = Auto, negative = Unlimited).</summary>
    public static int ParseConfiguredMaxCount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        string trimmed = raw.Trim().Trim('"');
        return int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
    }

    /// <summary>Computes the region corpse cap for the configured lifetime and memory tier.</summary>
    public static int ResolveMaxCorpses(float lifetimeSeconds, int configuredMax, bool compactTier)
    {
        if (configuredMax < 0)
        {
            return int.MaxValue;
        }

        if (configuredMax > 0)
        {
            return configuredMax;
        }

        if (lifetimeSeconds <= 0f)
        {
            return 0;
        }

        if (compactTier)
        {
            return lifetimeSeconds <= 15f ? 8 : (lifetimeSeconds <= 30f ? 12 : 20);
        }

        return lifetimeSeconds <= 15f
            ? 16
            : (lifetimeSeconds <= 60f ? 32 : (lifetimeSeconds <= 180f ? 64 : 96));
    }

    /// <summary>
    /// Returns true when a living creature exhibits translucent or overbright supernatural rendering
    /// (ghosts, spirits, wisps, Banshees, shadow creatures), marking it ineligible for a solid corpse.
    /// </summary>
    public static bool IsEtherealAppearance(in HostBridge.EntityAppearance look)
    {
        float maxTint = MathF.Max(look.TintLanes.X, MathF.Max(look.TintLanes.Y, look.TintLanes.Z));
        return look.Alpha < 0.90f || maxTint > 1.5f;
    }

    /// <summary>Returns true if a record name or model asset path matches an exploding bug or ethereal entity.</summary>
    public static bool HasExcludedName(string? nameOrPath)
    {
        if (string.IsNullOrEmpty(nameOrPath))
        {
            return false;
        }

        foreach (string token in ExcludedTokens)
        {
            if (nameOrPath.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether a GDB record hash or its inherited parent hierarchy (or model path) belongs to
    /// an excluded non-corpse archetype such as Beetles, Banshees, Ghosts, or Spire Shards.
    /// </summary>
    public static bool IsExcludedRecord(uint recordHash, string? modelPath)
    {
        if (HasExcludedName(modelPath))
        {
            return true;
        }

        if (recordHash == 0u || recordHash == GdbHash.Null)
        {
            return false;
        }

        GdbMulti? multi = Engine_GameGdb.Database;
        lock (CacheGate)
        {
            if (!ReferenceEquals(_cachedGdb, multi))
            {
                _cachedGdb = multi;
                ExcludedRecordCache.Clear();
            }

            if (ExcludedRecordCache.TryGetValue(recordHash, out bool cached))
            {
                return cached;
            }
        }

        bool excluded = EvaluateRecordExcluded(multi, recordHash);
        lock (CacheGate)
        {
            if (ReferenceEquals(_cachedGdb, multi))
            {
                if (ExcludedRecordCache.Count > 4096)
                {
                    ExcludedRecordCache.Clear();
                }

                ExcludedRecordCache[recordHash] = excluded;
            }
        }

        return excluded;
    }

    /// <summary>Returns true if a GDB component field hash denotes an excluded shadow/exploding/ethereal creature type.</summary>
    public static bool IsExcludedComponentFieldHash(uint fieldHash)
    {
        foreach (uint excluded in ExcludedComponentFieldHashes)
        {
            if (excluded == fieldHash)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExcludedArchetypeHash(uint recordHash) =>
        recordHash is BeetlesBaseHash or BansheesBaseHash or BansheeChildHash or TrollsBaseHash or SpireShardsBaseHash;

    private static bool HasExcludedComponentSlot(in GdbCursor cursor)
    {
        foreach (uint fieldHash in ExcludedComponentFieldHashes)
        {
            if (cursor.View.TryFindLocalRaw(cursor.Record, fieldHash, (int)GdbFieldType.Reference, out _, out _))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool EvaluateRecordExcluded(GdbMulti? multi, uint recordHash)
    {
        if (IsExcludedArchetypeHash(recordHash))
        {
            return true;
        }

        if (multi is null)
        {
            return false;
        }

        uint current = recordHash;
        for (int depth = 0; depth < 16 && current != 0u && current != GdbHash.Null; ++depth)
        {
            if (IsExcludedArchetypeHash(current) || HasExcludedName(multi.NameOf(current)))
            {
                return true;
            }

            if (!multi.TryLookup(current, out GdbCursor cursor) || !cursor.Valid)
            {
                break;
            }

            if (HasExcludedComponentSlot(in cursor))
            {
                return true;
            }

            if (!cursor.View.TryFindLocalRaw(cursor.Record, GdbView.HashParent, (int)GdbFieldType.Reference, out uint parent, out _))
            {
                break;
            }

            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>Evaluates all eligibility gates for turning a retired guest entity into a persistent corpse.</summary>
    public static bool IsEligibleCorpse(in CorpseCandidate candidate) =>
        candidate.IsDeadCreature &&
        candidate.HasSkeleton &&
        candidate.SawVisibleWhileDead &&
        !candidate.SawEtherealWhileAlive &&
        !candidate.IsPrincipal &&
        !candidate.IsCompanion &&
        !candidate.RecordExcluded &&
        !HasExcludedName(candidate.ModelPath);

    /// <summary>
    /// Selects which active non-fading corpse to evict first when exceeding the cap, strongly
    /// preferring distant corpses (&gt;25 m from the camera) before touching bodies near the player.
    /// </summary>
    public static int SelectEvictionIndex(ReadOnlySpan<CorpseSlotSummary> slots, Vector3 cameraPos)
    {
        float distantRadiusSq = DistantEvictionRadiusMeters * DistantEvictionRadiusMeters;
        int bestDistantIdx = -1;
        float bestDistantAge = float.MinValue;
        int bestAnyIdx = -1;
        float bestAnyAge = float.MinValue;

        for (int i = 0; i < slots.Length; ++i)
        {
            if (slots[i].FadingOut)
            {
                continue;
            }

            float age = slots[i].Age;
            if (age > bestAnyAge)
            {
                bestAnyAge = age;
                bestAnyIdx = i;
            }

            if (slots[i].Position.DistanceSquaredTo(cameraPos) > distantRadiusSq && age > bestDistantAge)
            {
                bestDistantAge = age;
                bestDistantIdx = i;
            }
        }

        return bestDistantIdx >= 0 ? bestDistantIdx : bestAnyIdx;
    }
}
