namespace Refii.Tests.Modding;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Level;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// One settings-page surface for the game and mods: the dynamic sky's page is a
/// <c>settings.ui.json</c>-shaped description over the game's switches, and its rows read and
/// store the switch text the sky reads. Pure .NET — rows are built directly and the description is
/// read with System.Text.Json; the page's own Godot JSON parse is not reached.
/// </summary>
[TestSuite]
public class SettingsPageTests
{
	private static ModSettingsUi.Row TextChoice(string fallback, params (string Text, string Label, string[] Aliases)[] options) =>
		new("game", "key", "Label", ModSettingsUi.Kind.Choice, 0f, options.Length - 1, 1f, string.Empty,
			options.Select((o, i) => new ModSettingsUi.Option(i, o.Label, o.Text, o.Aliases)).ToList(), fallback);

	[TestCase]
	public void TheSkyPageWritesTheSwitchesTheSkyReads()
	{
		using JsonDocument doc = JsonDocument.Parse(Engine_UiWeatherOptions.Description);
		string[] keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
		AssertThat(keys).IsEqual(new[]
		{
			Engine_DynamicSky.SwitchKey, Engine_DynamicSky.VolumetricCloudsKey, Engine_DynamicSky.CloudShadowsKey,
			Engine_DynamicSky.WeatherKey, Engine_DynamicSky.LightingOverrideKey, Engine_DynamicNightSky.TimeKey,
			Engine_DynamicNightSky.AuroraKey, Engine_DynamicSurfaceWeather.SwitchKey,
		});
	}

	[TestCase]
	public void ATextChoiceReadsItsTextItsAliasesAndTheNearestHour()
	{
		ModSettingsUi.Row aurora = TextChoice("auto",
			("auto", "Auto", System.Array.Empty<string>()), ("on", "On", new[] { "true", "1" }), ("off", "Off", new[] { "false", "0" }));
		AssertThat(aurora.IndexOfStored("ON")).IsEqual(1);
		AssertThat(aurora.IndexOfStored("true")).IsEqual(1);
		AssertThat(aurora.IndexOfStored("0")).IsEqual(2);
		AssertThat(aurora.IndexOfStored(string.Empty)).IsEqual(0);
		AssertThat(aurora.IndexOfStored("sometimes")).IsEqual(0);
		AssertThat(aurora.StoredAt(2)).IsEqual("off");

		var hours = new List<(string, string, string[])> { ("auto", "Auto", System.Array.Empty<string>()) };
		for (int h = 0; h < 24; h++)
		{
			hours.Add((h.ToString(), $"{h:00}:00", System.Array.Empty<string>()));
		}

		ModSettingsUi.Row time = TextChoice("auto", hours.ToArray());
		AssertThat(time.IndexOfStored("auto")).IsEqual(0);
		AssertThat(time.IndexOfStored("13")).IsEqual(14);
		AssertThat(time.IndexOfStored("12.4")).IsEqual(13);
		AssertThat(time.Caption(time.IndexOfStored("7"))).IsEqual("07:00");
	}

	[TestCase]
	public void AToggleReadsAndStoresTheSwitchesText()
	{
		var toggle = new ModSettingsUi.Row("game", "key", "Label", ModSettingsUi.Kind.Toggle, 0f, 1f, 1f, string.Empty, null, "false");
		AssertThat(toggle.IndexOfStored("true")).IsEqual(1);
		AssertThat(toggle.IndexOfStored("1")).IsEqual(1);
		AssertThat(toggle.IndexOfStored(string.Empty)).IsEqual(0);
		AssertThat(toggle.StoredAt(1)).IsEqual("true");
		AssertThat(toggle.StoredAt(0)).IsEqual("false");
	}

	[TestCase]
	public void AModsChoiceStaysNumericAndTextOptionsAreAnError()
	{
		var issues = ModValidate.Validate("settings.ui.json",
			"{ \"partner\": { \"type\": \"choice\", \"options\": [ { \"value\": \"lightning\", \"label\": \"Lightning\" } ] } }").ToList();
		AssertThat(issues.Any(i => i.Error && i.Message.Contains("must be a number"))).IsTrue();
	}
}
