using System;
using System.IO.Compression;
using Godot;

namespace Refii.Level;

[Tool]
public partial class Engine_BnkTerrainLoader : ResourceFormatLoader
{
    public const string PathPrefix = "game://";

    private static Func<string, byte[]?>? _resolver;

    public static void SetResolver(Func<string, byte[]?>? resolver) => _resolver = resolver;

    public static byte[]? ReadBytes(string virtualPath) => _resolver?.Invoke(virtualPath);

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".ghf", StringComparison.OrdinalIgnoreCase);

    public override string[] _GetRecognizedExtensions() => new[] { "ghf" };

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
            GD.PushWarning($"[bnk] heightfield not found: {path}");
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        if (!Engine_GhfTerrain.TryDecode(bytes, out GhfHeights heights, out string error))
        {
            GD.Print($"[bnk] {path}: {error}");
            return Variant.CreateFrom((int)Error.FileCorrupt);
        }

        TerrainMesh terrain = Engine_GhfTerrain.BuildMesh(heights);
        ArrayMesh mesh = Engine_LevelMesh.ToArrayMesh(terrain, "Terrain");
        mesh.ResourceName = virtualPath[(virtualPath.LastIndexOf('/') + 1)..];
        mesh.ResourcePath = path;
        return mesh;
    }
}

[Tool]
public partial class Engine_BnkTerrainAlbedoLoader : ResourceFormatLoader
{
    public const string PathPrefix = "game://";

    private static Func<string, byte[]?>? _resolver;
    private static Func<string, string?>? _heightfieldForEhf;

    public static void SetResolver(Func<string, byte[]?>? resolver) => _resolver = resolver;

    public static void SetHeightfieldLookup(Func<string, string?>? lookup) =>
        _heightfieldForEhf = lookup;

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".ehf", StringComparison.OrdinalIgnoreCase);

    public override string[] _GetRecognizedExtensions() => new[] { "ehf" };

    public override bool _RecognizePath(string path, StringName type) => Recognises(path);

    public override bool _Exists(string path) => Recognises(path);

    public override bool _HandlesType(StringName type)
    {
        string name = type.ToString();
        return name is "Texture2D" or "ImageTexture" or "Resource";
    }

    public override string _GetResourceType(string path) =>
        Recognises(path) ? "ImageTexture" : string.Empty;

    public override Variant _Load(
        string path, string originalPath, bool useSubThreads, int cacheMode)
    {
        if (!Recognises(path))
        {
            return Variant.CreateFrom((int)Error.FileUnrecognized);
        }

        string virtualPath = path[PathPrefix.Length..];
        byte[]? ehf = _resolver?.Invoke(virtualPath);
        if (ehf is null)
        {
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        string? heightfield = _heightfieldForEhf?.Invoke(virtualPath);
        int verticesX = 0;
        int verticesZ = 0;

        if (heightfield is not null)
        {
            byte[]? ghf = _resolver?.Invoke(heightfield);
            if (ghf is not null &&
                Engine_GhfTerrain.TryDecode(ghf, out GhfHeights heights, out _))
            {
                verticesX = heights.Width;
                verticesZ = heights.Height;
            }
        }

        // A vista has no .ghf; its .ehf states the same sample counts.
        if ((verticesX == 0 || verticesZ == 0) &&
            !Engine_EhfHeights.TryReadSize(ehf, out verticesX, out verticesZ))
        {
            return Variant.CreateFrom((int)Error.FileCorrupt);
        }

        if (!Engine_EhfAlbedo.TryBuild(ehf, verticesX, verticesZ, out EhfAlbedo albedo, out string why))
        {
            GD.Print($"[bnk] {path}: no terrain albedo ({why})");
            return Variant.CreateFrom((int)Error.FileCorrupt);
        }

        Image image = Image.CreateFromData(
            albedo.Width, albedo.Height, false, Image.Format.Rgba8, albedo.Rgba);

        ImageTexture texture = ImageTexture.CreateFromImage(image);
        texture.ResourceName = virtualPath[(virtualPath.LastIndexOf('/') + 1)..];
        texture.ResourcePath = path;
        return texture;
    }
}

[Tool]
public partial class Engine_BnkTerrainMaterialLoader : ResourceFormatLoader
{
    public const string PathPrefix = "game://";

    public const string PathSuffix = ".mat";

    private static Func<string, byte[]?>? _resolver;
    private static Func<string, string?>? _findByName;

    public static void SetResolver(Func<string, byte[]?>? resolver) => _resolver = resolver;

    public static void SetNameLookup(Func<string, string?>? lookup) => _findByName = lookup;

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".ehf" + PathSuffix, StringComparison.OrdinalIgnoreCase);

    public override string[] _GetRecognizedExtensions() => new[] { "mat" };

    public override bool _RecognizePath(string path, StringName type) => Recognises(path);

    public override bool _Exists(string path) => Recognises(path);

    public override bool _HandlesType(StringName type)
    {
        string name = type.ToString();
        return name is "ShaderMaterial" or "Material" or "Resource";
    }

    public override string _GetResourceType(string path) =>
        Recognises(path) ? "ShaderMaterial" : string.Empty;

    public override Variant _Load(
        string path, string originalPath, bool useSubThreads, int cacheMode)
    {
        if (!Recognises(path))
        {
            return Variant.CreateFrom((int)Error.FileUnrecognized);
        }

        string virtualPath = path[PathPrefix.Length..^PathSuffix.Length];
        byte[]? ehf = _resolver?.Invoke(virtualPath);
        if (ehf is null)
        {
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        if (_resolver is null)
        {
            return Variant.CreateFrom((int)Error.Unconfigured);
        }

        if (!Engine_TerrainSplatMaterial.TryBuild(
                ehf,
                p => _resolver(p),
                _findByName is null ? null : n => _findByName(n),
                out ShaderMaterial material,
                out string error))
        {
            GD.Print($"[bnk] {path}: no terrain splat ({error})");
            return Variant.CreateFrom((int)Error.FileCorrupt);
        }

        material.ResourceName = virtualPath[(virtualPath.LastIndexOf('/') + 1)..];
        material.ResourcePath = path;
        return material;
    }
}
