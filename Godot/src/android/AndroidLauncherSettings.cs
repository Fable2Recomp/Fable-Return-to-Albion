using System;
using System.IO;
using System.Text.RegularExpressions;
using Refii.Launcher.Core;

namespace Refii.AndroidRuntime;

/// <summary>Resolves desktop automatic paths before the shared settings panel writes Android's runtime configuration.</summary>
public static class AndroidLauncherSettings
{
    /// <summary>The launcher settings a phone ignores: the window's size, mode, monitor and vsync, the
    /// rendering driver, and the anti-aliasing and upscalers the phone's renderer turns off
    /// (AndroidGameHost: MSAA and TAA off, bilinear scaling at render_scale_percent).</summary>
    private static readonly System.Collections.Generic.HashSet<string> DesktopOnly = new(StringComparer.Ordinal)
    {
        "fullscreen", "monitor", "borderless", "display_aspect", "display_resolution", "vsync_mode",
        "rendering_driver", "msaa", "taa", "render_scaler", "fsr_quality", "fsr_sharpness",
    };

    /// <summary>Whether the launcher's settings list shows <paramref name="name"/>: not, on a phone, a
    /// setting the phone ignores, which only made the list longer and looked like it should do something.</summary>
    public static bool Shows(string name) => !GraphicsPresets.Offered || !DesktopOnly.Contains(name);

    /// <summary>What a phone player sets in the launcher without ticking Advanced: the graphics preset
    /// and the frame cap with the two settings beside them, the camera and vibration, and online and
    /// split-screen co-op. The rest of the list (the tuning dials the preset sets, the phone's own
    /// experiments, keyboard and mouse bindings) stays under Advanced; the game's Options panel has
    /// the graphics settings with an explanation each.</summary>
    private static readonly System.Collections.Generic.HashSet<string> PlayerFacing = new(StringComparer.Ordinal)
    {
        GraphicsPresets.Key, "max_fps", "fps_counter", "render_scale_percent",
        "camera_invert_x", "camera_invert_y", "controller_vibration", AndroidQuickSettings.VibrationStrengthKey,
        "rexnet", "rexnet_display_name", Refii.Game.GameSplitView.SwitchKey, Refii.Game.GameSplitView.OrientationKey,
    };

    /// <summary>On a phone, whether <paramref name="name"/> waits for Advanced (anything a player does not
    /// set); null elsewhere, where the panel's own rule decides.</summary>
    public static bool? Advanced(string name) => GraphicsPresets.Offered ? !PlayerFacing.Contains(name) : null;

    /// <summary>Whether the launcher offers the Look choice: not on a phone, which always draws the modern
    /// lighting, and where Modern would only switch on the desktop's modernised materials.</summary>
    public static bool ShowsLook => !GraphicsPresets.Offered;

    /// <summary>Preserves launch options while making automatic native paths writable and consistent with the selected installation.</summary>
    /// <param name="runtimeRoot">Physical private runtime directory supplied by the Android host.</param>
    /// <param name="gameRoot">Selected prepared game directory, or null before an installation exists.</param>
    public static void Prepare(string runtimeRoot, string? gameRoot)
    {
        string root = AndroidRuntimeOptions.PhysicalPath(runtimeRoot, "runtime root");
        string config = Path.Combine(root, Refii.Core.Engine_Title.Active.ConfigFileName);
        var values = RuntimeConfig.ReadValues(config);
        bool changed = false;
        // Native automatic logging uses the executable folder, which is /system/bin
        // for Android's app_process64. Resolve that default before TOML overrides argv.
        if (!values.TryGetValue("log_file", out string? log) || string.IsNullOrWhiteSpace(log))
        {
            values["log_file"] = Path.Combine(root, "logs", "refii.log");
            changed = true;
        }
        changed |= ApplyPresetOnce(values);
        changed |= GraphicsPresets.Upgrade(values);
        if (gameRoot != null)
        {
            string game = AndroidRuntimeOptions.PhysicalPath(gameRoot, "game root");
            PrepareUpdateBank(game);
            if (!values.TryGetValue("game_data_root", out string? previous) || string.IsNullOrWhiteSpace(previous) ||
                SamePath(previous, Path.Combine(root, "assets")) || (OwnedGamePath(previous, root) && !SamePath(previous, game)))
            {
                values["game_data_root"] = game;
                changed = true;
            }
        }
        if (changed && !RuntimeConfig.WriteValues(config, values, out string error))
            throw new IOException("Cannot prepare Android launcher paths: " + error);
    }

    /// <summary>Gives a configuration that never chose a graphics preset the one for this phone's GPU
    /// (<see cref="FirstPreset"/>) and its frame cap, or Low with that cap on a phone short of memory
    /// (<see cref="Refii.Game.AndroidMemoryRelease.LowRamPhone"/>).</summary>
    /// <remarks>Runs once: afterwards the key is present, and the player's own choices, the
    /// preset's settings and the cap included, stay exactly as they leave them.</remarks>
    /// <param name="values">The runtime configuration's values, changed in place.</param>
    /// <param name="adapter">The GPU's name; null asks the rendering server.</param>
    /// <returns>Whether the preset was applied.</returns>
    public static bool ApplyPresetOnce(System.Collections.Generic.IDictionary<string, string> values, string? adapter = null)
    {
        if (values.ContainsKey(GraphicsPresets.Key)) return false;
        (string preset, string cap) = FirstPreset(adapter ?? AdapterName());
        if (Refii.Core.Engine_Platform.IsAndroid && Refii.Game.AndroidMemoryRelease.LowRamPhone()) preset = GraphicsPresets.Low;
        if (!GraphicsPresets.Apply(preset, values)) return false;
        values["max_fps"] = cap;
        // The key that says RuntimeConfig's one-shot migration has run: without it the next read
        // turns placed_light_shadows back on, and the first preset reads as Custom with its
        // shadowless shaders rebuilt.
        values["placed_light_shadows_world"] = "false";
        return true;
    }

    /// <summary>
    /// The preset and frame cap a phone starts on, by its GPU: Low on an Adreno older than the
    /// 700s, else High, both at <see cref="GraphicsPresets.AndroidFrameCap"/>.
    /// </summary>
    /// <remarks>On the Galaxy S10e (Adreno 640) High ran 13 FPS at either cap, GPU-bound, while
    /// Low reached 22-25 at 30. A cap of 20 holds steadier there (every frame 50 ms), but it is the
    /// player's choice in Options, never the default. The 650 and 660 (Snapdragon 865, 888) have
    /// roughly half as much GPU again, untested. The S25+'s Adreno 830 runs High at 30 with room to
    /// spare, and the 700s and any other GPU start on High, as every phone did before.</remarks>
    /// <param name="adapter">The GPU's name as the rendering server gives it ("Adreno (TM) 640").</param>
    public static (string Preset, string Cap) FirstPreset(string adapter)
    {
        Match adreno = Regex.Match(adapter, @"Adreno\D{0,8}(\d{3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (adreno.Success && int.TryParse(adreno.Groups[1].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int model) && model < 700)
            return (GraphicsPresets.Low, GraphicsPresets.AndroidFrameCap);
        return (GraphicsPresets.High, GraphicsPresets.AndroidFrameCap);
    }

    /// <summary>The GPU's name on a phone; empty elsewhere (tools and tests run without a GPU's server).</summary>
    private static string AdapterName()
    {
        if (!Refii.Core.Engine_Platform.IsAndroid) return string.Empty;
        try
        {
            return Godot.RenderingServer.GetVideoAdapterName() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Copies the earlier Android TU layout into upstream's update volume without replacing existing files.</summary>
    /// <param name="gameRoot">The prepared private game directory containing the extracted update bank.</param>
    public static void PrepareUpdateBank(string gameRoot)
    {
        string source = Path.Combine(gameRoot, TitleUpdate.DataBankRelativePath);
        string target = Path.Combine(Refii.Core.Engine_GameData.UpdateRootFor(gameRoot), TitleUpdate.DataBankRelativePath);
        if (!File.Exists(source) || File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: false);
    }

    /// <summary>Recognizes an installer's own prior game locator when activating a replacement installation.</summary>
    /// <param name="path">Previously recorded physical game directory.</param>
    /// <param name="runtimeRoot">Private runtime directory beside phone-installs.</param>
    /// <returns>Whether the path is exactly an owned installation's game child.</returns>
    private static bool OwnedGamePath(string path, string runtimeRoot)
    {
        string parent = AndroidRuntimeOptions.TitleDirectory(Path.GetDirectoryName(runtimeRoot) ?? runtimeRoot);
        string relative = Path.GetRelativePath(Path.Combine(parent, "phone-installs"), path).Replace('\\', '/');
        return Regex.IsMatch(relative, @"\Ainstall-[0-9a-f]{32}/game\z", RegexOptions.CultureInvariant);
    }

    /// <summary>Compares physical paths using the current host's filesystem case and separators.</summary>
    /// <param name="left">Recorded path.</param>
    /// <param name="right">Automatic private default.</param>
    /// <returns>Whether both paths name the same location.</returns>
    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
