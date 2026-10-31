using System;
using Godot;

namespace Refii.Game;

/// <summary>
/// A hold on the guest, for photo mode and the Golden Cock's dialog: taken when made, let go and
/// taken again through <see cref="Set"/>, and on <see cref="Release"/> returned once to how it
/// was. A release with nothing held lets nothing go, for another hold may hold the guest meanwhile.
/// </summary>
public sealed class GameGuestHold
{
    private readonly Action<bool> _set;
    private readonly bool _was;
    private bool _released;

    public GameGuestHold(Action<bool> set, bool was)
    {
        _set = set;
        _was = was;
        set(true);
    }

    /// <summary>
    /// Holds the guest still: through <paramref name="holdGuest"/> on a host that steps its guest
    /// without a <see cref="GameSession"/> (Android's own session thread), otherwise the session's
    /// pause, returned to how it was while the session lives. Null with neither.
    /// </summary>
    public static GameGuestHold? Take(Action<bool>? holdGuest, GameSession? session)
    {
        if (holdGuest is { } hold) return new GameGuestHold(hold, false);
        if (session is null) return null;
        return new GameGuestHold(held => { if (GodotObject.IsInstanceValid(session)) session.Paused = held; }, session.Paused);
    }

    public void Set(bool held)
    {
        if (!_released) _set(held);
    }

    public void Release()
    {
        if (_released) return;
        _released = true;
        _set(_was);
    }
}
