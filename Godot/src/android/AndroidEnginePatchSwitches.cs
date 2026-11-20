using System;
using System.Collections.Generic;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// The run-time switches of godot-patches/0014-0016: refii.toml keys mirrored into the engine's
/// project settings, which the patched Mobile renderer reads each frame. Every default is the stock
/// engine's behaviour; an engine without the patches has none of the settings and is left alone. Every
/// graphics preset turns the two sun-shadow skips and the lamp range skip on (revision 23).
/// </summary>
/// <remarks>
/// Each switch leaves the picture the same: it skips work whose result is already known (see
/// tools/android/godot-patches/README.md). The shader skips (0014) are dynamic branches on scene-data
/// flags, so flipping one compiles no pipeline and suits a live A/B.
/// </remarks>
public static class AndroidEnginePatchSwitches
{
    /// <summary>0014 A1: no sun-shadow sampling past the shadow's fade-out distance.</summary>
    public const string SunShadowBeyondFadeKey = "android_sun_shadow_skip_beyond_fade";

    /// <summary>0014 A2: no sun-shadow sampling on surfaces turned away from the sun (custom light() shaders).</summary>
    public const string SunShadowFacingAwayKey = "android_sun_shadow_skip_facing_away";

    /// <summary>0014 B1: a lamp's maths ends where its range does not reach the pixel.</summary>
    public const string LampOutOfRangeKey = "android_lamp_skip_out_of_range";

    /// <summary>0014 B2: zero-energy or black lamps stay out of the light buffer.</summary>
    public const string LampBufferSkipsUnlitKey = "android_lamp_buffer_skips_unlit";

    /// <summary>0015: front-to-back bands only within this many metres (0: every distance, 0012's order).</summary>
    public const string FrontToBackReachKey = "android_opaque_front_to_back_reach";

    /// <summary>0016: the 3D depth buffer is not written back when nothing after the 3D pass reads it.</summary>
    public const string DepthNotStoredKey = "android_depth_not_stored";

    /// <summary>The largest reach the setting takes, in metres.</summary>
    public const int MaxReach = 1000;

    /// <summary>Each boolean key and the engine setting it drives.</summary>
    public static readonly IReadOnlyList<(string Key, string Setting)> Bools = new[]
    {
        (SunShadowBeyondFadeKey, "rendering/renderer/sun_shadow_skip_beyond_fade_mobile"),
        (SunShadowFacingAwayKey, "rendering/renderer/sun_shadow_skip_facing_away_mobile"),
        (LampOutOfRangeKey, "rendering/renderer/light_skip_out_of_range_mobile"),
        (LampBufferSkipsUnlitKey, "rendering/renderer/light_buffer_skips_unlit_mobile"),
        (DepthNotStoredKey, "rendering/renderer/depth_not_stored_mobile"),
    };

    /// <summary>The engine setting <see cref="FrontToBackReachKey"/> drives (metres, a float).</summary>
    public const string FrontToBackReachSetting = "rendering/renderer/opaque_front_to_back_reach_mobile";

    /// <summary>The engine values the keys ask for, in <see cref="Bools"/> order then the reach. Pure.</summary>
    public static (bool[] Bools, int Reach) Wanted(IReadOnlyDictionary<string, string> values)
    {
        var bools = new bool[Bools.Count];
        for (int i = 0; i < bools.Length; ++i)
        {
            bools[i] = Refii.Core.Engine_RefiiConfig.Bool(values, Bools[i].Key, false);
        }

        return (bools, Math.Clamp(Refii.Core.Engine_RefiiConfig.Int(values, FrontToBackReachKey, 0), 0, MaxReach));
    }

    private static readonly Dictionary<string, object> Applied = new(StringComparer.Ordinal);

    /// <summary>
    /// Mirrors the keys into the engine's settings, each only when it changed and only when the engine
    /// defines it (the patched Android templates). Main thread; live.
    /// </summary>
    public static void Apply(IReadOnlyDictionary<string, string> values)
    {
        (bool[] bools, int reach) = Wanted(values);
        for (int i = 0; i < bools.Length; ++i)
        {
            Set(Bools[i].Setting, bools[i], bools[i]);
        }

        Set(FrontToBackReachSetting, reach, (float)reach);

        // 0017's switch follows this process's restart-only choice, not the live file (AndroidCutoutPrepass).
        AndroidCutoutPrepass.Apply();
    }

    private static void Set(string setting, object wanted, Variant value)
    {
        if (Applied.TryGetValue(setting, out object? last) && last.Equals(wanted))
        {
            return;
        }

        Applied[setting] = wanted;
        if (ProjectSettings.HasSetting(setting))
        {
            ProjectSettings.SetSetting(setting, value);
            GD.Print($"[android] engine {setting} = {value}");
        }
    }
}
