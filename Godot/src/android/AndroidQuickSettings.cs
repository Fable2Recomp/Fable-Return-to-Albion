using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Refii.Core;
using Refii.Launcher.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// The Options panel's frame cap, graphics preset, vibration and launcher skip: each tap sets or steps one setting and
/// writes <c>refii.toml</c>, the same file the launcher's settings edit.
/// </summary>
/// <remarks>
/// LIVE. The host rereads the cap, render scale and mesh LOD from the file
/// (AndroidGameHost.RefreshRenderScale), and <see cref="ApplyPreset"/> hands the rest to
/// <see cref="Engine_RefiiConfig.TrySetGodotCvar"/>, whose listeners reload the placed lights,
/// the sun shadow's reach (High and Ultra cast the sun, Low and Medium do not) and the
/// props' draw ranges. Vibration is the launcher's <c>controller_vibration</c>, the one switch
/// for the host's rumble whether it reaches a controller or, played by touch, the phone;
/// GameInput rereads it.
/// </remarks>
public static class AndroidQuickSettings
{
    /// <summary>The caps the Frame limit row offers, a button each, left to right; 0 is uncapped.</summary>
    /// <remarks>20, 30 and 60 divide a 60 Hz panel's refresh, and 40 an 80 or 120 Hz panel's (a Galaxy
    /// S25+ runs its panel at 80 Hz for it); on a 60 Hz panel 40 alternates one and two refreshes. 20 is for a phone that cannot
    /// hold 30: it shows every frame for three refreshes, where a missed 30 alternates two and three.</remarks>
    public static readonly int[] FrameCaps = { 0, 20, 30, 40, 60 };

    /// <summary>The presets Options offers, in order.</summary>
    public static readonly string[] Presets =
        { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra };

    /// <summary>The Frame limit row's name: the cap is the button that stands pressed, unless it was set by
    /// hand to one the row doesn't offer, which the name then states.</summary>
    /// <remarks>"Frame limit", not "FPS": beside "FPS counter" the two read as one setting.</remarks>
    public static string FrameCapLabel(int cap) =>
        Array.IndexOf(FrameCaps, Math.Max(0, cap)) >= 0 ? "Frame limit" : $"Frame limit: {cap} FPS";

    /// <summary>A cap's button text in the Frame limit row: the number, or the infinity sign for uncapped.</summary>
    public static string FrameCapChoiceLabel(int cap) =>
        cap <= 0 ? "\u221E" : cap.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The preset's button text.</summary>
    public static string PresetLabel(string preset) => "Preset: " + GraphicsPresets.Label(preset);

    /// <summary>The configured cap; absent is uncapped.</summary>
    public static int FrameCap(string config) =>
        Math.Max(0, Engine_RefiiConfig.Int(RuntimeConfig.ReadValues(config), "max_fps", 0));

    /// <summary>The preset the configured settings make.</summary>
    public static string Preset(string config) => GraphicsPresets.Current(RuntimeConfig.ReadValues(config));

    /// <summary>The rumble switch's key, shared with the launcher's Controls settings.</summary>
    public const string VibrationKey = "controller_vibration";

    /// <summary>The FPS counter button's text.</summary>
    public static string FpsCounterLabel(bool on) => on ? "FPS counter: On" : "FPS counter: Off";

    /// <summary>Whether the frame rate shows in the corner (<see cref="AndroidFpsCounter"/>); absent is off.</summary>
    public static bool FpsCounter(string config) =>
        Engine_RefiiConfig.Bool(RuntimeConfig.ReadValues(config), AndroidFpsCounter.Key, false);

    /// <summary>Writes <paramref name="on"/> as <c>fps_counter</c>.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void SetFpsCounter(string config, bool on)
    {
        SortedDictionary<string, string> values = RuntimeConfig.ReadValues(config);
        values[AndroidFpsCounter.Key] = on ? "true" : "false";
        Write(config, values);
    }

    /// <summary>Whether the app goes straight to the game, past the launcher (LauncherApp.SkipLauncher).</summary>
    public const string SkipLauncherKey = "android_skip_launcher";

    /// <summary>The skip-launcher button's text.</summary>
    public static string SkipLauncherLabel(bool on) => on ? "Skip launcher: On" : "Skip launcher: Off";

    /// <summary>Whether the next start skips the launcher; absent is off.</summary>
    public static bool SkipLauncher(string config) =>
        Engine_RefiiConfig.Bool(RuntimeConfig.ReadValues(config), SkipLauncherKey, false);

    /// <summary>Writes <paramref name="on"/> as <c>android_skip_launcher</c>.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void SetSkipLauncher(string config, bool on)
    {
        SortedDictionary<string, string> values = RuntimeConfig.ReadValues(config);
        values[SkipLauncherKey] = on ? "true" : "false";
        Write(config, values);
    }

    /// <summary>The rumble's strength, a percentage, shared with the launcher's Controls settings.</summary>
    public const string VibrationStrengthKey = "vibration_strength";

    /// <summary>The rumble's strength until the player picks another: half, which a phone's vibrator plays firmly enough.</summary>
    public const int VibrationStrengthDefault = 50;

    /// <summary>The strengths a tap steps through, in order; 0 is off.</summary>
    public static readonly int[] VibrationLevels = { 100, 75, 50, 25, 0 };

    /// <summary>The vibration button's text.</summary>
    public static string VibrationLabel(int level) => level <= 0 ? "Vibration: Off" : $"Vibration: {level}%";

    /// <summary>The rumble's strength as a percentage, 0 when <c>controller_vibration</c> is off; absent is <see cref="VibrationStrengthDefault"/>, as the launcher's defaults.</summary>
    public static int VibrationLevel(string config)
    {
        SortedDictionary<string, string> values = RuntimeConfig.ReadValues(config);
        return Engine_RefiiConfig.Bool(values, VibrationKey, true)
            ? Math.Clamp(Engine_RefiiConfig.Int(values, VibrationStrengthKey, VibrationStrengthDefault), 1, 100)
            : 0;
    }

    /// <summary>The strength after <paramref name="current"/>; one not in the list steps to the first.</summary>
    public static int NextVibrationLevel(int current)
    {
        int at = Array.IndexOf(VibrationLevels, Math.Max(0, current));
        return VibrationLevels[(at + 1) % VibrationLevels.Length];
    }

    /// <summary>Writes <paramref name="level"/>: 0 turns <c>controller_vibration</c> off and keeps the strength; above 0 turns it on at that strength.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void SetVibrationLevel(string config, int level)
    {
        SortedDictionary<string, string> values = RuntimeConfig.ReadValues(config);
        values[VibrationKey] = level > 0 ? "true" : "false";
        if (level > 0) values[VibrationStrengthKey] = Math.Clamp(level, 1, 100).ToString(System.Globalization.CultureInfo.InvariantCulture);
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
        if (GraphicsPresets.Writes(preset, values) is not { } chosen || !GraphicsPresets.Apply(preset, values))
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
    public static void SetGraphics(string config, string key, string value, Action<string, string> live) =>
        SetGraphics(config, new[] { (key, value) }, live);

    /// <summary>
    /// Writes several graphics settings at once (an Options row and the settings that move with it,
    /// <see cref="Refii.AndroidRuntime.AndroidGraphicsOptions.Writes"/>) and passes each to <paramref name="live"/>.
    /// </summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void SetGraphics(string config, IEnumerable<(string Key, string Value)> settings, Action<string, string> live)
    {
        SortedDictionary<string, string> values = RuntimeConfig.ReadValues(config);
        var written = settings.ToList();
        foreach ((string key, string value) in written)
        {
            values[key] = value;
        }

        values[GraphicsPresets.Key] = GraphicsPresets.Current(values);
        Write(config, values);
        foreach ((string key, string value) in written)
        {
            live(key, value);
        }
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

    /// <summary>Writes the active title's file and copies what changed to the other title's, which the
    /// launcher's settings share (<see cref="AndroidLauncherSettings.ShareWithOtherTitles"/>).</summary>
    private static void Write(string config, IReadOnlyDictionary<string, string> values)
    {
        SortedDictionary<string, string> before = RuntimeConfig.ReadValues(config);
        if (!RuntimeConfig.WriteValues(config, values, out string error))
        {
            throw new IOException("Cannot write " + config + ": " + error);
        }

        AndroidLauncherSettings.ShareWithOtherTitles(config, before, values);
    }
}
