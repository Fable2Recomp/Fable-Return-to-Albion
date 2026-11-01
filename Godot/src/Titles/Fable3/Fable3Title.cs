namespace Refii.Titles.Fable3;

using System.Runtime.CompilerServices;
using Refii.Core;

/// <summary>
/// Fable 3 to the shell: its native screens, which its hooks state on the host-only bus.
/// </summary>
public sealed class Fable3Title : Engine_TitleGame
{
	[ModuleInitializer]
	internal static void RegisterTitle() => Register(new Fable3Title());

	public override Engine_Title Title => Engine_Title.Fable3;

	/// <summary>Fable 3's shader banks are entries of levels.bnk, not loose files.</summary>
	public override (string Named, string Release)? PackedShaderBanks =>
		("shaders/shaders.sbk", "shaders/shadersrelease.sbk");

	private Fable3Splash? _splash;

	/// <summary>The loading screen's layer: over the screen fade (7), as Fable 2's is.</summary>
	private const int LoadingLayer = 8;

	public override Godot.Node CreateUi()
	{
		Fable3UiSource.Install();

		var layer = new Godot.CanvasLayer { Name = "Fable3Ui", Layer = 5 };
		layer.AddChild(new Fable3Strings { Name = "Strings" });
		// The HUD on the shared view, under every other surface as the guest's HUD layer is; its
		// screen fade stands on its own canvas layer over all of them.
		layer.AddChild(new Fable3Hud { Name = "Hud" });
		var natives = new Fable3GuiNatives { Name = "GuiNatives" };
		layer.AddChild(natives);
		// The world icons sit under the boxes and screens, as the guest's HUD layer does.
		layer.AddChild(new Fable3WorldIcons { Name = "WorldIcons" });
		layer.AddChild(new Fable3Screens { Name = "Screens" });
		layer.AddChild(new Fable3Options { Name = "Options" });
		layer.AddChild(new Fable3StatedScreen { Name = "StatedScreen" });
		layer.AddChild(new Fable3Boxes(natives) { Name = "Boxes" });
		// Over the HUD, as Fable 2's toast stands above every presentation.
		layer.AddChild(new Fable3Achievements { Name = "Achievements" });
		_splash = new Fable3Splash { Name = "Splash" };
		layer.AddChild(_splash);
		layer.AddChild(new Fable3Culture { Name = "Culture" });

		var loading = new Godot.CanvasLayer { Name = "Fable3Loading", Layer = LoadingLayer };
		loading.AddChild(new Fable3LoadingScreen { Name = "LoadingScreen" });
		layer.AddChild(loading);
		return layer;
	}

	public override Refii.Text.Engine_BabelDatabase? Strings => Fable3Strings.Current;

	/// <summary>The `hud.bgf` widgets Fable 3's own surfaces draw rather than the shared HUD view (<see cref="Fable3Hud.Owned"/>).</summary>
	public override System.Collections.Generic.IReadOnlyCollection<string> HudOwnedSubtrees => Fable3Hud.Owned;

	/// <summary>
	/// Fable 3 rolls no weather. Precipitation is four floats of the evaluated theme block
	/// (Snow Size, Snow Fall Speed, Rain Size, Rain Density at T'+0xD4..+0xE0; the rain builder
	/// sub_82C8FFE8 reads scene+236/240, the snow builder sub_82C91F28 scene+228/232) and a
	/// scripted change of weather is a scripted THEME (Lua BlendToEnvironmentTheme, sub_8248FEA8).
	/// RefiiHostGetWeather states the block, so every level follows the theme.
	/// </summary>
	public override Refii.Level.DynamicWeatherRule? WeatherRule(Refii.Level.LevelEntry level) =>
		Refii.Level.DynamicWeatherRule.Theme with { Reason = "fable 3 states precipitation in its theme block" };

	public override bool HandleBusLine(int kind, string fields)
	{
		if (kind != Fable3Splash.StateKind)
		{
			return false;
		}

		if (_splash is not null && Godot.GodotObject.IsInstanceValid(_splash))
		{
			_splash.Stated(fields);
		}

		return true;
	}
}
