namespace Refii.AndroidRuntime;

/// <summary>
/// When a latched wheel opener lets go by itself. On the pad the player lets go of the wheel's
/// button once the wheel has done its job; a latched Emotes would keep the wheel's mode up and
/// cost a tap per emote. The guest closes its wheel on Close, Turn Safety and backing out (an
/// action picked inside a group is counted separately, GameUi.ExpressionWheelPicks), so the latch
/// follows the guest's open flag: open, then closed for <see cref="GoneFor"/>. A wheel the guest
/// never opens (it refuses during a hint banner) lets go after <see cref="NeverShownFor"/>, so the
/// controls are not left in the wheel's mode with nothing on screen.
/// </summary>
public sealed class AndroidTouchWheelRelease
{
    /// <summary>Seconds the opened wheel must stay closed before the latch lets go.</summary>
    public const double GoneFor = 0.25;

    /// <summary>Seconds a latch waits for a wheel the guest never draws.</summary>
    public const double NeverShownFor = 1.5;

    private bool _seen;
    private double _latched, _gone;

    /// <summary>One frame.</summary>
    /// <param name="latched">Whether the opener is latched on now.</param>
    /// <param name="shown">Whether the guest states its wheel open now.</param>
    /// <param name="delta">The frame's seconds.</param>
    /// <returns>True on the frame the latch should let go.</returns>
    public bool Update(bool latched, bool shown, double delta)
    {
        if (!latched)
        {
            _seen = false;
            _latched = _gone = 0.0;
            return false;
        }

        _latched += delta;
        if (shown)
        {
            _seen = true;
            _gone = 0.0;
            return false;
        }

        _gone += delta;
        return _seen ? _gone >= GoneFor : _latched >= NeverShownFor;
    }
}
