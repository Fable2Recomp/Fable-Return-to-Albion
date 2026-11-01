using System.Linq;

namespace Refii.Launcher.Core;

/// <summary>The phone's graphics preset (<see cref="GraphicsPresets"/>) in the settings model.</summary>
public sealed partial class RuntimeSettingsModel
{
    /// <summary>The graphics preset the current values match, or "custom".</summary>
    public string CurrentGraphicsPreset() => GraphicsPresets.Current(Values);

    /// <summary>Writes a named graphics preset into its settings and saves.</summary>
    public void ApplyGraphicsPreset(string presetName)
    {
        if (GraphicsPresets.Apply(presetName, Values))
        {
            Values["lighting_preset"] = CurrentLightingPreset();
            Save();
        }
    }

    /// <summary>Records which preset the values now match after one of its settings changed.</summary>
    /// <returns>True when the setting belongs to the graphics preset.</returns>
    public bool RefreshGraphicsPreset(string changedSetting)
    {
        if (!GraphicsPresets.Offered || !GraphicsPresets.Members.Contains(changedSetting))
        {
            return false;
        }

        Values[GraphicsPresets.Key] = CurrentGraphicsPreset();
        Save();
        return true;
    }

    /// <summary>
    /// Records which preset the values now match after settings changed outside the panel's own
    /// rows (the lighting dialog saves several preset settings at once). Without it a hand edit
    /// there kept the stored preset's name, and the next preset revision overwrote the edit.
    /// </summary>
    public void RefreshGraphicsPreset()
    {
        if (!GraphicsPresets.Offered)
        {
            return;
        }

        string current = CurrentGraphicsPreset();
        if (Values.TryGetValue(GraphicsPresets.Key, out string? stored) && stored == current)
        {
            return;
        }

        Values[GraphicsPresets.Key] = current;
        Save();
    }

    /// <summary>On a reload, records which preset the values read match; nothing where the presets are not offered.</summary>
    private void ReadGraphicsPreset()
    {
        if (GraphicsPresets.Offered)
        {
            Values[GraphicsPresets.Key] = CurrentGraphicsPreset();
        }
    }

    /// <summary>
    /// A phone's defaults are its Quality preset at 30 FPS: the settings' own defaults are the
    /// desktop picture. Nothing where the presets are not offered.
    /// </summary>
    private void ApplyPhoneDefaults()
    {
        if (GraphicsPresets.Offered)
        {
            GraphicsPresets.Apply(GraphicsPresets.Quality, Values);
            Values["max_fps"] = GraphicsPresets.AndroidFrameCap;
        }
    }
}
