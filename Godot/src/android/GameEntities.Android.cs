using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Game;

public partial class GameEntities
{
    /// <summary>Under the loading screen the builds are all that is left to wait for and the guest is
    /// held, so more of them run at once (about 4-5 s off a level change on a phone). Desktop and phone
    /// alike: builds already run three at a time, so this adds no sharing that was not there, and the
    /// statics `Engine_BnkMeshLoader._Load` reaches were audited for it (pr29 fixb item 8).</summary>
    private const int MaxInFlightLoadBuilds = 10;

    /// <summary>
    /// `android_offscreen_rig_walks` (AndroidOffscreenRigs): a rig out of every view is walked on one
    /// walk in this many; 1, the default and the desktop's only value, walks every rig every walk.
    /// </summary>
    public static int OffscreenRigWalks = 1;

    /// <summary>The last walk's rigs answered as off screen without their walk.</summary>
    public static int OffscreenDeferred;

    private int _offscreenDeferredThisWalk;

    /// <summary>Rigs deferred since their last walk: the walk that visits one takes it as changed.</summary>
    private readonly HashSet<uint> _offscreenDeferred = new();

    /// <summary>Metres around a rig's last drawn origin that must be out of every view (beyond the pose gate's own margin).</summary>
    private const float OffscreenRadiusMetres = 4f;

    /// <summary>
    /// A rig the walk can leave for a later walk this time: off screen, nobody's to watch, nothing
    /// hanging on its statement this walk. True when it was deferred, with what the walk's
    /// bookkeeping needs of it done here (owned, drawn, its stated placement kept).
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHAT IT SKIPS: the prologue, the placement, the zone re-read and the pose gate of a character
    /// that cannot be seen. Its bones are not written off screen in any case (the pose gate holds
    /// them); its node keeps the last placement it was given, and the walk that visits it again
    /// takes it as changed, so it is placed and posed from the guest's newest statement at once.
    /// </para>
    /// <para>
    /// NOT DEFERRED: a principal, the morph preview, a highlighted target, an entity fading or
    /// hidden by its look, an attached child or one just let go, a rig on its reconcile walk, one
    /// in view or within <see cref="RigHoldMs"/> of leaving it, one whose last drawn origin or stated
    /// view sphere is within <see cref="OffscreenRadiusMetres"/> (plus the gate's margin) of any
    /// view, anything under the load hold, and every rig while the sun casts shadows (an off-screen
    /// body's sun shadow can fall in view).
    /// </para>
    /// <para>
    /// LOSSY: a light or an effect that follows the body's node (a carried lantern, a mod's light)
    /// moves at the reduced rate while its holder is off screen, and a body walking into view is
    /// placed from up to N-1 walks back for the one walk before it is visited.
    /// </para>
    /// </remarks>
    private bool TryDeferOffscreen(in HostBridge.GuestEntity entity, int walks, Transform3D toWorld, ulong nowMs, bool haveEye)
    {
        uint address = entity.Address;
        if (!haveEye || LoadHold || entity.Highlighted || !entity.Appearance.IsConstructed ||
            address == _morphPreviewHero || IsPrincipal(address) ||
            Due(address, walks) || Due(address, ReconcileTicks) ||
            Refii.Lighting.Engine_LevelLighting.SunFarDepth > 0f ||
            !_skeletons.ContainsKey(address) || _justDetached.Contains(address) ||
            _attachments.ParentOf(address) != 0u ||
            !_nodes.TryGetValue(address, out MeshInstance3D? node) ||
            !_written.TryGetValue(address, out (MeshInstance3D Node, Transform3D Drawn) written) ||
            !ReferenceEquals(written.Node, node) ||
            !_rigSeenMs.TryGetValue(address, out ulong seenMs) || nowMs - seenMs <= RigHoldMs)
        {
            return false;
        }

        Transform3D rigWorld = toWorld * written.Drawn;
        if (SphereInViews(rigWorld.Origin, OffscreenRadiusMetres, EveryView))
        {
            return false;
        }

        // The guest's own sphere as last read (a cutscene's clip carries a body metres from its origin).
        if (_viewSpheres.TryGetValue(address, out (uint Frame, (Vector3 Centre, float Radius)? Sphere) stated) &&
            stated.Sphere is { } sphere)
        {
            (Vector3 Centre, float Radius) world = Refii.Entities.Engine_ViewSphere.ToWorld(
                rigWorld, Refii.Entities.Engine_EntitySkeleton.Swizzle(sphere.Centre), sphere.Radius);
            if (SphereInViews(world.Centre, world.Radius + OffscreenRadiusMetres, EveryView))
            {
                return false;
            }
        }

        // A title that states its own pose gate (Fable 3's always-in-view actors) keeps it.
        if (HostBridge.GetEntityPoseGate(address) == true)
        {
            return false;
        }

        // What the walk's own path leaves for every drawn entity: owned (Retire keeps it), its
        // record and model keys drawn (the twin reconcile), its stated placement and zone record.
        if (entity.RecordHash != 0)
        {
            Refii.Models.Engine_ClassicZones.NoteEntity(entity.RecordHash, address);
        }
        if (entity.Changed && IsUsablePlacement(entity.Position))
        {
            _guestPlacements[address] = (entity.Position, entity.Rotation);
        }
        if (!_adoptions.ContainsKey(address))
        {
            if (entity.RecordHash != 0)
            {
                _drawnRecords.Add(entity.RecordHash);
            }
            if (ModelKeyFor(entity.ModelHash) is { } modelKey)
            {
                _drawnKeys.Add((address, modelKey));
            }
            else if (entity.ModelHash == 0u && _meshParts.TryGetValue(address, out uint[]? partHashes))
            {
                for (int p = 0; p < partHashes.Length; p++)
                {
                    if (ModelKeyFor(partHashes[p]) is { } partKey)
                    {
                        _drawnKeys.Add((address, partKey));
                    }
                }
            }
        }

        _seen.Add(address);
        ++_withModel;
        _offscreenDeferred.Add(address);
        ++_offscreenDeferredThisWalk;
        return true;
    }

    /// <summary>
    /// `android_skip_index_rig_props` (AndroidSkipIndexHolds): a rigged prop whose clips have settled
    /// is held by the skip index between its settled-prop checks; false, the desktop's only value,
    /// walks its row every walk. Only with `entity_skip_index`.
    /// </summary>
    public static bool HoldRigProps;

    /// <summary>
    /// `android_skip_index_buildings` (AndroidSkipIndexHolds): a still multi-mesh appearance (a
    /// building of several mesh records) is held by the skip index while the walk's first ten reports
    /// keep it off the fast path; false, the desktop's only value, walks its full prologue every walk.
    /// </summary>
    public static bool HoldBuildings;

    /// <summary>A held rigged prop's verdict tag: its row adds it to the dynamic casters when in view.</summary>
    private const int RigPropDynamic = 1;

    /// <summary>
    /// A held rigged prop's verdict tag: its clips had not settled when it was parked (an idle loop,
    /// a pose-fed rig, or one written only on its first walk and out of view since). Its row writes
    /// bones only in view, so the verdict answers only a walk its pose gate says it is out of view.
    /// </summary>
    private const int RigPropUnsettled = 2;

    /// <summary>A held building's verdict tag: bit 0 always, bit 1 an interior node, mesh records from bit 8.</summary>
    private const int BuildingHeld = 1;

    private const int BuildingInterior = 2;

    private const int BuildingPartsShift = 8;

    /// <summary>The held rigged prop's drawn placement, read by <see cref="HoldsRigProp"/> for its replay.</summary>
    private Transform3D _heldDrawn;

    /// <summary>The pose gate <see cref="HoldsRigProp"/> already asked for the held prop (0 out of view), or -1.</summary>
    private int _heldGate = -1;

    /// <summary>This walk's views and clock, for the pose gate a held unsettled prop is asked before its hit.</summary>
    private bool _holdEye;

    private Transform3D _holdToWorld;

    private ulong _holdNowMs;

    /// <summary>
    /// Why the walk's rigged props (a skeleton, no character rig) were or were not answered by the
    /// skip index (debug, metrics.entityWalk.rigPropMiss). The first group counts each row past the
    /// index once, by what its end parked or why it parked nothing ("lever", "leverUnsettled": it would
    /// have parked, settled or not, but `android_skip_index_rig_props` is off, or host animation or
    /// off-screen deferral is on);
    /// the second each verdict found, by whether it answered the walk.
    /// </summary>
    private static readonly string[] RigPropMissNames =
    {
        "parkedSettled", "parkedSettledWrite", "parkedUnsettled",
        "changed", "notFast", "twin", "lever", "mover", "animating", "reblending",
        "unplaced", "neverAsked", "noAnimator", "wrote", "inView",
        "heldSettled", "heldUnsettled", "heldDue", "heldRefused", "heldInView", "leverUnsettled",
    };

    private const int MissParkedSettled = 0, MissParkedSettledWrite = 1, MissParkedUnsettled = 2,
        MissChanged = 3, MissNotFast = 4, MissTwin = 5, MissLever = 6, MissMover = 7, MissAnimating = 8,
        MissReblending = 9, MissUnplaced = 10, MissNeverAsked = 11, MissNoAnimator = 12, MissWrote = 13,
        MissInView = 14, HitSettled = 15, HitUnsettled = 16, HitDue = 17, HitRefused = 18, HitInView = 19,
        MissLeverUnsettled = 20;

    private readonly int[] _rigPropMiss = new int[RigPropMissNames.Length];

    /// <summary>The last walk's <see cref="RigPropMissNames"/> counts.</summary>
    public static readonly int[] LastWalkRigPropMiss = new int[RigPropMissNames.Length];

    /// <summary>The last walk's rigged-prop reasons by name, for the metrics' entityWalk.</summary>
    public static Dictionary<string, int> RigPropMissCounts()
    {
        var counts = new Dictionary<string, int>(RigPropMissNames.Length);
        for (int i = 0; i < RigPropMissNames.Length; ++i)
        {
            counts[RigPropMissNames[i]] = LastWalkRigPropMiss[i];
        }

        return counts;
    }

    /// <summary>Starts a walk's rigged-prop holds: its views and clock, and its counts from zero.</summary>
    private void BeginRigPropHolds(bool haveEye, Transform3D toWorld, ulong nowMs)
    {
        _holdEye = haveEye;
        _holdToWorld = toWorld;
        _holdNowMs = nowMs;
        _heldGate = -1;
        System.Array.Clear(_rigPropMiss);
    }

    /// <summary>The walk's rigged-prop counts, published with its row counts.</summary>
    private void PublishRigPropMiss() => System.Array.Copy(_rigPropMiss, LastWalkRigPropMiss, _rigPropMiss.Length);

    /// <summary>The cadence a skip-index verdict is re-asked on: its kind's own
    /// (Engine_EntitySkipIndex.RecheckTicks), except the kinds held here. A held building (tagged Still)
    /// re-asks on the unscaled reconcile: before its reports were counted it walked the full prologue,
    /// which reconciles every ReconcileTicks whatever the still scale. A held rigged prop re-asks on its
    /// own check.</summary>
    private static int RecheckFor(Refii.Entities.Engine_EntitySkipIndex.Kind kind, int tag) => kind switch
    {
        Refii.Entities.Engine_EntitySkipIndex.Kind.Still when tag != 0 => ReconcileTicks,
        Refii.Entities.Engine_EntitySkipIndex.Kind.RigProp => RigPropRecheckTicks(tag),
        _ => Refii.Entities.Engine_EntitySkipIndex.RecheckTicks(kind, ReconcileTicks, StillReconcileScale),
    };

    /// <summary>Whether a verdict off its re-check walk still holds: what a held rigged prop's or building's
    /// replay needs; every other kind holds.</summary>
    private bool HoldsKind(uint address, Refii.Entities.Engine_EntitySkipIndex.Kind kind, int tag) => kind switch
    {
        Refii.Entities.Engine_EntitySkipIndex.Kind.RigProp => HoldsRigProp(address, tag),
        Refii.Entities.Engine_EntitySkipIndex.Kind.Still when tag != 0 => HoldsBuilding(),
        _ => true,
    };

    /// <summary>A held rigged prop's row counts: drawn, posed, and held in view; no skip of the prologue.</summary>
    private void CountHeldRigProp(uint address, int tag, bool haveEye, Transform3D toWorld, ulong nowMs, ref int posed, ref int farHeld)
    {
        ++_withModel;
        ++posed;
        if (HeldRigPropInView(address, tag, haveEye, toWorld, nowMs))
        {
            ++farHeld;
        }
    }

    /// <summary>The re-check cadence of a held rigged prop (AndroidSkipIndexHolds.RigPropRecheckTicks).</summary>
    private static int RigPropRecheckTicks(int tag) =>
        Refii.AndroidRuntime.AndroidSkipIndexHolds.RigPropRecheckTicks((tag & RigPropUnsettled) != 0, ReconcileTicks);

    /// <summary>A verdict found is due on its re-check: false, its row decides; counted for a rigged prop.</summary>
    private bool HeldDue(Refii.Entities.Engine_EntitySkipIndex.Kind kind)
    {
        if (kind == Refii.Entities.Engine_EntitySkipIndex.Kind.RigProp)
        {
            ++_rigPropMiss[HitDue];
        }

        return false;
    }

    /// <summary>
    /// A rigged prop's verdict answers this walk: the lever on, its placement drawn, and, for one
    /// whose clips had not settled, its pose gate out of view (asked here, in its row's terms, and
    /// handed to the replay so it is asked once).
    /// </summary>
    private bool HoldsRigProp(uint address, int tag)
    {
        _heldGate = -1;
        // Not while `android_offscreen_rig_walks` defers: a deferred prop comes back as changed and reads its
        // clips at once, which a hold answered before the deferral would skip.
        if (!HoldRigProps || OffscreenRigWalks > 1 || HostAnimation || !_written.TryGetValue(address, out (MeshInstance3D Node, Transform3D Drawn) written))
        {
            ++_rigPropMiss[HitRefused];
            return false;
        }

        _heldDrawn = written.Drawn;
        if ((tag & RigPropUnsettled) != 0)
        {
            // In view its row may write bones (its rate decides): the row is walked, and asks the gate
            // again with the same inputs and the same answer.
            Transform3D rigWorld = _holdEye ? _holdToWorld * _heldDrawn : Transform3D.Identity;
            if (PosedThisTick(address, _holdEye, rigWorld, _holdNowMs))
            {
                ++_rigPropMiss[HitInView];
                return false;
            }

            _heldGate = 0;
            ++_rigPropMiss[HitUnsettled];
            return true;
        }

        ++_rigPropMiss[HitSettled];
        return true;
    }

    /// <summary>A building's verdict answers this walk: the lever on, and the reports still counting it.</summary>
    private bool HoldsBuilding() => HoldBuildings && _reports < 10;

    /// <summary>
    /// What the row of a held rigged prop does on a walk its verdict answers, in its order: the pose
    /// gate (which stamps when it was last in view), the dynamic caster it adds while in view, the
    /// facing sample of a node that has not moved. True when in view: a pose held in view.
    /// </summary>
    /// <remarks>
    /// The rest of its row writes nothing on such a walk: unchanged, not due on the settled-prop check
    /// (`Due(address, 6)`, which also covers the reconcile) and settled, it writes no bones, places
    /// nothing, re-reads no zone, and its shadow skeleton's rate was set off by the row that parked it.
    /// A door that starts to open is read on that check's walk, which its row reaches as today.
    /// UNSETTLED, held only out of view (<see cref="HoldsRigProp"/>), it writes no bones either: its
    /// row writes them only in view or on its first walk, and its clips are read only when it writes.
    /// </remarks>
    private bool HeldRigPropInView(uint address, int tag, bool haveEye, Transform3D toWorld, ulong nowMs)
    {
        ++_rigPropsHeld;
        bool measurable = haveEye;
        Transform3D rigWorld = measurable ? toWorld * _heldDrawn : Transform3D.Identity;
        bool inView = _heldGate >= 0 ? _heldGate != 0 : PosedThisTick(address, measurable, rigWorld, nowMs);
        _heldGate = -1;
        if (inView && (tag & RigPropDynamic) != 0)
        {
            _dynamicCasterPositions.Add(measurable ? rigWorld.Origin : Vector3.Zero);
            _dynamicCasterAddresses.Add(address);
        }

        if (_reports < 10 && _lastPosition.ContainsKey(address))
        {
            // SampleFacing of a node that stands where it was sampled: one more sample, no jump.
            ++_positionSamples;
        }

        return inView;
    }

    /// <summary>
    /// At the end of a rigged prop's row: the verdict its next walk's row would answer the same way,
    /// with what that row's fast path draws. <paramref name="parkable"/> is the walk's own guard
    /// (unchanged, not created or just let go, no parent, no principal, a usable placement).
    /// </summary>
    /// <remarks>
    /// <para>
    /// SETTLED: the row's settled-prop gate answers its next walks alike (not a mover, nothing
    /// animating or blending, the clips settled, its placement drawn), whether this row wrote no
    /// bones or wrote a settled pose (its settled-prop check: the clips read, nothing folded).
    /// </para>
    /// <para>
    /// UNSETTLED (an idle loop, a pose-fed rig, or a prop written only on its first walk and out
    /// of view since, whose clips were last read before they settled): its row writes bones only
    /// in view, so the verdict is parked only from a row that wrote none out of view, and answers
    /// only a walk its gate says it is out of view. A prop coming into view walks its row at once.
    /// </para>
    /// <para>
    /// A ROW OFF THE FAST PATH (its reconcile) parks what its next walk's fast path would: only
    /// when that walk takes it, and never for a prop married or batched (its own skip answers it).
    /// </para>
    /// </remarks>
    private void ParkRigProp(in HostBridge.GuestEntity entity, bool parkable, bool fastRow, bool writeBones)
    {
        // The desktop holds none and counts none; a phone with the lever off still counts why.
        if (!HoldRigProps && !Refii.Core.Engine_Platform.IsAndroid)
        {
            return;
        }

        uint address = entity.Address;
        bool hasAnimator = _clipAnimators.TryGetValue(address, out Refii.Animations.Engine_ClipAnimator? animator);
        if (hasAnimator && animator!.IsCharacterRig)
        {
            return;
        }

        int miss = !parkable ? MissChanged
            : _adoptions.ContainsKey(address) || (_batches?.Contains(address) ?? false) ? MissTwin
            : !fastRow && !NextRowIsFast(entity) ? MissNotFast
            : _movers.Contains(address) ? MissMover
            : _animating.Contains(address) ? MissAnimating
            : _reblending.Contains(address) ? MissReblending
            : !_written.ContainsKey(address) ? MissUnplaced
            : !_boneWriteAskedMs.ContainsKey(address) ? MissNeverAsked
            : !hasAnimator ? MissNoAnimator
            : animator!.IsSettled ? (writeBones ? MissParkedSettledWrite : MissParkedSettled)
            : writeBones ? MissWrote
            : _rigSeenMs.TryGetValue(address, out ulong seenMs) && _holdNowMs - seenMs <= RigHoldMs ? MissInView
            : MissParkedUnsettled;
        bool parks = miss == MissParkedSettled || miss == MissParkedSettledWrite || miss == MissParkedUnsettled;

        // The lever last, so a run with it off still says why the rest would not park.
        if (parks && (!HoldRigProps || HostAnimation || OffscreenRigWalks > 1))
        {
            miss = miss == MissParkedUnsettled ? MissLeverUnsettled : MissLever;
            parks = false;
        }

        ++_rigPropMiss[miss];
        if (!parks)
        {
            return;
        }

        int tag = (IsDynamicShadowEntity(address) ? RigPropDynamic : 0) |
            (miss == MissParkedUnsettled ? RigPropUnsettled : 0);
        _skipIndex.Park(address, entity.UniqueId, Refii.Entities.Engine_EntitySkipIndex.Kind.RigProp,
            entity.RecordHash, ModelKeyFor(entity.ModelHash), null, tag);
    }

    /// <summary>
    /// Whether an unchanged entity's next walk takes the walk's fast path (`fastRow`), as its row
    /// asks it: constructed, not highlighted, past the reports' multi-mesh hold or with a model of
    /// its own, its node, mesh and constructed look kept. The cadence and the statement are its
    /// verdict's to check.
    /// </summary>
    private bool NextRowIsFast(in HostBridge.GuestEntity entity) =>
        entity.Appearance.IsConstructed && !entity.Highlighted &&
        (_reports >= 10 || entity.ModelHash != 0u) &&
        _nodes.ContainsKey(entity.Address) &&
        _nodeMesh.TryGetValue(entity.Address, out Mesh? mesh) && mesh is not null &&
        _lastLook.TryGetValue(entity.Address, out HostBridge.EntityAppearance look) && look.IsConstructed;

    // THE STILL ROWS DUE ON THEIR RE-CHECK, BY PHASE (debug timers, "ent.due.*"). On a timed walk a
    // still row due on the still re-check is lapped through its prologue and placement: the part
    // resolve (the empty-path test, the adopted test, the index's unpark, the fast-path test, the
    // zone note, the eyes or part lists and the mesh), the adoption test, drawn sets and node, the
    // interior node, the garment hides, the look (with a node's creation, if any), the still-skip
    // test, the placement and the zone re-read, the layers and relight, and the rest to the next row
    // (the pose test and the park; the whole row of one that ended in its prologue). Their count is
    // metrics.entityWalk.rowsStillDue; their sum is that share of "ent.rows.still".
    private static readonly string[] DuePhaseNames =
    {
        "ent.due.resolve", "ent.due.drawn", "ent.due.sync", "ent.due.hidden",
        "ent.due.look", "ent.due.zone", "ent.due.relight", "ent.due.tail",
    };

    private const int DueResolve = 0, DueDrawn = 1, DueSync = 2, DueHidden = 3, DueLook = 4,
        DueZone = 5, DueRelight = 6, DueTail = 7;

    private readonly long[] _dueTicks = new long[DuePhaseNames.Length];

    private bool _dueRow;

    private long _dueAt;

    /// <summary>A timed row opens; <paramref name="due"/>, a still row due on its re-check, is lapped.</summary>
    private void OpenDueRow(bool due, long from)
    {
        _dueRow = due && Refii.Core.Engine_Platform.IsAndroid;
        _dueAt = from;
    }

    /// <summary>Ends the open due row's phase <paramref name="phase"/> here; nothing on any other row.</summary>
    private void DueLap(int phase)
    {
        if (!_dueRow)
        {
            return;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        _dueTicks[phase] += now - _dueAt;
        _dueAt = now;
    }

    /// <summary>The open due row ends: the rest of it is its tail.</summary>
    private void CloseDueRow()
    {
        DueLap(DueTail);
        _dueRow = false;
    }

    /// <summary>The timed walk's due phases, beside its "ent.rows.*", from zero again for the next.</summary>
    private void RecordDuePhases(double toMs)
    {
        if (!Refii.Core.Engine_Platform.IsAndroid)
        {
            return;
        }

        for (int phase = 0; phase < DuePhaseNames.Length; ++phase)
        {
            Refii.Core.Engine_FramePhases.Record(DuePhaseNames[phase], _dueTicks[phase] * toMs, plot: false);
        }

        System.Array.Clear(_dueTicks);
    }

    /// <summary>
    /// A still multi-mesh appearance's verdict, parked at its full prologue's skip: its record and
    /// part keys drawn, and what the prologue repeats on every walk to reach that skip.
    /// </summary>
    private void ParkHeldBuilding(in HostBridge.GuestEntity entity)
    {
        uint address = entity.Address;
        string? key = ModelKeyFor(entity.ModelHash);
        string[]? keys = null;
        if (key is null && _meshParts.TryGetValue(address, out uint[]? parts))
        {
            int count = 0;
            for (int p = 0; p < parts.Length; p++)
            {
                if (ModelKeyFor(parts[p]) is not null)
                {
                    ++count;
                }
            }

            if (count != 0)
            {
                keys = new string[count];
                count = 0;
                for (int p = 0; p < parts.Length; p++)
                {
                    if (ModelKeyFor(parts[p]) is { } partKey)
                    {
                        keys[count++] = partKey;
                    }
                }
            }
        }

        int records = _multiMeshSplit.TryGetValue(address, out (uint[] Exterior, uint[] Interior) split)
            ? split.Exterior.Length + split.Interior.Length
            : 0;
        int tag = BuildingHeld |
            (_interiorMeshesPending.ContainsKey(address) ? BuildingInterior : 0) |
            (records << BuildingPartsShift);
        _skipIndex.Park(address, entity.UniqueId, Refii.Entities.Engine_EntitySkipIndex.Kind.Still,
            entity.RecordHash, key, keys, tag);
    }

    /// <summary>
    /// What a held building's full prologue repeats on a walk its verdict answers: its zone record,
    /// its counts for the reports, and its interior node's layers and mesh (SyncInteriorMesh, which a
    /// light the region adds later can change). The rest reads only caches its own walks fill.
    /// </summary>
    private void ReplayHeldBuilding(in HostBridge.GuestEntity entity, int tag)
    {
        ++_buildingsHeld;
        if (entity.RecordHash != 0)
        {
            Refii.Models.Engine_ClassicZones.NoteEntity(entity.RecordHash, entity.Address);
        }

        ++_multiMesh;
        _multiMeshParts += tag >> BuildingPartsShift;
        if ((tag & BuildingInterior) != 0 && _nodes.TryGetValue(entity.Address, out MeshInstance3D? node))
        {
            SyncInteriorMesh(entity, node, adopted: false);
        }
    }

    /// <summary>
    /// The walk visits a rig it deferred: true when its statement reads unchanged, so the walk takes
    /// it as changed (whatever moved while it was deferred is placed now).
    /// </summary>
    private bool ResumesOffscreen(in HostBridge.GuestEntity entity) =>
        _offscreenDeferred.Remove(entity.Address) && !entity.Changed;
}
