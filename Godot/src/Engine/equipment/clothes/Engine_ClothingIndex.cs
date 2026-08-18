using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Equipment;

public sealed class ClothingEntry
{
    public uint RecordHash { get; init; }

    public ClothingSlot Slot { get; init; } = ClothingSlot.None;

    public string Name { get; init; } = string.Empty;

    public ItemDetail Detail { get; init; } = new();

    public uint MaleModelHash { get; set; }

    public uint FemaleModelHash { get; set; }

    public string MaleModelPath { get; set; } = string.Empty;

    public string FemaleModelPath { get; set; } = string.Empty;

    public bool MaleOnly { get; set; }

    public bool FemaleOnly { get; set; }

    public uint ModelHashFor(bool female) =>
        female && FemaleModelHash != 0 ? FemaleModelHash : MaleModelHash;

    public string ModelPathFor(bool female) =>
        female && FemaleModelPath.Length != 0 ? FemaleModelPath : MaleModelPath;

    public bool WornBy(bool female) => female ? !MaleOnly : !FemaleOnly;
}

public static class Engine_ClothingIndex
{
    public const string Folder = "equipment/clothes";

    public const string Extension = ".clothing";

    private static readonly object IndexLock = new();

    private static Func<string, byte[]?>? _resolver;

    private static GdbMulti? _multi;

    private static List<ClothingEntry>? _entries;

    private static Dictionary<string, ClothingEntry>? _byFileName;

    private static int _generation;

    public static int Generation
    {
        get
        {
            Build();
            return _generation;
        }
    }

    public static void SetResolver(Func<string, byte[]?>? resolver)
    {
        lock (IndexLock)
        {
            _resolver = resolver;
            Drop();
        }
    }

    public static void Invalidate()
    {
        lock (IndexLock)
        {
            Drop();
        }
    }

    private static void Drop()
    {
        _multi = null;
        _entries = null;
        _byFileName = null;
        _generation++;
    }

    public static IReadOnlyList<ClothingEntry> Entries
    {
        get
        {
            Build();
            return _entries ?? new List<ClothingEntry>();
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

    public static List<ClothingEntry> InSlot(ClothingSlot slot)
    {
        var found = new List<ClothingEntry>();
        foreach (ClothingEntry entry in Entries)
        {
            if (entry.Slot == slot)
            {
                found.Add(entry);
            }
        }

        return found;
    }

    public static List<ClothingEntry> InSlot(ClothingSlot slot, bool female)
    {
        var found = new List<ClothingEntry>();
        foreach (ClothingEntry entry in Entries)
        {
            if (entry.Slot == slot && entry.WornBy(female) && entry.ModelHashFor(female) != 0)
            {
                found.Add(entry);
            }
        }

        return found;
    }

    public static List<ClothingSlot> Slots()
    {
        var slots = new List<ClothingSlot>();
        foreach (ClothingSlot slot in Engine_ClothingSlots.Order)
        {
            if (InSlot(slot).Count != 0)
            {
                slots.Add(slot);
            }
        }

        return slots;
    }

    public static string FileNameOf(ClothingEntry entry) =>
        Engine_ItemNames.FileNameOf(entry.Name, entry.RecordHash, Extension);

    public static string VirtualPathOf(ClothingEntry entry) =>
        $"{Folder}/{Engine_ClothingSlots.FolderOf(entry.Slot)}/{FileNameOf(entry)}";

    public static bool TryGet(string virtualPath, out ClothingEntry entry)
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

    public static bool TryGetByHash(uint recordHash, out ClothingEntry entry)
    {
        foreach (ClothingEntry candidate in Entries)
        {
            if (candidate.RecordHash == recordHash)
            {
                entry = candidate;
                return true;
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

        var blobs = new List<byte[]?>();
        foreach (string path in Engine_LevelProps.SupplementalGdbPaths)
        {
            blobs.Add(_resolver(path) ?? _resolver("data/" + path));
        }

        GdbMulti multi = GdbMulti.Build(blobs);
        var entries = new List<ClothingEntry>();

        foreach (ItemDetail detail in Engine_ItemDetails.Build(multi))
        {
            if (detail.IsMoney || detail.Category == Engine_ClothingSlots.CategoryWeapon)
            {
                continue;
            }

            string maleText = Engine_ItemNames.SearchTextOf(detail, false);
            ClothingSlot slot = Engine_ClothingSlots.Classify(detail, maleText);
            if (slot == ClothingSlot.None)
            {
                continue;
            }

            var entry = new ClothingEntry
            {
                RecordHash = detail.RecordHash,
                Slot = slot,
                Name = Engine_ItemNames.DisplayNameOf(detail),
                Detail = detail,
                MaleOnly = Engine_ClothingSlots.IsOppositeSexAsset(maleText, true),
                FemaleOnly = Engine_ClothingSlots.IsOppositeSexAsset(
                    Engine_ItemNames.SearchTextOf(detail, true), false),
            };

            ResolveModels(entry, detail);
            entries.Add(entry);
        }

        entries.Sort(Compare);

        var map = new Dictionary<string, ClothingEntry>();
        foreach (ClothingEntry entry in entries)
        {
            map[FileNameOf(entry)] = entry;
        }

        _multi = multi;
        _byFileName = map;
        _entries = entries;

        GD.Print($"[clothes] indexed {entries.Count} garment(s) across " +
            $"{Slots().Count} slot(s) from {multi.Views.Count} database(s)");
    }

    private static void ResolveModels(ClothingEntry entry, ItemDetail detail)
    {
        entry.MaleModelHash = detail.WornModelPathHash != 0
            ? detail.WornModelPathHash
            : detail.ModelPathHash;
        entry.FemaleModelHash = detail.FemaleWornModelPathHash;

        entry.MaleModelPath = ResolvePath(entry.MaleModelHash, detail.WornModelPath);
        entry.FemaleModelPath = ResolvePath(
            entry.FemaleModelHash, detail.FemaleWornModelPath);

        if (entry.MaleModelPath.Length == 0 && entry.FemaleModelPath.Length != 0)
        {
            entry.FemaleOnly = true;
        }
    }

    private static string ResolvePath(uint hash, string authored)
    {
        if (hash == 0)
        {
            return string.Empty;
        }

        string? resolved = Engine_PropModelIndex.Resolve(hash, Array.Empty<string>());
        if (resolved is not null)
        {
            return resolved;
        }

        if (authored.Length == 0)
        {
            return string.Empty;
        }

        return Engine_PropModelIndex.ResolveByPath(authored) ?? string.Empty;
    }

    private static int Compare(ClothingEntry a, ClothingEntry b)
    {
        int slot = a.Slot.CompareTo(b.Slot);
        if (slot != 0)
        {
            return slot;
        }

        int name = Engine_ItemLabels.CompareLabels(a.Name, b.Name);
        return name != 0 ? name : a.RecordHash.CompareTo(b.RecordHash);
    }

}
