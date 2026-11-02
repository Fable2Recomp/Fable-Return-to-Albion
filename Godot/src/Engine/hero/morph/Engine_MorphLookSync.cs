using System.Collections.Generic;

namespace Refii.Hero;

/// <summary>
/// One settings slot's look against its player's look in the store (<see cref="Engine_CharacterMorphs"/>),
/// each frame: the slot is what this machine's player edits and keeps (the Mirror's settings, or its
/// preview), the store is what both co-op machines draw and judge. The rule, in order:
/// <list type="number">
/// <item>the slot's player changed (a join, a leave, the first frame in game): the slot is stated to the new player;</item>
/// <item>the slot changed (the Mirror's preview or Apply): it is stated;</item>
/// <item>the store holds no look (a new hero empties it): the slot is restated, at most every <see cref="RestateSeconds"/>;</item>
/// <item>the store holds another look once a statement has had <see cref="PendingSeconds"/> to land: a
/// mod's write (on either machine; it reaches the owner) — kept in the slot's settings;</item>
/// <item>otherwise they agree.</item>
/// </list>
/// A statement lands only where the player is played (<c>appearance_state</c>), the same owners the
/// slot's player is read from, so a slot never writes a look the other machine owns.
/// </summary>
public sealed class Engine_MorphLookSync
{
    public enum Step { None, Push, Persist }

    public const double RestateSeconds = 0.5;
    public const double PendingSeconds = 1.0;

    private int _player;
    private Dictionary<string, float>? _synced;
    private double _statedAt = double.NegativeInfinity;

    /// <summary>The player the slot is bound to now, 0 for none.</summary>
    public int Player => _player;

    /// <param name="player">The slot's guest player (1 or 2), 0 while it has none.</param>
    /// <param name="local">The slot's look: its settings, or the Mirror's preview over them.</param>
    /// <param name="stored">The player's look in the store, null while it holds none.</param>
    /// <param name="values">What to state (Push) or keep in the slot's settings (Persist).</param>
    public Step Next(int player, IReadOnlyDictionary<string, float> local, IReadOnlyDictionary<string, float>? stored,
        double now, out IReadOnlyDictionary<string, float> values)
    {
        values = local;
        if (player is not (1 or 2))
        {
            _player = 0;
            _synced = null;
            return Step.None;
        }

        if (player != _player || _synced is null || !Engine_CharacterMorphs.SameLook(local, _synced))
        {
            _player = player;
            _synced = new Dictionary<string, float>(local);
            _statedAt = now;
            return Step.Push;
        }

        if (stored is null)
        {
            if (now - _statedAt < RestateSeconds) return Step.None;
            _statedAt = now;
            values = _synced;
            return Step.Push;
        }

        if (Engine_CharacterMorphs.SameLook(stored, _synced))
        {
            _statedAt = double.NegativeInfinity;
            return Step.None;
        }

        if (now - _statedAt < PendingSeconds) return Step.None;
        _synced = new Dictionary<string, float>(stored);
        values = _synced;
        return Step.Persist;
    }
}
