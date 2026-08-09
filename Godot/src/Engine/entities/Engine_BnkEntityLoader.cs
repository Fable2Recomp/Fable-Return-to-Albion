using System;
using Godot;

namespace Refii.Entities;

[Tool]
public partial class Engine_BnkEntityLoader : ResourceFormatLoader
{
    public const string PathPrefix = "game://";

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(Engine_EntityIndex.Extension, StringComparison.OrdinalIgnoreCase);

    public override string[] _GetRecognizedExtensions() => new[] { "entity" };

    public override bool _RecognizePath(string path, StringName type) => Recognises(path);

    public override bool _Exists(string path) => Recognises(path);

    public override bool _HandlesType(StringName type)
    {
        string name = type.ToString();
        return name is "Mesh" or "ArrayMesh" or "Engine_EntityResource" or "Resource";
    }

    public override string _GetResourceType(string path) =>
        Recognises(path) ? "ArrayMesh" : string.Empty;

    public override Variant _Load(
        string path, string originalPath, bool useSubThreads, int cacheMode)
    {
        if (!Recognises(path))
        {
            return Variant.CreateFrom((int)Error.FileUnrecognized);
        }

        string virtualPath = path[PathPrefix.Length..];
        Engine_EntityResource? resource = Engine_EntityIndex.BuildResource(virtualPath);

        if (resource is null)
        {
            GD.PushWarning($"[entities] not found: {path}");
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        resource.ResourceName = resource.EntityName;
        resource.ResourcePath = path;
        return resource;
    }
}
