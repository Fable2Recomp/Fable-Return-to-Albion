using System;
using System.Collections.Generic;
using Refii.Level;

namespace Refii.Ui;

/// <summary>
/// The dynamic sky's switches as a host options page — one <see cref="Engine_UiHostOptionsPage"/>
/// whose rows are the launcher settings the sky reads, committed to the config on A.
/// </summary>
public static class Engine_UiWeatherOptions
{
    public const string PageName = "DynamicSkyAndWeather";
    public const string OpenCommand = "options:weather";
    public const string Title = "Dynamic Sky and Weather";

    /// <summary>The page the options row opens; registered by the host page kind under <see cref="OpenCommand"/>.</summary>
    public static readonly Engine_UiHostOptionsPage Page = new WeatherPage();

    private sealed class WeatherPage : Engine_UiHostOptionsPage
    {
        public override string Name => PageName;
        public override string Title => Engine_UiWeatherOptions.Title;
        public override int Count => Keys.Length;
        public override string RowTitle(int row) => Titles[row];
        public override int Choices(int row) => Engine_UiWeatherOptions.Count(row);
        public override string Caption(int row, int index) => Engine_UiWeatherOptions.Caption(row, index);
        public override int[] Read() => Engine_UiWeatherOptions.Read(ReadCurrent);

        /// <summary>Written together on A, as one config write; a refused write keeps the page open.</summary>
        public override bool Commit(Func<int, int> index) =>
            Engine_UiScreenActions.WriteSettings(Settings(index));
    }

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

    private static readonly string[] Weather = Engine_UiScreenActions.WeatherOverrideValues.Split(',');
    private static readonly string[] Aurora = { "auto", "on", "off" };
    private static readonly string[] WeatherTitles =
    {
        "Auto", "Clear", "Partly Cloudy", "Cloudy", "Overcast", "Rain", "Storm", "Snow", "Fog",
        "Light Rain", "Light Snow", "Blizzard",
    };

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

    private static bool IsOn(string value) => value.Trim().Trim('"').ToLowerInvariant() is "true" or "1";

    private static int TimeIndex(string value)
    {
        float time = Engine_DynamicNightSky.ResolveTime(-1f, true, value);
        return time < 0f ? 0 : (int)MathF.Round(time * 24f) % 24 + 1;
    }
}
