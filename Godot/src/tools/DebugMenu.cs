using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Launcher.Tools;

/// <summary>
/// Debug front-end: a mouse-clickable menu that posts guest front-end ACTIONS by name via
/// <see cref="HostBridge.PostFrontEndAction"/>. The action names ARE the contract — they go
/// through the guest's own strcmp dispatcher, so anything the real menu can do can be posted
/// here, and unknown names are ignored. Actions are safe to post any time: the guest
/// dispatcher drops what it does not handle, and StartNewGame re-queues until the front end is
/// up. The panel is built entirely in code, so the script embeds anywhere — the standalone
/// debug_menu.tscn fixture, or GameUi in place of the main menu.
///
/// Evolves into a team debug menu: add a (label, action) entry below, or a second column,
/// without touching the bridge or the guest.
/// </summary>
public partial class DebugMenu : Control
{
	private Label _status = null!;
	private string _lastAction = string.Empty;

	public override void _Ready()
	{
		// OUT OF THE HUD'S CORNER. This sat at the top LEFT, which is exactly where the health
		// bar anchors — so every in-game screenshot showed "no HUD" when the HUD was drawing
		// underneath the panel. The right edge is the one corner the single-player HUD leaves
		// free (co-op health lives there, and there is none in single player).
		var panel = new PanelContainer();
		panel.AnchorLeft = 1f;
		panel.AnchorRight = 1f;
		panel.GrowHorizontal = GrowDirection.Begin;
		panel.OffsetLeft = -300f;
		panel.OffsetRight = -24f;
		panel.OffsetTop = 24f;

		var vbox = new VBoxContainer();
		vbox.AddChild(new Label { Text = "Refii Debug Menu" });

		_status = new Label { Text = "guest running: ..." };
		vbox.AddChild(_status);
		vbox.AddChild(new HSeparator());

		var actions = new VBoxContainer();
		foreach ((string label, string action) in MenuEntries())
		{
			var button = new Button { Text = label, FocusMode = FocusModeEnum.None };
			button.Pressed += () => Post(action);
			actions.AddChild(button);
		}
		vbox.AddChild(actions);

		panel.AddChild(vbox);
		AddChild(panel);
	}

	public override void _Process(double delta)
	{
		var init = HostBridge.GetInitMarkers();
		var loading = HostBridge.GetLoadingScreen();
		var level = HostBridge.GetLevelChange();
		_status.Text =
			$"guest running: {HostBridge.IsRunning}   front end: 0x{HostBridge.FrontEndState():X}" +
			$"\ninit: gdb={init.GdbManager:X} inGame={init.InGame} game=0x{init.GameMode:X} fe=0x{init.FrontEndMode:X}" +
			$"\nworld active: {HostBridge.WorldIsActive()}" +
			(loading is { } ls
				? $"   loading: mgr=0x{ls.Manager:X} screen=0x{ls.Screen:X} fade={ls.Fade:F2} req={ls.Requested} done={ls.Done}"
				: "   loading: none") +
			(level is { } lc
				? $"   level: {lc.World}/{lc.Level} state={lc.State} full={lc.FullLoad}"
				: "") +
			(_lastAction.Length != 0 ? $"\nlast action: {_lastAction}" : "");
	}

	private void Post(string action)
	{
		HostBridge.PostFrontEndAction(action);
		_lastAction = action;
		GD.Print($"[debug-menu] posted '{action}'");
	}

	private static IEnumerable<(string, string)> MenuEntries()
	{
		yield return ("New Game (Male)", Engine_FrontEndCardChoice.BoyAction);
		yield return ("New Game (Female)", Engine_FrontEndCardChoice.GirlAction);
		yield return ("Load Save 0", "LoadSave:0");
		yield return ("Load Save 1", "LoadSave:1");
		yield return ("Load Save 2", "LoadSave:2");
	}
}
