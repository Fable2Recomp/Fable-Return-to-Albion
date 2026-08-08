using System;
using Godot;

namespace Refii.Gdb;

[Tool]
public partial class Engine_BnkGdbLoader : ResourceFormatLoader
{
    public const string PathPrefix = "game://";

    private static Func<string, byte[]?>? _resolver;

    public static void SetResolver(Func<string, byte[]?>? resolver) => _resolver = resolver;

    public static byte[]? ReadBytes(string virtualPath) => _resolver?.Invoke(virtualPath);

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase);

    public override string[] _GetRecognizedExtensions() => new[] { "gdb" };

    public override bool _RecognizePath(string path, StringName type) => Recognises(path);

    public override bool _Exists(string path) => Recognises(path);

    public override bool _HandlesType(StringName type)
    {
        string name = type.ToString();
        return name is "GdbDatabase" or "Resource";
    }

    public override string _GetResourceType(string path) =>
        Recognises(path) ? "GdbDatabase" : string.Empty;

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
            GD.PushWarning($"[bnk] database not found: {path}");
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        GdbDatabase? database = GdbDatabase.TryCreate(bytes, virtualPath, out string error);
        if (database is null)
        {
            GD.Print($"[bnk] {path}: {error}");
            return Variant.CreateFrom((int)Error.FileCorrupt);
        }

        database.ResourceName = virtualPath[(virtualPath.LastIndexOf('/') + 1)..];
        database.ResourcePath = path;
        return database;
    }
}
