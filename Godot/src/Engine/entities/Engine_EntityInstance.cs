using System.Collections.Generic;
using Godot;
using Godot.Collections;
using Refii.Level;

namespace Refii.Entities;

[Tool]
[GlobalClass]
public partial class Engine_EntityInstance : MeshInstance3D
{
    public const string NameMeta = "entity_name";
    public const string HashMeta = "entity_hash";
    public const string ModelsMeta = "entity_models";
    public const string StatsMeta = "entity_stats";
    public const string CombatMeta = "entity_combat";
    public const string FactionMeta = "entity_faction";
    public const string ProfileMeta = "entity_profile";

    private bool _meshTried;

    public override void _Ready() => EnsureMesh();

    public void EnsureMesh()
    {
        if (_meshTried || Mesh is not null)
        {
            return;
        }

        _meshTried = true;

        if (!HasMeta(ModelsMeta))
        {
            return;
        }

        var mesh = new ArrayMesh();
        foreach (string path in GetMeta(ModelsMeta).AsStringArray())
        {
            ArrayMesh? part = Engine_PropStreamer.Resolve(path);
            if (part is null)
            {
                continue;
            }

            for (int surface = 0; surface < part.GetSurfaceCount(); surface++)
            {
                mesh.AddSurfaceFromArrays(
                    part.SurfaceGetPrimitiveType(surface),
                    part.SurfaceGetArrays(surface));

                int added = mesh.GetSurfaceCount() - 1;
                mesh.SurfaceSetName(added, part.SurfaceGetName(surface));
                mesh.SurfaceSetMaterial(added, part.SurfaceGetMaterial(surface));
            }
        }

        if (mesh.GetSurfaceCount() != 0)
        {
            Mesh = mesh;
        }
    }

    public override Array<Dictionary> _GetPropertyList()
    {
        var list = new Array<Dictionary>();

        Group(list, "Entity");
        Row(list, "Name");
        Row(list, "Hash");

        if (Text(FactionMeta).Length != 0)
        {
            Row(list, "Faction");
        }

        if (Text(ProfileMeta).Length != 0)
        {
            Row(list, "Combat Profile");
        }

        PairRows(list, "Stats", StatsMeta);
        PairRows(list, "Combat", CombatMeta);

        if (HasMeta(ModelsMeta))
        {
            string[] models = GetMeta(ModelsMeta).AsStringArray();
            if (models.Length != 0)
            {
                Group(list, "Models");
                for (int i = 0; i < models.Length; i++)
                {
                    Row(list, "Model " + (i + 1));
                }
            }
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
            case "Hash":
                return Text(HashMeta);
            case "Faction":
                return Text(FactionMeta);
            case "Combat Profile":
                return Text(ProfileMeta);
        }

        if (name.StartsWith("Model ", System.StringComparison.Ordinal) &&
            HasMeta(ModelsMeta) &&
            int.TryParse(name["Model ".Length..], out int index))
        {
            string[] models = GetMeta(ModelsMeta).AsStringArray();
            if (index >= 1 && index <= models.Length)
            {
                return models[index - 1];
            }
        }

        foreach (string meta in new[] { StatsMeta, CombatMeta })
        {
            if (!HasMeta(meta))
            {
                continue;
            }

            Dictionary pairs = GetMeta(meta).AsGodotDictionary();
            if (pairs.TryGetValue(name, out Variant value))
            {
                return value;
            }
        }

        return default;
    }

    public override bool _Set(StringName property, Variant value) => false;

    private string Text(string meta) =>
        HasMeta(meta) ? GetMeta(meta).AsString() : string.Empty;

    private void PairRows(Array<Dictionary> list, string group, string meta)
    {
        if (!HasMeta(meta))
        {
            return;
        }

        Dictionary pairs = GetMeta(meta).AsGodotDictionary();
        if (pairs.Count == 0)
        {
            return;
        }

        Group(list, group);
        foreach (Variant key in pairs.Keys)
        {
            Row(list, key.AsString());
        }
    }

    private static void Group(Array<Dictionary> list, string name) =>
        list.Add(new Dictionary
        {
            { "name", name },
            { "type", (int)Variant.Type.Nil },
            { "usage", (int)PropertyUsageFlags.Group },
            { "hint_string", string.Empty },
        });

    private static void Row(Array<Dictionary> list, string name) =>
        list.Add(new Dictionary
        {
            { "name", name },
            { "type", (int)Variant.Type.String },
            { "usage", (int)(PropertyUsageFlags.Editor | PropertyUsageFlags.ReadOnly) },
        });
}
