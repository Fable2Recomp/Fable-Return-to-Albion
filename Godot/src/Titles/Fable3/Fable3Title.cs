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

	/// <summary>
	/// Measured 2026-11-09 (Mistpeak camp, the guest's own cloth publish): the guest simulated and
	/// drew the morphed hero's hat and both coat pieces as cloth (3 pieces stated and drawn for the
	/// hero entity), so its morphed garments keep their cloth.
	/// </summary>
	public override bool MorphedModelsKeepCloth => true;

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
		// The map table the guest presents in place of the world, under every UI surface.
		layer.AddChild(new Fable3MapTable { Name = "MapTable" });
		// The HUD on the shared view, under every other surface as the guest's HUD layer is; its
		// screen fade stands on its own canvas layer over all of them.
		layer.AddChild(new Fable3Hud { Name = "Hud" });
		var natives = new Fable3GuiNatives { Name = "GuiNatives" };
		layer.AddChild(natives);
		// The world icons sit under the boxes and screens, as the guest's HUD layer does.
		layer.AddChild(new Fable3WorldIcons { Name = "WorldIcons" });
		layer.AddChild(new Fable3Options { Name = "Options" });
		layer.AddChild(new Fable3HostOptions { Name = "HostOptions" });
		layer.AddChild(new Fable3StatedScreen { Name = "StatedScreen" });
		layer.AddChild(new Fable3Boxes(natives) { Name = "Boxes" });
		// The console's keyboard, over every screen that asks for it.
		layer.AddChild(new Fable3Keyboard { Name = "Keyboard" });
		// Over the HUD, as Fable 2's toast stands above every presentation.
		layer.AddChild(new Fable3Achievements { Name = "Achievements" });
		_splash = new Fable3Splash { Name = "Splash" };
		layer.AddChild(_splash);
		layer.AddChild(new Fable3Culture { Name = "Culture" });
		// After every screen has read the pointer: its leftovers are the pad's.
		layer.AddChild(new Fable3PointerLeftovers { Name = "PointerLeftovers" });

		var loading = new Godot.CanvasLayer { Name = "Fable3Loading", Layer = LoadingLayer };
		loading.AddChild(new Fable3LoadingScreen { Name = "LoadingScreen" });
		layer.AddChild(loading);
		return layer;
	}

	public override Refii.Text.Engine_BabelDatabase? Strings => Fable3Strings.Current;

	public override Refii.Game.GameTitleInput Input => Fable3Input.Instance;

	public override Refii.Ui.Engine_UiKeyGlyphs.GlyphArt? GlyphArtFor(string path) => Fable3Glyphs.ArtFor(path);

	/// <summary>Player one's dog, as the game binds it to its hero (RefiiHostGetHeroDogs: EntityManager+0xB8 / +0xC0, set by sub_82399CB8, read by sub_821E8250).</summary>
	public override uint CompanionEntityAddress
	{
		get
		{
			try
			{
				return RefiiHostGetHeroDogs(out uint one, out _) == 0u ? 0u : one;
			}
			catch (System.EntryPointNotFoundException)
			{
				return 0u;
			}
		}
	}

	[System.Runtime.InteropServices.DllImport(Refii.Host.HostBridge.Library)]
	private static extern uint RefiiHostGetHeroDogs(out uint one, out uint two);

	/// <summary>
	/// What each game button does, for the Controls page's remap rows: the shipped scheme the guest
	/// loads (`GameControlsCombatOnRelease`, GDB 0x8c1585e4: sub_8294BD08 from sub_8250E798 at boot).
	/// </summary>
	public override string? PadButtonRole(Refii.Core.PadInput button) => button switch
	{
		Refii.Core.PadInput.A => "Interact, run, accept, choice 1",
		Refii.Core.PadInput.B => "Magic, back",
		Refii.Core.PadInput.X => "Melee, block, choice 2",
		Refii.Core.PadInput.Y => "Ranged, aim, choice 3",
		Refii.Core.PadInput.LB => "First person, choice 5",
		Refii.Core.PadInput.RB => "Quick menu, choice 4",
		Refii.Core.PadInput.LT => "Target lock, zoom aim",
		Refii.Core.PadInput.RT => "Look at (hold)",
		Refii.Core.PadInput.L3 => "Photo mode (with R3)",
		Refii.Core.PadInput.R3 => "Camera mode",
		Refii.Core.PadInput.Start => "The Sanctuary",
		Refii.Core.PadInput.Back => "Nickname",
		Refii.Core.PadInput.DpadUp => "Shortcut up",
		Refii.Core.PadInput.DpadDown => "Shortcut down",
		Refii.Core.PadInput.DpadLeft => "Shortcut left",
		Refii.Core.PadInput.DpadRight => "Shortcut right",
		_ => null,
	};

	/// <summary>The host options page (the Controls page) on Fable 3's own driver (<see cref="Fable3HostOptions"/>).</summary>
	public override void EnterHostOptions(Refii.Ui.Engine_UiHostOptionsPage page) => Fable3HostOptions.Enter(page);

	public override void SetOptionsCursorIndex(int row, int index) => Fable3HostOptions.SetIndex(row, index);

	public override int OptionsCursorRow => Fable3HostOptions.Cursor;

	public override int OptionsCursorIndex(int row) => Fable3HostOptions.IndexOf(row);

	/// <summary>
	/// `f3ui` (match, doc, limit): per standing presentation document, each element whose path holds
	/// the match — stated, fed and resolved (<see cref="Fable3HudDocument.Describe"/>).
	/// </summary>
	public override System.Collections.Generic.Dictionary<string, object?>? DebugRequest(
		string op, System.Text.Json.JsonElement request)
	{
		if (op != "f3ui")
		{
			return null;
		}

		string match = request.TryGetProperty("match", out System.Text.Json.JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;
		string doc = request.TryGetProperty("doc", out System.Text.Json.JsonElement d) ? d.GetString() ?? string.Empty : string.Empty;
		int limit = request.TryGetProperty("limit", out System.Text.Json.JsonElement l) ? l.GetInt32() : 60;
		var documents = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
		foreach (Fable3HudDocument document in Fable3HudDocument.Live)
		{
			if (doc.Length == 0 || document.Document == doc)
			{
				documents.Add(document.Describe(match, limit));
			}
		}

		return new System.Collections.Generic.Dictionary<string, object?> { ["ok"] = true, ["documents"] = documents };
	}

	public override System.Collections.Generic.IReadOnlyList<string> DebugOps => new[] { "f3ui" };

	private static readonly Fable3Rig Rigs = new();

	public override Refii.Animations.Engine_TitleRig Rig => Rigs;

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
