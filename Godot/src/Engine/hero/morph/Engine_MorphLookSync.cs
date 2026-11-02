using System.Collections.Generic;

namespace Refii.Hero;

/// <summary>
/// One settings slot's look against its player's look in the store (<see cref="Engine_CharacterMorphs"/>),
/// each frame. The store is the look of record: its values are saved with the hero (the player store's
/// saved values, written beside the save and read back by its load), so each save keeps its own look
/// and both co-op machines draw and judge the same one. The slot is the Mirror's working copy. The
/// rule, in order:
/// <list type="number">
/// <item>the slot's player changed (a join, a leave, the first frame in game): the slot takes the
/// store's look;</item>
/// <item>the slot changed (the Mirror's preview or Apply): it is stated to the store;</item>
/// <item>the store changed (a save load, a new game, a mod's write on either machine) once a statement
/// has had <see cref="PendingSeconds"/> to land: the slot takes the store's look;</item>
/// <item>otherwise they agree.</item>
/// </list>
/// A store holding no look (a new game, or a save made without one) is the game's own look: the slot
/// is kept automatic. A statement lands only where the player is played (<c>appearance_state</c>), the
/// same owners the slot's player is read from, so a slot never writes a look the other machine owns.
/// </summary>
public sealed class Engine_MorphLookSync
{
    public enum Step { None, Push, Persist }

    public const double PendingSeconds = 1.0;

    private int _player;
    private Dictionary<string, float>? _local;
    private Dictionary<string, float>? _store;
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
            _local = null;
            _store = null;
            return Step.None;
        }

        if (player != _player || _local is null)
        {
            _player = player;
            _statedAt = double.NegativeInfinity;
            return Adopt(local, stored, out values);
        }

        if (!Engine_CharacterMorphs.SameLook(local, _local))
        {
            _local = new Dictionary<string, float>(local);
            _store = new Dictionary<string, float>(local);
            _statedAt = now;
            return Step.Push;
        }

        if (Engine_CharacterMorphs.SameLook(stored, _store)) return Step.None;
        if (now - _statedAt < PendingSeconds) return Step.None;
        return Adopt(local, stored, out values);
    }

    /// <summary>The slot takes the store's look, or turns automatic when the store holds none.</summary>
    private Step Adopt(IReadOnlyDictionary<string, float> local, IReadOnlyDictionary<string, float>? stored,
        out IReadOnlyDictionary<string, float> values)
    {
        _store = stored is null ? null : new Dictionary<string, float>(stored);
        var look = new Dictionary<string, float>(stored ?? local);
        if (stored is null) look[Engine_CharacterMorphs.StoreAutomaticKey] = 1;
        _local = look;
        values = look;
        return Engine_CharacterMorphs.SameLook(local, look) ? Step.None : Step.Persist;
    }
}
