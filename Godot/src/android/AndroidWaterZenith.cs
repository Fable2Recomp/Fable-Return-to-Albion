using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_water_zenith_once`: the water's foam takes the zenith sky (dome_reflect straight up)
/// from its vertices through a flat varying instead of working it out on every foam pixel
/// (water.gdshader, FABLE_WATER_ZENITH_VERTEX). Off by default; read once per process.
/// </summary>
/// <remarks>
/// THE SAME PICTURE: the zenith's colour depends only on uniforms, so it is one value for the whole
/// frame; the vertex runs the same expression (the in-scatter table has no mips, so its fetch is
/// level 0 in either stage). Saves the dome's table fetch, exp2/log2, atan, asin and two overlay
/// fetches on each foam-band pixel; costs them once per water vertex and three flat varying
/// components on every water pixel. Whether that pays depends on the foam's screen share.
/// </remarks>
public static class AndroidWaterZenith
{
    /// <summary>The refii.toml setting.</summary>
    public const string Key = "android_water_zenith_once";

    /// <summary>The water shader's define.</summary>
    public const string Define = "FABLE_WATER_ZENITH_VERTEX";

    /// <summary>Whether the values ask for it; unset is off. Pure.</summary>
    public static bool Wanted(IReadOnlyDictionary<string, string> values) => Engine_RefiiConfig.Bool(values, Key, false);

    /// <summary>The water shader's text with the define. Pure.</summary>
    public static string Compose(string code) => Refii.Fx.Engine_FxDepthReads.Compose(code, Define);

    private static readonly Lazy<bool> Chosen = new(() =>
    {
        if (!Engine_Platform.IsAndroid)
        {
            return false;
        }

        try
        {
            return Wanted(Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            return false;
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly object Gate = new();
    private static Shader? _built;

    /// <summary>
    /// The water shader at <paramref name="path"/>: the file, or with the key on, one shared build
    /// of it with <see cref="Define"/>. Null when the file does not load.
    /// </summary>
    public static Shader? Load(string path)
    {
        Shader? file = GD.Load<Shader>(path);
        if (file is null || !Chosen.Value)
        {
            return file;
        }

        lock (Gate)
        {
            return _built ??= new Shader { Code = Compose(file.Code) };
        }
    }
}
