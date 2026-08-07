using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
        foreach (RuntimeCVarDefinition definition in RuntimeCVars.Definitions)
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
        foreach (RuntimeCVarDefinition definition in RuntimeCVars.Definitions)
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

        foreach (RuntimeCVarDefinition definition in RuntimeCVars.Definitions)
        {
            if (!RuntimeConfig.ShouldShowCVar(definition, Values) || !MatchesFilter(definition, filter))
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

            if (IsPrimaryBackendSelector(definition))
            {
                bucket.Insert(0, definition);
            }
            else
            {
                bucket.Add(definition);
            }
        }

        MoveCategoryBefore(order, "VFS", "Renderer");
        return order.Select(category => (category, byCategory[category])).ToList();
    }

    private string DisplayCategory(RuntimeCVarDefinition definition)
    {
        bool usesPlume = GetValueOrDefault("renderer_backend", "xenia").ToLowerInvariant() == "plume";
        if (usesPlume && definition.Category == "GPU" && IsSharedRendererResolutionCVar(definition.Name))
        {
            return "Renderer";
        }

        return definition.Category;
    }

    private static void MoveCategoryBefore(List<string> order, string category, string before)
    {
        int from = order.IndexOf(category);
        int to = order.IndexOf(before);
        if (from < 0 || to < 0 || from < to)
        {
            return;
        }

        order.RemoveAt(from);
        order.Insert(order.IndexOf(before), category);
    }

    private static bool MatchesFilter(RuntimeCVarDefinition definition, string filter)
    {
        if (filter.Length == 0)
        {
            return true;
        }

        string haystack = (definition.Name + " " + definition.Category + " " + definition.Description)
            .ToLowerInvariant();
        return haystack.Contains(filter, StringComparison.Ordinal);
    }

    private static bool IsSharedRendererResolutionCVar(string name) =>
        name is "resolution" or "video_mode_width" or "video_mode_height";

    private static bool IsPrimaryBackendSelector(RuntimeCVarDefinition definition) =>
        definition.Name is "renderer_backend" or "vfs_loose_asset_backend";

    public static string FormatDisplayName(RuntimeCVarDefinition definition)
    {
        string name = definition.Name;
        switch (definition.Category)
        {
            case "UI/Window":
                name = StripPrefix(name, "window");
                break;
            case "UI/Presenter":
                name = StripPrefix(name, "host");
                name = StripPrefix(name, "present");
                break;
            case "GPU":
                name = StripPrefix(name, "video_mode");
                break;
            case "GPU/Vulkan":
            case "UI/Vulkan":
                name = StripPrefix(name, "vulkan");
                break;
            case "GPU/D3D12":
            case "UI/D3D12":
                name = StripPrefix(name, "d3d12");
                break;
        }

        return name;
    }

    private static string StripPrefix(string name, string prefix)
    {
        string withUnderscore = prefix + "_";
        return name.StartsWith(withUnderscore, StringComparison.Ordinal) && name.Length > withUnderscore.Length
            ? name[withUnderscore.Length..]
            : name;
    }

    public static List<string> ParseAllowedValues(string? allowedValues)
    {
        var options = new List<string>();
        if (string.IsNullOrEmpty(allowedValues))
        {
            return options;
        }

        foreach (string token in allowedValues!.Split(','))
        {
            string trimmed = token.Trim();
            if (trimmed.Length != 0)
            {
                options.Add(trimmed);
            }
        }

        return options;
    }

    public bool IsDisabledVideoModeSetting(RuntimeCVarDefinition definition)
    {
        if (!definition.Name.StartsWith("video_mode_", StringComparison.Ordinal) ||
            definition.Name == "video_mode_refresh_rate")
        {
            return false;
        }

        string resolution = GetValueOrDefault("resolution", string.Empty);
        bool isCustom = resolution.Length == 0 || resolution == "custom";
        return !isCustom;
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
