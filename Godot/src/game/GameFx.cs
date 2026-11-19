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
    /// <summary><c>refii.toml</c> key for drawing the effects the guest plays (<see cref="Engine_LiveFx"/>).</summary>
    public const string DrawSettingKey = Engine_LiveFx.DrawSettingKey;

    /// <summary>Whether to log the census. Off by default.</summary>
    public const string ReportSettingKey = "fx_live_report";

    /// <summary>
    /// Single persisted state record per live guest effect handle (<c>rootKey</c>), consolidating
    /// node ownership, timeline smoothing, attachment targets, and weapon spline latch state so
    /// retirement and handle recycling always clean up every field together.
    /// </summary>
    private sealed class TrackedEffect
    {
        public FableFx Node = null!;
        public uint EffectHash;
        public readonly HashSet<uint> KnownInstances = new();
        public uint Owner;
        public uint Attached;
        public bool WasEmitting;
        public bool SawGuestEmitting;
        public int Absences;
        public Vector3 StatedGuestPos;
        public Transform3D LastRootTransform = Transform3D.Identity;
        public bool LastRootIdentity;
        public Refii.Core.Engine_TickTimeline? Timeline;
        public Vector3[]? LastStatedSpline;
        public int LastStatedSplineCount;
        public uint WeaponSplineTarget;
        public Vector3[]? WeaponSplineLocal;
        public Vector3[]? DrawnSplinePool;
        public bool HasSplineOverride;
    }

    /// <summary>Pooled per-walk snapshot gathered from the guest's active node vector before lifecycle/placement decisions.</summary>
    private sealed class WalkEffectSnapshot
    {
        public bool HasRoot;
        public Transform3D RootTransform = Transform3D.Identity;
        public bool RootIdentity;
        public Vector3 RootGuestPos;
        public bool HasAliveNode;
        public uint EffectHash;
        public uint ObservedOwner;
        public uint ObservedAttached;
        public bool AnyEmitting;
        public bool AnyGuestDrawn;
        public bool HasStatedSpline;
        public bool GuestSpline;
        public Vector3[] StatedSpline = new Vector3[32];
        public int StatedSplineCount;
        public bool HasEmitterState;
        public float Scale = 1f;
        public float Fade = 1f;
        public float Intensity = 1f;
        public Color StatedColour = Colors.White;
        public uint StatedFlushes;
        public bool SaturationMask;
        public readonly List<uint> AliveInstances = new();

        public void Reset()
        {
            HasRoot = false;
            RootTransform = Transform3D.Identity;
            RootIdentity = false;
            RootGuestPos = Vector3.Zero;
            HasAliveNode = false;
            EffectHash = 0u;
            ObservedOwner = 0u;
            ObservedAttached = 0u;
            AnyEmitting = false;
            AnyGuestDrawn = false;
            HasStatedSpline = false;
            GuestSpline = false;
            StatedSplineCount = 0;
            HasEmitterState = false;
            Scale = 1f;
            Fade = 1f;
            Intensity = 1f;
            StatedColour = Colors.White;
            StatedFlushes = 0u;
            SaturationMask = false;
            AliveInstances.Clear();
        }
    }

    /// <summary>Tracked live effect handles, keyed by guest handle pointer.</summary>
    private readonly Dictionary<uint, TrackedEffect> _tracked = new();

    /// <summary>The guest's sim tick as of this frame's walk, read once.</summary>
    private double _tickNow;

    private readonly HashSet<uint> _seen = new();

    /// <summary>Per-walk snapshots keyed by <c>rootKey</c>; pooled to avoid per-frame allocations.</summary>
    private readonly Dictionary<uint, WalkEffectSnapshot> _walkSnapshots = new();
    private readonly Stack<WalkEffectSnapshot> _snapshotPool = new();
    private readonly List<uint> _deadKeys = new();

    /// <summary>Maps each live particle-system instance pointer (<c>fx.Instance</c>) to its effect key.</summary>
    private readonly Dictionary<uint, uint> _keyByInstance = new();

    /// <summary>
    /// Bone-attached effects resolved directly onto their host character/weapon pose this display frame
    /// (in proper <c>SO(3)</c> entity-mesh space), keyed by effect handle.
    /// </summary>
    private readonly Dictionary<uint, (Transform3D StatedEntityRoot, Transform3D BoneEntityRoot, bool Unrotated)> _boneRoots = new();

    private readonly Vector3[] _guestSplineRead = new Vector3[32];

    /// <summary>Drawn Godot world-space spline override points resolved onto the live host weapon/entity this frame.</summary>
    private readonly Dictionary<uint, Vector3[]> _drawnSplineByKey = new();

    /// <summary>
    /// Keys the census has already counted this frame. Separate from <see cref="_seen"/>, which
    /// is what <see cref="Retire"/> keeps alive: an effect must be counted ONCE however many of
    /// its nodes we walk, but kept only if one of them actually handed us a usable transform.
    /// </summary>
    private readonly HashSet<uint> _counted = new();

    /// <summary>Scratch buffer for transforming world-space spline override endpoints into node-local space.</summary>
    private readonly Vector3[] _localSplineScratch = new Vector3[32];

    /// <summary>Every effect hash the guest has played, and how many instances carried it.</summary>
    private readonly Dictionary<uint, int> _census = new();

    private readonly HashSet<uint> _unresolved = new();

    private bool _draw;

    private bool _report;

    /// <summary>Whether the guest has ever reported a live effect.</summary>
    private bool _everSawAny;

    private readonly List<(FableFx Node, double RetireTime)> _retiringNodes = new();
    private bool _weaponHookInstalled;

    /// <summary>Roughly ten seconds at 60fps.</summary>
    private const int SilenceTicks = 600;

    private int _ticks;

    private int _peakLive;

    private int _lastReportedDistinct = -1;

    /// <summary>Whether the buffer-full warning has been printed; it would otherwise be per-frame.</summary>
    private bool _warnedSaturated;

    /// <summary>
    /// How many refreshed walks an effect must be absent from before its node is freed: enough to
    /// ride out the buffer cut moving with the guest's swap-with-last retirement, short enough
    /// (100 ms at the 30 Hz step) not to hold a dead effect on screen.
    /// </summary>
    private const int AbsentWalksBeforeRetire = 3;

    /// <summary>
    /// Live guest effects do not use <see cref="FableFx"/>'s synthetic 1.5s preview replay loop:
    /// continuous guest effects loop via their authored emitter lifetimes, while one-shot effects
    /// (footsteps, splashes) must play once and stop.
    /// </summary>
    public const bool LiveFxDefaultLoop = false;

    /// <summary>
    /// Grace window in guest ticks (<see cref="Refii.Core.Engine_TickClock.GuestGameTick"/>, ~15 Hz)
    /// allowing retiring nodes' already-spawned particles to decay naturally (~1.5s at 15 Hz).
    /// </summary>
    public const double RetireGraceTicks = 22.5;

    /// <summary>
    /// Returns true once an effect absent from recent walks should retire: immediately (1 absence)
    /// when host-guest telemetry is healthy, or after <see cref="AbsentWalksBeforeRetire"/> walks
    /// only when <paramref name="activeFxSaturated"/> indicates swap-with-last overflow churn.
    /// </summary>
    public static bool ShouldRetireOnAbsence(int absences, bool activeFxSaturated) =>
        absences >= (activeFxSaturated ? AbsentWalksBeforeRetire : 1);

    /// <summary>
    /// Returns true when a retiring effect still holds live particles that should drain asynchronously
    /// (soft-stopped in guest, or carrying detached world-space particles); hard-destroyed
    /// <c>AttachToRoot</c>-only bursts recycle immediately so absorbed FX do not linger.
    /// </summary>
    public static bool ShouldDrainOnRetire(bool wasEmitting, bool hasDetachedParticles, int debugAlive) =>
        debugAlive > 0 && (!wasEmitting || hasDetachedParticles);

    /// <summary>
    /// Resolves the updated attachment target for an effect handle: latches a non-zero
    /// <paramref name="incomingAttached"/> target, retains <paramref name="currentAttached"/> if a
    /// temporary emitter entity was destroyed (<paramref name="owner"/> == 0), and clears it when
    /// a live emitter component (<paramref name="owner"/> != 0) explicitly states
    /// <paramref name="incomingAttached"/> == 0.
    /// </summary>
    public static uint ResolveAttachedTarget(uint currentAttached, uint owner, uint incomingAttached) =>
        incomingAttached != 0u
            ? incomingAttached
            : (owner != 0u ? 0u : currentAttached);

    /// <summary>
    /// Updates the persisted attachment target for <paramref name="rootKey"/> using
    /// <see cref="ResolveAttachedTarget"/>.
    /// </summary>
    public static void TrackAttachedTarget(
        Dictionary<uint, uint> attachedByKey, uint rootKey, uint owner, uint attached)
    {
        uint current = attachedByKey.TryGetValue(rootKey, out uint existing) ? existing : 0u;
        uint next = ResolveAttachedTarget(current, owner, attached);
        if (next != 0u)
        {
            attachedByKey[rootKey] = next;
        }
        else
        {
            attachedByKey.Remove(rootKey);
        }
    }

    /// <summary>
    /// Returns true when an already-tracked guest handle (<c>rootKey</c>) has been recycled for a
    /// new effect allocation — either because the effect hash changed, or (when the active FX
    /// buffer is not saturated) all live child <c>fx.Instance</c> pointers are disjoint from
    /// previously seen instances of this handle, or emission re-armed
    /// (<c>!wasEmitting &amp;&amp; incomingAnyEmitting</c>).
    /// </summary>
    public static bool ShouldRecycleTracked(
        uint trackedEffect,
        uint incomingEffect,
        bool hasKnownInstances,
        bool hasInstanceOverlap,
        bool wasEmitting,
        bool incomingAnyEmitting,
        bool bufferSaturated = false) =>
        trackedEffect != incomingEffect ||
        (!bufferSaturated &&
            ((hasKnownInstances && !hasInstanceOverlap) ||
             (!wasEmitting && incomingAnyEmitting)));

    /// <summary>
    /// Resolves whether a live <see cref="FableFx"/> node should keep emission open on this walk.
    /// If the guest already closed a short/instant burst's emission window before the host node
    /// stepped once (<c>!sawGuestEmitting &amp;&amp; debugSpawned == 0</c>), keep emission open
    /// until the initial burst spawns so <c>InitialNum</c> is not cleared before frame 1.
    /// </summary>
    public static bool ResolveNodeEmitting(
        bool incomingAnyEmitting, bool sawGuestEmitting, int debugSpawned) =>
        incomingAnyEmitting || (!sawGuestEmitting && debugSpawned == 0);

    /// <summary>
    /// Returns true when a latched weapon spline no longer matches the effect's current attachment
    /// target (detached to <c>0</c> or re-attached to a different entity).
    /// </summary>
    public static bool ShouldClearWeaponSpline(uint currentAttached, uint weaponSplineTarget) =>
        weaponSplineTarget != 0u && currentAttached != weaponSplineTarget;

    /// <summary>
    /// Converts stated world-space spline override endpoints into <paramref name="targetStated"/>'s
    /// local space, rejecting degenerate or non-finite target transforms.
    /// </summary>
    public static bool TryLatchLocalSpline(
        Transform3D targetStated, ReadOnlySpan<Vector3> worldPoints, out Vector3[] localPoints)
    {
        localPoints = Array.Empty<Vector3>();
        if (worldPoints.Length < 2)
        {
            return false;
        }

        float det = targetStated.Basis.Determinant();
        if (!float.IsFinite(det) || Mathf.Abs(det) <= 1e-6f)
        {
            return false;
        }

        Transform3D toTargetLocal = targetStated.AffineInverse();
        var result = new Vector3[worldPoints.Length];
        for (int i = 0; i < worldPoints.Length; i++)
        {
            Vector3 local = toTargetLocal * worldPoints[i];
            if (!local.IsFinite())
            {
                return false;
            }
            result[i] = local;
        }

        localPoints = result;
        return true;
    }

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

        foreach (TrackedEffect tracked in _tracked.Values)
        {
            if (IsInstanceValid(tracked.Node))
            {
                tracked.Node.QueueFree();
            }
        }
        _tracked.Clear();

        foreach (var (rNode, _) in _retiringNodes)
        {
            if (IsInstanceValid(rNode))
            {
                rNode.QueueFree();
            }
        }
        _retiringNodes.Clear();

        ReleaseWalkSnapshots();
        _keyByInstance.Clear();
        _boneRoots.Clear();
        _drawnSplineByKey.Clear();
    }

    /// <summary>
    /// The drawn effects nearest <paramref name="eye"/>, one line each: handle, effect, distance,
    /// position, the guest's intensity, and what our simulation holds for it.
    /// </summary>
    public List<string> Describe(Vector3 eye, int take)
    {
        FxBank? bank = FableFx.SharedBank();
        var rows = new List<(float Distance, uint Key, TrackedEffect Tracked)>();
        foreach ((uint key, TrackedEffect tracked) in _tracked)
        {
            if (IsInstanceValid(tracked.Node) && tracked.Node.IsInsideTree())
            {
                rows.Add((eye.DistanceTo(tracked.Node.GlobalPosition), key, tracked));
            }
        }

        rows.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var described = new List<string>();
        foreach ((float distance, uint key, TrackedEffect tracked) in rows.GetRange(0, System.Math.Min(take, rows.Count)))
        {
            FableFx node = tracked.Node;
            Vector3 p = node.GlobalPosition;
            uint effect = tracked.EffectHash;

            // The guest's stated position beside where we put the node: zero here means the
            // bridge's transform walk produced nothing; non-zero with an origin node means this
            // feed dropped it.
            Vector3 g = tracked.StatedGuestPos;
            string stated = g != Vector3.Zero
                ? $" guest=({g.X:0.0},{g.Y:0.0},{g.Z:0.0})"
                : " guest=none";
            string name = bank?.FindByHash(effect)?.Name ?? string.Empty;
            described.Add(
                $"{key:X8} FX_{effect:X8}{(name.Length != 0 ? " " + name : string.Empty)} d={distance:0.0} " +
                $"at=({p.X:0.0},{p.Y:0.0},{p.Z:0.0}) intensity={node.Intensity:0.000} scale={node.SizeScale:0.00} " +
                $"colour=({node.StatedColour.R:0.00},{node.StatedColour.G:0.00},{node.StatedColour.B:0.00},{node.StatedColour.A:0.00}) " +
                $"alive={node.DebugAlive} spawned={node.DebugSpawned} surfaces={node.DebugSurfaces}/{node.DebugAddSurfaces}/{node.DebugDispSurfaces} " +
                $"built={node.DebugBuilt} em/vis/trail/parts={node.DebugCensus.Emitters}/{node.DebugCensus.Visuals}/" +
                $"{node.DebugCensus.TrailVisuals}/{node.DebugCensus.Particles}{stated}");
        }

        return described;
    }

    public int DrawnCount => _tracked.Count;

    /// <summary>
    /// Live effects that a LEVEL-spawned node (<see cref="Refii.Level.Engine_LevelFx"/>) of the same
    /// effect also stands within <paramref name="radius"/> of — one effect drawn twice. Returns the
    /// count and up to <paramref name="take"/> of them.
    /// </summary>
    public (int Doubles, int LevelNodes, List<string> Rows) DescribeLevelDoubles(float radius, int take)
    {
        var mine = new HashSet<FableFx>(_tracked.Count);
        foreach (TrackedEffect tracked in _tracked.Values)
        {
            mine.Add(tracked.Node);
        }

        var level = new List<FableFx>();
        var pending = new Stack<Node>();
        pending.Push(GetTree().Root);
        while (pending.Count != 0)
        {
            Node walk = pending.Pop();
            if (walk is FableFx candidate && !mine.Contains(candidate) && candidate.IsInsideTree())
            {
                level.Add(candidate);
            }

            foreach (Node child in walk.GetChildren())
            {
                pending.Push(child);
            }
        }

        int doubles = 0;
        var rows = new List<string>();
        foreach ((uint key, TrackedEffect tracked) in _tracked)
        {
            FableFx node = tracked.Node;
            if (!IsInstanceValid(node) || !node.IsInsideTree())
            {
                continue;
            }

            Vector3 at = node.GlobalPosition;
            foreach (FableFx other in level)
            {
                if (other.EffectName == node.EffectName && other.GlobalPosition.DistanceTo(at) <= radius)
                {
                    ++doubles;
                    if (rows.Count < take)
                    {
                        rows.Add($"{key:X8} {node.EffectName} at=({at.X:0.0},{at.Y:0.0},{at.Z:0.0}) level={other.Name} d={other.GlobalPosition.DistanceTo(at):0.00}");
                    }

                    break;
                }
            }
        }

        return (doubles, level.Count, rows);
    }

    /// <summary>Every drawn effect's lights, nearest <paramref name="eye"/> first.</summary>
    public List<string> DescribeLights(Vector3 eye, int take)
    {
        var rows = new List<(float Distance, string Row)>();
        foreach (TrackedEffect tracked in _tracked.Values)
        {
            FableFx node = tracked.Node;
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

    /// <summary>Pulls the stated transforms in for this walk when diagnostic reporting is enabled.</summary>
    private void RefreshStated()
    {
        _stated.Clear();
        if (!_report || Refii.Host.HostBridge.GetStatedFx(_statedRows) == 0)
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
        if (!_tracked.TryGetValue(instance, out TrackedEffect? tracked) || !IsInstanceValid(tracked.Node))
        {
            return null;
        }

        Vector3 at = tracked.Node.GlobalPosition;
        return new[] { at.X, at.Y, at.Z };
    }

    public override void _Ready()
    {
        System.Collections.Generic.IReadOnlyDictionary<string, string> config =
            Refii.Core.Engine_RefiiConfig.Read(out string path);

        _draw = Engine_LiveFx.Draws(config);
        _report = Refii.Core.Engine_RefiiConfig.Bool(config, ReportSettingKey, false);
        Refii.Fx.Engine_FxFastPaths.Configure(config);
        ConfigureSpawns(config);

        // Under classic the title's particle path (Engine_ClassicParticles) draws every live
        // effect with its own shaders; drawing them here as well drew each one twice.
        bool classic = Refii.Models.Engine_ClassicMaterial.Active;
        _draw &= !classic;

        GD.Print(
            $"[game-fx] live effect feed up; drawing is {(_draw ? "ON" : "OFF")} " +
            (classic ? "(classic renderer: the title's particles draw them)"
                : $"({DrawSettingKey} in {(path.Length != 0 ? path : Refii.Core.Engine_Title.Active.ConfigFileName)})"));

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

    private WalkEffectSnapshot GetOrRentSnapshot(uint rootKey)
    {
        if (_walkSnapshots.TryGetValue(rootKey, out WalkEffectSnapshot? existing))
        {
            return existing;
        }

        WalkEffectSnapshot snap = _snapshotPool.Count != 0 ? _snapshotPool.Pop() : new WalkEffectSnapshot();
        snap.Reset();
        _walkSnapshots[rootKey] = snap;
        return snap;
    }

    private void ReleaseWalkSnapshots()
    {
        foreach (WalkEffectSnapshot snap in _walkSnapshots.Values)
        {
            snap.Reset();
            _snapshotPool.Push(snap);
        }

        _walkSnapshots.Clear();
    }

    private void RetireTracked(uint key, TrackedEffect tracked)
    {
        _tracked.Remove(key);
        RetireTrackedNode(tracked);
    }

    private void RetireTrackedNode(TrackedEffect tracked)
    {
        FableFx node = tracked.Node;
        if (!IsInstanceValid(node))
        {
            return;
        }

        node.Emitting = false;
        node.Loop = false;
        node.SuppressLights = true;
        if (ShouldDrainOnRetire(tracked.WasEmitting, node.HasDetachedParticles, node.DebugAlive))
        {
            _retiringNodes.Add((node, _tickNow));
        }
        else
        {
            node.QueueFree();
        }
    }

    public override void _Process(double delta)
    {
        using var _framePhase = Refii.Core.Engine_FramePhases.Measure("gamefx");
        if (HostBridge.TryGetAbiVersion() is null)
        {
            return;
        }

        _ticks++;
        _tickNow = Refii.Core.Engine_TickClock.GuestGameTick;

        if (!_weaponHookInstalled && HostBridge.IsGuestUp)
        {
            _weaponHookInstalled = HostBridge.InstallWeaponAugmentHook();
        }

        // Clean up retired nodes that have completed particle decay (or if the tick clock reset backwards on load)
        for (int i = _retiringNodes.Count - 1; i >= 0; i--)
        {
            var (rNode, rTime) = _retiringNodes[i];
            if (!IsInstanceValid(rNode) || rNode.DebugAlive == 0 || _tickNow < rTime || (_tickNow - rTime) > RetireGraceTicks)
            {
                if (IsInstanceValid(rNode))
                {
                    rNode.QueueFree();
                }
                _retiringNodes.RemoveAt(i);
            }
        }

        // On the guest's frame: the effect list is the guest's statement and only moves when
        // the guest does; between two, the last list stands and only host-attached bone/weapon
        // effects and unsettled moving world effects need display-rate transform updates.
        bool refreshed = Refii.Core.Engine_GuestFrame.Changed || _liveFx.Count == 0;
        if (!refreshed)
        {
            StepIntermediateDisplayFrame();
            Report();
            return;
        }

        List<HostBridge.ActiveFx> live = HostBridge.GetActiveFx(_liveFx);
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
        _spawns.BeginWalk();
        RefreshStated();

        // SEPARATION OF CONCERNS BETWEEN MODERN AND CLASSIC PARTICLE SYSTEMS:
        // - Modern mode (`!Engine_ClassicMaterial.Active`) uses `FableFx` + `Engine_FxSystem`
        //   exclusively for all billboard, ribbon, trail, displacement, and light effects across
        //   0..200m (`GuestDrawn = false`). `GameSession._Process` already pumps `Engine_ClassicViews`
        //   and `Engine_FxMeshTwins` once per frame before `GameFx._Process`.
        // - Classic mode (`Engine_ClassicMaterial.Active`) uses `Engine_ClassicParticles` exclusively
        //   (pumped from `Engine_ClassicPath.Pump`), while sharing pure static math/table helpers
        //   on `Engine_FxSystem`.
        HashSet<uint>? classicOwners = Refii.Models.Engine_ClassicMaterial.Active
            ? Refii.Models.Engine_ClassicParticles.DrawnOwners
            : null;

        // Stage 1: Gather per-handle walk snapshots (root transform, alive non-root nodes,
        // owner/attachment targets, active spline overrides, and Classic drawn-owner state).
        // Non-root rows with `fx.Alive == false` are skipped so pooled/dying guest nodes cannot
        // pollute snapshots or resurrect stale attachments.
        ReleaseWalkSnapshots();
        _keyByInstance.Clear();
        _boneRoots.Clear();
        _drawnSplineByKey.Clear();
        foreach (HostBridge.ActiveFx fx in live)
        {
            uint rootKey = fx.Handle != 0 ? fx.Handle : fx.Instance;
            if (fx.Instance != 0)
            {
                _keyByInstance[fx.Instance] = rootKey;
            }

            WalkEffectSnapshot snap = GetOrRentSnapshot(rootKey);
            if (classicOwners is { Count: > 0 } && classicOwners.Contains(fx.Instance))
            {
                snap.AnyGuestDrawn = true;
            }

            if (fx.Root)
            {
                // A root nobody has positioned is identity at the origin: the node ctor
                // `sub_8322A228` seeds +0x20 with `sub_8223BDB0` (identity).
                if (!snap.HasRoot &&
                    fx.Position.IsFinite() && fx.Position.Length() < MaxFxReach &&
                    fx.Position != Vector3.Zero)
                {
                    snap.HasRoot = true;
                    snap.RootTransform = ToGodot(fx.Position, fx.Rows);
                    snap.RootIdentity = IsExactGuestIdentityRows(fx.Rows);
                    snap.RootGuestPos = fx.Position;
                }

                continue;
            }

            if (!fx.Alive)
            {
                continue;
            }

            if (fx.Instance != 0)
            {
                snap.AliveInstances.Add(fx.Instance);
            }

            snap.AnyEmitting |= fx.Emitting;
            if (fx.Owner != 0u && snap.ObservedOwner == 0u)
            {
                snap.ObservedOwner = fx.Owner;
            }

            if (fx.Attached != 0u && snap.ObservedAttached == 0u)
            {
                snap.ObservedAttached = fx.Attached;
            }

            FxEmitter? emitterDef = fx.NamesEffect ? null : bank?.EmitterForType(fx.NameHash);

            // Runtime spline override endpoints on a Type 5 `CParticleSplineNode` (`sub_8322E858`),
            // exported by `RefiiHostGetActiveFx` with flag 32 (`fx.SplineOverride`) in `fx.Rows[0..count-1]`
            // and `fx.Scale = count`. Requiring `fx.SplineOverride` prevents regular Type 5 rotation
            // matrices from ever being misread as world-space spline points.
            if (fx.SplineOverride &&
                emitterDef is { TypeId: 5 } &&
                float.IsFinite(fx.Scale) && fx.Scale is >= 2f and <= 3.5f &&
                !snap.HasStatedSpline)
            {
                int spCount = Math.Clamp((int)MathF.Round(fx.Scale), 2, 3);
                bool finitePts = fx.Rows.Length >= spCount;
                for (int p = 0; p < spCount && finitePts; p++)
                {
                    if (!fx.Rows[p].IsFinite() || fx.Rows[p].Length() >= MaxFxReach)
                    {
                        finitePts = false;
                    }
                }

                if (finitePts)
                {
                    for (int p = 0; p < spCount; p++)
                    {
                        snap.StatedSpline[p] = ToGodot(fx.Rows[p]);
                    }

                    snap.HasStatedSpline = true;
                    snap.GuestSpline = false;
                    snap.StatedSplineCount = spCount;
                }
            }

            // A spline setter's points (Fable 3: an energy beam's ends, an arc's list), guest world.
            if (fx.SplineStated && !snap.HasStatedSpline)
            {
                int count = HostBridge.GetFxSpline(fx.Handle, _guestSplineRead);
                bool usable = count >= 2 && count <= snap.StatedSpline.Length;
                for (int p = 0; p < count && usable; p++)
                {
                    usable = _guestSplineRead[p].IsFinite() && _guestSplineRead[p].Length() < MaxFxReach;
                }

                if (usable)
                {
                    for (int p = 0; p < count; p++)
                    {
                        snap.StatedSpline[p] = ToGodot(_guestSplineRead[p]);
                    }

                    snap.HasStatedSpline = true;
                    snap.GuestSpline = true;
                    snap.StatedSplineCount = count;
                }
            }

            // Scale and fade live on the element, and only a standard emitter's slots mean
            // anything (a spline or light node's +412 holds something else). The bank says which
            // type a node is. A row that names its effect carries the effect's own stated values.
            if (!snap.HasEmitterState &&
                (fx.NamesEffect || emitterDef is { TypeId: 0 or 9 }) &&
                float.IsFinite(fx.Scale) && fx.Scale > 0f && fx.Scale < 100f &&
                float.IsFinite(fx.Fade))
            {
                snap.HasEmitterState = true;
                snap.Scale = fx.Scale;
                snap.Fade = Math.Clamp(fx.Fade, 0f, 1f);
            }

            // The instance names its type, not its effect; the bank holds the type->effect join.
            uint effect = fx.NamesEffect ? fx.NameHash : bank?.EffectForType(fx.NameHash) ?? 0u;
            bool known = effect != 0 && bank?.FindByHash(effect) is not null;
            if (!known)
            {
                // The synthetic per-type-vector root belongs to no effect; not a miss.
                if (fx.NameHash != FxBank.ReservedRootType)
                {
                    // The list holds every particle node (emitters, attractors, splines), not only
                    // effects; counted, not warned.
                    _unresolved.Add(fx.NameHash);
                }
                continue;
            }

            if (_counted.Add(rootKey))
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

            if (!snap.HasAliveNode)
            {
                snap.HasAliveNode = true;
                snap.EffectHash = effect;
                snap.Intensity = fx.Intensity;
                snap.StatedColour = fx.StatedColour ?? Colors.White;
                snap.StatedFlushes = fx.StatedFlushes;
                snap.SaturationMask = fx.SaturationMask;
            }
        }

        // Stage 2: Detect recycled guest handles BEFORE attachment resolution or placement so an
        // incoming allocation never inherits stale owners, weapon splines, timelines, or FableFx nodes.
        if (_draw)
        {
            foreach ((uint key, WalkEffectSnapshot snap) in _walkSnapshots)
            {
                if (!snap.HasAliveNode || !_tracked.TryGetValue(key, out TrackedEffect? tracked))
                {
                    continue;
                }

                if (!IsInstanceValid(tracked.Node))
                {
                    _tracked.Remove(key);
                    continue;
                }

                bool hasOverlap = false;
                if (tracked.KnownInstances.Count > 0)
                {
                    for (int i = 0; i < snap.AliveInstances.Count; i++)
                    {
                        if (tracked.KnownInstances.Contains(snap.AliveInstances[i]))
                        {
                            hasOverlap = true;
                            break;
                        }
                    }
                }

                if (ShouldRecycleTracked(
                        tracked.EffectHash,
                        snap.EffectHash,
                        tracked.KnownInstances.Count > 0,
                        hasOverlap,
                        tracked.WasEmitting,
                        snap.AnyEmitting,
                        HostBridge.ActiveFxSaturated))
                {
                    RetireTracked(key, tracked);
                }
            }

            // Stage 3a: Spawn or refresh tracked handles that have both an alive node and a valid root.
            foreach ((uint key, WalkEffectSnapshot snap) in _walkSnapshots)
            {
                if (!snap.HasAliveNode)
                {
                    continue;
                }

                if (!snap.HasRoot)
                {
                    ++UnrootedFx;
                    continue;
                }

                if (!_tracked.TryGetValue(key, out TrackedEffect? tracked))
                {
                    // Over this walk's budget of new nodes (a phone's): built on a later walk, unseen till then.
                    if (SpawnDeferred(key))
                    {
                        continue;
                    }

                    long madeFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                    var newNode = new FableFx
                    {
                        // Hash token form: the guest stores only the FNV-1 of the name.
                        EffectName = $"FX_{snap.EffectHash:X8}",
                        Loop = LiveFxDefaultLoop,
                        Emitting = true,
                    };
                    if (!SpawnsUnnamed)
                    {
                        newNode.Name = $"LiveFx_{key:X8}";
                    }
                    AddChild(newNode);
                    Refii.Core.Engine_SpikeNotes.Since(Refii.Core.Engine_SpikeNotes.Kind.FxNode, madeFrom);

                    tracked = new TrackedEffect
                    {
                        Node = newNode,
                        EffectHash = snap.EffectHash,
                        SawGuestEmitting = snap.AnyEmitting,
                    };
                    _tracked[key] = tracked;
                }

                tracked.EffectHash = snap.EffectHash;
                tracked.WasEmitting = snap.AnyEmitting;
                tracked.SawGuestEmitting |= snap.AnyEmitting;
                tracked.Absences = 0;
                tracked.StatedGuestPos = snap.RootGuestPos;
                tracked.LastRootTransform = snap.RootTransform;
                tracked.LastRootIdentity = snap.RootIdentity;
                for (int i = 0; i < snap.AliveInstances.Count; i++)
                {
                    tracked.KnownInstances.Add(snap.AliveInstances[i]);
                }

                tracked.Owner = snap.ObservedOwner;
                tracked.Attached = ResolveAttachedTarget(tracked.Attached, snap.ObservedOwner, snap.ObservedAttached);
                if (ShouldClearWeaponSpline(tracked.Attached, tracked.WeaponSplineTarget))
                {
                    tracked.WeaponSplineTarget = 0u;
                    tracked.WeaponSplineLocal = null;
                }

                if (snap.HasStatedSpline && snap.StatedSplineCount >= 2)
                {
                    if (tracked.LastStatedSpline is null || tracked.LastStatedSpline.Length != snap.StatedSplineCount)
                    {
                        tracked.LastStatedSpline = new Vector3[snap.StatedSplineCount];
                    }
                    Array.Copy(snap.StatedSpline, tracked.LastStatedSpline, snap.StatedSplineCount);
                    tracked.LastStatedSplineCount = snap.StatedSplineCount;
                }
                else
                {
                    tracked.LastStatedSplineCount = 0;
                }

                _seen.Add(key);
            }
        }

        // Stage 3b: Resolve CECObjectAttachment bone mounts (also needed in Classic mode via `_boneRoots`)
        // and multi-dummy weapon spline overrides directly against live entity poses.
        UpdateAttachedPlacements();

        // Stage 3c: Place nodes, update simulation state/recipes, and retire absent handles.
        if (_draw)
        {
            foreach ((uint key, WalkEffectSnapshot snap) in _walkSnapshots)
            {
                if (!_seen.Contains(key) || !_tracked.TryGetValue(key, out TrackedEffect? tracked))
                {
                    continue;
                }

                if (_stated.ContainsKey(key))
                {
                    ++StatedPlacements;
                }
                else
                {
                    ++WalkedPlacements;
                }

                ReadOnlySpan<Vector3> statedSpline = snap.HasStatedSpline && snap.StatedSplineCount >= 2
                    ? snap.StatedSpline.AsSpan(0, snap.StatedSplineCount)
                    : ReadOnlySpan<Vector3>.Empty;
                ReadOnlySpan<Vector3> drawnSpline = _drawnSplineByKey.TryGetValue(key, out Vector3[]? dSpline) && dSpline.Length >= 2
                    ? dSpline
                    : ReadOnlySpan<Vector3>.Empty;
                bool hasBone = _boneRoots.TryGetValue(key, out var bonePlacement);

                ApplyNodePlacement(
                    tracked,
                    statedSpline,
                    drawnSpline,
                    hasBone,
                    bonePlacement,
                    snap.RootTransform,
                    refreshed: true);

                FableFx node = tracked.Node;
                float scale = snap.HasEmitterState ? snap.Scale : 1f;
                float fade = snap.HasEmitterState ? snap.Fade : 1f;
                node.Emitting = ResolveNodeEmitting(snap.AnyEmitting, tracked.SawGuestEmitting, node.DebugSpawned);
                node.SuppressLights = false;
                node.Intensity = snap.Intensity * fade;
                node.LightFade = fade;
                // The guest's element scale carries a mod effect's authored scale (the instance hooks,
                // Fable2/src/vfs/src/hooks/particle_dress.cpp); this node applies that factor itself
                // (FxEffect.Scale, Engine_FxSystem), so the size it takes from the guest is the game's.
                node.SizeScale = scale / AuthoredScale(snap.EffectHash);
                node.StatedColour = snap.StatedColour;
                node.StatedFlushes = snap.StatedFlushes;
                node.StatedSaturationMask = snap.SaturationMask;
                node.GuestDrawn |= snap.AnyGuestDrawn;
                Engine_FxRecipes.Apply(node, snap.EffectHash, tracked.Owner, tracked.Attached, key);
            }

            _spawns.EndWalk();

            // One absence is not a retirement. A walk that fills its buffer stops at the cap, and the
            // guest appends new nodes at the end of its vector and retires by swap-with-last, so
            // which effects fall past the cut changes every tick and the missing ones may be the
            // newest. Retirement waits AbsentWalksBeforeRetire absences.
            //
            // The grace is counted in refreshed walks, not display frames (the list only moves on the
            // guest's frame). It is bounded: never retiring while saturated would leak a host node
            // for every effect the guest drops.
            Retire();
        }
        else
        {
            _spawns.EndWalk();
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
    /// Fast path for intermediate display frames (<c>!Engine_GuestFrame.Changed</c>): updates only
    /// host-attached bone/weapon effects and unsettled moving world timelines without re-walking
    /// the guest buffer or churning dictionaries.
    /// </summary>
    private void StepIntermediateDisplayFrame()
    {
        if (!_draw || _tracked.Count == 0)
        {
            return;
        }

        GameEntities? entities = GameEntities.Current;
        _drawnSplineByKey.Clear();

        foreach ((uint rootKey, TrackedEffect tracked) in _tracked)
        {
            if (!IsInstanceValid(tracked.Node))
            {
                continue;
            }

            bool hasWeaponSpline = tracked.WeaponSplineLocal is { Length: >= 2 };
            bool hasAttachment = tracked.Owner != 0u || tracked.Attached != 0u;
            bool hasTimeline = tracked.Timeline is { Settled: false };
            if (!hasWeaponSpline && !hasAttachment && !hasTimeline)
            {
                continue;
            }

            ReadOnlySpan<Vector3> drawnSpline = entities is not null && ResolveDrawnWeaponSpline(tracked, entities, rootKey, out Vector3[]? dSpline) && dSpline is { Length: >= 2 }
                ? dSpline
                : ReadOnlySpan<Vector3>.Empty;

            bool hasBone = false;
            var bonePlacement = default((Transform3D StatedEntityRoot, Transform3D BoneEntityRoot, bool Unrotated));
            if (drawnSpline.Length < 2 && hasAttachment && entities is not null)
            {
                hasBone = ResolveBoneRoot(
                    entities,
                    rootKey,
                    tracked.Owner,
                    tracked.Attached,
                    tracked.LastRootTransform,
                    tracked.LastRootIdentity,
                    out bonePlacement);
            }

            ReadOnlySpan<Vector3> statedSpline = tracked.LastStatedSpline is not null && tracked.LastStatedSplineCount >= 2
                ? tracked.LastStatedSpline.AsSpan(0, tracked.LastStatedSplineCount)
                : ReadOnlySpan<Vector3>.Empty;

            ApplyNodePlacement(
                tracked,
                statedSpline,
                drawnSpline,
                hasBone,
                bonePlacement,
                tracked.LastRootTransform,
                refreshed: false);
        }
    }

    private void UpdateAttachedPlacements()
    {
        if (GameEntities.Current is not { } entities)
        {
            return;
        }

        foreach ((uint rootKey, WalkEffectSnapshot snap) in _walkSnapshots)
        {
            TrackedEffect? tracked = _tracked.TryGetValue(rootKey, out TrackedEffect? t) ? t : null;
            uint owner = tracked?.Owner ?? snap.ObservedOwner;
            uint attached = tracked?.Attached ?? snap.ObservedAttached;

            if (tracked is not null &&
                attached != 0u &&
                !snap.GuestSpline &&
                snap.HasStatedSpline &&
                snap.StatedSplineCount >= 2 &&
                (tracked.WeaponSplineLocal is null ||
                 tracked.WeaponSplineTarget != attached ||
                 tracked.WeaponSplineLocal.Length != snap.StatedSplineCount) &&
                entities.TryGetStatedAndDrawnTransform(attached, out Transform3D targetStated, out _) &&
                TryLatchLocalSpline(targetStated, snap.StatedSpline.AsSpan(0, snap.StatedSplineCount), out Vector3[] localPts))
            {
                tracked.WeaponSplineTarget = attached;
                tracked.WeaponSplineLocal = localPts;
            }

            if (tracked is not null)
            {
                ResolveDrawnWeaponSpline(tracked, entities, rootKey, out _);
            }

            if (snap.HasRoot && (owner != 0u || attached != 0u))
            {
                ResolveBoneRoot(entities, rootKey, owner, attached, snap.RootTransform, snap.RootIdentity, out _);
            }
        }
    }

    private bool ResolveDrawnWeaponSpline(
        TrackedEffect tracked, GameEntities entities, uint rootKey, out Vector3[]? drawnPts)
    {
        drawnPts = null;
        if (ShouldClearWeaponSpline(tracked.Attached, tracked.WeaponSplineTarget))
        {
            tracked.WeaponSplineTarget = 0u;
            tracked.WeaponSplineLocal = null;
            return false;
        }

        if (tracked.WeaponSplineLocal is not { Length: >= 2 } localSpline ||
            !entities.TryGetStatedAndDrawnTransform(tracked.WeaponSplineTarget, out _, out Transform3D currentTargetDrawn))
        {
            return false;
        }

        if (tracked.DrawnSplinePool is null || tracked.DrawnSplinePool.Length != localSpline.Length)
        {
            tracked.DrawnSplinePool = new Vector3[localSpline.Length];
        }

        for (int i = 0; i < localSpline.Length; i++)
        {
            tracked.DrawnSplinePool[i] = currentTargetDrawn * localSpline[i];
        }

        drawnPts = tracked.DrawnSplinePool;
        _drawnSplineByKey[rootKey] = drawnPts;
        return true;
    }

    private bool ResolveBoneRoot(
        GameEntities entities,
        uint rootKey,
        uint owner,
        uint attached,
        Transform3D statedFxRoot,
        bool unrotated,
        out (Transform3D StatedEntityRoot, Transform3D BoneEntityRoot, bool Unrotated) bonePlacement)
    {
        Transform3D statedEntityRoot = Engine_FxSystem.FxToEntityMeshTransform(statedFxRoot);
        if (entities.TryPlaceAttachedEffect(owner, attached, statedEntityRoot, out Transform3D placedEntityRoot))
        {
            bonePlacement = (statedEntityRoot, placedEntityRoot, unrotated);
            _boneRoots[rootKey] = bonePlacement;
            return true;
        }

        _boneRoots.Remove(rootKey);
        bonePlacement = default;
        return false;
    }

    private void ApplyNodePlacement(
        TrackedEffect tracked,
        ReadOnlySpan<Vector3> statedWorldSpline,
        ReadOnlySpan<Vector3> drawnWorldSpline,
        bool hasBonePlacement,
        (Transform3D StatedEntityRoot, Transform3D BoneEntityRoot, bool Unrotated) bonePlacement,
        Transform3D placed,
        bool refreshed)
    {
        Transform3D smoothed;
        ReadOnlySpan<Vector3> activeWorldSpline;
        if (drawnWorldSpline.Length >= 2)
        {
            tracked.Timeline = null;
            smoothed = new Transform3D(placed.Basis, drawnWorldSpline[0]);
            activeWorldSpline = drawnWorldSpline;
        }
        else if (hasBonePlacement)
        {
            tracked.Timeline = null;
            // Honour `OrientParticleToAttachmentPoint`: when the guest published an identity basis
            // (`_rootIdentity` -> `Unrotated`), keep the flame upright (`GuestIdentity`) at the dummy's
            // world position instead of tilting it by the wall bracket's bone rotation (`sub_821EC8A8`).
            smoothed = bonePlacement.Unrotated
                ? new Transform3D(GuestIdentity, bonePlacement.BoneEntityRoot.Origin)
                : Engine_FxSystem.EntityMeshToFxTransform(bonePlacement.BoneEntityRoot);
            activeWorldSpline = statedWorldSpline;
        }
        else
        {
            smoothed = Smoothed(tracked, placed, refreshed);
            activeWorldSpline = statedWorldSpline;
        }

        FableFx node = tracked.Node;
        if (!node.Placed.IsEqualApprox(smoothed))
        {
            Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.Fx);
            node.Placed = smoothed;
        }

        ApplyWorldSplineOverride(node, tracked, smoothed, activeWorldSpline);
    }

    private void ApplyWorldSplineOverride(
        FableFx node, TrackedEffect tracked, Transform3D nodePlaced, ReadOnlySpan<Vector3> worldSpline)
    {
        if (worldSpline.Length >= 2)
        {
            float det = nodePlaced.Basis.Determinant();
            if (!float.IsFinite(det) || Mathf.Abs(det) <= 1e-6f)
            {
                return;
            }

            Transform3D toNodeLocal = nodePlaced.AffineInverse();
            int count = Math.Min(worldSpline.Length, _localSplineScratch.Length);
            for (int i = 0; i < count; i++)
            {
                Vector3 local = toNodeLocal * worldSpline[i];
                if (!local.IsFinite())
                {
                    return;
                }
                _localSplineScratch[i] = local;
            }

            node.SetSplineOverride(_localSplineScratch.AsSpan(0, count));
            tracked.HasSplineOverride = true;
        }
        else if (tracked.HasSplineOverride)
        {
            node.SetSplineOverride(ReadOnlySpan<Vector3>.Empty);
            tracked.HasSplineOverride = false;
        }
    }

    /// <summary>
    /// Drops the nodes whose guest instance is gone. Keyed on the instance pointer: the guest
    /// retires by swap-and-pop, so vector indices are not stable.
    /// </summary>
    private void Retire()
    {
        _deadKeys.Clear();
        foreach ((uint key, TrackedEffect tracked) in _tracked)
        {
            if (_seen.Contains(key))
            {
                tracked.Absences = 0;
                continue;
            }

            if (!ShouldRetireOnAbsence(++tracked.Absences, HostBridge.ActiveFxSaturated))
            {
                continue;
            }

            _deadKeys.Add(key);
        }

        for (int i = 0; i < _deadKeys.Count; i++)
        {
            uint key = _deadKeys[i];
            if (_tracked.Remove(key, out TrackedEffect? tracked))
            {
                RetireTrackedNode(tracked);
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

    /// <summary>
    /// Resolves a guest particle group's or trail's world transform (in entity-mesh <c>SO(3)</c> space)
    /// directly onto its attached host bone/dummy pose when its effect is hung on a parent entity via
    /// <c>CECObjectAttachment</c>. World-space emitters (<paramref name="exactIdentity4x4"/>) are
    /// skipped because their particle positions are already baked in world space.
    /// </summary>
    public bool TryResolveBoneAttachedWorld(
        uint ownerInstance,
        Transform3D rawGroupEntityWorld,
        bool rawIdentityBasis,
        bool exactIdentity4x4,
        out Transform3D resolvedEntityWorld)
    {
        resolvedEntityWorld = rawGroupEntityWorld;
        if (exactIdentity4x4 ||
            ownerInstance == 0u ||
            !_keyByInstance.TryGetValue(ownerInstance, out uint rootKey) ||
            !_boneRoots.TryGetValue(rootKey, out (Transform3D StatedEntityRoot, Transform3D BoneEntityRoot, bool Unrotated) bone))
        {
            return false;
        }

        // Guard against world-space groups whose origin sits near (0, 0, 0) far from the character:
        // any local-space emitter group belonging to this effect sits within a few metres of its root.
        if (rawGroupEntityWorld.Origin.DistanceSquaredTo(bone.StatedEntityRoot.Origin) > 16f)
        {
            return false;
        }

        // For any local-space (`AttachToRoot`) emitter node, `sub_8322F1E0` -> `sub_8322EEC8` writes
        // `sub_832295F0(root_node)` directly into the render twin (`R + 320`, `rawGroupEntityWorld`),
        // while `sub_8322D988` already bakes the emitter child's local offset into the particle
        // vertices in root space. Because the 15 Hz particle step runs before `CECObjectAttachment`
        // updates the child entity (`bone.StatedEntityRoot`), `rawGroupEntityWorld` is simply the
        // stale root from the last 15 Hz particle step — never compose `StatedEntityRoot^-1 * rawGroupEntityWorld`
        // or it will cancel out the character's movement since the last particle step.
        resolvedEntityWorld = new Transform3D(
            rawIdentityBasis || bone.Unrotated ? Engine_FxSystem.EntityGuestIdentity : bone.BoneEntityRoot.Basis,
            bone.BoneEntityRoot.Origin);
        return true;
    }

    private static bool IsExactGuestIdentityRows(Vector3[] rows) =>
        rows.Length >= 3 &&
        rows[0] == new Vector3(1f, 0f, 0f) &&
        rows[1] == new Vector3(0f, 1f, 0f) &&
        rows[2] == new Vector3(0f, 0f, 1f);

    private Transform3D Smoothed(TrackedEffect tracked, Transform3D stated, bool refreshed)
    {
        // Hold proper right-handed SO(3) (det = +1) transforms in Engine_TickTimeline so quaternion
        // slerp never drops FableFx's axis-swap reflection (det = -1).
        Transform3D entityStated = Engine_FxSystem.FxToEntityMeshTransform(stated);
        if (tracked.Timeline is not { } timeline)
        {
            timeline = new Refii.Core.Engine_TickTimeline(stillWhenUnchanged: false);
            timeline.Reset(_tickNow, entityStated);
            tracked.Timeline = timeline;
            return stated;
        }

        if (timeline.Newest.Origin.DistanceTo(entityStated.Origin) > 4f ||
            (_tickNow > timeline.NewestTick && _tickNow - timeline.NewestTick > 8))
        {
            timeline.Reset(_tickNow, entityStated);
            return stated;
        }

        if (refreshed)
        {
            timeline.Observe(_tickNow, entityStated);
        }

        if (timeline.Settled)
        {
            return stated;
        }

        Transform3D entityDrawn = timeline.Advance(
            Refii.Entities.Engine_DrawnFrame.NowUsec,
            Refii.Entities.Engine_DrawnFrame.DrawTick);
        return Engine_FxSystem.EntityMeshToFxTransform(entityDrawn);
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
            $"{_tracked.Count} drawn, peak {_peakLive} live node(s)");
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
