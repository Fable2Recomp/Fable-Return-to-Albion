using System;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// <c>android_no_spot_lights</c>: no spot lights on a phone, neither the stable-count fillers nor placed fixtures.
/// </summary>
/// <remarks>
/// Godot Mobile's light loop always evaluates slot 0 of each light type, so one paired spot makes every pixel
/// of the instance run the material's light(). With no placed spot visible the per-object spot count is
/// "none" everywhere, so pipelines don't change. Omni fixtures and fillers are untouched.
/// </remarks>
public static class AndroidNoSpotLights
{
    /// <summary>refii.toml's key; off by default.</summary>
    public const string Key = "android_no_spot_lights";

    private static bool? _on;

    /// <summary>True on a phone whose refii.toml turns the option on; read once.</summary>
    public static bool Active => _on ??= Engine_Platform.IsAndroid && Read();

    /// <summary>Whether a fixture may be shown: every omni, and a spot only when spots are not suppressed.</summary>
    /// <param name="omni">Whether the fixture is an omni light.</param>
    /// <param name="noSpots">Whether spot lights are suppressed.</param>
    public static bool Shown(bool omni, bool noSpots) => omni || !noSpots;

    private static bool Read()
    {
        try
        {
            return Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), Key, false);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
