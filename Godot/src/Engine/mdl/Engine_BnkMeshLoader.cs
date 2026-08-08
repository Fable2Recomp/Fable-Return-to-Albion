using System;
using Godot;

namespace Refii.Models;

[Tool]
public partial class Engine_BnkMeshLoader : ResourceFormatLoader
{
    public const string PathPrefix = "game://";

    private static Func<string, byte[]?>? _resolver;
    private static Func<string, bool>? _exists;

    public static void SetResolver(Func<string, byte[]?>? resolver) => _resolver = resolver;

    public static void SetExistsProbe(Func<string, bool>? probe) => _exists = probe;

    public static bool FileExists(string virtualPath) => _exists?.Invoke(virtualPath) ?? false;

    public static byte[]? ReadBytes(string virtualPath) => _resolver?.Invoke(virtualPath);

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);

    public override string[] _GetRecognizedExtensions() => new[] { "mdl" };

    public override bool _RecognizePath(string path, StringName type) => Recognises(path);

    public override bool _Exists(string path) => Recognises(path);

    public override bool _HandlesType(StringName type)
    {
        string name = type.ToString();
        return name is "Mesh" or "ArrayMesh" or "Resource";
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
        byte[]? bytes = _resolver?.Invoke(virtualPath);
        if (bytes is null)
        {
            GD.PushWarning($"[bnk] model not found: {path}");
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        ArrayMesh? mesh = Engine_MdlMesh.TryCreateMesh(
            bytes, virtualPath, out string error, out bool isStub);

        if (mesh is null)
        {
            mesh = new ArrayMesh();
            if (!isStub)
            {
                GD.Print($"[bnk] {path}: {error}");
            }
        }

        mesh.ResourceName = virtualPath[(virtualPath.LastIndexOf('/') + 1)..];
        mesh.ResourcePath = path;
        return mesh;
    }
}
