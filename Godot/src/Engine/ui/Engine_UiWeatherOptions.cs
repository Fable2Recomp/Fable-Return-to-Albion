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
    };

    public static readonly string[] Titles =
    {
        "Enable/Disable", "Volumetric Clouds", "Cloud Shadows", "Weather Override",
    };

    public static readonly string[] Rows =
    {
        "Options.Subtitles_pickbox", "Options.BreadcrumbSize_pickbox",
        "Options.TutorialBoxes_pickbox", "Options.MultiplayerOrbs_pickbox",
    };

    private static readonly string[] Weather = Engine_UiScreenActions.WeatherOverrideValues.Split(',');
    private static readonly string[] WeatherTitles =
    {
        "Auto", "Clear", "Partly Cloudy", "Cloudy", "Overcast", "Rain", "Storm", "Snow", "Fog",
    };

    public static bool Open => Engine_UiOptionsPending.Page == PageName;

    public static int[] Read(Func<string, string> read) => new[]
    {
        IsOn(read(Keys[0])) ? 1 : 0,
        IsOn(read(Keys[1])) ? 1 : 0,
        IsOn(read(Keys[2])) ? 1 : 0,
        Math.Max(0, Array.IndexOf(Weather, read(Keys[3]).Trim().Trim('"').ToLowerInvariant())),
    };

    public static string ReadCurrent(string key) => Refii.Core.Engine_Switches.String(key, key == Keys[3] ? "theme" : "false");

    public static int Count(int row) => row == 3 ? Weather.Length : 2;

    public static string Caption(int row, int index) => row == 3
        ? WeatherTitles[Math.Clamp(index, 0, WeatherTitles.Length - 1)]
        : index == 0 ? "Off" : "On";

    public static IReadOnlyDictionary<string, string> Settings(Func<int, int> index)
    {
        var values = new Dictionary<string, string>();
        for (int row = 0; row < Keys.Length; row++)
            values[Keys[row]] = row == 3 ? Weather[Math.Clamp(index(row), 0, Weather.Length - 1)]
                : index(row) == 0 ? "false" : "true";
        return values;
    }

    public static Dictionary<string, string> Text(Func<int, int> index)
    {
        var values = new Dictionary<string, string>();
        for (int row = 0; row < Rows.Length; row++)
        {
            values[Rows[row] + ".grp.text"] = Caption(row, index(row));
            values[Rows[row][..^"_pickbox".Length]] = Titles[row];
        }
        values["Page1.Contents.OptionName"] = Title;
        values["Options.Auto_joinable_pickbox.grp.text"] = string.Empty;
        values["Options.Auto_joinable"] = string.Empty;
        return values;
    }

    public static void Apply(Engine_UiOwnedBook book, Engine_UiExpandableMenu.Item? row)
    {
        if (!Open && row?.Name != PageName) return;
        int[] committed = Read(ReadCurrent);
        int Index(int at) => Open ? Engine_UiOptionsPending.IndexOf(at) : committed[at];
        book.HostValues = Text(Index);
        var active = new Dictionary<string, bool>(book.HostActive ?? new Dictionary<string, bool>());
        active["Foreground.Book.Pages.Page1"] = true;
        active["Options.Auto_joinable_pickbox"] = false;
        active[Buttons] = Open;
        active[Buttons + ".Learn.Edit"] = false;
        active[Buttons + ".Learn.Done"] = Open;
        foreach (string path in Rows) active[path] = true;
        book.HostActive = active;
        var opacity = new Dictionary<string, float>(book.HostOpacity ?? new Dictionary<string, float>());
        opacity[Buttons] = 1f;
        for (int i = 0; i < Rows.Length; i++)
            opacity[Rows[i]] = !Open || Engine_UiOptionsPending.Row == i ? 1f : 0.6f;
        book.HostOpacity = opacity;
    }

    private static bool IsOn(string value) => value.Trim().Trim('"').ToLowerInvariant() is "true" or "1";
}
