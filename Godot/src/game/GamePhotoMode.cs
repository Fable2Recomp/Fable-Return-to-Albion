using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Refii.Core;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// Photo mode: L3 + R3 or F11 holds the world and hands the camera to the player. The hold is the
/// engine's own pause latch (<see cref="Engine_StatedPause.HostHold"/>) beside
/// <see cref="GameSession.Paused"/>, so the drawn world stands exactly as it does under the guest's
/// pause menu and everything else — the UI, the debug channel, the mods, the audio — keeps running.
/// The camera is placed here first each frame (ProcessPriority -100); <see cref="GameWorld"/> leaves
/// it alone while <see cref="Active"/> and reads the guest's statement again once it is handed back.
/// </summary>
public partial class GamePhotoMode : Node
{
    private const uint ControlsButton = 1u << 12;

    /// <summary>
    /// A canvas layer in this group stays up while photo mode hides the others: the host's touch
    /// controls, which hide themselves while a photo is taken (<see cref="Capturing"/>).
    /// </summary>
    public const string KeepLayerGroup = "photo_mode_keep";

    public static bool Active { get; private set; }

    /// <summary>Whether a photo is being taken: every overlay must be off the screen.</summary>
    public static bool Capturing { get; private set; }

    /// <summary>Whether the photo panel (or its controls help) is on the screen.</summary>
    public static bool PanelShown { get; private set; }

    /// <summary>
    /// The shown panel's rectangle in the root viewport's units, for a touch layer to leave a finger
    /// there to the panel; null while it is hidden.
    /// </summary>
    public static Rect2? PanelRect { get; private set; }

    private static bool _openRequested, _showRequested;
    private static Vector2 _touchLook;
    private readonly Engine_PhotoMode _settings = new();
    private readonly List<CanvasLayer> _hiddenLayers = new();
    private GameSession? _session;

    /// <summary>
    /// For a host that steps its guest without a <see cref="GameSession"/> (Android's own session
    /// thread): holds the guest still while true, as <see cref="GameSession.Paused"/> does. Null on
    /// the desktop, where the session is held.
    /// </summary>
    public Action<bool>? HoldGuest { get; set; }

    /// <summary>Lets the guest run again as it was before photo mode held it.</summary>
    private Action? _release;
    private Camera3D? _camera;
    private Transform3D _originalTransform;
    private float _originalFov;
    private float _yaw;
    private float _pitch;
    private Vector3 _position;
    private Input.MouseModeEnum _mouseMode;
    private CanvasLayer? _effects;
    private CanvasLayer? _controls;
    private ShaderMaterial? _filter;
    private Engine_UiPhotoMode? _nativeControls;
    private Camera3D? _uiCamera;
    private SubViewportContainer? _uiContainer;
    private Vector2 _viewSize;
    private Vector2 _mouseLook;
    private ulong _lastUsec;
    private uint _held;
    private double _repeat;
    private bool _closeRequested;
    private bool _capturing;
    private int _generation;

    public static string? KeyLabel(string button) => button switch
    {
        "A" => "Enter", "B" => "Esc", "X" => "R", "Y" => "H", "LB" => "PgUp", "RB" => "PgDn",
        "LT" => "Q", "RT" => "E", "LS" => "WASD", "RS" => "RMB", "DPad" => "Arrows",
        "Start" => "F11", "Back" => "C", _ => null
    };

    /// <summary>Opens photo mode on the next frame, where L3 + R3 or F11 would (a touch button).</summary>
    public static void RequestOpen() => _openRequested = true;

    private static bool _backRequested;

    /// <summary>Presses photo mode's B once, from outside the pad: the phone's back gesture.</summary>
    public static void RequestBack() => _backRequested = true;

    /// <summary>
    /// Why photo mode would not open now, or null when it would: it holds the world still, which a
    /// co-op session (a joined peer, a client, two local players) cannot, nor a cutscene, a menu or
    /// the dev menu. A button that opens it says so rather than doing nothing.
    /// </summary>
    public static string? OpenRefusal()
    {
        // The title says whether its world is open to the player's own interactions (co-op,
        // its own menus, its decorating and mirror modes); the shell adds its own gates.
        if (!Engine_TitleGame.Active.WorldInteractionAllowed)
            return "not now";
        if (!GameInput.Playing || GameDevMenu.WindowOpen || GameDevMenu.KeyboardCaptured)
            return "not now";
        return null;
    }

    /// <summary>Brings the hidden panel back (a tap on the touchscreen, where the pad has Y).</summary>
    public static void ShowPanel() => _showRequested = true;

    /// <summary>
    /// A camera drag on the touchscreen, as a fraction of the screen's height, turning the photo
    /// camera as <see cref="GameInput.PostTouchLook"/> turns the game's: right turns right, up looks up.
    /// </summary>
    public static void PostTouchLook(Vector2 screens, float sensitivity) =>
        _touchLook += new Vector2(Mathf.DegToRad(GameInput.TouchYawPerScreen) * (GameInput.InvertCameraX ? -1f : 1f),
            Mathf.DegToRad(GameInput.TouchPitchPerScreen)) * screens * sensitivity;

    /// <summary>
    /// Where the panel stands in a viewport of <paramref name="size"/>, matching
    /// <see cref="ResizeControls"/> and <see cref="Engine_UiPhotoMode.Layout"/>: the settings at the
    /// left edge, the controls help in the middle.
    /// </summary>
    public static Rect2 PanelRectFor(Vector2 size, bool help)
    {
        float aspect = size.X / Math.Max(size.Y, 1);
        bool narrow = aspect < (Engine_UiPhotoMode.Width + 40) / 720;
        float unit = narrow ? size.X / (Engine_UiPhotoMode.Width + 40) : size.Y / 720f;
        float height = help ? Engine_UiPhotoMode.HelpHeight : Engine_UiPhotoMode.Height;
        float left = help ? (size.X - Engine_UiPhotoMode.Width * unit) / 2f : 20f * unit;
        return new Rect2(left, (size.Y - height * unit) / 2f, Engine_UiPhotoMode.Width * unit, height * unit);
    }

    public override void _Ready()
    {
        ProcessPriority = -100;
        _lastUsec = Time.GetTicksUsec();
    }

    public override void _Process(double delta)
    {
        ulong now = Time.GetTicksUsec();
        float seconds = Math.Min((now - _lastUsec) / 1_000_000f, 0.05f);
        _lastUsec = now;
        bool focused = GetWindow().HasFocus();
        int pad = GameInput.JoypadForPad(0);
        bool key = focused && GameInput.PhotoModeKeyHeld;
        bool left = focused && Pad(pad, JoyButton.LeftStick);
        bool right = focused && Pad(pad, JoyButton.RightStick);
        bool toggle = _settings.TogglePressed(key, left, right);
        bool requested = _openRequested;
        _openRequested = false;
        if (!Active)
        {
            _showRequested = false;
            _touchLook = Vector2.Zero;
            if (toggle || requested)
            {
                if (OpenRefusal() is { } refusal)
                {
                    if (requested) GD.Print($"[photo-mode] refused: {refusal}");
                }
                else
                {
                    Open();
                }
            }
            return;
        }
        if (HoldGuest is null && (_session is null || !IsInstanceValid(_session)) || !HostBridge.IsInGame ||
            _camera is null || !IsInstanceValid(_camera))
        {
            Close();
            return;
        }
        if (toggle) _closeRequested = true;
        uint buttons = focused ? ReadActions(pad) : 0;
        uint pressed = buttons & ~_held;
        _held = buttons;
        if ((pressed & 2) != 0 && _nativeControls?.HelpOpen != true) _closeRequested = true;
        if (_closeRequested)
        {
            if (!key && !left && !right && buttons == 0 && !_capturing) Close();
            return;
        }
        if (!focused)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            _mouseLook = Vector2.Zero;
            return;
        }
        _nativeControls?.RefreshDevice();
        Engine_UiKeyGlyphs.Update();
        ResizeControls();
        if (_showRequested)
        {
            // A tap on the camera: back from the controls help, or the hidden panel shown again.
            _showRequested = false;
            if (_nativeControls?.HelpOpen == true) SetHelpOpen(false);
            else if (_controls is { Visible: false }) ToggleControls();
        }
        StatePanel();
        if (HandleHelpActions(pressed)) return;
        if ((pressed & 1) != 0) _ = Capture();
        if ((pressed & 4) != 0) ResetView();
        if ((pressed & 8) != 0) ToggleControls();
        uint navigation = buttons & 0xFF0;
        _repeat -= seconds;
        if ((pressed & 0xFF0) != 0 || (navigation != 0 && _repeat <= 0))
        {
            if ((navigation & 16) != 0) _settings.Select(-1);
            if ((navigation & 32) != 0) _settings.Select(1);
            if ((navigation & 64) != 0) _settings.Adjust(_settings.Selected, -1);
            if ((navigation & 128) != 0) _settings.Adjust(_settings.Selected, 1);
            if ((navigation & 256) != 0) _settings.Adjust(0, -1);
            if ((navigation & 512) != 0) _settings.Adjust(0, 1);
            if ((navigation & 1024) != 0) _settings.Adjust(2, -1);
            if ((navigation & 2048) != 0) _settings.Adjust(2, 1);
            _repeat = (pressed & 0xFF0) != 0 ? 0.32 : 0.1;
            RefreshSettings();
        }
        MoveCamera(pad, seconds, left);
    }

    private bool HandleHelpActions(uint pressed)
    {
        if (_nativeControls?.HelpOpen == true)
        {
            if ((pressed & (ControlsButton | 3)) != 0) SetHelpOpen(false);
            Input.MouseMode = Input.MouseModeEnum.Visible;
            _mouseLook = Vector2.Zero;
            return true;
        }
        if ((pressed & ControlsButton) == 0 || _capturing) return false;
        SetHelpOpen(true);
        return true;
    }

    private void SetHelpOpen(bool open)
    {
        if (_nativeControls is null || _controls is null || _capturing) return;
        _controls.Visible = true;
        _nativeControls.SetHelpOpen(open);
        StatePanel();
        _mouseLook = Vector2.Zero;
        _repeat = 0.32;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private void MoveCamera(int pad, float seconds, bool fast)
    {
        bool looking = Input.IsMouseButtonPressed(MouseButton.Right);
        var mouse = looking ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        if (Input.MouseMode != mouse) Input.MouseMode = mouse;
        Vector2 turn = new(Axis(pad, JoyAxis.RightX), Axis(pad, JoyAxis.RightY));
        // A touchscreen drag turns by its own angle; the pad and mouse turn inside Engine_FreeCamera.Step.
        _yaw -= _touchLook.X;
        _pitch = Math.Clamp(_pitch - _touchLook.Y, -1.55f, 1.55f);
        _touchLook = Vector2.Zero;
        // The touch controls' stick and Raise/Lower, in the guest's units (stick up positive).
        GameInput.TouchHold touch = GameInput.Touch;
        var move = new Vector3(Axis(pad, JoyAxis.LeftX) + KeyAxis(Key.A, Key.D) + touch.LeftX / 32767f,
            Trigger(pad, JoyAxis.TriggerRight) - Trigger(pad, JoyAxis.TriggerLeft) + KeyAxis(Key.Q, Key.E) +
                (touch.RightTrigger - (float)touch.LeftTrigger) / 255f,
            Axis(pad, JoyAxis.LeftY) + KeyAxis(Key.W, Key.S) - touch.LeftY / 32767f);
        float speed = _settings[3] * (Held(Key.Shift) || fast ? 3f : Held(Key.Ctrl) ? 0.2f : 1f);
        _camera!.GlobalTransform = Engine_FreeCamera.Step(ref _position, ref _yaw, ref _pitch,
            move, turn, _mouseLook, seconds, speed, Mathf.DegToRad(_settings[2]));
        _mouseLook = Vector2.Zero;
    }

    public bool Open()
    {
        if (Active) return false;
        GameSession? session = HoldGuest is null ? GameSession.Current : null;
        string? why = HoldGuest is null && session is null ? "no game session"
            : session is { Paused: true } ? "the game is paused"
            : GetViewport().GetCamera3D() is null ? "no camera"
            : !HostBridge.IsInGame ? "not in a game" : null;
        if (why is not null)
        {
            GD.Print($"[photo-mode] cannot open: {why}");
            return false;
        }
        return Begin(session, GetViewport().GetCamera3D()!);
    }

    private bool Begin(GameSession? session, Camera3D camera)
    {
        _session = session;
        _camera = camera;
        _originalTransform = camera.GlobalTransform;
        _originalFov = camera.Fov;
        _mouseMode = Input.MouseMode;
        _closeRequested = false;
        _held = uint.MaxValue;
        _repeat = 0;
        _generation++;
        Active = true;
        try
        {
            if (HostBridge.TryGetAbiVersion() is not null) GameInput.ReleaseForPhotoMode();
            Hold(session);
            Engine_StatedPause.HostHold = true;
            HideLayers(GetTree().Root);
            BuildControls();
            ResetView();
            Input.MouseMode = Input.MouseModeEnum.Visible;
            return true;
        }
        catch (Exception exception)
        {
            Close();
            GD.PushError($"[photo-mode] {exception.Message}");
            return false;
        }
    }

    /// <summary>Holds the guest still: the session's pause on the desktop, the host's own hold elsewhere.</summary>
    private void Hold(GameSession? session)
    {
        if (HoldGuest is { } hold)
        {
            hold(true);
            _release = () => hold(false);
            return;
        }

        bool wasPaused = session!.Paused;
        session.Paused = true;
        _release = () =>
        {
            if (IsInstanceValid(session)) session.Paused = wasPaused;
        };
    }

    private void HideLayers(Node node)
    {
        if (node == this || node is Refii.Fx.Engine_FxDisplacement || node.IsInGroup(KeepLayerGroup)) return;
        if (node is CanvasLayer { Visible: true } layer)
        {
            _hiddenLayers.Add(layer);
            layer.Visible = false;
        }
        foreach (Node child in node.GetChildren()) HideLayers(child);
    }

    public void Close()
    {
        if (!Active) return;
        _generation++;
        if (_camera is not null && IsInstanceValid(_camera))
        {
            _camera.GlobalTransform = _originalTransform;
            _camera.Fov = _originalFov;
        }
        if (_effects is not null) { _effects.Visible = false; _effects.QueueFree(); }
        if (_controls is not null) { _controls.Visible = false; _controls.QueueFree(); }
        _effects = null;
        _controls = null;
        _filter = null;
        _nativeControls = null;
        _uiCamera = null;
        _uiContainer = null;
        foreach (CanvasLayer layer in _hiddenLayers)
            if (IsInstanceValid(layer)) layer.Visible = true;
        _hiddenLayers.Clear();
        Engine_StatedPause.HostHold = false;
        _release?.Invoke();
        _release = null;
        Input.MouseMode = _mouseMode;
        if (HostBridge.TryGetAbiVersion() is not null) GameInput.ReleaseForPhotoMode();
        _session = null;
        _camera = null;
        _mouseLook = Vector2.Zero;
        _capturing = false;
        Capturing = false;
        Active = false;
        StatePanel();
        _touchLook = Vector2.Zero;
        Engine_UiKeyGlyphs.Update();
    }

    public override void _ExitTree() => Close();

    public override void _Input(InputEvent input)
    {
        if (!Active) return;
        // The touchscreen's pointer (the emulation device) is touch, not the mouse: it keeps the touch layout.
        bool touchPointer = input is InputEventMouse && input.Device == InputEvent.DeviceIdEmulation;
        if (!touchPointer && (input is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true } ||
            input is InputEventMouseMotion moved && moved.Relative.LengthSquared() > 4))
            Engine_UiInputMode.KeyboardMouse = true;
        else if (input is InputEventJoypadButton { Pressed: true } ||
                 input is InputEventJoypadMotion axis && Math.Abs(axis.AxisValue) > 0.5f)
            Engine_UiInputMode.KeyboardMouse = false;
        if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click &&
            _controls is { Visible: true } && _nativeControls is not null && _uiCamera is not null && !_capturing)
        {
            Vector3 at = _uiCamera.ProjectRayOrigin(click.Position) - _nativeControls.Position;
            int action = _nativeControls.ActionAt(new Vector2(at.X, at.Y));
            if (action is >= 0 and < 16) Adjust(action / 2, action % 2 == 0 ? -1 : 1);
            else if (action == 16) _ = Capture();
            else if (action == 17) ResetView();
            else if (action == 18) ToggleControls();
            else if (action == 19) _closeRequested = true;
            else if (action == 20) SetHelpOpen(true);
            else if (action == 21) SetHelpOpen(false);
            GetViewport().SetInputAsHandled();
        }
        if (_nativeControls?.HelpOpen == true)
        {
            // Everything stops here while the help is up, but the touchscreen's own fingers: the
            // touch controls turn them into the pointer that taps the help's Back.
            if (input is not (InputEventScreenTouch or InputEventScreenDrag)) GetViewport().SetInputAsHandled();
            return;
        }
        if (input is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _mouseLook += motion.Relative;
            GetViewport().SetInputAsHandled();
        }
        else if (input is InputEventMouseButton { Pressed: true } wheel &&
                 wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            _settings.Adjust(1, wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1);
            RefreshSettings();
            GetViewport().SetInputAsHandled();
        }
        else if (input is InputEventKey or InputEventJoypadButton or InputEventJoypadMotion)
            GetViewport().SetInputAsHandled();
    }

    private void ResetView()
    {
        _settings.Reset(_originalFov);
        _position = _originalTransform.Origin;
        Vector3 angles = _originalTransform.Basis.GetEuler();
        _yaw = angles.Y;
        _pitch = angles.X;
        _mouseLook = Vector2.Zero;
        if (_camera is not null) _camera.GlobalTransform = _originalTransform;
        RefreshSettings();
    }

    /// <summary>The window's title UI, wherever the title added it.</summary>
    private static IGameTitleUi? FindTitleUi(Node? window)
    {
        if (window is null)
            return null;
        foreach (Node child in window.GetChildren())
            if (child is IGameTitleUi ui)
                return ui;
        return null;
    }

    private void BuildControls()
    {
        // The game's UI stands beside the session on the desktop, beside this node on Android.
        var theme = FindTitleUi(_session?.GetParent() ?? GetParent())?.PhotoModeTheme
            ?? throw new InvalidOperationException("The title's Photo Mode UI is not ready.");
        _effects = new CanvasLayer { Name = "PhotoFilter", Layer = 120 };
        AddChild(_effects);
        _filter = new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Engine/material/photo_mode.gdshader") };
        var effect = new ColorRect { Material = _filter, MouseFilter = Control.MouseFilterEnum.Ignore };
        effect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _effects.AddChild(effect);
        _controls = new CanvasLayer { Name = "PhotoControls", Layer = 121 };
        AddChild(_controls);
        _uiContainer = new SubViewportContainer { Stretch = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        _controls.AddChild(_uiContainer);
        var viewport = new SubViewport { OwnWorld3D = true, TransparentBg = true,
            GuiDisableInput = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        _uiContainer.AddChild(viewport);
        _uiCamera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal,
            Position = new Vector3(0, 0, 20000), Far = 40000, Current = true };
        viewport.AddChild(_uiCamera);
        _nativeControls = new Engine_UiPhotoMode { Name = "NativePhotoControls" };
        viewport.AddChild(_nativeControls);
        _nativeControls.Build(theme);
        Engine_UiKeyGlyphs.Update();
        _viewSize = Vector2.Zero;
        ResizeControls();
    }

    private void ResizeControls()
    {
        Vector2 size = GetViewport().GetVisibleRect().Size;
        if (size == _viewSize || _uiContainer is null || _uiCamera is null || _nativeControls is null) return;
        _viewSize = size;
        _uiContainer.Size = size;
        float aspect = size.X / Math.Max(size.Y, 1);
        bool narrow = aspect < (Engine_UiPhotoMode.Width + 40) / 720;
        _uiCamera.KeepAspect = narrow ? Camera3D.KeepAspectEnum.Width : Camera3D.KeepAspectEnum.Height;
        _uiCamera.Size = narrow ? Engine_UiPhotoMode.Width + 40 : 720;
        float width = narrow ? Engine_UiPhotoMode.Width + 40 : 720 * aspect;
        _nativeControls.Layout(width);
    }
    private void Adjust(int setting, int direction)
    {
        _settings.Adjust(setting, direction);
        RefreshSettings();
    }

    private void RefreshSettings()
    {
        if (_filter is null || _camera is null) return;
        _camera.Fov = _settings[1];
        _filter.SetShaderParameter("preset", _settings.Filter);
        _filter.SetShaderParameter("exposure", _settings[4]);
        _filter.SetShaderParameter("contrast", _settings[5]);
        _filter.SetShaderParameter("saturation", _settings[6]);
        _filter.SetShaderParameter("vignette", _settings[7]);
        _nativeControls?.ShowSettings(_settings);
    }

    private void ToggleControls()
    {
        if (_controls is not null && !_capturing && _nativeControls?.HelpOpen != true) _controls.Visible = !_controls.Visible;
        StatePanel();
    }

    /// <summary>States where the panel stands for the touch layer (<see cref="PanelRect"/>).</summary>
    private void StatePanel()
    {
        PanelShown = Active && !_capturing && _controls is { Visible: true } && _nativeControls is not null;
        Rect2? rect = PanelShown ? PanelRectFor(GetViewport().GetVisibleRect().Size, _nativeControls!.HelpOpen) : null;
        PanelRect = rect;
    }

    private async Task Capture()
    {
        if (!Active || _capturing || _controls is null || _nativeControls?.HelpOpen == true) return;
        _capturing = true;
        Capturing = true;
        StatePanel();
        int generation = _generation;
        bool visible = _controls.Visible;
        _controls.Visible = false;
        try
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (!Active || generation != _generation) return;
            string pictures = OS.GetSystemDir(OS.SystemDir.Pictures);
            if (string.IsNullOrWhiteSpace(pictures)) pictures = OS.GetUserDataDir();
            string folder = Path.Combine(pictures, "Refii");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"{Refii.Core.Engine_Title.Active.Folder}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid().ToString("N")[..6]}.png");
            using Image photo = GetViewport().GetTexture().GetImage();
            // An HDR 2D viewport holds linear colour whatever format it reads back in: a float
            // format on the desktop, eight bits on Android's mobile renderer.
            bool linear = GetViewport().UseHdr2D || photo.GetFormat() is Image.Format.Rgbah or Image.Format.Rgbaf;
            photo.Convert(Image.Format.Rgba8);
            if (linear) photo.LinearToSrgb();
            // Written by .NET, not Godot's file layer: on Android that layer cannot write the shared
            // Pictures folder an app creates its own media in, and a file written there directly is
            // indexed into the phone's gallery.
            byte[] png = photo.SavePngToBuffer();
            if (png.Length == 0) throw new IOException("PNG could not be encoded.");
            File.WriteAllBytes(path, png);
            _nativeControls?.SetStatus("Photo saved");
            GD.Print($"[photo-mode] {path}");
        }
        catch (Exception exception)
        {
            if (Active && generation == _generation) _nativeControls?.SetStatus("Photo could not be saved");
            GD.PushWarning($"[photo-mode] {exception.Message}");
        }
        finally
        {
            if (Active && generation == _generation && _controls is not null)
            {
                _controls.Visible = visible;
                _capturing = false;
            }
            Capturing = _capturing;
            StatePanel();
        }
    }

    private static bool Held(Key key) => Input.IsPhysicalKeyPressed(key);
    private static float KeyAxis(Key negative, Key positive) => (Held(positive) ? 1f : 0f) - (Held(negative) ? 1f : 0f);
    private static bool Pad(int pad, JoyButton button) => pad >= 0 && Input.IsJoyButtonPressed(pad, button);
    private static float Trigger(int pad, JoyAxis axis) => pad >= 0 ? Math.Max(0, Input.GetJoyAxis(pad, axis)) : 0;
    private static float Axis(int pad, JoyAxis axis)
    {
        float value = pad >= 0 ? Input.GetJoyAxis(pad, axis) : 0;
        return Math.Abs(value) <= 0.18f ? 0 : Math.Sign(value) * (Math.Abs(value) - 0.18f) / 0.82f;
    }

    private static uint ReadActions(int pad)
    {
        uint buttons = 0;
        if (Held(Key.Enter) || Pad(pad, JoyButton.A)) buttons |= 1;
        if (Held(Key.Escape) || Pad(pad, JoyButton.B) || _backRequested) buttons |= 2;
        _backRequested = false;
        if (Held(Key.R) || Pad(pad, JoyButton.X)) buttons |= 4;
        if (Held(Key.H) || Pad(pad, JoyButton.Y)) buttons |= 8;
        if (Held(Key.Up) || Pad(pad, JoyButton.DpadUp)) buttons |= 16;
        if (Held(Key.Down) || Pad(pad, JoyButton.DpadDown)) buttons |= 32;
        if (Held(Key.Left) || Pad(pad, JoyButton.DpadLeft)) buttons |= 64;
        if (Held(Key.Right) || Pad(pad, JoyButton.DpadRight)) buttons |= 128;
        if (Held(Key.Pageup) || Pad(pad, JoyButton.LeftShoulder)) buttons |= 256;
        if (Held(Key.Pagedown) || Pad(pad, JoyButton.RightShoulder)) buttons |= 512;
        if (Held(Key.Bracketleft)) buttons |= 1024;
        if (Held(Key.Bracketright)) buttons |= 2048;
        if (Held(Key.C) || Pad(pad, JoyButton.Back)) buttons |= ControlsButton;
        return buttons | TouchDPad();
    }

    /// <summary>
    /// The touch controls' D-pad, as the navigation bits: up and down choose a setting, left and
    /// right adjust it, held to repeat — the pad's own D-pad, under a thumb on a phone.
    /// </summary>
    internal static uint TouchDPad()
    {
        uint touch = GameInput.Touch.Buttons, bits = 0;
        if ((touch & GameInput.PadUp) != 0) bits |= 16;
        if ((touch & GameInput.PadDown) != 0) bits |= 32;
        if ((touch & GameInput.PadLeft) != 0) bits |= 64;
        if ((touch & GameInput.PadRight) != 0) bits |= 128;
        return bits;
    }
}
