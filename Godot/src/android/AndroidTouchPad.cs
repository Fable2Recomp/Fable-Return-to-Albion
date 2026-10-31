using System.Collections.Generic;
using Godot;
using Refii.Game;

namespace Refii.AndroidRuntime;

/// <summary>What the touch controls hold on the pad this frame, in the guest's units.</summary>
public readonly record struct TouchPadOutput(uint Buttons, uint LeftTrigger, uint RightTrigger, int LeftX, int LeftY)
{
    public static readonly TouchPadOutput Neutral = new(0u, 0u, 0u, 0, 0);
}

/// <summary>A pointer press, move or release a finger made on no control, for the menus under it.</summary>
public readonly record struct TouchPointerEvent(TouchPointerPhase Phase, Vector2 Position, Vector2 Relative);

public enum TouchPointerPhase { Down, Move, Up }

/// <summary>
/// The fingers, as a pad. Every finger is bound, the moment it lands, to the control under it —
/// or to the pointer, when it lands on none in a menu — and keeps that binding until it lifts,
/// wherever it slides. Pure state: the node feeds it touches and the frame clock, and reads back
/// the pad, the camera turn and the pointer events.
/// </summary>
public sealed class AndroidTouchPad
{
    /// <summary>
    /// The shortest press the pad shows, in seconds. The guest reads the pad on its own 30 Hz step
    /// and edge-detects, so a tap shorter than a step could fall between two reads; three steps
    /// is a press it cannot miss (the pointer presses use the same hold, GameInput.PointerHold).
    /// </summary>
    public const double MinimumHold = 0.1;

    /// <summary>A latch press shorter than this is a tap and latches; longer is an ordinary hold.</summary>
    public const double LatchTap = 0.3;

    /// <summary>How far a steering button's finger slides before its motion turns the camera, in canvas units.</summary>
    public const float SteerSlop = 18f;

    /// <summary>The stick's dead zone, as a fraction of its travel.</summary>
    public const float StickDeadZone = 0.10f;

    /// <summary>
    /// The smallest deflection a live stick sends. The guest dead-zones the left stick itself
    /// (XInput's 7849 of 32767, about 0.24), so a touch stick mapped one-to-one would stand still
    /// for the first quarter of its travel: past the touch dead zone it starts just outside the
    /// guest's.
    /// </summary>
    public const float StickFloor = 0.26f;

    /// <summary>Dragged this far past the ring (in radii), the stick sprints — A held, as on the pad.</summary>
    public const float SprintReach = 1.3f;

    /// <summary>The action a tap on the camera in photo mode reports (<see cref="TakeActions"/>).</summary>
    public const string PhotoTap = "PhotoTap";

    /// <summary>
    /// A tap on the camera side is a touch this short that moved no further than
    /// <see cref="SteerSlop"/>; two of them this close in time and place are the right stick's
    /// click (the camera mode), which has no button of its own.
    /// </summary>
    public const double TapTime = 0.25, DoubleTapGap = 0.35;

    /// <summary>How far apart a double tap's two taps may land, in canvas units.</summary>
    public const float DoubleTapReach = 90f;

    private sealed class Finger
    {
        public TouchControl? Control;
        public bool Pointer;
        public Vector2 Start, Position;
        public double Since;
        public bool Steering;

        /// <summary>The D-pad direction this finger holds, kept through a wobble (<see cref="DPadDirection"/>).</summary>
        public uint DPad;
        public bool UnlatchOnRelease;
    }

    private readonly Dictionary<int, Finger> _fingers = new();
    private readonly HashSet<string> _latched = new();
    private readonly List<(uint Bits, TouchTrigger Trigger, double Until)> _lingering = new();
    private readonly List<TouchPointerEvent> _pointerEvents = new();
    private IReadOnlyList<TouchControl> _controls = System.Array.Empty<TouchControl>();
    private IReadOnlyList<Rect2> _passThrough = System.Array.Empty<Rect2>();
    private Vector2 _look;
    private int _pointerFinger = -1;
    private readonly List<string> _actions = new();
    private double _lookTapAt = double.NegativeInfinity;
    private Vector2 _lookTapAtPosition;

    /// <summary>The context the controls were last laid out for.</summary>
    public TouchContext Context { get; private set; } = TouchContext.Hidden;

    /// <summary>The controls as laid out now, for drawing.</summary>
    public IReadOnlyList<TouchControl> Controls => _controls;

    /// <summary>Where the move stick's base stands while a thumb holds it, else null.</summary>
    public Vector2? StickOrigin { get; private set; }

    /// <summary>The knob's offset from <see cref="StickOrigin"/>, clamped to the ring.</summary>
    public Vector2 StickKnob { get; private set; }

    /// <summary>Whether the stick is dragged past the ring: sprinting.</summary>
    public bool Sprinting { get; private set; }

    /// <summary>The stick radius the layout was built with, in canvas units.</summary>
    public float StickRadius { get; private set; } = AndroidTouchLayout.StickRadius;

    /// <summary>Whether a control is held by a finger or latched — for drawing it pressed.</summary>
    public bool IsActive(string id)
    {
        if (_latched.Contains(id))
        {
            return true;
        }

        foreach (Finger finger in _fingers.Values)
        {
            if (finger.Control?.Id == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a control is latched on by a tap.</summary>
    public bool IsLatched(string id) => _latched.Contains(id);

    /// <summary>Lets a latch go as if tapped off — the game is done with what it held open.</summary>
    public void Unlatch(string id) => _latched.Remove(id);

    /// <summary>The D-pad direction a finger holds, as pad bits, for drawing.</summary>
    public uint DPadBits { get; private set; }

    /// <summary>Whether any finger is down.</summary>
    public bool AnyFinger => _fingers.Count > 0;

    /// <summary>
    /// Lays the controls out for a context. A finger keeps its control across the change when the
    /// new layout has a control of the same id; otherwise it lets go and stays inert until lifted.
    /// Latches end when the play ends — a lock or a held wheel must not outlive a menu opening.
    /// </summary>
    /// <param name="passThrough">Screen rectangles owned by ordinary UI (the diagnostics buttons):
    /// a touch there is always the pointer.</param>
    /// <param name="stickRadius">The move stick's travel at this layout's scale, in canvas units.</param>
    public void SetLayout(TouchContext context, IReadOnlyList<TouchControl> controls, IReadOnlyList<Rect2> passThrough,
        float stickRadius = AndroidTouchLayout.StickRadius)
    {
        _passThrough = passThrough;
        StickRadius = stickRadius;
        bool changed = context != Context;
        Context = context;
        _controls = controls;
        if (!changed)
        {
            // Same context, perhaps a new size: keep each finger on its control's new bounds.
            foreach (Finger finger in _fingers.Values)
            {
                if (finger.Control is { } held && Find(held.Id) is { } fresh)
                {
                    finger.Control = fresh;
                }
            }

            return;
        }

        if (context is TouchContext.Menu or TouchContext.Hidden or TouchContext.Photo or TouchContext.Mirror or TouchContext.PubGame)
        {
            _latched.Clear();
        }

        _latched.RemoveWhere(id => Find(id) is null);
        foreach (Finger finger in _fingers.Values)
        {
            if (finger.Control is { } held)
            {
                finger.Control = Find(held.Id);
                finger.Steering = false;
            }
        }
    }

    private TouchControl? Find(string id)
    {
        foreach (TouchControl control in _controls)
        {
            if (control.Id == id)
            {
                return control;
            }
        }

        return null;
    }

    /// <summary>A finger landed.</summary>
    public void Down(int index, Vector2 position, double now)
    {
        Up(index, position, now);
        var finger = new Finger { Start = position, Position = position, Since = now };
        _fingers[index] = finger;
        bool passThrough = false;
        foreach (Rect2 rect in _passThrough)
        {
            passThrough |= rect.HasPoint(position);
        }

        if (!passThrough)
        {
            foreach (TouchControl control in _controls)
            {
                if (control.Hit(position))
                {
                    finger.Control = control;
                    break;
                }
            }
        }

        if (finger.Control is null)
        {
            // Off every control: the menus' pointer — one finger at a time. A pub game's board
            // takes no taps (its mouse is the pad's A), so there only the pass-through UI has one.
            if (_pointerFinger < 0 && (passThrough || Context != TouchContext.PubGame))
            {
                _pointerFinger = index;
                finger.Pointer = true;
                _pointerEvents.Add(new TouchPointerEvent(TouchPointerPhase.Down, position, Vector2.Zero));
            }

            return;
        }

        if (finger.Control.Kind is TouchControlKind.Latch or TouchControlKind.Menu && _latched.Contains(finger.Control.Id))
        {
            finger.UnlatchOnRelease = true;
        }

        if (finger.Control.Kind == TouchControlKind.Stick)
        {
            // The base centres under the thumb, kept far enough inside the zone to draw whole.
            Rect2 zone = finger.Control.Bounds;
            float reach = StickRadius;
            StickOrigin = new Vector2(
                Mathf.Clamp(position.X, zone.Position.X + reach, Mathf.Max(zone.Position.X + reach, zone.End.X - reach)),
                Mathf.Clamp(position.Y, zone.Position.Y + reach, Mathf.Max(zone.Position.Y + reach, zone.End.Y - reach)));
            Steer(position);
        }
    }

    /// <summary>A finger moved.</summary>
    public void Move(int index, Vector2 position, Vector2 relative)
    {
        if (!_fingers.TryGetValue(index, out Finger? finger))
        {
            return;
        }

        finger.Position = position;
        if (finger.Pointer)
        {
            _pointerEvents.Add(new TouchPointerEvent(TouchPointerPhase.Move, position, relative));
            return;
        }

        switch (finger.Control)
        {
            case { Kind: TouchControlKind.Stick }:
                Steer(position);
                break;
            case { Kind: TouchControlKind.Look }:
                _look += relative;
                break;
            case { Steers: true } steering:
                // Once past the slop the button is a stick for the camera too; the slop itself is
                // not turned, so the view does not jump when a press starts to slide.
                if (finger.Steering)
                {
                    _look += relative;
                }
                else if (position.DistanceTo(finger.Start) > SteerSlop)
                {
                    finger.Steering = true;
                }

                break;
        }
    }

    /// <summary>A finger lifted (or was cancelled).</summary>
    public void Up(int index, Vector2 position, double now)
    {
        if (!_fingers.Remove(index, out Finger? finger))
        {
            return;
        }

        finger.Position = position;
        if (finger.Pointer)
        {
            _pointerFinger = -1;
            _pointerEvents.Add(new TouchPointerEvent(TouchPointerPhase.Up, position, Vector2.Zero));
            return;
        }

        if (finger.Control is not { } control)
        {
            return;
        }

        bool tap = now - finger.Since < TapTime && finger.Position.DistanceTo(finger.Start) <= SteerSlop;
        switch (control.Kind)
        {
            case TouchControlKind.Action:
                if (finger.Position.DistanceTo(finger.Start) <= SteerSlop)
                {
                    _actions.Add(control.Id);
                }

                break;
            case TouchControlKind.Look when tap && Context == TouchContext.Photo:
                // Photo mode has no right-stick click: a tap is the photo panel's way back.
                _actions.Add(PhotoTap);
                break;
            case TouchControlKind.Look when tap && Context == TouchContext.Mirror:
                // The mirror has no camera mode to click: a tap off its panel does nothing.
                break;
            case TouchControlKind.Look when tap:
                if (now - _lookTapAt <= DoubleTapGap && position.DistanceTo(_lookTapAtPosition) <= DoubleTapReach)
                {
                    _lingering.Add((GameInput.PadRightThumb, TouchTrigger.None, now + MinimumHold));
                    _lookTapAt = double.NegativeInfinity;
                }
                else
                {
                    _lookTapAt = now;
                    _lookTapAtPosition = position;
                }

                break;
            case TouchControlKind.Stick:
                StickOrigin = null;
                StickKnob = Vector2.Zero;
                Sprinting = false;
                break;
            case TouchControlKind.Menu:
                // Latched: the tap that lets go. Held: the game's menu opened under the finger and
                // stays latched open. A short tap: the host's action.
                if (finger.UnlatchOnRelease)
                {
                    _latched.Remove(control.Id);
                }
                else if (now - finger.Since >= LatchTap || finger.Steering)
                {
                    _latched.Add(control.Id);
                }
                else if (finger.Position.DistanceTo(finger.Start) <= SteerSlop)
                {
                    _actions.Add(control.Id);
                }

                break;
            case TouchControlKind.Latch:
                if (finger.UnlatchOnRelease || now - finger.Since >= LatchTap || finger.Steering)
                {
                    _latched.Remove(control.Id);
                }
                else
                {
                    _latched.Add(control.Id);
                }

                break;
            case TouchControlKind.Button or TouchControlKind.DPad:
                uint bits = control.Kind != TouchControlKind.DPad ? control.Bits
                    : finger.DPad != 0u ? finger.DPad : DPadDirection(control, finger.Position);
                if (now - finger.Since < MinimumHold && bits != 0u)
                {
                    _lingering.Add((bits, control.Trigger, finger.Since + MinimumHold));
                }

                break;
        }
    }

    /// <summary>Lets go of everything: the app lost focus. Latches end too.</summary>
    public void Cancel()
    {
        if (_pointerFinger >= 0 && _fingers.TryGetValue(_pointerFinger, out Finger? pointer))
        {
            _pointerEvents.Add(new TouchPointerEvent(TouchPointerPhase.Up, pointer.Position, Vector2.Zero));
        }

        _pointerFinger = -1;
        _fingers.Clear();
        _latched.Clear();
        _lingering.Clear();
        StickOrigin = null;
        StickKnob = Vector2.Zero;
        Sprinting = false;
    }

    private void Steer(Vector2 position)
    {
        if (StickOrigin is not { } origin)
        {
            return;
        }

        Vector2 offset = position - origin;
        Sprinting = Context == TouchContext.World && offset.Length() > StickRadius * SprintReach;
        StickKnob = offset.LimitLength(StickRadius);
    }

    /// <summary>
    /// How far off its arm, in degrees, a finger holding a D-pad direction may wander before the
    /// next direction takes over: 20 past the diagonal, so a thumb's wobble near one never flips
    /// the item, and a deliberate slide still changes it.
    /// </summary>
    public const float DPadHoldDegrees = 65f;

    /// <summary>
    /// The D-pad direction under a finger: the dominant axis, nothing near the centre; the
    /// direction it already <paramref name="holds"/> kept until it is well into another's.
    /// </summary>
    public static uint DPadDirection(TouchControl dpad, Vector2 position, uint holds = 0u)
    {
        Vector2 offset = position - dpad.Bounds.GetCenter();
        if (offset.Length() < dpad.Bounds.Size.X * 0.12f)
        {
            return 0u;
        }

        Vector2 axis = holds switch
        {
            GameInput.PadUp => Vector2.Up, GameInput.PadDown => Vector2.Down,
            GameInput.PadLeft => Vector2.Left, GameInput.PadRight => Vector2.Right, _ => Vector2.Zero,
        };
        if (axis != Vector2.Zero && offset.Normalized().Dot(axis) >= Mathf.Cos(Mathf.DegToRad(DPadHoldDegrees)))
        {
            return holds;
        }

        return Mathf.Abs(offset.X) > Mathf.Abs(offset.Y)
            ? offset.X < 0f ? GameInput.PadLeft : GameInput.PadRight
            : offset.Y < 0f ? GameInput.PadUp : GameInput.PadDown;
    }

    /// <summary>The pad as the fingers hold it now.</summary>
    public TouchPadOutput Sample(double now)
    {
        uint buttons = 0u;
        bool left = false, right = false;
        uint dpad = 0u;
        // A loop, not RemoveAll with a lambda: the lambda captures `now`, a closure and a
        // delegate on every sample, and the pad is sampled every frame.
        for (int i = _lingering.Count - 1; i >= 0; --i)
        {
            if (_lingering[i].Until <= now)
            {
                _lingering.RemoveAt(i);
            }
        }
        foreach ((uint bits, TouchTrigger trigger, _) in _lingering)
        {
            buttons |= bits;
            left |= trigger == TouchTrigger.Left;
            right |= trigger == TouchTrigger.Right;
        }

        foreach (TouchControl control in _controls)
        {
            if (control.Kind is TouchControlKind.Latch or TouchControlKind.Menu && _latched.Contains(control.Id))
            {
                buttons |= control.Bits;
                left |= control.Trigger == TouchTrigger.Left;
                right |= control.Trigger == TouchTrigger.Right;
            }
        }

        int stickX = 0, stickY = 0;
        foreach (Finger finger in _fingers.Values)
        {
            if (finger.Control is not { } control)
            {
                continue;
            }

            switch (control.Kind)
            {
                case TouchControlKind.Button or TouchControlKind.Latch:
                    buttons |= control.Bits;
                    left |= control.Trigger == TouchTrigger.Left;
                    right |= control.Trigger == TouchTrigger.Right;
                    break;
                case TouchControlKind.DPad:
                    finger.DPad = DPadDirection(control, finger.Position, finger.DPad);
                    dpad |= finger.DPad;
                    break;
                case TouchControlKind.Menu when finger.UnlatchOnRelease || now - finger.Since >= LatchTap || finger.Steering:
                    // Pressed only once it is a hold: a tap is the host's and must not flash the game's menu.
                    buttons |= control.Bits;
                    left |= control.Trigger == TouchTrigger.Left;
                    right |= control.Trigger == TouchTrigger.Right;
                    break;
                case TouchControlKind.Stick:
                    (stickX, stickY) = StickAxes(StickKnob, StickRadius);
                    if (Sprinting)
                    {
                        buttons |= GameInput.PadA;
                    }

                    break;
            }
        }

        DPadBits = dpad;
        return new TouchPadOutput(buttons | dpad, left ? 255u : 0u, right ? 255u : 0u, stickX, stickY);
    }

    /// <summary>
    /// The knob as the Xbox's left stick: up positive, past the touch dead zone starting just
    /// outside the guest's own (<see cref="StickFloor"/>), full at the ring.
    /// </summary>
    public static (int X, int Y) StickAxes(Vector2 knob, float radius)
    {
        float magnitude = radius > 0f ? Mathf.Min(knob.Length() / radius, 1f) : 0f;
        if (magnitude < StickDeadZone)
        {
            return (0, 0);
        }

        float output = StickFloor + (1f - StickFloor) * (magnitude - StickDeadZone) / (1f - StickDeadZone);
        Vector2 direction = knob.Normalized() * output;
        return (Mathf.RoundToInt(direction.X * 32767f), Mathf.RoundToInt(-direction.Y * 32767f));
    }

    /// <summary>The camera drag since the last call, in canvas units (screen axes: right and down positive).</summary>
    public Vector2 TakeLook()
    {
        Vector2 look = _look;
        _look = Vector2.Zero;
        return look;
    }

    /// <summary>The host actions tapped since the last call (Options), in order.</summary>
    public IReadOnlyList<string> TakeActions()
    {
        if (_actions.Count == 0)
        {
            return System.Array.Empty<string>();
        }

        var actions = _actions.ToArray();
        _actions.Clear();
        return actions;
    }

    /// <summary>The pointer events since the last call, in order.</summary>
    public IReadOnlyList<TouchPointerEvent> TakePointerEvents()
    {
        if (_pointerEvents.Count == 0)
        {
            return System.Array.Empty<TouchPointerEvent>();
        }

        var events = _pointerEvents.ToArray();
        _pointerEvents.Clear();
        return events;
    }
}
