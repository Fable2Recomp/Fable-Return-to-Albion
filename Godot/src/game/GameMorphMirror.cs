using System;
using Godot;
using Refii.Core;
using Refii.Hero;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

public partial class GameMorphMirror : Node
{
    private static GameMorphMirror? _current;
    private static Engine_MorphMirror? _actor;
    private Engine_MorphMirrorSettings? _settings;
    private CanvasLayer? _layer;
    private SubViewportContainer? _container;
    private Camera3D? _uiCamera;
    private Engine_UiMorphMirror? _ui;
    private Camera3D? _camera;
    private Transform3D _originalTransform;
    private float _originalFov;
    private float _originalHOffset;
    private Transform3D _previewTransform;
    private float _previewFov;
    private Vector2 _size;
    private Vector2 _dragOrigin;
    private float _mouseTurn;
    private float _sentRotation;
    private uint _held = uint.MaxValue;
    private uint _generation;
    private double _poll;
    private double _touch;
    private double _repeat;
    private double _rotationPoll;
    private bool _dragging;
    private bool _closing;
    private bool _ignoreActive;

    public static bool OwnsInput => _current?._settings is not null;
    public static void Register(Engine_MorphMirror actor) => _actor = actor;
    public static void Unregister(Engine_MorphMirror actor)
    {
        if (_actor != actor) return;
        _current?.Close();
        _actor = null;
    }

    public static bool ObserveGuest(HostBridge.GuestEntity entity)
    {
        if (entity.RecordHash != Engine_MorphMirrorRuntime.Record) return false;
        if (_actor is not null && IsInstanceValid(_actor)) _actor.Highlight(entity.Highlight);
        return true;
    }

    public static string? KeyLabel(string button) => button switch
    {
        "A" => "Enter", "B" => "Esc", "X" => "R", "DPad" => "Arrows", _ => null
    };

    public override void _Ready() => _current = this;

    public override void _Process(double delta)
    {
        if (_actor is null || !IsInstanceValid(_actor)) { if (OwnsInput) Close(); return; }
        if (!HostBridge.IsInGame) { if (OwnsInput) Close(); return; }
        _poll -= delta;
        if (_poll <= 0)
        {
            _poll = 0.1;
            uint generation = HostBridge.GetNewGameState().Generation;
            if (_generation != generation)
            {
                if (OwnsInput) Close();
                _generation = generation;
            }
            bool active = HostBridge.TryGetModValue(Engine_MorphMirrorRuntime.ActiveKey, out float value) && value == 1;
            if (!active) _ignoreActive = false;
            if (!active && OwnsInput) Close();
            else if (active && !OwnsInput && !_ignoreActive)
            {
                if (CanOpen()) Open();
                else { Command("End"); _ignoreActive = true; }
            }
        }
        if (_settings is null) return;
        _touch -= delta;
        if (_touch <= 0) { Command("Touch"); _touch = 1; }
        Resize();
        Engine_UiKeyGlyphs.Update();
        bool focused = GetWindow().HasFocus();
        uint buttons = focused ? ReadButtons() : 0;
        uint pressed = buttons & ~_held;
        _held = buttons;
        if (_closing)
        {
            if (buttons == 0) Close();
            return;
        }
        if ((pressed & 1) != 0) RequestClose(true);
        else if ((pressed & 2) != 0) RequestClose(false);
        else if ((pressed & 4) != 0) { _settings.Reset(); Preview(); }
        uint navigation = buttons & 0xF0;
        _repeat -= delta;
        if ((pressed & 0xF0) != 0 || navigation != 0 && _repeat <= 0)
        {
            if ((navigation & 16) != 0) _settings.Select(-1);
            if ((navigation & 32) != 0) _settings.Select(1);
            if ((navigation & 64) != 0) Adjust(_settings.Selected, -1);
            if ((navigation & 128) != 0) Adjust(_settings.Selected, 1);
            _repeat = (pressed & 0xF0) != 0 ? 0.32 : 0.1;
            _ui?.ShowSettings(_settings);
        }
        if (!_closing) Rotate(delta, focused);
    }

    private static bool CanOpen() => _actor?.Placement is { Enabled: true } && IsInstanceValid(_actor) &&
        GameSession.Current is { Paused: false } && GameUi.WorldInteractionAllowed && !GamePhotoMode.Active &&
        !GameGoldenCock.OwnsInput && !GameInput.HostUiOwnsPad && !GameDevMenu.KeyboardCaptured &&
        GameGoldenCock.SessionAllowsInteraction(CoopSession.State, CoopSession.Side, CoopSession.LocalCoop) &&
        GameGoldenCock.MenuAllowsInteraction(HostBridge.GetPauseState());

    private void Open()
    {
        try
        {
            _camera = GetViewport().GetCamera3D() ?? throw new InvalidOperationException("The game camera is unavailable.");
            var theme = GameSession.Current?.GetParent().GetNodeOrNull<GameUi>("GameUi")?.PhotoModeTheme
                ?? throw new InvalidOperationException("The native mirror menu is unavailable.");
            _originalTransform = _camera.GlobalTransform;
            _originalFov = _camera.Fov;
            _originalHOffset = _camera.HOffset;
            _settings = new Engine_MorphMirrorSettings(key => Engine_Switches.String(key, string.Empty));
            _held = uint.MaxValue;
            _closing = false;
            _touch = 0;
            _repeat = 0;
            _rotationPoll = 0;
            _sentRotation = 0;
            _mouseTurn = 0;
            _layer = new CanvasLayer { Name = "MorphMirrorMenu", Layer = 121 };
            AddChild(_layer);
            _container = new SubViewportContainer { Stretch = true, MouseFilter = Control.MouseFilterEnum.Ignore };
            _layer.AddChild(_container);
            var viewport = new SubViewport { OwnWorld3D = true, TransparentBg = true, GuiDisableInput = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            _container.AddChild(viewport);
            _uiCamera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal,
                Position = new Vector3(0, 0, 20000), Far = 40000, Current = true };
            viewport.AddChild(_uiCamera);
            _ui = new Engine_UiMorphMirror();
            viewport.AddChild(_ui);
            _ui.Build(theme);
            _ui.ShowSettings(_settings);
            _size = Vector2.Zero;
            Resize();
            GameInput.ReleaseForPhotoMode();
            Input.MouseMode = Input.MouseModeEnum.Visible;
            ApplyCamera(_camera);
        }
        catch (Exception exception)
        {
            Close();
            Command("End");
            GD.PushError("[morph-mirror] " + exception.Message);
        }
    }

    public static bool ApplyCamera(Camera3D camera)
    {
        if (!OwnsInput) return false;
        camera.GlobalTransform = _current!._previewTransform;
        camera.Fov = _current._previewFov;
        camera.HOffset = 0;
        return true;
    }

    public static Transform3D FrameCamera(Vector3 eye, Vector3 hero, float fov, float aspect, float panelFraction)
    {
        Basis basis = Basis.LookingAt(hero + Vector3.Up * 1.15f - eye, Vector3.Up);
        float yaw = MathF.Atan(panelFraction * aspect * MathF.Tan(Mathf.DegToRad(fov * 0.5f)));
        return new Transform3D(basis * new Basis(Vector3.Up, yaw), eye);
    }

    private void Resize()
    {
        Vector2 size = GetViewport().GetVisibleRect().Size;
        if (_size == size || _container is null || _uiCamera is null || _ui is null) return;
        _size = size;
        _container.Size = size;
        float width = Math.Max(Engine_UiMorphMirror.Width + 40, 720 * size.X / Math.Max(1, size.Y));
        _uiCamera.KeepAspect = width == Engine_UiMorphMirror.Width + 40 ? Camera3D.KeepAspectEnum.Width : Camera3D.KeepAspectEnum.Height;
        _uiCamera.Size = _uiCamera.KeepAspect == Camera3D.KeepAspectEnum.Width ? width : 720;
        _ui.Layout(width);
        if (_actor?.Placement is { } placement && IsInstanceValid(_actor))
        {
            Transform3D world = _actor.GlobalTransform;
            _previewFov = placement.CameraFov;
            _previewTransform = FrameCamera(world * placement.CameraPosition, world * placement.HeroPose.Origin,
                _previewFov, size.X / Math.Max(1, size.Y), (Engine_UiMorphMirror.Width + 20) / width);
        }
    }

    private void Preview()
    {
        if (_settings is null) return;
        GameCharacterMorphs.Preview(_settings.Values());
        _ui?.ShowSettings(_settings);
    }

    private void Rotate(double delta, bool focused)
    {
        if (!focused) { EndDrag(); return; }
        int pad = GameInput.JoypadForPad(0);
        _settings!.Rotate(pad >= 0 ? Input.GetJoyAxis(pad, JoyAxis.RightX) : 0, _mouseTurn, (float)delta);
        _mouseTurn = 0;
        _rotationPoll -= delta;
        if (_rotationPoll > 0 || _settings.Rotation == _sentRotation) return;
        _sentRotation = _settings.Rotation;
        _rotationPoll = 1.0 / 30;
        HostBridge.PostModCommand(FormattableString.Invariant($"publish {Engine_MorphMirrorRuntime.RotationKey} {_sentRotation:R}"));
    }

    private void EndDrag()
    {
        _mouseTurn = 0;
        if (!_dragging) return;
        _dragging = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        if (GetWindow().HasFocus()) Input.WarpMouse(_dragOrigin);
    }

    private void Adjust(int row, int direction)
    {
        if (_settings?.Adjust(row, direction) == true) Preview();
    }

    private void RequestClose(bool save)
    {
        if (_settings is null || _closing) return;
        if (save && !Engine_UiScreenActions.WriteSettings(_settings.Values()))
        {
            _ui?.SetStatus("Could not save. Try Apply again.");
            return;
        }
        _closing = true;
        EndDrag();
    }

    private void Close()
    {
        if (_settings is null) return;
        EndDrag();
        Command("End");
        _ignoreActive = true;
        _settings = null;
        GameCharacterMorphs.Preview(null);
        if (_camera is not null && IsInstanceValid(_camera))
        {
            _camera.GlobalTransform = _originalTransform;
            _camera.Fov = _originalFov;
            _camera.HOffset = _originalHOffset;
        }
        if (_layer is not null) { _layer.Visible = false; _layer.QueueFree(); }
        _layer = null;
        _container = null;
        _uiCamera = null;
        _ui = null;
        _camera = null;
        _closing = false;
        GameInput.ReleaseForPhotoMode();
        Engine_UiKeyGlyphs.Update();
    }

    private static void Command(string method) => HostBridge.PostModCommand("run_lua if RefiiMorphMirror then RefiiMorphMirror." + method + "() end");

    public override void _Input(InputEvent input)
    {
        if (_settings is null) return;
        if (input is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true } ||
            input is InputEventMouseMotion moved && moved.Relative.LengthSquared() > 4) Engine_UiInputMode.KeyboardMouse = true;
        else if (input is InputEventJoypadButton { Pressed: true } ||
            input is InputEventJoypadMotion axis && Math.Abs(axis.AxisValue) > 0.5f) Engine_UiInputMode.KeyboardMouse = false;
        if (!_closing && GetWindow().HasFocus())
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right } right)
            {
                if (right.Pressed)
                {
                    _dragOrigin = right.Position;
                    _dragging = true;
                    _mouseTurn = 0;
                    Input.MouseMode = Input.MouseModeEnum.Captured;
                }
                else EndDrag();
            }
            else if (_dragging && input is InputEventMouseMotion motion)
                _mouseTurn += motion.ScreenRelative.X;
        }
        if (!_closing && !_dragging && input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click && _uiCamera is not null && _ui is not null)
        {
            Vector3 at = _uiCamera.ProjectRayOrigin(click.Position) - _ui.Position;
            int action = _ui.ActionAt(new Vector2(at.X, at.Y));
            if (action == 20) RequestClose(true);
            else if (action == 21) RequestClose(false);
            else if (action == 22) { _settings.Reset(); Preview(); }
            else if (action >= 0) Adjust(action / 2, action % 2 == 0 ? -1 : 1);
        }
        if (input is InputEventKey or InputEventMouseButton or InputEventMouseMotion or InputEventJoypadButton or InputEventJoypadMotion)
            GetViewport().SetInputAsHandled();
    }

    private static uint ReadButtons()
    {
        int pad = GameInput.JoypadForPad(0);
        bool Down(Key key, JoyButton button) => Input.IsPhysicalKeyPressed(key) || pad >= 0 && Input.IsJoyButtonPressed(pad, button);
        uint buttons = 0;
        if (Down(Key.Enter, JoyButton.A)) buttons |= 1;
        if (Down(Key.Escape, JoyButton.B)) buttons |= 2;
        if (Down(Key.R, JoyButton.X)) buttons |= 4;
        if (Down(Key.Up, JoyButton.DpadUp)) buttons |= 16;
        if (Down(Key.Down, JoyButton.DpadDown)) buttons |= 32;
        if (Down(Key.Left, JoyButton.DpadLeft)) buttons |= 64;
        if (Down(Key.Right, JoyButton.DpadRight)) buttons |= 128;
        return buttons;
    }

    public override void _ExitTree()
    {
        Close();
        if (_current == this) _current = null;
    }
}
