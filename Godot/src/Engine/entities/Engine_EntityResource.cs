using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace Refii.Entities;

[Tool]
[GlobalClass]
public partial class Engine_EntityResource : ArrayMesh
{
    public string EntityName { get; set; } = string.Empty;

    public string EntityHash { get; set; } = string.Empty;

    public string NameTag { get; set; } = string.Empty;

    public string Faction { get; set; } = string.Empty;

    public string CombatProfile { get; set; } = string.Empty;

    public List<string> ModelPaths { get; } = new();

    public List<uint> ModelHashes { get; } = new();

    public List<string> MissingModels { get; } = new();

    public List<string> Components { get; } = new();

    public List<KeyValuePair<string, string>> Stats { get; } = new();

    public List<KeyValuePair<string, string>> Combat { get; } = new();

    public List<KeyValuePair<string, string>> GdbFields { get; } = new();

    public List<KeyValuePair<string, string>> CreatureFields { get; } = new();

    public override Array<Dictionary> _GetPropertyList()
    {
        var list = new Array<Dictionary>();

        AddGroup(list, "Entity");
        AddRow(list, "Name");
        AddRow(list, "Hash");

        if (NameTag.Length != 0)
        {
            AddRow(list, "Name Tag");
        }

        if (Faction.Length != 0)
        {
            AddRow(list, "Faction");
        }

        if (CombatProfile.Length != 0)
        {
            AddRow(list, "Combat Profile");
        }

        AddPairGroup(list, "Stats", Stats);
        AddPairGroup(list, "Combat", Combat);
        AddListGroup(list, "Models", ModelPaths);
        AddListGroup(list, "Missing Models", MissingModels);
        AddListGroup(list, "Components", Components);
        AddPairGroup(list, "Creature Component", CreatureFields);
        AddPairGroup(list, "All GDB Fields", GdbFields);

        return list;
    }

    public override Variant _Get(StringName property)
    {
        string name = property.ToString();

        switch (name)
        {
            case "Name":
                return EntityName;
            case "Hash":
                return EntityHash;
            case "Name Tag":
                return NameTag;
            case "Faction":
                return Faction;
            case "Combat Profile":
                return CombatProfile;
        }

        foreach (List<KeyValuePair<string, string>> pairs in
                 new[] { Stats, Combat, CreatureFields, GdbFields })
        {
            foreach (KeyValuePair<string, string> pair in pairs)
            {
                if (pair.Key == name)
                {
                    return pair.Value;
                }
            }
        }

        if (TryIndexed(ModelPaths, "Model ", name, out string model))
        {
            return model;
        }

        if (TryIndexed(MissingModels, "Missing ", name, out string missing))
        {
            return missing;
        }

        if (TryIndexed(Components, "Component ", name, out string component))
        {
            return component;
        }

        return default;
    }

    public override bool _Set(StringName property, Variant value) => false;

    private static bool TryIndexed(
        List<string> values, string prefix, string name, out string found)
    {
        found = string.Empty;

        if (!name.StartsWith(prefix, System.StringComparison.Ordinal) ||
            !int.TryParse(name[prefix.Length..], out int index) ||
            index < 1 || index > values.Count)
        {
            return false;
        }

        found = values[index - 1];
        return true;
    }

    private static void AddGroup(Array<Dictionary> list, string name) =>
        list.Add(new Dictionary
        {
            { "name", name },
            { "type", (int)Variant.Type.Nil },
            { "usage", (int)PropertyUsageFlags.Group },
            { "hint_string", string.Empty },
        });

    private static void AddRow(Array<Dictionary> list, string name) =>
        list.Add(new Dictionary
        {
            { "name", name },
            { "type", (int)Variant.Type.String },
            { "usage", (int)(PropertyUsageFlags.Editor | PropertyUsageFlags.ReadOnly) },
        });

    private static void AddPairGroup(
        Array<Dictionary> list, string group, List<KeyValuePair<string, string>> pairs)
    {
        if (pairs.Count == 0)
        {
            return;
        }

        AddGroup(list, group);
        foreach (KeyValuePair<string, string> pair in pairs)
        {
            AddRow(list, pair.Key);
        }
    }

    private static void AddListGroup(
        Array<Dictionary> list, string group, List<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        AddGroup(list, group);

        string prefix = group switch
        {
            "Models" => "Model ",
            "Missing Models" => "Missing ",
            _ => "Component ",
        };

        for (int i = 0; i < values.Count; i++)
        {
            AddRow(list, prefix + (i + 1));
        }
    }
}
