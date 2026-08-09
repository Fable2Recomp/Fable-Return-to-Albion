using System;
using System.Collections.Generic;

namespace Refii.Gdb;

public sealed class EntityEntry
{
    public string Name { get; set; } = string.Empty;

    public uint EntityHash { get; set; }

    public uint NameTagHash { get; set; }

    public uint CreatureComponent { get; set; }

    public List<uint> ModelHashes { get; } = new();
}

public static class Engine_EntityCatalog
{
    public const uint CreatureComponent = 0xC3B90D4F;
    public const uint HealthComponent = 0x26546FBC;
    public const uint CombatComponent = 0xF6D5AD36;
    public const uint FactionComponent = 0x8CE69EBC;
    public const uint Faction = 0x6F3C0103;
    public const uint NameTag = 0x9555A6FC;
    public const uint SoundEventPrefix = 0xABEAEBD2;

    public static bool TryDescribe(GdbMulti multi, uint entityHash, out EntityEntry entry)
    {
        entry = null!;

        if (!multi.TryLookup(entityHash, out GdbCursor cursor) ||
            !multi.TryFindInherited(
                cursor, CreatureComponent, 6, out _, out uint component, out _) ||
            component == 0 || component == GdbHash.Null)
        {
            return false;
        }

        List<uint> models = Engine_EntityModels.ForEntity(multi, entityHash);
        if (models.Count == 0)
        {
            return false;
        }

        entry = new EntityEntry
        {
            EntityHash = entityHash,
            CreatureComponent = component,
            Name = ResolveName(multi, entityHash, component),
        };

        entry.ModelHashes.AddRange(models);
        return true;
    }

    public static List<EntityEntry> Build(GdbMulti multi)
    {
        var entries = new List<EntityEntry>();
        var seen = new HashSet<uint>();

        foreach (GdbView view in multi.Views)
        {
            for (int i = 0; i < view.RecordCount; i++)
            {
                uint entityHash = view.RecordHash(i);
                if (entityHash == 0 || entityHash == GdbHash.Null ||
                    seen.Contains(entityHash))
                {
                    continue;
                }

                var cursor = new GdbCursor(view, i);
                if (!multi.TryFindInherited(
                        cursor, CreatureComponent, 6, out _, out uint component, out _) ||
                    component == 0 || component == GdbHash.Null)
                {
                    continue;
                }

                List<uint> models = Engine_EntityModels.ForEntity(multi, entityHash);
                if (models.Count == 0)
                {
                    continue;
                }

                seen.Add(entityHash);

                var entry = new EntityEntry
                {
                    EntityHash = entityHash,
                    CreatureComponent = component,
                    Name = ResolveName(multi, entityHash, component),
                };

                entry.ModelHashes.AddRange(models);

                if (multi.TryLookup(component, out GdbCursor creature) &&
                    multi.TryFindInherited(
                        creature, NameTag, -1, out _, out uint tag, out GdbFieldType tagType) &&
                    tagType is GdbFieldType.String or GdbFieldType.ResourceReference &&
                    tag != 0 && tag != GdbHash.Null)
                {
                    entry.NameTagHash = tag;
                }

                entries.Add(entry);
            }
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return entries;
    }

    private static string ResolveName(GdbMulti multi, uint entityHash, uint component)
    {
        string name = multi.NameOf(entityHash);
        if (name.Length != 0)
        {
            return name;
        }

        if (multi.TryLookup(component, out GdbCursor creature))
        {
            foreach (uint field in new[] { SoundEventPrefix, NameTag })
            {
                if (!multi.TryFindInherited(
                        creature, field, -1, out _, out uint value, out GdbFieldType type) ||
                    (type != GdbFieldType.String && type != GdbFieldType.ResourceReference))
                {
                    continue;
                }

                string text = multi.NameOf(value);
                if (text.Length != 0)
                {
                    return Readable(text);
                }
            }
        }

        return $"Creature 0x{entityHash:X8}";
    }

    public static string Readable(string value)
    {
        foreach (string prefix in new[] { "SO_", "CREATURE_", "TEXT_CHARACTER_NAME_" })
        {
            if (value.Length > prefix.Length &&
                value.StartsWith(prefix, StringComparison.Ordinal))
            {
                value = value[prefix.Length..];
                break;
            }
        }

        var text = new System.Text.StringBuilder(value.Length);
        bool upperNext = true;

        foreach (char c in value)
        {
            char ch = c == '_' ? ' ' : c;
            if (ch == ' ')
            {
                upperNext = true;
                text.Append(ch);
                continue;
            }

            text.Append(upperNext ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
            upperNext = false;
        }

        return text.ToString();
    }
}
