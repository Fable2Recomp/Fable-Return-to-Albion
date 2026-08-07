using System.Globalization;
using Godot;

namespace Refii.Launcher.Core;

public static class LauncherTheme
{
    public static Color ChangeLogBackground { get; private set; }
    public static Color ChangeLogForeground { get; private set; }
    public static Color MenuBarBackground { get; private set; }
    public static Color MenuBarForeground { get; private set; }
    public static Color WarningBackground { get; private set; }
    public static Color WarningForeground { get; private set; }
    public static Color LaunchButtonBackground { get; private set; }
    public static Color LaunchButtonForeground { get; private set; }
    public static Color BaseBackground { get; private set; }
    public static Color BaseForeground { get; private set; }

    public static string WindowTitle { get; private set; } = "Fable 2 Launcher";
    public static string WarningText { get; private set; } = string.Empty;
    public static bool ShowLog { get; private set; } = true;

    public static float BackgroundDuration { get; private set; } = 7.0f;
    public static float BackgroundFadeDuration { get; private set; } = 1.0f;

    public static void Load()
    {
        ChangeLogBackground = ReadColor("ChangeLog_background", new Color(0.2f, 0.8f, 0.4f, 1f));
        ChangeLogForeground = ReadColor("ChangeLog_forground", new Color(0.02f, 0.1f, 0.2f, 1f));
        MenuBarBackground = ReadColor("MenuBar_background", new Color(0.2f, 0.8f, 0.4f, 1f));
        MenuBarForeground = ReadColor("MenuBar_forground", new Color(0.02f, 0.1f, 0.2f, 1f));
        WarningBackground = ReadColor("Warning_background", new Color(1f, 0f, 0f, 1f));
        WarningForeground = ReadColor("Warning_forground", new Color(1f, 0f, 0f, 1f));
        LaunchButtonBackground = ReadColor("LaunchButton_background", new Color(0.2f, 0.8f, 0.4f, 1f));
        LaunchButtonForeground = ReadColor("LaunchButton_forground", new Color(0.02f, 0.1f, 0.2f, 1f));
        BaseBackground = ReadColor("BaseColor_background", new Color(0.2f, 0.8f, 0.4f, 1f));
        BaseForeground = ReadColor("BaseColor_forground", new Color(0.02f, 0.1f, 0.2f, 1f));

        WindowTitle = Ini.GetString("Title", "Fable 2 Launcher", "Window");
        WarningText = Ini.GetString("WarningText", string.Empty, "Window");
        ShowLog = Ini.GetBool("Log", true, "Window");

        BackgroundDuration = ReadFloat("Duration", 7.0f, "Background");
        BackgroundFadeDuration = ReadFloat("FadeDuration", 1.0f, "Background");
    }

    private static Color ReadColor(string key, Color fallback)
    {
        string raw = Ini.GetString(key, string.Empty, "Theme");
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        string[] parts = raw.Split(',');
        if (parts.Length < 4)
        {
            return fallback;
        }

        float[] components = new float[4];
        for (int i = 0; i < 4; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out components[i]))
            {
                return fallback;
            }
        }

        return new Color(components[0], components[1], components[2], components[3]);
    }

    private static float ReadFloat(string key, float fallback, string section)
    {
        string raw = Ini.GetString(key, string.Empty, section);
        return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;
    }
}
