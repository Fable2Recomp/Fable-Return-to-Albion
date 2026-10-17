using System;
using System.Collections.Generic;
using System.Linq;
using Refii.Core;
using Refii.Gdb;

namespace Refii.Save;

public sealed class Engine_NewGameInventory
{
    private readonly GdbMulti _database;
    private readonly Dictionary<uint, string> _names = new();
    private readonly Dictionary<Engine_SaveHero.Augmentation, uint> _augmentItems = new();
    public GdbPatchDatabase Aliases { get; } = new();

    public Engine_NewGameInventory(GdbMulti database)
    {
        _database = database;
        var used = database.Views.SelectMany(v => v.AliasPairs()).Select(p => p.Alias).ToHashSet();
        foreach (uint record in database.Views.SelectMany(v => v.AliasPairs()).Select(p => p.Target).Distinct().Order())
        {
            if (!database.TryLookup(record, out var cursor)) continue;
            string name = "RefiiCarry_" + record.ToString("X8");
            uint alias = Engine_GameHash.Fnv1(name);
            while (database.TryLookup(alias, out _) || !used.Add(alias))
            {
                name += "_";
                alias = Engine_GameHash.Fnv1(name);
            }
            _names.Add(record, name);
            Aliases.Aliases.Add((alias, record));
            Aliases.Names.Add(alias, name);
            if (Reference(cursor, "InventoryItemComponent") == 0) continue;
            var augments = InitialAugments(record);
            if (augments.Count == 1) _augmentItems.TryAdd(augments[0], record);
        }
    }

    public string? RecordName(uint record) => _names.GetValueOrDefault(record);

    public string? AugmentationName(Engine_SaveHero.Augmentation augmentation) =>
        _augmentItems.TryGetValue(augmentation, out uint record) ? RecordName(record) : null;

    public IReadOnlyList<Engine_SaveHero.Augmentation> InitialAugments(uint record)
    {
        var result = new List<Engine_SaveHero.Augmentation>();
        if (!_database.TryLookup(record, out var item) ||
            !_database.TryLookup(Reference(item, "AugmentableComponent"), out var augmentable) ||
            !_database.TryLookup(Reference(augmentable, "InitialAugmentations"), out var list)) return result;
        foreach (var field in _database.InheritedFields(list))
        {
            if (field.Type is not (GdbFieldType.Reference or GdbFieldType.ResourceReference) || !_database.TryLookup(field.Raw, out var entry)) continue;
            uint primary = Reference(entry, "PrimaryType");
            if (primary != 0) result.Add(new(primary, Reference(entry, "SecondaryType")));
        }
        return result;
    }

    private uint Reference(GdbCursor cursor, string field) =>
        _database.TryFindInherited(cursor, Engine_GameHash.Fnv1(field), -1, out _, out uint value, out var type)
            && type is GdbFieldType.Reference or GdbFieldType.ResourceReference && value != GdbHash.Null ? value : 0;
}
