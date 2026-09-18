using System;
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

    /// <summary>
    /// Each drawn effect's stated transform on the guest's tick timeline, drawn 1.5 ticks
    /// behind like every body and the camera — an effect snapped to its newest sample ran
    /// 100 ms ahead of the entity it trails and stepped at 15 Hz against a smooth world.
    /// </summary>
    private readonly Dictionary<uint, Refii.Core.Engine_TickTimeline> _timelines = new();

    /// <summary>The guest's sim tick as of this frame's walk, read once.</summary>
    private uint _tickNow;

    private readonly HashSet<uint> _seen = new();

    /// <summary>Each effect's root placement this frame, by handle.</summary>
    private readonly Dictionary<uint, Transform3D> _roots = new();

    /// <summary>Each effect's (scale, fade) this frame, from its first standard emitter node.</summary>
    private readonly Dictionary<uint, (float Scale, float Fade)> _emitterState = new();

    /// <summary>
    /// Keys the census has already counted this frame. Separate from <see cref="_seen"/>, which
    /// is what <see cref="Retire"/> keeps alive: an effect must be counted ONCE however many of
    /// its nodes we walk, but kept only if one of them actually handed us a usable transform.
    /// </summary>
    private readonly HashSet<uint> _counted = new();

    /// <summary>The effect hash each drawn node was built for — guest handles RECYCLE.</summary>
    private readonly Dictionary<uint, uint> _effectOf = new();

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

    /// <summary>Whether the buffer-full warning has been printed; it would otherwise be per-frame.</summary>
    private bool _warnedSaturated;

    /// <summary>Consecutive REFRESHED walks each drawn effect has been missing from.</summary>
    private readonly Dictionary<uint, int> _absent = new();

    /// <summary>
    /// How many refreshed walks an effect must be absent from before its node is freed: enough to
    /// ride out the buffer cut moving with the guest's swap-with-last retirement, short enough
    /// (100 ms at the 30 Hz step) not to hold a dead effect on screen.
    /// </summary>
    private const int AbsentWalksBeforeRetire = 3;

    /// <summary>The feed in the tree, for the debug channel.</summary>
    public static GameFx? Current { get; private set; }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this)
        {
            Current = null;
        }
    }

    /// <summary>
    /// The drawn effects nearest <paramref name="eye"/>, one line each: handle, effect, distance,
    /// position, the guest's intensity, and what our simulation holds for it.
    /// </summary>
    public List<string> Describe(Vector3 eye, int take)
    {
        FxBank? bank = FableFx.SharedBank();
        var rows = new List<(float Distance, uint Key, FableFx Node)>();
        foreach ((uint key, FableFx node) in _nodes)
        {
            if (IsInstanceValid(node) && node.IsInsideTree())
            {
                rows.Add((eye.DistanceTo(node.GlobalPosition), key, node));
            }
        }

        rows.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var described = new List<string>();
        foreach ((float distance, uint key, FableFx node) in rows.GetRange(0, System.Math.Min(take, rows.Count)))
        {
            Vector3 p = node.GlobalPosition;
            uint effect = _effectOf.TryGetValue(key, out uint e) ? e : 0u;

            // The guest's stated position beside where we put the node: zero here means the
            // bridge's transform walk produced nothing; non-zero with an origin node means this
            // feed dropped it.
            string stated = _statedAt.TryGetValue(key, out Vector3 g)
                ? $" guest=({g.X:0.0},{g.Y:0.0},{g.Z:0.0})"
                : " guest=none";
            string name = bank?.FindByHash(effect)?.Name ?? string.Empty;
            described.Add(
                $"{key:X8} FX_{effect:X8}{(name.Length != 0 ? " " + name : string.Empty)} d={distance:0.0} " +
                $"at=({p.X:0.0},{p.Y:0.0},{p.Z:0.0}) intensity={node.Intensity:0.000} scale={node.SizeScale:0.00} " +
                $"alive={node.DebugAlive} spawned={node.DebugSpawned} surfaces={node.DebugSurfaces}/{node.DebugAddSurfaces}/{node.DebugDispSurfaces} " +
                $"built={node.DebugBuilt} em/vis/trail/parts={node.DebugCensus.Emitters}/{node.DebugCensus.Visuals}/" +
                $"{node.DebugCensus.TrailVisuals}/{node.DebugCensus.Particles}{stated}");
        }

        return described;
    }

    public int DrawnCount => _nodes.Count;

    /// <summary>Particle nodes the last walk handed over, against the buffer cap.</summary>
    public int NodesRead { get; private set; }

    /// <summary>The stated transform per instance, refreshed from the hook each walk.</summary>
    private readonly Dictionary<uint, (float P1, float P2, float P3)> _stated = new();

    private readonly List<Refii.Host.HostBridge.GuestStatedFx> _statedRows = new();

    /// <summary>
    /// How many placements the transform hook also covered, and how many it did not. Neither
    /// decides the placement (that is the composed matrix at <c>node + 0x20</c>); the hook sees
    /// only the two setters, not the two per-tick writers or the authored load.
    /// </summary>
    public static long StatedPlacements;

    public static long WalkedPlacements;

    /// <summary>Pulls the stated transforms in for this walk.</summary>
    private void RefreshStated()
    {
        _stated.Clear();
        if (Refii.Host.HostBridge.GetStatedFx(_statedRows) == 0)
        {
            return;
        }

        foreach (Refii.Host.HostBridge.GuestStatedFx row in _statedRows)
        {
            _stated[row.Instance] = (row.P1, row.P2, row.P3);
        }
    }

    /// <summary>
    /// Where the walk placed one particle instance, for comparison against the transform the
    /// guest stated for the same instance. Null when the walk has no node for it.
    /// </summary>
    public float[]? DescribePlacement(uint instance)
    {
        if (!_nodes.TryGetValue(instance, out FableFx? node) || !IsInstanceValid(node))
        {
            return null;
        }

        Vector3 at = node.GlobalPosition;
        return new[] { at.X, at.Y, at.Z };
    }

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

        // A child, so its step follows this node's _Process in tree order — after the live
        // nodes are placed — and leaves with it on a level change; see Engine_FxDriver.
        AddChild(new Engine_FxDriver { Name = "FxDriver" });
    }

    /// <summary>The per-frame effects list, reused.</summary>
    private readonly List<HostBridge.ActiveFx> _liveFx = new();

    /// <summary>The position the GUEST stated for each effect handle, as read this frame.</summary>
    private readonly Dictionary<uint, Vector3> _statedAt = new();

    public override void _Process(double delta)
    {
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("gamefx");
        if (HostBridge.TryGetAbiVersion() is null)
        {
            return;
        }

        _ticks++;
        _tickNow = Refii.Core.Engine_TickClock.GuestGameTick;

        // On the guest's frame: the effect list is the guest's statement and only moves when
        // the guest does; between two, the last list stands.
        bool refreshed = Refii.Core.Engine_GuestFrame.Changed || _liveFx.Count == 0;
        List<HostBridge.ActiveFx> live = refreshed ? HostBridge.GetActiveFx(_liveFx) : _liveFx;
        NodesRead = live.Count;
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
        _counted.Clear();
        RefreshStated();

        // The placement is the root's. Every type-vector of an effect hangs off a synthetic root
        // whose transform is what the guest's positioners wrote; a child node's world transform
        // adds its own chain motion (e.g. a spline node's random point per tick), which our
        // simulation applies itself. Collected first: the root need not precede its children.
        _roots.Clear();
        _emitterState.Clear();
        foreach (HostBridge.ActiveFx fx in live)
        {
            uint rootKey = fx.Handle != 0 ? fx.Handle : fx.Instance;
            if (fx.Root)
            {
                if (fx.Position.IsFinite() && fx.Position.Length() < MaxFxReach && !_roots.ContainsKey(rootKey))
                {
                    _roots[rootKey] = ToGodot(fx.Position, fx.Rows);
                }

                continue;
            }

            // Scale and fade live on the element, and only a standard emitter's slots mean
            // anything (a spline or light node's +412 holds something else). The bank says which
            // type a node is.
            if (!_emitterState.ContainsKey(rootKey) &&
                bank?.EmitterForType(fx.NameHash) is { TypeId: 0 or 9 } &&
                float.IsFinite(fx.Scale) && fx.Scale > 0f && fx.Scale < 100f &&
                float.IsFinite(fx.Fade))
            {
                _emitterState[rootKey] = (fx.Scale, Math.Clamp(fx.Fade, 0f, 1f));
            }
        }

        foreach (HostBridge.ActiveFx fx in live)
        {
            // The guest's has-work predicate; a dead instance is retired this same tick.
            if (!fx.Alive || fx.Root)
            {
                continue;
            }

            // One node per effect, not per instance: an effect is many instances (one per entity
            // per type-vector plus a synthetic root each).
            uint key = fx.Handle != 0 ? fx.Handle : fx.Instance;

            // Already PLACED from another node of this effect; nothing left to do for it. Note
            // this only reads the set — a key is marked seen once a transform has been accepted,
            // not on first sight. See the refusal below.
            if (_seen.Contains(key))
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

            if (_counted.Add(key))
            {
                _census[effect] = _census.TryGetValue(effect, out int n) ? n + 1 : 1;
            }

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

                // NOT MARKED SEEN — AND THIS IS THE FLYING FIRE.
                //
                // The key used to be added the moment the effect was first sighted, before this
                // test, so a node whose transform we refuse was already in `_seen`. `Retire`
                // keeps whatever is in `_seen`, so the node stood, kept its LAST GOOD transform
                // for ever, and `FableFx._Process` went on emitting there — a fire burning in
                // mid-air at a position the guest abandoned, for as long as the camera stayed
                // within FreezeDistance.
                //
                // Leaving the key unmarked lets another node of the same effect place it this
                // frame, and lets `Retire` take it down when none can.
                continue;
            }

            // THE HANDLE RECYCLES. `key` is a guest address, so it can come back naming a
            // DIFFERENT effect; the node found here was built for the old one and `EffectName`
            // is only ever read at construction, so it would draw the previous effect's art.
            if (_nodes.TryGetValue(key, out FableFx? node) &&
                (!_effectOf.TryGetValue(key, out uint was) || was != effect))
            {
                _timelines.Remove(key);
                _nodes.Remove(key);
                if (IsInstanceValid(node))
                {
                    node.QueueFree();
                }

                node = null;
            }

            if (node is null)
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

            _effectOf[key] = effect;
            _seen.Add(key);
            _statedAt[key] = fx.Position;

            // The composed world transform at `node + 0x20`, position and orientation together.
            // Row 3 of that 64-byte matrix is `node + 0x50`; every writer of an emitter's
            // placement lands inside it:
            //
            //   sub_832325A8  `li r24,0x50` / `stvx128 v12,r31,r24`      the stated position
            //   sub_83231C40 -> sub_83228D78  `std r4,0x10(r31)` then
            //                  sub_8222E678 writing rows 0..3 at +0x20   the stated orientation
            //   sub_8322E4D0  `li r6,0x50` / `stvx128 v11,r31,r6`        PER TICK, type-5 path
            //   sub_83230880  `li r7,0x50` / `stvx128 v11,r31,r7`        PER TICK, type-9 spline
            //   sub_8322B090 / sub_8322B848                              the authored .pfx load
            //
            // So the hook is not the placement source and must not become one. `_stated` carries
            // only the two setters; the two per-tick writers run later in the frame from the
            // manager update (`sub_83234C38` -> `sub_83233890` -> vtable slot +36) and win, and an
            // effect whose owner never fires component message 10 keeps the .pfx-authored
            // transform without either setter being called. `_stated` also has no orientation.
            // It is a diagnostic only, reported through `refii_fx`.
            Transform3D placed = _roots.TryGetValue(key, out Transform3D root)
                ? root
                : ToGodot(fx.Position, fx.Rows);
            if (_stated.ContainsKey(key))
            {
                ++StatedPlacements;
            }
            else
            {
                ++WalkedPlacements;
            }

            node.Transform = Smoothed(key, placed);
            (float scale, float fade) = _emitterState.TryGetValue(key, out var state) ? state : (1f, 1f);
            node.Intensity = fx.Intensity * fade;
            node.SizeScale = scale;
        }

        // One absence is not a retirement. A walk that fills its buffer stops at the cap, and the
        // guest appends new nodes at the end of its vector and retires by swap-with-last, so
        // which effects fall past the cut changes every tick and the missing ones may be the
        // newest. Retirement waits AbsentWalksBeforeRetire absences.
        //
        // The grace is counted in refreshed walks, not display frames (the list only moves on the
        // guest's frame). It is bounded: never retiring while saturated would leak a host node
        // for every effect the guest drops.
        if (_draw && refreshed)
        {
            Retire();
        }

        if (HostBridge.ActiveFxSaturated && !_warnedSaturated)
        {
            _warnedSaturated = true;
            GD.PushWarning(
                $"[game-fx] the effect walk filled its buffer ({live.Count} nodes) — the guest " +
                "has more live particle nodes than are being read, so the newest effects are " +
                "being cut. Raise HostBridge.MaxActiveFx.");
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
                _absent.Remove(entry.Key);
                continue;
            }

            int absences = _absent.TryGetValue(entry.Key, out int n) ? n + 1 : 1;
            _absent[entry.Key] = absences;
            if (absences < AbsentWalksBeforeRetire)
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
            _absent.Remove(instance);
            _timelines.Remove(instance);
            _effectOf.Remove(instance);
            if (_nodes.Remove(instance, out FableFx? node) && IsInstanceValid(node))
            {
                node.QueueFree();
            }
        }
    }

    /// <summary>
    /// The transform to draw an effect at: the stated one on the tick timeline, with the same
    /// teleport rule as <see cref="GameEntities"/> — a jump over four metres or a gap of many
    /// ticks restarts the timeline rather than blending across it.
    /// </summary>
    private Transform3D Smoothed(uint key, Transform3D stated)
    {
        double now = Godot.Time.GetTicksUsec();
        if (!_timelines.TryGetValue(key, out Refii.Core.Engine_TickTimeline? timeline))
        {
            timeline = new Refii.Core.Engine_TickTimeline();
            timeline.Reset(_tickNow, stated);
            _timelines[key] = timeline;
            return stated;
        }

        bool moved = timeline.Newest.Origin.DistanceSquaredTo(stated.Origin) > 1e-8f
            || !timeline.Newest.Basis.IsEqualApprox(stated.Basis);
        if (moved)
        {
            if (timeline.Newest.Origin.DistanceTo(stated.Origin) > 4f ||
                (_tickNow > timeline.NewestTick && _tickNow - timeline.NewestTick > 8))
            {
                timeline.Reset(_tickNow, stated);
                return stated;
            }

            timeline.Push(_tickNow, stated, now);
        }

        return timeline.Sample(now);
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
