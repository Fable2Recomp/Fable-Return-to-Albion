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

        // BEFORE the early-out, because the clocks beat whether or not this frame had anything
        // to draw, and a sampler that only runs on populated frames measures the population.
        SampleSimClock();

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
                // HOST ANIMATION AND THE POSE FEED ARE MUTUALLY EXCLUSIVE. Both write every
                // bone of the same skeleton, so running them together is not a blend — it is
                // whichever wrote last this frame, per bone, which looks like a rig tearing
                // rather than like a configuration mistake. Following the guest's bones is
                // still the default; host animation is opted into per run.
                if (HostAnimation)
                {
                    DriveLocomotion(entity.Address, node, rig);
                }
                else
                {
                    rig.Follow(entity.Address);
                }
                SampleFacing(entity.Address, node, entity.Rotation);
                StateVisibleExtent(node, rig);

                float poseYaw = rig.PoseYawDegrees();
                if (!float.IsNaN(poseYaw))
                {
                    ++_poseYawSamples;
                    _poseYawSum += Mathf.Abs(poseYaw);
                    _poseYawWorst = Mathf.Max(_poseYawWorst, Mathf.Abs(poseYaw));
                }
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

        // DO THE PARTS AGREE ON THE SKELETON THEY SHARE?
        //
        // The union keeps the first part's rest for a bone two parts both carry, which is only
        // lossless if they quote the same one. Across the shipped models they often do not —
        // `Shadow_L_Shin` is 0.26 apart between a male coat and the male body — but most of
        // that spread is child-versus-adult rigs that never appear on one character. Only the
        // guest's own part lists settle it, and they are only known here.
        //
        // A non-zero disagreement count means the losing part's geometry is displaced by up to
        // the distance shown, which reads as a garment hanging off a body rather than on it.
        int shared = Refii.Models.Engine_MdlComposite.SharedBonesAgreeing +
            Refii.Models.Engine_MdlComposite.SharedBonesDisagreeing;
        if (shared != 0)
        {
            GD.Print($"[game-entities] composite rigs: {shared} shared bone(s) across the " +
                $"parts, {Refii.Models.Engine_MdlComposite.SharedBonesDisagreeing} of them " +
                $"quoting a DIFFERENT rest" +
                (Refii.Models.Engine_MdlComposite.SharedBonesDisagreeing == 0
                    ? " — the parts agree, first-wins is lossless"
                    : $", worst '{Refii.Models.Engine_MdlComposite.WorstRestBone}' at " +
                      $"{Refii.Models.Engine_MdlComposite.WorstRestDisagreement:0.####} apart " +
                      "— that part's vertices are displaced by exactly that"));
        }

        // A part that could not be remapped was binding through ANOTHER part's numbering.
        if (Refii.Models.Engine_MdlComposite.UnremappableSurfaces != 0)
        {
            GD.Print($"[game-entities] composite rigs: " +
                $"{Refii.Models.Engine_MdlComposite.UnremappableSurfaces} skinned surface(s) " +
                $"({Refii.Models.Engine_MdlComposite.UnremappableVertices} vertices) shipped no " +
                "usable bone table, so their indices addressed the UNIONED table raw — they " +
                "were binding to whatever bone sat at that index. Collapsed to the root");
        }

        // THE FACING, CHECKED AGAINST WALKING — the one reference the conversion chain cannot
        // manufacture. See SampleFacing: the offset's VALUE names the fix.
        if (_facingSamples != 0)
        {
            // A MAJORITY, NOT MERELY THE LARGEST BUCKET. The first version called 234 of 464 a
            // pass because 0° was the biggest pile, and reported the facing RIGHT while half
            // the samples disagreed with it. A verdict that cannot fail is not a verdict.
            int onTarget = _facingBuckets[0];
            int total = onTarget + _facingBuckets[1] + _facingBuckets[2] + _facingBuckets[3];
            int worst = 1;
            for (int i = 2; i < 4; ++i)
            {
                if (_facingBuckets[i] > _facingBuckets[worst])
                {
                    worst = i;
                }
            }

            GD.Print($"[game-entities] facing vs travel: {_facingSamples} walking sample(s), " +
                $"mean {_facingSum / _facingSamples:0.#}° off; {Quadrants(_facingBuckets)}" +
                (onTarget >= (int)(0.9f * total)
                    ? " — characters walk the way they face, the facing is RIGHT"
                    : $" — NOT clean; the largest error pile is {worst * 90}°") +
                $"\n    composite (morph) characters: {Quadrants(_facingBucketsComposite)}" +
                $"\n    single-model characters:      {Quadrants(_facingBucketsNamed)}" +
                (_poseYawSamples == 0
                    ? "\n    pose yaw: no hip bone to measure"
                    : $"\n    pose yaw: the POSE turns the body {_poseYawSum / _poseYawSamples:0.#}° " +
                      $"from the model's own forward on average (worst {_poseYawWorst:0.#}°) — " +
                      (_poseYawSum / _poseYawSamples < 15f
                          ? "small, so the heading is NOT in the pose and any facing error is the entity rotation"
                          : "LARGE, so the pose carries the heading and composing the entity rotation on top double-counts it")) +
                $"\n    culling box: stated on {_extentStated} rig(s), {_extentOverflow} of " +
                "those had a live pose reaching OUTSIDE the mesh's own box" +
                (_extentOverflow == 0
                    ? " — the bind box already covered them, so culling was not the vanishing"
                    : " — those are the ones the renderer could drop while they were on screen") +
                $"\n    position coherence: {_positionJumps} of {_positionSamples} frame(s) " +
                $"moved an entity more than {TeleportThreshold} in one frame " +
                $"(worst {_worstJump:0.#})" +
                (_positionJumps == 0
                    ? " — reads are coherent, so jitter is the two clocks beating, not tearing"
                    : " — that is either a scripted teleport or a TORN READ, and tearing would " +
                      "explain both the jitter and the vanishing") +
                SimClockReport() +
                $"\n    poses refused as TORN: {Refii.Entities.Engine_EntitySkeleton.TornPoses}" +
                (Refii.Entities.Engine_EntitySkeleton.TornPoses == 0
                    ? " — every rig read came back coherent"
                    : " — those rigs held last frame's pose instead of a half-written one; " +
                      "each would otherwise have been a character exploding for one frame") +
                $"\n    rotations refused as NOT UNIT: {NonUnitRotations}" +
                (NonUnitRotations == 0
                    ? " — every entity handed us a real rotation"
                    : " — that many transforms fell back to identity because what the bridge " +
                      "returned was not a rotation"));
        }

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
    /// <summary>
    /// Whether Godot plays CLIPS for characters instead of copying the guest's bones.
    ///
    /// Opt-in, and off by default, because it replaces a working feed rather than adding to
    /// one. `REFII_HOST_ANIM=1` turns it on.
    /// </summary>
    private static readonly bool HostAnimation =
        System.Environment.GetEnvironmentVariable("REFII_HOST_ANIM") == "1";

    /// <summary>How many characters currently have a host-side locomotion animator.</summary>
    private int _locomotionDriven;

    /// <summary>
    /// Gives one character a locomotion animator over the skeleton it is ALREADY skinned to.
    ///
    /// The skeleton is passed in rather than built, because the mesh's bind matrices were
    /// derived from this exact one. A second skeleton would animate correctly and move nothing
    /// anyone can see.
    /// </summary>
    private void DriveLocomotion(
        uint address, MeshInstance3D node, Refii.Entities.Engine_EntitySkeleton rig)
    {
        var animator =
            node.GetNodeOrNull<Refii.Animations.Engine_LocomotionAnimator>(
                Refii.Animations.Engine_LocomotionAnimator.NodeName);

        if (animator is null)
        {
            animator = new Refii.Animations.Engine_LocomotionAnimator
            {
                Name = Refii.Animations.Engine_LocomotionAnimator.NodeName,
                EntityAddress = address,
            };

            node.AddChild(animator);

            uint[] hashes = node.Mesh is Refii.Entities.Engine_EntityResource resource
                ? System.Linq.Enumerable.ToArray(resource.ModelHashes)
                : System.Array.Empty<uint>();

            animator.Drive(rig.Node, rig.Node.Name, hashes);
            _locomotionDriven++;
        }
    }

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
    /// <summary>
    /// How much geometry is weighted to a bone the guest never drives — ACROSS EVERY SURFACE.
    ///
    /// With the bone bounds now matching the bind pose, the skeleton is right and whatever is
    /// left is about which vertices attach where. A bone the guest does not name keeps its bind
    /// transform for ever while its parent moves, so any vertex weighted to it is anchored in
    /// place while the rest of the body walks away — which on screen is a flat sheet of
    /// triangles fanning between the character and where that bone used to be. That is the
    /// shape actually on screen, and it is a different fault from a rig that explodes.
    ///
    /// EVERY SURFACE, not surface 0. A composite character is a body plus a head plus garments,
    /// concatenated as separate surfaces, and the body is usually first — so a garment left
    /// behind is invisible to a probe that only ever looks at surface 0. The pale sheets hang
    /// off forearms and chests, which is where garments are.
    ///
    /// Weight-bearing only: an index with a zero weight influences nothing and is common
    /// padding, so counting it would report tearing on meshes that are perfectly fine.
    /// </summary>
    private static void ReportUndrivenWeights(
        ArrayMesh array, Refii.Entities.Engine_EntitySkeleton skeleton)
    {
        var byBone = new Dictionary<int, int>();
        int affected = 0;
        int total = 0;

        for (int s = 0; s < array.GetSurfaceCount(); ++s)
        {
            Godot.Collections.Array arrays = array.SurfaceGetArrays(s);
            if (arrays.Count <= (int)Mesh.ArrayType.Weights ||
                arrays[(int)Mesh.ArrayType.Bones].VariantType == Variant.Type.Nil ||
                arrays[(int)Mesh.ArrayType.Weights].VariantType == Variant.Type.Nil)
            {
                continue;
            }

            int[] bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            float[] weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();

            for (int v = 0; v + 3 < bones.Length && v + 3 < weights.Length; v += 4)
            {
                ++total;
                bool torn = false;
                for (int k = 0; k < 4; ++k)
                {
                    if (weights[v + k] <= 0.0001f || skeleton.IsPosed(bones[v + k]))
                    {
                        continue;
                    }

                    torn = true;
                    byBone.TryGetValue(bones[v + k], out int count);
                    byBone[bones[v + k]] = count + 1;
                }

                if (torn)
                {
                    ++affected;
                }
            }
        }

        if (affected == 0)
        {
            GD.Print($"    every weighted vertex of {total} attaches to a bone the guest " +
                "drives — nothing is anchored in bind");
            return;
        }

        var worst = new List<KeyValuePair<int, int>>(byBone);
        worst.Sort((a, b) => b.Value.CompareTo(a.Value));

        var named = new List<string>();
        for (int i = 0; i < worst.Count && i < 5; ++i)
        {
            named.Add($"'{skeleton.BoneName(worst[i].Key)}'[{worst[i].Key}] x{worst[i].Value}");
        }

        GD.Print($"    ANCHORED IN BIND: {affected} of {total} vertices carry weight on a bone " +
            $"the guest never drives, across {byBone.Count} such bone(s) — " +
            string.Join(", ", named) +
            " — those vertices stay put while their parents move, which is the sheet of " +
            "triangles on screen");
    }

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

        // The bind is the MODEL's, because that is the space the vertices are authored in. This
        // line is how far the GUEST's own bind sits from it — informative, deliberately unused.
        // The child hero is the reason: its guest bind is the adult rig, 1.85 against a 1.38
        // mesh, so "same rig as the pose" is the wrong rule and this number is the proof.
        GD.Print($"    bind: from the MODEL" +
            (skeleton.BindGap > 0.0005f
                ? $"; the guest's own bind differs by up to {skeleton.BindGap:0.####} at " +
                  $"'{skeleton.BindGapBone}' — measured, not used"
                : "; the guest's bind agrees with it"));

        ReportUndrivenWeights(array, skeleton);

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
    /// TELLS THE RENDERER HOW BIG THE CHARACTER ACTUALLY IS, so it stops culling live ones.
    ///
    /// A skinned <see cref="MeshInstance3D"/> is culled against its MESH's bounding box, and
    /// that box is the BIND pose — the extent of the character standing still in the shape the
    /// artist authored. Bones driven from outside, as these are, move the skin without the mesh
    /// resource ever hearing about it, so a character whose arm leaves the bind box has geometry
    /// outside the volume the renderer is testing. When the whole box leaves the frustum the
    /// character is dropped even though it is plainly on screen: it VANISHES.
    ///
    /// Nothing in our own bookkeeping can see that happen — the entity is still live, still
    /// drawn, still rigged, still posed, and every counter says so. That is why the vanishing
    /// showed up in the run and in none of the numbers.
    ///
    /// So state the extent rather than let it be inferred. The union of the mesh's own box and
    /// the live bone cloud is a real measurement of where this character is, not a guess, and it
    /// is only ever larger than what Godot would have assumed — a custom box that was too small
    /// would cull MORE, so the margin matters.
    /// </summary>
    private void StateVisibleExtent(MeshInstance3D node, Refii.Entities.Engine_EntitySkeleton rig)
    {
        // Not every frame. The box only has to bound the pose, not track it, and re-stating it
        // per character per frame is work the renderer does not need.
        if (_ticks % ExtentRestateTicks != 0 || node.Mesh is not { } mesh)
        {
            return;
        }

        Aabb live = rig.Bounds(false);
        Aabb box = mesh.GetAabb().Merge(live).Grow(BoneSkinMargin);

        if (!box.Position.IsFinite() || !box.Size.IsFinite() || box.Size.Length() < 1e-3f)
        {
            return;  // a torn pose is one bad frame; do not bake it into the culling box
        }

        // Counted so the next run says whether this was the fault: a live cloud that fits inside
        // the mesh box means the renderer was never culling wrongly and the vanishing is
        // something else.
        if (!mesh.GetAabb().Encloses(live))
        {
            ++_extentOverflow;
        }

        ++_extentStated;
        node.CustomAabb = box;
    }

    /// <summary>
    /// How far the skin can hang off the bones. A bone is a point; the flesh around it is not,
    /// and the bone cloud alone would still cut a shoulder off at the joint.
    /// </summary>
    private const float BoneSkinMargin = 0.35f;

    /// <summary>Half a second at 60fps. The box bounds the pose; it need not track it.</summary>
    private const int ExtentRestateTicks = 30;

    private int _extentStated;

    private int _extentOverflow;

    /// <summary>
    /// DOES A CHARACTER FACE THE WAY IT WALKS?
    ///
    /// The conversion chain cannot answer whether the facing is RIGHT, only whether it is
    /// applied consistently — and it is: the rendered forward works out to
    /// <c>N·R·M⁻¹ · M(0,1,0) = N(guest forward)</c> algebraically, so the maths will always
    /// agree with itself no matter how wrong the input is. Settling it needs a reference the
    /// pipeline does not produce.
    ///
    /// Walking is that reference. People walk forwards, so over any distance the direction of
    /// travel IS the facing, and it is measured from positions alone — no rotation, no basis,
    /// no convention. If the two agree the facing is right and the fault is elsewhere; if they
    /// sit at a constant offset, that offset is the bug and its VALUE names the fix: 180° is a
    /// forward/back flip, ±90° is an axis convention, and a uniform spread is the facing not
    /// tracking at all.
    ///
    /// The model's own forward is +Y, measured from the `.mdl` rather than assumed:
    /// `Carry_SheathWeaponFront_DummyObject` sits at y=+0.1116 and `...Back...` at y=-0.1023 on
    /// the same spine bone. In mesh space that is <c>M(0,1,0) = (0, 0, -1)</c>.
    /// </summary>
    private void SampleFacing(uint address, MeshInstance3D node, Quaternion rotation)
    {
        Vector3 here = node.Transform.Origin;
        if (!_lastPosition.TryGetValue(address, out Vector3 last))
        {
            _lastPosition[address] = here;
            return;
        }

        _lastPosition[address] = here;

        // DID THIS ENTITY TELEPORT? The measurement that separates a torn read from a rate.
        //
        // We sample guest memory from Godot's thread while the guest simulates on its own,
        // unlocked, so a read can splice bytes from either side of a write. A torn float is
        // usually enormous, which puts the character out of the frustum for one frame and reads
        // as it VANISHING — the same symptom the culling box also produces, from a completely
        // different cause, which is why guessing between them was never going to work.
        //
        // A walking character covers ~0.025 per frame. Anything past this is a teleport: either
        // a genuine one (a door, a cutscene — rare, and they exist) or a torn read. If the count
        // is zero the reads are coherent and the jitter is purely the two clocks beating against
        // each other, which needs interpolation and NOT locking. If it is large, the reads
        // themselves are the fault and interpolating them would only smear the garbage.
        float jump = (here - last).Length();
        if (jump > TeleportThreshold)
        {
            ++_positionJumps;
            if (jump > _worstJump)
            {
                _worstJump = jump;
            }
        }

        ++_positionSamples;

        // ONLY A CHARACTER THAT IS ACTUALLY WALKING CAN TESTIFY, and the threshold decides
        // whether that is true. The first version used 0.004 per frame; a person walking at
        // ~1.4 m/s at 60fps covers 0.023, so that admitted samples six times smaller than a
        // stride — standing jitter, turning on the spot, physics settling — and every one of
        // those has a travel direction that means nothing. Half the first run's samples landed
        // off-axis and it was impossible to say whether that was a real defect or the noise
        // floor. A walker moves at least this far.
        Vector3 travel = here - last;
        travel.Y = 0f;
        if (travel.Length() < 0.015f)
        {
            return;
        }

        // KEPT RAW as well as flattened. Zeroing Y before printing would hide the one case the
        // numbers most need to distinguish — a basis that sends the model's forward somewhere
        // VERTICAL, whose horizontal remainder is then whatever noise is left over.
        Vector3 rawForward = node.Transform.Basis * new Vector3(0f, 0f, -1f);
        Vector3 forward = rawForward;
        forward.Y = 0f;
        if (forward.Length() < 1e-4f)
        {
            return;
        }

        float degrees = Mathf.RadToDeg(
            travel.Normalized().SignedAngleTo(forward.Normalized(), Vector3.Up));

        ++_facingSamples;
        _facingSum += degrees;

        // Bucketed as well as averaged, because a mean of zero is produced BOTH by a facing
        // that is correct and by one that is uniformly wrong in both directions. The buckets
        // tell those apart.
        int bucket = (int)Mathf.Round(Mathf.PosMod(degrees, 360f) / 90f) % 4;
        ++_facingBuckets[bucket];

        // SPLIT BY KIND, because the guest has more than one orientation source and a mixture
        // averages into nonsense. `sub_82211110` tries the Havok phantom first, then the
        // component's own cache at +0xE0, then the delegate chain — and a previous defect in
        // exactly this area had chickens turning correctly while every human faced one way. If
        // one kind is clean and the other is not, that names the source; if both look the same,
        // the fault is common to all of them and the source is not the question.
        //
        // Composite characters are the ones assembled from morph parts and carry no mesh
        // resource name, which is the cheapest discriminator available here and happens to
        // separate the humans from the animals and props almost exactly.
        bool composite = node.Mesh is not ArrayMesh named || named.ResourceName.Length == 0;
        int[] buckets = composite ? _facingBucketsComposite : _facingBucketsNamed;
        ++buckets[bucket];

        // THE RAW PAIRS, because a histogram cannot name a RELATIONSHIP.
        //
        // 21% on target with 43% at 180° and 28% at 90° is not a fixed convention error — a
        // constant offset would pile into one bucket. A spread like that is what an
        // input-DEPENDENT error looks like, and the candidates are distinguishable only by
        // seeing the two angles side by side:
        //
        //   facing ==  travel          -> right
        //   facing == -travel          -> the rotation is INVERTED (a transposed basis)
        //   facing ==  travel + k*90   -> an axis convention, and k names it
        //   facing ==  2*travel        -> the rotation is applied twice
        //   no relation at all         -> we are reading something that is not the facing
        //
        // A dozen pairs settle which. Capped, because this is a work-list and not telemetry.
        if (_facingPairs < FacingPairLimit)
        {
            ++_facingPairs;
            float travelYaw = Mathf.RadToDeg(
                Vector3.Forward.SignedAngleTo(travel.Normalized(), Vector3.Up));
            float facingYaw = Mathf.RadToDeg(
                Vector3.Forward.SignedAngleTo(forward.Normalized(), Vector3.Up));
            // THE INPUT AS WELL AS THE OUTPUT. `facing` came back as exactly 0.0° on every
            // sample, which no real rotation can produce through this basis — even identity
            // renders 180° — so either the quaternion is degenerate or my reading of the basis
            // is wrong. Printing the quaternion, its length and the forward vector settles
            // which without another round of algebra: a zero/near-zero |q| is the degenerate
            // branch, a unit |q| with a constant forward means the basis is at fault.
            float length = Mathf.Sqrt((rotation.X * rotation.X) + (rotation.Y * rotation.Y) +
                (rotation.Z * rotation.Z) + (rotation.W * rotation.W));

            GD.Print($"    [facing] 0x{address:X8} travel={travelYaw,7:0.#}°  " +
                $"facing={facingYaw,7:0.#}°  diff={degrees,7:0.#}°  " +
                $"q=({rotation.X:0.###},{rotation.Y:0.###},{rotation.Z:0.###},{rotation.W:0.###}) " +
                $"|q|={length:0.###}  " +
                $"fwd=({rawForward.X:0.##},{rawForward.Y:0.##},{rawForward.Z:0.##})  " +
                $"{(composite ? "composite" : "single")}");
        }
    }

    /// <summary>
    /// Samples the guest's own simulation clock against the frame we are about to draw.
    ///
    /// THE JITTER QUESTION, MEASURED RATHER THAN ARGUED. The guest advances on a clock we do
    /// not drive — `sub_82276D10` schedules a fixed 30 Hz step against the wall clock, with a
    /// game tick every second one — and Godot presents on vsync. Two unlocked clocks means a
    /// state is shown for a VARYING number of frames even when every individual sample is
    /// perfectly correct, and that unevenness is what a character walking in a straight line
    /// looks like when it stutters.
    ///
    /// WHAT THIS COUNTS, PRECISELY, BECAUSE IT IS NOT THE POSE RATE. `dword_83319650` is
    /// bumped only by `sub_821D4628` and `sub_823D9898`, and `sub_82275F68` under them divides
    /// its delta by 15.0 — so this is the 15 Hz GAME tick. Bone poses are driven from the
    /// other world-advance path, `sub_822AF5D8` -> `sub_82278C20` -> `sub_8220D1B8` ->
    /// `sub_82204FF0`, which runs at 30 Hz and never touches this counter. So the histogram
    /// below describes the game tick against the display, NOT animation judder, and reading it
    /// as the latter would be the same mistake as comparing a raw count to a deduplicated one.
    ///
    /// It is still worth having: it is the only direct statement of whether the guest is
    /// keeping its intended schedule at all, and a guest that has fallen off 15 Hz is a
    /// different problem from one whose clock merely disagrees with ours.
    ///
    /// Deliberately a run total: the beat is a property of the two rates and takes seconds of
    /// frames to show up, so a per-frame reset would report noise.
    /// </summary>
    private void SampleSimClock()
    {
        uint tick = HostBridge.SimTick;
        ++_renderFrames;

        if (_simClockStartMsec == 0)
        {
            _simClockStartMsec = Godot.Time.GetTicksMsec();
            _simTickLast = tick;
            return;
        }

        // INEQUALITY, not subtraction. The counter's reset behaviour across a level load is
        // not settled, so a difference is not a trustworthy count of ticks — but "it changed"
        // is trustworthy whatever the counter did.
        if (tick == _simTickLast)
        {
            ++_framesOnThisState;
            return;
        }

        // SKIPPED STATES, counted separately and defensively. A difference is only meaningful
        // while the counter is monotonic, and whether a level load resets it is unproven — so
        // an implausible step is DISCARDED rather than reported as a thousand lost frames. What
        // survives is the case that matters: the guest ticking faster than we draw, where we
        // never see some of the states it produced.
        uint delta = tick - _simTickLast;
        if (delta > 1u && delta < 1000u)
        {
            _simStatesSkipped += (int)(delta - 1u);
        }

        _simTickLast = tick;
        ++_simAdvances;

        // Bucket how long the state we just finished was on screen for. Index 0 is one frame.
        int shown = _framesOnThisState + 1;
        _framesOnThisState = 0;
        _stateRepeats[Mathf.Min(shown - 1, _stateRepeats.Length - 1)]++;
    }

    /// <summary>
    /// Renders the two clocks and the beat between them.
    ///
    /// Reports both rates because either one alone is unfalsifiable: a sim rate with no render
    /// rate beside it cannot say whether the mismatch is the guest running slow or Godot
    /// running fast, and those call for opposite responses.
    /// </summary>
    private string SimClockReport()
    {
        if (_simClockStartMsec == 0 || _simAdvances == 0)
        {
            return "\n    game tick: the guest has not advanced a tick yet";
        }

        float seconds = (Godot.Time.GetTicksMsec() - _simClockStartMsec) / 1000f;
        if (seconds < 1f)
        {
            return "\n    game tick: too little wall time to state a rate";
        }

        int settled = _stateRepeats[0] + _stateRepeats[1] + _stateRepeats[2] + _stateRepeats[3];
        int dominant = 0;
        for (int i = 1; i < _stateRepeats.Length; ++i)
        {
            if (_stateRepeats[i] > _stateRepeats[dominant])
            {
                dominant = i;
            }
        }

        bool locked = settled != 0 && _stateRepeats[dominant] >= (int)(0.95f * settled);

        return $"\n    game tick: the guest advanced {_simAdvances / seconds:0.#} time(s)/s, " +
            $"Godot drew {_renderFrames / seconds:0.#} frame(s)/s over {seconds:0.#}s" +
            $"\n    each game tick was shown for: " +
            $"1 frame x{_stateRepeats[0]}, 2 x{_stateRepeats[1]}, 3 x{_stateRepeats[2]}, " +
            $"4+ x{_stateRepeats[3]}" +
            (locked
                ? $" — {dominant + 1} frame(s) every time, so these two clocks are IN STEP"
                : " — an UNEVEN spread, which is two clocks beating; that shows as stutter " +
                  "however correct each sample is, and the answer to it is interpolation, " +
                  "never locking") +
            (_simStatesSkipped == 0
                ? ""
                : $"\n    game ticks we never sampled: {_simStatesSkipped} — the guest is " +
                  "ticking faster than we look at it");
    }

    private uint _simTickLast;

    private ulong _simClockStartMsec;

    private int _simStatesSkipped;

    private int _renderFrames;

    private int _simAdvances;

    private int _framesOnThisState;

    /// <summary>How many frames each sim state stayed on screen: 1, 2, 3, 4 or more.</summary>
    private readonly int[] _stateRepeats = new int[4];

    /// <summary>
    /// Eighty times a walking stride in one frame. Below this is movement; above it is either a
    /// scripted teleport or a read that straddled a write.
    /// </summary>
    private const float TeleportThreshold = 2.0f;

    private int _positionJumps;

    private int _positionSamples;

    private float _worstJump;

    private int _facingPairs;

    /// <summary>Enough pairs to see the relationship, few enough to read in one screen.</summary>
    private const int FacingPairLimit = 14;

    /// <summary>Quadrant counts for composite (morph-assembled) characters.</summary>
    private readonly int[] _facingBucketsComposite = new int[4];

    /// <summary>Quadrant counts for characters drawn from a single named model.</summary>
    private readonly int[] _facingBucketsNamed = new int[4];

    /// <summary>Renders one quadrant histogram, with the share that landed on target.</summary>
    private static string Quadrants(int[] buckets)
    {
        int total = buckets[0] + buckets[1] + buckets[2] + buckets[3];
        return total == 0
            ? "no moving samples"
            : $"0°={buckets[0]} 90°={buckets[1]} 180°={buckets[2]} 270°={buckets[3]} " +
              $"({100f * buckets[0] / total:0}% on target of {total})";
    }

    /// <summary>Last frame's position per rigged entity, for the travel direction.</summary>
    private readonly Dictionary<uint, Vector3> _lastPosition = new();

    private int _facingSamples;

    private float _facingSum;

    /// <summary>Counts of samples nearest 0°, 90°, 180° and 270° off the direction of travel.</summary>
    private readonly int[] _facingBuckets = new int[4];

    private int _poseYawSamples;

    private float _poseYawSum;

    private float _poseYawWorst;

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

        // A ROTATION QUATERNION IS UNIT. Anything else is not a rotation we can use, and the
        // old guard — finite and not near zero — let a great deal through: measured live,
        // character rotations arrived as `(0, 0, -8.98e34, 0)` and `(0, 0, 19169.12, -0)`.
        //
        // The second of those is the dangerous shape. The `s = 2/|q|²` form self-normalises, so
        // a wildly non-unit quaternion does not blow up — it quietly becomes a PERFECTLY VALID
        // rotation that has nothing to do with the character, and every downstream check passes.
        // Rejecting it makes the failure visible as "everyone faces one way" instead of
        // "everyone faces a plausible wrong way", and the counter below says how often.
        if (!float.IsFinite(lengthSquared) || lengthSquared < 0.98f || lengthSquared > 1.02f)
        {
            ++NonUnitRotations;
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

    /// <summary>
    /// Entity transforms whose quaternion was not unit and was refused. Run total.
    ///
    /// The number that says whether the bridge is handing us rotations at all. Non-zero means
    /// some entity's orientation source is still returning something that is not a rotation.
    /// </summary>
    public static int NonUnitRotations;
}
