using Refii.Mods;

namespace Refii.Ui;

/// <summary>
/// The dynamic sky's switches as a host options page, DESCRIBED IN A MOD'S OWN FORMAT: the rows
/// are a <c>settings.ui.json</c>-shaped description (<see cref="Description"/>) read by the same
/// parser a mod's page is built from (<see cref="ModSettingsUi.DescribeRows"/>), over the game's
/// config switches instead of a mod's settings (<see cref="ModSettingsUi.SwitchPage"/>) — the game's
/// page and a mod's are one kind, so a change to how pages read or draw reaches both.
/// </summary>
public static class Engine_UiWeatherOptions
{
	public const string PageName = "DynamicSkyAndWeather";
	public const string OpenCommand = "options:weather";
	public const string Title = "Dynamic Sky and Weather";

	/// <summary>
	/// The page's rows, keyed by the switch each one writes, in the order they show. A key's
	/// <c>default</c> is what the sky reads while the switch is unset (Engine_DynamicSky,
	/// Engine_DynamicNightSky, Engine_DynamicSurfaceWeather). The weather override's values are the
	/// presets the dynamic sky takes; the time of day is "auto" or a whole hour; the aurora reads
	/// "on"/"off" also as "true"/"1" and "false"/"0" (<c>Engine_DynamicNightSky.ParseMode</c>).
	/// </summary>
	public const string Description = """
		{
		  "dynamic_sky_and_weather":       { "label": "Enable/Disable", "type": "toggle", "default": false },
		  "dynamic_sky_volumetric_clouds": { "label": "Volumetric Clouds", "type": "toggle", "default": false },
		  "dynamic_sky_cloud_shadows":     { "label": "Cloud Shadows", "type": "toggle", "default": false },
		  "dynamic_sky_weather": { "label": "Weather Override", "type": "choice", "default": "theme", "options": [
		    { "value": "theme", "label": "Auto" }, { "value": "clear", "label": "Clear" },
		    { "value": "partlycloudy", "label": "Partly Cloudy" }, { "value": "cloudy", "label": "Cloudy" },
		    { "value": "overcast", "label": "Overcast" }, { "value": "rain", "label": "Rain" },
		    { "value": "storm", "label": "Storm" }, { "value": "snow", "label": "Snow" },
		    { "value": "fog", "label": "Fog" }, { "value": "lightrain", "label": "Light Rain" },
		    { "value": "lightsnow", "label": "Light Snow" }, { "value": "blizzard", "label": "Blizzard" } ] },
		  "dynamic_sky_lighting_override": { "label": "Lighting Override", "type": "toggle", "default": false },
		  "dynamic_sky_time_of_day": { "label": "Time of Day Override", "type": "choice", "default": "auto", "options": [
		    { "value": "auto", "label": "Auto" },
		    { "value": "0", "label": "00:00" }, { "value": "1", "label": "01:00" }, { "value": "2", "label": "02:00" },
		    { "value": "3", "label": "03:00" }, { "value": "4", "label": "04:00" }, { "value": "5", "label": "05:00" },
		    { "value": "6", "label": "06:00" }, { "value": "7", "label": "07:00" }, { "value": "8", "label": "08:00" },
		    { "value": "9", "label": "09:00" }, { "value": "10", "label": "10:00" }, { "value": "11", "label": "11:00" },
		    { "value": "12", "label": "12:00" }, { "value": "13", "label": "13:00" }, { "value": "14", "label": "14:00" },
		    { "value": "15", "label": "15:00" }, { "value": "16", "label": "16:00" }, { "value": "17", "label": "17:00" },
		    { "value": "18", "label": "18:00" }, { "value": "19", "label": "19:00" }, { "value": "20", "label": "20:00" },
		    { "value": "21", "label": "21:00" }, { "value": "22", "label": "22:00" }, { "value": "23", "label": "23:00" } ] },
		  "dynamic_sky_aurora": { "label": "Aurora", "type": "choice", "default": "auto", "options": [
		    { "value": "auto", "label": "Auto (10%)" },
		    { "value": "on", "label": "On", "aliases": [ "true", "1" ] },
		    { "value": "off", "label": "Off", "aliases": [ "false", "0" ] } ] },
		  "dynamic_sky_surface_weather": { "label": "Surface Weather", "type": "toggle", "default": false }
		}
		""";

	/// <summary>The page the options row opens; registered by the host page kind under <see cref="OpenCommand"/>.</summary>
	public static readonly ModSettingsUi.SwitchPage Page =
		ModSettingsUi.SwitchPage.FromDescription(PageName, Title, Description);
}
