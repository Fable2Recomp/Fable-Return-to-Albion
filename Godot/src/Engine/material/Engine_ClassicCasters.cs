using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic sun cascades' own scene: the guest's casters, and nothing else.
/// </summary>
/// <remarks>
/// The guest draws its cascades from a scene of their own ("Directional Light Renderer",
/// sub_82AA0A98) holding bucket 0 only. A render twin joins the near cascade when its +28 mask
/// has bit 8 and the far one with bit 16 (`sub_82AA3C78`, from the stated flag word: 0x40 no
/// cast, 0x80 no distant cast); a StaticMesh is cut hard at its draw distance D from the eye
/// (`sub_8221E5F0`, twin+292) and a tree casts only its ShadowModel, within S (`sub_822DAB20`,
/// +600). Terrain, water and particles are not in it.
///
/// Here that scene is a World3D of its own, shared by both cascade viewports, so the main view's
/// cull never walks it. Each caster is a server instance on the drawn node's own mesh or
/// MultiMesh (no copy; a MultiMesh slot write reaches both) and, for a skinned body, on its
/// skeleton, so it follows the pose without one. It draws with the drawn materials: their
/// shadow-pass branch (XR_SHADOW_PASS_LAYER) leaves only depth, alpha test included. No Godot
/// visibility range applies: Godot measures one from the rendering camera, and a cascade
/// camera sits 100 m up the sun; the cut is the guest's, from the eye.
/// </remarks>
public static class Engine_ClassicCasters
{
    /// <summary>Casting bits a node was built with (Engine_ClassicShadows.Casts*).</summary>
    public static readonly StringName CastingMeta = "classic_casting";

    /// <summary>The eye distance a node casts within; 0 or absent is no cut.</summary>
    public static readonly StringName ReachMeta = "classic_cast_reach";

    private sealed class Entry
    {
        public required GeometryInstance3D Node;
        public Rid Instance;
        public Rid Base;
        public Rid Skeleton;
        public float Reach;
        public bool Dynamic;
        public bool Shown = true;
        public bool TwoSided;
        public bool SurfacesPending;
        public int Stagger;
        public Transform3D Placed;
        /// <summary>The drawn box's centre at the last placement, for the reach cut.</summary>
        public Vector3 Centre;
        /// <summary>The drawn box's centre in the node's space, taken when the base changes.</summary>
        public Vector3 LocalCentre;
        /// <summary>The base changed since <see cref="LocalCentre"/> was taken (a new mesh, a new box).</summary>
        public bool CentreStale = true;
        /// <summary>The joined cast mesh the instance draws instead of the node's (Engine_ClassicCasterMerge); held alive here.</summary>
        public ArrayMesh? Merged;
        /// <summary>A batch's twin carrying <see cref="Merged"/> (Engine_PropShadow.ClassicTwin); held alive here.</summary>
        public MultiMesh? MergedBatch;
        /// <summary>The base the instance draws now (the node's, or a joined cast).</summary>
        public Rid Cast;
        /// <summary>The drawn node's visibility in the tree, kept by its `visibility_changed` (emitted on descendants too).</summary>
        public bool Visible = true;
        /// <summary>The cascades this caster joins (Engine_ClassicShadows.CasterLayers).</summary>
        public uint Layers;
        /// <summary>Its place in a cell of still casters (Engine_ClassicCasterCells), while it holds still.</summary>
        public Engine_ClassicCasterCells.Member? Cell;
        /// <summary>A dynamic caster's frames unmoved, toward joining a cell.</summary>
        public int StillFrames;
        /// <summary>It cannot share a cell (skinned, overridden, nothing joinable) until its next rebase.</summary>
        public bool CellRefused;
    }

    private static World3D? _world;
    private static readonly List<Entry> Entries = new();
    private static readonly Dictionary<ulong, Entry> ByNode = new();

    /// <summary>Casters whose node's visibility changed since the last update.</summary>
    private static readonly HashSet<Entry> Touched = new();
    private static int _cursor;
    private static long _frame;

    /// <summary>Entries the slice re-checks each frame; dynamic ones are followed every frame.</summary>
    private const int SlicePerFrame = 512;

    /// <summary>The scene both cascade viewports draw.</summary>
    public static World3D World => _world ??= new World3D();

    /// <summary>Casters registered now.</summary>
    public static int Count => Entries.Count;

    /// <summary>Casters inside their reach at the last check.</summary>
    public static int Shown { get; private set; }

    /// <summary>
    /// Whether the guest states that <paramref name="node"/> casts no sun-cascade shadow at all
    /// (renderable flag 0x40, or a prop's stated casting of nothing). Only applies under the
    /// classic sun-cascade renderer; in the modern renderer, indoor furniture and cave props have
    /// 0x40 set because the 360 had no point-light prop shadows.
    /// </summary>
    public static bool NeverCasts(Node node) =>
        Engine_ClassicMaterial.Active &&
        node.HasMeta(CastingMeta) && (node.GetMeta(CastingMeta).AsInt32() &
            (Engine_ClassicShadows.CastsNear | Engine_ClassicShadows.CastsFar)) == 0;

    /// <summary>States how a node casts, for <see cref="Register"/> to read once its mesh is in.</summary>
    public static void Mark(Node node, byte casting, float reach)
    {
        node.SetMeta(CastingMeta, (int)casting);
        node.SetMeta(ReachMeta, reach);
    }

    /// <summary>
    /// Gives a drawn node its caster, or refreshes the one it has. <paramref name="dynamic"/>
    /// nodes (entities) are followed every frame; the rest are re-checked in slices.
    /// </summary>
    public static void Register(GeometryInstance3D node, bool dynamic = false)
    {
        if (!Engine_ClassicMaterial.Active || !GodotObject.IsInstanceValid(node))
        {
            return;
        }

        byte casting = node.HasMeta(CastingMeta)
            ? (byte)node.GetMeta(CastingMeta).AsInt32()
            : Engine_ClassicShadows.CastsDefault;
        uint layers = Engine_ClassicShadows.CasterLayers(casting);
        Rid baseRid = BaseOf(node);
        if (layers == 0 && ByNode.TryGetValue(node.GetInstanceId(), out Entry? silenced))
        {
            // Stated a non-caster after it was registered with the defaults (a body's proxy
            // registers before its entity states the flags).
            RenderingServer.InstanceSetLayerMask(silenced.Instance, 0u);
            return;
        }
        if (layers == 0 || !baseRid.IsValid)
        {
            return;
        }

        bool twoSided = (casting & Engine_ClassicShadows.CastsTwoSided) != 0;
        if (ByNode.TryGetValue(node.GetInstanceId(), out Entry? known))
        {
            known.Reach = node.HasMeta(ReachMeta) ? (float)node.GetMeta(ReachMeta).AsDouble() : 0f;
            known.TwoSided = twoSided;
            if (known.Layers != layers)
            {
                LeaveCell(known);
            }
            known.Layers = layers;
            RenderingServer.InstanceSetLayerMask(known.Instance, layers);
            Rebase(known, baseRid);
            return;
        }

        var entry = new Entry
        {
            Node = node,
            Reach = node.HasMeta(ReachMeta) ? (float)node.GetMeta(ReachMeta).AsDouble() : 0f,
            Dynamic = dynamic,
            TwoSided = twoSided,
            Stagger = Entries.Count & 31,
            Instance = RenderingServer.InstanceCreate(),
            Layers = layers,
        };
        RenderingServer.InstanceSetScenario(entry.Instance, World.Scenario);
        RenderingServer.InstanceSetLayerMask(entry.Instance, layers);
        RenderingServer.InstanceGeometrySetCastShadowsSetting(
            entry.Instance, RenderingServer.ShadowCastingSetting.Off);
        Rebase(entry, baseRid);
        Place(entry, node.GlobalTransform);

        Entries.Add(entry);
        ++Shown;  // a new server instance is visible until the cut says otherwise
        ByNode[node.GetInstanceId()] = entry;
        Hook(entry, node);
    }

    /// <summary>The node's signals onto its caster; apart from <see cref="Register"/> so only a new caster allocates the closures.</summary>
    private static void Hook(Entry entry, GeometryInstance3D node)
    {
        node.TreeExiting += () => Release(entry);
        entry.Visible = node.IsVisibleInTree();
        node.VisibilityChanged += () =>
        {
            entry.Visible = GodotObject.IsInstanceValid(node) && node.IsVisibleInTree();
            Touched.Add(entry);
        };
    }

    /// <summary>
    /// Follows the drawn nodes: dynamic ones every frame, the rest a slice at a time, and every
    /// caster's eye-distance cut. Main thread, once per frame.
    /// </summary>
    public static void Update(Vector3 eye)
    {
        _hasSecondEye = false;
        UpdateFrom(eye);
    }

    /// <summary>
    /// As <see cref="Update(Vector3)"/> for split screen: a caster within its reach of either eye
    /// casts, since both views' cascades draw this one scene.
    /// </summary>
    public static void Update(Vector3 eye, Vector3 secondEye)
    {
        _hasSecondEye = true;
        _secondEye = secondEye;
        UpdateFrom(eye);
    }

    private static bool _hasSecondEye;
    private static Vector3 _secondEye;

    private static void UpdateFrom(Vector3 eye)
    {
        if (Entries.Count == 0)
        {
            return;
        }
        ++_frame;

        long from = System.Diagnostics.Stopwatch.GetTimestamp();
        // A caster whose node was just shown or hidden follows now, not when its slice comes; a
        // dynamic one takes the full look it skipped while hidden.
        foreach (Entry touched in Touched)
        {
            if (touched.Instance.IsValid)
            {
                Follow(touched, eye, force: true);
            }
        }
        Touched.Clear();
        foreach (Entry entry in Entries)
        {
            // A hidden body casts nothing and is not followed until its node is shown again.
            if (entry.Dynamic && (entry.Visible || entry.Shown))
            {
                Follow(entry, eye);
            }
        }
        Refii.Core.Engine_FramePhases.Lap("classic.shadows.dynamic", ref from);

        int checks = System.Math.Min(SlicePerFrame, Entries.Count);
        for (int i = 0; i < checks; i++)
        {
            _cursor = (_cursor + 1) % Entries.Count;
            Entry entry = Entries[_cursor];
            if (!entry.Dynamic)
            {
                Follow(entry, eye);
            }
        }
        Refii.Core.Engine_FramePhases.Lap("classic.shadows.slice", ref from);

        Engine_ClassicCasterCells.Pump();
        Refii.Core.Engine_FramePhases.Lap("classic.shadows.cells", ref from);
    }

    /// <summary>
    /// What the cascades draw, for the debug channel: shown casters, the distinct (base,
    /// materials) shapes among them (Godot merges equal ones into one instanced draw), and how
    /// many carry a per-instance transfer pair, which merges with nothing.
    /// </summary>
    public static Dictionary<string, object?> Census()
    {
        var shapes = new Dictionary<long, int>();
        var bases = new Dictionary<ulong, int>();
        int shown = 0, transfer = 0, dynamic = 0, dynamicShown = 0, joined = 0;
        var outside = new SortedDictionary<string, int>();
        foreach (Entry entry in Entries)
        {
            dynamic += entry.Dynamic ? 1 : 0;
            dynamicShown += entry.Dynamic && entry.Shown ? 1 : 0;
            if (!entry.Shown || !GodotObject.IsInstanceValid(entry.Node))
            {
                continue;
            }
            if (!entry.Dynamic && entry.Cell is not { Built: true })
            {
                // Why a shown still caster draws on its own instead of in a cell.
                string why = entry.Cell is not null ? "awaiting build"
                    : entry.Node.MaterialOverride is not null ? "material override"
                    : entry.Node is MultiMeshInstance3D { Multimesh: { } mm } ? (Refii.Level.Engine_PropShadow.StatedTransforms(mm) is null ? "batch without record" : "batch moved or unjoinable")
                    : entry.Node is MeshInstance3D { Mesh: ArrayMesh m } mi
                        ? mi.GetSkinReference() is not null ? "skinned"
                        : !DrawsOwnMaterials(mi) ? "surface override"
                        : Engine_ClassicCasterMerge.CellGeometry(m, entry.TwoSided) is not { } shape ? "no joinable surface"
                        : shape.HasKept && entry.Merged is null ? "kept surfaces, no joined cast"
                        : "moved"
                    : "other";
                outside[why] = outside.GetValueOrDefault(why) + 1;
            }
            ++shown;
            joined += entry.Merged is not null ? 1 : 0;
            ulong drawnBase = entry.MergedBatch?.GetRid().Id ?? entry.Merged?.GetRid().Id ?? entry.Base.Id;
            bases[drawnBase] = bases.GetValueOrDefault(drawnBase) + 1;
            long key = (long)drawnBase * 31 + (long)(entry.Node.MaterialOverride?.GetInstanceId() ?? 0UL);
            bool ownPair = false;
            if (entry.Merged is null && entry.Node is MeshInstance3D mesh && mesh.Mesh is { } drawn)
            {
                for (int i = 0; i < drawn.GetSurfaceCount(); i++)
                {
                    key = key * 31 + (long)(DepthSurface(mesh, i)?.GetInstanceId() ?? 0UL);
                    ownPair |= Engine_ClassicTransfer.SurfaceOverride(mesh, i) is not null;
                }
            }
            if (ownPair)
            {
                ++transfer;
            }
            shapes[key] = shapes.GetValueOrDefault(key) + 1;
        }
        int single = 0, baseOnce = 0;
        foreach (int n in shapes.Values)
        {
            if (n == 1)
            {
                ++single;
            }
        }
        foreach (int n in bases.Values)
        {
            if (n == 1)
            {
                ++baseOnce;
            }
        }
        return new Dictionary<string, object?>
        {
            ["registered"] = Entries.Count,
            ["shown"] = shown,
            ["distinctShapes"] = shapes.Count,
            ["shapesDrawnOnce"] = single,
            ["withTransferPair"] = transfer,
            ["joined"] = joined,
            ["joinedSurfaces"] = Engine_ClassicCasterMerge.SurfacesJoined,
            ["dynamic"] = dynamic,
            ["dynamicShown"] = dynamicShown,
            ["distinctMeshes"] = bases.Count,
            ["meshesCastOnce"] = baseOnce,
            ["cells"] = Engine_ClassicCasterCells.CellCount,
            ["cellMembers"] = Engine_ClassicCasterCells.BuiltMembers,
            ["cellBuilds"] = Engine_ClassicCasterCells.Builds,
            ["outsideCells"] = outside,
        };
    }

    /// <summary>
    /// A static caster's drawn node was just moved (a twin its entity drives): placed now, not
    /// when the slice next comes round, so its shadow does not trail the body. Main thread.
    /// </summary>
    public static void Moved(Node3D node)
    {
        if (Entries.Count == 0)
        {
            return;
        }
        if (node is GeometryInstance3D drawn && ByNode.TryGetValue(drawn.GetInstanceId(), out Entry? entry) && !entry.Dynamic)
        {
            Place(entry, drawn.GlobalTransform);
        }
        foreach (Node child in node.GetChildren())
        {
            if (child is Node3D spatial)
            {
                Moved(spatial);
            }
        }
    }

    /// <summary>Frees every caster: a level change drops the drawn nodes with it.</summary>
    public static void Clear()
    {
        foreach (Entry entry in Entries)
        {
            if (entry.Instance.IsValid)
            {
                RenderingServer.FreeRid(entry.Instance);
            }
        }
        Entries.Clear();
        ByNode.Clear();
        Batched.Clear();
        Engine_ClassicCasterCells.Clear();
        Touched.Clear();
        _cursor = 0;
        Shown = 0;
    }

    private static void Follow(Entry entry, Vector3 eye, bool force = false)
    {
        GeometryInstance3D node = entry.Node;
        if (!GodotObject.IsInstanceValid(node))
        {
            return;
        }

        // A dynamic node moves every frame but swaps its mesh or skeleton rarely; the transform
        // and the cut are followed every frame. Leaving the tree releases the caster (TreeExiting), and a new mesh, skeleton or outfit
        // re-registers it (the entity feed, Engine_ShadowProxy.Sync): the periodic look is only
        // the safety net, every 32nd frame for a moving node.
        bool full = force || !entry.Dynamic || ((_frame + entry.Stagger) & 31) == 0 || entry.SurfacesPending || !entry.Base.IsValid;
        Rid baseRid = entry.Base;
        if (full)
        {
            baseRid = BaseOf(node);
            if (baseRid != entry.Base)
            {
                Rebase(entry, baseRid);
            }
            else if (entry.Dynamic && node is MeshInstance3D mesh &&
                (mesh.GetSkinReference()?.GetSkeleton() ?? default) != entry.Skeleton)
            {
                Rebase(entry, baseRid);
            }
            else if (entry.SurfacesPending)
            {
                Surfaces(entry);
            }
        }

        // Nothing the drawn node does not draw casts (a hidden prop, a twin its entity replaced,
        // a batched entity's node); such a node is not placed until it draws again.
        bool shown = entry.Visible && baseRid.IsValid;
        if (shown)
        {
            Transform3D world = node.GlobalTransform;
            if (world != entry.Placed || entry.CentreStale)
            {
                entry.StillFrames = 0;
                Place(entry, world);
            }
            else if (entry.Dynamic && entry.Cell is null && !entry.CellRefused && ++entry.StillFrames >= StillToJoin)
            {
                // An entity that has stood still this long (a barrel, a cart, furniture) casts from
                // its cell; its first move takes it out (Place).
                entry.CellRefused = !TryJoinCell(entry);
            }

            // The guest's cut: a hard one at the reach, from the eye to the drawn box's centre
            // (a batch's origin is not where its copies stand), kept from the last placement.
            shown = entry.Reach <= 0f || eye.DistanceSquaredTo(entry.Centre) <= entry.Reach * entry.Reach ||
                (_hasSecondEye && _secondEye.DistanceSquaredTo(entry.Centre) <= entry.Reach * entry.Reach);
        }
        if (shown != entry.Shown)
        {
            entry.Shown = shown;
            Shown += shown ? 1 : -1;
            if (entry.Cell is { } member)
            {
                Engine_ClassicCasterCells.Show(member, shown);
            }
            RenderingServer.InstanceSetVisible(entry.Instance, shown && entry.Cell is not { Built: true, HasKept: false });
        }
    }

    private static void Place(Entry entry, Transform3D world)
    {
        if (entry.Cell is { } member && world != member.World)
        {
            // A cell holds its members where they stood; a moved one casts on its own again.
            LeaveCell(entry);
        }
        if (entry.CentreStale)
        {
            entry.LocalCentre = Refii.Core.Engine_ServerSyncs.LocalAabb(entry.Node).GetCenter();
            entry.CentreStale = false;
        }
        entry.Placed = world;
        entry.Centre = world * entry.LocalCentre;
        RenderingServer.InstanceSetTransform(entry.Instance, world);
    }

    /// <summary>
    /// The drawn node's base, skeleton and whole-instance override onto the caster; a two-sided
    /// caster's materials as their both-faces copies (Engine_ClassicMaterial.TwoSidedCopy). A
    /// mesh's per-surface overrides follow on its next <see cref="Follow"/>: the server sizes an
    /// instance's surface slots only when it next updates the instance, after the base is set.
    /// </summary>
    private static void Rebase(Entry entry, Rid baseRid)
    {
        LeaveCell(entry);
        entry.Base = baseRid;
        entry.SurfacesPending = false;
        entry.CentreStale = true;
        GeometryInstance3D node = entry.Node;

        // A still, rigid prop drawing its mesh's own materials casts its joined mesh; a batch of
        // them casts a twin of the batch carrying it.
        entry.Merged = null;
        entry.MergedBatch = null;
        Rid cast = baseRid;
        if (baseRid.IsValid && node.MaterialOverride is null)
        {
            // Not cloth: its vertices are rewritten in place every frame, which a joined copy would never see.
            if (node is MeshInstance3D { Mesh: ArrayMesh joinable } single && node is not Refii.Physics.Engine_ClothBody)
            {
                // A body casts what it shows: its collapsed clusters are left out.
                entry.Merged = Engine_ClassicCasterMerge.For(joinable, entry.TwoSided, CollapsedOf(single));
                cast = entry.Merged?.GetRid() ?? baseRid;
            }
            else if (!entry.Dynamic && node is MultiMeshInstance3D { Multimesh: { Mesh: ArrayMesh batchMesh } source } &&
                Engine_ClassicCasterMerge.For(batchMesh, entry.TwoSided) is { } joined &&
                Refii.Level.Engine_PropShadow.ClassicTwin(source, joined) is { } twin)
            {
                entry.Merged = joined;
                entry.MergedBatch = twin;
                cast = twin.GetRid();
            }
        }
        bool same = cast == entry.Cast;
        entry.Cast = cast;
        if (!same)
        {
            RenderingServer.InstanceSetBase(entry.Instance, cast);
        }
        if (!baseRid.IsValid)
        {
            return;
        }
        entry.StillFrames = 0;
        entry.CellRefused = false;
        if (!entry.Dynamic && node is MeshInstance3D)
        {
            TryJoinCell(entry);
        }
        else if (!entry.Dynamic && node is MultiMeshInstance3D { Multimesh: { Mesh: ArrayMesh copied } batch } &&
            node.MaterialOverride is null && Refii.Level.Engine_PropShadow.StatedTransforms(batch) is { } built &&
            Engine_ClassicCasterMerge.CellGeometry(copied, entry.TwoSided) is { HasKept: false } batchShape)
        {
            // A batch joins whole, at its copies' built places; a later move of any copy takes it out.
            Transform3D world = node.GlobalTransform;
            entry.Cell = Engine_ClassicCasterCells.Join(entry.Instance, batchShape.Groups, default, world,
                (Transform3D[])built.Clone(), world * Refii.Core.Engine_ServerSyncs.LocalAabb(node).GetCenter(), entry.Layers, entry.Shown, World);
            Batched[batch.GetInstanceId()] = entry;
        }
        if (entry.Merged is not null)
        {
            // Its surfaces carry their own materials; nothing of the node's is laid over them.
            // A skinned body's joined mesh keeps its bone indices and rides its skeleton.
            Rid skeleton = node is MeshInstance3D skinned ? skinned.GetSkinReference()?.GetSkeleton() ?? default : default;
            if (!same || skeleton != entry.Skeleton)
            {
                RenderingServer.InstanceGeometrySetMaterialOverride(entry.Instance, default);
                RenderingServer.InstanceAttachSkeleton(entry.Instance, skeleton);
                entry.SurfacesPending = !same;
            }
            entry.Skeleton = skeleton;
            return;
        }

        Material? whole = node.MaterialOverride;
        if (whole is null && entry.TwoSided && node is MultiMeshInstance3D { Multimesh.Mesh: { } batched })
        {
            // A MultiMesh instance has no surface slots: a two-sided batch takes its copy whole
            // when every surface draws the same material.
            whole = SharedMaterial(batched);
        }
        if (whole is not null && entry.TwoSided)
        {
            whole = Engine_ClassicMaterial.TwoSidedCopy(whole);
        }
        RenderingServer.InstanceGeometrySetMaterialOverride(entry.Instance, whole?.GetRid() ?? default);
        if (node is MeshInstance3D mesh)
        {
            Rid skeleton = mesh.GetSkinReference()?.GetSkeleton() ?? default;
            entry.Skeleton = skeleton;
            RenderingServer.InstanceAttachSkeleton(entry.Instance, skeleton);
            entry.SurfacesPending = mesh.Mesh is { } drawn && drawn.GetSurfaceCount() > 0;
        }
    }

    /// <summary>A mesh caster's per-surface overrides: the drawn node's, or two-sided copies.</summary>
    private static void Surfaces(Entry entry)
    {
        entry.SurfacesPending = false;
        if (entry.MergedBatch is not null)
        {
            return;
        }
        if (entry.Merged is { } merged)
        {
            for (int i = 0; i < merged.GetSurfaceCount(); i++)
            {
                RenderingServer.InstanceSetSurfaceOverrideMaterial(entry.Instance, i, default);
            }
            return;
        }
        if (entry.Node is not MeshInstance3D node || node.Mesh is not { } mesh)
        {
            return;
        }
        bool copies = entry.TwoSided && node.MaterialOverride is null;
        int surfaces = mesh.GetSurfaceCount();
        for (int i = 0; i < surfaces; i++)
        {
            Material? surface = DepthSurface(node, i);
            if (copies)
            {
                surface ??= mesh.SurfaceGetMaterial(i);
                surface = surface is null ? null : Engine_ClassicMaterial.TwoSidedCopy(surface);
            }
            RenderingServer.InstanceSetSurfaceOverrideMaterial(entry.Instance, i, surface?.GetRid() ?? default);
        }
    }

    /// <summary>Whether every surface of <paramref name="node"/> casts as its mesh's own material would (<see cref="DepthSurface"/>).</summary>
    private static bool DrawsOwnMaterials(MeshInstance3D node)
    {
        int surfaces = node.Mesh?.GetSurfaceCount() ?? 0;
        for (int i = 0; i < surfaces; i++)
        {
            if (DepthSurface(node, i) is not null)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Puts a single model's caster in its cell when it can share one: rigid, drawing its mesh's
    /// own materials, not cloth. Surfaces that cannot join stay on the own instance as a mesh of
    /// their own; that swap needs every surface override of the own instance empty, which holds
    /// over a joined cast (its surfaces carry their materials) and over the source drawn
    /// one-sided (<see cref="DrawsOwnMaterials"/>), not over two-sided copies. False when it cannot.
    /// </summary>
    private static bool TryJoinCell(Entry entry)
    {
        GeometryInstance3D node = entry.Node;
        if (entry.Cell is not null || node is not MeshInstance3D { Mesh: ArrayMesh still } single ||
            node is Refii.Physics.Engine_ClothBody || node.MaterialOverride is not null ||
            single.GetSkinReference() is not null || !DrawsOwnMaterials(single) ||
            Engine_ClassicCasterMerge.CellGeometry(still, entry.TwoSided) is not { } shape ||
            (shape.HasKept && entry.Merged is null && entry.TwoSided))
        {
            return false;
        }

        Rid kept = shape.HasKept ? Engine_ClassicCasterMerge.KeptOnly(still, entry.TwoSided, shape).GetRid() : default;
        Transform3D world = node.GlobalTransform;
        entry.Cell = Engine_ClassicCasterCells.Join(entry.Instance, shape.Groups, kept, world, null,
            world * still.GetAabb().GetCenter(), entry.Layers, entry.Shown, World);
        return true;
    }

    /// <summary>Frames an entity's caster holds still before it joins a cell; a move takes it out again.</summary>
    private const int StillToJoin = 90;

    /// <summary>Batch casters in a cell, by their MultiMesh: told when a copy moves (<see cref="BatchChanged"/>).</summary>
    private static readonly Dictionary<ulong, Entry> Batched = new();

    /// <summary>
    /// A copy of <paramref name="batch"/> was moved or hidden after the build (Engine_PropShadow,
    /// where every such write goes): its cell holds the built places, so the batch casts on its own again.
    /// </summary>
    public static void BatchChanged(MultiMesh batch)
    {
        if (Batched.Count != 0 && Batched.Remove(batch.GetInstanceId(), out Entry? entry))
        {
            LeaveCell(entry);
        }
    }

    /// <summary>Takes the caster out of its cell; its own instance draws again if it is shown.</summary>
    private static void LeaveCell(Entry entry)
    {
        if (entry.Cell is not { } member)
        {
            return;
        }
        bool built = member.Built;
        Engine_ClassicCasterCells.Leave(member);
        entry.Cell = null;
        if (built && entry.Instance.IsValid)
        {
            if (member.HasKept)
            {
                RenderingServer.InstanceSetBase(entry.Instance, entry.Cast);
            }
            RenderingServer.InstanceSetVisible(entry.Instance, entry.Shown);
        }
    }

    /// <summary>The one material every surface of <paramref name="mesh"/> draws, or null.</summary>
    private static Material? SharedMaterial(Mesh mesh)
    {
        Material? shared = null;
        for (int i = 0; i < mesh.GetSurfaceCount(); i++)
        {
            Material? surface = mesh.SurfaceGetMaterial(i);
            if (surface is null || (shared is not null && !ReferenceEquals(shared, surface)))
            {
                return null;
            }
            shared = surface;
        }
        return shared;
    }

    /// <summary>The node's mesh or multimesh RID: the instance's base, read without a resource wrapper.</summary>
    private static Rid BaseOf(GeometryInstance3D node) =>
        node is MeshInstance3D or MultiMeshInstance3D ? node.GetBase() : default;

    /// <summary>The surfaces the hidden-cluster pass has collapsed on <paramref name="node"/>, as a mask over its mesh.</summary>
    private static System.UInt128 CollapsedOf(MeshInstance3D node)
    {
        System.UInt128 mask = System.UInt128.Zero;
        int surfaces = System.Math.Min(node.Mesh?.GetSurfaceCount() ?? 0, 128);
        for (int i = 0; i < surfaces; i++)
        {
            if (Refii.Entities.Engine_HiddenClusters.IsCollapsed(node.GetSurfaceOverrideMaterial(i)))
            {
                mask |= System.UInt128.One << i;
            }
        }
        return mask;
    }

    /// <summary>
    /// A surface's material for the cascades: a classic variant of the surface's own material
    /// (an ambient pair, a transfer pair, a light group) differs from it only in lighting, which
    /// a depth pass never runs, so the mesh's own shared material stands in (null); anything
    /// else (a collapsed surface, a non-classic material) is kept.
    /// </summary>
    private static Material? DepthSurface(MeshInstance3D node, int surface)
    {
        Material? drawn = node.GetSurfaceOverrideMaterial(surface);
        return drawn is ShaderMaterial classic && classic.HasMeta(Engine_ClassicMaterial.ClassicMeta) &&
            !Refii.Entities.Engine_HiddenClusters.IsCollapsed(classic)
            ? null
            : drawn;
    }

    private static void Release(Entry entry)
    {
        LeaveCell(entry);
        if (entry.Instance.IsValid)
        {
            RenderingServer.FreeRid(entry.Instance);
            entry.Instance = default;
        }
        if (entry.Shown)
        {
            entry.Shown = false;
            --Shown;
        }
        Entries.Remove(entry);
        ByNode.Remove(entry.Node.GetInstanceId());
        if (_cursor >= Entries.Count)
        {
            _cursor = 0;
        }
    }
}
