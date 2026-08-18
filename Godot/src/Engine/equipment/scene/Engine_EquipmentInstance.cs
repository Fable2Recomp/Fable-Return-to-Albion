using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace Refii.Equipment;

[Tool]
[GlobalClass]
public partial class Engine_EquipmentInstance : MeshInstance3D
{
    public const string NameMeta = "equipment_name";
    public const string KindMeta = "equipment_kind";
    public const string SlotMeta = "equipment_slot";
    public const string HashMeta = "equipment_hash";
    public const string ModelMeta = "equipment_model";
    public const string DetailMeta = "equipment_detail";

    private bool _meshTried;

    public override void _Ready() => EnsureMesh();

    public string ModelPath => Text(ModelMeta);

    public void EnsureMesh()
    {
        if (_meshTried || Mesh is not null)
        {
            return;
        }

        _meshTried = true;

        string path = ModelPath;
        if (path.Length == 0)
        {
            return;
        }

        ArrayMesh? mesh = Refii.Level.Engine_PropStreamer.Resolve(path);
        if (mesh is null || mesh.GetSurfaceCount() == 0)
        {
            GD.PushWarning($"[equipment] {Name}: no geometry from {path}");
            return;
        }

        Mesh = mesh;

        Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
            this, Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting);
    }

    public override Array<Dictionary> _GetPropertyList()
    {
        var list = new Array<Dictionary>();

        Group(list, Text(KindMeta).Length != 0 ? Text(KindMeta) : "Equipment");
        Row(list, "Name", Text(NameMeta));

        if (Text(SlotMeta).Length != 0)
        {
            Row(list, "Slot");
        }

        Row(list, "Record Hash");

        if (ModelPath.Length != 0)
        {
            Row(list, "Model", ModelPath);
        }

        if (!HasMeta(DetailMeta))
        {
            return list;
        }

        Dictionary detail = GetMeta(DetailMeta).AsGodotDictionary();
        if (detail.Count == 0)
        {
            return list;
        }

        Group(list, "GDB Record");
        foreach (Variant key in detail.Keys)
        {
            Row(list, key.AsString(), detail[key].AsString());
        }

        return list;
    }

    public override Variant _Get(StringName property)
    {
        string name = property.ToString();

        switch (name)
        {
            case "Name":
                return Text(NameMeta);
            case "Slot":
                return Text(SlotMeta);
            case "Record Hash":
                return Text(HashMeta);
            case "Model":
                return ModelPath;
        }

        if (!HasMeta(DetailMeta))
        {
            return default;
        }

        Dictionary detail = GetMeta(DetailMeta).AsGodotDictionary();
        return detail.TryGetValue(name, out Variant value) ? value : default;
    }

    public override bool _Set(StringName property, Variant value) => false;

    public static Dictionary DetailRows(IEnumerable<KeyValuePair<string, string>> rows)
    {
        var stored = new Dictionary();
        foreach (KeyValuePair<string, string> row in rows)
        {
            if (row.Key.Length != 0 && row.Value.Length != 0)
            {
                stored[row.Key] = row.Value;
            }
        }

        return stored;
    }

    private string Text(string meta) =>
        HasMeta(meta) ? GetMeta(meta).AsString() : string.Empty;

    private static void Group(Array<Dictionary> list, string name) =>
        list.Add(new Dictionary
        {
            { "name", name },
            { "type", (int)Variant.Type.Nil },
            { "usage", (int)PropertyUsageFlags.Group },
            { "hint_string", string.Empty },
        });

    public const int MultilineThreshold = 64;

    public static bool NeedsMultiline(string value) =>
        value.Length > MultilineThreshold ||
        value.Contains('\n') ||
        value.Contains('\r');

    private static void Row(Array<Dictionary> list, string name) =>
        list.Add(new Dictionary
        {
            { "name", name },
            { "type", (int)Variant.Type.String },
            { "usage", (int)(PropertyUsageFlags.Editor | PropertyUsageFlags.ReadOnly) },
        });

    private static void Row(Array<Dictionary> list, string name, string value)
    {
        if (!NeedsMultiline(value))
        {
            Row(list, name);
            return;
        }

        list.Add(new Dictionary
        {
            { "name", name },
            { "type", (int)Variant.Type.String },
            { "usage", (int)(PropertyUsageFlags.Editor | PropertyUsageFlags.ReadOnly) },
            { "hint", (int)PropertyHint.MultilineText },
            { "hint_string", string.Empty },
        });
    }
}
