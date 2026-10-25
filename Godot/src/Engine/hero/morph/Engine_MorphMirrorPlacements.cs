using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Level;
using Refii.Save;

namespace Refii.Hero;

public static class Engine_MorphMirrorPlacements
{
    public const string PlacementPath = "res://refii/morph_mirror/placement.tres";
    public const string Marker = "refii_morph_mirror";
    public const string SetName = "MorphMirrorSet";
    public const string ModelName = "bs_murgo_mirror.mdl";

    private static IEnumerable<Node3D> Nodes(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D spatial) yield return spatial;
            foreach (var nested in Nodes(child)) yield return nested;
        }
    }

    public static Engine_MorphMirrorPlacement? Load(string path = PlacementPath) =>
        ResourceLoader.Exists(path) ? ResourceLoader.Load<Engine_MorphMirrorPlacement>(path,
            cacheMode: ResourceLoader.CacheMode.Ignore) : null;

    public static bool IsMirror(string path) =>
        path.Replace('\\', '/').Split('/')[^1].Equals(ModelName, StringComparison.OrdinalIgnoreCase);

    public static (Transform3D Hero, Vector3 Camera) PreviewFor(Transform3D mirror)
    {
        Vector3 front = new Vector3(-mirror.Basis.Z.X, 0, -mirror.Basis.Z.Z).Normalized();
        Vector3 stand = mirror.Origin + front * 2.8f;
        Vector3 eye = mirror * new Vector3(0, 0.85f, -0.25f);
        Vector3 facing = eye - stand;
        facing.Y = 0;
        return (new Transform3D(Basis.LookingAt(facing, Vector3.Up), stand), eye);
    }

    public static bool Capture(Node3D level, string path = PlacementPath)
    {
        var marked = Nodes(level).Where(n => n.GetMeta(Marker, false).AsBool() || n.Name == "MorphMirror").ToArray();
        if (marked.Length == 0) return false;
        if (marked.Length != 1) throw new InvalidOperationException("Mark exactly one MorphMirror instance; the previous installation was kept.");
        Node3D mirror = marked[0];
        if (!IsMirror(LevelRecordMetadata.FindModelPath(mirror)))
            throw new InvalidOperationException("MorphMirror must be the placed bs_murgo_mirror.mdl instance.");
        mirror.SetMeta(Marker, true);
        string name = level.GetMeta(LevelRecordMetadata.LevelNameMeta, "").AsString();
        if (name.Length == 0) throw new InvalidOperationException("Place MorphMirror inside an imported game level.");
        Transform3D pose = Engine_GoldenCockPlacements.RelativePose(ModelNode(mirror), level);
        Node3D group = mirror;
        for (Node? parent = mirror.GetParent(); parent is not null && parent != level; parent = parent.GetParent())
            if (parent is Node3D spatial && parent.Name == SetName) { group = spatial; break; }
        var placement = new Engine_MorphMirrorPlacement
        {
            World = level.GetMeta(Engine_GoldenCockPlacements.WorldMeta, "albion").AsString(),
            LevelPath = level.GetMeta(Engine_GoldenCockPlacements.PathMeta, "").AsString(),
            LevelName = name, Scenario = level.GetMeta(LevelRecordMetadata.ScenarioNameMeta, "").AsString(),
            SourceScene = level.SceneFilePath, InstancePath = level.GetPathTo(mirror).ToString(), Pose = pose, Enabled = true
        };
        var captured = new HashSet<Node3D>();
        AddModel(mirror);
        if (group != mirror)
            foreach (Node3D node in Nodes(group))
                if (node != mirror && !HasModelAncestor(node, group)) AddModel(node);
        (placement.HeroPose, placement.CameraPosition) = PreviewFor(pose);
        foreach (Node3D node in Nodes(group))
        {
            if (node.Name == "HeroStand") placement.HeroPose = Engine_GoldenCockPlacements.RelativePose(node, level).Orthonormalized();
            if (node.Name == "PreviewCamera")
            {
                placement.CameraPosition = Engine_GoldenCockPlacements.RelativePose(node, level).Origin;
                if (node is Camera3D camera) placement.CameraFov = camera.Fov;
            }
        }
        if (!placement.CameraPosition.IsFinite() || placement.CameraPosition.DistanceSquaredTo(placement.HeroPose.Origin + Vector3.Up * 1.05f) < 0.25f ||
            !float.IsFinite(placement.CameraFov) || placement.CameraFov < 10 || placement.CameraFov > 120)
            throw new InvalidOperationException("The MorphMirror preview camera needs a valid position away from HeroStand and a field of view between 10 and 120 degrees.");
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(path.GetBaseDir()));
        Error error = ResourceSaver.Save(placement, path);
        if (error != Error.Ok) throw new InvalidOperationException("Could not save MorphMirror placement: " + error);
        return true;

        void AddModel(Node3D node)
        {
            node = ModelNode(node);
            string model = LevelRecordMetadata.FindModelPath(node);
            if (!model.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || !captured.Add(node)) return;
            placement.Models.Add(model.StartsWith("game://", StringComparison.OrdinalIgnoreCase) ? model[7..] : model);
            placement.Poses.Add(Engine_GoldenCockPlacements.RelativePose(node, level));
        }
    }

    private static Node3D ModelNode(Node3D node) =>
        node.HasMeta(LevelRecordMetadata.ModelPathMeta) || node is MeshInstance3D ? node :
        node.GetChildren().OfType<Node3D>().FirstOrDefault(child => child.HasMeta(LevelRecordMetadata.ModelPathMeta) || child is MeshInstance3D) ?? node;

    private static bool HasModelAncestor(Node node, Node root)
    {
        for (Node? parent = node.GetParent(); parent is not null && parent != root; parent = parent.GetParent())
            if (parent.HasMeta(LevelRecordMetadata.ModelPathMeta) || parent is MeshInstance3D) return true;
        return false;
    }

    public static void AddToLevel(Node3D root, LevelEntry level)
    {
        var placement = Load();
        if (placement is not { Enabled: true } || !Engine_GoldenCockPlacements.Matches(placement, level.World, level.LevelPath, level.Name)) return;
        if (placement.Scenario.Length > 0 && !placement.Scenario.Equals(level.Scenario, StringComparison.OrdinalIgnoreCase)) return;
        if (root.GetNodeOrNull<Node>(SetName) is not null) return;
        var actor = new Engine_MorphMirror { Name = SetName, Placement = placement };
        root.AddChild(actor);
        actor.Owner = root;
    }
}
