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
		// Before the HUD, whose screen fade covers it.
		layer.AddChild(new Fable3TopBox { Name = "TopBox" });
		var natives = new Fable3GuiNatives { Name = "GuiNatives" };
		layer.AddChild(natives);
		layer.AddChild(new Fable3Boxes(natives) { Name = "Boxes" });
		layer.AddChild(new Fable3Hud { Name = "Hud" });
		_splash = new Fable3Splash { Name = "Splash" };
		layer.AddChild(_splash);
		layer.AddChild(new Fable3Culture { Name = "Culture" });

		var loading = new Godot.CanvasLayer { Name = "Fable3Loading", Layer = LoadingLayer };
		loading.AddChild(new Fable3LoadingScreen { Name = "LoadingScreen" });
		layer.AddChild(loading);
		return layer;
	}

	public override Refii.Text.Engine_BabelDatabase? Strings => Fable3Strings.Current;

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
