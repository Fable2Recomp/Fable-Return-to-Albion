using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Refii.Core;
using Refii.Launcher.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// The Options panel's frame cap, graphics preset and vibration: each tap steps one setting and
/// writes <c>refii.toml</c>, the same file the launcher's settings edit.
/// </summary>
/// <remarks>
/// LIVE. The host rereads the cap, render scale and mesh LOD from the file
/// (AndroidGameHost.RefreshRenderScale), and <see cref="ApplyPreset"/> hands the rest to
/// <see cref="Engine_RefiiConfig.TrySetGodotCvar"/>, whose listeners reload the placed lights,
/// the sun shadow's reach (Balanced and Quality cast the sun, the two Performance presets do not) and the
/// props' draw ranges. Vibration is the launcher's <c>controller_vibration</c>, the one switch
/// for the host's rumble whether it reaches a controller or, played by touch, the phone;
/// GameInput rereads it.
/// </remarks>
public static class AndroidQuickSettings
{
    /// <summary>The caps a tap steps through, in order; 0 is uncapped.</summary>
    /// <remarks>Each divides a 60 Hz panel's refresh. 20 is for a phone that cannot hold 30: it
    /// shows every frame for three refreshes, where a missed 30 alternates two and three.</remarks>
    public static readonly int[] FrameCaps = { 20, 30, 60, 0 };

    /// <summary>The presets a tap steps through, in order.</summary>
    public static readonly string[] Presets = { GraphicsPresets.UltraPerformance, GraphicsPresets.Performance, GraphicsPresets.Balanced, GraphicsPresets.Quality };

    /// <summary>The cap after <paramref name="current"/>; one not in the list steps to the first.</summary>
    public static int NextFrameCap(int current)
    {
        int at = Array.IndexOf(FrameCaps, Math.Max(0, current));
        return FrameCaps[(at + 1) % FrameCaps.Length];
    }

    /// <summary>The preset after <paramref name="current"/>; Custom steps to the first.</summary>
    public static string NextPreset(string current)
    {
        int at = Array.FindIndex(Presets, p => p.Equals(current, StringComparison.OrdinalIgnoreCase));
        return Presets[(at + 1) % Presets.Length];
    }

    /// <summary>The cap's button text.</summary>
    public static string FrameCapLabel(int cap) => cap <= 0 ? "FPS: Uncapped" : $"FPS: {cap}";

    /// <summary>The preset's button text.</summary>
    public static string PresetLabel(string preset) => "Preset: " + GraphicsPresets.Label(preset);

    /// <summary>The configured cap; absent is uncapped.</summary>
    public static int FrameCap(string config) =>
        Math.Max(0, Engine_RefiiConfig.Int(RuntimeConfig.ReadValues(config), "max_fps", 0));

    /// <summary>The preset the configured settings make.</summary>
    public static string Preset(string config) => GraphicsPresets.Current(RuntimeConfig.ReadValues(config));

    /// <summary>The rumble switch's key, shared with the launcher's Controls settings.</summary>
    public const string VibrationKey = "controller_vibration";

    /// <summary>The vibration button's text.</summary>
    public static string VibrationLabel(bool on) => on ? "Vibration: On" : "Vibration: Off";

    /// <summary>Whether rumble is on; absent is on, as the launcher's default.</summary>
    public static bool Vibration(string config) =>
        Engine_RefiiConfig.Bool(RuntimeConfig.ReadValues(config), VibrationKey, true);

    /// <summary>Writes <paramref name="on"/> as <c>controller_vibration</c>.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void SetVibration(string config, bool on)
    {
        SortedDictionary<string, string> values = RuntimeConfig.ReadValues(config);
        values[VibrationKey] = on ? "true" : "false";
        Write(config, values);
    }

    /// <summary>Writes <paramref name="cap"/> as <c>max_fps</c>.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void SetFrameCap(string config, int cap)
    {
        SortedDictionary<string, string> values = RuntimeConfig.ReadValues(config);
        values["max_fps"] = Math.Max(0, cap).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Write(config, values);
    }

    /// <summary>Writes <paramref name="preset"/>'s settings and passes each to <paramref name="live"/>.</summary>
    /// <param name="config">The runtime configuration file.</param>
    /// <param name="preset">A preset name from <see cref="GraphicsPresets"/>.</param>
    /// <param name="live">Receives each written setting for the running game, as name and value.</param>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void ApplyPreset(string config, string preset, Action<string, string> live)
    {
        SortedDictionary<string, string> values = RuntimeConfig.ReadValues(config);
        if (GraphicsPresets.Values(preset) is not { } chosen || !GraphicsPresets.Apply(preset, values))
        {
            throw new ArgumentException("Unknown graphics preset: " + preset, nameof(preset));
        }

        Write(config, values);
        foreach ((string key, string value) in chosen)
        {
            live(key, value);
        }
    }

    /// <summary>A setting's configured value, unquoted; absent is the setting's default.</summary>
    public static string Value(string config, string key) =>
        RuntimeConfig.ReadValues(config).TryGetValue(key, out string? value)
            ? value.Trim().Trim('"')
            : RuntimeCVarSet.All.FirstOrDefault(definition => definition.Name == key)?.DefaultValue ?? "";

    /// <summary>
    /// Writes one graphics setting and passes it to <paramref name="live"/>. The stored preset becomes the one
    /// the settings now make, "custom" when they match none, as a change in the launcher stores it.
    /// </summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void SetGraphics(string config, string key, string value, Action<string, string> live)
    {
        SortedDictionary<string, string> values = RuntimeConfig.ReadValues(config);
        values[key] = value;
        values[GraphicsPresets.Key] = GraphicsPresets.Current(values);
        Write(config, values);
        live(key, value);
    }

    /// <summary>The touch controls' opacity key, which they reread every couple of seconds.</summary>
    public const string TouchOpacityKey = "touch_opacity";

    /// <summary>The touch controls' opacity, 0.1 to 1; absent is the launcher's 0.5.</summary>
    public static float TouchOpacity(string config) =>
        Math.Clamp(Engine_RefiiConfig.Float(RuntimeConfig.ReadValues(config), TouchOpacityKey, 0.5f), 0.1f, 1f);

    /// <summary>Writes the touch controls' opacity, stepped to tenths and kept within 0.1 to 1.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void SetTouchOpacity(string config, float opacity)
    {
        SortedDictionary<string, string> values = RuntimeConfig.ReadValues(config);
        values[TouchOpacityKey] = Math.Clamp(MathF.Round(opacity, 1), 0.1f, 1f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        Write(config, values);
    }

    private static void Write(string config, IReadOnlyDictionary<string, string> values)
    {
        if (!RuntimeConfig.WriteValues(config, values, out string error))
        {
            throw new IOException("Cannot write " + config + ": " + error);
        }
    }
}
