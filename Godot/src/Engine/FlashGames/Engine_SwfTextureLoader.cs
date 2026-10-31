using System;

using Godot;

namespace Refii.FlashGames;

[Tool]
public partial class Engine_SwfTextureLoader : ResourceFormatLoader
{
    public const string PathPrefix = "game://";

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        Engine_SwfAssets.RecognisesTexture(path);

    public override string[] _GetRecognizedExtensions() => new[] { "png" };

    public override bool _RecognizePath(string path, StringName type) => Recognises(path);

    public override bool _Exists(string path) => Recognises(path);

    public override bool _HandlesType(StringName type)
    {
        string name = type.ToString();
        return name is "Texture2D" or "Texture" or "Resource" or "ImageTexture";
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

        Image? image = Engine_SwfAssets.TryGetEnhancedImage(path);
        if (image is null)
        {
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        ImageTexture texture = Refii.Textures.Engine_AndroidTextureUpload.CreateTexture(image);
        texture.TakeOverPath(path);
        return texture;
    }
}
