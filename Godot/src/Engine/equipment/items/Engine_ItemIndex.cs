using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Equipment;

public sealed class ItemEntry
{
    public uint RecordHash { get; init; }

    public int Category { get; init; } = -1;

    public string Name { get; init; } = string.Empty;

    public ItemDetail Detail { get; init; } = new();

    public uint ModelHash { get; set; }

    public string ModelPath { get; set; } = string.Empty;

    public bool HasModel => ModelPath.Length != 0;

    public string Folder => Engine_ItemCategories.FolderOf(Category);
}

public static class Engine_ItemIndex
{
    public const string Folder = "items";

    public const string Extension = ".item";

    private static readonly object IndexLock = new();

    private static Func<string, byte[]?>? _resolver;

    private static GdbMulti? _multi;

    private static List<ItemEntry>? _entries;

    private static List<int>? _categories;

    private static Dictionary<string, ItemEntry>? _byFileName;

    public static void SetResolver(Func<string, byte[]?>? resolver)
    {
        lock (IndexLock)
        {
            _resolver = resolver;
            Clear();
        }
    }

    public static void Invalidate()
    {
        lock (IndexLock)
        {
            Clear();
        }
    }

    private static void Clear()
    {
        _multi = null;
        _entries = null;
        _categories = null;
        _byFileName = null;
    }

    public static IReadOnlyList<ItemEntry> Entries
    {
        get
        {
            Build();
            return _entries ?? new List<ItemEntry>();
        }
    }

    public static IReadOnlyList<int> Categories
    {
        get
        {
            Build();
            return _categories ?? new List<int>();
        }
    }

    public static GdbMulti? Database
    {
        get
        {
            Build();
            return _multi;
        }
    }

    public static List<ItemEntry> InCategory(int category)
    {
        var found = new List<ItemEntry>();
        foreach (ItemEntry entry in Entries)
        {
            if (entry.Category == category)
            {
                found.Add(entry);
            }
        }

        return found;
    }

    public static string FileNameOf(ItemEntry entry) =>
        Engine_ItemNames.FileNameOf(entry.Name, entry.RecordHash, Extension);

    public static string VirtualPathOf(ItemEntry entry) =>
        $"{Folder}/{entry.Folder}/{FileNameOf(entry)}";

    public static bool TryGet(string virtualPath, out ItemEntry entry)
    {
        entry = null!;
        Build();

        if (_byFileName is null)
        {
            return false;
        }

        string path = virtualPath.Replace('\\', '/');
        int slash = path.LastIndexOf('/');
        string leaf = slash < 0 ? path : path[(slash + 1)..];

        return _byFileName.TryGetValue(leaf, out entry!);
    }

    /// <summary>
    /// Resolves an item entry by either its definition record hash or its inventory component hash.
    /// </summary>
    public static bool TryGetByHash(uint recordHash, out ItemEntry entry)
    {
        Build();
        if (_entries is not null)
        {
            foreach (ItemEntry candidate in _entries)
            {
                if (candidate.RecordHash == recordHash || candidate.Detail.InventoryComponentHash == recordHash)
                {
                    entry = candidate;
                    return true;
                }
            }
        }

        entry = null!;
        return false;
    }

    private static void Build()
    {
        lock (IndexLock)
        {
            BuildLocked();
        }
    }

    private static void BuildLocked()
    {
        if (_entries is not null || _resolver is null)
        {
            return;
        }

        var blobs = Refii.Gdb.Engine_GameGdb.LoadSupplementalBlobs(_resolver, includeTu: true);
        GdbMulti multi = GdbMulti.Build(blobs);

        var entries = new List<ItemEntry>();
        var categories = new HashSet<int>();

        foreach (ItemDetail detail in Engine_ItemDetails.Build(multi))
        {
            if (detail.IsMoney || !detail.HasInventoryItem)
            {
                continue;
            }

            categories.Add(detail.Category);

            var entry = new ItemEntry
            {
                RecordHash = detail.RecordHash,
                Category = detail.Category,
                Name = Engine_ItemNames.DisplayNameOf(detail),
                Detail = detail,
            };

            ResolveModel(entry, detail);
            entries.Add(entry);
        }

        entries.Sort(Compare);

        var map = new Dictionary<string, ItemEntry>();
        foreach (ItemEntry entry in entries)
        {
            map[FileNameOf(entry)] = entry;

            // DUAL-HASH ALIAS LOOKUP:
            // Allow resolving this item by its inventory component filename as well,
            // ensuring any legacy references or scenes saved under the component hash continue to load.
            if (entry.Detail.InventoryComponentHash != 0)
            {
                string compFile = Engine_ItemNames.FileNameOf(
                    entry.Name, entry.Detail.InventoryComponentHash, Extension);
                map.TryAdd(compFile, entry);
            }
        }

        int withModel = 0;
        foreach (ItemEntry entry in entries)
        {
            if (entry.HasModel)
            {
                withModel++;
            }
        }

        _multi = multi;
        _categories = Engine_ItemCategories.SortCategories(categories);
        _byFileName = map;
        _entries = entries;

        GD.Print($"[items] indexed {entries.Count} item(s) in {_categories.Count} " +
            $"categor(ies) from {multi.Views.Count} database(s), {multi.RecordCount:N0} " +
            $"records — {withModel} with a mesh");
    }

    private static void ResolveModel(ItemEntry entry, ItemDetail detail)
    {
        entry.ModelHash = detail.ModelPathHash != 0
            ? detail.ModelPathHash
            : detail.WornModelPathHash;

        if (entry.ModelHash != 0)
        {
            string? resolved = Engine_PropModelIndex.Resolve(
                entry.ModelHash, Array.Empty<string>());

            if (resolved is not null)
            {
                entry.ModelPath = resolved;
                return;
            }
        }

        foreach (string authored in new[] { detail.ModelPath, detail.WornModelPath })
        {
            if (authored.Length == 0)
            {
                continue;
            }

            string? resolved = Engine_PropModelIndex.ResolveByPath(authored);
            if (resolved is not null)
            {
                entry.ModelPath = resolved;
                entry.ModelHash = Engine_PropModelIndex.PathHash(resolved);
                return;
            }
        }

        if (detail.FemaleWornModelPathHash == 0)
        {
            return;
        }

        string? female = Engine_PropModelIndex.Resolve(
            detail.FemaleWornModelPathHash, Array.Empty<string>());

        if (female is not null)
        {
            entry.ModelHash = detail.FemaleWornModelPathHash;
            entry.ModelPath = female;
        }
    }

    public static string NameOf(ItemDetail detail) => Engine_ItemNames.DisplayNameOf(detail);

    public static string DescriptionOf(ItemDetail detail) =>
        Engine_ItemNames.DescriptionOf(detail);

    private static int Compare(ItemEntry a, ItemEntry b)
    {
        int category = Engine_ItemCategories.CompareCategories(a.Category, b.Category);
        if (category != 0)
        {
            return category;
        }

        int name = Engine_ItemLabels.CompareLabels(a.Name, b.Name);
        return name != 0 ? name : a.RecordHash.CompareTo(b.RecordHash);
    }
}
