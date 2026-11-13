using System.Collections.Generic;

namespace Refii.Game;

/// <summary>
/// The live effect feed on a phone: new effect nodes spread over the walks
/// (<see cref="AndroidFxSpawns"/>), and built without a debugging name.
/// </summary>
public partial class GameFx
{
    /// <summary>The effects waiting for a node; no limit (the desktop's way) unless a phone set one.</summary>
    private readonly AndroidFxSpawnQueue _spawns = new();

    /// <summary>
    /// Whether a live node is built without its <c>LiveFx_</c> name: on a phone. Nothing looks a node
    /// up by it; Godot names the child itself, without the string a node per effect cost.
    /// </summary>
    private static bool SpawnsUnnamed => Refii.Core.Engine_Platform.IsAndroid;

    /// <summary>Effects waiting for a node, for the debug channel.</summary>
    public int SpawnsWaiting => _spawns.Waiting;

    /// <summary>Requests the per-walk limit has put off so far. Run total.</summary>
    public long SpawnsDeferred => _spawns.Deferred;

    /// <summary>Reads the phone's per-walk limit when the feed starts.</summary>
    private void ConfigureSpawns(IReadOnlyDictionary<string, string> config) =>
        _spawns.Limit = AndroidFxSpawns.Resolve(config, Refii.Core.Engine_Platform.IsAndroid);

    /// <summary>
    /// True when <paramref name="key"/>'s node must wait for a later walk. Its key is then not
    /// marked seen, so a later walk builds it while the guest still lists it, and
    /// <see cref="Retire"/>, which walks built nodes only, has nothing of it to take down.
    /// </summary>
    private bool SpawnDeferred(uint key) => !_spawns.TryAdmit(key);
}
