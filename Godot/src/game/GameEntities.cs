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

    /// <summary>
    /// The entities that hang off another entity's bone. Placed in a second pass, after every
    /// parent has been given its placement and its pose this frame — a child drawn from last
    /// frame's parent is the lag this exists to remove.
    /// </summary>
    private readonly Refii.Entities.Engine_EntityAttachments _attachments = new();

    /// <summary>Children waiting for their parent: address, node, and the guest's own placement.</summary>
    private readonly List<(uint Child, MeshInstance3D Node, Transform3D Stated)> _attached = new();


    /// <summary>Nodes by guest entity address, so a moved entity is updated rather than rebuilt.</summary>
    /// <summary>The live feed node, for the debug channel's levers.</summary>
    public static GameEntities? Current;

    /// <summary>
    /// Whether the feed re-walks the guest every drawn frame. Off: it walks once per guest
    /// frame (<c>Engine_GuestFrame.Changed</c>, 30 Hz) and redraws the pairs in between.
    /// </summary>
    public static bool FeedEveryFrame = false;

    public static int FeedFramesSkipped;
    private uint _feedTickApplied = uint.MaxValue;

    private readonly Dictionary<uint, MeshInstance3D> _nodes = new();

    /// <summary>
    /// The node built for one live entity, by guest address, or null. For a consumer that draws
    /// an entity somewhere OTHER than where the feed placed it — the co-op screen's mannequin
    /// stands in an empty front-end world and belongs in a panel. Read its mesh; do not re-parent
    /// the node, the feed still drives it.
    /// </summary>
    public MeshInstance3D? NodeFor(uint address) =>
        _nodes.TryGetValue(address, out MeshInstance3D? node) && GodotObject.IsInstanceValid(node) ? node : null;

    /// <summary>
    /// How far an entity is DRAWN from where the guest last stated it — the playhead's blend,
    /// as a world offset. Anything the guest anchors on an entity at its tick (a heart over a
    /// walking villager) adds this to ride the body that is on screen.
    /// </summary>
    public Vector3 DrawnLead(uint address) =>
        NodeFor(address) is { } node
            ? node.Transform.Origin - StatedOf(address, node.Transform).Origin
            : Vector3.Zero;

    /// <summary>Meshes by model hash. Resolving and loading is the expensive half.</summary>
    private readonly Dictionary<uint, Mesh?> _meshes = new();

    /// <summary>Rigs by guest entity address, for the entities that turned out to be characters.</summary>
    private readonly Dictionary<uint, Refii.Entities.Engine_EntitySkeleton> _skeletons = new();

    private readonly HashSet<uint> _seen = new();

    private int _ticks;

    private int _reports;

    /// <summary>The set of unplaceable placement classes as last reported.</summary>
    private string _unknownSignature = string.Empty;

    /// <summary>
    /// Meshes are built OFF THIS THREAD, and land in the caches above a frame or two later.
    /// </summary>
    /// <remarks>
    /// THE ENTITY LOOP USED TO BUILD SYNCHRONOUSLY, and a cache miss is the whole asset chain:
    /// mesh decode, material create, then an ORM whose height channel is a 24-pass least-squares
    /// solve over the entire texture (<see cref="Refii.Textures.Engine_TexHeight"/>, Passes = 24).
    /// One batch of new NPCs was therefore one frame's work. Measured on the 2026-08-19 freeze:
    /// the Godot main thread sat at 98% of a core in userspace for over four minutes with RSS
    /// flat to the byte, `godot.log` silent, while the guest stayed healthy and the last frame
    /// stayed on screen. The same mechanism at a smaller batch size is the stutter.
    ///
    /// The seam is that <see cref="Refii.Models.Engine_MdlComposite.Build"/> takes nothing but a
    /// list of PATHS. Reading which parts an entity wears is a live guest read and stays here;
    /// turning those paths into a mesh is pure asset work and does not.
    ///
    /// LEGAL FOR THE SAME REASON THE REGION BUILD IS: this creates RESOURCES, never nodes, and
    /// touches no scene tree. The caches it reaches through — the bank reader, the material
    /// texture cache, the model index — were already locked when Engine_LevelScene.Build moved
    /// to a worker, and a level change already puts two builders in them at once.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentQueue<System.Action> _completions =
        new();

    /// <summary>Composite keys currently being built, so a miss queues once and not per frame.</summary>
    private readonly HashSet<string> _compositeJobs = new(System.StringComparer.Ordinal);

    /// <summary>Model hashes currently being built, for the same reason.</summary>
    private readonly HashSet<uint> _meshJobs = new();

    /// <summary>
    /// Builds allowed in flight at once.
    /// </summary>
    /// <remarks>
    /// Small on purpose. These threads meet in the bank reader, whose file position and read are
    /// one locked operation, so past a handful they queue on that lock instead of working — and
    /// every one of them is competing with the guest, which is the simulation.
    /// </remarks>
    private const int MaxInFlightBuilds = 3;

    private int _inFlight;

    public override void _Process(double delta)
	{
		using (Refii.Core.Engine_FramePhases.Measure("entities_process"))
		{
			entities_processBody(delta);
		}
	}

	/// <summary>What the last entity walk's loop cost, in milliseconds.</summary>
	private double LastLoopMs;

	/// <summary>Feeds since the last cost report; the report is every 120th.</summary>
	private int _costReports;

	private void entities_processBody(double delta)
	{
        Current = this;
        long lap = System.Diagnostics.Stopwatch.GetTimestamp();
        if (HostBridge.TryGetAbiVersion() is null || !Visible)
        {
            return;
        }

        // BEFORE the early-out, because the clocks beat whether or not this frame had anything
        // to draw, and a sampler that only runs on populated frames measures the population.
        SampleSimClock();

        // BEFORE the early-out on an empty entity list, and before anything that can return.
        // A finished build releases its in-flight slot here; draining only on populated frames
        // would leave the slots held through a level change and stall every later build.
        // Draining is a dictionary store apiece, so it needs no budget — the cost this used to
        // have is now on the worker.
        while (_completions.TryDequeue(out System.Action? apply))
        {
            apply();
        }

        Refii.Core.Engine_FramePhases.Lap("ent.pre", ref lap);

        // ONCE PER GUEST TICK. The guest steps at 30 Hz or less and the display can draw
        // faster; between two ticks every placement and pose reads identically, so the walk
        // and the loop are skipped and the scene keeps what the last tick placed.
        // Once per guest frame (30 Hz, Engine_GuestFrame), not per 15 Hz game tick: the guest
        // writes two poses per tick. Between guest frames its memory reads identically, so the
        // walk and the loop are skipped and the pairs are redrawn.
        uint feedTick = Refii.Core.Engine_TickClock.GuestGameTick;
        if (!Refii.Core.Engine_GuestFrame.Changed && !FeedEveryFrame)
        {
            ++FeedFramesSkipped;

            // THE TIMELINE STILL HAS TO BE SAMPLED. Skipping the walk between ticks is right —
            // the guest states nothing new — but the SMOOTHING is what turns those ticks into
            // motion, and it lives inside the walk. The camera samples its own timeline every
            // frame (GameWorld), so with this loop skipped the camera glided at display rate
            // while every entity stepped at tick rate: the hero trailed the camera and snapped
            // back when he stopped.
            //
            // Only the movers. A rock's timeline holds one sample and answers the same
            // transform for ever, so re-applying it for 2900 static props would cost the walk
            // we just skipped, to no effect.
            ApplyDrawn();
            return;
        }

        // The guest frame that completes a game tick: the sub-step on which the guest poses its
        // far rigs (FarRigPosesThisStep).
        _tickCompletedThisWalk = feedTick != _feedTickApplied;
        _feedTickApplied = feedTick;

        // The walk kicked at frame start (GameWindow) is taken here; without one — the lever
        // off, or no kick this frame — it is walked synchronously.
        List<HostBridge.GuestEntity> entities;
        uint unknown;
        HostBridge.UnknownPlacement[] unknownVtables;
        if (HostEntityWalk.TryTake(out HostEntityWalk.Walk walk))
        {
            entities = walk.Entities;
            unknown = walk.Unknown;
            unknownVtables = walk.UnknownVtables;
        }
        else
        {
            entities = HostBridge.GetEntities(out unknown, out unknownVtables);
        }

        Refii.Core.Engine_FramePhases.Lap("ent.walk", ref lap);
        if (entities.Count == 0)
        {
            return;
        }

        // WHERE THE LOOP'S TIME GOES, accumulated per section rather than lapped per entity: a
        // lap inside a 300-entity loop records 300 values a frame and says nothing. These are
        // summed across the walk and recorded once, so they read as "pose cost this frame".
        long poseTicks = 0;
        long placeTicks = 0;
        long clipTicks = 0;
        int posedWritten = 0;
        int posedHalfRate = 0;
        long prologueTicks = 0;
        long skipTicks = 0;
        long highlightTicks = 0;
        int pastPrologue = 0;
        long facingTicks = 0;
        long extentTicks = 0;
        long traceTicks = 0;
        long yawTicks = 0;
        long stillTicks = 0;
        long twinTicks = 0;
        Refii.Entities.Engine_EntitySkeleton.ReadMs = 0.0;
        Refii.Entities.Engine_EntitySkeleton.WriteMs = 0.0;
        Refii.Entities.Engine_EntitySkeleton.WritesSkipped = 0;
        _reblending.Clear();
        _animating.Clear();
        int posed = 0;

        // ONE CAMERA READ FOR THE WHOLE WALK. `GetCamera3D` is a viewport query; asking it per
        // entity would be 2900 crossings to answer the same question.
        Camera3D? eye = GetViewport()?.GetCamera3D();
        Vector3 eyeAt = eye is not null && IsInstanceValid(eye)
            ? eye.GlobalTransform.Origin
            : Vector3.Zero;
        bool haveEye = eye is not null && IsInstanceValid(eye);
        Plane[] frustum = haveEye ? Frustum(eye!) : System.Array.Empty<Plane>();
        ulong nowMs = Time.GetTicksMsec();

        _seen.Clear();
        _drawnRecords.Clear();
        _drawnPlaces.Clear();
        _drawnKeys.Clear();

        // Cleared at the START of the walk: `PlaceAttached` iterates this list on the frames
        // between walks too, so attachments keep up with bones that move every frame.
        _attached.Clear();
        Refii.Entities.Engine_EntityHighlight.BeginWalk();
        Refii.Entities.Engine_EntityAppearance.BeginWalk();

        // The half-res surface the highlight silhouettes are drawn into. Idempotent; builds
        // nothing until the post chain is up, since the layer reaches the screen only through it.
        Refii.Entities.Engine_EntityHighlightLayer.Ensure(this);

        // PER FRAME, because that is what the report claims to describe. A counter that
        // accumulates for the life of the run answers a different question than its name asks,
        // and printed beside a live entity count it reads as a per-frame ratio that nothing
        // divides into.
        _withModel = 0;
        _noMesh = 0;
        _prologueSkipped = 0;
        _awaitingBuild = 0;
        _awaitingStub = 0;
        _awaitingCreate = 0;

        // Reset for the same reason, and beside the two above so the whole report is on one
        // clock — a mix of run totals and per-frame counts in one line multiplies the totals
        // by however many frames have passed.
        _composites = 0;
        _multiMesh = 0;
        _multiMeshParts = 0;
        _builtParts = 0;
        _compositePartsUnresolved = 0;
        Refii.Entities.Engine_EntitySkeleton.BeginFrame();

        _batches?.Refresh();
        if (_batches is not null && !Refii.Entities.Engine_EntityBatches.Enabled && Refii.Entities.Engine_EntityBatches.Batched != 0)
        {
            UnbatchAll();
        }

        uint heroAddress = HostBridge.HeroEntityAddress();
        (uint playerOne, uint playerTwo) = HostBridge.CoopPlayerEntities();
        _principals = (heroAddress, playerOne, playerTwo);
        _createdThisFrame = 0;
        _createFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        _guestPositions.Clear();
        foreach (HostBridge.GuestEntity entity in entities)
        {
            long prologueFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            Refii.Models.Engine_ClassicZones.NoteEntity(entity.RecordHash, entity.Address);
            if (IsUsablePlacement(entity.Position))
            {
                _guestPositions[entity.Address] = entity.Position;
            }

            if (entity.Address == heroAddress && heroAddress != 0u && IsUsablePlacement(entity.Position))
            {
                // THE HERO'S STATED POSITION, in Godot space, for whatever samples the world at
                // the hero the way the guest does (the painted day-set grid).
                HeroPosition = ToGodot(entity.Position, entity.Rotation).Origin;
                HeroGuestPosition = entity.Position;
                HeroGuestRotation = entity.Rotation;
            }

            // NOTHING TO DRAW, ALREADY KNOWN. Most entities are triggers, markers and
            // sound emitters: no model hash, no composite parts, no mesh records. The two part
            // resolvers below only re-read the guest on the reconcile cadence and answer from a
            // cache between, so for such an entity the prologue was three dictionary lookups per
            // guest frame to reach the same `continue`. The
            // verdict is keyed on the entity's unique id (an address recycles) and lasts until the
            // same cadence re-asks the guest, when it is confirmed or dropped.
            if (entity.ModelHash == 0 &&
                _nothingToDraw.TryGetValue(entity.Address, out ulong knownId) &&
                knownId == entity.UniqueId &&
                !Due(entity.Address, ReconcileTicks))
            {
                ++_prologueSkipped;
                skipTicks += System.Diagnostics.Stopwatch.GetTimestamp() - prologueFrom;
                continue;
            }

            // STILL AND ADOPTED. Many entities are static props adopted into the level's
            // batches: a hidden node here, a slot in a batch there. Unchanged (the walk compares
            // every stated word, placement and look alike) and not due for a reconcile, the whole
            // prologue below is lookups that end at the still-entity `continue`; the twin slot
            // keeps the transform it has. The node stays owned (`_seen`) so Retire keeps it.
            // The same for a still prop already promoted into an entity batch: hidden node, slot
            // in a batch, nothing to re-apply while unchanged. A change of placement or look
            // (both in `Changed`) takes the full path, which unbatches it.
            // A batched prop is still DRAWN by us: its record and place go into the drawn sets
            // exactly as on the full path, or the reconcile reads the record as undrawn and
            // stands the region's copy back up inside it.
            bool adoptedStill = _adoptions.ContainsKey(entity.Address);
            if (!entity.Changed && !Due(entity.Address, ReconcileTicks) &&
                (adoptedStill || (_batches?.Contains(entity.Address) ?? false)))
            {
                if (!adoptedStill)
                {
                    if (entity.RecordHash != 0)
                    {
                        _drawnRecords.Add(entity.RecordHash);
                    }
                    if (ModelKeyFor(entity.ModelHash) is { } stillKey)
                    {
                        _drawnKeys.Add((entity.Address, stillKey));
                    }
                }

                _seen.Add(entity.Address);
                ++_withModel;
                ++_prologueSkipped;
                skipTicks += System.Diagnostics.Stopwatch.GetTimestamp() - prologueFrom;
                continue;
            }

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
                // TWO ROUTES, NOT ONE. Component 17 layers a character; component 5
                // (`CECGraphicAppearanceStaticMultipleMesh`) holds a VECTOR of mesh records and
                // is what buildings and multi-part scenery use. Neither states a hash in the
                // single-slot appearance components the entity walk reads, so both arrive here
                // as `ModelHash == 0`. Missing the second is why a region's build-time copy
                // looked load-bearing: its standing records were named `… interior` / `…
                // exterior`, which are two records of ONE MeshRecords list.
                if (ResolveComposite(entity.Address) is { } composite)
                {
                    mesh = composite;
                    ++_withModel;
                    ++_composites;
                    _nothingToDraw.Remove(entity.Address);
                }
                else if (ResolveMultiMesh(entity.Address) is { } multiple)
                {
                    mesh = multiple;
                    ++_withModel;
                    ++_multiMesh;
                    _nothingToDraw.Remove(entity.Address);
                }
                else
                {
                    _nothingToDraw[entity.Address] = entity.UniqueId;
                    continue;
                }
            }
            else if (EyesOf(entity.Address) is { Length: > 0 } eyes)
            {
                // EYES ARE COMPONENT 7'S OWN, NOT THE MORPH LIST'S. `CreatureHeroChild` states
                // its whole body as one ModelFile on component 6 and its eyes on
                // GraphicAppearanceEyesComponent, and the guest hangs them on that same rig's
                // Shadow_R_Eye/Shadow_L_Eye (`sub_82209480`) — so a single-model character with
                // eyes is assembled like a composite: the body first, then the two eyes, which
                // the build anchors to the eye bones. Cached by the part list like every
                // composite. This is why the child hero had no eyes: nothing on the single-model
                // route ever asked for them.
                ++_withModel;
                mesh = ComposeParts(BodyAndEyes(entity.Address, entity.ModelHash, eyes));
                if (mesh is null)
                {
                    continue;
                }

                ++_composites;
            }
            else
            {
                ++_withModel;

                // A miss returns null and queues; the entity draws once the build lands. Counted
                // apart from _noMesh, which is the asset-fault signal ("a hash that resolves to a
                // PATH is not a model that loads") and must not absorb entities that are merely
                // early.
                if (!_meshes.ContainsKey(entity.ModelHash))
                {
                    // A model that has come back as a stub StubGiveUp times is a body no mounted
                    // bank has yet: it stops counting as pending (the release gate would wait on
                    // it forever) and is re-asked every StubRetryTicks in case a bank paged it in.
                    if (_stubReturns.TryGetValue(entity.ModelHash, out int stubs) && stubs >= StubGiveUp)
                    {
                        ++_awaitingStub;
                        if (_ticks % StubRetryTicks == 0)
                        {
                            Resolve(entity.ModelHash);
                        }

                        continue;
                    }

                    ++_awaitingBuild;
                    Resolve(entity.ModelHash);
                    continue;
                }

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
            bool adopted = _adoptions.ContainsKey(entity.Address);
            // UNRIGGED MODELS ONLY, for now. A rigged twin would have to follow its dominant
            // bone's pose relative to its rest, and the first cut of that displaced the
            // market's double doors; until that delta is verified against a door offline, a
            // rigged entity keeps the hide-and-draw path, which is right but for the seam.
            if (!adopted && !_nodes.ContainsKey(entity.Address) && Twins is { } adoptable &&
                Refii.Models.Engine_MdlRig.TryFrom(mesh) is null &&
                ModelKeyFor(entity.ModelHash) is { } adoptKey && IsUsablePlacement(entity.Position) &&
                adoptable.TryAdopt(entity.RecordHash, adoptKey,
                    ToGlobal(ToGodot(entity.Position, entity.Rotation).Origin))
                    is { } adoption)
            {
                _adoptions[entity.Address] = (adoption, entity.UniqueId);
                adopted = true;
            }

            if (!adopted)
            {
                if (entity.RecordHash != 0)
                {
                    _drawnRecords.Add(entity.RecordHash);
                }
                if (ModelKeyFor(entity.ModelHash) is { } modelKey)
                {
                    _drawnKeys.Add((entity.Address, modelKey));
                }
            }

            bool created = false;
            if (!_nodes.TryGetValue(entity.Address, out MeshInstance3D? node))
            {
                // Node creation is budgeted per frame by time, bounded by a minimum count (so a
                // region cannot stall on one slow creation) and a maximum; the rest wait a frame.
                // Under the load hold the larger load budget applies: nothing is on screen, and
                // a deferred node would appear after the player has control.
                int maxCreations = LoadHold ? MaxCreationsPerLoadFrame : MaxCreationsPerFrame;
                double createMs = LoadHold ? CreateMillisecondsPerLoadFrame : CreateMillisecondsPerFrame;
                if (_createdThisFrame >= MinCreationsPerFrame &&
                    (_createdThisFrame >= maxCreations ||
                        (System.Diagnostics.Stopwatch.GetTimestamp() - _createFrom) * 1000.0 /
                            System.Diagnostics.Stopwatch.Frequency >= createMs))
                {
                    ++_creationsDeferred;
                    ++_awaitingCreate;
                    continue;
                }

                ++_createdThisFrame;
                created = true;
                if (entity.Address == heroAddress && heroAddress != 0u)
                {
                    GD.Print($"[game-entities] hero {heroAddress:X8} node created on tick {_ticks}" +
                        (LoadHold ? " (under the load hold)" : " (AFTER the load hold)"));
                }
                node = new MeshInstance3D
                {
                    Name = $"Entity_{entity.Address:X8}",
                    Mesh = mesh,
                    // Layer 1 draws it and the second bit is what a fixture's shadow map takes; a
                    // lamp host takes neither, as its static twin would not.
                    Layers = Refii.Lighting.Engine_LevelLights.EntityLayersOf(entity.RecordHash),
                    // It moves: it takes the GI from the probes and is never voxelised into it.
                    GIMode = GeometryInstance3D.GIModeEnum.Dynamic,
                };
                _nodeMesh[entity.Address] = mesh;
                node.SetMeta(MetaEntityAddress, entity.Address);
                node.SetMeta(MetaEntityUniqueId, entity.UniqueId);
                node.SetMeta(MetaEntityRecord, entity.RecordHash);
                node.SetMeta(MetaEntityModel, entity.ModelHash);

                // HERE, on the frame the live copy is created, and not merely when it is
                // reported — hiding on the report would take the region's copy away for an
                // entity whose mesh never loaded, turning a duplicate into a hole. The periodic
                // reconcile below enforces the same invariant for everything else.
                if (adopted)
                {
                    // The region's copy is the drawable; this node only carries the rig.
                    node.Visible = false;
                }
                else
                {
                    Twins?.Claim(entity.RecordHash);
                }
                AddChild(node);
                _nodes[entity.Address] = node;
                if (entity.ModelHash == 0u)
                {
                    // A multi-mesh appearance: a building whose interior records are the rain's
                    // and snow's roofs.
                    Refii.Level.Engine_ClassicRoofs.Track(entity.Address, node);
                }

                // Light it. These entities are created at runtime and hang BESIDE the region
                // rather than inside it, so neither the level scene's baked pass nor the prop
                // streamer ever touches them — without this they run on the shader's bare
                // defaults, which is no bake and no ambient, and every character in the world
                // renders as a solid black silhouette in a correctly lit street.
                //
                // They carry no probe metadata: the guest places them, so there was no build
                // step to sample the field at. The theme's flat ambient is what they get.
                LightEntity(node);

                bool ranged = StateDrawRange(entity.Address, node);
                if (!TryRig(entity.Address, node, mesh))
                {
                    // No skeleton = dressing: it gets the region's prop policies — its stated
                    // range, else a screen-size cull — and a shadow reach keyed on its size.
                    _unrigged.Add(entity.Address);
                    Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(node, mesh);
                    Refii.Lighting.Engine_ShadowReach.Adopt(node);
                }
                else if (ranged)
                {
                    Refii.Level.Engine_PropDrawRange.Apply(node);
                }

                // The cloth body goes on BEFORE the cluster pass: it hides the surfaces it
                // draws through the same pass, which then presents the body once.
                if (Refii.Physics.Engine_ClothBody.Attach(node, mesh, PartPathsOf(mesh)) is { } cloth)
                {
                    LightEntity(cloth);
                }

                Refii.Entities.Engine_HiddenClusters.Apply(node, entity.Address);
                AttachDisplacementParts(entity.Address, node, mesh);
                if (Refii.Models.Engine_ClassicMaterial.Active)
                {
                    Refii.Models.Engine_ClassicDisplacement.AdoptRecord(entity.RecordHash, node, mesh);
                }
                Refii.Entities.Engine_ShadowProxy.Sync(node);
            }
            else if (!_nodeMesh.TryGetValue(entity.Address, out Mesh? drawnMesh) || !ReferenceEquals(drawnMesh, mesh))
            {
                // An entity can change model — a door opening, a prop breaking, a body that
                // dresses after it was rigged. Compared against what this feed assigned, not the
                // node's property: that read is a crossing per entity per frame.
                Unbatch(entity.Address, node);
                node.Mesh = mesh;
                _nodeMesh[entity.Address] = mesh;
                Refii.Physics.Engine_ClothBody.Attach(node, mesh, PartPathsOf(mesh));
                LightEntity(node);
                Refii.Entities.Engine_HiddenClusters.Apply(node, entity.Address);
                if (_unrigged.Contains(entity.Address))
                {
                    // The cull range and the shadow reach are the mesh's extent: a new mesh,
                    // a new range, and the reach measured again from scratch.
                    node.VisibilityRangeEnd = 0f;
                    Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(node, mesh);
                    Refii.Lighting.Engine_ShadowReach.Release(node);
                    Refii.Lighting.Engine_ShadowReach.Adopt(node);
                }

                // The rig is the mesh's: a composite's bone table is the union of its parts, and
                // the skeleton and skin are built from it, so a new mesh needs a new rig or its
                // vertices address the wrong bones (a body that dresses after it was rigged).
                if (_skeletons.TryGetValue(entity.Address, out Refii.Entities.Engine_EntitySkeleton? stale))
                {
                    node.Skeleton = new NodePath();
                    node.Skin = null;
                    Refii.Entities.Engine_ShadowProxy.Sync(node);
                    if (GodotObject.IsInstanceValid(stale.Node))
                    {
                        stale.Node.GetParent()?.RemoveChild(stale.Node);
                        stale.Node.QueueFree();
                    }

                    _skeletons.Remove(entity.Address);
                    _clipAnimators.Remove(entity.Address);
                    _attachments.Forget(entity.Address);
                    node.VisibilityRangeEnd = 0f;
                    if (!TryRig(entity.Address, node, mesh))
                    {
                        _unrigged.Add(entity.Address);
                        Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(node, mesh);
                        Refii.Lighting.Engine_ShadowReach.Adopt(node);
                    }
                    else
                    {
                        Refii.Level.Engine_PropDrawRange.Apply(node);
                    }

                    GD.Print($"[rig] entity 0x{entity.Address:X8} changed mesh — re-rigged " +
                        $"({(_skeletons.ContainsKey(entity.Address) ? "new skeleton" : "no rig yet, retrying")})");
                }

                // Last: the cull range, the shadow reach and the rig above are what it copies.
                Refii.Entities.Engine_ShadowProxy.Sync(node);
            }
            else if (Due(entity.Address, ReconcileTicks))
            {
                // Clothes change without the model changing: the hero dresses, an NPC's outfit
                // is re-committed. The garment set is re-read on the reconcile cadence.
                Refii.Entities.Engine_HiddenClusters.Apply(node, entity.Address);
                Refii.Entities.Engine_ShadowProxy.SyncHidden(node);
            }

            // THE STATED LOOK — alpha, drawable — also ABOVE the skip: an entity can fade without
            // moving (a dying hollow man). A look other than the constructor's needs the
            // node itself: a batch draws every instance alike, and a married twin may be one
            // instance of a MultiMesh. The marriage ends for good; the region's copy goes, as
            // it would for an entity destroyed, and this node draws from now on.
            if (Refii.Entities.Engine_EntityAppearance.NeedsOwnNode(entity.Appearance))
            {
                if (adopted && _adoptions.TryGetValue(entity.Address, out var marriage))
                {
                    Twins?.Release(marriage.Twin, hide: true);
                    _adoptions.Remove(entity.Address);
                    adopted = false;
                }

                Unbatch(entity.Address, node);
            }

            Refii.Entities.Engine_EntityAppearance.Apply(node, entity.Appearance, IsPrincipal(entity.Address));

            // The stated targeting highlight goes ABOVE the unchanged-entity skip: the guest
            // ramps its colour over several ticks on a target that may not move. Cheap when
            // unchanged: the word is stored per mesh and compared first.
            long highlightFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            Refii.Entities.Engine_EntityHighlight.Apply(
                node, entity.Highlighted, entity.HighlightColour, entity.Highlight);
            highlightTicks += System.Diagnostics.Stopwatch.GetTimestamp() - highlightFrom;

            // Everything above runs for every entity, every walk, and is timed as one span
            // (`prologue` in the log line).
            // Skipped below when unchanged since the last walk and nothing would act: no rig to
            // pose, no parent bone to hang from, the node already on the same mesh, off the
            // reconcile cadence; a married twin keeps the transform it was last driven to. The
            // walk compared the stated words itself.
            prologueTicks += System.Diagnostics.Stopwatch.GetTimestamp() - prologueFrom;

            if (!entity.Changed && !created && !Due(entity.Address, ReconcileTicks) &&
                entity.Appearance.IsConstructed &&
                !_skeletons.ContainsKey(entity.Address) &&
                !_movers.Contains(entity.Address) &&
                _attachments.ParentOf(entity.Address) == 0u)
            {
                ++_unchangedSkipped;
                long stillFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                NoteStill(entity, node, mesh, adopted);
                stillTicks += System.Diagnostics.Stopwatch.GetTimestamp() - stillFrom;
                continue;
            }

            // The guest has stated something new, or the prop has become more than a prop: it
            // is a node again before anything below places or poses it.
            if (entity.Changed || created || entity.Highlighted ||
                !entity.Appearance.IsConstructed ||
                _skeletons.ContainsKey(entity.Address) || _movers.Contains(entity.Address) ||
                _attachments.ParentOf(entity.Address) != 0u)
            {
                Unbatch(entity.Address, node);
            }

            ++pastPrologue;

            // A NON-FINITE ORIGIN TAKES GODOT'S RENDERER DOWN, not just this entity.
            //
            // The render list is sorted by depth with std::sort, whose final pass in libstdc++
            // is __unguarded_linear_insert — no bounds check, relying on the minimum already
            // sitting in the first partition. EVERY comparison against NaN is false, so one NaN
            // depth destroys that invariant: the insert walks off the FRONT of the array,
            // dereferences whatever lies below it and faults. The fault re-executes the same
            // instruction forever, so the process pins a core, stops drawing, ignores SIGTERM
            // and has to be SIGKILLed.
            //
            // Caught live under gdb (2026-08-19): 437 surfaces, key bytes all 128 so the
            // tie-break float decided every comparison, slots 0..30 correctly ordered
            // 346.3 -> 300.0, and slot 33 carrying depth=nan.
            //
            // The ROTATION above was hardened against exactly this class and the position was
            // left open. The guest publishes both by writing in place with no publish step, so
            // a read landing mid-update is the same fault in a different field.
            //
            // The bound is not decoration. A depth is a squared distance, and 1e20 squared is
            // +inf, which subtracts to NaN a step later with every component still "finite".
            //
            // KEEPS THE LAST GOOD TRANSFORM rather than zeroing: a torn read is one frame's
            // noise and the next read is usually clean, where snapping to the origin would be
            // a visible teleport.
            long placeFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            if (IsUsablePlacement(entity.Position))
            {
                Transform3D stated = ToGodot(entity.Position, entity.Rotation);

                // An attached child is placed by PlaceAttached under its parent's bone, which
                // also drops its pair; it is not blended here.
                bool attachedChild = _attachments.ParentOf(entity.Address) != 0u;

                if (!attachedChild)
                {
                    Place(entity.Address, node,
                        Refii.Entities.Engine_EntityAppearance.Scaled(Drawn(entity, stated), entity.Appearance));
                }

                // LIT FROM THE LEVEL'S PROBE FIELD where it stands, as the guest lights every
                // dynamic object; re-sampled on the reconcile cadence (it moves) and at once
                // when it has no sample yet.
                // And only when the answer can have changed: the sample depends on position and
                // theme, so it is redone when the entity has moved half a metre or the region's
                // lighting generation has changed. Each sample allocates Godot wrappers.
                if (Due(entity.Address, ReconcileTicks) || _relit.Add(entity.Address))
                {
                    // A region's lights can spawn after the entity that hosts one.
                    uint layers = Refii.Lighting.Engine_LevelLights.EntityLayersOf(entity.RecordHash);
                    if (node.Layers != layers)
                    {
                        // A batch is keyed by its layers; the prop rejoins one under the new ones.
                        Unbatch(entity.Address, node);
                        node.Layers = layers;
                        Refii.Entities.Engine_ShadowProxy.Sync(node);
                    }

                    Vector3 at = node.IsInsideTree() ? node.GlobalTransform.Origin : Vector3.Zero;
                    long generation = Refii.Lighting.Engine_LevelBakedLighting.LightingGeneration;
                    if (!_lastLit.TryGetValue(entity.Address, out (Vector3 At, long Gen) last) ||
                        last.Gen != generation ||
                        last.At.DistanceSquaredTo(at) > 0.25f)
                    {
                        _lastLit[entity.Address] = (at, generation);
                        RelightEntity(node);
                    }
                }

                // AN ATTACHED CHILD IS NOT PLACED BY ITS OWN TRANSFORM. The guest wrote that
                // transform from ITS pose, which is not the pose being drawn; the child belongs
                // on the host's bone. Deferred so the parent has already been placed and posed.
                if (attachedChild)
                {
                    _attached.Add((entity.Address, node, stated));
                }
            }
            else
            {
                ++NonFinitePlacements;
            }

            // The rig, after the placement: the pose is relative to the entity, so the
            // skeleton rides whatever transform the placement just set.
            long poseFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            placeTicks += poseFrom - placeFrom;
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
                    // GODOT PLAYS THE CLIPS the guest states for this rig; the pose feed is
                    // the fallback only for a frame whose clips cannot be played here.
                    //
                    // EVERY RIG, EVERY FRAME. A frustum gate used to skip rigs the camera
                    // could not see, and the pose it saved was the pose those rigs kept: a
                    // villager culled from the moment it spawned was never posed at all, so
                    // it slid down the street in its bind pose. Driving the timeline is 3.4 ms
                    // of a 50 ms frame for a full street, and a character that animates only
                    // once the camera has already framed it is the defect the gate created.
                    ++posed;

                    // POSE WHAT CAN BE SEEN; ADVANCE EVERYTHING. Beyond the radius the clips
                    // still run — the rig keeps its place in the animation — but the per-bone
                    // sampling and the two engine writes per bone are skipped. A rig that walks
                    // back into range poses correctly on its first written frame, which is what
                    // the old frustum gate could not do because it skipped the advance too.
                    // IN THE TREE FIRST. `GlobalTransform` on a node that is not yet parented
                    // logs an error and answers IDENTITY, which reads as the world origin and
                    // therefore as "far away" — so a character would stop being posed for
                    // exactly the frames it is being built. Unmeasurable means write it.
                    // The guest's own gate (sub_8220D1B8 / sub_822142E8): a rig is posed when
                    // it is in the view frustum, and for 30 render frames after it leaves;
                    // otherwise its pose is HELD and its clips keep running. The first write is
                    // unconditional: a held pose needs a pose to hold.
                    bool measurable = haveEye && node.IsInsideTree();
                    Vector3 rigAt = measurable ? node.GlobalTransform.Origin : Vector3.Zero;
                    float eyeDistanceSq = measurable ? eyeAt.DistanceSquaredTo(rigAt) : 0f;

                    // Distance is a RATE, never a cutoff. `sub_822142E8` gates on the view cone
                    // and on `MaxDrawDistance`, whose default is `flt_82096028` = 10000.0 times a
                    // class multiplier that is 1.0 for all four classes (flt_83319464/68/6C/70),
                    // so the guest does not distance-cull animation. It has a 2:1 divider at
                    // 0x8220D67C instead: inside BoneLODDistance a rig poses on both HF sub-steps,
                    // outside it only on the sub-step that completes a tick (30 Hz / 15 Hz).
                    // A principal off screen still throws a shadow onto it.
                    // Past its stated reach the rig is not drawn and casts nothing, so its pose is
                    // held like one off screen; the clips run on and the next write is correct.
                    // A principal found after its node was stamped loses the stamp here.
                    if (IsPrincipal(entity.Address) && _drawRanges.Remove(entity.Address))
                    {
                        node.RemoveMeta(Refii.Level.Engine_PropDrawRange.Meta);
                        node.VisibilityRangeEnd = 0f;
                        node.VisibilityRangeEndMargin = 0f;
                        if (Refii.Entities.Engine_ShadowProxy.ProxyOf(node) is { } caster)
                        {
                            caster.VisibilityRangeEnd = 0f;
                            caster.VisibilityRangeEndMargin = 0f;
                        }
                    }

                    float reach = DrawnReach(entity.Address) + ReachSlackMetres;
                    bool withinReach = !measurable || float.IsPositiveInfinity(reach) || eyeDistanceSq <= reach * reach;
                    bool inView = IsPrincipal(entity.Address) ||
                        (withinReach && RigInView(entity.Address, measurable, rigAt, frustum, nowMs));
                    // A principal's shadow keeps full fidelity; everyone else's follows on guest frames.
                    Refii.Entities.Engine_ShadowSkeletons.SetEveryFrame(rig.Node, IsPrincipal(entity.Address));
                    bool fullRate = !measurable || IsPrincipal(entity.Address) ||
                        eyeDistanceSq <= PoseFullRateRadius * PoseFullRateRadius;
                    bool writeBones = inView && (fullRate || FarRigPosesThisStep(entity.Address));
                    if (writeBones)
                    {
                        _boneWriteAskedMs[entity.Address] = nowMs;
                        ++posedWritten;
                        if (!fullRate)
                        {
                            ++posedHalfRate;
                        }
                    }

                    long clipFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (!ClipAnimatorFor(entity.Address, rig).Apply(entity.Address, writeBones))
                    {
                        // The feed is about to write this skeleton, so the animator's
                        // written-pose cache no longer describes it.
                        ClipAnimatorFor(entity.Address, rig).ForgetWrittenPose();
                        rig.Follow(entity.Address, writeBones);
                        _reblending.Add(entity.Address);
                    }
                    else
                    {
                        rig.ForgetLastPose();
                        _animating.Add(entity.Address);
                    }

                    clipTicks += System.Diagnostics.Stopwatch.GetTimestamp() - clipFrom;
                }

                long tailFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                SampleFacing(entity.Address, node, entity.Rotation);
                long facingAt = System.Diagnostics.Stopwatch.GetTimestamp();
                StateVisibleExtent(entity.Address, node, rig);
                long extentAt = System.Diagnostics.Stopwatch.GetTimestamp();
                SampleTrace(entity.Address, node, StatedOf(entity.Address, node.Transform), rig);
                facingTicks += facingAt - tailFrom;
                extentTicks += extentAt - facingAt;
                traceTicks += System.Diagnostics.Stopwatch.GetTimestamp() - extentAt;

                long yawFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                float poseYaw = rig.PoseYawDegrees();
                yawTicks += System.Diagnostics.Stopwatch.GetTimestamp() - yawFrom;
                if (!float.IsNaN(poseYaw))
                {
                    ++_poseYawSamples;
                    _poseYawSum += Mathf.Abs(poseYaw);
                    _poseYawWorst = Mathf.Max(_poseYawWorst, Mathf.Abs(poseYaw));
                }
            }

            poseTicks += System.Diagnostics.Stopwatch.GetTimestamp() - poseFrom;

            if (_adoptions.TryGetValue(entity.Address, out var married))
            {
                long twinFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                Refii.Entities.Engine_StaticTwins.Drive(married.Twin, AdoptedWorld(entity.Address, node));
                twinTicks += System.Diagnostics.Stopwatch.GetTimestamp() - twinFrom;
            }
        }

        long drawnFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyDrawn();
        long flushFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        long drawnTicks = flushFrom - drawnFrom;
        Refii.Level.Engine_PropShadow.FlushDeferred();
        Refii.Core.Engine_FramePhases.Lap("ent.flush", ref flushFrom);
        // THE PLACE OF EVERY DRAWN ENTITY, read from the node AFTER attachment placement: a
        // door is a child attached to its building, and its stated position describes the
        // parent-relative body nobody draws; the node's world origin is where the mesh is.
        foreach ((uint address, string modelKey) in _drawnKeys)
        {
            if (_nodes.TryGetValue(address, out MeshInstance3D? placed))
            {
                _drawnPlaces.Add((modelKey, placed.GlobalTransform.Origin));
            }
        }
        Refii.Core.Engine_FramePhases.Lap("ent.drawnplaces", ref flushFrom);
        // The loop's own breakdown, in the same units as the laps around it.
        double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double poseMs = poseTicks * toMs;
        double placeMs = placeTicks * toMs;
        LastLoopMs = (System.Diagnostics.Stopwatch.GetTimestamp() - lap) * toMs;
        Refii.Core.Engine_FramePhases.Record("ent.pose", poseMs, plot: true);
        Refii.Core.Engine_FramePhases.Record("ent.place", placeMs, plot: true);

        Refii.Core.Engine_FramePhases.Lap("ent.loop", ref lap);
        _unchangedSkippedTotal += _unchangedSkipped;
        _unchangedSkipped = 0;

        // THE BREAKDOWN, IN THE LOG. The phases are readable over the debug channel and as
        // profiler plots, and neither is available on an ordinary run — so the one number this
        // walk exists to explain is printed where every run already writes. Every 120th feed,
        // which is a few seconds, so it is a trace rather than a spew.
        if (++_costReports % 120 == 0)
        {
            // The prologue is timed too; "rest" is only what no section covers.
            double loopMs = poseMs + placeMs + prologueTicks * toMs;
            GD.Print($"[entities] {entities.Count} entity(s), {posed} posed ({posedWritten} written, {posedHalfRate} of them at the guest's half rate): " +
                $"pose {poseMs:0.00}ms (read {Refii.Entities.Engine_EntitySkeleton.ReadMs:0.00}, " +
                $"write {Refii.Entities.Engine_EntitySkeleton.WriteMs:0.00}, " +
                $"{Refii.Entities.Engine_EntitySkeleton.WritesSkipped} settled), " +
                $"place {placeMs:0.00}ms, rest {System.Math.Max(0.0, LastLoopMs - loopMs):0.00}ms " +
                $"— of {LastLoopMs:0.00}ms in the loop | " +
                $"clip {clipTicks * toMs:0.00} facing {facingTicks * toMs:0.00} " +
                $"extent {extentTicks * toMs:0.00} trace {traceTicks * toMs:0.00} " +
                $"yaw {yawTicks * toMs:0.00} | " +
                $"prologue {prologueTicks * toMs:0.00} ({entities.Count - _prologueSkipped} walked) + {skipTicks * toMs:0.00} ({_prologueSkipped} skipped: nothing to draw, or still and adopted/batched) " +
                $"highlight {highlightTicks * toMs:0.00}, {pastPrologue} past it | " +
                $"still {stillTicks * toMs:0.00} twins {twinTicks * toMs:0.00} drawn {drawnTicks * toMs:0.00}");
        }
        Retire();
        Refii.Core.Engine_FramePhases.Lap("ent.retire", ref lap);
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
        // The placement stamp: must be the ungated game tick, or two distinct placements share
        // one tick.
        _tickNow = Refii.Core.Engine_TickClock.GuestGameTick;
        // EVERY WALK. A weapon drawn from the back moves to the hand on one of the guest's
        // frames, as a re-attach with a new ticket; read every fifteenth walk, it hung on the back
        // for up to half a second and then snapped into the hand.
        _attachments.Refresh();

        if (entities.Count != _lastCount)
        {
            _lastCount = entities.Count;
            _settledFor = 0;
        }
        else
        {
            ++_settledFor;
        }

        // A count that holds still for SettleTicks, OR twenty seconds of ticks: Bowerstone
        // Market never holds still (NPCs come and go every few ticks), and gating the reconcile
        // on stillness alone left every layout door standing beside its live twin for a whole
        // session in the first region of a run.
        _everSettled |= _settledFor >= SettleTicks || _ticks >= SettleFallbackTicks;

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
        // ON THE CADENCE FROM THE FIRST TICK, not after the pass settles: reconciling early
        // is safe (a record not yet reported keeps its copy standing, which is the no-hole
        // direction), and waiting for stillness left two door sets up for twenty seconds.
        if (Twins is not null && _ticks > 1 &&
            (_ticks % ReconcileTicks == 0 || _settledFor == SettleTicks || !_reconciledOnce))
        {
            _reconciledOnce = true;
            _liveRecords.Clear();
            foreach (HostBridge.GuestEntity entity in entities)
            {
                if (entity.RecordHash != 0)
                {
                    _liveRecords.Add(entity.RecordHash);
                }
            }

            // AND THE GUEST'S OWN LAYER STATE, which is what decides visibility now.
            //
            // THE DEACTIVATED LIST, not the active one. Every prop the region built came from a
            // layer whose `load` is `AlwaysOn` or `InitiallyOn`, and the guest gates exactly one
            // of those: `InitiallyOn` spawns unless its hash is in worldMgr+72. The activated
            // list at +60 governs the `InitiallyOff` layers we never build, and it is EMPTY on a
            // new game — gating on it would have hidden the whole level.
            //
            // Rebuilt per reconcile rather than cached: `Layers.ActivateLayer` and
            // `DeactivateLayer` move layers while you play, and a set sampled once at mount would
            // freeze the region into whatever the world looked like on that frame.
            //
            // Null is "cannot ask" and is NOT the same as empty, which is the ordinary answer for
            // a world where nothing has been deactivated. `Reconcile` gates nothing on null.
            uint[]? deactivated = HostBridge.GetDeactivatedLayers();
            if (deactivated is not null)
            {
                _deactivatedLayers.Clear();
                foreach (uint layer in deactivated)
                {
                    _deactivatedLayers.Add(layer);
                }
            }

            // The gate is a method group over a set whose contents change, not the field, so
            // one cached delegate serves the whole run.
            Twins.Reconcile(
                _liveRecords, _drawnRecords, !HostBridge.EntitiesTruncated,
                deactivated is null ? null : _layerIsDeactivated ??= _deactivatedLayers.Contains);
            // THE RECORD-LESS TWINS — a building's doors and windows come from its layout with
            // no record of their own, so only the model and the place can name them.
            Twins.HideAtPlaces(_drawnPlaces);
        }

        bool settled = _settledFor == SettleTicks ||
            (_everSettled && _ticks % 600 == 0);
        if (!settled)
        {
            return;
        }

        // A change in the set of unplaceable classes is reported even past the report cap.
        var seen = new System.Text.StringBuilder();
        foreach (HostBridge.UnknownPlacement v in unknownVtables)
        {
            seen.Append(v.Vtable.ToString("X8")).Append(';');
        }

        string signature = seen.ToString();
        bool changed = signature != _unknownSignature;
        _unknownSignature = signature;
        if (_reports >= 10 && !changed)
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
        GD.Print($"[game-entities] {_awaitingBuild} entity(s) waiting on an off-thread build; " +
            $"of {_withModel} with a model hash, {_noMesh} loaded NO MESH " +
            "(header-only stub or not in a mounted bank)" +
            // SPLIT BY WHY, from the loader that actually knows. A stub is a body in an unmounted
            // bank and should drain as streaming banks come up; an undecodable model never will.
            // Both used to return an empty mesh in silence and be counted as a success here.
            $" — the loader turned back {Refii.Models.Engine_BnkMeshLoader.EmptyStubs} stub(s) " +
            $"and {Refii.Models.Engine_BnkMeshLoader.EmptyUndecodable} undecodable model(s) " +
            "in this region");

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

        GD.Print($"[game-entities] morphs: {_compositesMorphed} composite(s) built with morph " +
            $"targets (run total); the guest stated {HostBridge.PartMorphStatements} part(s) " +
            $"with target records, {HostBridge.PartMorphsMorphed} of them morphed, and " +
            $"{HostBridge.TextureLayerStatements} texture composite(s), " +
            $"{Refii.Hero.Engine_HeroTextureMorph.CompositesBuilt} built here");

        GD.Print($"[game-entities] multi-mesh appearances: {_multiMesh} entity(s) drawn this " +
            $"frame from component 5, {_multiMeshParts} mesh record(s) stated — these used to " +
            "read as \"names no model\" and were drawn only by the region's static copy");

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
                $"\n    culling box: stated on {_extentStated} rig(s) ({_extentHeld} held), {_extentOverflow} of " +
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
                $"\n    bone JUMPS over 1.5 m between accepted poses: {Refii.Entities.Engine_EntitySkeleton.PoseJumps}" +
                $"\n    most active rigs (changed/applied): {MostActiveRigs()}" +
                $"\n    pose samples that CHANGED: {Refii.Entities.Engine_EntitySkeleton.PoseChanges} of {Refii.Entities.Engine_EntitySkeleton.PoseApplies} applied ({(Refii.Entities.Engine_EntitySkeleton.PoseApplies > 0 ? 100.0 * Refii.Entities.Engine_EntitySkeleton.PoseChanges / Refii.Entities.Engine_EntitySkeleton.PoseApplies : 0):0}%) — 100% means the animation moves every drawn frame, ~50% at 30 fps means it moves at the 15 Hz tick" +
                $"\n    poses HELD while still local (space 1): {Refii.Entities.Engine_EntitySkeleton.LocalPosesHeld}, rigs stuck local 300+ frames: {Refii.Entities.Engine_EntitySkeleton.LocalStuckRigs}, flag-local-but-data-model applied: {Refii.Entities.Engine_EntitySkeleton.FlagLocalDataModel}" +
                $"\n    poses refused as IMPLAUSIBLE: {Refii.Entities.Engine_EntitySkeleton.ImplausiblePoses}" +
                (Refii.Entities.Engine_EntitySkeleton.ImplausiblePoses == 0
                    ? " — every accepted rig read lay within twice the rig's rest reach"
                    : " — half-converted arrays that read identically twice; held last frame's pose") +
                // WHO IS DRIVING EACH RIG. The clip animator hands a rig back to the pose feed
                // on four conditions, and only one of them is expected: flag 8, the guest's
                // physics-written pose. Folding clips over a Havok pose is what sent an
                // electrocuted body flying apart, so RAGDOLL reading zero while a corpse bursts
                // says the gate never fired — a different fault from the gate firing and the
                // feed then mishandling the array. Until now these were reachable only through
                // the debug channel, which needs a live handshake.
                $"\n    clip-driven frames: {Refii.Animations.Engine_ClipAnimator.ClipDrivenFrames}, " +
                $"handed back — ragdoll: {Refii.Animations.Engine_ClipAnimator.FallbackRagdoll}, " +
                $"procedural: {Refii.Animations.Engine_ClipAnimator.FallbackProcedural}, " +
                $"unknown clip: {Refii.Animations.Engine_ClipAnimator.FallbackUnknownClip}, " +
                $"no leaves: {Refii.Animations.Engine_ClipAnimator.FallbackNoLeaves}" +
                $"\n    rotations refused as NOT UNIT: {NonUnitRotations}" +
                (NonUnitRotations == 0
                    ? " — every entity handed us a real rotation"
                    : " — that many transforms fell back to identity because what the bridge " +
                      "returned was not a rotation") +

                // EVERY REFUSAL IN ONE LINE, and reported from here because this is the only
                // periodic report that runs the whole time the world is up. They were being
                // counted and never printed, which made them useless as the thing they exist
                // for: saying WHICH source produced a non-finite value. A guard nobody can read
                // the result of is just a silent drop.
                //
                // All zero is the answer we want. Any one of them moving names the culprit —
                // and `surfaces` in particular means a DECODE bug, not bad data, because the
                // .mdl bytes are read natively from the bank and should never yield a NaN.
                $"\n    refused as NOT FINITE — placements: {NonFinitePlacements}, " +
                $"camera: {Refii.Game.GameWorld.NonFiniteCameras}, " +
                $"fx: {Refii.Game.GameFx.NonFiniteFx}, " +
                $"ui transforms: {Refii.Ui.Engine_UiSceneBuilder.NonFiniteLiveTransforms}, " +
                $"prop instances: {Refii.Level.Engine_LevelProps.NonFiniteInstances}, " +
                $"mesh surfaces: {Refii.Models.Engine_MdlMesh.NonFiniteSurfaces}" +

                // Reported beside them but NOT as one of them: an out-of-reach effect carries a
                // real number and is a range judgement of ours, not evidence of a torn read.
                $"; fx out of reach: {Refii.Game.GameFx.OutOfReachFx}");
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
            int shown = twins.Records - twins.HiddenForLive - twins.HiddenByLayer;
            GD.Print($"[game-entities] static twins: {twins.Records} record(s) over " +
                $"{twins.Instances} drawable(s) — {twins.HiddenForLive} hidden because the " +
                $"guest draws its own, {twins.HiddenByLayer} hidden because their LAYER is " +
                $"deactivated, {twins.HiddenByPlace} hidden by model and place, {twins.Adopted} married to a live entity, {shown} left standing as the only copy" +
                (twins.LiveWasTruncated
                    ? "  (the entity walk saturated its buffer this tick — the dedup half is " +
                      "under-counting, the layer gate is unaffected)"
                    : string.Empty));

            // THE GATE'S OWN HEALTH, said every time, because the failure it guards against is
            // invisible by construction: a wrongly-read layer state hides props while every
            // other counter on this line stays perfectly plausible.
            GD.Print($"[game-entities] layer gate: " +
                (twins.LayerGateRan
                    ? $"ON over {twins.LayeredRecords} record(s) in " +
                      $"{twins.AlwaysOnLayers} AlwaysOn layer(s) + the rest InitiallyOn"
                    : "OFF — no `.save` layer map on the region, or the bridge could not be " +
                      "asked; nothing is being gated") +
                (twins.GatedButLive == 0
                    ? string.Empty
                    : $"  ** {twins.GatedButLive} RECORD(S) GATED OFF THAT THE GUEST REPORTS AS " +
                      "LIVE — the layer state is being read wrongly and props are being deleted **"));

            GD.Print($"[game-entities] {twins.Unmatched} live record(s) the region drew nothing " +
                $"for, {twins.NoRecord} live draw(s) with no record hash, {twins.Restored} " +
                "record(s) restored after the guest gave one up");

            if (twins.HiddenAsDestroyed != 0)
            {
                GD.Print($"[game-entities] {twins.HiddenAsDestroyed} record(s) held hidden " +
                    "because the guest's DeadEntities set says the placement is destroyed — " +
                    "without this the region's build-time copy stands where the smashed one was");
            }

            if (twins.StandingSample.Count != 0)
            {
                GD.Print("[game-entities] the static layer's whole return — records standing as " +
                    "the only copy: " + string.Join(" ", twins.StandingSample));
            }
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
                Refii.Entities.Engine_BodyVariants.SourceMesh(owner) is { } rigMesh)
            {
                ReportRig(rigMesh, rigged.Value);
            }
        }

        // PLAYER TWO'S RIG, NAMED, ONCE — outside the report cap, because a co-op body drawn
        // wrong on the host is the one rig whose mapping numbers have to be readable.
        (uint one, uint two) = Refii.Host.HostBridge.CoopPlayerEntities();
        if (two != 0u && two != _playerTwoReported &&
            _skeletons.TryGetValue(two, out var twoRig) &&
            _nodes.TryGetValue(two, out MeshInstance3D? twoNode) && Refii.Entities.Engine_BodyVariants.SourceMesh(twoNode) is ArrayMesh twoMesh)
        {
            _playerTwoReported = two;
            int surfaces = twoMesh.GetSurfaceCount();
            GD.Print($"[rig] PLAYER TWO 0x{two:X8} (player one 0x{one:X8}): '{twoMesh.ResourceName}' " +
                $"{surfaces} surface(s), skeleton bones={twoRig.BoneCount} (guest rig {twoRig.GuestBoneCount}, " +
                $"{twoRig.Mapped} mapped by name), node scale {twoNode.Scale}, " +
                $"aabb {twoMesh.GetAabb().Size}");
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

    private readonly Dictionary<uint, Refii.Animations.Engine_ClipAnimator> _clipAnimators = new();

    private Refii.Animations.Engine_ClipAnimator ClipAnimatorFor(
        uint address, Refii.Entities.Engine_EntitySkeleton rig)
    {
        // THE RIG, NOT THE ADDRESS, IS THE IDENTITY. A job re-creates the hero: the entity is
        // retired and built again at the same address with a new skeleton, and an animator
        // still holding the old one throws ObjectDisposedException from inside the entity walk
        // — 5,421 times after one blacksmith shift, each aborting the frame's loop for every
        // rig after the hero. "All the animations broke after the minigame."
        if (!_clipAnimators.TryGetValue(address, out Refii.Animations.Engine_ClipAnimator? animator) ||
            !animator.Drives(rig.Node))
        {
            animator = new Refii.Animations.Engine_ClipAnimator(rig.Node, rig.Node.Name, rig.ModelToGuest);
            _clipAnimators[address] = animator;
        }

        animator.Rig = rig;
        animator.Address = address;
        return animator;
    }

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

            uint[] hashes = Refii.Entities.Engine_BodyVariants.SourceMesh(node) is Refii.Entities.Engine_EntityResource resource
                ? System.Linq.Enumerable.ToArray(resource.ModelHashes)
                : System.Array.Empty<uint>();

            animator.Drive(rig.Node, rig.Node.Name, hashes);
            _locomotionDriven++;
        }
    }

    /// <summary>
    /// Puts every attached child on its parent's bone, after the parents have been placed and
    /// posed this frame. A child whose parent is not drawn — off in an unloaded part of the
    /// world, or without a rig — keeps the guest's own placement, which is what it had before.
    /// </summary>
    /// <summary>Attached children placed on the guest's own statement because no host bone could take them.</summary>
    public static long AttachedOnStated;

    private void PlaceAttached()
    {
        foreach ((uint child, MeshInstance3D node, Transform3D stated) in _attached)
        {
            // A child whose dummy names no bone this host has, or whose parent has no rig yet, is
            // placed on the guest's own statement; the walk defers every attached child to this
            // pass, so nothing else places it.
            //
            // The guest's contract (CECObjectAttachment): on every parent move, `sub_825EBAA0`
            // writes each live child's placement as inv(grip) x dummy x parentWorld
            // (`sub_825ED2A0`, unconditional). The dummy lookup `sub_82235CA8` tries the part
            // gmd tables (`sub_8223F668`, `sub_8223BE58`) and otherwise returns the IDENTITY
            // (`sub_8223BDB0`), so a child whose dummy no gmd names is planted at its parent's
            // root every update — never detached, never hidden. Following the statement puts
            // an unresolved child exactly there.
            uint parent = _attachments.ParentOf(child);
            Transform3D placed = stated;
            bool onBone = _nodes.TryGetValue(parent, out MeshInstance3D? parentNode) &&
                _skeletons.TryGetValue(parent, out Refii.Entities.Engine_EntitySkeleton? rig) &&
                _attachments.TryPlace(child, stated, StatedOf(parent, parentNode.Transform),
                    parentNode.Transform, rig, PartPaths(parentNode), out placed);
            if (!onBone)
            {
                ++AttachedOnStated;
            }

            // THE WEAPON'S SHADOW FOLLOWS AT THE GUEST'S RATE. Marked before the placement
            // below, so a proxy built on this frame is placed by it; the holder's principal
            // status decides every-frame placement, as it does for the holder's own body.
            if (Refii.Entities.Engine_ShadowProxy.MarkFollower(node, true, IsPrincipal(parent)))
            {
                Refii.Entities.Engine_ShadowProxy.Sync(node);
            }

            _followers[child] = node;
            Place(child, node, placed);
            _bodies.Remove(child);  // it is not being blended; do not blend back to it
        }

        // DROPPED, NOT MISSED. A follower stops following when the guest's attachment record says
        // it has no parent — not when this frame's list lacks it, which a torn placement read can
        // cause for one walk. The body then casts as any rigid prop does.
        foreach ((uint child, MeshInstance3D node) in _followers)
        {
            if (!GodotObject.IsInstanceValid(node))
            {
                _unfollowed.Add(child);
                continue;
            }

            if (_attachments.ParentOf(child) == 0u)
            {
                _unfollowed.Add(child);
                if (Refii.Entities.Engine_ShadowProxy.MarkFollower(node, false, false))
                {
                    Refii.Entities.Engine_ShadowProxy.Sync(node);
                }
            }
        }

        foreach (uint child in _unfollowed)
        {
            _followers.Remove(child);
        }

        _unfollowed.Clear();
    }

    /// <summary>Attached children whose shadow casts through a following proxy, by address.</summary>
    private readonly Dictionary<uint, MeshInstance3D> _followers = new();

    private readonly List<uint> _unfollowed = new();

    /// <summary>
    /// The newest placement the guest STATED for an entity, as opposed to the blended one it is
    /// being drawn at. Falls back to the drawn transform for an entity with no history yet.
    /// </summary>
    private Transform3D StatedOf(uint address, Transform3D drawn) =>
        _bodies.TryGetValue(address, out Refii.Core.Engine_TickTimeline? body)
            ? body.Newest
            : drawn;

    /// <summary><see cref="PartPaths"/> per mesh, computed once.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, string[]> _partPathsByMesh = new();

    /// <summary>
    /// The model paths behind a node's mesh — every part of a composite, or the one model. The
    /// dummy an attachment names is a record in one of their `.mdl.gmd` sidecars.
    /// </summary>
    private static string[] PartPaths(MeshInstance3D node)
    {
        if (Refii.Entities.Engine_BodyVariants.SourceMesh(node) is not { } mesh)
        {
            return System.Array.Empty<string>();
        }

        if (_partPathsByMesh.TryGetValue(mesh, out string[]? known))
        {
            return known;
        }

        string[] paths = PartPathsOf(mesh);
        _partPathsByMesh.AddOrUpdate(mesh, paths);
        return paths;
    }

    private static string[] PartPathsOf(Mesh mesh) =>
        Refii.Models.Engine_MdlComposite.ModelPathsOf(mesh);

    /// <summary>
    /// A composite's displacement-only parts (GDB DisplacementMeshes,
    /// <see cref="HostBridge.GetEntityDisplacementParts"/>) under classic: the guest draws them only
    /// into the displacement target (flag 0x800, `sub_8220DE88`), so their surfaces leave the body
    /// (<see cref="Refii.Entities.Engine_HiddenClusters.SetDisplacedSurfaces"/>) and a child on the
    /// same skeleton and skin draws them on the displacement layer with their own diffuse.
    /// </summary>
    private void AttachDisplacementParts(uint address, MeshInstance3D node, Mesh mesh)
    {
        if (!Refii.Models.Engine_ClassicMaterial.Active || mesh is not ArrayMesh source ||
            node.Skin is null || node.GetNodeOrNull(node.Skeleton) is not Node skeleton ||
            !source.HasMeta(Refii.Models.Engine_MdlComposite.SurfacePartsMetaName) ||
            !source.HasMeta(Refii.Models.Engine_MdlComposite.PartHashesMetaName))
        {
            return;
        }
        uint[] displaced = HostBridge.GetEntityDisplacementParts(address);
        if (displaced.Length == 0)
        {
            return;
        }
        int[] surfaceParts = source.GetMeta(Refii.Models.Engine_MdlComposite.SurfacePartsMetaName).AsInt32Array();
        uint[] partHashes = System.Array.ConvertAll(
            source.GetMeta(Refii.Models.Engine_MdlComposite.PartHashesMetaName).AsInt64Array(), v => unchecked((uint)v));
        var subset = new ArrayMesh();
        System.UInt128 mask = System.UInt128.Zero;
        for (int s = 0; s < source.GetSurfaceCount() && s < 128 && s < surfaceParts.Length; s++)
        {
            int part = surfaceParts[s];
            if (part < 0 || part >= partHashes.Length || System.Array.IndexOf(displaced, partHashes[part]) < 0)
            {
                continue;
            }
            mask |= System.UInt128.One << s;
            int added = Refii.Models.Engine_MdlSurfaceArrays.Add(subset, Mesh.PrimitiveType.Triangles,
                Refii.Models.Engine_MdlSurfaceArrays.Of(source, s), null);
            subset.SurfaceSetMaterial(added, Refii.Models.Engine_ClassicDisplacement.MeshMaterial(source.SurfaceGetMaterial(s)));
        }
        if (mask == System.UInt128.Zero)
        {
            return;
        }
        var child = new MeshInstance3D
        {
            Name = "DisplacementParts",
            Mesh = subset,
            Skin = node.Skin,
            Layers = Refii.Models.Engine_ClassicDisplacement.Layer,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        node.AddChild(child);
        child.Skeleton = child.GetPathTo(skeleton);
        Refii.Models.Engine_ClassicDisplacement.Track(child);
        Refii.Entities.Engine_HiddenClusters.SetDisplacedSurfaces(node, mask);
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

        // The mesh has to be a CHILD of the skeleton and name it. The skin is the rig's own:
        // the model's inverse rests, each carrying the bone's live scale (the morph), which the
        // skeleton itself never holds.
        node.Skeleton = node.GetPathTo(skeleton.Node);
        node.Skin = skeleton.Skin;
        _skeletons[address] = skeleton;

        // The proxy casts on THIS skeleton and skin, or its shadow stands in the bind pose.
        Refii.Entities.Engine_ShadowProxy.Sync(node);

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
                !GodotObject.IsInstanceValid(node) || Refii.Entities.Engine_BodyVariants.SourceMesh(node) is not { } mesh)
            {
                _rigged.Add(address);  // gone, not rigged — either way stop asking
                continue;
            }

            if (TryRig(address, node, mesh))
            {
                // A character after all: no screen-size cull and no size-keyed shadow reach, only
                // the range it states (none leaves it drawn and casting at any distance).
                node.VisibilityRangeEnd = 0f;
                Refii.Level.Engine_PropDrawRange.Apply(node);
                Refii.Lighting.Engine_ShadowReach.Release(node);

                // After the release, which hands the BODY its shadow back: the proxy casts.
                Refii.Entities.Engine_ShadowProxy.Sync(node);
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

    /// <summary>
    /// Each entity's stated range as applied ({begin, margin, end, margin, fade}); absent when the
    /// guest states none usable or the entity is a principal. See <see cref="StateDrawRange"/>.
    /// </summary>
    private readonly Dictionary<uint, float[]> _drawRanges = new();

    /// <summary>
    /// THE GUEST'S DRAW DISTANCE. The renderable states D — `MaxDrawDistance` times the class
    /// multiplier — and the gathers fade the entity out to D and cut its shadow hard there
    /// (`sub_82219AB8`, `sub_8221E5F0`). Stamped on the node for the same range rule the static
    /// placements use; `entity_draw_distance_scale` widens it. A principal is never stamped: the
    /// players are drawn at any distance. False when nothing is stated.
    /// </summary>
    private bool StateDrawRange(uint address, GeometryInstance3D node)
    {
        if (IsPrincipal(address) ||
            !HostBridge.GetEntityDrawRange(address, out float distance, out float fade) ||
            Refii.Level.Engine_PropDrawRange.EntityRange(distance, fade) is not { } stated)
        {
            return false;
        }

        _drawRanges[address] = stated;
        Refii.Level.Engine_PropDrawRange.StampEntity(node, stated);
        // The same D cuts its shadow hard (`sub_8221E5F0`), from the eye.
        Refii.Models.Engine_ClassicCasters.Mark(node, CastingOf(address, node),
            Refii.Level.Engine_PropDrawRange.Reach(stated));
        Refii.Models.Engine_ClassicCasters.Register(node, dynamic: true);
        return true;
    }

    /// <summary>
    /// The cascades an entity casts into, as its definition states them (the renderable's flag
    /// word, <see cref="HostBridge.GetEntityCasterFlags"/>: 0x40 none, 0x80 not the far cascade,
    /// 0x100 both faces). Only the static-mesh descriptor carries the both-faces bit
    /// (`sub_822370A8`; the animated one, `sub_8220DE88`, never does), so a skinned body drops it.
    /// </summary>
    private static byte CastingOf(uint address, GeometryInstance3D node)
    {
        if (HostBridge.GetEntityCasterFlags(address) is not { } flags)
        {
            return Refii.Models.Engine_ClassicShadows.CastsDefault;
        }
        byte casting = Refii.Models.Engine_ClassicShadows.CastingOf(flags);
        if (node is MeshInstance3D { Skin: not null })
        {
            casting &= unchecked((byte)~Refii.Models.Engine_ClassicShadows.CastsTwoSided);
        }
        return casting;
    }

    /// <summary>
    /// Godot measures a range to the instance's box centre, the pose gate to the rig's origin; the
    /// slack keeps a body at the edge of its range posed while it can still be drawn.
    /// </summary>
    private const float ReachSlackMetres = 3f;

    /// <summary>How far from the eye this entity is drawn at all; infinity when it states no range.</summary>
    private float DrawnReach(uint address) =>
        _drawRanges.TryGetValue(address, out float[]? stated)
            ? Refii.Level.Engine_PropDrawRange.Reach(stated)
            : float.PositiveInfinity;

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

    /// <summary>Ticks after which the pass counts as settled whether or not the count held still.</summary>
    private const int SettleFallbackTicks = 600;

    private int _lastCount = -1;

    private int _settledFor;

    /// <summary>The entity list has filled at least once. Never cleared — see the reconcile.</summary>
    private bool _everSettled;

    /// <summary>Whether the twins have been reconciled at least once for this region.</summary>
    private bool _reconciledOnce;

    /// <summary>
    /// Whether every entity the guest has reported is on screen the way it will stay: every
    /// mesh built, the region's static copies reconciled against them. The world holds the
    /// guest in its loading screen until this is true, so nothing fills in after control returns.
    /// </summary>
    /// <remarks>
    /// A creation deferred by the per-frame budget (<c>_awaitingCreate</c>) is pending too: its
    /// mesh is built but it has no node yet.
    /// </remarks>
    public bool ReadyForRelease => _ticks > 1 && _awaitingBuild == 0 && _awaitingCreate == 0 && _reconciledOnce;

    /// <summary>The hero as the feed has it, for the release line: does it have a node, and is it shown.</summary>
    public string DescribeHero()
    {
        uint hero = HostBridge.HeroEntityAddress();
        if (hero == 0u)
        {
            return "hero: the guest names none";
        }

        return _nodes.TryGetValue(hero, out MeshInstance3D? node) && GodotObject.IsInstanceValid(node)
            ? $"hero {hero:X8}: node built, {(node.IsVisibleInTree() ? "visible" : "HIDDEN")}"
            : $"hero {hero:X8}: NO NODE yet";
    }

    /// <summary>What <see cref="ReadyForRelease"/> is still waiting on, for the hold's log line.</summary>
    public string ReleaseWait =>
        _ticks <= 1 ? "the first entity pass"
        : _awaitingBuild != 0 ? $"{_awaitingBuild} entity mesh build(s) ({_awaitingStub} stub(s) not counted)"
        : _awaitingCreate != 0 ? $"{_awaitingCreate} entity node(s) not yet created"
        : !_reconciledOnce ? "the first twin reconcile"
        : "nothing";

    /// <summary>Half a second at 60fps between reconciles.</summary>
    private const int ReconcileTicks = 30;

    /// <summary>
    /// A per-entity cadence: true on one walk in <paramref name="period"/>, at a phase fixed by the
    /// address. Every entity still reconciles once a period, but not all on the SAME walk, so the
    /// part reads, relights and full passes are spread across the period.
    /// </summary>
    private bool Due(uint address, int period) =>
        (uint)(_ticks + (int)((address * 2654435761u) >> 20)) % (uint)period == 0u;

    /// <summary>Scratch for the reconcile, reused so a twice-a-second pass allocates nothing.</summary>
    private readonly HashSet<uint> _liveRecords = new();

    /// <summary>The guest's deactivated-layer set, refreshed each reconcile. See the note there.</summary>
    private readonly HashSet<uint> _deactivatedLayers = new();

    /// <summary>The layer gate handed to <c>Reconcile</c>, built once. See the call site.</summary>
    private System.Func<uint, bool>? _layerIsDeactivated;

    /// <summary>Records we have a live node for THIS frame, filled as the entities are drawn.</summary>
    private readonly HashSet<uint> _drawnRecords = new();

    /// <summary>The model and place of every entity we drew this pass — see Engine_StaticTwins.HideAtPlaces.</summary>
    private readonly List<(string ModelKey, Vector3 Origin)> _drawnPlaces = new();
    private readonly List<(uint Address, string ModelKey)> _drawnKeys = new();

    /// <summary>
    /// THE MARRIED ENTITIES: the region's own copy is the drawable, driven by the guest's
    /// placement (and for a rigged model, by its dominant bone's pose), and the entity's own
    /// node stays invisible so the rig still poses. See Engine_StaticTwins.Adoption.
    /// </summary>
    private readonly Dictionary<uint, (Refii.Entities.Engine_StaticTwins.Adoption Twin, ulong Uid)> _adoptions = new();

    private readonly Dictionary<Mesh, int> _dominantBones = new();

    public int AdoptedEntities => _adoptions.Count;

    /// <summary>
    /// The bone that carries most of a skinned mesh's weight — a rigid door is one bone of
    /// its rig, and the static copy follows that bone's pose relative to its rest.
    /// </summary>
    private int DominantBone(Mesh mesh)
    {
        if (_dominantBones.TryGetValue(mesh, out int cached))
        {
            return cached;
        }

        int best = 0;
        if (mesh is ArrayMesh array && array.GetSurfaceCount() > 0)
        {
            var weights = new Dictionary<int, float>();
            for (int surface = 0; surface < array.GetSurfaceCount(); surface++)
            {
                Godot.Collections.Array arrays = Refii.Models.Engine_MdlSurfaceArrays.Of(array, surface);
                Variant bonesVariant = arrays[(int)Mesh.ArrayType.Bones];
                Variant weightsVariant = arrays[(int)Mesh.ArrayType.Weights];
                if (bonesVariant.VariantType == Variant.Type.Nil ||
                    weightsVariant.VariantType == Variant.Type.Nil)
                {
                    continue;
                }

                int[] bones = bonesVariant.AsInt32Array();
                float[] w = weightsVariant.AsFloat32Array();
                for (int i = 0; i < bones.Length && i < w.Length; i++)
                {
                    weights.TryGetValue(bones[i], out float had);
                    weights[bones[i]] = had + w[i];
                }
            }

            float top = -1f;
            foreach (KeyValuePair<int, float> pair in weights)
            {
                if (pair.Value > top)
                {
                    top = pair.Value;
                    best = pair.Key;
                }
            }
        }

        _dominantBones[mesh] = best;
        return best;
    }

    /// <summary>Where the married copy stands this tick, in world space.</summary>
    private Transform3D AdoptedWorld(uint address, MeshInstance3D node)
    {
        Transform3D world = node.GlobalTransform;
        if (_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig) &&
            Refii.Entities.Engine_BodyVariants.SourceMesh(node) is { } mesh)
        {
            Skeleton3D sk = rig.Node;
            int bone = DominantBone(mesh);
            if (bone >= 0 && bone < sk.GetBoneCount())
            {
                world *= sk.GetBoneGlobalPose(bone) * sk.GetBoneGlobalRest(bone).AffineInverse();
            }
        }

        return world;
    }
    /// <summary>
    /// The live entities whose model key contains <paramref name="needle"/> and the static
    /// twins of the same models, side by side: the join HideAtPlaces makes, laid out to read.
    /// </summary>
    public Dictionary<string, object?> DescribeTwins(string needle, bool apply = false)
    {
        string want = needle.ToLowerInvariant();
        int applied = -1;
        if (apply && Twins is { } twinsNow)
        {
            int before = twinsNow.HiddenByPlace;
            twinsNow.HideAtPlaces(_drawnPlaces);
            applied = twinsNow.HiddenByPlace - before;
        }
        var live = new List<Dictionary<string, object?>>();
        foreach ((uint address, MeshInstance3D node) in _nodes)
        {
            uint modelHash = node.HasMeta("entity_model") ? (uint)node.GetMeta("entity_model").AsInt64() : 0u;
            string? key = modelHash == 0u ? null : ModelKeyFor(modelHash);
            if (key is null || !key.Contains(want, System.StringComparison.Ordinal))
            {
                continue;
            }

            Vector3 at = node.GlobalTransform.Origin;
            live.Add(new Dictionary<string, object?>
            {
                ["address"] = $"{address:X8}",
                ["model"] = key,
                ["modelHash"] = $"{modelHash:X8}",
                ["record"] = node.HasMeta("entity_record") ? $"{(uint)node.GetMeta("entity_record").AsInt64():X8}" : null,
                ["at"] = $"{at.X:F2},{at.Y:F2},{at.Z:F2}",
                ["visible"] = node.Visible,
                ["rigged"] = _skeletons.ContainsKey(address),
                ["adopted"] = _adoptions.ContainsKey(address),
                ["seen"] = _seen.Contains(address),
            });
        }

        int drawnKeys = 0;
        foreach ((string key, Vector3 _) in _drawnPlaces)
        {
            if (key.Contains(want, System.StringComparison.Ordinal))
            {
                ++drawnKeys;
            }
        }

        return new Dictionary<string, object?>
        {
            ["live"] = live,
            ["drawnPlaces"] = drawnKeys,
            ["drawnTotal"] = _drawnPlaces.Count,
            ["applied"] = applied,
            ["ticks"] = _ticks,
            ["everSettled"] = _everSettled,
            ["twins"] = Twins?.DescribeByModel(want, _drawnPlaces),
        };
    }

    /// <summary>
    /// Every drawn entity with a drawn static twin within <paramref name="radius"/>, any model:
    /// the complete list of doubles on screen, with the flags that say which path missed.
    /// </summary>
    public Dictionary<string, object?> DescribeDoubles(float radius)
    {
        var pairs = new List<Dictionary<string, object?>>();
        var unresolved = new List<Dictionary<string, object?>>();
        if (Twins is not { } twins)
        {
            return new Dictionary<string, object?> { ["error"] = "no twins" };
        }

        var grid = new Dictionary<(int, int, int), List<int>>();
        List<(string Model, Vector3 At, uint Record, bool Adopted, string Node)> drawn = twins.DrawnTwins();
        for (int i = 0; i < drawn.Count; i++)
        {
            (int, int, int) cell = Cell(drawn[i].At);
            if (!grid.TryGetValue(cell, out List<int>? list))
            {
                list = new List<int>();
                grid[cell] = list;
            }

            list.Add(i);
        }

        float limit = radius * radius;
        foreach ((uint address, MeshInstance3D node) in _nodes)
        {
            if (!node.Visible || !node.IsInsideTree())
            {
                continue;
            }

            uint modelHash = node.HasMeta("entity_model") ? (uint)node.GetMeta("entity_model").AsInt64() : 0u;
            uint record = node.HasMeta("entity_record") ? (uint)node.GetMeta("entity_record").AsInt64() : 0u;
            string? key = modelHash == 0u ? null : ModelKeyFor(modelHash);
            Vector3 at = node.GlobalTransform.Origin;
            if (key is null)
            {
                unresolved.Add(new Dictionary<string, object?>
                {
                    ["address"] = $"{address:X8}",
                    ["modelHash"] = $"{modelHash:X8}",
                    ["record"] = $"{record:X8}",
                    ["at"] = $"{at.X:F2},{at.Y:F2},{at.Z:F2}",
                });
            }

            (int cx, int cy, int cz) = Cell(at);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out List<int>? list))
                        {
                            continue;
                        }

                        foreach (int i in list)
                        {
                            float d2 = drawn[i].At.DistanceSquaredTo(at);
                            if (d2 > limit)
                            {
                                continue;
                            }

                            pairs.Add(new Dictionary<string, object?>
                            {
                                ["address"] = $"{address:X8}",
                                ["entityModel"] = key,
                                ["entityRecord"] = $"{record:X8}",
                                ["rigged"] = _skeletons.ContainsKey(address),
                                ["adopted"] = _adoptions.ContainsKey(address),
                                ["twinModel"] = drawn[i].Model,
                                ["twinNode"] = drawn[i].Node,
                                ["twinRecord"] = drawn[i].Record == 0u ? null : $"{drawn[i].Record:X8}",
                                ["twinAdopted"] = drawn[i].Adopted,
                                ["distance"] = $"{Mathf.Sqrt(d2):F3}",
                                ["at"] = $"{at.X:F2},{at.Y:F2},{at.Z:F2}",
                            });
                        }
                    }
                }
            }
        }

        // OWN NODES STANDING IN FOR A RECORD: the entity draws through the entity lighting path
        // (no vertex bake) while its record copy is hidden. Every one of these is a surface that
        // lost its authored bake; a rock among them is a rock gone black in a cave.
        var ownNodes = new List<Dictionary<string, object?>>();
        foreach ((uint address, MeshInstance3D node) in _nodes)
        {
            if (!node.Visible || _adoptions.ContainsKey(address))
            {
                continue;
            }

            uint record = node.HasMeta("entity_record") ? (uint)node.GetMeta("entity_record").AsInt64() : 0u;
            if (record == 0u || !twins.HasRecord(record))
            {
                continue;
            }

            uint modelHash = node.HasMeta("entity_model") ? (uint)node.GetMeta("entity_model").AsInt64() : 0u;
            Vector3 at = node.GlobalTransform.Origin;
            ownNodes.Add(new Dictionary<string, object?>
            {
                ["address"] = $"{address:X8}",
                ["record"] = $"{record:X8}",
                ["entityModel"] = modelHash == 0u ? null : ModelKeyFor(modelHash),
                ["twinModels"] = twins.ModelsOfRecord(record),
                ["rigged"] = _skeletons.ContainsKey(address),
                ["at"] = $"{at.X:F2},{at.Y:F2},{at.Z:F2}",
            });
        }

        return new Dictionary<string, object?>
        {
            ["ticks"] = _ticks,
            ["drawnEntities"] = _nodes.Count,
            ["drawnTwins"] = drawn.Count,
            ["pairs"] = pairs,
            ["unresolvedModel"] = unresolved,
            ["ownNodesWithRecordTwin"] = ownNodes,
        };
    }

    private static (int, int, int) Cell(Vector3 at) =>
        (Mathf.FloorToInt(at.X), Mathf.FloorToInt(at.Y), Mathf.FloorToInt(at.Z));

    private readonly Dictionary<uint, string?> _modelKeys = new();

    private string? ModelKeyFor(uint modelHash)
    {
        if (_modelKeys.TryGetValue(modelHash, out string? cached))
        {
            return cached;
        }

        string? path = Refii.Level.Engine_PropModelIndex.Resolve(modelHash, System.Array.Empty<string>());
        string? key = path is null ? null : Refii.Entities.Engine_StaticTwins.ModelKey(path);
        _modelKeys[modelHash] = key;
        return key;
    }

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
            Godot.Collections.Array arrays = Refii.Models.Engine_MdlSurfaceArrays.Of(array, s);
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

    private uint _playerTwoReported;

    private void ReportRig(Mesh mesh, Refii.Entities.Engine_EntitySkeleton skeleton)
    {
        if (_rigReports >= RigReportLimit || mesh is not ArrayMesh array || array.GetSurfaceCount() == 0)
        {
            return;
        }

        Godot.Collections.Array surface = Refii.Models.Engine_MdlSurfaceArrays.Of(array, 0);
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

        // A dictionary cannot be edited while it is walked, so the addresses are collected
        // first, into a reused list: this can run every frame.
        _gone.Clear();
        foreach (KeyValuePair<uint, MeshInstance3D> pair in _nodes)
        {
            if (!_seen.Contains(pair.Key))
            {
                _gone.Add(pair.Key);
            }
        }

        foreach (uint address in _gone)
        {
            if (_nodes.TryGetValue(address, out MeshInstance3D? node) &&
                GodotObject.IsInstanceValid(node))
            {
                Refii.Entities.Engine_EntityAppearance.Forget(node);
                node.QueueFree();
            }

            _nodes.Remove(address);
            _batches?.Demote(address);
            _still.Remove(address);
            if (_adoptions.TryGetValue(address, out var married))
            {
                // Destroyed by the guest (a key taken, a door broken): the copy goes with it.
                // Merely unreported (streamed out, walk truncated): it stays where it stood.
                bool destroyed = false;
                foreach (ulong dead in HostBridge.GetDeadEntities())
                {
                    if (dead == married.Uid)
                    {
                        destroyed = true;
                        break;
                    }
                }
                Twins?.Release(married.Twin, destroyed);
                _adoptions.Remove(address);
            }

            // The rig goes with the node it hung under; leaving the entry behind would keep
            // a freed skeleton reachable and pose it every frame.
            _skeletons.Remove(address);
            _rigSeenMs.Remove(address);
            _boneWriteAskedMs.Remove(address);
            _lastLit.Remove(address);
            _eyes.Remove(address);
            _bodyAndEyes.Remove(address);
            _clipAnimators.Remove(address);
            _bodies.Remove(address);
            _movers.Remove(address);
            _written.Remove(address);
            _compositeParts.Remove(address);
            _partMorphs.Remove(address);
            _textureLayers.Remove(address);
            _meshParts.Remove(address);
            _nodeMesh.Remove(address);
            _relit.Remove(address);
            _attachments.Forget(address);

            // And the retry, which would otherwise keep asking the guest about an entity it
            // no longer has — an address the guest has reused answers for something else.
            _unrigged.Remove(address);
            _drawRanges.Remove(address);
        }
    }

    /// <summary>Scratch for <see cref="Retire"/> — reused, for the reason stated there.</summary>
    private readonly List<uint> _gone = new();

    private int _withModel;

    private int _noMesh;

    /// <summary>Entities waiting on a build that is running off this thread. Not a fault.</summary>
    private int _awaitingBuild;

    /// <summary>Entities whose model keeps coming back as a stub; not pending for the release.</summary>
    private int _awaitingStub;

    /// <summary>Entities with a built mesh still waiting for their node because the frame's creation budget was spent.</summary>
    private int _awaitingCreate;

    /// <summary>How many times each model's build has returned an empty stub.</summary>
    private readonly Dictionary<uint, int> _stubReturns = new();

    private const int StubGiveUp = 3;

    private const int StubRetryTicks = 300;

    /// <summary>
    /// Applies the region's current lighting to one live entity.
    ///
    /// Re-applied whenever the mesh changes, and re-applied for every entity when the region's
    /// time of day moves — the environment node's own refresh walks the region and these are
    /// not in it.
    /// </summary>
    private static void LightEntity(GeometryInstance3D node)
    {
        Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
            node, Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting);

        // The cloth draws beside its body and is lit as its body is.
        if (Refii.Physics.Engine_ClothBody.Of(node) is { } cloth)
        {
            Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
                cloth, Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting);
        }
    }

    /// <summary>Probe-lit at the entity's current place: the region root is the sky node's
    /// parent, and the probe field is indexed in the region's own frame.</summary>
    private static void RelightEntity(Node3D node)
    {
        Refii.Level.Engine_SkyEnvironmentNode? sky = Refii.Level.Engine_SkyEnvironmentNode.Current;
        if (sky is null || sky.GetParent() is not Node3D root || node is not GeometryInstance3D geometry)
        {
            return;
        }

        Vector3 local = root.GlobalTransform.AffineInverse() * node.GlobalPosition;
        Refii.Lighting.Engine_LevelBakedLighting.LightEntityAt(geometry, local, sky.Lighting);
        if (Refii.Physics.Engine_ClothBody.Of(node) is { } cloth)
        {
            Refii.Lighting.Engine_LevelBakedLighting.LightEntityAt(cloth, local, sky.Lighting);
        }
    }

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
    /// <summary>
    /// Component 7's eye models for a SINGLE-MODEL entity, or empty. The bridge answers the
    /// eyes alone when there are no morph parts. Re-read on the reconcile cadence: eyes change
    /// with an appearance rebuild, not per frame.
    /// </summary>
    private uint[] EyesOf(uint address)
    {
        if (!Due(address, ReconcileTicks) && _eyes.TryGetValue(address, out uint[]? known))
        {
            return known;
        }

        uint[] eyes = HostBridge.GetEntityMorphParts(address, out _);
        _eyes[address] = eyes;
        return eyes;
    }

    private readonly Dictionary<uint, uint[]> _eyes = new();

    /// <summary>
    /// The body hash followed by <see cref="EyesOf"/>'s, as one part list.
    /// </summary>
    /// <remarks>
    /// Cached per entity to avoid a per-frame allocation. `EyesOf` hands back the same instance
    /// between reconciles, so eye-array identity plus the model hash is the whole change test.
    /// </remarks>
    private uint[] BodyAndEyes(uint address, uint modelHash, uint[] eyes)
    {
        if (_bodyAndEyes.TryGetValue(address, out (uint Model, uint[] Eyes, uint[] Parts) known) &&
            known.Model == modelHash && ReferenceEquals(known.Eyes, eyes))
        {
            return known.Parts;
        }

        var parts = new uint[eyes.Length + 1];
        parts[0] = modelHash;
        eyes.CopyTo(parts, 1);
        _bodyAndEyes[address] = (modelHash, eyes, parts);
        return parts;
    }

    private readonly Dictionary<uint, (uint Model, uint[] Eyes, uint[] Parts)> _bodyAndEyes = new();

    private Mesh? ResolveComposite(uint address)
    {
        // The part list is re-read on the reconcile cadence, as the eyes are.
        uint[] parts;
        int built = 0;
        if (!Due(address, ReconcileTicks) && _compositeParts.TryGetValue(address, out uint[]? knownParts))
        {
            parts = knownParts;
        }
        else
        {
            parts = HostBridge.GetEntityMorphParts(address, out built);
            _compositeParts[address] = parts;
        }

        if (parts.Length == 0)
        {
            return null;
        }

        _builtParts += built;
        return ComposeParts(parts, TextureLayersOf(address), PartMorphsOf(address));
    }

    /// <summary>
    /// The entity's morphed parts as the guest stated them (<c>sub_82A48B90</c>), re-read on the
    /// reconcile cadence; the same array is handed back until the statement moves.
    /// </summary>
    private HostBridge.GuestPartMorph[] PartMorphsOf(uint address)
    {
        if (!Due(address, ReconcileTicks) &&
            _partMorphs.TryGetValue(address, out (uint Generation, HostBridge.GuestPartMorph[] Parts) known))
        {
            return known.Parts;
        }

        HostBridge.GuestPartMorph[] parts = HostBridge.GetEntityPartMorphs(address, out uint generation);
        if (_partMorphs.TryGetValue(address, out known) && known.Generation == generation &&
            known.Parts.Length == parts.Length)
        {
            return known.Parts;
        }

        _partMorphs[address] = (generation, parts);
        return parts;
    }

    private readonly Dictionary<uint, (uint Generation, HostBridge.GuestPartMorph[] Parts)> _partMorphs = new();

    /// <summary>
    /// The texture composites the guest states for the entity (<c>sub_825B38B0</c>), re-read on
    /// the reconcile cadence; the same array is handed back until the statement moves.
    /// </summary>
    private HostBridge.GuestTextureComposite[] TextureLayersOf(uint address)
    {
        if (!Due(address, ReconcileTicks) &&
            _textureLayers.TryGetValue(address, out (uint Generation, HostBridge.GuestTextureComposite[] Composites) known))
        {
            return known.Composites;
        }

        HostBridge.GuestTextureComposite[] composites =
            HostBridge.GetEntityTextureLayers(address, out uint generation);
        if (_textureLayers.TryGetValue(address, out known) && known.Generation == generation &&
            known.Composites.Length == composites.Length)
        {
            return known.Composites;
        }

        _textureLayers[address] = (generation, composites);
        return composites;
    }

    private readonly Dictionary<uint, (uint Generation, HostBridge.GuestTextureComposite[] Composites)> _textureLayers = new();

    /// <summary>Composites built with at least one morphed part; run total.</summary>
    private int _compositesMorphed;

    /// <summary>
    /// The assembled mesh for a multi-mesh appearance component (id 5), or null.
    /// </summary>
    /// <remarks>
    /// Same assembly as a composite character and deliberately so — both are "N models drawn as
    /// one entity", and the cache keys on the part list, so a street of identical townhouses
    /// shares a single built mesh. What differs is only where the hashes come from.
    /// </remarks>
    private Mesh? ResolveMultiMesh(uint address)
    {
        uint[] parts;
        int total = 0;
        if (!Due(address, ReconcileTicks) && _meshParts.TryGetValue(address, out uint[]? knownParts))
        {
            parts = knownParts;
        }
        else
        {
            parts = HostBridge.GetEntityMeshParts(address, out total);
            _meshParts[address] = parts;
        }

        if (parts.Length == 0)
        {
            return null;
        }

        _multiMeshParts += total;
        return ComposeParts(parts);
    }

    /// <summary>Morph part lists per entity, re-read on the reconcile cadence.</summary>
    private readonly Dictionary<uint, uint[]> _compositeParts = new();

    /// <summary>Entities with no model, no composite parts and no mesh records, by unique id. See the walk.</summary>
    private readonly Dictionary<uint, ulong> _nothingToDraw = new();

    /// <summary>Prologues answered from <see cref="_nothingToDraw"/> this walk.</summary>
    private int _prologueSkipped;

    /// <summary>Entities the loop left alone because their statement had not moved; run total.</summary>
    public static long UnchangedSkipped => _unchangedSkippedTotal;

    private int _unchangedSkipped;
    private static long _unchangedSkippedTotal;

    /// <summary>The mesh this feed last assigned to each entity's node.</summary>
    private readonly Dictionary<uint, Mesh?> _nodeMesh = new();

    /// <summary>Never fewer than this many a frame, however slow they turn out to be.</summary>
    private const int MinCreationsPerFrame = 8;

    /// <summary>And never more, however cheap: a crowd arriving together is still a crowd.</summary>
    private const int MaxCreationsPerFrame = 128;

    /// <summary>How long a frame may spend building new entity nodes.</summary>
    private const double CreateMillisecondsPerFrame = 2.0;

    /// <summary>The same two limits while the world holds the guest behind its loading screen.</summary>
    private const int MaxCreationsPerLoadFrame = 2048;

    private const double CreateMillisecondsPerLoadFrame = 12.0;

    /// <summary>Set by the world while it holds the guest for the region build.</summary>
    public bool LoadHold { get; set; }

    /// <summary>The batches standing for still props; made on first use, a child of this feed.</summary>
    private Refii.Entities.Engine_EntityBatches? _batches;

    /// <summary>Consecutive walks each unrigged prop has gone unchanged.</summary>
    private readonly Dictionary<uint, int> _still = new();

    /// <summary>
    /// One more unchanged walk for a prop; at <see cref="Refii.Entities.Engine_EntityBatches.StillWalks"/>
    /// it is drawn from a batch and its node hidden. Only what is nothing but a prop: no rig, not
    /// married to a static twin (that node is hidden already), not highlighted, and only under the
    /// unified lighting model.
    /// </summary>
    private void NoteStill(HostBridge.GuestEntity entity, MeshInstance3D node, Mesh mesh, bool adopted)
    {
        // A target's silhouette is drawn from its node, and this branch is where an unchanged
        // prop that has just been targeted is seen.
        if (entity.Highlighted || !entity.Appearance.IsConstructed)
        {
            Unbatch(entity.Address, node);
            return;
        }

        if (adopted || !Refii.Entities.Engine_EntityBatches.Enabled ||
            !Refii.Lighting.Engine_LightingFrame.ModernModel || !_unrigged.Contains(entity.Address) ||
            mesh is not ArrayMesh shape || (_batches?.Contains(entity.Address) ?? false))
        {
            return;
        }

        int still = _still.GetValueOrDefault(entity.Address) + 1;
        _still[entity.Address] = still;
        if (still < Refii.Entities.Engine_EntityBatches.StillWalks ||
            !_written.TryGetValue(entity.Address, out (MeshInstance3D Node, Transform3D Drawn) placed) ||
            !ReferenceEquals(placed.Node, node))
        {
            return;
        }

        if (_batches is null)
        {
            _batches = new Refii.Entities.Engine_EntityBatches { Name = Refii.Entities.Engine_EntityBatches.NodeName };
            AddChild(_batches);
        }

        uint layers = Refii.Lighting.Engine_LevelLights.EntityLayersOf(entity.RecordHash);
        if (_batches.Promote(entity.Address, shape, layers, placed.Drawn,
            _drawRanges.GetValueOrDefault(entity.Address)))
        {
            node.Visible = false;
        }
    }

    /// <summary>Back to a node, if it was batched; the still count starts again either way.</summary>
    private void Unbatch(uint address, MeshInstance3D node)
    {
        _still.Remove(address);
        if (_batches is not null && _batches.Demote(address) && GodotObject.IsInstanceValid(node))
        {
            node.Visible = true;
        }
    }

    private void UnbatchAll()
    {
        foreach (KeyValuePair<uint, MeshInstance3D> pair in _nodes)
        {
            Unbatch(pair.Key, pair.Value);
        }
    }

    /// <summary>
    /// The players' heroes as the guest states them: the hero the vitals resolve, and keys 1 and 2
    /// of its player map. A PRINCIPAL is never given less for being far from the eye — full pose
    /// rate, re-posed every drawn frame, posed when off screen — and both players are principals
    /// alike: the co-op partner is as much the centre of attention as the local hero.
    /// </summary>
    private (uint Hero, uint One, uint Two) _principals;

    private bool IsPrincipal(uint address) =>
        address != 0u && (address == _principals.Hero || address == _principals.One || address == _principals.Two);

    private int _createdThisFrame;

    private long _createFrom;
    private static long _creationsDeferred;

    /// <summary>Entities that waited a frame for their node because the frame's creations were spent; run total.</summary>
    public static long CreationsDeferred => _creationsDeferred;

    private static readonly StringName MetaEntityAddress = "entity_address";
    private static readonly StringName MetaEntityUniqueId = "entity_unique_id";
    private static readonly StringName MetaEntityRecord = "entity_record";
    private static readonly StringName MetaEntityModel = "entity_model";

    /// <summary>Entities lit from the probe field at least once; the rest is the reconcile cadence.</summary>
    private readonly HashSet<uint> _relit = new();
    private readonly Dictionary<uint, uint[]> _meshParts = new();

    /// <summary>Built meshes by the part list's FNV-1a hash, so the path resolve and join run once per list.</summary>
    private readonly Dictionary<ulong, Mesh?> _composedBySignature = new();

    /// <summary>Resolves part hashes to paths and returns the cached assembly, queueing it once.</summary>
    private Mesh? ComposeParts(
        uint[] parts, HostBridge.GuestTextureComposite[]? layers = null,
        HostBridge.GuestPartMorph[]? stated = null)
    {
        ulong signature = 14695981039346656037UL;
        foreach (uint part in parts)
        {
            signature = (signature ^ part) * 1099511628211UL;
        }

        ulong statedHash = 14695981039346656037UL;
        if (stated is not null)
        {
            foreach (HostBridge.GuestPartMorph morph in stated)
            {
                statedHash = (statedHash ^ morph.BaseModel) * 1099511628211UL;
                foreach ((uint model, float weight) in morph.Targets)
                {
                    statedHash = (statedHash ^ model) * 1099511628211UL;
                    statedHash = (statedHash ^ System.BitConverter.SingleToUInt32Bits(weight)) * 1099511628211UL;
                }
            }

            signature = (signature ^ statedHash) * 1099511628211UL;
        }

        ulong layerHash = 14695981039346656037UL;
        if (layers is not null)
        {
            foreach (HostBridge.GuestTextureComposite composite in layers)
            {
                layerHash = (layerHash ^ composite.Original ^ (composite.Overlay ? 1u : 0u) ^ (composite.Wildcard ? 2u : 0u)) * 1099511628211UL;
                foreach (HostBridge.GuestTextureLayer layer in composite.Layers)
                {
                    layerHash = (layerHash ^ layer.Texture) * 1099511628211UL;
                    layerHash = (layerHash ^ (uint)layer.Mode) * 1099511628211UL;
                    layerHash = (layerHash ^ System.BitConverter.SingleToUInt32Bits(layer.Weight)) * 1099511628211UL;
                    layerHash = (layerHash ^ (layer.HasTint ? layer.Tint : 0u)) * 1099511628211UL;
                    layerHash = (layerHash ^ System.BitConverter.SingleToUInt32Bits(layer.Brightness)) * 1099511628211UL;
                    layerHash = (layerHash ^ layer.Bump) * 1099511628211UL;
                }
            }

            signature = (signature ^ layerHash) * 1099511628211UL;
        }

        if (_composedBySignature.TryGetValue(signature, out Mesh? known))
        {
            return known;
        }

        var paths = new List<string>(parts.Length);
        var hashes = new List<long>(parts.Length);
        foreach (uint part in parts)
        {
            if (Refii.Level.Engine_PropModelIndex.Resolve(
                    part, System.Array.Empty<string>()) is string path)
            {
                paths.Add(path);
                hashes.Add(part);
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

        var key = new System.Text.StringBuilder(string.Join('|', paths));
        if (layers is not null && layers.Length != 0)
        {
            key.Append("|t").Append(layerHash.ToString("x16"));
        }

        Dictionary<uint, List<(string Path, float Weight)>>? targets = null;
        if (stated is not null && stated.Length != 0)
        {
            key.Append("|s").Append(statedHash.ToString("x16"));
            targets = new Dictionary<uint, List<(string Path, float Weight)>>();
            foreach (HostBridge.GuestPartMorph morph in stated)
            {
                var listed = new List<(string Path, float Weight)>(morph.Targets.Length);
                foreach ((uint model, float weight) in morph.Targets)
                {
                    if (Refii.Level.Engine_PropModelIndex.Resolve(
                            model, System.Array.Empty<string>()) is string target)
                    {
                        listed.Add((target, weight));
                    }
                    else
                    {
                        ++_compositePartsUnresolved;
                    }
                }

                targets[morph.BaseModel] = listed;
            }
        }

        string composedKey = key.ToString();
        if (_composed.TryGetValue(composedKey, out Mesh? cached))
        {
            _composedBySignature[signature] = cached;
            return cached;
        }

        // Not built yet. Queue it and draw nothing this frame; the completion stores it and the
        // next frame is a cache hit.
        ++_awaitingBuild;
        QueueCompositeBuild(composedKey, paths, hashes, layers, targets);
        return null;
    }

    /// <summary>
    /// One part, blended with the targets the guest listed for it (<c>sub_82A48B90</c>).
    /// </summary>
    private static Mesh? LoadPart(string path, List<(string Path, float Weight)>? stated)
    {
        Mesh? part = ResourceLoader.Load<Mesh>(
            Refii.Models.Engine_BnkMeshLoader.PathPrefix + path, "Mesh");

        if (stated is null || part is not ArrayMesh own)
        {
            return part;
        }

        var listed = new List<(ArrayMesh Target, float Weight)>(stated.Count);
        foreach ((string target, float weight) in stated)
        {
            if (ResourceLoader.Load<Mesh>(
                    Refii.Models.Engine_BnkMeshLoader.PathPrefix + target, "Mesh") is ArrayMesh mesh)
            {
                listed.Add((mesh, weight));
            }
        }

        return Refii.Hero.Engine_HeroMorph.Blend(own, listed);
    }

    /// <summary>Builds one composite off this thread, once.</summary>
    private void QueueCompositeBuild(
        string key, List<string> paths, List<long> hashes,
        HostBridge.GuestTextureComposite[]? layers,
        Dictionary<uint, List<(string Path, float Weight)>>? targets)
    {
        // Already building, or the pipe is full and this retries next frame. NOT added to the
        // job set in the full case: leaving it out is what makes the retry possible.
        if (_compositeJobs.Contains(key) || _inFlight >= MaxInFlightBuilds)
        {
            return;
        }

        _compositeJobs.Add(key);
        ++_inFlight;

        System.Threading.Tasks.Task.Run(() =>
        {
            ArrayMesh? assembled = null;
            int resolved = 0;
            int missing = 0;
            string? error = null;

            try
            {
                assembled = Refii.Models.Engine_MdlComposite.Build(
                    paths,
                    path =>
                    {
                        int at = paths.IndexOf(path);
                        List<(string Path, float Weight)>? stated = null;
                        if (targets is not null && at >= 0)
                        {
                            targets.TryGetValue((uint)hashes[at], out stated);
                        }

                        return LoadPart(path, stated);
                    },
                    out resolved,
                    out missing);

                if (assembled is not null && layers is not null && layers.Length != 0)
                {
                    Refii.Hero.Engine_HeroTextureMorph.ApplyTo(assembled, layers);
                }
            }
            catch (System.Exception e)
            {
                // CARRIED BACK, never left to the Task. An exception here does not stop the game:
                // it is stored in the Task and discarded unobserved, so the character would simply
                // never appear with nothing in the log to say why.
                // With where it was thrown: the message alone has named a fault and not its site.
                string[] trace = (e.StackTrace ?? string.Empty).Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
                error = $"{e.GetType().Name}: {e.Message}" +
                    (trace.Length != 0 ? $" {trace[0].Trim()}" : string.Empty);
            }

            _completions.Enqueue(() =>
            {
                _compositeJobs.Remove(key);
                --_inFlight;

                if (error is not null)
                {
                    GD.PushWarning($"[game-entities] composite build failed ({error}): {key}");

                    // A THROWN build is not an answer about the part list — the one seen was two
                    // workers racing on a shared table — so it is asked again, a bounded number of
                    // times; only then is the null cached like a list that does not assemble.
                    int failures = _compositeFailures.GetValueOrDefault(key) + 1;
                    _compositeFailures[key] = failures;
                    if (failures < CompositeBuildRetries)
                    {
                        return;
                    }
                }

                // Cached even when null, exactly as the synchronous path did: a part list whose
                // models do not assemble will not assemble on the next frame either.
                // The part hashes, aligned with the paths the assembly walked, so a surface's
                // part index resolves to the guest's built record (Engine_HiddenClusters).
                assembled?.SetMeta(Refii.Models.Engine_MdlComposite.PartHashesMeta, hashes.ToArray());
                _composed[key] = assembled;
                if (targets is not null)
                {
                    ++_compositesMorphed;
                }

                _compositePartsDrawn += resolved;
                _compositePartsMissing += missing;
            });
        });
    }

    /// <summary>Thrown composite builds per part list, and how many are asked again before the null is cached.</summary>
    private readonly Dictionary<string, int> _compositeFailures = new();

    private const int CompositeBuildRetries = 3;

    /// <summary>Assembled composite meshes, keyed by their part list — a crowd shares parts.</summary>
    private readonly Dictionary<string, Mesh?> _composed = new();

    private int _composites;

    /// <summary>Entities drawn this tick from a multi-mesh appearance component (id 5).</summary>
    private int _multiMesh;

    /// <summary>Mesh records those components stated this tick.</summary>
    private int _multiMeshParts;

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

        // The index lookup stays here: it is a dictionary hit, and a hash that names no path is
        // an answer, not work. Cached as null so it is asked once.
        if (Refii.Level.Engine_PropModelIndex.Resolve(
                modelHash, System.Array.Empty<string>()) is not string path)
        {
            _meshes[modelHash] = null;
            return null;
        }

        QueueMeshBuild(modelHash, path);
        return null;
    }

    /// <summary>Loads one model off this thread, once.</summary>
    private void QueueMeshBuild(uint modelHash, string path)
    {
        if (_meshJobs.Contains(modelHash) || _inFlight >= MaxInFlightBuilds)
        {
            return;
        }

        _meshJobs.Add(modelHash);
        ++_inFlight;

        System.Threading.Tasks.Task.Run(() =>
        {
            Mesh? mesh = null;
            string? error = null;

            try
            {
                mesh = ResourceLoader.Load<Mesh>(
                    Refii.Models.Engine_BnkMeshLoader.PathPrefix + path, "Mesh");
            }
            catch (System.Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
            }

            _completions.Enqueue(() =>
            {
                _meshJobs.Remove(modelHash);
                --_inFlight;

                if (error is not null)
                {
                    GD.PushWarning($"[game-entities] mesh load failed ({error}): {path}");
                    // A failed load is an answer: recorded so the entity is not re-queued every
                    // tick for the rest of the region, and so the release gate sees it settled.
                    _meshes[modelHash] = null;
                    return;
                }

                // A MESH WITH NO SURFACES IS NOT A MESH, and it is not null either.
                //
                // `Engine_BnkMeshLoader` hands back an empty `ArrayMesh` when a model is a stub —
                // a header whose body is in no mounted bank — because a loader that returns an
                // error makes Godot log a resource failure for something that is expected mid
                // level-change. The cost was paid here: the null check at the call site passed, a
                // node was created, and `Twins.Claim` hid the region's own copy for an entity
                // that draws nothing.
                //
                // NOT CACHED as a failure, and so not marked done either: the body pages in from
                // a streaming bank, so a later frame's ask can legitimately succeed. Only a
                // resolved, surfaced mesh is kept.
                if (mesh is not null && mesh.GetSurfaceCount() == 0)
                {
                    // Counted, so a stub that keeps coming back stops holding the level's
                    // release and re-asks on a slow cadence instead of every tick.
                    _stubReturns[modelHash] = _stubReturns.TryGetValue(modelHash, out int seen) ? seen + 1 : 1;
                    return;
                }

                _meshes[modelHash] = mesh;
            });
        });
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
    private void StateVisibleExtent(uint address, MeshInstance3D node, Refii.Entities.Engine_EntitySkeleton rig)
    {
        // Not every frame. The box only has to bound the pose, not track it, and re-stating it
        // per character per frame is work the renderer does not need.
        if (!Due(address, ExtentRestateTicks) || Refii.Entities.Engine_BodyVariants.SourceMesh(node) is not { } mesh)
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

        // HOLD THE BOX WHILE IT STILL BOUNDS THE POSE. Godot queues a full instance update on
        // every custom-AABB write, changed or not, and on a caster that marks every paired
        // light's shadow dirty — so a restate on the proxy re-rendered every lantern map the
        // character stood in; spread over a crowd, that is a restate in every lamp's pool on
        // nearly every display frame. The recomputed box
        // differs by millimetres from the last one, so it is written only when the pose has
        // grown out of the stated box or shrunk well inside it; the stated box carries a
        // hysteresis margin so the next restates fit. A larger culling box costs nothing seen.
        Aabb held = node.CustomAabb;
        if (held.Size.Length() > 1e-3f && held.Encloses(box) && box.Volume >= held.Volume * ExtentShrinkAt)
        {
            ++_extentHeld;
            return;
        }

        Aabb stated = box.Grow(ExtentHysteresis);
        ++_extentStated;
        Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.EntityExtent);
        node.CustomAabb = stated;

        // The proxy too: without a stated box Godot rebuilds its bounds from the bones on every
        // guest-rate write and re-pairs it against every light it moves across.
        if (Refii.Entities.Engine_ShadowProxy.ProxyOf(node) is { } proxy)
        {
            Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.EntityExtent);
            proxy.CustomAabb = stated;
        }
    }

    /// <summary>Restates answered by the box already stated; see StateVisibleExtent.</summary>
    private int _extentHeld;

    /// <summary>Metres the stated box is grown past the pose, so a walk cycle stays inside it.</summary>
    private const float ExtentHysteresis = 0.15f;

    /// <summary>A pose box under this share of the stated box's volume restates it smaller.</summary>
    private const float ExtentShrinkAt = 0.5f;

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
        bool composite = Refii.Entities.Engine_BodyVariants.SourceMesh(node) is not ArrayMesh named || named.ResourceName.Length == 0;
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



    /// <summary>The transform last written to each entity's node, with the node it went to.</summary>
    private readonly Dictionary<uint, (MeshInstance3D Node, Transform3D Drawn)> _written = new();

    /// <summary>
    /// Writes a node's transform only when it differs (IsEqualApprox) from the last one written
    /// to that same node. A Node3D transform set crosses into Godot and re-marks the subtree
    /// dirty whether or not the value changed, and nearly every entity is still. True when
    /// written.
    /// </summary>
    private bool Place(uint address, MeshInstance3D node, Transform3D drawn)
    {
        if (_written.TryGetValue(address, out (MeshInstance3D Node, Transform3D Drawn) last) &&
            ReferenceEquals(last.Node, node) && last.Drawn.IsEqualApprox(drawn))
        {
            return false;
        }

        Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.EntityTransform);
        node.Transform = drawn;
        _written[address] = (node, drawn);
        // The body's shadow proxy stands beside it and follows at the guest's rate.
        Refii.Entities.Engine_ShadowSkeletons.Placed(node);
        return true;
    }

    /// <summary>The hero's newest stated position in Godot space; null before the first walk.</summary>
    public static Vector3? HeroPosition { get; private set; }

    /// <summary>The same position in the GUEST's own space, for readers that reproduce the guest's
    /// arithmetic on it — the world map's minimap among them.</summary>
    public static Vector3? HeroGuestPosition { get; private set; }

    /// <summary>The hero's stated rotation in the guest's space, beside the position.</summary>
    public static Quaternion? HeroGuestRotation { get; private set; }

    /// <summary>Every walked entity's stated position in the guest's space, by address, from the
    /// last walk — for the readers that plot an entity the guest names (the minimap's dog and
    /// quest markers).</summary>
    private static readonly Dictionary<uint, Vector3> _guestPositions = new();

    public static bool TryGuestPosition(uint address, out Vector3 position) =>
        _guestPositions.TryGetValue(address, out position);

    /// <summary>The guest's sim tick as of this frame's walk, read once.</summary>
    private uint _tickNow;

    /// <summary>
    /// Entities whose placement is still being interpolated, and so must be re-drawn on a frame
    /// the feed skips. An address joins when a new stated pair arrives and leaves once the drawn
    /// transform has reached the newest.
    /// </summary>
    private readonly HashSet<uint> _movers = new();

    /// <summary>Scratch for the movers that arrived this frame; cleared on every use.</summary>
    private readonly List<uint> _arrived = new();

    /// <summary>Rigs the guest's pose feed drove this tick, and so must keep blending.</summary>
    private readonly HashSet<uint> _reblending = new();

    /// <summary>Rigs the clip animator drove this tick, and so must keep advancing.</summary>
    private readonly HashSet<uint> _animating = new();

    /// <summary>
    /// Advances animation on a frame the feed skips.
    /// </summary>
    /// <remarks>
    /// BOTH BRANCHES STEP WITHOUT THIS. The pose feed's blend and the clip animator's advance
    /// are both driven by WALL TIME, so each is built to run every frame; trapped inside the
    /// tick gate they ran once per tick and the characters moved in 30 Hz jumps under a camera
    /// gliding at display rate. The clip animator takes its own delta from the clock, so
    /// calling it more often advances the same total time — it does not run animation fast.
    /// </remarks>
    private void ResampleAnimation()
    {
        // EVERY RIG IN VIEW, EVERY DRAWN FRAME. Animation runs on Godot's clock; a rig that
        // waited for the guest's next frame stepped at 30 Hz under a display drawing faster.
        // Out of view the pose holds, as the guest's own gate holds it (`sub_822142E8`). A rig
        // the walk already posed this frame is not posed twice.
        Camera3D? eye = GetViewport()?.GetCamera3D();
        bool haveEye = eye is not null && IsInstanceValid(eye);
        Transform3D toWorld = GlobalTransform;

        Plane[] frustum = haveEye ? Frustum(eye!) : System.Array.Empty<Plane>();
        ulong nowMs = Time.GetTicksMsec();

        foreach (uint address in _reblending)
        {
            if (_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig) &&
                InViewForRepose(address, haveEye, toWorld, frustum, nowMs))
            {
                rig.Reblend();
            }
        }

        foreach (uint address in _animating)
        {
            if (!_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig) ||
                !InViewForRepose(address, haveEye, toWorld, frustum, nowMs))
            {
                continue;
            }

            Refii.Animations.Engine_ClipAnimator animator = ClipAnimatorFor(address, rig);
            if (animator.WroteThisFrame)
            {
                continue;
            }

            animator.Apply(address, writeBones: true);
        }
    }

    private bool InViewForRepose(
        uint address, bool haveEye, Transform3D toWorld, Plane[] frustum, ulong nowMs)
    {
        if (!haveEye || IsPrincipal(address) ||
            !_written.TryGetValue(address, out (MeshInstance3D Node, Transform3D Drawn) last))
        {
            return true;
        }

        return RigInView(address, true, toWorld * last.Drawn.Origin, frustum, nowMs);
    }

    /// <summary>
    /// Whether a rig outside <see cref="PoseFullRateRadius"/> writes its bones this step.
    /// </summary>
    /// <remarks>
    /// The guest's divider (0x8220D67C) skips the step whenever the animation LOD is not 3 and the
    /// sub-step does not complete a tick, so a far rig lands on 15 Hz against a near rig's 30 —
    /// and EVERY far rig on the same sub-step, the one that completes the tick.
    ///
    /// The phase is the guest's as well as the rate, never spread by address: a shadow map
    /// re-renders when any caster in its pool is written, and halves interleaved by address give
    /// every lamp's pool a write on every guest frame. On the guest's phase a pool of far rigs is
    /// written on alternate guest frames and its map re-renders at 15 Hz.
    /// </remarks>
    private bool FarRigPosesThisStep(uint address) => _tickCompletedThisWalk;

    /// <summary>Whether this walk's guest frame completed a game tick; see FarRigPosesThisStep.</summary>
    private bool _tickCompletedThisWalk = true;

    /// <summary>
    /// The guest's visibility gate for posing (`sub_822142E8`): in the frustum now, or seen
    /// within <see cref="RigHoldMs"/>. An unmeasurable rig (not in the tree yet) and a rig never
    /// written both count as in view. A margin covers a body whose origin is just off screen.
    /// </summary>
    private bool RigInView(uint address, bool measurable, Vector3 at, Plane[] frustum, ulong nowMs)
    {
        if (!measurable || frustum.Length == 0 || !_rigSeenMs.ContainsKey(address))
        {
            _rigSeenMs[address] = nowMs;
            return true;
        }

        bool inside = true;
        foreach (Plane plane in frustum)
        {
            if (plane.DistanceTo(at) > RigViewMargin)
            {
                inside = false;
                break;
            }
        }

        if (inside)
        {
            _rigSeenMs[address] = nowMs;
            return true;
        }

        return nowMs - _rigSeenMs[address] <= RigHoldMs;
    }

    private Plane[] Frustum(Camera3D eye)
    {
        ulong frame = Engine.GetProcessFrames();
        if (frame != _frustumFrame)
        {
            // `GetFrustum` allocates a fresh wrapper every call and takes no buffer, so it is
            // called once per drawn frame (the walk and ResampleAnimation both ask) and copied
            // into `_frustum`.
            Godot.Collections.Array<Plane> planes = eye.GetFrustum();
            if (_frustum.Length != planes.Count)
            {
                _frustum = new Plane[planes.Count];
            }

            for (int i = 0; i < planes.Count; i++)
            {
                _frustum[i] = planes[i];
            }

            // Disposed, not left to the finalizer: an undisposed wrapper survives its first GC.
            ((Godot.Collections.Array)planes).Dispose();

            _frustumFrame = frame;
        }

        return _frustum;
    }

    /// <summary>Last time each rig was inside the frustum.</summary>
    private readonly Dictionary<uint, ulong> _rigSeenMs = new();

    /// <summary>
    /// Last time the feed ASKED for each rig's bones to be written — in view and on its rate
    /// step. For the debug channel: beside the animator's `driven` and the rig's `applied` it
    /// separates "off screen", "in view but starved by the rate gate" and "asked and not
    /// written", which those two counters cannot, since both only move while a rig is in view.
    /// </summary>
    private readonly Dictionary<uint, ulong> _boneWriteAskedMs = new();

    /// <summary>Where each entity was when its probe sample was last taken, and under which theme.</summary>
    private readonly Dictionary<uint, (Vector3 At, long Gen)> _lastLit = new();

    /// <summary>The guest keeps posing for 30 render frames after a rig leaves view: one second at its rate.</summary>
    private const ulong RigHoldMs = 1000;

    /// <summary>Metres past a frustum plane a rig's origin may sit and still count as in view.</summary>
    private const float RigViewMargin = 2.5f;

    private Plane[] _frustum = System.Array.Empty<Plane>();
    private ulong _frustumFrame = ulong.MaxValue;

    /// <summary>
    /// Re-drives the static twin of every entity that is moving, on the frames between walks.
    /// </summary>
    /// <remarks>
    /// The walk drives married twins at tick rate; this keeps them with their bodies every
    /// frame. Only the movers: a twin married to something standing still needs no re-drive.
    /// </remarks>
    private void ResampleAdopted()
    {
        if (_adoptions.Count == 0 || _movers.Count == 0)
        {
            return;
        }

        foreach (uint address in _movers)
        {
            if (_adoptions.TryGetValue(address, out (Refii.Entities.Engine_StaticTwins.Adoption Twin, ulong Uid) married) &&
                _nodes.TryGetValue(address, out MeshInstance3D? node) &&
                IsInstanceValid(node))
            {
                Refii.Entities.Engine_StaticTwins.Drive(married.Twin, AdoptedWorld(address, node));
            }
        }
    }

    /// <summary>
    /// Everything that turns the guest's stated pairs into what is on screen, in the one order
    /// it must happen in, run EVERY frame.
    /// </summary>
    /// <remarks>
    /// The walk updates pairs; this draws them, on walk frames and the frames between alike.
    ///
    /// Order: bodies first, because a bone's world position is measured through the body it
    /// hangs off; bones next; then attachments, which read a finished bone; finally the twins,
    /// which read a finished body.
    /// </remarks>
    private void ApplyDrawn()
    {
        ++Refii.Entities.Engine_DrawnFrame.Frames;
        long lap = System.Diagnostics.Stopwatch.GetTimestamp();
        ResampleMovers();
        Refii.Core.Engine_FramePhases.Lap("ent.drawn.movers", ref lap);
        ResampleAnimation();
        Refii.Core.Engine_FramePhases.Lap("ent.drawn.animation", ref lap);
        PlaceAttached();
        Refii.Core.Engine_FramePhases.Lap("ent.drawn.attached", ref lap);
        ResampleAdopted();
        Refii.Core.Engine_FramePhases.Lap("ent.drawn.adopted", ref lap);
        Refii.Entities.Engine_ShadowSkeletons.EndWalk();
        Refii.Core.Engine_FramePhases.Lap("ent.drawn.shadows", ref lap);
    }

    private void ResampleMovers()
    {
        if (_movers.Count == 0)
        {
            return;
        }

        _arrived.Clear();
        double drawTick = Refii.Entities.Engine_DrawnFrame.DrawTick;
        foreach (uint address in _movers)
        {
            if (!_bodies.TryGetValue(address, out Refii.Core.Engine_TickTimeline? body) ||
                !_nodes.TryGetValue(address, out MeshInstance3D? node) ||
                !IsInstanceValid(node))
            {
                _arrived.Add(address);
                continue;
            }

            Place(address, node, body.Advance(Refii.Entities.Engine_DrawnFrame.NowUsec, drawTick));
            if (body.Settled)
            {
                _arrived.Add(address);
            }
        }

        foreach (uint address in _arrived)
        {
            _movers.Remove(address);
        }
    }

    /// <summary>
    /// Inside this (metres), a rig poses every step; outside it, every other step. The guest's
    /// <c>BoneLODDistance</c> (AM+220), whose constructor default is 10.0 and which
    /// <c>sub_8257CB20</c> clamps UP to a floor of 8.0 (<c>flt_8209B478</c>).
    /// </summary>
    /// <remarks>
    /// A rate, never a cutoff: the guest never stops posing a visible rig at any distance, and a
    /// rig that is never written stays in its bind pose (<c>sub_8257CB20</c> seeds the palette
    /// with the model-space rest pose). Live-settable through the debug channel's
    /// `render poseFullRate=`: a very large value puts every rig on 30 Hz, 0 puts every rig on
    /// 15 Hz.
    /// </remarks>
    public static float PoseFullRateRadius = 10f;

    /// <summary>Entities drawn blended between consecutive stated transforms, and those taken live.</summary>
    public static long BlendedEntities;

    public static long LiveOnlyEntities;

    /// <summary>
    /// The transform to draw: a blend of the previous and current stated transforms, following
    /// the guest's own interpolation contract.
    /// </summary>
    /// <remarks>
    /// The guest's tick first latches every renderable's transform (`sub_8222B2E0`: position to
    /// `obj+0xA0`, quaternion to `obj+0xB0`), the tick moves it, and `sub_822112D8` draws
    /// `prev*(1-alpha) + live*alpha` with a real slerp for the rotation.
    ///
    /// `Interpolate` is `obj+0xC9` and it is an ENABLE — non-zero blends, zero takes the live
    /// transform. It is cleared only by the Havok rigid-body component's init, so a physics-owned
    /// body is drawn where physics put it.
    /// </remarks>
    private Transform3D Drawn(HostBridge.GuestEntity entity, Transform3D stated)
    {
        if (!entity.Interpolate)
        {
            _bodies.Remove(entity.Address);
            _movers.Remove(entity.Address);
            ++LiveOnlyEntities;
            return stated;
        }

        // Blended between consecutive STATED transforms, not across the guest's own pair: the
        // guest latches `prev <- live` late in a tick and moves `live` only partway into the
        // next, so for part of every tick both halves are equal. The guest renders only at alpha
        // 0.5 and 1.0, where that is harmless; we render through the window. The value drawn is
        // still the guest's; only its history is kept here, stamped with the tick it was read on
        // and drawn at the world's draw point with the limbs it carries.
        uint tick = Refii.Core.Engine_TickClock.GuestGameTick;
        if (!_bodies.TryGetValue(entity.Address, out Refii.Core.Engine_TickTimeline? body))
        {
            body = new Refii.Core.Engine_TickTimeline();
            body.Reset(tick, stated);
            _bodies[entity.Address] = body;
            ++LiveOnlyEntities;
            return stated;
        }

        bool moved = !stated.Origin.IsEqualApprox(body.Newest.Origin) ||
            !stated.Basis.IsEqualApprox(body.Newest.Basis);

        // A jump beyond TeleportMetres (a door, a cut, a scripted placement) is a teleport,
        // drawn at once rather than glided across.
        if (moved && body.Newest.Origin.DistanceSquaredTo(stated.Origin) >
            Refii.Core.Engine_TickTimeline.TeleportMetres * Refii.Core.Engine_TickTimeline.TeleportMetres)
        {
            body.Reset(tick, stated);
            _movers.Remove(entity.Address);
            ++LiveOnlyEntities;
            return stated;
        }

        // Every read is stated, changed or not: unchanged is a body standing still.
        body.Observe(tick, stated);
        if (moved)
        {
            _movers.Add(entity.Address);

            // Still told, because the world's own playhead is what MEASURES the shared period.
            Refii.Entities.Engine_DrawnFrame.NoteRestated();
        }

        Transform3D drawn = body.Advance(Refii.Entities.Engine_DrawnFrame.NowUsec,
            Refii.Entities.Engine_DrawnFrame.DrawTick);
        if (body.Settled)
        {
            _movers.Remove(entity.Address);
            ++LiveOnlyEntities;
            return stated;  // standing still: nothing to blend, and nothing to re-blend later
        }

        _movers.Add(entity.Address);
        ++BlendedEntities;
        return drawn;
    }

    /// <summary>Each entity's stated placements, stamped with the tick they were read on.</summary>
    private readonly Dictionary<uint, Refii.Core.Engine_TickTimeline> _bodies = new();

    private uint _traceAddress;
    private int _traceRemaining;
    private readonly List<(ulong Usec, uint Tick, Vector3 Drawn, Vector3 Stated, Vector3 Hip)> _trace = new();

    /// <summary>
    /// Starts recording one entity's drawn origin, stated origin and hip bone per frame — the
    /// series that says whether a body moves in even steps or in the jerks a viewer calls
    /// stutter. Read back with <see cref="TraceReport"/>.
    /// </summary>
    public void BeginTrace(uint address, int frames)
    {
        _traceAddress = address;
        _traceRemaining = frames;
        _trace.Clear();
    }

    public List<Dictionary<string, object?>> TraceReport()
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach ((ulong usec, uint tick, Vector3 drawn, Vector3 stated, Vector3 hip) in _trace)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["usec"] = usec,
                ["tick"] = tick,
                ["drawn"] = new[] { drawn.X, drawn.Y, drawn.Z },
                ["stated"] = new[] { stated.X, stated.Y, stated.Z },
                ["hip"] = new[] { hip.X, hip.Y, hip.Z },
            });
        }

        return rows;
    }

    private void SampleTrace(uint address, MeshInstance3D node, Transform3D stated,
        Refii.Entities.Engine_EntitySkeleton? rig)
    {
        if (address != _traceAddress || _traceRemaining <= 0)
        {
            return;
        }

        --_traceRemaining;
        Vector3 hip = Vector3.Zero;
        if (rig is not null &&
            (rig.TryBone("Shadow_Hip", out int bone) || rig.TryBone("Shadow_Hips", out bone)))
        {
            hip = (node.Transform * rig.Node.GetBoneGlobalPose(bone)).Origin;
        }

        _trace.Add((Godot.Time.GetTicksUsec(), Refii.Host.HostBridge.SimTick,
            node.Transform.Origin, stated.Origin, hip));
    }

    private SubViewport? _closeup;
    private Camera3D? _closeupCamera;

    /// <summary>
    /// Frames one entity's head (or its whole body) in a private viewport that shares the
    /// world, so the debug channel can look at a face without moving the player's camera. One
    /// render is queued; the image is ready on the following frame.
    /// </summary>
    public bool ArmCloseup(uint address, string bone, float distance, int size)
    {
        if (!_nodes.TryGetValue(address, out MeshInstance3D? node))
        {
            return false;
        }

        if (_closeup is null)
        {
            _closeup = new SubViewport
            {
                Size = new Vector2I(size, size),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                World3D = GetViewport().World3D,
                Msaa3D = Viewport.Msaa.Msaa4X,
            };
            _closeupCamera = new Camera3D { Current = true, Near = 0.02f, Far = 50f, Fov = 30f };
            _closeup.AddChild(_closeupCamera);
            AddChild(_closeup);
        }

        _closeup.Size = new Vector2I(size, size);
        Vector3 target = node.GlobalTransform.Origin + Vector3.Up;
        Vector3 forward = -node.GlobalTransform.Basis.Z;
        if (_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig) &&
            bone.Length != 0 && rig.TryBone(bone, out int index))
        {
            Transform3D boneGlobal = node.GlobalTransform * rig.Node.GetBoneGlobalPose(index);
            target = boneGlobal.Origin;
        }

        // In front of the face: the character's forward is its -Z the way the placement
        // conversion builds it, and a camera looks down its own -Z.
        Vector3 eye = target + forward.Normalized() * distance;
        _closeupCamera!.GlobalTransform = new Transform3D(Basis.Identity, eye).LookingAt(target, Vector3.Up);
        _closeup.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        return true;
    }

    public Image? CloseupImage() => _closeup?.GetTexture().GetImage();

    /// <summary>Every clip-driven rig, for the debug channel: address, driven frames, fallback.</summary>
    public List<Dictionary<string, object?>> DescribeAnimators()
    {
        var rows = new List<Dictionary<string, object?>>();
        ulong nowMs = Time.GetTicksMsec();
        foreach ((uint address, Refii.Animations.Engine_ClipAnimator animator) in _clipAnimators)
        {
            if (!animator.Alive)
            {
                continue;
            }

            rows.Add(new Dictionary<string, object?>
            {
                ["address"] = $"{address:X8}",

                // The host's OWN view decision, in ms since it last held; -1 is never.
                ["seenMsAgo"] = _rigSeenMs.TryGetValue(address, out ulong seen) ? (long)(nowMs - seen) : -1L,
                ["writeAskedMsAgo"] = _boneWriteAskedMs.TryGetValue(address, out ulong asked)
                    ? (long)(nowMs - asked)
                    : -1L,
                ["driven"] = animator.Driven,
                ["lastFallback"] = animator.LastFallback,
                ["bones"] = animator.BoneCount,
                ["restBones"] = animator.LastRestBones,
                ["mostlyRestFrames"] = animator.MostlyRestFrames,
                ["topClip"] = $"{animator.LastTopKey:X8}",
                ["topShare"] = animator.LastTopShare,
                ["postOps"] = animator.PostOpsApplied,
                ["channels"] = animator.LastChannels,
                ["overlays"] = animator.LastOverlays,
                ["clips"] = animator.DescribeClips(),
            });
        }

        return rows;
    }

    /// <summary>The guest's stated clip leaves for one drawn entity, for the debug channel.</summary>
    public List<Dictionary<string, object?>>? DescribeLeaves(uint address) =>
        _clipAnimators.TryGetValue(address, out Refii.Animations.Engine_ClipAnimator? animator)
            ? animator.Describe(address)
            : null;

    /// <summary>
    /// The surfaces one drawn entity is made of, with the material each carries — the answer to
    /// "what is actually on this face", which a screenshot of a 40-pixel head cannot give.
    /// </summary>
    /// <summary>
    /// One entity's rig as GODOT holds it: every bone's current pose rotation and global
    /// position, and whether the node that should follow it is visible and skinned. The
    /// question this answers is "the animator drove the rig, so why did nothing move" — by
    /// showing whether the bones moved and whether a mesh is bound to them at all.
    /// </summary>
    public Dictionary<string, object?>? DescribeBones(uint address)
    {
        if (!_nodes.TryGetValue(address, out MeshInstance3D? node))
        {
            return null;
        }

        var result = new Dictionary<string, object?>
        {
            ["visible"] = node.Visible && node.IsVisibleInTree(),
            ["surfaces"] = node.Mesh?.GetSurfaceCount() ?? 0,
            ["skinned"] = node.Skin is not null && !node.Skeleton.IsEmpty,
            ["twinHidden"] = Twins?.IsHidden(node.GetMeta("entity_record", 0u).AsUInt32()),
            ["position"] = $"({node.GlobalPosition.X:0.00},{node.GlobalPosition.Y:0.00},{node.GlobalPosition.Z:0.00})",
        };

        if (_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? skeleton))
        {
            Skeleton3D sk = skeleton.Node;
            var bones = new List<string>();
            for (int b = 0; b < sk.GetBoneCount(); b++)
            {
                Transform3D pose = sk.GetBonePose(b);
                Vector3 e = pose.Basis.GetEuler() * (180f / Mathf.Pi);
                Vector3 g = (sk.GlobalTransform * sk.GetBoneGlobalPose(b)).Origin;
                Vector3 sc = sk.GetBonePoseScale(b);
                string scale = (sc - Vector3.One).LengthSquared() > 1e-6f ? $" scale=({sc.X:0.000},{sc.Y:0.000},{sc.Z:0.000})" : string.Empty;
                bones.Add($"{b} '{sk.GetBoneName(b)}' parent={sk.GetBoneParent(b)} rot=({e.X:0.0},{e.Y:0.0},{e.Z:0.0}) " +
                    $"local=({pose.Origin.X:0.00},{pose.Origin.Y:0.00},{pose.Origin.Z:0.00}) global=({g.X:0.00},{g.Y:0.00},{g.Z:0.00}){scale}");
            }

            result["bones"] = bones;
        }

        // THE GUEST'S OWN MORPH RECORD for this entity, as exported: what the rig applies.
        var morph = new List<string>();
        foreach (HostBridge.GuestBoneMorph m in HostBridge.GetBoneMorph(address))
        {
            morph.Add($"bone {m.Bone} off=({m.Offset.X:0.000},{m.Offset.Y:0.000},{m.Offset.Z:0.000}) sc=({m.Scale.X:0.000},{m.Scale.Y:0.000},{m.Scale.Z:0.000})");
        }

        result["morph"] = morph;

        // What the guest stated to its composite submit for this entity.
        var partMorphs = new List<string>();
        HostBridge.GuestPartMorph[] statedParts = HostBridge.GetEntityPartMorphs(address, out uint partGeneration);
        foreach (HostBridge.GuestPartMorph part in statedParts)
        {
            var targets = new List<string>();
            foreach ((uint model, float weight) in part.Targets)
            {
                targets.Add($"{model:X8}@{weight:0.000}");
            }

            partMorphs.Add($"{part.BaseModel:X8} <- {string.Join(' ', targets)}");
        }

        result["partMorphs"] = partMorphs;
        result["partMorphGeneration"] = partGeneration;
        result["partMorphStatements"] = HostBridge.PartMorphStatements;
        result["partMorphsMorphed"] = HostBridge.PartMorphsMorphed;

        var textureLayers = new List<string>();
        foreach (HostBridge.GuestTextureComposite composite in HostBridge.GetEntityTextureLayers(address, out _))
        {
            var layers = new List<string>();
            foreach (HostBridge.GuestTextureLayer layer in composite.Layers)
            {
                layers.Add($"{layer.Texture:X8} mode={layer.Mode} w={layer.Weight:0.000}" +
                    (layer.HasTint ? $" tint={layer.Tint:X8} b={layer.Brightness:0.00}" : string.Empty) +
                    $" '{Refii.Textures.Engine_TexPathIndex.Resolve(layer.Texture) ?? "?"}'");
            }

            textureLayers.Add($"{(composite.Overlay ? "OVERLAY " : string.Empty)}{composite.Original:X8} '{Refii.Textures.Engine_TexPathIndex.Resolve(composite.Original) ?? "?"}': {string.Join(" | ", layers)}");
        }

        result["textureLayers"] = textureLayers;
        List<HostBridge.GuestWornItem> worn = HostBridge.GetEntityWornItems(address);
        var wornLines = new List<string>();
        foreach (HostBridge.GuestWornItem item in worn)
        {
            string name = Refii.Gdb.Engine_GdbCatalog.All?.NameOf(item.Hash) ?? "?";
            Refii.Equipment.ItemDetail? detail = Refii.Entities.Engine_HiddenClusters.DetailFor(item.Hash);
            wornLines.Add($"{item.Hash:X8} '{name}'{(item.Female ? " female" : "")}{(item.Open ? " open" : "")} clusters=[{(detail is null ? "-" : string.Join(",", detail.ClustersCovered))}]");
        }

        result["worn"] = wornLines;
        var partLines = new List<string>();
        List<HostBridge.GuestPartHides> builtParts = HostBridge.GetEntityPartHides(address);
        Dictionary<uint, HashSet<string>> hiddenPerPart = Refii.Entities.Engine_HiddenClusters.HiddenPerPart(builtParts);
        foreach (HostBridge.GuestPartHides part in builtParts)
        {
            string hides = hiddenPerPart.TryGetValue(part.Hash, out HashSet<string>? set) ? string.Join(";", set) : "";
            partLines.Add($"{part.Hash:X8} layer {part.Layer}{(part.Body ? " body" : "")} covers=[{string.Join(";", part.Covers)}] hidden=[{hides}]");
        }

        result["parts"] = partLines;
        if (HostBridge.GetAnimPostOps(address) is { } post)
        {
            var opLines = new List<string>();
            foreach (HostBridge.GuestPostOp op in post.Ops)
            {
                opLines.Add($"{(op.Kind == 0 ? "aim" : "look")} uid {op.RecordUid:X8} mode {op.Mode} w {op.Weight:0.000} rw {op.RecordWeight:0.000} q0=({op.Targets[0].X:0.00},{op.Targets[0].Y:0.00},{op.Targets[0].Z:0.00},{op.Targets[0].W:0.00})");
            }

            result["postOps"] = new Dictionary<string, object?>
            {
                ["aimOn"] = post.AimOn,
                ["lookOn"] = post.LookOn,
                ["aimBones"] = post.AimBones,
                ["lookBones"] = post.LookBones,
                ["ops"] = opLines,
            };
        }
        if (_nodes.TryGetValue(address, out MeshInstance3D? owner) && Refii.Entities.Engine_BodyVariants.SourceMesh(owner) is ArrayMesh ownerMesh)
        {
            string[] regions = Refii.Models.Engine_MdlMesh.SurfaceRegions(ownerMesh);
            var surfaceRegions = new List<string>();
            for (int s = 0; s < ownerMesh.GetSurfaceCount(); s++)
            {
                string region = s < regions.Length ? regions[s] : string.Empty;
                bool hidden = (Refii.Entities.Engine_HiddenClusters.CollapsedMask(owner) & (System.UInt128.One << s)) != System.UInt128.Zero;
                surfaceRegions.Add($"{s} '{ownerMesh.SurfaceGetName(s)}' region='{region}'{(hidden ? " HIDDEN" : "")}");
            }

            result["surfaceRegions"] = surfaceRegions;
        }

        return result;
    }

    /// <summary>
    /// What lights one entity, as numbers: the instance terms the shader was handed (probe SH,
    /// lightmap factor and bake mode, flat ambient) and every placed light whose range reaches
    /// it. An address of 0 is the hero.
    /// </summary>
    /// <summary>
    /// Bones whose global pose has a NEGATIVE determinant. Godot takes a skinned instance's facing
    /// from the NODE transform alone, so triangles skinned to a mirrored bone rasterise as back
    /// faces, and the material negates a back face's normal: that body region lights inside out.
    /// </summary>
    private static List<string> MirroredBones(MeshInstance3D node)
    {
        var found = new List<string>();
        if (node.Skeleton.IsEmpty || node.GetNodeOrNull<Skeleton3D>(node.Skeleton) is not Skeleton3D skeleton)
        {
            found.Add("(no skeleton)");
            return found;
        }

        int bones = skeleton.GetBoneCount();
        for (int bone = 0; bone < bones; bone++)
        {
            float determinant = skeleton.GetBoneGlobalPose(bone).Basis.Determinant();
            if (determinant < 0f)
            {
                found.Add($"{skeleton.GetBoneName(bone)} det={determinant:0.###}");
            }
        }

        found.Insert(0, $"{found.Count} of {bones} bones mirrored");
        return found;
    }

    public Dictionary<string, object?>? DescribeLighting(uint address)
    {
        if (address == 0u)
        {
            address = HostBridge.HeroEntityAddress();
        }

        if (!_nodes.TryGetValue(address, out MeshInstance3D? node) || !node.IsInsideTree())
        {
            return null;
        }

        Vector3 at = node.GlobalPosition;
        var result = new Dictionary<string, object?>
        {
            ["address"] = $"{address:X8}",
            ["at"] = $"({at.X:0.0},{at.Y:0.0},{at.Z:0.0})",
            ["layers"] = $"0x{node.Layers:X}",
            ["nodeDeterminant"] = node.GlobalTransform.Basis.Determinant(),
            ["mirroredBones"] = MirroredBones(node),
            ["castShadow"] = node.CastShadow.ToString(),
        };

        foreach (string parameter in new[]
            { "probe_r", "probe_g", "probe_b", "lightmap_colour", "ambient_colour", "darkening_colour" })
        {
            Variant value = node.GetInstanceShaderParameter(parameter);
            result[parameter] = value.VariantType == Variant.Type.Nil ? "unset" : value.ToString();
        }

        var lights = new List<Light3D>();
        Refii.Lighting.Engine_LevelLights.SnapshotLights(lights);
        var reaching = new List<(float Distance, string Row)>();
        foreach (Light3D light in lights)
        {
            if (!light.IsInsideTree() || !light.Visible)
            {
                continue;
            }

            float range = light is OmniLight3D omni ? omni.OmniRange
                : light is SpotLight3D spot ? spot.SpotRange : 0f;
            float distance = at.DistanceTo(light.GlobalPosition);
            if (distance > range)
            {
                continue;
            }

            Color c = light.LightColor;
            reaching.Add((distance,
                $"{light.Name} {light.GetType().Name} d={distance:0.0} range={range:0.0} " +
                $"energy={light.LightEnergy:0.00} colour=({c.R:0.00},{c.G:0.00},{c.B:0.00}) " +
                $"shadow={light.ShadowEnabled} casters=0x{light.ShadowCasterMask:X}"));
        }

        reaching.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        result["reaching"] = reaching.ConvertAll(row => row.Row);
        return result;
    }

    public List<Dictionary<string, object?>>? DescribeSurfaces(uint address)
    {
        if (!_nodes.TryGetValue(address, out MeshInstance3D? node) || Refii.Entities.Engine_BodyVariants.SourceMesh(node) is not { } mesh)
        {
            return null;
        }

        var rows = new List<Dictionary<string, object?>>();
        for (int surface = 0; surface < mesh.GetSurfaceCount(); ++surface)
        {
            var row = new Dictionary<string, object?>
            {
                ["surface"] = surface,
                ["name"] = mesh is ArrayMesh named ? named.SurfaceGetName(surface) : string.Empty,
            };

            if (mesh.SurfaceGetMaterial(surface) is ShaderMaterial material)
            {
                row["shader"] = material.Shader?.ResourcePath ?? string.Empty;
                foreach (string parameter in new[]
                    { "albedo_tex", "normal_tex", "specular_tex", "emission_tex", "orm_tex" })
                {
                    if (material.GetShaderParameter(parameter).As<Texture2D>() is { } texture)
                    {
                        row[parameter] = texture.ResourcePath.Length != 0
                            ? texture.ResourcePath
                            : texture.ResourceName;
                    }
                }

                row["scissor"] = material.GetShaderParameter("alpha_scissor_on").AsBool();
            }
            else
            {
                row["material"] = mesh.SurfaceGetMaterial(surface)?.GetType().Name ?? "none";
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The attachments hanging on one drawn entity, for the debug channel.</summary>
    public List<Dictionary<string, object?>> DescribeAttachments(uint address) =>
        _attachments.Describe(address, _skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig) ? rig : null);

    /// <summary>The rig of one drawn entity, for the debug channel.</summary>
    public Dictionary<string, object?>? DescribeRig(uint address)
    {
        if (!_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig))
        {
            return null;
        }

        (int changes, int applies) = rig.ChangeCounts;
        return new Dictionary<string, object?>
        {
            ["bones"] = rig.BoneCount,
            ["guestBones"] = rig.GuestBoneCount,
            ["mapped"] = rig.Mapped,
            ["guestOnly"] = rig.GuestOnly,
            ["changed"] = changes,
            ["applied"] = applies,
            ["localReads"] = rig.LocalReads,
            ["channels"] = _clipAnimators.TryGetValue(address, out Refii.Animations.Engine_ClipAnimator? fold)
                ? fold.LastChannels : -1,
            ["runnerFlips"] = fold?.RigRunnerFlips ?? -1,
            ["topUid"] = fold?.LastTopUid ?? 0u,
            ["runnerUid"] = fold?.LastRunnerUid ?? 0u,
            ["topShare"] = fold?.LastTopShare ?? -1f,
            ["topKey"] = fold?.LastTopKey ?? 0u,
            ["shareSteps"] = fold?.RigShareSteps ?? -1,
        };
    }

    public string MostActiveRigs()
    {
        var rows = new List<(int Changes, int Applies, uint Address)>();
        foreach ((uint address, Refii.Entities.Engine_EntitySkeleton rig) in _skeletons)
        {
            (int changes, int applies) = rig.ChangeCounts;
            if (applies > 0)
            {
                rows.Add((changes, applies, address));
            }
        }

        rows.Sort((a, b) => b.Changes.CompareTo(a.Changes));
        var line = new System.Text.StringBuilder();
        for (int i = 0; i < rows.Count && i < 3; ++i)
        {
            line.Append($" 0x{rows[i].Address:X8} {rows[i].Changes}/{rows[i].Applies} ({100.0 * rows[i].Changes / rows[i].Applies:0}%)");
        }

        return line.Length == 0 ? "none" : line.ToString();
    }

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

    /// <summary>
    /// Entity placements whose ORIGIN was not usable and was refused. Run total.
    ///
    /// Non-zero is not automatically a fault — a torn read of a position the guest is midway
    /// through writing is expected at some rate. A rate that CLIMBS is the fault, and one that
    /// reaches the renderer is a hang rather than a wrong pixel.
    /// </summary>
    public static int NonFinitePlacements;

    /// <summary>Squares to 1e18, three orders inside float's range, so a depth cannot overflow.</summary>
    private const float MaxPlacementCoordinate = 1e9f;

    private static bool IsUsablePlacement(Vector3 position) =>
        position.IsFinite() &&
        System.Math.Abs(position.X) < MaxPlacementCoordinate &&
        System.Math.Abs(position.Y) < MaxPlacementCoordinate &&
        System.Math.Abs(position.Z) < MaxPlacementCoordinate;
}
