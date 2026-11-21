using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.Game;

/// <summary>
/// The dev menu, toggled with F6: the full diagnostic view beside F5's spot-check readout —
/// frame timings and hitches, every measured phase, the GPU passes, host and guest threads,
/// the guest's pacing and UI state, memory, the live logs and the cvars.
/// </summary>
/// <remarks>
/// Read-mostly and in-process: every tab reads the same sources the debug channel serves
/// (`Engine_FramePhases`, `Engine_GpuPasses`, `HostBridge`), so a number here and in an MCP
/// reply are the same number. Nothing is sampled while the menu is closed, and only the tab on
/// screen refreshes; the frame graph samples every frame while the menu is open so it has a
/// history when its tab is picked.
/// </remarks>
public partial class GameDevMenu : CanvasLayer
{
	/// <summary>
	/// True while a dev-menu text field has the keyboard, so typing a filter does not walk the
	/// hero — <see cref="GameInput"/> drops its keyboard buttons while this holds.
	/// </summary>
	public static bool KeyboardCaptured { get; private set; }

	/// <summary>Whether the menu's window is open — the one embedded window, which wants the cursor.</summary>
	public static bool WindowOpen { get; private set; }

	private readonly List<GameDevTab> _tabs = new();
	private readonly Dictionary<GameDevTab, double> _sinceRefresh = new();
	private TabContainer? _container;

	/// <summary>
	/// A real (embedded) window: dragged by its title bar, resized from any edge or corner,
	/// closed with its button or F6. It keeps its size and place for the session.
	/// </summary>
	private Window? _window;

	public override void _Ready()
	{
		// Under F3's readout (128), so the spot check stays readable over the menu.
		Layer = 127;

		// From startup, so the Errors tab has the whole run whenever it is opened.
		GameDevErrorLog.Install();
		ProcessMode = ProcessModeEnum.Always;
	}

	private void EnsureWindow()
	{
		if (_window is not null && GodotObject.IsInstanceValid(_window))
		{
			return;
		}

		Vector2I screen = (Vector2I)GetViewport().GetVisibleRect().Size;
		_window = new Window
		{
			Title = "Dev menu   ·   F6 close   ·   F5 spot check",
			Visible = false,
			Unresizable = false,
			Transient = false,
			Exclusive = false,
			WrapControls = false,
			MinSize = new Vector2I(520, 320),
			Size = new Vector2I(
				System.Math.Max(900, (int)(screen.X * 0.6f)), System.Math.Max(560, (int)(screen.Y * 0.6f))),
			Position = new Vector2I(40, 60),
		};
		_window.CloseRequested += Close;
		_window.WindowInput += @event =>
		{
			if (IsToggle(@event))
			{
				Close();
				_window.SetInputAsHandled();
			}
		};
		AddChild(_window);

		var backdrop = new Panel
		{
			AnchorLeft = 0f, AnchorTop = 0f, AnchorRight = 1f, AnchorBottom = 1f,
		};
		var style = new StyleBoxFlat { BgColor = new Color(0.05f, 0.06f, 0.07f, 0.94f) };
		backdrop.AddThemeStyleboxOverride("panel", style);
		_window.AddChild(backdrop);

		var margin = new MarginContainer
		{
			AnchorLeft = 0f, AnchorTop = 0f, AnchorRight = 1f, AnchorBottom = 1f,
		};
		margin.AddThemeConstantOverride("margin_left", 10);
		margin.AddThemeConstantOverride("margin_right", 10);
		margin.AddThemeConstantOverride("margin_top", 6);
		margin.AddThemeConstantOverride("margin_bottom", 8);
		_window.AddChild(margin);

		_container = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		margin.AddChild(_container);

		Add(new GameDevTabFrame());
		Add(new GameDevTabPhases());
		Add(new GameDevTabGpu());
		Add(new GameDevTabThreads());
		foreach (GameDevTab tab in Refii.Core.Engine_TitleGame.Active.GuestDevTabs())
		{
			Add(tab);
		}

		Add(new GameDevTabNetwork());
		Add(new GameDevTabMemory());
		Add(new GameDevTabMonitors());
		Add(new GameDevTabScene());
		Add(new GameDevTabErrors());
		Add(new GameDevTabLog());
		Add(new GameDevTabCvars());

		// The game's own Tracy capture. With `tracy` off the one tab says how to turn it on;
		// GameSession, an earlier sibling, has already read the setting by now.
		Add(new GameDevTabTracy());
		if (GameTracy.Enabled)
		{
			Add(new GameDevTabTracyZones());
			Add(new GameDevTabTracySamples());
			Add(new GameDevTabTracyPlots());
			Add(new GameDevTabTracyLocks());
		}
	}

	private void Add(GameDevTab tab)
	{
		tab.Name = tab.Title;
		_tabs.Add(tab);
		_sinceRefresh[tab] = double.MaxValue;
		_container!.AddChild(tab);
	}

	private static bool IsToggle(InputEvent @event) =>
		@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F6 };

	/// <summary>Whether the diagnostic window is currently visible.</summary>
	public bool IsOpen => _window is { Visible: true };

	private bool Open => IsOpen;

	private void Close()
	{
		if (_window is null)
		{
			return;
		}

		_window.Hide();
		WindowOpen = false;
		KeyboardCaptured = false;
	}

	public override void _Input(InputEvent @event)
	{
		if (!IsToggle(@event))
		{
			return;
		}

		Toggle();
		GetViewport().SetInputAsHandled();
	}

	/// <summary>Uses the same open, refresh and keyboard-release lifecycle for keyboard and touch.</summary>
	public void Toggle()
	{
		EnsureWindow();
		if (_window is null) return;
		if (Open)
		{
			Close();
		}
		else
		{
			_window.Show();
			WindowOpen = true;
			foreach (GameDevTab tab in _tabs)
			{
				_sinceRefresh[tab] = double.MaxValue;
			}
		}

	}

	/// <summary>Releases keyboard capture when the scene owning the diagnostic window ends.</summary>
	public override void _ExitTree() => KeyboardCaptured = false;

	public override void _Process(double delta)
	{
		if (!Open || _container is null)
		{
			return;
		}

		KeyboardCaptured = _window!.HasFocus() &&
			_window.GuiGetFocusOwner() is LineEdit or TextEdit;

		foreach (GameDevTab tab in _tabs)
		{
			tab.Tick(delta);
		}

		if (_container.GetCurrentTabControl() is GameDevTab current)
		{
			double since = _sinceRefresh[current] + delta;
			if (since >= current.RefreshSeconds)
			{
				since = 0.0;
				current.Refresh();
			}

			_sinceRefresh[current] = since;
		}
	}
}

/// <summary>One page of the dev menu.</summary>
public abstract partial class GameDevTab : VBoxContainer
{
	/// <summary>The tab's caption.</summary>
	public abstract string Title { get; }

	/// <summary>How often <see cref="Refresh"/> runs while this tab is on screen.</summary>
	public virtual double RefreshSeconds => 0.5;

	/// <summary>Re-reads the tab's sources. Only while the tab is the one showing.</summary>
	public abstract void Refresh();

	/// <summary>
	/// The game's viewport — the root, where the world draws. A tab's own
	/// <see cref="Node.GetViewport"/> is the menu's embedded window, which draws only the menu.
	/// </summary>
	protected Viewport GameViewportNode => GetTree().Root;

	/// <summary>Every frame the menu is open, whichever tab shows — for sampled history.</summary>
	public virtual void Tick(double delta)
	{
	}

	/// <summary>A dimmed caption above a section.</summary>
	protected Label Section(string text)
	{
		var label = new Label { Text = text, Modulate = new Color(0.7f, 0.85f, 1f) };
		label.AddThemeFontSizeOverride("font_size", 15);
		AddChild(label);
		return label;
	}

	/// <summary>A monospaced multi-line readout.</summary>
	protected static Label Readout()
	{
		var label = new Label();
		label.AddThemeFontOverride("font", new SystemFont { FontNames = new[] { "monospace" } });
		label.AddThemeFontSizeOverride("font_size", 14);
		return label;
	}
}
