using System.Collections.Generic;
using Godot;
using Refii.Fx;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Draws the effects the GUEST is playing — the ones Godot did not place for itself.
///
/// The level's ambient effects come out of the GDB: <see cref="Refii.Level.Engine_LevelFx"/>
/// resolves a torch's flame from its own placement and spawns a native particle system for it.
/// That covers a region standing still and nothing else. Every effect the guest starts at
/// RUNTIME — a spell cast, a weapon impact, a fire catching, a creature's aura — had no route to
/// the screen at all, which is why the game looked right in a quiet street and empty in a fight.
///
/// THIS IS A MIRROR, and it is only possible because the particle update is on the SIM path:
/// `sub_82319BE8` (the wall-clock main loop) -> `sub_821DC270` -> `sub_821DFDD0` ->
/// `sub_83234C38` -> per instance `sub_8322D988`. Nothing in that chain touches a present or the
/// render thread, so the guest keeps simulating particles perfectly well with our no-GPU latch
/// set and never drawing a pixel.
///
/// THE OVERLAP WITH THE STATIC PASS IS NOT YET RESOLVED, and it is the known risk here. The guest
/// owns the level's ambient effects too — a torch is an entity with a particle-attacher component,
/// and loading a region spawns those attachers — so the live list is expected to contain effects
/// <see cref="Refii.Level.Engine_LevelFx"/> has already placed, and drawing both means drawing
/// some flames twice. <see cref="GameEntities"/> solves exactly this for entities with its static
/// twins: it retires the region's build ONE RECORD AT A TIME as each live equivalent appears,
/// rather than guessing a dedup rule up front.
///
/// The same treatment belongs here and is not written yet. Until it is, <c>fx_live_effects</c>
/// draws the live feed on top of the static pass, and <c>fx_live_report</c> logs the census that
/// says how big the double actually is. If flames turn out to double, the fix is the twin
/// retirement, not a proximity test.
///
/// A SIBLING OF THE REGION, not a child — the feed reports guest world space and carries the
/// whole guest-to-Godot map itself, exactly as <see cref="GameEntities"/> and the camera do.
/// Inheriting the region's own mirror instead would put every effect in the level's internal
/// space, which is the world reflected through X.
/// </summary>
public partial class GameFx : Node3D
{
    /// <summary>
    /// <c>refii.toml</c>'s key for drawing the mirror. ON by default.
    ///
    /// A SETTING, NOT AN ENVIRONMENT VARIABLE. The game is launched from Godot and from the
    /// launcher, and neither offers anywhere to put one — a feature reachable only by exporting a
    /// shell variable is a feature nobody can turn on. This is the same route
    /// <see cref="GameWindowSettings"/> takes for the window: the launcher writes
    /// <c>refii.toml</c>, the settings panel edits it, and the engine reads it.
    /// </summary>
    public const string DrawSettingKey = "fx_live_effects";

    /// <summary>Whether to log the census. Off by default; it is a diagnostic, not a feature.</summary>
    public const string ReportSettingKey = "fx_live_report";

    /// <summary>
    /// Overrides the setting for a headless run, where there may be no <c>refii.toml</c> at all.
    /// <c>1</c> forces drawing on, <c>0</c> forces it off; unset defers to the setting.
    /// </summary>
    public const string DrawEnvironmentVariable = "REFII_FX_LIVE";

    /// <summary>Nodes by guest instance pointer, so a moving effect is updated, not rebuilt.</summary>
    private readonly Dictionary<uint, FableFx> _nodes = new();

    private readonly HashSet<uint> _seen = new();

    /// <summary>Every effect hash the guest has played, and how many instances carried it.</summary>
    private readonly Dictionary<uint, int> _census = new();

    private readonly HashSet<uint> _unresolved = new();

    private bool _draw;

    private bool _report;

    /// <summary>Whether the guest has ever reported a live effect.</summary>
    private bool _everSawAny;

    /// <summary>Roughly ten seconds at 60fps, long enough to be in-world and settled.</summary>
    private const int SilenceTicks = 600;

    private int _ticks;

    private int _peakLive;

    private int _lastReportedDistinct = -1;

    public override void _Ready()
    {
        System.Collections.Generic.IReadOnlyDictionary<string, string> config =
            Refii.Core.Engine_RefiiConfig.Read(out string path);

        _draw = Refii.Core.Engine_RefiiConfig.Bool(config, DrawSettingKey, true);
        _report = Refii.Core.Engine_RefiiConfig.Bool(config, ReportSettingKey, false);

        // The environment still wins, because a headless harness may have no config at all.
        string forced = OS.GetEnvironment(DrawEnvironmentVariable);
        if (forced == "1" || forced == "0")
        {
            _draw = forced == "1";
        }

        GD.Print(
            $"[game-fx] live effect feed up; drawing is {(_draw ? "ON" : "OFF")} " +
            $"({DrawSettingKey} in {(path.Length != 0 ? path : "refii.toml")})");
    }

    public override void _Process(double delta)
    {
        if (HostBridge.TryGetAbiVersion() is null)
        {
            return;
        }

        _ticks++;

        List<HostBridge.ActiveFx> live = HostBridge.GetActiveFx();
        if (live.Count > _peakLive)
        {
            _peakLive = live.Count;
        }

        // FIRST CONTACT, AND THE SILENCE THAT MEANS FAILURE — both unconditional, neither behind
        // the census setting.
        //
        // The first run of this feed produced one line saying it was up and drawing, and then
        // nothing whatsoever: the native walk was rejecting every instance pointer against a
        // wrong address range and returning an empty list, which looks exactly like a quiet
        // street. A feed that cannot say "I found things" or "I have found nothing for a while"
        // cannot be debugged from a log, and that cost a whole run.
        if (live.Count != 0 && !_everSawAny)
        {
            _everSawAny = true;
            GD.Print($"[game-fx] first contact: the guest reports {live.Count} live effect node(s)");
        }
        else if (!_everSawAny && _ticks == SilenceTicks)
        {
            GD.Print(
                "[game-fx] the guest has reported NO live effect nodes at all. Either nothing is " +
                "playing, or the walk is not reading the guest's list — check the vector at " +
                "0x8349E48C and the pointer validity range in RefiiHostGetActiveFx.");
        }

        FxBank? bank = FableFx.SharedBank();
        _seen.Clear();

        foreach (HostBridge.ActiveFx fx in live)
        {
            // The guest's own has-work predicate. An instance that is neither emitting nor
            // holding particles is on its way out this same tick — the retire pass runs last in
            // the manager update — so following it would flicker a node in and straight back out.
            if (!fx.Alive)
            {
                continue;
            }

            // ONE NODE PER EFFECT, NOT PER INSTANCE. An effect is many instances — one per entity
            // in each type-vector plus a synthetic root for each — so the beehive's six
            // (spline, emitter) vectors arrive as eighteen entries naming one effect. Keying on
            // the instance would build the whole effect eighteen times over.
            uint key = fx.Handle != 0 ? fx.Handle : fx.Instance;
            if (!_seen.Add(key))
            {
                continue;
            }

            // The instance names its TYPE, not its effect, and the two live in different
            // registries with different key spaces — asking the bank for the type hash directly
            // matched zero of forty on the first working run. The bank holds the join.
            uint effect = bank?.EffectForType(fx.NameHash) ?? 0u;
            bool known = effect != 0 && bank?.FindByHash(effect) is not null;
            if (!known)
            {
                // The synthetic root the guest builds per type-vector belongs to no effect and is
                // not a miss — counting it as one would make a healthy feed look broken.
                if (fx.NameHash == FxBank.ReservedRootType)
                {
                    continue;
                }

                // A node name that is not a bank effect is expected: the guest's list holds every
                // particle NODE, and the emitters, attractors and spline nodes inside one effect
                // are named too. Counted rather than warned about, because the interesting number
                // is what fraction of the list we can draw, not each individual miss.
                _unresolved.Add(fx.NameHash);
                continue;
            }

            _census[effect] = _census.TryGetValue(effect, out int n) ? n + 1 : 1;

            if (!_draw)
            {
                continue;
            }

            if (!_nodes.TryGetValue(key, out FableFx? node))
            {
                node = new FableFx
                {
                    Name = $"LiveFx_{key:X8}",

                    // FableFx already understands the hash token form, which is what we have —
                    // the guest stores only the FNV-1 of the name, never the text.
                    EffectName = $"FX_{effect:X8}",
                };
                _nodes[key] = node;
                AddChild(node);
            }

            node.Transform = ToGodot(fx.Position, fx.Rows);
        }

        if (_draw)
        {
            Retire();
        }

        Report();
    }

    /// <summary>
    /// Drops the nodes whose guest instance is gone.
    ///
    /// Keyed on the instance POINTER because the guest retires by swapping with the last element
    /// and popping, so an effect's index in that vector changes when an unrelated effect ends.
    /// Keying on the index would have live effects swap places with each other.
    /// </summary>
    private void Retire()
    {
        List<uint>? dead = null;
        foreach (KeyValuePair<uint, FableFx> entry in _nodes)
        {
            if (_seen.Contains(entry.Key))
            {
                continue;
            }

            (dead ??= new List<uint>()).Add(entry.Key);
        }

        if (dead is null)
        {
            return;
        }

        foreach (uint instance in dead)
        {
            if (_nodes.Remove(instance, out FableFx? node) && IsInstanceValid(node))
            {
                node.QueueFree();
            }
        }
    }

    /// <summary>
    /// The census, printed only when it MOVES.
    ///
    /// The number that matters is not how many effects are live — that rises and falls with the
    /// fight — but how many DISTINCT effects the guest has asked for that we could not draw. A
    /// feed reporting hundreds of instances is worthless if none of their hashes is a bank entry.
    /// </summary>
    private void Report()
    {
        if (!_report || _ticks % 300 != 0 || _census.Count == _lastReportedDistinct)
        {
            return;
        }

        _lastReportedDistinct = _census.Count;
        GD.Print(
            $"[game-fx] guest effects: {_census.Count} distinct resolved in the bank, " +
            $"{_unresolved.Count} distinct hashes with no bank entry, " +
            $"{_nodes.Count} drawn, peak {_peakLive} live node(s)");
    }

    /// <summary>
    /// Guest world space to Godot's.
    ///
    /// THE SAME MAP <see cref="GameEntities"/> USES, and it is not the one the region's own
    /// contents use: a node standing beside the region needs the X negation, one built inside it
    /// does not. The two differ by the whole world reflected through X, which reads as a level
    /// that is subtly, consistently wrong rather than as an obvious fault.
    ///
    /// The rows arrive normalised and in guest axes — row 0 is the image of the guest's local X,
    /// row 1 of its Y, row 2 of its Z — so the basis is rebuilt from them the same way the entity
    /// feed rebuilds an entity's: Godot's Y comes from the guest's Z, and its Z is the negated
    /// guest Y, which is what carries the handedness flip the X negation introduces.
    /// </summary>
    private static Transform3D ToGodot(Vector3 position, Vector3[] rows)
    {
        if (rows.Length < 3 || rows[0].IsZeroApprox() || rows[2].IsZeroApprox())
        {
            return new Transform3D(Basis.Identity, ToGodot(position));
        }

        var basis = new Basis(ToGodot(rows[0]), ToGodot(rows[2]), -ToGodot(rows[1]));
        float determinant = basis.Determinant();
        if (determinant is < 0.01f and > -0.01f)
        {
            return new Transform3D(Basis.Identity, ToGodot(position));
        }

        return new Transform3D(basis, ToGodot(position));
    }

    private static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);
}
