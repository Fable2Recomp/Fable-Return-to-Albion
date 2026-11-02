using Godot;
using Refii.Game;
using Refii.Level;
using Refii.Ui;

namespace Refii.Titles.Fable2;

/// <summary>
/// Decorating under the touch controls. A finger is the mouse here, not the pad: a tap on a piece
/// picks it up and a tap on the room puts the held piece down there (the mouse's click), a drag
/// that starts on the held piece slides it (the mouse's drag); the stick, the camera drag and
/// Raise/Lower fly the camera as in photo mode, and Turn, Item and Place work the held piece.
/// The touch controls post here; <see cref="GameDecorate"/> reads it where it reads the keyboard.
/// </summary>
public partial class GameDecorate
{
    /// <summary>The panel's rectangle in the root viewport's units while it is shown, for the touch layer to leave a finger there to it.</summary>
    public static Rect2? PanelRect => Active ? _panelRect : null;

    /// <summary>Whether the panel is shown (not hidden for a clear view).</summary>
    public static bool PanelShown => Active && _panelRect is not null;

    /// <summary>Whether a piece is held: Place, Turn and Item stand.</summary>
    public static bool Placing => Active && _placing;

    private static Rect2? _panelRect, _pieceRect;
    private static bool _placing, _backRequested, _showRequested;
    private static Vector2? _tapAt, _grabAt, _dragTo;
    private static Vector2 _touchLook;
    private static uint _touchHeld;
    private Vector2? _touchCursor;

    /// <summary>Presses decoration's B once, from outside the pad: the phone's back gesture. With nothing to back out of, it leaves.</summary>
    public static void RequestBack() => _backRequested = Active;

    /// <summary>Brings the hidden panel back (the Panel button).</summary>
    public static void ShowPanel() => _showRequested = Active;

    /// <summary>A tap on the room, in the root viewport's units: the mouse's click there.</summary>
    public static void TouchTap(Vector2 at) { if (Active) _tapAt = at; }

    /// <summary>
    /// A finger landing at <paramref name="at"/>: true when it is on the held piece, and the
    /// decoration has it from now on (<see cref="TouchDrag"/>), as the mouse's drag.
    /// </summary>
    public static bool TouchGrab(Vector2 at)
    {
        if (!Active || _pieceRect is not { } piece || !piece.HasPoint(at)) return false;
        _grabAt = at; _dragTo = null;
        return true;
    }

    /// <summary>The grabbing finger moved to <paramref name="at"/>.</summary>
    public static void TouchDrag(Vector2 at) { if (Active) _dragTo = at; }

    /// <summary>
    /// A camera drag on the touchscreen, as a fraction of the screen's height, turning the camera as
    /// photo mode's does, posted as the mouse pixels Engine_FreeCamera turns by (0.0025 rad each).
    /// </summary>
    public static void PostTouchLook(Vector2 screens, float sensitivity)
    {
        if (!Active) return;
        _touchLook += new Vector2(Mathf.DegToRad(GameInput.TouchYawPerScreen) * (GameInput.InvertCameraX ? -1f : 1f),
            Mathf.DegToRad(GameInput.TouchPitchPerScreen)) * screens * sensitivity / 0.0025f;
    }

    /// <summary>The touch controls' Place, as the decoration's A.</summary>
    private static uint TouchButtons() => (GameInput.Touch.Buttons & GameInput.PadA) != 0 ? 1u : 0u;

    /// <summary>The touch stick and Raise/Lower as a camera move, in the keyboard's axes (forward negative).</summary>
    private static Vector3 TouchMove()
    {
        GameInput.TouchHold touch = GameInput.Touch;
        return new Vector3(touch.LeftX / 32767f, (touch.RightTrigger - (float)touch.LeftTrigger) / 255f, -touch.LeftY / 32767f);
    }

    /// <summary>Item up and down, as the keyboard's R and F.</summary>
    private static float TouchHeight()
    {
        uint buttons = GameInput.Touch.Buttons;
        return ((buttons & GameInput.PadUp) != 0 ? 1 : 0) - ((buttons & GameInput.PadDown) != 0 ? 1 : 0);
    }

    /// <summary>Turn left and right held, as the pad's LB and RB.</summary>
    private static float TouchTurn(float seconds, bool fine)
    {
        uint buttons = GameInput.Touch.Buttons;
        return Engine_DecorateControls.Rotation((buttons & GameInput.PadLeftShoulder) != 0,
            (buttons & GameInput.PadRightShoulder) != 0, seconds, fine);
    }

    /// <summary>Where the decoration's cursor is: under touch the last finger on the room, else the mouse.</summary>
    private Vector2 TouchCursor(Vector2 mouse) => Engine_UiInputMode.Touch ? _touchCursor ?? _size * 0.5f : mouse;

    /// <summary>The back gesture as the button it presses: B, or with nothing to back out of, R3 to show a hidden panel, else Start to leave.</summary>
    private uint TouchBack()
    {
        if (!_backRequested) return 0;
        _backRequested = false;
        return _editingRotation || _ui?.HelpOpen == true || HasSelection || _waiting || !_room ? 2u : _uiHidden ? 256u : 512u;
    }

    /// <summary>
    /// States the panel and the held piece for the touch layer, and brings the panel back when
    /// asked. Every frame, before the edits and the help return.
    /// </summary>
    private void StateTouch()
    {
        if (_showRequested) { _showRequested = false; if (_uiHidden) SetUiHidden(false); }
        _placing = HasSelection;
        _panelRect = null;
        if (!_uiHidden && _ui is not null && _uiCamera is not null)
        {
            Rect2 bounds = _ui.PanelBounds;
            Vector2 a = _uiCamera.UnprojectPosition(_ui.Position + new Vector3(bounds.Position.X, bounds.End.Y, 0));
            Vector2 b = _uiCamera.UnprojectPosition(_ui.Position + new Vector3(bounds.End.X, bounds.Position.Y, 0));
            _panelRect = new Rect2(a, b - a).Abs();
        }

        _pieceRect = null;
        if (HasSelection && !_waiting && !_editingRotation && _ui?.HelpOpen != true && _camera is not null && _ghost?.Mesh is { } mesh)
        {
            Aabb box = mesh.GetAabb();
            Rect2? rect = null;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = _ghost.GlobalTransform * box.GetEndpoint(i);
                if (_camera.IsPositionBehind(corner)) continue;
                Vector2 at = _camera.UnprojectPosition(corner);
                rect = rect is { } so_far ? so_far.Expand(at) : new Rect2(at, Vector2.Zero);
            }

            // A finger lands a little wide, and a small piece far off is still a finger's target.
            if (rect is { } seen) _pieceRect = seen.Grow(_size.Y * 0.04f);
        }

        if (_ui?.HelpOpen == true || _editingRotation || _waiting) { _tapAt = null; _grabAt = null; _dragTo = null; }
    }

    /// <summary>
    /// The fingers' work this frame, before the cursor is read: the camera drag; a grab anchored
    /// where it landed and a drag moving the cursor, which the mouse's drag then follows; a tap
    /// picking up, or putting the held piece down where it landed.
    /// </summary>
    private void ApplyTouch()
    {
        _mouse += _touchLook;
        _touchLook = Vector2.Zero;
        if (_grabAt is { } grab)
        {
            _grabAt = null;
            _touchCursor = _cursor = grab;
            AnchorDrag();
        }

        if (_dragTo is { } drag)
        {
            _dragTo = null;
            _touchCursor = drag;
        }

        if (_tapAt is not { } tap) return;
        _tapAt = null;
        _touchCursor = _cursor = tap;
        if (HasSelection)
        {
            if (CursorOnPlane(out Vector3 point)) { _pendingMove = false; _piece = point; _dirty = true; }
            UpdateGhost();
            AnchorDrag();
            Choose();
        }
        else
        {
            UpdateAim(tap);
            if (_aimIndex >= 0) { _room = true; Choose(); }
        }
    }

    /// <summary>
    /// An angle being edited under touch: Turn left and right step it by 15 degrees, Place
    /// accepts it and back cancels it, as Enter and Esc do.
    /// </summary>
    private void TouchRotationEdit()
    {
        uint held = GameInput.Touch.Buttons & (GameInput.PadLeftShoulder | GameInput.PadRightShoulder);
        uint pressed = held & ~_touchHeld;
        _touchHeld = held;
        if (!Engine_UiInputMode.Touch) return;
        _ui?.Status("Turn left / right: 15 degrees; Confirm to accept");
        if ((pressed & GameInput.PadLeftShoulder) != 0) AdjustRotation(-15);
        if ((pressed & GameInput.PadRightShoulder) != 0) AdjustRotation(15);
    }
}
