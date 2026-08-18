using System.Collections.Generic;
using Godot;

namespace Refii.Equipment;

public static class Engine_EquipmentScene
{
    public const string WeaponKind = "Weapon";

    public const string ClothingKind = "Garment";

    public static PackedScene? ForWeapon(WeaponEntry entry)
    {
        ItemDetail detail = entry.Detail;

        var rows = new List<KeyValuePair<string, string>>
        {
            new("Family", entry.Family),
            new("Weapon Type", Engine_WeaponKinds.DescribeType(detail.WeaponType)),
            new("Internal Name", detail.InternalName),
            new("Name Tag", detail.NameTagName),
            new("Description", Engine_ItemNames.DescriptionOf(detail)),
            new("Icon", detail.IconTex),
        };

        if (detail.Money >= 0)
        {
            rows.Add(new KeyValuePair<string, string>("Base Value", detail.Money.ToString()));
        }

        rows.AddRange(detail.Stats);

        return Build(
            entry.Name, WeaponKind, entry.Family, detail.RecordHash, entry.ModelPath, rows);
    }

    public static PackedScene? ForClothing(ClothingEntry entry, bool female)
    {
        ItemDetail detail = entry.Detail;

        var rows = new List<KeyValuePair<string, string>>
        {
            new("Worn By", entry.MaleOnly ? "Male only"
                : entry.FemaleOnly ? "Female only"
                : "Male and female"),
            new("Internal Name", detail.InternalName),
            new("Name Tag", detail.NameTagName),
            new("Description", Engine_ItemNames.DescriptionOf(detail)),
            new("Icon", detail.IconTex),
        };

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

        if (detail.Money >= 0)
        {
            rows.Add(new KeyValuePair<string, string>("Base Value", detail.Money.ToString()));
        }

        rows.AddRange(detail.Stats);

        return Build(
            entry.Name,
            ClothingKind,
            Engine_ClothingSlots.LabelOf(entry.Slot),
            detail.RecordHash,
            entry.ModelPathFor(female),
            rows);
    }

    private static PackedScene? Build(
        string name,
        string kind,
        string slot,
        uint recordHash,
        string modelPath,
        IEnumerable<KeyValuePair<string, string>> rows)
    {
        string safeName = name.Length != 0 ? name : kind;

        var node = new Engine_EquipmentInstance
        {
            Name = SanitiseNodeName(safeName),
        };

        node.SetMeta(Engine_EquipmentInstance.NameMeta, safeName);
        node.SetMeta(Engine_EquipmentInstance.KindMeta, kind);
        node.SetMeta(Engine_EquipmentInstance.SlotMeta, slot);
        node.SetMeta(Engine_EquipmentInstance.HashMeta, $"0x{recordHash:X8}");
        node.SetMeta(Engine_EquipmentInstance.ModelMeta, modelPath);
        node.SetMeta(
            Engine_EquipmentInstance.DetailMeta, Engine_EquipmentInstance.DetailRows(rows));

        node.EnsureMesh();

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
}
