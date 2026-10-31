using System;
using Godot;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// Photo mode under the touch controls: the buttons and gestures that reach it from outside the
/// pad (open, back, show the panel, a camera drag, the touch D-pad), and where its panel stands
/// so the touch layer leaves a finger there to the panel. <see cref="GamePhotoMode"/> reads these
/// where it reads the pad and the keyboard.
/// </summary>
public partial class GamePhotoMode
{
    /// <summary>Whether the photo panel (or its controls help) is on the screen.</summary>
    public static bool PanelShown { get; private set; }

    /// <summary>
    /// The shown panel's rectangle in the root viewport's units, for a touch layer to leave a finger
    /// there to the panel; null while it is hidden.
    /// </summary>
    public static Rect2? PanelRect { get; private set; }

    private static bool _openRequested, _showRequested;
    private static Vector2 _touchLook;

    /// <summary>Opens photo mode on the next frame, where L3 + R3 or F11 would (a touch button).</summary>
    public static void RequestOpen() => _openRequested = true;

    private static bool _backRequested;

    /// <summary>Presses photo mode's B once, from outside the pad: the phone's back gesture.</summary>
    public static void RequestBack() => _backRequested = true;

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

    /// <summary>States where the panel stands for the touch layer (<see cref="PanelRect"/>).</summary>
    private void StatePanel()
    {
        PanelShown = Active && !_capturing && _controls is { Visible: true } && _nativeControls is not null;
        Rect2? rect = PanelShown ? PanelRectFor(GetViewport().GetVisibleRect().Size, _nativeControls!.HelpOpen) : null;
        PanelRect = rect;
    }

    /// <summary>A tap on the camera (<see cref="ShowPanel"/>): back from the controls help, or the hidden panel shown again.</summary>
    private void ShowRequestedPanel()
    {
        if (_showRequested)
        {
            _showRequested = false;
            if (_nativeControls?.HelpOpen == true) SetHelpOpen(false);
            else if (_controls is { Visible: false }) ToggleControls();
        }
    }

    /// <summary>
    /// Turns the camera by the touchscreen drags posted since the last frame
    /// (<see cref="PostTouchLook"/>): a drag turns by its own angle, where the pad and mouse turn
    /// inside Engine_FreeCamera.Step.
    /// </summary>
    private void ApplyTouchLook()
    {
        _yaw -= _touchLook.X;
        _pitch = Math.Clamp(_pitch - _touchLook.Y, -1.55f, 1.55f);
        _touchLook = Vector2.Zero;
    }

    /// <summary>
    /// The touch controls' stick and Raise/Lower as a camera move, in the free camera's axes
    /// (the guest's stick is up positive, the camera's forward negative).
    /// </summary>
    private static (float X, float Y, float Z) TouchMove()
    {
        GameInput.TouchHold touch = GameInput.Touch;
        return (touch.LeftX / 32767f, (touch.RightTrigger - (float)touch.LeftTrigger) / 255f, -touch.LeftY / 32767f);
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
