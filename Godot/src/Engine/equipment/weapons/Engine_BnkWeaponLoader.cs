using System;
using Godot;

namespace Refii.Equipment;

[Tool]
public partial class Engine_BnkWeaponLoader : ResourceFormatLoader
{
    public const string PathPrefix = "game://";

    public static bool Recognises(string path) =>
        path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(Engine_WeaponIndex.Extension, StringComparison.OrdinalIgnoreCase);

    public override string[] _GetRecognizedExtensions() => new[] { "weapon" };

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
        if (!Engine_WeaponIndex.TryGet(virtualPath, out WeaponEntry entry))
        {
            GD.PushWarning($"[weapons] not found: {path}");
            return Variant.CreateFrom((int)Error.FileNotFound);
        }

        PackedScene? scene = Engine_EquipmentScene.ForWeapon(entry);
        if (scene is null)
        {
            return Variant.CreateFrom((int)Error.FileCorrupt);
        }

        scene.ResourcePath = path;
        return scene;
    }
}
