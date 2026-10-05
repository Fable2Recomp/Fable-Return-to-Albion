using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Equipment;

public sealed class WeaponEntry
{
    public uint RecordHash { get; init; }

    public string Family { get; init; } = Engine_WeaponKinds.Fallback;

    public string Name { get; init; } = string.Empty;

    public int WeaponType { get; init; }

    public ItemDetail Detail { get; init; } = new();

    public uint ModelHash { get; set; }

    public string ModelPath { get; set; } = string.Empty;

    public bool HasModel => ModelPath.Length != 0;
}

public static class Engine_WeaponIndex
{
    public const string Folder = "equipment/weapons";

    public const string Extension = ".weapon";

    private static readonly object IndexLock = new();

    private static Func<string, byte[]?>? _resolver;

    private static GdbMulti? _multi;

    private static List<WeaponEntry>? _entries;

    private static List<string>? _families;

    private static Dictionary<string, WeaponEntry>? _byFileName;

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
        _families = null;
        _byFileName = null;
    }

    public static IReadOnlyList<WeaponEntry> Entries
    {
        get
        {
            Build();
            return _entries ?? new List<WeaponEntry>();
        }
    }

    public static IReadOnlyList<string> Families
    {
        get
        {
            Build();
            return _families ?? new List<string>();
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

    public static List<WeaponEntry> InFamily(string family)
    {
        var found = new List<WeaponEntry>();
        foreach (WeaponEntry entry in Entries)
        {
            if (entry.Family.Equals(family, StringComparison.Ordinal))
            {
                found.Add(entry);
            }
        }

        return found;
    }

    public static string FileNameOf(WeaponEntry entry) =>
        Engine_ItemNames.FileNameOf(entry.Name, entry.RecordHash, Extension);

    public static string VirtualPathOf(WeaponEntry entry) =>
        $"{Folder}/{entry.Family}/{FileNameOf(entry)}";

    public static bool TryGet(string virtualPath, out WeaponEntry entry)
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
    /// Resolves a weapon entry by either its definition record hash or its inventory component hash.
    /// </summary>
    public static bool TryGetByHash(uint recordHash, out WeaponEntry entry)
    {
        Build();
        if (_entries is not null)
        {
            foreach (WeaponEntry candidate in _entries)
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

        var entries = new List<WeaponEntry>();
        var families = new HashSet<string>();

        foreach (ItemDetail detail in Engine_ItemDetails.Build(multi))
        {
            if (detail.WeaponComponent == 0)
            {
                continue;
            }

            string family = Engine_WeaponKinds.FamilyOf(detail);
            families.Add(family);

            var entry = new WeaponEntry
            {
                RecordHash = detail.RecordHash,
                Family = family,
                Name = NameOf(detail),
                WeaponType = detail.WeaponType,
                Detail = detail,
            };

            ResolveModel(entry, detail);
            entries.Add(entry);
        }

        entries.Sort(Compare);

        var map = new Dictionary<string, WeaponEntry>();
        foreach (WeaponEntry entry in entries)
        {
            map[FileNameOf(entry)] = entry;

            // DUAL-HASH ALIAS LOOKUP:
            // Allow resolving this weapon by its inventory component filename as well.
            if (entry.Detail.InventoryComponentHash != 0)
            {
                string compFile = Engine_ItemNames.FileNameOf(
                    entry.Name, entry.Detail.InventoryComponentHash, Extension);
                map.TryAdd(compFile, entry);
            }
        }

        _multi = multi;
        _families = Engine_WeaponKinds.SortFolders(families);
        _byFileName = map;
        _entries = entries;

        GD.Print($"[weapons] indexed {entries.Count} weapon(s) in {_families.Count} " +
            $"famil(ies) from {multi.Views.Count} database(s), {multi.RecordCount:N0} records");
    }

    private static void ResolveModel(WeaponEntry entry, ItemDetail detail)
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
    }

    public static string NameOf(ItemDetail detail) => Engine_ItemNames.DisplayNameOf(detail);

    public static string DescriptionOf(ItemDetail detail) =>
        Engine_ItemNames.DescriptionOf(detail);

    private static int Compare(WeaponEntry a, WeaponEntry b)
    {
        int family = Engine_WeaponKinds.CompareFolders(a.Family, b.Family);
        if (family != 0)
        {
            return family;
        }

        int name = Engine_ItemLabels.CompareLabels(a.Name, b.Name);
        return name != 0 ? name : a.RecordHash.CompareTo(b.RecordHash);
    }

}
