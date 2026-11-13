using System.Collections.Generic;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>One mouse action the menu pointer makes, in canvas units.</summary>
public readonly record struct TouchMouseAction(TouchMouseKind Kind, Vector2 Position, Vector2 Relative, int Notches = 0);

public enum TouchMouseKind { Hover, Press, Drag, Release, Wheel }

/// <summary>
/// The pointer finger as a mouse, for menus written for one. The menus act on the PRESS — a PC
/// list activates the row the click lands on — so a finger cannot press the moment it lands, or
/// every swipe through a list would choose the row it started on. The press waits: a finger that
/// lifts where it landed is a click; one that stays put a moment is a held button (a slider's
/// drag, a held expression); one that slides first is a scroll, played out as wheel notches, and
/// never clicks.
/// </summary>
public sealed class AndroidTouchPointer
{
    /// <summary>How far a finger may wander and still be a tap, in canvas units.</summary>
    public const float TapSlop = 14f;

    /// <summary>How long a still finger waits before it is a held press, in seconds.</summary>
    public const double HoldDelay = 0.22;

    /// <summary>The swipe one wheel notch is, in canvas units.</summary>
    public const float NotchLength = 36f;

    private enum State { Idle, Pending, Pressed, Scrolling, Clicking, Releasing }

    private State _state;
    private Vector2 _start, _position, _scrollRest;
    private double _since;
    private readonly List<TouchMouseAction> _actions = new();

    /// <summary>Whether the mouse button is down now.</summary>
    public bool Pressed => _state == State.Pressed;

    /// <summary>Whether nothing is in flight: no finger's gesture under way and no mouse action waiting to be taken; a tick changes nothing.</summary>
    public bool Idle => _state == State.Idle && _actions.Count == 0;

    /// <summary>Feeds the pointer finger's events, in order.</summary>
    public void Feed(TouchPointerEvent touch, double now)
    {
        switch (touch.Phase)
        {
            case TouchPointerPhase.Down:
                Finish();

                _state = State.Pending;
                _start = _position = _scrollRest = touch.Position;
                _since = now;
                // The pointer moves under the finger at once — a row highlights where it lands.
                _actions.Add(new TouchMouseAction(TouchMouseKind.Hover, touch.Position, Vector2.Zero));
                break;
            case TouchPointerPhase.Move:
                Vector2 previous = _position;
                _position = touch.Position;
                switch (_state)
                {
                    case State.Pending when _position.DistanceTo(_start) > TapSlop:
                        _state = State.Scrolling;
                        Scroll();
                        break;
                    case State.Pressed:
                        _actions.Add(new TouchMouseAction(TouchMouseKind.Drag, _position, _position - previous));
                        break;
                    case State.Scrolling:
                        Scroll();
                        break;
                }

                break;
            case TouchPointerPhase.Up:
                _position = touch.Position;
                if (_state == State.Pending)
                {
                    // THE CLICK IS PLAYED OUT OVER THE NEXT FRAMES, not at once: the press on the
                    // frame after the lift and the release on the one after that. A view that
                    // picks by hover (the expression wheel points its sector at the pointer, then
                    // selects on the click) must see the pointer arrive before the press lands.
                    _state = State.Clicking;
                    break;
                }

                if (_state == State.Pressed)
                {
                    _actions.Add(new TouchMouseAction(TouchMouseKind.Release, _position, Vector2.Zero));
                }

                _state = State.Idle;
                break;
        }
    }

    /// <summary>The frame clock: a still finger becomes a held press.</summary>
    public void Tick(double now)
    {
        if (_state == State.Releasing)
        {
            _actions.Add(new TouchMouseAction(TouchMouseKind.Release, _start, Vector2.Zero));
            _state = State.Idle;
        }
        else if (_state == State.Clicking)
        {
            _actions.Add(new TouchMouseAction(TouchMouseKind.Press, _start, Vector2.Zero));
            _state = State.Releasing;
        }
        else if (_state == State.Pending && now - _since >= HoldDelay)
        {
            _state = State.Pressed;
            _actions.Add(new TouchMouseAction(TouchMouseKind.Press, _start, Vector2.Zero));
            if (_position != _start)
            {
                _actions.Add(new TouchMouseAction(TouchMouseKind.Drag, _position, _position - _start));
            }
        }
    }

    /// <summary>Releases a held press: the app lost focus.</summary>
    public void Cancel() => Finish();

    /// <summary>Completes whatever the last finger left in flight — a held press or a click not yet played out.</summary>
    private void Finish()
    {
        switch (_state)
        {
            case State.Pressed:
                _actions.Add(new TouchMouseAction(TouchMouseKind.Release, _position, Vector2.Zero));
                break;
            case State.Clicking:
                _actions.Add(new TouchMouseAction(TouchMouseKind.Press, _start, Vector2.Zero));
                _actions.Add(new TouchMouseAction(TouchMouseKind.Release, _start, Vector2.Zero));
                break;
            case State.Releasing:
                _actions.Add(new TouchMouseAction(TouchMouseKind.Release, _start, Vector2.Zero));
                break;
        }

        _state = State.Idle;
    }

    /// <summary>Wheel notches for the swipe since the last notch: finger up is wheel down, the page following the finger.</summary>
    private void Scroll()
    {
        float travel = _position.Y - _scrollRest.Y;
        int notches = (int)(travel / NotchLength);
        if (notches == 0)
        {
            return;
        }

        _scrollRest.Y += notches * NotchLength;
        // Dragging the page up shows what is below it: the wheel turned down (+1 per notch).
        _actions.Add(new TouchMouseAction(TouchMouseKind.Wheel, _start, Vector2.Zero, -notches));
    }

    /// <summary>The mouse actions since the last call, in order.</summary>
    public IReadOnlyList<TouchMouseAction> Take()
    {
        if (_actions.Count == 0)
        {
            return System.Array.Empty<TouchMouseAction>();
        }

        var actions = _actions.ToArray();
        _actions.Clear();
        return actions;
    }
}
