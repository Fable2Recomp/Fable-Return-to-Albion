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
    private double _tickNow;

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

    /// <summary>This walk's effects the guest draws itself (<see cref="FableFx.GuestDrawn"/>).</summary>
    private readonly HashSet<uint> _guestDrawn = new();

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

    public override void _EnterTree()
    {
        Current = this;
    }

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

    /// <summary>Every drawn effect's lights, nearest <paramref name="eye"/> first.</summary>
    public List<string> DescribeLights(Vector3 eye, int take)
    {
        var rows = new List<(float Distance, string Row)>();
        foreach (FableFx node in _nodes.Values)
        {
            if (!IsInstanceValid(node) || !node.IsInsideTree())
            {
                continue;
            }

            foreach (string row in node.DescribeLights(eye))
            {
                rows.Add((eye.DistanceTo(node.GlobalPosition), row));
            }
        }

        rows.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return rows.GetRange(0, System.Math.Min(take, rows.Count)).ConvertAll(r => r.Row);
    }

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
        Refii.Fx.Engine_FxFastPaths.Configure(config);

        // The environment wins: a headless harness may have no config.
        string forced = OS.GetEnvironment(DrawEnvironmentVariable);
        if (forced == "1" || forced == "0")
        {
            _draw = forced == "1";
        }

        // Under classic the title's particle path (Engine_ClassicParticles) draws every live
        // effect with its own shaders; drawing them here as well drew each one twice.
        bool classic = Refii.Models.Engine_ClassicMaterial.Active;
        _draw &= !classic;

        GD.Print(
            $"[game-fx] live effect feed up; drawing is {(_draw ? "ON" : "OFF")} " +
            (classic ? "(classic renderer: the title's particles draw them)"
                : $"({DrawSettingKey} in {(path.Length != 0 ? path : "refii.toml")})"));

        // A child, so its step follows this node's _Process in tree order — after the live
        // nodes are placed — and leaves with it on a level change; see Engine_FxDriver.
        AddChild(new Engine_FxDriver { Name = "FxDriver" });

        // The bank's textures decoded off-thread now, so a spell or impact that first names one
        // mid-play does not read and decode it on the frame it appears.
        if (_draw)
        {
            FableFx.PrefetchTextures();
        }
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
        Engine_FxRecipes.Begin();
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
                // A root nobody has positioned is identity at the origin: the node ctor
                // `sub_8322A228` seeds +0x20 with `sub_8223BDB0` (identity).
                if (fx.Position.IsFinite() && fx.Position.Length() < MaxFxReach &&
                    fx.Position != Vector3.Zero && !_roots.ContainsKey(rootKey))
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

        // The effects whose particle groups the guest is drawing itself (modern renderer): one
        // owned group is enough, the guest draws an effect's groups together.
        _guestDrawn.Clear();
        System.Collections.Generic.HashSet<uint> owners = Refii.Models.Engine_ClassicParticles.DrawnOwners;
        if (owners.Count != 0)
        {
            foreach (HostBridge.ActiveFx fx in live)
            {
                if (owners.Contains(fx.Instance))
                {
                    _guestDrawn.Add(fx.Handle != 0 ? fx.Handle : fx.Instance);
                }
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

            // Placed from the root only. `+0x20` is a node's OWN transform (quaternion +0x10,
            // row 3 +0x50); its world is `sub_832295F0`: +0x70 concatenated up the +240 parent
            // chain. Only the root, which has no parent, carries its world there; a child's +0x20
            // is local to its parent. An effect with no placed root this walk waits for one,
            // unmarked for the same reason as a refused position above.
            if (!_roots.TryGetValue(key, out Transform3D placed))
            {
                ++UnrootedFx;
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
                long madeFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                node = new FableFx
                {
                    Name = $"LiveFx_{key:X8}",

                    // Hash token form: the guest stores only the FNV-1 of the name.
                    EffectName = $"FX_{effect:X8}",
                };
                _nodes[key] = node;
                AddChild(node);
                Refii.Core.Engine_SpikeNotes.Since(Refii.Core.Engine_SpikeNotes.Kind.FxNode, madeFrom);
            }

            _effectOf[key] = effect;
            _seen.Add(key);
            _statedAt[key] = fx.Position;

            // The ROOT's transform at `node + 0x20`, position and orientation together, which
            // is its world because a root has no parent. Row 3 of that 64-byte matrix is
            // `node + 0x50`; every writer of an emitter's placement lands inside it:
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
            if (_stated.ContainsKey(key))
            {
                ++StatedPlacements;
            }
            else
            {
                ++WalkedPlacements;
            }

            // Written only when it moved: a node write propagates an instance update to every
            // surface, light and mesh under the effect, and a standing effect's smoothed
            // placement is the same transform every frame.
            Transform3D smoothed = Smoothed(key, placed);
            if (!node.Transform.IsEqualApprox(smoothed))
            {
                Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.Fx);
                node.Transform = smoothed;
            }
            (float scale, float fade) = _emitterState.TryGetValue(key, out var state) ? state : (1f, 1f);
            node.Intensity = fx.Intensity * fade;
            node.LightFade = fade;
            // The guest's element scale carries a mod effect's authored scale (the instance hooks,
            // Fable2/src/vfs/src/hooks/particle_dress.cpp); this node applies that factor itself
            // (FxEffect.Scale, Engine_FxSystem), so the size it takes from the guest is the game's.
            node.SizeScale = scale / AuthoredScale(effect);
            node.GuestDrawn = _guestDrawn.Contains(key);
            Engine_FxRecipes.Apply(node, effect, fx.Owner, fx.Attached, fx.Handle);
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
    /// <summary>A mod effect's authored scale (1 for every shipped effect), from the bank this feed draws with.</summary>
    private static float AuthoredScale(uint effect) =>
        FableFx.SharedBank()?.FindByHash(effect) is { Scale: > 0.0001f } e ? e.Scale : 1f;

    private Transform3D Smoothed(uint key, Transform3D stated)
    {
        if (!_timelines.TryGetValue(key, out Refii.Core.Engine_TickTimeline? timeline))
        {
            timeline = new Refii.Core.Engine_TickTimeline(stillWhenUnchanged: false);
            timeline.Reset(_tickNow, stated);
            _timelines[key] = timeline;
            return stated;
        }

        if (timeline.Newest.Origin.DistanceTo(stated.Origin) > 4f ||
            (_tickNow > timeline.NewestTick && _tickNow - timeline.NewestTick > 8))
        {
            timeline.Reset(_tickNow, stated);
            return stated;
        }

        timeline.Observe(_tickNow, stated);
        return timeline.Advance(Refii.Entities.Engine_DrawnFrame.NowUsec, Refii.Entities.Engine_DrawnFrame.DrawTick);
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
    /// Guest world space to Godot's, the same position map <see cref="GameEntities"/> uses (a
    /// sibling of the region needs the X negation; the region's contents do not). Rows are
    /// normalised guest axes: row 0 = local X, row 1 = Y, row 2 = Z.
    /// </summary>
    /// <remarks>
    /// The basis is the simulation's, not a mesh's. <see cref="Engine_FxSystem"/> holds a guest
    /// local (x, y, z) as node-local (x, z, y) — a swap, determinant -1 — so the node's columns
    /// are the guest rows in that order: world = x·row0 + z·row2 + y·row1. The entity meshes are
    /// converted (x, z, -y) and take `-row1` as their third column, which here would mirror the
    /// effect along its local Y. `Engine_LevelFx` converts with diag(1, 1, -1) for the same reason.
    /// </remarks>
    private static Transform3D ToGodot(Vector3 position, Vector3[] rows)
    {
        if (rows.Length < 3 || rows[0].IsZeroApprox() || rows[2].IsZeroApprox())
        {
            return new Transform3D(GuestIdentity, ToGodot(position));
        }

        var basis = new Basis(ToGodot(rows[0]), ToGodot(rows[2]), ToGodot(rows[1]));
        float determinant = basis.Determinant();

        // The range test is false for NaN, so a NaN or infinite basis needs its own check.
        if (!float.IsFinite(determinant) || determinant is < 0.01f and > -0.01f)
        {
            return new Transform3D(GuestIdentity, ToGodot(position));
        }

        return new Transform3D(basis, ToGodot(position));
    }

    /// <summary>The node basis of an unrotated guest placement: (-x, z, y) from the sim's (x, z, y).</summary>
    private static readonly Basis GuestIdentity = new(new Vector3(-1f, 0f, 0f), Vector3.Up, Vector3.Back);

    /// <summary>
    /// An effect node's transform for an unrotated effect at <paramref name="guestPosition"/> (the
    /// guest's world axes) — how a placement the guest states with no rows is drawn, and where a
    /// mod's <c>play_fx</c> puts its effect (Engine_FxPlayer).
    /// </summary>
    public static Transform3D UnrotatedAt(Vector3 guestPosition) => new(GuestIdentity, ToGodot(guestPosition));

    /// <summary>A guest-axes vector (position or offset) in Godot's axes.</summary>
    public static Vector3 GuestToGodot(Vector3 guest) => ToGodot(guest);

    /// <summary>Furthest an FX emitter may sit from the origin before its depth can overflow.</summary>
    private const float MaxFxReach = 1_000_000f;

    /// <summary>FX placements refused as not finite. Run total.</summary>
    public static int NonFiniteFx;

    /// <summary>Effects with a finite position beyond <see cref="MaxFxReach"/>. Run total.</summary>
    public static int OutOfReachFx;

    /// <summary>Effect sightings with no placed root to draw from. Run total.</summary>
    public static int UnrootedFx;

    private static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);
}
