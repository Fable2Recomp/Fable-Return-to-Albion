using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

namespace Refii.Launcher.Core;

public sealed class RuntimeSettingsModel
{
    public string ConfigPath { get; private set; } = string.Empty;
    public SortedDictionary<string, string> Values { get; private set; } = new(StringComparer.Ordinal);
    public string StatusMessage { get; private set; } = string.Empty;
    public string Filter { get; set; } = string.Empty;

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

    public void Reload()
    {
        StatusMessage = string.Empty;
        ConfigPath = Installer.GetRuntimeConfigPath();

        if (!RuntimeConfig.EnsureDefaults(ConfigPath, out string error))
        {
            StatusMessage = "Failed to prepare refii.toml: " + error;
        }

        Values = RuntimeConfig.ReadValues(ConfigPath);
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All)
        {
            if (!Values.ContainsKey(definition.Name))
            {
                Values[definition.Name] = definition.DefaultValue;
            }
        }
    }

    public void Save()
    {
        if (!RuntimeConfig.WriteValues(ConfigPath, Values, out string error))
        {
            StatusMessage = "Failed to save refii.toml: " + error;
            return;
        }

        StatusMessage = "Saved " + System.IO.Path.GetFileName(ConfigPath);
    }

    public void ResetAllToDefaults()
    {
        Values.Clear();
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All)
        {
            Values[definition.Name] = definition.DefaultValue;
        }

        Save();
    }

    public List<(string Category, List<RuntimeCVarDefinition> Definitions)> BuildGroups()
    {
        string filter = Filter.ToLowerInvariant();
        var order = new List<string>();
        var byCategory = new Dictionary<string, List<RuntimeCVarDefinition>>(StringComparer.Ordinal);

        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All)
        {
            if (!RuntimeConfig.ShouldShowCVar(definition) || !MatchesFilter(definition, filter))
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

        // Developer switches go last, after everything a player tunes.
        return order
            .OrderBy(category => category == RuntimeCVarsGodot.DeveloperCategory ? 1 : 0)
            .Select(category => (category, byCategory[category]))
            .ToList();
    }

    private static string DisplayCategory(RuntimeCVarDefinition definition)
    {
        // The few window keys that outlived their category — see RuntimeConfig.IsDisplayCVar.
        // They are generated entries and cannot be re-categorised at the source, so they are
        // re-homed here instead.
        if (RuntimeConfig.IsDisplayCVar(definition.Name))
        {
            return RuntimeConfig.DisplayCategory;
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
    /// The label a setting is shown under: its refii.toml key as words, so `max_fps` reads
    /// "Max FPS". The key itself is unchanged and still what the file holds.
    /// </summary>
    public static string FormatDisplayName(RuntimeCVarDefinition definition)
    {
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
            definition.Description,
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
