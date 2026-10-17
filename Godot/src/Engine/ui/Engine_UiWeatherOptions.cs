using System;
using System.Collections.Generic;
using Refii.Level;

namespace Refii.Ui;

public static class Engine_UiWeatherOptions
{
    public const string PageName = "DynamicSkyAndWeather";
    public const string OpenCommand = "options:weather";
    public const string Title = "Dynamic Sky and Weather";
    public const string Buttons = "Page1.Contents.Buttons.Buttons";

    public static readonly string[] Keys =
    {
        Engine_DynamicSky.SwitchKey, Engine_DynamicSky.VolumetricCloudsKey,
        Engine_DynamicSky.CloudShadowsKey, Engine_UiScreenActions.WeatherOverrideKey,
        Engine_DynamicSky.LightingOverrideKey,
        Engine_DynamicNightSky.TimeKey, Engine_DynamicNightSky.AuroraKey,
        Engine_DynamicSurfaceWeather.SwitchKey,
    };

    public static readonly string[] Titles =
    {
        "Enable/Disable", "Volumetric Clouds", "Cloud Shadows", "Weather Override",
        "Lighting Override", "Time of Day Override", "Aurora",
        "Surface Weather",
    };

    public static readonly string[] Rows =
    {
        "Options.Subtitles_pickbox", "Options.BreadcrumbSize_pickbox",
        "Options.TutorialBoxes_pickbox", "Options.MultiplayerOrbs_pickbox",
        "Options.Auto_joinable_pickbox",
    };

    private static readonly string[] Weather = Engine_UiScreenActions.WeatherOverrideValues.Split(',');
    private static readonly string[] Aurora = { "auto", "on", "off" };
    private static readonly string[] WeatherTitles =
    {
        "Auto", "Clear", "Partly Cloudy", "Cloudy", "Overcast", "Rain", "Storm", "Snow", "Fog",
        "Light Rain", "Light Snow", "Blizzard",
    };

    public static bool Open => Engine_UiOptionsPending.Page == PageName;

    public static int VisibleStart => Open ? WindowStart(Engine_UiOptionsPending.Row) : 0;

    public static int WindowStart(int selected) => Math.Clamp(selected - Rows.Length + 1, 0, Keys.Length - Rows.Length);

    public static int[] Read(Func<string, string> read) => new[]
    {
        IsOn(read(Keys[0])) ? 1 : 0,
        IsOn(read(Keys[1])) ? 1 : 0,
        IsOn(read(Keys[2])) ? 1 : 0,
        Math.Max(0, Array.IndexOf(Weather, read(Keys[3]).Trim().Trim('"').ToLowerInvariant())),
        IsOn(read(Keys[4])) ? 1 : 0,
        TimeIndex(read(Keys[5])),
        (int)Engine_DynamicNightSky.ParseMode(read(Keys[6])),
        IsOn(read(Keys[7])) ? 1 : 0,
    };

    public static string ReadCurrent(string key) => Refii.Core.Engine_Switches.String(key,
        key == Keys[3] ? "theme" : key == Keys[5] || key == Keys[6] ? "auto" : "false");

    public static int Count(int row) => row switch { 3 => Weather.Length, 5 => 25, 6 => 3, _ => 2 };

    public static string Caption(int row, int index) => row switch
    {
        3 => WeatherTitles[Math.Clamp(index, 0, WeatherTitles.Length - 1)],
        5 => index <= 0 ? "Auto" : $"{Math.Clamp(index - 1, 0, 23):00}:00",
        6 => index == 1 ? "On" : index == 2 ? "Off" : "Auto (10%)",
        _ => index == 0 ? "Off" : "On",
    };

    public static IReadOnlyDictionary<string, string> Settings(Func<int, int> index)
    {
        var values = new Dictionary<string, string>();
        for (int row = 0; row < Keys.Length; row++)
            values[Keys[row]] = row switch
            {
                3 => Weather[Math.Clamp(index(row), 0, Weather.Length - 1)],
                5 => index(row) <= 0 ? "auto" : Math.Clamp(index(row) - 1, 0, 23).ToString(System.Globalization.CultureInfo.InvariantCulture),
                6 => Aurora[Math.Clamp(index(row), 0, 2)],
                _ => index(row) == 0 ? "false" : "true",
            };
        return values;
    }

    public static Dictionary<string, string> Text(Func<int, int> index, int first = 0)
    {
        var values = new Dictionary<string, string>();
        for (int row = 0; row < Rows.Length; row++)
        {
            int setting = row + Math.Clamp(first, 0, Keys.Length - Rows.Length);
            values[Rows[row] + ".grp.text"] = Caption(setting, index(setting));
            values[Rows[row][..^"_pickbox".Length]] = Titles[setting];
        }
        values["Page1.Contents.OptionName"] = Title + $" ({first + 1}-{first + Rows.Length}/{Keys.Length})";
        return values;
    }

    public static void Apply(Engine_UiOwnedBook book, Engine_UiExpandableMenu.Item? row)
    {
        if (!Open && row?.Name != PageName) return;
        int[] committed = Read(ReadCurrent);
        int Index(int at) => Open ? Engine_UiOptionsPending.IndexOf(at) : committed[at];
        book.HostValues = Text(Index, VisibleStart);
        var active = new Dictionary<string, bool>(book.HostActive ?? new Dictionary<string, bool>());
        active["Foreground.Book.Pages.Page1"] = true;
        active[Buttons] = Open;
        active[Buttons + ".Learn.Edit"] = false;
        active[Buttons + ".Learn.Done"] = Open;
        foreach (string path in Rows) active[path] = true;
        book.HostActive = active;
        var opacity = new Dictionary<string, float>(book.HostOpacity ?? new Dictionary<string, float>());
        opacity[Buttons] = 1f;
        for (int i = 0; i < Rows.Length; i++)
            opacity[Rows[i]] = !Open || Engine_UiOptionsPending.Row == i + VisibleStart ? 1f : 0.6f;
        book.HostOpacity = opacity;
    }

    private static bool IsOn(string value) => value.Trim().Trim('"').ToLowerInvariant() is "true" or "1";

    private static int TimeIndex(string value)
    {
        float time = Engine_DynamicNightSky.ResolveTime(-1f, true, value);
        return time < 0f ? 0 : (int)MathF.Round(time * 24f) % 24 + 1;
    }
}
