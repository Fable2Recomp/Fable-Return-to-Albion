using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Draws the guest's LIVE entities — the plan's phase 1, on screen.
///
/// The world Godot builds from the banks is static: it is where the buildings are, and it
/// never changes. Everything that moves — the hero, NPCs, creatures, doors, anything a quest
/// touches — exists only inside the guest, which owns the simulation and tells us where each
/// thing is. This node is the join: read the transforms each frame, keep one node per entity,
/// and let Godot draw them.
///
/// IT IS A SIBLING OF THE REGION, not a child, so it carries the whole guest-to-Godot map
/// itself rather than inheriting the level root's mirror. See <see cref="GameWorld.ToGodot"/>
/// — the same reasoning, and the same trap: matching only the per-placement half puts
/// everything in the region's internal space, which is the level reflected through X.
///
/// Entities the bridge could not place are NOT drawn. It reports their placement vtables
/// instead, because an entity at a plausible wrong position is worse than a missing one — it
/// looks like a physics bug rather than a gap in a table.
/// </summary>
public partial class GameEntities : Node3D
{
    /// <summary>
    /// The region's build-time copy of everything the guest places, indexed by `.save` record.
    ///
    /// Set by <see cref="GameWorld"/>. Removing the whole static build instead emptied the
    /// street: only 1030 of the guest's 1437 live entities name a model, and the props
    /// container also holds foliage and level geometry the guest never states. Retiring ONE
    /// RECORD AT A TIME, and only once a live node has actually been created, removes the
    /// duplicate without betting on a count.
    /// </summary>
    public Refii.Entities.Engine_StaticTwins? Twins { get; set; }

    /// <summary>Nodes by guest entity address, so a moved entity is updated rather than rebuilt.</summary>
    private readonly Dictionary<uint, MeshInstance3D> _nodes = new();

    /// <summary>Meshes by model hash. Resolving and loading is the expensive half.</summary>
    private readonly Dictionary<uint, Mesh?> _meshes = new();

    /// <summary>Rigs by guest entity address, for the entities that turned out to be characters.</summary>
    private readonly Dictionary<uint, Refii.Entities.Engine_EntitySkeleton> _skeletons = new();

    private readonly HashSet<uint> _seen = new();

    private int _ticks;

    private int _reports;

    public override void _Process(double delta)
    {
        if (HostBridge.TryGetAbiVersion() is null || !Visible)
        {
            return;
        }

        List<HostBridge.GuestEntity> entities =
            HostBridge.GetEntities(out uint unknown, out HostBridge.UnknownPlacement[] unknownVtables);
        if (entities.Count == 0)
        {
            return;
        }

        _seen.Clear();
        _drawnRecords.Clear();

        // PER FRAME, because that is what the report claims to describe. A counter that
        // accumulates for the life of the run answers a different question than its name asks,
        // and printed beside a live entity count it reads as a per-frame ratio that nothing
        // divides into.
        _withModel = 0;
        _noMesh = 0;

        // Reset for the same reason, and beside the two above so the whole report is on one
        // clock — a mix of run totals and per-frame counts in one line multiplies the totals
        // by however many frames have passed.
        _composites = 0;
        _builtParts = 0;
        _compositePartsUnresolved = 0;
        Refii.Entities.Engine_EntitySkeleton.BeginFrame();

        foreach (HostBridge.GuestEntity entity in entities)
        {
            Mesh? mesh;
            if (entity.ModelHash == 0)
            {
                // A COMPOSITE CHARACTER, or nothing at all.
                //
                // Most entities with no model hash are triggers, markers and sound emitters and
                // were never going to draw. But 29 of BWSSlums's 138 characters are people: a
                // live 160-bone skeleton and `comp6+0x20 == 0`, which the guest's own mesh block
                // branches straight past. Their geometry is layered on component 17 instead —
                // a head, a torso, legs, eyes and accessories — so it is assembled here rather
                // than resolved from one hash.
                if (ResolveComposite(entity.Address) is not { } composite)
                {
                    continue;
                }

                mesh = composite;
                ++_withModel;
                ++_composites;
            }
            else
            {
                ++_withModel;
                mesh = Resolve(entity.ModelHash);
            }

            if (mesh is null)
            {
                // THE COUNT THAT MATTERS. A model hash that resolves to a PATH is not a model
                // that loads: creature meshes are header-only stubs in the resident banks and
                // page in from streaming ones. Reading path resolution as "the guest can draw
                // this" is what emptied the street of everything but chickens.
                ++_noMesh;
                continue;
            }

            _seen.Add(entity.Address);
            if (entity.RecordHash != 0)
            {
                _drawnRecords.Add(entity.RecordHash);
            }

            if (!_nodes.TryGetValue(entity.Address, out MeshInstance3D? node))
            {
                node = new MeshInstance3D
                {
                    Name = $"Entity_{entity.Address:X8}",
                    Mesh = mesh,
                };
                node.SetMeta("entity_address", entity.Address);
                node.SetMeta("entity_unique_id", entity.UniqueId);
                node.SetMeta("entity_record", entity.RecordHash);

                // HERE, on the frame the live copy is created, and not merely when it is
                // reported — hiding on the report would take the region's copy away for an
                // entity whose mesh never loaded, turning a duplicate into a hole. The periodic
                // reconcile below enforces the same invariant for everything else.
                Twins?.Claim(entity.RecordHash);
                AddChild(node);
                _nodes[entity.Address] = node;

                // Light it. These entities are created at runtime and hang BESIDE the region
                // rather than inside it, so neither the level scene's baked pass nor the prop
                // streamer ever touches them — without this they run on the shader's bare
                // defaults, which is no bake and no ambient, and every character in the world
                // renders as a solid black silhouette in a correctly lit street.
                //
                // They carry no probe metadata: the guest places them, so there was no build
                // step to sample the field at. The theme's flat ambient is what they get.
                LightEntity(node);

                if (!TryRig(entity.Address, node, mesh))
                {
                    _unrigged.Add(entity.Address);
                }
            }
            else if (node.Mesh != mesh)
            {
                // An entity can change model — a door opening, a prop breaking.
                node.Mesh = mesh;
                LightEntity(node);
            }

            node.Transform = ToGodot(entity.Position, entity.Rotation);

            // The rig, after the placement: the pose is relative to the entity, so the
            // skeleton rides whatever transform the placement just set.
            if (_skeletons.TryGetValue(entity.Address, out
                    Refii.Entities.Engine_EntitySkeleton? rig))
            {
                rig.Follow(entity.Address);
            }
        }

        Retire();
        RetryRigs();

        // SETTLED, not merely periodic, and this is the second time the sampling point has
        // been the thing at fault rather than the code under test.
        //
        // The region mounts long before the guest has finished populating it: props stream in
        // over many seconds, entities keep arriving, and rigs are built as their meshes land.
        // A report on an early tick describes a half-built world — the first version fired
        // once, on the frame the node was created, and said "drawing 0 of 1 live entities".
        // Making it periodic only moved the problem, because the first ten reports still all
        // landed inside the first twenty seconds.
        //
        // So nothing is reported until the live entity count has held steady for a while.
        // That is the guest telling us it has finished, rather than us guessing a delay.
        ++_ticks;
        if (entities.Count != _lastCount)
        {
            _lastCount = entities.Count;
            _settledFor = 0;
        }
        else
        {
            ++_settledFor;
        }

        _everSettled |= _settledFor >= SettleTicks;

        // THE INVARIANT, RE-APPLIED FOR THE WHOLE RUN.
        //
        // `Claim` above handles the common case the instant it happens, but it only ever fires
        // for an entity the guest reports. Two things it cannot see: a record the guest never
        // instantiated (a layer whose `load` is `InitiallyOff`, a quest that has not run), and
        // a record the guest LATER gives up or takes over. `Layers.ActivateLayer` spawns a
        // gated layer mid-game and `DeactivateLayer` destroys it again, so the right answer
        // moves while you play — twice a second is far finer than a door can open.
        //
        // Latched on having settled ONCE, not on being settled now. A region populates over
        // many seconds and reconciling into a half-built world would read it as "the guest has
        // nothing"; but once it has filled, entity churn is normal and must not stop this.
        // The `_settledFor` clause is not redundant: the report below fires on exactly that tick,
        // and its counts come from here. Without it the first report can land on a tick the
        // reconcile skipped and read "0 hidden" over a region that is fully reconciled.
        if (Twins is not null && _everSettled &&
            (_ticks % ReconcileTicks == 0 || _settledFor == SettleTicks))
        {
            _liveRecords.Clear();
            foreach (HostBridge.GuestEntity entity in entities)
            {
                if (entity.RecordHash != 0)
                {
                    _liveRecords.Add(entity.RecordHash);
                }
            }

            Twins.Reconcile(_liveRecords, _drawnRecords);
        }

        bool settled = _settledFor == SettleTicks ||
            (_settledFor > SettleTicks && _ticks % 600 == 0);
        if (!settled)
        {
            return;
        }

        if (_reports >= 10)
        {
            return;
        }

        ++_reports;
        string vtables = unknownVtables.Length == 0
            ? string.Empty
            : "  unplaceable vtables: " +
              string.Join(", ", System.Array.ConvertAll(
                  unknownVtables, v => $"0x{v.Vtable:X8}x{v.Entities}"));

        GD.Print($"[game-entities] settled after {_ticks} frames: drawing {_nodes.Count} of " +
            $"{entities.Count} live entities ({unknown} unplaceable), " +
            $"{_skeletons.Count} of them rigged{vtables}");

        // Path resolution and mesh loading are DIFFERENT NUMBERS: a hash that resolves to a
        // path still has to find a mesh, so "resolved 1030/1030" is not "1030 drawable".
        GD.Print($"[game-entities] of {_withModel} with a model hash, {_noMesh} loaded NO MESH " +
            "(header-only stub or not in a mounted bank)");

        // THE COMPOSITE CHARACTERS — the ones with no model hash of their own.
        //
        // `built` is the cross-check: the guest fills its 24-byte part records in the DRAW
        // virtual, and this guest never draws, so it should be zero and the source list at
        // comp17+0x28 should be the only one populated. Non-zero means the draw path runs here.
        //
        // THIS FRAME for the entity counts, TOTALS for the cache, because they answer different
        // questions. `_composites` is how many composite characters were drawn on this tick;
        // `_compositePartsDrawn` is how much geometry the shared cache has assembled over the
        // run, which is per distinct body and not per character.
        GD.Print($"[game-entities] composite characters: {_composites} drawn this frame from " +
            $"{_composed.Count} distinct body(s); the cache assembled {_compositePartsDrawn} " +
            $"part(s) ({_compositePartsMissing} would not load, {_compositePartsUnresolved} " +
            $"hash(es) not in the index this frame); guest built-part records this frame = " +
            $"{_builtParts} — non-zero means the guest's DRAW path runs here and comp17+0x38 " +
            "is populated, which was NOT the assumption");

        // THE GHOST COUNT, and the proof that the two sides are keyed on the same thing.
        //
        // `claimed` and `unmatched` together validate the join. Both halves are built from this
        // level's `.save` map, so a live entity's record should almost always name something
        // the region also drew; `claimed` near zero with `unmatched` near the entity count
        // would mean the guest's `entity+0x1C` is NOT the GDB record hash and the match is
        // void. That failure removes nothing — an unmatched record retires nothing — so the
        // symptom would be the ghosts coming back, never a hole in the street.
        if (Twins is { } twins)
        {
            // THE INVARIANT, AS THREE NUMBERS THAT MUST ADD UP. A record is hidden because we
            // draw it, hidden because the guest does not have it, or visible because the guest
            // has it and we cannot draw it. Anything else is a bug in the reconcile.
            int shown = twins.Records - twins.HiddenForLive - twins.HiddenUnstated;
            GD.Print($"[game-entities] static twins: {twins.Records} record(s) over " +
                $"{twins.Instances} drawable(s) — {twins.HiddenForLive} hidden because the " +
                $"guest draws its own, {twins.HiddenUnstated} hidden because the guest states " +
                $"no such entity (a layer or quest gate), {shown} left standing as the only copy");

            GD.Print($"[game-entities] {twins.Unmatched} live record(s) the region drew nothing " +
                $"for, {twins.NoRecord} live draw(s) with no record hash, {twins.Restored} " +
                "record(s) restored after the guest gave one up");
        }

        // WHERE THE MISSING RIGS WENT. The guest reports 138 characters and we build 109
        // skeletons, and until this line the 29 had nowhere to be counted — they could equally
        // have been characters whose mesh never loaded (so no node, so no rig was ever
        // attempted) or characters with a node whose rest pose the bridge would not give up.
        // Those need opposite fixes, so the report separates them: an entity that is still in
        // the retry set AND has a live pose is a character we hold and cannot rig.
        int stubbornCharacters = 0;
        foreach (uint address in _unrigged)
        {
            if (HostBridge.GetEntityPose(address) is not null)
            {
                ++stubbornCharacters;
            }
        }

        GD.Print($"[game-entities] {_unrigged.Count} drawn entities still have no skeleton, " +
            $"{stubbornCharacters} of which the guest IS posing — those are characters with a " +
            "node whose rest pose the bridge will not return, not characters we never saw");

        // HERE, not at creation. Reporting a rig on the frame it was built compares the bind
        // pose against itself — Follow has not run yet, so the "live" bounds are just the seed
        // and always agree. On a settled tick they are the guest's.
        foreach (KeyValuePair<uint, Refii.Entities.Engine_EntitySkeleton> rigged in _skeletons)
        {
            if (_rigReports >= RigReportLimit)
            {
                break;
            }

            if (_nodes.TryGetValue(rigged.Key, out MeshInstance3D? owner) &&
                owner.Mesh is { } rigMesh)
            {
                ReportRig(rigMesh, rigged.Value);
            }
        }
    }

    /// <summary>
    /// Gives one entity a skeleton, if the guest has a rig for it. False means "not yet or not
    /// ever" — the two are indistinguishable from here, which is why <see cref="RetryRigs"/>
    /// exists.
    /// </summary>
    private bool TryRig(uint address, MeshInstance3D node, Mesh mesh)
    {
        // THE MODEL'S OWN BONE TABLE IS THE SKELETON. The mesh's bone indices address it, so
        // building in any other numbering binds every vertex to the wrong bone; the guest is
        // asked only for the names, which is the one key that relates its rig to this one.
        if (Refii.Entities.Engine_EntitySkeleton.TryBuild(
                address, Refii.Models.Engine_MdlRig.TryFrom(mesh)) is not { } skeleton)
        {
            return false;
        }

        node.AddChild(skeleton.Node);

        // The mesh has to be a CHILD of the skeleton and name it, and the skin comes from the
        // rest transforms — Godot derives the bind matrices by inverting them, which is exactly
        // why the rest is fed in local and left for Godot to compose.
        node.Skeleton = node.GetPathTo(skeleton.Node);
        node.Skin = skeleton.Node.CreateSkinFromRestTransforms();
        _skeletons[address] = skeleton;

        if (_alignReports < RigReportLimit)
        {
            ++_alignReports;

            // THE NUMBER THAT SAYS WHETHER A CHARACTER CAN BE POSED AT ALL is how many of the
            // model's bones found a guest bone of the same name. A low count is not a partial
            // success — it means the two rigs are being related by something other than the
            // names, and the bones that did not map stay frozen in bind while the rest move.
            GD.Print($"[rig] '{(mesh as ArrayMesh)?.ResourceName}' " +
                $"model={skeleton.BoneCount} guest={skeleton.GuestBoneCount} " +
                $"mapped={skeleton.Mapped} " +
                $"({skeleton.BoneCount - skeleton.Mapped} model bones unposed, " +
                $"{skeleton.GuestOnly} guest bones unused)" +
                (skeleton.Mapped == 0 ? " — NO NAMES MATCHED, the rig cannot be driven" : ""));
        }

        return true;
    }

    /// <summary>
    /// Gives the entities that had no rig at creation another chance.
    ///
    /// A RIG ATTEMPTED ONCE CAN NEVER RECOVER, and the original code attempted it exactly once
    /// on the frame the node was made. The bridge returns nothing for a character whose pose
    /// vector is "sized but never filled" — `pose_end &lt;= pose` — which is the ordinary state
    /// of an entity that exists before the animation system has reached it. Every character
    /// that lost that race stayed a static mesh for the rest of the run, and the symptom is a
    /// street of correctly placed people standing in bind pose, which reads as an animation
    /// problem rather than as a rig that was never built.
    ///
    /// Polled rather than event-driven because the guest offers no event: the pose array simply
    /// starts being non-empty. A thirtieth of a second's granularity is invisible next to how
    /// long a region takes to populate, and the probe costs one bit test at `entity+0x24` for
    /// the props, which are most of the set and answer no forever.
    /// </summary>
    private void RetryRigs()
    {
        if (_unrigged.Count == 0 || _ticks % RigRetryTicks != 0)
        {
            return;
        }

        _rigged.Clear();
        foreach (uint address in _unrigged)
        {
            if (!_nodes.TryGetValue(address, out MeshInstance3D? node) ||
                !GodotObject.IsInstanceValid(node) || node.Mesh is not { } mesh)
            {
                _rigged.Add(address);  // gone, not rigged — either way stop asking
                continue;
            }

            if (TryRig(address, node, mesh))
            {
                _rigged.Add(address);
            }
        }

        foreach (uint address in _rigged)
        {
            _unrigged.Remove(address);
        }
    }

    /// <summary>Entities with a node but no skeleton, still being asked.</summary>
    private readonly HashSet<uint> _unrigged = new();

    /// <summary>Scratch for <see cref="RetryRigs"/> — a set cannot be edited while walked.</summary>
    private readonly HashSet<uint> _rigged = new();

    /// <summary>Half a second at 60fps between rig retries.</summary>
    private const int RigRetryTicks = 30;

    /// <summary>
    /// How long the live entity count must hold steady before anything is reported.
    ///
    /// Two seconds at 60fps. The world streams in for far longer than that overall, but the
    /// entity list is what these reports describe, and it stops changing well before the last
    /// texture lands.
    /// </summary>
    private const int SettleTicks = 120;

    private int _lastCount = -1;

    private int _settledFor;

    /// <summary>The entity list has filled at least once. Never cleared — see the reconcile.</summary>
    private bool _everSettled;

    /// <summary>Half a second at 60fps between reconciles.</summary>
    private const int ReconcileTicks = 30;

    /// <summary>Scratch for the reconcile, reused so a twice-a-second pass allocates nothing.</summary>
    private readonly HashSet<uint> _liveRecords = new();

    /// <summary>Records we have a live node for THIS frame, filled as the entities are drawn.</summary>
    private readonly HashSet<uint> _drawnRecords = new();

    /// <summary>
    /// Checks that the MESH's bone indices and the GUEST's skeleton are the same numbering.
    ///
    /// They may well not be, and that is the leading explanation for characters rendering as
    /// a collapsed fan of triangles. The guest does not hand vertex bone indices straight to
    /// the skeleton: `sub_8222E2B8` builds a per-LOD bone PALETTE and remaps through it
    /// (`v23 = *(v19 + *(v11 + 20))`, a per-LOD table) before concatenating matrices. If the
    /// mesh indices address that palette rather than the skeleton, then binding them to a
    /// Skeleton3D built in skeleton order attaches every vertex to the wrong bone — which
    /// looks exactly like broken weights or a broken rig, and is neither.
    ///
    /// Reported rather than worked around, because the fix depends on which it is: equal
    /// counts and an in-range maximum means the numbering agrees and the fault is elsewhere;
    /// a maximum at or above the skeleton's bone count proves a remap is missing.
    /// </summary>
    private void ReportRig(Mesh mesh, Refii.Entities.Engine_EntitySkeleton skeleton)
    {
        if (_rigReports >= RigReportLimit || mesh is not ArrayMesh array || array.GetSurfaceCount() == 0)
        {
            return;
        }

        Godot.Collections.Array surface = array.SurfaceGetArrays(0);
        if (surface.Count <= (int)Mesh.ArrayType.Bones ||
            surface[(int)Mesh.ArrayType.Bones].VariantType == Variant.Type.Nil)
        {
            ++_rigReports;
            GD.Print($"[rig] '{array.ResourceName}' has NO bone array — it is rigged in the " +
                "guest but our mesh reader produced no weights, so it can only draw in bind pose");
            return;
        }

        // ONLY REPORT THINGS THAT ARE ACTUALLY SKINNED, and spend the report budget on them.
        //
        // The first run of this burned all three reports on a lamp post and a cemetery gate —
        // props that carry a skeleton but weight every vertex to bone 0, so they exercise none
        // of the skinning and answer none of the question. The characters, which are the whole
        // reason this exists, never got a line. A cap that fires before the interesting case is
        // not a diagnostic; it is a way of not looking.
        int[] probe = surface[(int)Mesh.ArrayType.Bones].AsInt32Array();
        int probeHighest = 0;
        foreach (int id in probe)
        {
            if (id > probeHighest)
            {
                probeHighest = id;
            }
        }

        if (probeHighest == 0)
        {
            return;  // single-bone prop, not a rig
        }

        ++_rigReports;

        int[] bones = surface[(int)Mesh.ArrayType.Bones].AsInt32Array();
        float[] weights = surface[(int)Mesh.ArrayType.Weights].AsFloat32Array();

        int highest = -1;
        foreach (int bone in bones)
        {
            if (bone > highest)
            {
                highest = bone;
            }
        }

        // Weight sums, because Godot expects them normalised and a mesh whose weights sum to
        // something else collapses toward the origin in its own right.
        int unnormalised = 0;
        for (int v = 0; v + 3 < weights.Length; v += 4)
        {
            float sum = weights[v] + weights[v + 1] + weights[v + 2] + weights[v + 3];
            if (Mathf.Abs(sum - 1f) > 0.01f)
            {
                ++unnormalised;
            }
        }

        // Now that the skeleton IS the model's table this should never be out of range, and
        // that is exactly why it is still checked: it is the assertion that the numbering the
        // skin binds through and the numbering the vertices index are the same one.
        GD.Print($"[rig] '{array.ResourceName}': mesh highest bone index={highest}, " +
            $"skeleton bones={skeleton.BoneCount} (guest rig {skeleton.GuestBoneCount}, " +
            $"{skeleton.Mapped} mapped by name), " +
            $"{unnormalised} of {weights.Length / 4} vertices have weights that do not sum to 1" +
            (highest >= skeleton.BoneCount
                ? " — OUT OF RANGE, the skeleton is not the model's table"
                : " — in range"));

        // The decisive comparison, and the one that needs no camera. If the bind bounds do not
        // resemble the mesh the rig is wrong before the guest's pose is even applied; if they
        // do and the live bounds do not, the pose feed is what breaks it. Screenshots could not
        // separate those — four runs in a row put no character in frame at all.
        Aabb mesh3d = array.GetAabb();
        Aabb bind = skeleton.Bounds(rest: true);
        Aabb live = skeleton.Bounds(rest: false);

        GD.Print($"    mesh aabb pos={mesh3d.Position} size={mesh3d.Size}");
        GD.Print($"    bind bones pos={bind.Position} size={bind.Size}");
        GD.Print($"    live bones pos={live.Position} size={live.Size}");

        // WHICH bone, when the live bounds do not resemble the mesh. A rig whose live extent is
        // several times its bind extent has one chain flying off, and the name plus the parent's
        // position says whether the composition is at fault or that single bone's transform is.
        if (live.Size.Length() > bind.Size.Length() * 2f)
        {
            GD.Print($"    EXPLODED — live extent is {live.Size.Length() / bind.Size.Length():0.#}x " +
                $"the bind extent{skeleton.DescribeOutlier()}");
        }
    }

    private int _rigReports;

    /// <summary>
    /// Rig reports per run.
    ///
    /// Eight rather than three because a region holds more interesting cases than that — the
    /// chickens, the child hero and the composite characters are each worth a line, and a cap
    /// that fires before the case being chased reports nothing useful.
    /// </summary>
    private const int RigReportLimit = 8;

    /// <summary>Bone-alignment reports emitted, capped so a busy region does not flood.</summary>
    private int _alignReports;

    /// <summary>Drops nodes for entities the guest no longer reports.</summary>
    private void Retire()
    {
        if (_nodes.Count == _seen.Count)
        {
            return;
        }

        var gone = new List<uint>();
        foreach (KeyValuePair<uint, MeshInstance3D> pair in _nodes)
        {
            if (!_seen.Contains(pair.Key))
            {
                gone.Add(pair.Key);
            }
        }

        foreach (uint address in gone)
        {
            if (_nodes.TryGetValue(address, out MeshInstance3D? node) &&
                GodotObject.IsInstanceValid(node))
            {
                node.QueueFree();
            }

            _nodes.Remove(address);

            // The rig goes with the node it hung under; leaving the entry behind would keep
            // a freed skeleton reachable and pose it every frame.
            _skeletons.Remove(address);

            // And the retry, which would otherwise keep asking the guest about an entity it
            // no longer has — an address the guest has reused answers for something else.
            _unrigged.Remove(address);
        }
    }

    private int _withModel;

    private int _noMesh;

    /// <summary>
    /// Applies the region's current lighting to one live entity.
    ///
    /// Re-applied whenever the mesh changes, and re-applied for every entity when the region's
    /// time of day moves — the environment node's own refresh walks the region and these are
    /// not in it.
    /// </summary>
    private static void LightEntity(GeometryInstance3D node) =>
        Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
            node, Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting);

    /// <summary>
    /// The mesh for a model hash, or null when it does not resolve or will not decode.
    ///
    /// Cached including the failures — a hash that resolves to nothing resolves to nothing
    /// every frame, and 1049 entities re-attempting a bank lookup per frame is not free.
    /// </summary>
    /// <summary>
    /// The assembled mesh for a composite character, or null when the entity is not one.
    ///
    /// Cached by the PART LIST, not by the entity, because a crowd is built from a small pool
    /// of heads and torsos — two villagers wearing the same three models share one mesh, and
    /// re-assembling per entity would rebuild the same surfaces dozens of times.
    ///
    /// The guest's SOURCE list is what the bridge returns. The component's other, built list is
    /// populated in its draw virtual, and this guest never draws; the count comes back only so
    /// the report can say whether that assumption held.
    /// </summary>
    private Mesh? ResolveComposite(uint address)
    {
        uint[] parts = HostBridge.GetEntityMorphParts(address, out int built);
        if (parts.Length == 0)
        {
            return null;
        }

        _builtParts += built;

        var paths = new List<string>(parts.Length);
        foreach (uint part in parts)
        {
            if (Refii.Level.Engine_PropModelIndex.Resolve(
                    part, System.Array.Empty<string>()) is string path)
            {
                paths.Add(path);
            }
            else
            {
                ++_compositePartsUnresolved;
            }
        }

        if (paths.Count == 0)
        {
            return null;
        }

        string key = string.Join('|', paths);
        if (_composed.TryGetValue(key, out Mesh? cached))
        {
            return cached;
        }

        ArrayMesh? assembled = Refii.Models.Engine_MdlComposite.Build(
            paths,
            path => ResourceLoader.Load<Mesh>(
                Refii.Models.Engine_BnkMeshLoader.PathPrefix + path, "Mesh"),
            out int resolved,
            out int missing);

        _compositePartsDrawn += resolved;
        _compositePartsMissing += missing;
        _composed[key] = assembled;
        return assembled;
    }

    /// <summary>Assembled composite meshes, keyed by their part list — a crowd shares parts.</summary>
    private readonly Dictionary<string, Mesh?> _composed = new();

    private int _composites;

    private int _builtParts;

    private int _compositePartsDrawn;

    private int _compositePartsMissing;

    private int _compositePartsUnresolved;

    private Mesh? Resolve(uint modelHash)
    {
        if (_meshes.TryGetValue(modelHash, out Mesh? cached))
        {
            return cached;
        }

        Mesh? mesh = null;
        if (Refii.Level.Engine_PropModelIndex.Resolve(
                modelHash, System.Array.Empty<string>()) is string path)
        {
            mesh = ResourceLoader.Load<Mesh>(
                Refii.Models.Engine_BnkMeshLoader.PathPrefix + path, "Mesh");
        }

        _meshes[modelHash] = mesh;
        return mesh;
    }

    /// <summary>
    /// A guest entity's transform, in Godot world space.
    ///
    /// Three conversions have to agree here and it is worth naming all three, because using
    /// any one alone is wrong:
    ///
    /// 1. N, the guest-to-Godot map this node lives in: <c>(x, y, z) -> (-x, z, y)</c>. That
    ///    is the level root's mirror composed with the per-placement swizzle, and it is what
    ///    <see cref="GameWorld"/>'s camera uses for the same reason — both are siblings of the
    ///    region rather than children of it.
    /// 2. R, the guest's rotation, taken from the quaternion with the guest's own formula
    ///    (sub_8220C8F0, <c>s = 2/|q|^2</c>) in ROW-vector form, so its rows are the local axes.
    /// 3. M, the map already baked into the mesh by Engine_MdlGeometry.ConvertZUpToYUp:
    ///    <c>(x, y, z) -> (x, z, -y)</c>.
    ///
    /// The basis is <c>N * R * M^-1</c>, and M^-1 sends e0, e1, e2 to localX, localZ and
    /// -localY — which is why the columns below are not localX/Y/Z in order.
    /// </summary>
    private static Transform3D ToGodot(Vector3 position, Quaternion rotation)
    {
        float lengthSquared = (rotation.X * rotation.X) + (rotation.Y * rotation.Y) +
            (rotation.Z * rotation.Z) + (rotation.W * rotation.W);

        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-8f)
        {
            return new Transform3D(Basis.Identity, ToGodot(position));
        }

        float s = 2f / lengthSquared;
        float x = rotation.X, y = rotation.Y, z = rotation.Z, w = rotation.W;

        var localX = new Vector3(
            1f - (s * ((y * y) + (z * z))),
            s * ((x * y) + (z * w)),
            s * ((x * z) - (y * w)));
        var localY = new Vector3(
            s * ((x * y) - (z * w)),
            1f - (s * ((x * x) + (z * z))),
            s * ((y * z) + (x * w)));
        var localZ = new Vector3(
            s * ((x * z) + (y * w)),
            s * ((y * z) - (x * w)),
            1f - (s * ((x * x) + (y * y))));

        return new Transform3D(
            new Basis(ToGodot(localX), ToGodot(localZ), -ToGodot(localY)),
            ToGodot(position));
    }

    /// <summary>Guest world space to Godot world space, outside the region root.</summary>
    private static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);
}
