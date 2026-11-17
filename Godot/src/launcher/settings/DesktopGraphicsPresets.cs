using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Launcher.Core;

/// <summary>
/// First-class desktop performance presets (<c>performance_preset</c>) in <c>refii.toml</c>.
/// </summary>
/// <remarks>
/// Controls GPU rendering cost (upscaler, shadows, global illumination, volumetric fog, fixture
/// limits, and LOD distances) as one top-level choice while leaving artistic look
/// (<c>lighting_model</c>, <c>materials_modernized</c>, <c>modern_tone_curve</c>), texture
/// enhancement (<c>texture_upscaling_enabled</c>), HDR output, and bloom untouched.
/// </remarks>
public static class DesktopGraphicsPresets
{
    /// <summary>The <c>refii.toml</c> key storing the selected desktop performance preset.</summary>
    public const string Key = "performance_preset";

    /// <summary>The <c>refii.toml</c> key recording which preset revision wrote the current values.</summary>
    public const string RevisionKey = "performance_preset_revision";

    /// <summary>Current revision of the desktop preset definitions.</summary>
    public const int Revision = 4;

    /// <summary>Preset tuned for entry-level integrated GPUs and battery play.</summary>
    public const string Low = "low";

    /// <summary>Default preset tuned for smooth 60 FPS across modern iGPUs and mainstream PCs.</summary>
    public const string Balanced = "balanced";

    /// <summary>Preset enabling real-time SDFGI, SMAA, and 4096 sun shadows at native resolution.</summary>
    public const string High = "high";

    /// <summary>Maximum-fidelity preset with full-res GI, 8192 4-cascade sun shadows, and cubemap point shadows.</summary>
    public const string Ultra = "ultra";

    /// <summary>Read-only preset state shown when any member setting differs from the named presets.</summary>
    public const string Custom = "custom";

    /// <summary>The default preset applied on first install across desktop systems.</summary>
    public const string DefaultPreset = Balanced;

    /// <summary>
    /// Whether desktop performance presets are offered on this platform (true whenever mobile
    /// <see cref="GraphicsPresets.Offered"/> is false).
    /// </summary>
    public static bool Offered => !GraphicsPresets.Offered;

    /// <summary>Ordered named presets from lowest GPU cost to highest fidelity.</summary>
    public static readonly IReadOnlyList<string> Order = [Low, Balanced, High, Ultra];

    private static readonly IReadOnlyDictionary<string, string> BalancedValues =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["render_scaler"] = "fsr",
            ["fsr_quality"] = "ultra_quality",
            ["render_scale_percent"] = "100",
            ["screen_space_aa"] = "fxaa",
            ["sdfgi_enabled"] = "false",
            ["gi_half_resolution"] = "true",
            ["volumetric_fog"] = "true",
            ["water_quality"] = "balanced",
            ["sun_shadows"] = "true",
            ["sun_shadow_map"] = "2048",
            ["sun_shadow_splits"] = "2",
            ["sun_shadow_distance"] = "150",
            ["tree_shadow_reach"] = "stated",
            ["placed_light_limit"] = "24",
            ["placed_light_shadows"] = "true",
            ["placed_shadow_limit"] = "2",
            ["lighting_preset"] = "balanced",
            ["placed_light_range"] = "65.0",
            ["placed_shadow_range"] = "50.0",
            ["terrain_ao_resolution"] = "4096",
            ["static_shadow_resolution"] = "2048",
            ["texture_orm_height_enabled"] = "true",
            ["anisotropic_filtering"] = "2",
            ["tree_card_lod"] = "true",
            ["mesh_lod_pixels"] = "1.25",
            ["prop_cull_distance_scale"] = "0.85",
            ["entity_draw_distance_scale"] = "1.5",
        };

    private static readonly IReadOnlyDictionary<string, string> LowValues =
        WithOverrides(BalancedValues, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fsr_quality"] = "quality",
            ["volumetric_fog"] = "false",
            ["water_quality"] = "low",
            ["sun_shadow_distance"] = "100",
            ["placed_light_limit"] = "16",
            ["placed_light_shadows"] = "true",
            ["placed_shadow_limit"] = "1",
            ["lighting_preset"] = "performance",
            ["placed_light_range"] = "45.0",
            ["placed_shadow_range"] = "30.0",
            ["terrain_ao_resolution"] = "2048",
            ["static_shadow_resolution"] = "2048",
            ["texture_orm_height_enabled"] = "false",
            ["mesh_lod_pixels"] = "2",
            ["prop_cull_distance_scale"] = "0.65",
            ["entity_draw_distance_scale"] = "1",
        });

    private static readonly IReadOnlyDictionary<string, string> HighValues =
        WithOverrides(BalancedValues, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["render_scaler"] = "bilinear",
            ["fsr_quality"] = "custom",
            ["render_scale_percent"] = "100",
            ["screen_space_aa"] = "smaa",
            ["sdfgi_enabled"] = "true",
            ["gi_half_resolution"] = "true",
            ["volumetric_fog"] = "true",
            ["water_quality"] = "high",
            ["sun_shadow_map"] = "4096",
            ["sun_shadow_splits"] = "2",
            ["sun_shadow_distance"] = "200",
            ["tree_shadow_reach"] = "full",
            ["placed_light_limit"] = "32",
            ["placed_light_shadows"] = "true",
            ["placed_shadow_limit"] = "4",
            ["lighting_preset"] = "balanced",
            ["placed_light_range"] = "65.0",
            ["placed_shadow_range"] = "50.0",
            ["terrain_ao_resolution"] = "4096",
            ["static_shadow_resolution"] = "2048",
            ["texture_orm_height_enabled"] = "true",
            ["anisotropic_filtering"] = "4",
            ["tree_card_lod"] = "false",
            ["mesh_lod_pixels"] = "1",
            ["prop_cull_distance_scale"] = "1",
            ["entity_draw_distance_scale"] = "2",
        });

    private static readonly IReadOnlyDictionary<string, string> UltraValues =
        WithOverrides(HighValues, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gi_half_resolution"] = "false",
            ["water_quality"] = "ultra",
            ["sun_shadow_map"] = "8192",
            ["sun_shadow_splits"] = "4",
            ["sun_shadow_distance"] = "250",
            ["placed_light_limit"] = "0",
            ["placed_shadow_limit"] = "8",
            ["lighting_preset"] = "quality",
            ["placed_light_range"] = "85.0",
            ["placed_shadow_range"] = "70.0",
            ["terrain_ao_resolution"] = "8192",
            ["static_shadow_resolution"] = "4096",
            ["anisotropic_filtering"] = "16",
            ["mesh_lod_pixels"] = "0.75",
            ["prop_cull_distance_scale"] = "1.25",
            ["entity_draw_distance_scale"] = "2.5",
        });

    private static readonly HashSet<string> MemberSet = new(BalancedValues.Keys, StringComparer.Ordinal);

    /// <summary>Every CVar key governed by the desktop performance presets.</summary>
    public static IReadOnlyCollection<string> Members => MemberSet;

    /// <summary>Whether <paramref name="name"/> should be written to <c>refii.toml</c>.</summary>
    public static bool ShouldWrite(string name) => (name != Key && name != RevisionKey) || Offered;

    /// <summary>Returns the human-readable display label for <paramref name="preset"/>.</summary>
    public static string Label(string preset) => preset switch
    {
        Low => "Low (iGPU)",
        Balanced => "Balanced",
        High => "High",
        Ultra => "Ultra",
        _ => "Custom",
    };

    /// <summary>Returns a concise one-line summary describing what <paramref name="preset"/> enables.</summary>
    public static string Summary(string preset) => preset switch
    {
        Low => "FSR Quality (67%), fast static shadows, fog off, 1 cubemap lamp shadow — maximum FPS on integrated GPUs.",
        Balanced => "FSR Ultra Quality (77%), volumetric fog, cubemap lamp shadows (2 lights) — crisp 60 FPS target.",
        High => "Native 100%, real-time SDFGI global illumination, SMAA, 4096 sun & 4 cubemap lamp shadows — recommended for discrete GPUs.",
        Ultra => "Native 100%, full-res SDFGI, 8192 4-cascade sun shadows, 8 cubemap lamp shadows & extended draw reach.",
        _ => "One or more graphics or display scaling settings have been customized individually.",
    };

    /// <summary>Returns the member CVar dictionary for a named <paramref name="preset"/>, or null for <c>custom</c>/unknown.</summary>
    public static IReadOnlyDictionary<string, string>? Values(string preset) => preset switch
    {
        Low => LowValues,
        Balanced => BalancedValues,
        High => HighValues,
        Ultra => UltraValues,
        _ => null,
    };

    /// <summary>Returns the preset name matching <paramref name="values"/>, or <see cref="Custom"/>.</summary>
    public static string Current(IReadOnlyDictionary<string, string> values)
    {
        foreach (string name in Order)
        {
            if (Matches(values, Values(name)!))
            {
                return name;
            }
        }

        return Custom;
    }

    /// <summary>
    /// Writes all settings of <paramref name="preset"/> into <paramref name="values"/> along with
    /// <see cref="Key"/> and <see cref="RevisionKey"/>.
    /// </summary>
    /// <returns>True when <paramref name="preset"/> is one of the four named presets.</returns>
    public static bool Apply(string preset, IDictionary<string, string> values)
    {
        if (Values(preset) is not { } target)
        {
            return false;
        }

        values[Key] = preset;
        values[RevisionKey] = Revision.ToString(CultureInfo.InvariantCulture);
        foreach ((string k, string v) in target)
        {
            values[k] = v;
        }

        return true;
    }

    /// <summary>
    /// Selects the initial desktop preset on first install. Currently returns <see cref="Balanced"/>
    /// across all desktop systems so every new installation starts on a smooth, safe baseline.
    /// </summary>
    public static string SelectInitialDesktopPreset(string adapterName, bool isIntegratedGpu)
    {
        _ = adapterName;
        _ = isIntegratedGpu;
        return DefaultPreset;
    }

    /// <summary>
    /// Applies <see cref="DefaultPreset"/> once when initializing a brand-new desktop configuration
    /// that has not yet recorded <see cref="Key"/>.
    /// </summary>
    public static bool ApplyInitialPresetOnce(IDictionary<string, string> values)
    {
        if (!Offered || values.ContainsKey(Key))
        {
            return false;
        }

        string initial = SelectInitialDesktopPreset(string.Empty, isIntegratedGpu: false);
        if (!Apply(initial, values))
        {
            return false;
        }

        values["placed_light_shadows_world"] = "false";
        values["volumetric_fog"] = Values(initial)!["volumetric_fog"];
        return true;
    }

    /// <summary>
    /// Upgrades a desktop configuration still on a named preset when <see cref="Revision"/> advances,
    /// leaving <see cref="Custom"/> or user-modified configurations untouched.
    /// </summary>
    public static bool Upgrade(IDictionary<string, string> values)
    {
        if (!Offered || !values.TryGetValue(Key, out string? preset))
        {
            return false;
        }

        string currentRev = Revision.ToString(CultureInfo.InvariantCulture);
        if (values.TryGetValue(RevisionKey, out string? storedRev) &&
            int.TryParse(storedRev, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
            parsed >= Revision)
        {
            return false;
        }

        values.Remove("omni_shadow_mode");

        if (preset == Custom || Values(preset) is null)
        {
            values[Key] = Custom;
            values[RevisionKey] = currentRev;
            return true;
        }

        return Apply(preset, values);
    }

    private static bool Matches(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> preset)
    {
        foreach ((string key, string expected) in preset)
        {
            string actual = values.TryGetValue(key, out string? found) && found.Length != 0
                ? found
                : DefaultOf(key);
            if (!SameValue(actual, expected))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool SameValue(string actual, string expected)
    {
        string a = actual.Trim().Trim('"');
        string b = expected.Trim().Trim('"');
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((a is "true" or "false" or "1" or "0" or "on" or "off" or "yes" or "no") &&
            (b is "true" or "false" or "1" or "0" or "on" or "off" or "yes" or "no"))
        {
            return RuntimeConfig.NormalizeBoolValue(a) == RuntimeConfig.NormalizeBoolValue(b);
        }

        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double da) &&
            double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
        {
            return Math.Abs(da - db) < 1e-4;
        }

        return false;
    }

    private static string DefaultOf(string key)
    {
        foreach (RuntimeCVarDefinition def in RuntimeCVarSet.All)
        {
            if (def.Name == key)
            {
                return def.DefaultValue;
            }
        }

        return string.Empty;
    }

    private static IReadOnlyDictionary<string, string> WithOverrides(
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new Dictionary<string, string>(source, StringComparer.Ordinal);
        foreach ((string key, string value) in overrides)
        {
            if (!merged.ContainsKey(key))
            {
                throw new InvalidOperationException($"Unknown desktop preset member key '{key}'.");
            }

            merged[key] = value;
        }

        return merged;
    }
}
