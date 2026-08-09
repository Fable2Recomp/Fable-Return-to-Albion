using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Gdb;

public sealed class EntityGameplay
{
    public string FactionName { get; set; } = string.Empty;

    public string CombatProfileName { get; set; } = string.Empty;

    public uint CreatureComponent { get; set; }

    public uint HealthComponent { get; set; }

    public uint CombatComponent { get; set; }

    public uint FactionComponent { get; set; }

    public List<KeyValuePair<string, string>> CoreFields { get; } = new();

    public List<KeyValuePair<string, string>> CombatFields { get; } = new();
}

public static class Engine_EntityGameplay
{
    public const uint CombatBalanceParams = 0x48D1A921;
    public const uint CombatBalanceTable = 0x1754A434;

    private static readonly (string Label, uint Hash)[] CoreFieldSpecs =
    {
        ("Starting health", 0x83632C03),
        ("Maximum health", 0x5B42D9DB),
        ("Health modifier", 0xFE697294),
        ("Maximum-health modifier", 0x78BBA6AC),
        ("Base level", 0x820BD770),
        ("Combat rating", 0x67687AC2),
        ("Danger level", 0x4B251BA6),
        ("Can be attacked", 0x77235EE5),
    };

    private static readonly (string Label, uint Hash)[] CombatFieldSpecs =
    {
        ("Creature damage", 0xF7BDA1F9),
        ("Base damage", 0x103BEC7A),
        ("Damage amount", 0x95DBBE26),
        ("Damage multiplier", 0x53E9D10B),
        ("Damage taken multiplier", 0x17122D36),
        ("Melee damage multiplier", 0x11C89DB1),
        ("Ranged damage multiplier", 0x541BA4E6),
        ("Attack speed multiplier", 0xCC6F1577),
        ("Melee attack speed multiplier", 0x9355A989),
        ("Knockdown damage threshold", 0x1637A977),
        ("Damage reaction threshold", 0x3D24BC1D),
        ("Combat style", 0xB1F6B9C8),
        ("Combat behaviour group", 0x3753CB77),
    };

    public static EntityGameplay? Extract(GdbMulti multi, uint entityHash)
    {
        if (!multi.TryLookup(entityHash, out GdbCursor entity))
        {
            return null;
        }

        var details = new EntityGameplay();

        bool hasCreature = TryComponent(
            multi, entity, Engine_EntityCatalog.CreatureComponent,
            out uint creatureHash, out GdbCursor creature);
        bool hasHealth = TryComponent(
            multi, entity, Engine_EntityCatalog.HealthComponent,
            out uint healthHash, out GdbCursor health);
        bool hasCombat = TryComponent(
            multi, entity, Engine_EntityCatalog.CombatComponent,
            out uint combatHash, out GdbCursor combat);
        bool hasFaction = TryComponent(
            multi, entity, Engine_EntityCatalog.FactionComponent,
            out uint factionHash, out GdbCursor faction);

        if (!hasCreature && !hasHealth && !hasCombat && !hasFaction)
        {
            return null;
        }

        details.CreatureComponent = creatureHash;
        details.HealthComponent = healthHash;
        details.CombatComponent = combatHash;
        details.FactionComponent = factionHash;

        var coreSources = new List<GdbCursor>();
        if (hasHealth)
        {
            coreSources.Add(health);
        }

        if (hasCreature)
        {
            coreSources.Add(creature);
        }

        if (hasCombat)
        {
            coreSources.Add(combat);
        }

        coreSources.Add(entity);

        var profileSources = new List<GdbCursor>();
        if (hasCombat)
        {
            profileSources.Add(combat);
        }

        if (hasCreature)
        {
            profileSources.Add(creature);
        }

        profileSources.Add(entity);

        GdbCursor profile = default;
        if (TryReadReference(
                multi, profileSources, CombatBalanceParams,
                out uint profileHash, out string profileName))
        {
            details.CombatProfileName = profileName;
            multi.TryLookup(profileHash, out profile);
        }

        if (details.CombatProfileName.Length == 0 && hasCombat &&
            TryReadValue(multi, new[] { combat }, CombatBalanceTable, out string table))
        {
            details.CombatProfileName = table;
        }

        var factionSources = new List<GdbCursor>();
        if (hasFaction)
        {
            factionSources.Add(faction);
        }

        if (hasCreature)
        {
            factionSources.Add(creature);
        }

        if (hasCombat)
        {
            factionSources.Add(combat);
        }

        factionSources.Add(entity);

        if (TryReadReference(
                multi, factionSources, Engine_EntityCatalog.Faction,
                out _, out string factionName))
        {
            details.FactionName = factionName;
        }

        foreach ((string label, uint hash) in CoreFieldSpecs)
        {
            if (TryReadValue(multi, coreSources, hash, out string value))
            {
                details.CoreFields.Add(new KeyValuePair<string, string>(label, value));
            }
        }

        var combatSources = new List<GdbCursor>();
        if (profile.Valid)
        {
            combatSources.Add(profile);
        }

        if (hasCombat)
        {
            combatSources.Add(combat);
        }

        if (hasCreature)
        {
            combatSources.Add(creature);
        }

        combatSources.Add(entity);

        foreach ((string label, uint hash) in CombatFieldSpecs)
        {
            if (TryReadValue(multi, combatSources, hash, out string value))
            {
                details.CombatFields.Add(new KeyValuePair<string, string>(label, value));
            }
        }

        return details;
    }

    private static bool TryComponent(
        GdbMulti multi, GdbCursor entity, uint fieldHash,
        out uint componentHash, out GdbCursor component)
    {
        component = default;
        componentHash = 0;

        return multi.TryFindInherited(entity, fieldHash, 6, out _, out componentHash, out _) &&
               componentHash != 0 && componentHash != GdbHash.Null &&
               multi.TryLookup(componentHash, out component);
    }

    private static bool TryReadReference(
        GdbMulti multi, IReadOnlyList<GdbCursor> sources, uint fieldHash,
        out uint recordHash, out string name)
    {
        recordHash = 0;
        name = string.Empty;

        foreach (GdbCursor source in sources)
        {
            if (!multi.TryFindInherited(
                    source, fieldHash, -1, out _, out uint raw, out GdbFieldType type) ||
                (type != GdbFieldType.String && type != GdbFieldType.Reference &&
                 type != GdbFieldType.ResourceReference) ||
                raw == 0 || raw == GdbHash.Null)
            {
                continue;
            }

            recordHash = raw;
            name = multi.NameOf(raw);
            return true;
        }

        return false;
    }

    private static bool TryReadValue(
        GdbMulti multi, IReadOnlyList<GdbCursor> sources, uint fieldHash, out string value)
    {
        value = string.Empty;

        foreach (GdbCursor source in sources)
        {
            if (!multi.TryFindInherited(
                    source, fieldHash, -1, out _, out uint raw, out GdbFieldType type))
            {
                continue;
            }

            switch (type)
            {
                case GdbFieldType.Bool:
                    value = raw != 0 ? "Yes" : "No";
                    return true;

                case GdbFieldType.Int:
                case GdbFieldType.Enum:
                    value = unchecked((int)raw).ToString(CultureInfo.InvariantCulture);
                    return true;

                case GdbFieldType.Float:
                {
                    float number = BitConverter.Int32BitsToSingle(unchecked((int)raw));
                    if (!float.IsFinite(number))
                    {
                        continue;
                    }

                    value = number.ToString("0.###", CultureInfo.InvariantCulture);
                    return true;
                }

                case GdbFieldType.String:
                case GdbFieldType.Reference:
                case GdbFieldType.ResourceReference:
                    value = multi.NameOf(raw);
                    if (value.Length == 0)
                    {
                        value = $"0x{raw:X8}";
                    }

                    return true;
            }
        }

        return false;
    }
}
