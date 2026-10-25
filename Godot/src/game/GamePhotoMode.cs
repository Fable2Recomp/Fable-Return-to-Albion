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
    public static bool Active { get; private set; }
    private readonly Engine_PhotoMode _settings = new();
    private readonly List<CanvasLayer> _hiddenLayers = new();
    private GameSession? _session;
    private Camera3D? _camera;
    private Transform3D _originalTransform;
    private float _originalFov;
    private float _yaw;
    private float _pitch;
    private Vector3 _position;
    private bool _sessionPaused;
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
        if (!Active)
        {
            if (toggle && GameInput.Playing && GameUi.WorldInteractionAllowed &&
                !GameDevMenu.WindowOpen && !GameDevMenu.KeyboardCaptured &&
                !GameGoldenCock.OwnsInput && !GameMorphMirror.OwnsInput &&
                GameGoldenCock.SessionAllowsInteraction(CoopSession.State, CoopSession.Side, CoopSession.LocalCoop))
                Open();
            return;
        }
        if (_session is null || !IsInstanceValid(_session) || !HostBridge.IsInGame ||
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
        _yaw -= turn.X * seconds * 1.8f + _mouseLook.X * 0.0025f;
        _pitch = Math.Clamp(_pitch - turn.Y * seconds * 1.8f - _mouseLook.Y * 0.0025f, -1.55f, 1.55f);
        _mouseLook = Vector2.Zero;
        Basis basis = Basis.FromEuler(new Vector3(_pitch, _yaw, Mathf.DegToRad(_settings[2])));
        var move = new Vector3(Axis(pad, JoyAxis.LeftX) + KeyAxis(Key.A, Key.D),
            Trigger(pad, JoyAxis.TriggerRight) - Trigger(pad, JoyAxis.TriggerLeft) + KeyAxis(Key.Q, Key.E),
            Axis(pad, JoyAxis.LeftY) + KeyAxis(Key.W, Key.S));
        if (move.LengthSquared() > 1) move = move.Normalized();
        float speed = _settings[3] * (Held(Key.Shift) || fast ? 3f : Held(Key.Ctrl) ? 0.2f : 1f);
        _position += (basis.X * move.X + Vector3.Up * move.Y + basis.Z * move.Z) * speed * seconds;
        _camera!.GlobalTransform = new Transform3D(basis, _position);
    }

    public bool Open()
    {
        if (Active || GameSession.Current is not { Paused: false } session ||
            GetViewport().GetCamera3D() is not { } camera || !HostBridge.IsInGame) return false;
        return Begin(session, camera);
    }

    private bool Begin(GameSession session, Camera3D camera)
    {
        _session = session;
        _camera = camera;
        _originalTransform = camera.GlobalTransform;
        _originalFov = camera.Fov;
        _sessionPaused = session.Paused;
        _mouseMode = Input.MouseMode;
        _closeRequested = false;
        _held = uint.MaxValue;
        _repeat = 0;
        _generation++;
        Active = true;
        try
        {
            if (HostBridge.TryGetAbiVersion() is not null) GameInput.ReleaseForPhotoMode();
            session.Paused = true;
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

    private void HideLayers(Node node)
    {
        if (node == this || node is Refii.Fx.Engine_FxDisplacement) return;
        if (node is CanvasLayer { Visible: true } layer)
        {
            _hiddenLayers.Add(layer);
            layer.Visible = false;
        }
        foreach (Node child in node.GetChildren()) HideLayers(child);
    }

    public void Close()
    {
        if (!Active || _session is null) return;
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
        if (IsInstanceValid(_session)) _session.Paused = _sessionPaused;
        Input.MouseMode = _mouseMode;
        if (HostBridge.TryGetAbiVersion() is not null) GameInput.ReleaseForPhotoMode();
        _session = null;
        _camera = null;
        _mouseLook = Vector2.Zero;
        _capturing = false;
        Active = false;
        Engine_UiKeyGlyphs.Update();
    }

    public override void _ExitTree() => Close();

    public override void _Input(InputEvent input)
    {
        if (!Active) return;
        if (input is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true } ||
            input is InputEventMouseMotion moved && moved.Relative.LengthSquared() > 4)
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
            GetViewport().SetInputAsHandled();
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

    private void BuildControls()
    {
        var theme = _session?.GetParent().GetNodeOrNull<GameUi>("GameUi")?.PhotoModeTheme
            ?? throw new InvalidOperationException("Fable 2's Photo Mode UI is not ready.");
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
    }

    private async Task Capture()
    {
        if (!Active || _capturing || _controls is null || _nativeControls?.HelpOpen == true) return;
        _capturing = true;
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
            string path = Path.Combine(folder, $"Fable2_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}_{Guid.NewGuid().ToString("N")[..6]}.png");
            using Image photo = GetViewport().GetTexture().GetImage();
            bool linear = photo.GetFormat() is Image.Format.Rgbah or Image.Format.Rgbaf;
            photo.Convert(Image.Format.Rgba8);
            if (linear) photo.LinearToSrgb();
            Error result = photo.SavePng(path);
            if (result != Error.Ok) throw new IOException($"PNG could not be saved ({result}).");
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
        if (Held(Key.Escape) || Pad(pad, JoyButton.B)) buttons |= 2;
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
        return buttons;
    }
}
