using System;

using Godot;

namespace Refii.FlashGames;

[Tool]
public partial class Engine_SwfFontLoader : ResourceFormatLoader
{
    public const string PathPrefix = Engine_SwfAssets.PathPrefix;

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        Engine_SwfAssets.RecognisesFont(path);

    public override string[] _GetRecognizedExtensions() => new[] { "tres" };

    public override bool _RecognizePath(string path, StringName type) => Recognises(path);

    public override bool _Exists(string path) => Recognises(path);

    public override bool _HandlesType(StringName type)
    {
        string name = type.ToString();
        return name is "FontFile" or "Font" or "Resource";
    }

    public override string _GetResourceType(string path) =>
        Recognises(path) ? "FontFile" : string.Empty;

    public override Variant _Load(
        string path, string originalPath, bool useSubThreads, int cacheMode)
    {
        if (!Recognises(path))
        {
            return Variant.CreateFrom((int)Error.FileUnrecognized);
        }

        FontFile? font = Engine_SwfAssets.TryBuildFont(path);
        if (font is null)
        {
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        return font;
    }
}
