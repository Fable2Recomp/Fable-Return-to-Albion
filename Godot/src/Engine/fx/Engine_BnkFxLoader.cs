using System;
using Godot;

namespace Refii.Fx;

[Tool]
public partial class Engine_BnkFxLoader : ResourceFormatLoader
{
    public const string PathPrefix = "game://";

    public const string Extension = ".pfx";

    public const string Directory = "art/particles/particle_bank.bnk/";

    public static string VirtualPathOf(string effectName) =>
        Directory + effectName + Extension;

    public static string PathOf(string effectName) =>
        PathPrefix + VirtualPathOf(effectName);

    public static string EffectNameOf(string path)
    {
        string value = path;
        if (value.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[PathPrefix.Length..];
        }

        int slash = value.LastIndexOf('/');
        if (slash >= 0)
        {
            value = value[(slash + 1)..];
        }

        if (value.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^Extension.Length];
        }

        return value;
    }

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static bool TryParseHashToken(string token, out uint hash)
    {
        hash = 0;
        return token.Length == 11 &&
            token.StartsWith("FX_", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(
                token.AsSpan(3),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out hash);
    }

    public static FxEffect? Resolve(FxBank bank, string token) =>
        TryParseHashToken(token, out uint hash)
            ? bank.FindByHash(hash)
            : bank.FindByName(token);

    public override string[] _GetRecognizedExtensions() => new[] { "pfx" };

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

        string effect = EffectNameOf(path);
        FxBank? bank = FableFx.SharedBank();
        if (bank is null)
        {
            GD.PushWarning($"[bnk] fx bank unavailable for {path}");
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        if (Resolve(bank, effect) is null)
        {
            GD.PushWarning($"[bnk] fx effect not in bank: {effect}");
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        var node = new FableFx { Name = effect, EffectName = effect };
        var scene = new PackedScene();
        scene.Pack(node);
        scene.ResourceName = effect;
        scene.ResourcePath = path;
        node.Free();
        return scene;
    }
}
