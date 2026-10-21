#if TOOLS
using System;
using Godot;
using Refii.Ui;

namespace Refii.Assets.UI;

/// <summary>
/// The game's own presentation host inside the editor, for the UI tab's preview: the banks served
/// from the editor's mount (<see cref="BnkMountPlugin.FileSystem"/>, the enabled mods' layers
/// included), the fonts and string book from the data root — read off the editor's main thread —
/// and a canvas camera fitted to the preview's window the way the game fits it. The HUD, menu and
/// options views stand on it exactly as the game stands them on its own host; they are tool
/// scripts (<c>[Tool]</c>), so their <c>_Ready</c> and <c>_Process</c> run in the editor.
/// </summary>
[Tool]
public partial class UiStudioPreviewHost : Engine_UiPresentationHost
{
	/// <summary>The presentations are loaded; the views may be built.</summary>
	public event Action? HostLoaded;

	/// <summary>A line the host reports (a load failure).</summary>
	public event Action<string>? Reported;

	/// <summary>The canvas camera every view here is drawn through.</summary>
	public Camera3D? Camera { get; private set; }

	private Vector2 _fitted;

	protected override void OnLoaded()
	{
		Camera = Engine_UiSceneBuilder.ActivateCamera(this);
		HostLoaded?.Invoke();
	}

	public override void _Process(double delta)
	{
		base._Process(delta);
		if (Camera is null || !GodotObject.IsInstanceValid(Camera) || !IsInsideTree())
		{
			return;
		}

		Vector2 size = GetViewport().GetVisibleRect().Size;
		if (size != _fitted)
		{
			_fitted = size;
			Engine_UiSceneBuilder.FitCanvasCamera(Camera, size);
		}
	}

	protected override void Report(string message)
	{
		base.Report(message);
		Reported?.Invoke(message);
	}
}

/// <summary>
/// The UI tab's preview pane: a window of a chosen shape (16:9, 21:9, 4:3) rendering a
/// <see cref="UiStudioPreviewHost"/> in its own world, and the pointer in that window's pixels and
/// on the game's canvas. What stands on the host is the sub-view's: the HUD view or the menu list.
/// </summary>
[Tool]
public partial class UiStudioPreview : VBoxContainer
{
	/// <summary>The window shapes the anchors are checked against, at the game's 720 design height.</summary>
	public static readonly (string Label, Vector2I Size)[] Windows =
	{
		("16:9 — 1280×720", new Vector2I(1280, 720)),
		("21:9 — 1680×720", new Vector2I(1680, 720)),
		("4:3 — 960×720", new Vector2I(960, 720)),
		("32:9 — 2560×720", new Vector2I(2560, 720)),
	};

	public event Action? HostLoaded;

	/// <summary>The window shape changed.</summary>
	public event Action? WindowChanged;

	/// <summary>A press in the window: pixel position, button.</summary>
	public event Action<Vector2, MouseButton>? Pressed;

	/// <summary>The pointer moved with the left button down: pixel position.</summary>
	public event Action<Vector2>? Dragged;

	/// <summary>The left button came up: pixel position.</summary>
	public event Action<Vector2>? Released;

	/// <summary>A wheel notch: −1 up, +1 down.</summary>
	public event Action<int>? Wheel;

	/// <summary>The sub-view's own controls over the window.</summary>
	public HFlowContainer Toolbar { get; private set; } = null!;

	/// <summary>The sub-view's own controls under the window (the stand-in values).</summary>
	public VBoxContainer Below { get; private set; } = null!;

	public UiStudioPreviewHost? Host { get; private set; }

	private OptionButton _window = null!;
	private AspectRatioContainer _frame = null!;
	private SubViewport _viewport = null!;
	private Label _status = null!;
	private bool _dragging;

	public override void _Ready()
	{
		AddThemeConstantOverride("separation", 6);
		SizeFlagsHorizontal = SizeFlags.ExpandFill;
		SizeFlagsVertical = SizeFlags.ExpandFill;

		var bar = new HBoxContainer();
		bar.AddChild(new Label { Text = "Window" });
		_window = new OptionButton();
		foreach ((string label, _) in Windows)
		{
			_window.AddItem(label);
		}

		_window.ItemSelected += _ => ApplyWindow();
		bar.AddChild(_window);
		Toolbar = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
		bar.AddChild(Toolbar);
		AddChild(bar);

		var backdrop = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
		backdrop.AddThemeStyleboxOverride("panel", ModStudioUiStyles.CardStyle(background: new Color(0.06f, 0.06f, 0.08f)));
		_frame = new AspectRatioContainer
		{
			StretchMode = AspectRatioContainer.StretchModeEnum.Fit,
			SizeFlagsVertical = SizeFlags.ExpandFill,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
		};
		backdrop.AddChild(_frame);
		var container = new SubViewportContainer { Stretch = true, MouseFilter = MouseFilterEnum.Stop };
		container.GuiInput += OnInput;
		container.Resized += () => WindowChanged?.Invoke();
		_frame.AddChild(container);
		_viewport = new SubViewport
		{
			OwnWorld3D = true,
			HandleInputLocally = false,
			Msaa3D = Viewport.Msaa.Msaa2X,
			RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible,
		};
		container.AddChild(_viewport);
		var world = new WorldEnvironment
		{
			Environment = new Godot.Environment
			{
				BackgroundMode = Godot.Environment.BGMode.Color,
				BackgroundColor = new Color(0.10f, 0.11f, 0.13f),
				AmbientLightSource = Godot.Environment.AmbientSource.Color,
				AmbientLightColor = Colors.White,
			},
		};
		_viewport.AddChild(world);
		AddChild(backdrop);

		Below = new VBoxContainer();
		AddChild(Below);
		_status = ModStudioUiStyles.Note("The preview loads the game's UI when it is first shown.");
		AddChild(_status);
		ApplyWindow();
	}

	/// <summary>Stands the host up on first use (the banks, fonts and string book load once).</summary>
	public void EnsureHost()
	{
		if (Host is not null)
		{
			return;
		}

		if (BnkMountPlugin.Instance?.FileSystem is not { } mount)
		{
			SetStatus("The game's banks are not mounted in the editor yet — the preview starts once they are.");
			return;
		}

		// The banks' index, the fonts and the string book are read on a worker
		// (Engine_UiPresentationHost.LoadsInBackground): the editor keeps drawing while they load, and
		// the views stand up on HostLoaded.
		SetStatus("Loading the game's UI…");
		Host = new UiStudioPreviewHost
		{
			Name = "UiStudioHost",
			Mount = mount,
			GameDataRoot = BnkMountPlugin.GameDirectory,
			LoadsInBackground = true,
		};
		Host.HostLoaded += () =>
		{
			SetStatus("The game's own UI, from the editor's mount (the enabled mods' art included).");
			HostLoaded?.Invoke();
		};
		Host.Reported += message => SetStatus(message);
		_viewport.AddChild(Host);
	}

	/// <summary>Drops the host — the editor's mount was rebuilt — so the next use loads it fresh.</summary>
	public void ResetHost()
	{
		if (Host is null)
		{
			return;
		}

		Host.QueueFree();
		Host = null;
	}

	public bool Loaded => Host is { IsLoaded: true, Camera: not null };

	/// <summary>
	/// The preview window in pixels — what the views' anchors resolve against. The window takes the
	/// chosen shape at whatever size the pane allows; the HUD's anchors and scale depend on the shape.
	/// </summary>
	public Vector2 WindowSize => _viewport.Size is { X: > 0, Y: > 0 } size
		? new Vector2(size.X, size.Y)
		: Windows[Math.Max(0, _window.Selected)].Size;

	/// <summary>A pixel of the window on the game's canvas (canvas units, +Y up).</summary>
	public Vector2? CanvasAt(Vector2 pixel)
	{
		if (Host?.Camera is not { } camera || !GodotObject.IsInstanceValid(camera) || !camera.IsInsideTree())
		{
			return null;
		}

		Vector3 origin = camera.ProjectRayOrigin(pixel);
		return new Vector2(origin.X, origin.Y);
	}

	public void SetStatus(string text) => _status.Text = text;

	private void ApplyWindow()
	{
		Vector2I size = Windows[Math.Max(0, _window.Selected)].Size;
		_frame.Ratio = size.X / (float)size.Y;
		WindowChanged?.Invoke();
	}

	private void OnInput(InputEvent @event)
	{
		// The container's pixels are the window's (it stretches the window to itself).
		Vector2 container = ((Control)_viewport.GetParent()).Size;
		Vector2 scale = container.X > 0f && container.Y > 0f ? (Vector2)_viewport.Size / container : Vector2.One;
		switch (@event)
		{
			case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
				Wheel?.Invoke(-1);
				break;
			case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
				Wheel?.Invoke(1);
				break;
			case InputEventMouseButton button when button.Pressed:
				_dragging = button.ButtonIndex == MouseButton.Left;
				Pressed?.Invoke(button.Position * scale, button.ButtonIndex);
				break;
			case InputEventMouseButton button when !button.Pressed && button.ButtonIndex == MouseButton.Left:
				if (_dragging)
				{
					_dragging = false;
					Released?.Invoke(button.Position * scale);
				}

				break;
			case InputEventMouseMotion motion when _dragging:
				Dragged?.Invoke(motion.Position * scale);
				break;
		}
	}
}
#endif
