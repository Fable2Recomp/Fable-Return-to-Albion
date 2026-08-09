using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Entities;

public sealed class EntityPlaceStats
{
    public int SaveEntities { get; set; }

    public int Creatures { get; set; }

    public int Placed { get; set; }

    public int NoTransform { get; set; }

    public override string ToString() =>
        $"save={SaveEntities} creatures={Creatures} placed={Placed} " +
        $"noTransform={NoTransform}";
}

public static class Engine_LevelEntities
{
    public const string NodeName = "Entities";

    public static Node3D? Build(
        LevelEntry level, Func<string, byte[]?> read, out EntityPlaceStats stats) =>
        Build(level, read, null, null, out stats);

    public static Node3D? Build(
        LevelEntry level,
        Func<string, byte[]?> read,
        Refii.Lighting.LightProbeField? probes,
        Refii.Lighting.LightingFrame? lighting,
        out EntityPlaceStats stats)
    {
        stats = new EntityPlaceStats();

        byte[]? levelGdb = Engine_LevelGdb.TryRead(level, read, out string gdbPath);
        if (levelGdb is null || !GdbView.TryParse(levelGdb, out GdbView levelView, out _))
        {
            return null;
        }

        List<KeyValuePair<uint, string>> saveMap = Engine_GdbSaveMap.Parse(
            read(Engine_LevelGdb.SiblingWithExtension(gdbPath, ".save")));

        stats.SaveEntities = saveMap.Count;
        if (saveMap.Count == 0)
        {
            return null;
        }

        var blobs = new List<byte[]?> { levelGdb };
        foreach (string path in Engine_LevelProps.SupplementalGdbPaths)
        {
            blobs.Add(read(path) ?? read("data/" + path));
        }

        GdbMulti multi = GdbMulti.Build(blobs);
        var container = new Node3D { Name = NodeName };
        var used = new HashSet<string>();

        foreach (KeyValuePair<uint, string> saved in saveMap)
        {
            if (saved.Key == 0 ||
                !Engine_EntityCatalog.TryDescribe(multi, saved.Key, out EntityEntry entry))
            {
                continue;
            }

            stats.Creatures++;

            if (!Engine_GdbPlacements.TryLookupPlacement(
                    levelView, saved.Key, saved.Value, true, out GdbPlacement placement))
            {
                stats.NoTransform++;
                continue;
            }

            if (saved.Value.Length != 0)
            {
                entry.Name = saved.Value;
            }

            var node = new Engine_EntityInstance
            {
                Name = UniqueName(used, saved.Value.Length != 0 ? saved.Value : entry.Name),
                Transform = Engine_LevelProps.BuildTransform(placement),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
            };

            node.SetMeta(Engine_EntityInstance.NameMeta, entry.Name);
            node.SetMeta(Engine_EntityInstance.HashMeta, $"0x{entry.EntityHash:X8}");

            var models = new Godot.Collections.Array<string>();
            foreach (uint modelHash in entry.ModelHashes)
            {
                if (Engine_PropModelIndex.Resolve(
                        modelHash, Array.Empty<string>()) is string modelPath)
                {
                    models.Add(modelPath);
                }
            }

            node.SetMeta(Engine_EntityInstance.ModelsMeta, models);

            EntityGameplay? gameplay = Engine_EntityGameplay.Extract(multi, saved.Key);
            if (gameplay is not null)
            {
                node.SetMeta(Engine_EntityInstance.FactionMeta, gameplay.FactionName);
                node.SetMeta(Engine_EntityInstance.ProfileMeta, gameplay.CombatProfileName);
                node.SetMeta(Engine_EntityInstance.StatsMeta, ToDictionary(gameplay.CoreFields));
                node.SetMeta(
                    Engine_EntityInstance.CombatMeta, ToDictionary(gameplay.CombatFields));
            }

            Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
                node, node.Transform.Origin, probes, lighting);
            container.AddChild(node);
            stats.Placed++;
        }

        if (stats.Placed == 0)
        {
            container.QueueFree();
            return null;
        }

        return container;
    }

    private static Godot.Collections.Dictionary ToDictionary(
        List<KeyValuePair<string, string>> pairs)
    {
        var map = new Godot.Collections.Dictionary();
        foreach (KeyValuePair<string, string> pair in pairs)
        {
            map[pair.Key] = pair.Value;
        }

        return map;
    }

    private static string UniqueName(HashSet<string> used, string raw)
    {
        var text = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            text.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or ' ' ? c : '_');
        }

        string name = text.ToString().Trim();
        if (name.Length == 0)
        {
            name = "Entity";
        }

        if (used.Add(name))
        {
            return name;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{name} ({i})";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
