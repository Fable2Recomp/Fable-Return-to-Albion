using System;
using System.IO;
using System.Text.RegularExpressions;
using Refii.Launcher.Core;

namespace Refii.AndroidRuntime;

/// <summary>Resolves desktop automatic paths before the shared settings panel writes Android's runtime configuration.</summary>
public static class AndroidLauncherSettings
{
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

    /// <summary>Gives a configuration that never chose a graphics preset Balanced and the 30 FPS cap.</summary>
    /// <remarks>Runs once: afterwards the key is present, and the player's own choices, the
    /// preset's settings and the cap included, stay exactly as they leave them.</remarks>
    /// <param name="values">The runtime configuration's values, changed in place.</param>
    /// <returns>Whether the preset was applied.</returns>
    public static bool ApplyPresetOnce(System.Collections.Generic.IDictionary<string, string> values)
    {
        if (values.ContainsKey(GraphicsPresets.Key) || !GraphicsPresets.Apply(GraphicsPresets.Balanced, values))
            return false;
        values["max_fps"] = GraphicsPresets.AndroidFrameCap;
        return true;
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
