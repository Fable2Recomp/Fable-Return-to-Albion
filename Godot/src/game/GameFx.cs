using System.Collections.Generic;
using Godot;
using Refii.Fx;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Mirrors the runtime effects the guest is playing (spells, impacts, auras); ambient level
/// effects come from the GDB via <see cref="Refii.Level.Engine_LevelFx"/>. The particle update
/// is on the sim path (`sub_82319BE8` -> `sub_821DC270` -> `sub_821DFDD0` -> `sub_83234C38` ->
/// per instance `sub_8322D988`), so it runs with no GPU. Must be a sibling of the region, not a
/// child: the feed is guest world space and carries the guest-to-Godot map itself.
/// </summary>
public partial class GameFx : Node3D
{
    /// <summary><c>refii.toml</c> key for drawing the mirror; on by default.</summary>
    public const string DrawSettingKey = "fx_live_effects";

    /// <summary>Whether to log the census. Off by default.</summary>
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

    /// <summary>Roughly ten seconds at 60fps.</summary>
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

        // The environment wins: a headless harness may have no config.
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
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("gamefx");
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

        // Logged unconditionally: an empty list is indistinguishable from nothing playing.
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
            // The guest's has-work predicate; a dead instance is retired this same tick.
            if (!fx.Alive)
            {
                continue;
            }

            // One node per effect, not per instance: an effect is many instances (one per entity
            // per type-vector plus a synthetic root each).
            uint key = fx.Handle != 0 ? fx.Handle : fx.Instance;
            if (!_seen.Add(key))
            {
                continue;
            }

            // The instance names its type, not its effect; the bank holds the type->effect join.
            uint effect = bank?.EffectForType(fx.NameHash) ?? 0u;
            bool known = effect != 0 && bank?.FindByHash(effect) is not null;
            if (!known)
            {
                // The synthetic per-type-vector root belongs to no effect; not a miss.
                if (fx.NameHash == FxBank.ReservedRootType)
                {
                    continue;
                }

                // The list holds every particle node (emitters, attractors, splines), not only
                // effects; counted, not warned.
                _unresolved.Add(fx.NameHash);
                continue;
            }

            _census[effect] = _census.TryGetValue(effect, out int n) ? n + 1 : 1;

            if (!_draw)
            {
                continue;
            }

            // Positions are three raw words with no publish barrier; a NaN render depth hangs
            // Godot's render-list sort. Tested before the node exists so an effect with no good
            // position yet is not parked at the world origin.
            bool finite = fx.Position.IsFinite();
            if (!finite || fx.Position.Length() >= MaxFxReach)
            {
                // Counted apart: non-finite is a torn read, out-of-reach is a distrusted value.
                if (finite)
                {
                    ++OutOfReachFx;
                }
                else
                {
                    ++NonFiniteFx;
                }

                continue;
            }

            if (!_nodes.TryGetValue(key, out FableFx? node))
            {
                node = new FableFx
                {
                    Name = $"LiveFx_{key:X8}",

                    // Hash token form: the guest stores only the FNV-1 of the name.
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
    /// Drops the nodes whose guest instance is gone. Keyed on the instance pointer: the guest
    /// retires by swap-and-pop, so vector indices are not stable.
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

    /// <summary>The census, printed only when the distinct-effect count moves.</summary>
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
    /// Guest world space to Godot's, the same map <see cref="GameEntities"/> uses (a sibling of
    /// the region needs the X negation; the region's contents do not). Rows are normalised guest
    /// axes: row 0 = local X, row 1 = Y, row 2 = Z; Godot Y = guest Z, Godot Z = -guest Y.
    /// </summary>
    private static Transform3D ToGodot(Vector3 position, Vector3[] rows)
    {
        if (rows.Length < 3 || rows[0].IsZeroApprox() || rows[2].IsZeroApprox())
        {
            return new Transform3D(Basis.Identity, ToGodot(position));
        }

        var basis = new Basis(ToGodot(rows[0]), ToGodot(rows[2]), -ToGodot(rows[1]));
        float determinant = basis.Determinant();

        // The range test is false for NaN, so a NaN or infinite basis needs its own check.
        if (!float.IsFinite(determinant) || determinant is < 0.01f and > -0.01f)
        {
            return new Transform3D(Basis.Identity, ToGodot(position));
        }

        return new Transform3D(basis, ToGodot(position));
    }

    /// <summary>Furthest an FX emitter may sit from the origin before its depth can overflow.</summary>
    private const float MaxFxReach = 1_000_000f;

    /// <summary>FX placements refused as not finite. Run total.</summary>
    public static int NonFiniteFx;

    /// <summary>Effects with a finite position beyond <see cref="MaxFxReach"/>. Run total.</summary>
    public static int OutOfReachFx;

    private static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);
}
