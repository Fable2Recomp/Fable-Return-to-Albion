using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Refii.Equipment;

public static class Engine_EquipmentScene
{
    public const string WeaponKind = "Weapon";

    public const string ClothingKind = "Garment";

    public const string ItemKind = "Item";

    /// <summary>
    /// Builds the list of inspector key-value pairs for an ItemDetail, reflecting current GDB and text values.
    /// </summary>
    public static List<KeyValuePair<string, string>> BuildRowsForDetail(ItemDetail detail)
    {
        var rows = new List<KeyValuePair<string, string>>
        {
            new("Category ID", detail.Category.ToString()),
            new("Internal Name", detail.InternalName),
            new("Name Tag", detail.NameTagName),
            new("Description Tag", detail.DescTagName),
            new("Description", Engine_ItemNames.DescriptionOf(detail)),
            new("Icon", detail.IconTex),
        };

        if (detail.WeaponType != 0)
        {
            rows.Add(new KeyValuePair<string, string>(
                "Weapon Type", Engine_WeaponKinds.DescribeType(detail.WeaponType)));
        }

        if (detail.Rating > 0)
        {
            rows.Add(new KeyValuePair<string, string>(
                "Tier / Rating", DescribeRating(detail.Rating)));
        }

        if (detail.BasePrice >= 0)
        {
            rows.Add(new KeyValuePair<string, string>(
                "Base Price", $"{detail.BasePrice:N0} Gold"));
        }
        else if (detail.Money >= 0)
        {
            rows.Add(new KeyValuePair<string, string>("Base Value", $"{detail.Money:N0} Gold"));
        }

        if (detail.AugmentSlots > 0)
        {
            rows.Add(new KeyValuePair<string, string>(
                "Augment Slots", detail.AugmentSlots.ToString()));
        }

        if (detail.WornModelPath.Length != 0 || detail.WornModelPathHash != 0)
        {
            rows.Add(new KeyValuePair<string, string>(
                "Worn Model",
                detail.WornModelPath.Length != 0
                    ? detail.WornModelPath
                    : $"0x{detail.WornModelPathHash:X8}"));
        }

        if (detail.FemaleWornModelPath.Length != 0 || detail.FemaleWornModelPathHash != 0)
        {
            rows.Add(new KeyValuePair<string, string>(
                "Female Worn Model",
                detail.FemaleWornModelPath.Length != 0
                    ? detail.FemaleWornModelPath
                    : $"0x{detail.FemaleWornModelPathHash:X8}"));
        }

        if (detail.BodyAreasCovered != 0)
        {
            rows.Add(new KeyValuePair<string, string>(
                "Body Areas Covered", $"0x{detail.BodyAreasCovered:X}"));
        }

        if (detail.ClusterSortLayer != -100)
        {
            rows.Add(new KeyValuePair<string, string>(
                "Cluster Sort Layer", detail.ClusterSortLayer.ToString()));
        }

        rows.AddRange(detail.Stats);
        return rows;
    }

    public static PackedScene? ForItem(ItemEntry entry)
    {
        var rows = BuildRowsForDetail(entry.Detail);
        return Build(
            entry.Name,
            ItemKind,
            Engine_ItemCategories.LabelOf(entry.Detail.Category),
            entry.Detail.RecordHash,
            entry.ModelPath,
            rows,
            "Category");
    }

    public static PackedScene? ForWeapon(WeaponEntry entry)
    {
        var rows = BuildRowsForDetail(entry.Detail);
        rows.Insert(0, new KeyValuePair<string, string>("Family", entry.Family));
        return Build(
            entry.Name, WeaponKind, entry.Family, entry.Detail.RecordHash, entry.ModelPath, rows);
    }

    public static PackedScene? ForClothing(ClothingEntry entry, bool female)
    {
        var rows = BuildRowsForDetail(entry.Detail);
        rows.Insert(0, new KeyValuePair<string, string>(
            "Worn By", entry.MaleOnly ? "Male only"
                : entry.FemaleOnly ? "Female only"
                : "Male and female"));

        return Build(
            entry.Name,
            ClothingKind,
            Engine_ClothingSlots.LabelOf(entry.Slot),
            entry.Detail.RecordHash,
            entry.ModelPathFor(female),
            rows);
    }

    private static PackedScene? Build(
        string name,
        string kind,
        string slot,
        uint recordHash,
        string modelPath,
        IEnumerable<KeyValuePair<string, string>> rows,
        string slotLabel = Engine_EquipmentInstance.DefaultSlotLabel)
    {
        string safeName = name.Length != 0 ? name : kind;

        var node = new Engine_EquipmentInstance
        {
            Name = SanitiseNodeName(safeName),
        };

        node.SetMeta(Engine_EquipmentInstance.NameMeta, safeName);
        node.SetMeta(Engine_EquipmentInstance.KindMeta, kind);
        node.SetMeta(Engine_EquipmentInstance.SlotMeta, slot);
        node.SetMeta(Engine_EquipmentInstance.SlotLabelMeta, slotLabel);
        node.SetMeta(Engine_EquipmentInstance.HashMeta, $"0x{recordHash:X8}");
        node.SetMeta(Engine_EquipmentInstance.ModelMeta, modelPath);
        node.SetMeta(
            Engine_EquipmentInstance.DetailMeta, Engine_EquipmentInstance.DetailRows(rows));

        node.EnsureMesh();
        AttachCloth(node, modelPath);

        var scene = new PackedScene();
        Error packed = scene.Pack(node);
        node.Free();

        if (packed != Error.Ok)
        {
            GD.PushWarning($"[equipment] could not pack {safeName}: {packed}");
            return null;
        }

        scene.ResourceName = safeName;
        return scene;
    }

    private static void AttachCloth(Engine_EquipmentInstance node, string modelPath)
    {
        if (modelPath.Length != 0)
        {
            Refii.Physics.Engine_ClothBody.Attach(node, node.Mesh, new[] { modelPath });
        }
    }

    private static string SanitiseNodeName(string name)
    {
        var text = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            text.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or ' ' ? c : '_');
        }

        string safe = text.ToString().Trim();
        return safe.Length == 0 ? "Equipment" : safe;
    }

    /// <summary>
    /// Translates an inventory Rating integer (1..5) into a human-friendly star and tier label.
    /// </summary>
    public static string DescribeRating(int rating) => rating switch
    {
        1 => "1 Star (Tier 1)",
        2 => "2 Stars (Tier 2)",
        3 => "3 Stars (Tier 3)",
        4 => "4 Stars (Tier 4)",
        5 => "5 Stars (Tier 5)",
        _ => $"{rating} Stars (Tier {rating})",
    };
}
