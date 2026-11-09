using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace Refii.Launcher.Core;

public sealed partial class RuntimeSettingsModel
{
    public string ConfigPath { get; private set; } = string.Empty;
    public SortedDictionary<string, string> Values { get; private set; } = new(StringComparer.Ordinal);
    public string StatusMessage { get; private set; } = string.Empty;
    public string Filter { get; set; } = string.Empty;

    private const string PanelSection = "Settings";
    private const string ShowAdvancedKey = "ShowAdvanced";

    /// <summary>
    /// Whether the tuning dials are listed. Off, the panel shows the decisions a player makes
    /// — the look, the display, the controls — and the remaster's dials, the developer switches
    /// and the runtime's own knobs stay out of sight; a non-empty filter searches everything.
    /// </summary>
    public bool ShowAdvanced
    {
        get => Ini.GetBool(ShowAdvancedKey, false, PanelSection);
        set
        {
            Ini.SetBool(ShowAdvancedKey, value, PanelSection);
            Ini.Save();
        }
    }

    /// <summary>
    /// A setting that tunes rather than decides. The remaster's lighting and texture dials, the
    /// developer switches, the picture's internals, and every runtime cvar that is not a key
    /// binding. The two decisions a look is made of stay in the plain view.
    /// </summary>
    public static bool IsAdvanced(RuntimeCVarDefinition definition)
    {
        if (Refii.AndroidRuntime.AndroidLauncherSettings.Advanced(definition.Name) is bool phone) return phone;
        if (definition.Name is "lighting_model" or "materials_modernized" or "texture_upscaling_enabled"
            or "lighting_preset" or "hdr_output" or "bloom_scale" or GraphicsPresets.Key)
        {
            return false;
        }
        // fullscreen, monitor and vsync are runtime-side entries re-homed under Display; their
        // registry category ("UI/Window") would otherwise count them as advanced.
        if (RuntimeConfig.IsDisplayCVar(definition.Name))
        {
            return false;
        }
        if (definition.Category is RuntimeCVarsGodot.DeveloperCategory
            or RuntimeCVarsGodot.LightingCategory or RuntimeCVarsGodot.Category)
        {
            return true;
        }
        if (definition.Name is "renderdoc" or "renderdoc_path" or "gc_heaps" or "perf_overlay"
            or "gpu_profile" or "render_thread" or "android_render_thread" or "draw_latency_ticks" or "mesh_lod_pixels"
            or "prop_cull_min_pixels" or "prop_fade_fraction" or "texture_mipmap_bias"
            or "cull_back_faces" or "video_skip_after" or "debanding" or "occlusion_culling")
        {
            return true;
        }
        // The runtime's own registry, apart from what a player sets: the bindings, and the
        // RexNet settings, which are the multiplayer options (user, 2026-10-11).
        bool godotSide = RuntimeCVarsGodot.Definitions.Contains(definition);
        return !godotSide
            && !definition.Category.StartsWith("Input", StringComparison.Ordinal)
            && definition.Category != "RexNet";
    }

    /// <summary>
    /// The look the switches add up to: "modern_fable", "modern_filmic", "classic", or "custom" when mixed.
    /// </summary>
    public string CurrentLook()
    {
        string lighting = Values.TryGetValue("lighting_model", out string? model) ? model.Trim().Trim('"') : "modern";
        bool classicLighting = lighting.Equals("classic", StringComparison.OrdinalIgnoreCase)
            || lighting.Equals("fable", StringComparison.OrdinalIgnoreCase);
        bool modernized = !Values.TryGetValue("materials_modernized", out string? materials)
            || RuntimeConfig.NormalizeBoolValue(materials) == "true";
        if (!classicLighting && modernized)
        {
            string curve = Values.TryGetValue("modern_tone_curve", out string? tc)
                ? tc.Trim().Trim('"').ToLowerInvariant()
                : "fable";
            return curve switch
            {
                "" or "fable" => "modern_fable",
                "filmic" or "all" => "modern_filmic",
                _ => "custom",
            };
        }
        if (classicLighting && !modernized)
        {
            return "classic";
        }
        return "custom";
    }

    /// <summary>
    /// Sets the look as one decision: the lighting model, materials switch, and modern tone curve
    /// together, since "the original game" or a modern tonemapper choice is a single top-level preset.
    /// </summary>
    public void ApplyLook(string look)
    {
        bool classic = look == "classic";
        Values["lighting_model"] = classic ? "classic" : "modern";
        Values["materials_modernized"] = classic ? "false" : "true";
        if (!classic)
        {
            Values["modern_tone_curve"] = look == "modern_filmic" ? "all" : "fable";
        }
        Save();
    }

    public sealed record LightingPreset(
        double LightRange,
        double ShadowRange);

    public static readonly IReadOnlyDictionary<string, LightingPreset> Presets =
        new Dictionary<string, LightingPreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["performance"] = new(45.0, 30.0),
            ["balanced"] = new(65.0, 50.0),
            ["quality"] = new(85.0, 70.0),
        };

    /// <summary>The lighting preset the current range values match, or "custom".</summary>
    public string CurrentLightingPreset()
    {
        double lightRange = ParseDouble(GetValueOrDefault("placed_light_range", "65.0"), "65.0");
        double shadowRange = ParseDouble(GetValueOrDefault("placed_shadow_range", "50.0"), "50.0");

        foreach ((string name, LightingPreset preset) in Presets)
        {
            if (Math.Abs(lightRange - preset.LightRange) < 1.0 &&
                Math.Abs(shadowRange - preset.ShadowRange) < 1.0)
            {
                return name;
            }
        }

        return "custom";
    }

    /// <summary>Applies a named preset to individual lighting and shadow range CVars.</summary>
    public void ApplyLightingPreset(string presetName)
    {
        if (Presets.TryGetValue(presetName, out LightingPreset? preset))
        {
            Values["lighting_preset"] = presetName.ToLowerInvariant();
            Values["placed_light_range"] = preset.LightRange.ToString("0.0", CultureInfo.InvariantCulture);
            Values["placed_shadow_range"] = preset.ShadowRange.ToString("0.0", CultureInfo.InvariantCulture);
            Save();
        }
    }

    private bool _initialized;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Reload();
    }

    /// <summary>
    /// Reads the ACTIVE title's config afresh: the launcher calls this when the title switches, so
    /// the panel edits that game's file and lists that game's settings.
    /// </summary>
    public void Reload()
    {
        StatusMessage = string.Empty;
        ConfigPath = Installer.GetRuntimeConfigPath();
        // The mods' Controls rows follow the enabled mods' input.json: read again on every reload.
        RuntimeCVarSet.Refresh();

        if (!RuntimeConfig.EnsureDefaults(ConfigPath, out string error))
        {
            StatusMessage = "Failed to prepare " + System.IO.Path.GetFileName(ConfigPath) + ": " + error;
        }

        Values = RuntimeConfig.ReadValues(ConfigPath);
        Refii.Core.Engine_Switches.Invalidate();
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.Active)
        {
            if (!Values.ContainsKey(definition.Name))
            {
                Values[definition.Name] = definition.DefaultValue;
            }
        }
        if (Values.TryGetValue("placed_light_range", out string? lrStr) &&
            double.TryParse(lrStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedLr) &&
            parsedLr <= 8.0)
        {
            Values["placed_light_range"] = (parsedLr <= 1.1 ? 40.0 : parsedLr >= 1.9 ? 80.0 : 60.0).ToString("0.0", CultureInfo.InvariantCulture);
        }
        Values["lighting_preset"] = CurrentLightingPreset();
        ReadGraphicsPreset();  // the phone's, RuntimeSettingsModel.GraphicsPreset.cs
    }

    public void Save()
    {
        if (!RuntimeConfig.WriteValues(ConfigPath, Values, out string error))
        {
            StatusMessage = "Failed to save " + System.IO.Path.GetFileName(ConfigPath) + ": " + error;
            return;
        }

        StatusMessage = "Saved " + System.IO.Path.GetFileName(ConfigPath);
        Refii.Core.Engine_Switches.Invalidate();
    }

    public void ResetAllToDefaults()
    {
        Values.Clear();
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.Active)
        {
            Values[definition.Name] = definition.DefaultValue;
        }

        ApplyPhoneDefaults();
        Values["lighting_preset"] = CurrentLightingPreset();
        Save();
    }

    public List<(string Category, List<RuntimeCVarDefinition> Definitions)> BuildGroups()
    {
        string filter = Filter.ToLowerInvariant();
        var order = new List<string>();
        var byCategory = new Dictionary<string, List<RuntimeCVarDefinition>>(StringComparer.Ordinal);

        bool showAdvanced = ShowAdvanced || filter.Length != 0;
        // The active title's settings only; a category none of them fills is not listed.
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.Active)
        {
            if (!RuntimeConfig.ShouldShowCVar(definition) || !MatchesFilter(definition, filter))
            {
                continue;
            }

            // All advanced lighting settings live in the LightingSettingsDialog
            // accessed via the "Advanced..." button under Lighting Preset.
            if (definition.Category == RuntimeCVarsGodot.LightingCategory &&
                definition.Name is not ("lighting_preset" or "hdr_output" or "bloom_scale"))
            {
                continue;
            }

            if (!showAdvanced && IsAdvanced(definition))
            {
                continue;
            }

            string displayCategory = DisplayCategory(definition);
            if (!byCategory.TryGetValue(displayCategory, out List<RuntimeCVarDefinition>? bucket))
            {
                bucket = new List<RuntimeCVarDefinition>();
                byCategory[displayCategory] = bucket;
                order.Add(displayCategory);
            }

            bucket.Add(definition);
        }

        // The picture first, then the game's services, then whatever the runtime adds, and
        // the developer switches last. Definition order alone put Multiplayer second, because
        // the runtime's registry is listed before Godot's.
        return order
            .OrderBy(GroupRank)
            .Select(category => (category, byCategory[category]))
            .ToList();
    }

    private static int GroupRank(string category) => category switch
    {
        RuntimeConfig.DisplayCategory => 0,
        RuntimeCVarsGodot.LightingCategory => 1,
        RuntimeCVarsGodot.VideoCategory => 2,
        RuntimeCVarsGodot.ControlsCategory => 3,
        // A mod's Controls group ("Controls: <title>") sits with the Controls group, after it.
        _ when category.StartsWith(RuntimeCVarsGodot.ControlsCategory + ": ", StringComparison.Ordinal) => 3,
        RuntimeCVarsGodot.Category => 4,
        MultiplayerCategory => 5,
        RuntimeCVarsGodot.PresenceCategory => 6,
        RuntimeCVarsGodot.DeveloperCategory => 9,
        _ => 7,
    };

    /// <summary>The panel's name for the runtime's RexNet group: what it is to a player.</summary>
    private const string MultiplayerCategory = RuntimeCVarsGodot.MultiplayerCategory;

    private static string DisplayCategory(RuntimeCVarDefinition definition)
    {
        // The few window keys that outlived their category — see RuntimeConfig.IsDisplayCVar.
        // They are generated entries and cannot be re-categorised at the source, so they are
        // re-homed here instead.
        if (RuntimeConfig.IsDisplayCVar(definition.Name))
        {
            return RuntimeConfig.DisplayCategory;
        }
        if (definition.Category == "RexNet")
        {
            return MultiplayerCategory;
        }

        return definition.Category;
    }

    private static bool MatchesFilter(RuntimeCVarDefinition definition, string filter)
    {
        if (filter.Length == 0)
        {
            return true;
        }

        string haystack = (definition.Name + " " + FormatDisplayName(definition) + " "
                + definition.Category + " " + definition.Description)
            .ToLowerInvariant();
        return haystack.Contains(filter, StringComparison.Ordinal);
    }

    /// <summary>
    /// The label a setting is shown under: its config key as words, so `max_fps` reads
    /// "Max FPS". The key itself is unchanged and still what the file holds.
    /// </summary>
    public static string FormatDisplayName(RuntimeCVarDefinition definition)
    {
        if (AndroidSettingNames.Name(definition.Name) is { } phone) return phone;
        string[] words = definition.Name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (DisplayWords.TryGetValue(words[i], out string? spelled))
            {
                words[i] = spelled;
            }
            else if (i == 0)
            {
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
            }
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// A setting's description with every other setting it names spelled the way the panel
    /// labels it, so "Needs texture_orm_enabled" reads "Needs Texture ORM enabled".
    /// </summary>
    public static string FormatDescription(RuntimeCVarDefinition definition) =>
        System.Text.RegularExpressions.Regex.Replace(
            AndroidSettingNames.About(definition.Name) ?? definition.Description,
            @"`?\b[a-z0-9]+(?:_[a-z0-9]+)+\b`?",
            match =>
            {
                string key = match.Value.Trim('`');
                RuntimeCVarDefinition? named = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == key);
                return named is null ? match.Value : FormatDisplayName(named);
            });

    /// <summary>Words in setting keys that are acronyms or names rather than plain English.</summary>
    private static readonly Dictionary<string, string> DisplayWords = new(StringComparer.Ordinal)
    {
        ["aa"] = "AA",
        ["ao"] = "AO",
        ["cl"] = "CL",
        ["csv"] = "CSV",
        ["fps"] = "FPS",
        ["fsr"] = "FSR",
        ["fx"] = "FX",
        ["gc"] = "GC",
        ["gi"] = "GI",
        ["gpu"] = "GPU",
        ["hdr"] = "HDR",
        ["io"] = "IO",
        ["lod"] = "LOD",
        ["lua"] = "Lua",
        ["mb"] = "MB",
        ["ms"] = "ms",
        ["msaa"] = "MSAA",
        ["orm"] = "ORM",
        ["postfx"] = "Post-FX",
        ["renderdoc"] = "RenderDoc",
        ["rexcrt"] = "RexCRT",
        ["rexnet"] = "RexNet",
        ["taa"] = "TAA",
        ["vfs"] = "VFS",
        ["vsync"] = "VSync",
        ["xam"] = "XAM",
    };

    /// <summary>
    /// The resolutions offered for each aspect ratio. Ordinary desktop sizes rather than a
    /// computed sweep: a generated list produces oddities nobody's monitor has, and the point
    /// of the pair of dropdowns is that a player recognises their own resolution in it.
    /// </summary>
    private static readonly Dictionary<string, string[]> ResolutionsByAspect =
        new(StringComparer.Ordinal)
        {
            ["16:9"] = new[]
            {
                "1280x720", "1366x768", "1600x900", "1920x1080",
                "2560x1440", "3200x1800", "3840x2160",
            },
            ["16:10"] = new[]
            {
                "1280x800", "1440x900", "1680x1050", "1920x1200", "2560x1600", "3840x2400",
            },
            ["4:3"] = new[]
            {
                "1024x768", "1280x960", "1400x1050", "1600x1200", "2048x1536",
            },
            ["21:9"] = new[]
            {
                "2560x1080", "3440x1440", "3840x1600", "5120x2160",
            },
            ["32:9"] = new[]
            {
                "3840x1080", "5120x1440",
            },
        };

    /// <summary>
    /// The options a setting offers RIGHT NOW. Static <c>AllowedValues</c> cannot express a list
    /// that depends on another setting, and the resolution list depends on the chosen aspect —
    /// so that one pair is answered here and everything else falls through unchanged. A
    /// non-empty answer makes the setting a dropdown whatever its type.
    /// </summary>
    public static List<RuntimeCVarOption> OptionsFor(
        RuntimeCVarDefinition definition, IReadOnlyDictionary<string, string> values)
    {
        if (IsLockedRenderingDriver(definition))
        {
            return new List<RuntimeCVarOption> { new("vulkan", "Vulkan") };
        }

        if (definition.Name != "display_resolution")
        {
            return ParseAllowedValues(definition.AllowedValues);
        }

        string aspect = values.TryGetValue("display_aspect", out string? chosen) && chosen.Length != 0
            ? chosen
            : "16:9";

        string[] candidates = ResolutionsByAspect.TryGetValue(aspect, out string[]? sizes)
            ? sizes
            : ResolutionsByAspect["16:9"];

        return FitToMonitor(candidates, aspect, values)
            .Select(size => new RuntimeCVarOption(size, size))
            .ToList();
    }

    /// <summary>
    /// Direct3D 12 exists only on Windows, so elsewhere the driver is Vulkan and the dropdown
    /// offers nothing else and cannot be changed.
    /// </summary>
    public static bool IsLockedRenderingDriver(RuntimeCVarDefinition definition) =>
        definition.Name == "rendering_driver" && !OperatingSystem.IsWindows();

    /// <summary>
    /// Narrows a list to what the chosen monitor can actually show, and adds that monitor's own
    /// resolution when the aspect matches.
    /// </summary>
    /// <remarks>
    /// GODOT CANNOT ENUMERATE MODES. `DisplayServer` reports a screen's CURRENT size, refresh
    /// rate and usable rect — there is no "every mode this output supports" call of the kind
    /// SDL has. So this cannot be a true mode list, and pretending otherwise would be worse
    /// than the fixed table: it would look authoritative and be invented.
    ///
    /// What it can honestly do is drop sizes that do not fit — offering 4K on a 1080p panel is
    /// the thing that makes a list feel machine-generated — and make sure the panel's own
    /// resolution is present, since that is the one entry a player is certain to look for.
    ///
    /// It never returns empty: a monitor smaller than every listed size still gets the smallest,
    /// because a dropdown with nothing in it cannot be corrected from the UI.
    /// </remarks>
    private static List<string> FitToMonitor(
        string[] candidates, string aspect, IReadOnlyDictionary<string, string> values)
    {
        Vector2I screen;
        try
        {
            int monitor = 0;
            if (values.TryGetValue("monitor", out string? chosen) &&
                int.TryParse(chosen, out int index) && index >= 0 &&
                index < DisplayServer.GetScreenCount())
            {
                monitor = index;
            }

            screen = DisplayServer.ScreenGetSize(monitor);
        }
        catch (Exception)
        {
            // Headless, or a display server that will not answer: keep the whole list rather
            // than filtering against a size we do not have.
            return new List<string>(candidates);
        }

        if (screen.X <= 0 || screen.Y <= 0)
        {
            return new List<string>(candidates);
        }

        // ASK THE PLATFORM FIRST. `DisplayModes` enumerates what this output can actually do —
        // Win32 on Windows, the X server on Linux — and returns nothing when it cannot say
        // (Wayland, headless). Real modes beat the curated table outright: this monitor offers
        // 1280x1024 and 1152x864, which the table lacks, and does NOT offer 1366x768 or 4K,
        // which the table would have shown.
        IReadOnlyList<Vector2I> reported = DisplayModes.ForScreen(screen);
        if (reported.Count != 0)
        {
            var enumerated = reported
                .Where(size => MatchesAspect(size, aspect))
                .Select(size => $"{size.X}x{size.Y}")
                .ToList();

            // Only when the aspect has something in it. An aspect no mode matches falls through
            // to the curated list rather than showing an empty dropdown.
            if (enumerated.Count != 0)
            {
                enumerated.Reverse();
                return enumerated;
            }
        }

        var fitted = candidates
            .Where(size => Parse(size) is { } wh && wh.X <= screen.X && wh.Y <= screen.Y)
            .ToList();

        // The monitor's own size, when it belongs to the aspect being shown.
        string native = $"{screen.X}x{screen.Y}";
        if (!fitted.Contains(native, StringComparer.Ordinal) && MatchesAspect(screen, aspect))
        {
            fitted.Add(native);
            fitted.Sort((a, b) => (Parse(a)?.X ?? 0).CompareTo(Parse(b)?.X ?? 0));
        }

        return fitted.Count != 0 ? fitted : new List<string> { candidates[0] };
    }

    /// <summary>"1920x1080" to a size, or null when it is not one.</summary>
    private static Vector2I? Parse(string size)
    {
        int cross = size.IndexOf('x');
        return cross > 0 &&
            int.TryParse(size[..cross], out int width) &&
            int.TryParse(size[(cross + 1)..], out int height)
            ? new Vector2I(width, height)
            : null;
    }

    /// <summary>
    /// Whether a size is the given aspect, within a percent. Real panels are not exact — 1366x768
    /// is sold as 16:9 and is 16:8.99 — so an equality test would reject the very sizes players
    /// have.
    /// </summary>
    private static bool MatchesAspect(Vector2I size, string aspect)
    {
        int colon = aspect.IndexOf(':');
        if (colon <= 0 ||
            !int.TryParse(aspect[..colon], out int wide) ||
            !int.TryParse(aspect[(colon + 1)..], out int high) ||
            high == 0 || size.Y == 0)
        {
            return false;
        }

        double want = (double)wide / high;
        double have = (double)size.X / size.Y;
        return Math.Abs(have - want) / want <= 0.01;
    }

    /// <summary>
    /// An allowed-values list is comma separated. An entry is a bare value, shown as itself, or
    /// <c>value|Label</c>, which shows the label and writes the value — how an Int that names its
    /// modes becomes a readable dropdown. The separator is a pipe because values carry colons
    /// ("16:9").
    /// </summary>
    public static List<RuntimeCVarOption> ParseAllowedValues(string? allowedValues)
    {
        var options = new List<RuntimeCVarOption>();
        if (string.IsNullOrEmpty(allowedValues))
        {
            return options;
        }

        foreach (string token in allowedValues!.Split(','))
        {
            string trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            int bar = trimmed.IndexOf('|');
            options.Add(bar < 0
                ? new RuntimeCVarOption(trimmed, trimmed)
                : new RuntimeCVarOption(trimmed[..bar].Trim(), trimmed[(bar + 1)..].Trim()));
        }

        return options;
    }

    public string GetValueOrDefault(string key, string fallback) =>
        Values.TryGetValue(key, out string? value) && value.Length != 0 ? value : fallback;

    public static int ParseInt(string value, string fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : int.TryParse(fallback, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fallbackValue)
                ? fallbackValue
                : 0;

    public static double ParseDouble(string value, string fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : double.TryParse(fallback, NumberStyles.Float, CultureInfo.InvariantCulture, out double fallbackValue)
                ? fallbackValue
                : 0.0;
}
