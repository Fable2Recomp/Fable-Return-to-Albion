using System;

using Godot;

namespace Refii.Titles.Fable2.PubGames;

[Tool]
public partial class Engine_SwfMeshLoader : ResourceFormatLoader
{
    public const string PathPrefix = Engine_SwfAssets.PathPrefix;

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        Engine_SwfAssets.RecognisesMesh(path);

    public override string[] _GetRecognizedExtensions() => new[] { "mesh" };

    public override bool _RecognizePath(string path, StringName type) => Recognises(path);

    public override bool _Exists(string path) => Recognises(path);

    public override bool _HandlesType(StringName type)
    {
        string name = type.ToString();
        return name is "ArrayMesh" or "Mesh" or "Resource";
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

        ArrayMesh? mesh = Engine_SwfAssets.TryGetMesh(path);
        if (mesh is null)
        {
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        return mesh;
    }
}
