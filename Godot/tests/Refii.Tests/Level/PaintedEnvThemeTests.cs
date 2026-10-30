namespace Refii.Tests.Level;

using System;
using System.Collections.Generic;
using System.Text;
using GdUnit4;
using Refii.Level;
using Refii.Lighting;
using static GdUnit4.Assertions;

/// <summary>
/// Fable 3's painted layer resolves a cell's word through EnvironmentThemeMap.txt (sub_821766D0
/// matches entry+16) to an .envtheme, which states only the parameters it carries.
/// </summary>
[TestSuite]
public class PaintedEnvThemeTests
{
	private const uint NoLightId = 0x9EC122A5;

	private static string Colour(string name, float x, float y, float z, float w) =>
		$"<ThemeParam Name=\"{name}\" param_type=\"rgbcolourwithfactor\"><TimelineItem><Time type=\"float\">0.000000 0</Time>" +
		$"<Value><X type=\"float\">{x}</X><Y type=\"float\">{y}</Y><Z type=\"float\">{z}</Z><W type=\"float\">{w}</W></Value>" +
		"</TimelineItem></ThemeParam>";

	private static byte[] Theme(float fadeIn, string parameters) => Encoding.ASCII.GetBytes(
		"<?xml version=\"1.0\"?>\n<EnvironmentTheme version=\"1\">" +
		$"<FadeInTime type=\"float\">{fadeIn}</FadeInTime><FadeOutTime type=\"float\">2</FadeOutTime>" +
		$"<ThemeParams>{parameters}</ThemeParams></EnvironmentTheme>");

	private static List<byte[]> Chain(Dictionary<string, byte[]> files)
	{
		var chain = new List<byte[]>();
		Engine_EnvThemeFile.AppendLevelThemes(null, path => files.TryGetValue(path, out byte[]? b) ? b : null, chain);
		return chain;
	}

	private static Dictionary<string, byte[]> Files() => new()
	{
		[Engine_EnvThemeFile.MapPath] = Encoding.ASCII.GetBytes(
			"Version=1\r\n576DCF9A\r\nEnvThemeBase\r\nnewenvironmentthemes\\EnvThemeBase.envtheme\r\nTrue\r\n" +
			"9EC122A5\r\nBWS_Castle_Interior_NoLight\r\nnewenvironmentthemes\\region_final\\NoLight.envtheme\r\nFalse\r\n"),
		["newenvironmentthemes/envthemebase.envtheme"] = Theme(5f,
			Colour("Main Light Colour", 1.5f, 1.38f, 1.38f, 1.5f) + Colour("Ambient Colour", 0.1f, 0.2f, 0.3f, 1f)),
		["newenvironmentthemes/region_final/nolight.envtheme"] = Theme(3f,
			Colour("Main Light Colour", 0f, 0f, 0f, 0f)),
	};

	[TestCase]
	public void PaintedIdResolvesThroughTheMap()
	{
		Chain(Files());
		AssertBool(Engine_EnvThemeFile.TryMapTheme(NoLightId, out Engine_EnvThemeFile theme, out string name)).IsTrue();
		AssertString(name).IsEqual("BWS_Castle_Interior_NoLight");
		AssertFloat(theme.FadeInTime).IsEqualApprox(3f, 1e-5f);
		AssertFloat(theme.FadeOutTime).IsEqualApprox(2f, 1e-5f);
		AssertBool(Engine_EnvThemeFile.TryMapTheme(0x12345678, out _, out _)).IsFalse();
	}

	[TestCase]
	public void PaintedLayerStatesOnlyWhatItCarries()
	{
		List<byte[]> chain = Chain(Files());
		AssertBool(Engine_LightingTheme.TryExtract(chain, 0.42f, NoLightId, out LightingTheme painted)).IsTrue();
		AssertBool(painted.HasMainLightColour).IsTrue();
		AssertBool(painted.HasAmbientColour).IsFalse();
		AssertFloat(painted.MainLightColour[0]).IsEqualApprox(0f, 1e-6f);

		AssertBool(Engine_LightingTheme.TryExtract(chain, 0.42f, out LightingTheme level)).IsTrue();
		LightingTheme folded = Engine_LightingTheme.FoldPainted(level, painted, 0.5f);
		AssertFloat(folded.MainLightColour[0]).IsEqualApprox(0.75f, 1e-5f);
		AssertFloat(folded.AmbientColour[2]).IsEqualApprox(0.3f, 1e-5f);
	}
}
