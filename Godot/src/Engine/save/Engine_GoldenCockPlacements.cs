using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Level;

namespace Refii.Save;

public static class Engine_GoldenCockPlacements
{
    public const string ScenePath = "res://refii/golden_cock/GoldenCock.tscn";
    public const string ModelPath = "res://refii/golden_cock/golden_cock.glb";
    public const string PlacementPath = "res://refii/golden_cock/placement.tres";
    public const string WorldMeta = "refii_level_world";
    public const string PathMeta = "refii_level_path";

    public static IEnumerable<Engine_GoldenCock> Objects(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is Engine_GoldenCock cock) yield return cock;
            else foreach (var nested in Objects(child)) yield return nested;
        }
    }

    private static IEnumerable<Node3D> AuthoredObjects(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is Engine_GoldenCock cock) yield return cock;
            else if (child is Node3D model && string.Equals(model.SceneFilePath, ModelPath, StringComparison.Ordinal))
                yield return model;
            else foreach (var nested in AuthoredObjects(child)) yield return nested;
        }
    }

    public static Transform3D RelativePose(Node3D node, Node3D level)
    {
        Transform3D pose = Transform3D.Identity;
        Node? current = node;
        while (current != level)
        {
            if (current is null) throw new InvalidOperationException("Golden Cock must be inside the level scene.");
            if (current is Node3D spatial)
            {
                if (spatial.TopLevel) throw new InvalidOperationException("Golden Cock and its parents must inherit the level transform.");
                pose = spatial.Transform * pose;
            }
            current = current.GetParent();
        }
        if (!pose.IsFinite() || Math.Abs(pose.Basis.Determinant()) < 0.000001f)
            throw new InvalidOperationException("Golden Cock has an invalid or zero-scale transform.");
        return pose;
    }

    public static bool Capture(Node3D level, string path = PlacementPath)
    {
        var objects = AuthoredObjects(level).ToArray();
        if (objects.Length > 1) throw new InvalidOperationException("Place only one Golden Cock. The previous placement was kept.");
        string name = level.GetMeta(LevelRecordMetadata.LevelNameMeta, "").AsString();
        if (name.Length == 0) throw new InvalidOperationException("Open a game level before placing Golden Cock.");
        string world = level.GetMeta(WorldMeta, "albion").AsString();
        string levelPath = level.GetMeta(PathMeta, "").AsString();
        var previous = Load(path);
        if (objects.Length == 0)
        {
            if (previous is null || !Matches(previous, world, levelPath, name)) return false;
            previous.Enabled = false;
            Save(previous, path);
            return true;
        }
        var placement = new Engine_GoldenCockPlacement
        {
            World = world, LevelPath = levelPath, LevelName = name,
            Scenario = level.GetMeta(LevelRecordMetadata.ScenarioNameMeta, "").AsString(),
            Pose = RelativePose(objects[0], level), Enabled = true
        };
        Save(placement, path);
        return true;
    }

    public static Engine_GoldenCockPlacement? Load(string path = PlacementPath) =>
        ResourceLoader.Exists(path) ? ResourceLoader.Load<Engine_GoldenCockPlacement>(path, cacheMode: ResourceLoader.CacheMode.Ignore) : null;

    public static void Save(Engine_GoldenCockPlacement placement, string path)
    {
        Error error = ResourceSaver.Save(placement, path);
        if (error != Error.Ok) throw new InvalidOperationException("Could not save Golden Cock placement: " + error);
    }

    public static bool Matches(Engine_GoldenCockPlacement placement, string world, string path, string name) =>
        string.Equals(placement.World, world, StringComparison.OrdinalIgnoreCase) &&
        (placement.LevelPath.Length > 0 && path.Length > 0
            ? string.Equals(placement.LevelPath, path, StringComparison.OrdinalIgnoreCase)
            : string.Equals(placement.LevelName, name, StringComparison.OrdinalIgnoreCase));

    public static Engine_GoldenCock? AddToLevel(Node3D root, LevelEntry level, string path = PlacementPath)
    {
        if (Objects(root).Any()) return null;
        var placement = Load(path);
        if (placement is not { Enabled: true } || !Matches(placement, level.World, level.LevelPath, level.Name)) return null;
        if (placement.Scenario.Length > 0 && !string.Equals(placement.Scenario, level.Scenario, StringComparison.OrdinalIgnoreCase)) return null;
        var scene = ResourceLoader.Load<PackedScene>(ScenePath);
        var actor = scene.Instantiate<Engine_GoldenCock>();
        actor.Transform = placement.Pose;
        root.AddChild(actor);
        actor.Owner = root;
        return actor;
    }
}
