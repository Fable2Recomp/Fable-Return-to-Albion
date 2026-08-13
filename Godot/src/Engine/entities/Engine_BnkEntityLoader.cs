using System;
using Godot;

namespace Refii.Entities;

[Tool]
public partial class Engine_BnkEntityLoader : ResourceFormatLoader
{
    public const string PathPrefix = "game://";

    public static bool IsHero(string virtualPath) =>
        virtualPath.Replace('\\', '/').StartsWith(
            Refii.Hero.Engine_HeroCatalog.Folder + "/", StringComparison.OrdinalIgnoreCase);

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(Engine_EntityIndex.Extension, StringComparison.OrdinalIgnoreCase);

    public override string[] _GetRecognizedExtensions() => new[] { "entity" };

    public override bool _RecognizePath(string path, StringName type) => Recognises(path);

    public override bool _Exists(string path) => Recognises(path);

    public override bool _HandlesType(StringName type)
    {
        string name = type.ToString();
        return name is "PackedScene" or "Resource";
    }

    public override string _GetResourceType(string path) =>
        Recognises(path) ? "PackedScene" : string.Empty;

    public override Variant _Load(
        string path, string originalPath, bool useSubThreads, int cacheMode)
    {
        if (!Recognises(path))
        {
            return Variant.CreateFrom((int)Error.FileUnrecognized);
        }

        string virtualPath = path[PathPrefix.Length..];

        Engine_EntityResource? resource = IsHero(virtualPath)
            ? Refii.Hero.Engine_HeroCatalog.BuildResource(virtualPath)
            : Engine_EntityIndex.BuildResource(virtualPath);

        if (resource is null)
        {
            GD.PushWarning($"[entities] not found: {path}");
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        resource.ResourceName = resource.EntityName;

        var node = new Engine_EntityInstance
        {
            Name = resource.EntityName.Length != 0 ? resource.EntityName : "Entity",
            Mesh = resource,
        };

        node.SetMeta(Engine_EntityInstance.NameMeta, resource.EntityName);
        node.SetMeta(Engine_EntityInstance.HashMeta, resource.EntityHash);

        var models = new Godot.Collections.Array<string>();
        foreach (string model in resource.ModelPaths)
        {
            models.Add(model);
        }

        var hashes = new Godot.Collections.Array<long>();
        foreach (uint hash in resource.ModelHashes)
        {
            hashes.Add(hash);
        }

        node.SetMeta(Engine_EntityInstance.ModelsMeta, models);
        node.SetMeta(Engine_EntityInstance.ModelHashesMeta, hashes);

        var animator = new Refii.Animations.Engine_EntityAnimator
        {
            Name = Refii.Animations.Engine_EntityAnimator.NodeName,
        };

        node.AddChild(animator);
        animator.Owner = node;

        var scene = new PackedScene();
        Error packed = scene.Pack(node);
        node.Free();

        if (packed != Error.Ok)
        {
            GD.PushWarning($"[entities] could not pack {path}: {packed}");
            return Variant.CreateFrom((int)packed);
        }

        scene.ResourceName = resource.EntityName;
        scene.ResourcePath = path;
        return scene;
    }
}
