using System;
using System.Collections.Generic;
using System.Linq;
using Refii.Core;
using Refii.Gdb;

namespace Refii.Titles.Fable2;

using Refii.Save;

public sealed class Engine_NewGameInventory
{
    /// <summary>
    /// Read only by the New Game script, and about 40 MB and 330,000 objects to keep. In the
    /// low-memory tier only the weak reference holds it, and a script that finds it collected
    /// builds it again, about a second on a phone (not measured there).
    /// </summary>
    private readonly WeakReference<GdbMulti> _weakDatabase;
    private readonly GdbMulti? _keptDatabase;
    private readonly Func<GdbMulti> _rebuild;
    private readonly Dictionary<uint, string> _names = new();
    private readonly Dictionary<Engine_SaveHero.Augmentation, uint> _augmentItems = new();
    /// <summary>
    /// A container's aliases may name only its own entries (<see cref="GdbWriter"/>), so each
    /// named record goes in too, with the fields the game states for it.
    /// </summary>
    public GdbPatchDatabase Aliases { get; } = new();

    public Engine_NewGameInventory(GdbMulti database)
    {
    }

    internal Engine_NewGameInventory(GdbMulti database, Func<GdbMulti> rebuild, bool keepDatabase)
    {
        _weakDatabase = new WeakReference<GdbMulti>(database);
        _keptDatabase = keepDatabase ? database : null;
        _rebuild = rebuild;
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
            var entry = new GdbPatchEntry(record, cursor.View.RecordSchemaFlags(cursor.Record));
            foreach (var field in cursor.View.RawFields(cursor.Record))
            {
                entry.Properties.Add(new GdbPatchProperty(field.Hash, field.Type, field.Raw));
                // THE STRINGS COME WITH THE COPY. It shadows the original record, and its field names
                // and String/Enum values are hashes into its own container's name table: without them
                // every quest's NameTag resolved to nothing and the pause menu said "!!!<id>".
                CopyName(cursor.View, field.Hash);
                if (field.Type is GdbFieldType.String or GdbFieldType.Enum) CopyName(cursor.View, field.Raw);
            }
            Aliases.Entries.Add(entry);
            Aliases.Aliases.Add((alias, record));
            Aliases.Names.Add(alias, name);
            if (Reference(cursor, "InventoryItemComponent") == 0) continue;
            var augments = InitialAugments(record);
            if (augments.Count == 1) _augmentItems.TryAdd(augments[0], record);
        }
    }

    private void CopyName(GdbView view, uint hash)
    {
        if (view.Names.TryGetValue(hash, out string? text)) Aliases.Names.TryAdd(hash, text);
    }

    private GdbMulti Database()
    {
        if (!_weakDatabase.TryGetTarget(out GdbMulti? database))
        {
            database = _rebuild();
            _weakDatabase.SetTarget(database);
        }

        return database;
    }

    public string? RecordName(uint record) => _names.GetValueOrDefault(record);

    public string? AugmentationName(Engine_SaveHero.Augmentation augmentation) =>
        _augmentItems.TryGetValue(augmentation, out uint record) ? RecordName(record) : null;

    public IReadOnlyList<Engine_SaveHero.Augmentation> InitialAugments(uint record)
    {
        var result = new List<Engine_SaveHero.Augmentation>();
        GdbMulti database = Database();
        if (!database.TryLookup(record, out var item) ||
            !database.TryLookup(Reference(item, "AugmentableComponent"), out var augmentable) ||
            !database.TryLookup(Reference(augmentable, "InitialAugmentations"), out var list)) return result;
        foreach (var field in database.InheritedFields(list))
        {
            if (field.Type is not (GdbFieldType.Reference or GdbFieldType.ResourceReference) || !database.TryLookup(field.Raw, out var entry)) continue;
            uint primary = Reference(entry, "PrimaryType");
            if (primary != 0) result.Add(new(primary, Reference(entry, "SecondaryType")));
        }
        return result;
    }

    private uint Reference(GdbCursor cursor, string field) =>
        Database().TryFindInherited(cursor, Engine_GameHash.Fnv1(field), -1, out _, out uint value, out var type)
            && type is GdbFieldType.Reference or GdbFieldType.ResourceReference && value != GdbHash.Null ? value : 0;
}
