using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Refii.AndroidRuntime;

/// <summary>
/// refii.toml's <c>android_render_thread</c> on Android: Godot's separate render thread
/// (<c>rendering/driver/threads/thread_model = 2</c>) or its default, rendering on the main thread.
/// </summary>
/// <remarks>
/// The thread model is fixed in <c>Main::setup2</c>, before any game code runs, so a running game
/// can only state it for the next start. The desktop passes <c>--render-thread separate</c> to the
/// game process it spawns (GameChildProcess); an Android app has no such parent, and its packaged
/// command line (<c>assets/_cl_</c>) is fixed at export. What Godot does read at every start is the
/// project-settings override, <c>user://renderer.cfg</c> (project.godot
/// <c>application/config/project_settings_override</c>, the app's <c>files/renderer.cfg</c>), so
/// the host writes the key there. A change takes effect at the next launch, which the cvar says
/// already.
///
/// OFF UNLESS refii.toml SAYS <c>android_render_thread = true</c>: Godot marks the mode experimental, and
/// the Android default waits on measurement. The file is the toml's mirror: a hand-written
/// <c>driver/threads/thread_model</c> there is replaced by what the toml says, and every other
/// key in the file is kept.
/// </remarks>
internal static class AndroidRenderThread
{
    /// <summary>
    /// Android's own key. Not the desktop's <c>render_thread</c>: the launcher writes every cvar
    /// explicitly, so existing Android configs already hold that key at its desktop default, true.
    /// </summary>
    public const string Key = "android_render_thread";

    /// <summary>Where project.godot points the project-settings override.</summary>
    public const string SettingsPath = "user://renderer.cfg";

    private const string Section = "rendering";
    private const string SettingKey = "driver/threads/thread_model";

    /// <summary>Godot's <c>OS::RENDER_SEPARATE_THREAD</c>.</summary>
    private const int Separate = 2;

    /// <summary>Whether refii.toml asks for the separate render thread; off when unset.</summary>
    public static bool Configured(IReadOnlyDictionary<string, string> values) =>
        Refii.Core.Engine_RefiiConfig.Bool(values, Key, false);

    /// <summary>Whether this run renders on its own thread: the setting as the override file left it at start.</summary>
    public static bool Active =>
        ProjectSettings.GetSetting("rendering/driver/threads/thread_model", 1).AsInt32() == Separate;

    /// <summary>
    /// Brings <see cref="SettingsPath"/> in line with <paramref name="values"/>. Never throws: a
    /// write that fails keeps the previous mode and says why.
    /// </summary>
    /// <returns>An empty string, or the error.</returns>
    public static string Apply(IReadOnlyDictionary<string, string> values)
    {
        bool separate = Configured(values);
        string path = ProjectSettings.GlobalizePath(SettingsPath);
        try
        {
            string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
            string? wanted = Rewrite(existing, separate);
            if (wanted == existing)
            {
                return string.Empty;
            }

            if (wanted is null)
            {
                File.Delete(path);
            }
            else
            {
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, wanted, new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }

            GD.Print($"[android] render thread {(separate ? "separate" : "main (Godot's default)")} from the next launch; " +
                $"this run renders {(Active ? "on its own thread" : "on the main thread")}");
            return string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"[android] {SettingsPath} not written: {error.Message}");
            return error.Message;
        }
    }

    /// <summary>
    /// The override file's text with the thread model stated (<paramref name="separate"/>) or
    /// removed, every other line as it was; null when nothing would be left in it.
    /// </summary>
    public static string? Rewrite(string? existing, bool separate)
    {
        var lines = new List<string>((existing ?? string.Empty).Replace("\r\n", "\n").Split('\n'));
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        // The stated line keeps the place of the first one found; any repeats go.
        string section = string.Empty;
        int sectionAt = -1, sectionEnd = -1, at = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (section == Section && sectionEnd < 0)
                {
                    sectionEnd = i;
                }
                section = line[1..^1].Trim();
                if (section == Section && sectionAt < 0)
                {
                    sectionAt = i;
                }
                continue;
            }

            int equals = line.IndexOf('=');
            if (section == Section && equals > 0 && line[..equals].Trim() == SettingKey)
            {
                if (at < 0)
                {
                    at = i;
                }
                else
                {
                    lines.RemoveAt(i--);
                }
            }
        }
        if (sectionAt >= 0 && sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        string stated = SettingKey + "=" + Separate;
        if (!separate)
        {
            if (at >= 0)
            {
                lines.RemoveAt(at);
            }
        }
        else if (at >= 0)
        {
            lines[at] = stated;
        }
        else if (sectionAt >= 0)
        {
            // At the end of the section's own lines, ahead of the blank ones before the next.
            int insert = sectionEnd;
            while (insert - 1 > sectionAt && lines[insert - 1].Trim().Length == 0)
            {
                insert--;
            }
            if (insert == sectionAt + 1)
            {
                lines.Insert(insert++, string.Empty);
            }
            lines.Insert(insert, stated);
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Trim().Length != 0)
            {
                lines.Add(string.Empty);
            }
            lines.Add("[" + Section + "]");
            lines.Add(string.Empty);
            lines.Add(stated);
        }

        bool anySetting = lines.Exists(line =>
        {
            string trimmed = line.Trim();
            return trimmed.Length != 0 && !trimmed.StartsWith('[') && !trimmed.StartsWith(';') && !trimmed.StartsWith('#');
        });
        if (!anySetting)
        {
            return null;
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>The status file's row: what runs, what the toml asks for, and whether a relaunch is pending.</summary>
    public static object Describe(IReadOnlyDictionary<string, string> values)
    {
        bool configured = Configured(values);
        bool active = Active;
        return new
        {
            active = active ? "separate" : "main",
            configured = configured ? "separate" : "main",
            next_launch_differs = configured != active,
            scope = "Godot's thread model is fixed at start; refii.toml android_render_thread is mirrored to user://renderer.cfg for the next launch",
        };
    }
}
