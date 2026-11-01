using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace Refii.Physics;

/// <summary>
/// The cloth of one drawn entity: a child of the entity's mesh node that draws the cloth
/// surfaces itself and steps them the way the guest's cloth proxy does, once per rendered frame.
///
/// The contract, all of it the guest's:
/// - dt is the frame time clamped to 0.2 s and divided by ceil(dt * 20) — ONE step of at most
///   50 ms per frame, never a loop (<c>sub_821E3180</c>; the substep count is computed and
///   never iterated).
/// - The owner's world matrix is the transform; a jump of more than one unit resets the cloth
///   to the skinned pose (<c>sub_8218D9A0</c>).
/// - The skin palette is the rig's pose times inverse bind, in the model's space, in the
///   cloth block's own bone ids (<c>sub_8222E2B8</c>); colliders pose off the same bones.
/// - Wind is the level's weather wind, direction times speed; gravity is world down.
/// - Beyond MaxClothDistance, or out of the camera's view, nothing is simulated and the
///   skinned pose is drawn; inside the last 15% of that distance the drawn pose is lerped
///   between skinned and simulated (<c>sub_821E34B0</c>).
/// - The cloth normals are rebuilt from the cloth's own triangle list after each step and are
///   what the next step's wind term reads (<c>sub_822BA0B0</c>).
///
/// The guest solved on its own hardware thread while the render thread built the frame; here
/// the solve runs as a task started at the end of one frame's process and collected at the
/// start of the next, so a frame of latency buys the main thread back. What stays on the main
/// thread is the rig read, the frustum test and one in-place vertex write per surface. A prop
/// whose cloth has come to rest sleeps until its transform or the wind changes.
///
/// The parent's copies of the driven surfaces are hidden through <see cref="Refii.Entities.Engine_HiddenClusters"/>,
/// which owns the surface hiding for the body; this node draws them instead.
/// </summary>
[Tool]
[GlobalClass]
public partial class Engine_ClothBody : MeshInstance3D
{
    public const string NodeName = "ClothBody";

    /// <summary>The guest's config default for "MaxClothDistance" (<c>flt_83317D74</c>).</summary>
    public static float MaxClothDistance = 20.0f;

    /// <summary><c>flt_8209D568</c>: the fade begins at this fraction of the distance.</summary>
    public const float FadeStart = 0.85f;

    /// <summary><c>flt_83317E84</c>: the frame dt is clamped here before stepping.</summary>
    public const float FrameDtClamp = 0.2f;

    /// <summary><c>flt_8209F1C8</c>: substeps per second the step is divided by.</summary>
    public const float SubstepRate = 20.0f;

    /// <summary>A root move of more than this, squared, resets the cloth to its skinned pose.</summary>
    public const float TeleportResetDistanceSquared = 1.0f;

    /// <summary>Beyond the distance the skinned pose is refreshed on this cadence, not every frame.</summary>
    public const int FarRefreshFrames = 4;

    /// <summary>A prop whose vertices all moved less than this, squared, for <see cref="SleepAfterSteps"/> steps sleeps.</summary>
    public const float SleepMoveSquared = 1.0e-8f;

    public const int SleepAfterSteps = 60;

    /// <summary>A transform change larger than this wakes a sleeping prop.</summary>
    public const float WakeMoveSquared = 1.0e-8f;

    public static readonly PhysVec4 GravityDown = new(0.0f, -9.8f, 0.0f, 0.0f);

    private static readonly List<WeakReference<Engine_ClothBody>> Registry = new();

    private static long _solveTicks;

    private static long _uploadTicks;

    private static long _mainTicks;

    private static int _stepped;

    private static int _slept;

    private static int _frames;

    private readonly List<ClothInstance> _instances = new();

    private readonly List<ClothPiece> _pieces = new();

    private readonly List<PhysVec4[]> _drawn = new();

    private readonly List<Transform3D[]> _inverseRest = new();

    private readonly List<int[]> _usedBones = new();

    private ClothPatch? _patch;

    private ArrayMesh? _source;

    private Skeleton3D? _skeleton;

    private Task? _job;

    /// <summary><see cref="Solve"/> as a delegate made once: a method group is a new one per call.</summary>
    private Action? _solve;

    private float _jobDt;

    private bool _jobStepped;

    private int _farFrames;

    private bool _sleeping;

    private PhysMat4 _sleptAt;

    private Vector3 _sleptWind;

    private UInt128 _appliedCollapse;

    private long _lastSolveTicks;

    private long _lastUploadTicks;

    private Aabb _bounds;

    [Export]
    public bool Simulate { get; set; } = true;

    public IReadOnlyList<ClothInstance> Instances => _instances;

    public int PieceCount => _instances.Count;

    public UInt128 DrivenMask => _patch?.DrivenMask ?? UInt128.Zero;

    /// <summary>Whether the parent's mesh is one this body was bound to.</summary>
    public bool Bound(Mesh? mesh) => _source is not null && ReferenceEquals(_source, mesh);

    /// <summary>The body under <paramref name="node"/>, or null.</summary>
    public static Engine_ClothBody? Of(Node node) =>
        node.GetNodeOrNull<Engine_ClothBody>(NodeName);

    /// <summary>
    /// Gives <paramref name="node"/> a cloth body for <paramref name="mesh"/> when any of the
    /// models behind it carries a cloth block that joins to the mesh; replaces one bound to a
    /// different mesh; removes one when nothing joins. Returns the body or null. The parent's
    /// driven surfaces are hidden here, synchronously, so a cluster pass that follows sees them.
    /// </summary>
    public static Engine_ClothBody? Attach(MeshInstance3D node, Mesh? mesh, IReadOnlyList<string> modelPaths)
    {
        Engine_ClothBody? existing = Of(node);
        if (existing is not null && existing.Bound(mesh))
        {
            return existing;
        }

        Detach(node);

        if (mesh is not ArrayMesh source)
        {
            return null;
        }

        // A morphed model carries no cloth definition in the guest (its record copies drop the
        // pointer), so its garments draw GPU-skinned; the same here.
        if (source.HasMeta(Refii.Hero.Engine_HeroBodyMorph.MorphedMeta))
        {
            return null;
        }

        bool any = false;
        foreach (string path in modelPaths)
        {
            if (Engine_PhysicsIndex.HasCloth(path))
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            return null;
        }

        List<ClothPiece> pieces = Engine_ClothPieces.ForMesh(
            modelPaths, source, Refii.Level.Engine_PropStreamer.Resolve);
        if (pieces.Count == 0)
        {
            return null;
        }

        ClothPatch? patch = ClothPatch.Capture(pieces);
        if (patch is null)
        {
            return null;
        }

        bool rigged = Refii.Models.Engine_MdlRig.TryFrom(source) is not null;
        var body = new Engine_ClothBody
        {
            Name = NodeName,
            Layers = node.Layers,
            CastShadow = rigged ? GeometryInstance3D.ShadowCastingSetting.Off : node.CastShadow,
            GIMode = node.GIMode,
            VisibilityRangeBegin = node.VisibilityRangeBegin,
            VisibilityRangeEnd = node.VisibilityRangeEnd,
            VisibilityRangeBeginMargin = node.VisibilityRangeBeginMargin,
            VisibilityRangeEndMargin = node.VisibilityRangeEndMargin,
            VisibilityRangeFadeMode = node.VisibilityRangeFadeMode,
        };

        body.Bind(source, pieces, patch);
        node.AddChild(body);
        body.Owner = node.Owner ?? node;
        if (!rigged)
        {
            Refii.Lighting.Engine_ShadowReach.Adopt(body);
        }
        // The body's tint and fade as they stand; later changes arrive with the body's look.
        Refii.Entities.Engine_EntityAppearance.MirrorToCloth(node);
        // Under classic the cloth casts into the guest's cascades with the body it is part of.
        Refii.Models.Engine_ClassicCasters.Register(body, dynamic: true);

        Refii.Entities.Engine_HiddenClusters.SetClothSurfaces(node, patch.DrivenMask);
        return body;
    }

    public static void Detach(MeshInstance3D node)
    {
        Engine_ClothBody? body = Of(node);
        if (body is null)
        {
            return;
        }

        Refii.Entities.Engine_HiddenClusters.SetClothSurfaces(node, UInt128.Zero);
        body.Collect();
        node.RemoveChild(body);
        body.QueueFree();
    }

    private void Bind(ArrayMesh source, List<ClothPiece> pieces, ClothPatch patch)
    {
        _source = source;
        _patch = patch;
        _pieces.Clear();
        _instances.Clear();
        _drawn.Clear();
        _inverseRest.Clear();
        _usedBones.Clear();

        foreach (ClothPiece piece in pieces)
        {
            ClothInstance instance = ClothInstance.Create();
            instance.GravityDirWorld = GravityDown;
            instance.Bind(piece.Asset);

            _pieces.Add(piece);
            _instances.Add(instance);
            _drawn.Add(new PhysVec4[piece.Asset.VertexCount]);
            _inverseRest.Add(Array.Empty<Transform3D>());
            _usedBones.Add(UsedBones(piece.Asset));
        }

        Mesh = patch.Built;
        PackAll();
        _patch.Upload();
        ApplyBounds();

        GD.Print($"[physics] {source.ResourceName}: {_instances.Count} cloth piece(s) bound, " +
            $"standing in for {PopCount(patch.DrivenMask)} body surface(s)");
    }

    /// <summary>The distinct bone ids a piece's driver and colliders name, so the palette reads only those.</summary>
    private static int[] UsedBones(ClothAsset asset)
    {
        var used = new HashSet<int>();
        if (asset.Driver is not null)
        {
            foreach (byte id in asset.Driver.BoneIndices)
            {
                used.Add(id);
            }
        }

        foreach (SphereCollider c in asset.Spheres)
        {
            if (c.BoneIndex >= 0) used.Add(c.BoneIndex);
        }

        foreach (PlaneCollider c in asset.Planes)
        {
            if (c.BoneIndex >= 0) used.Add(c.BoneIndex);
        }

        foreach (CapsuleCollider c in asset.Capsules)
        {
            if (c.BoneIndex >= 0) used.Add(c.BoneIndex);
        }

        foreach (BoxCollider c in asset.Boxes)
        {
            if (c.BoneIndex >= 0) used.Add(c.BoneIndex);
        }

        var list = new List<int>(used);
        list.Sort();
        return list.ToArray();
    }

    private static bool _hooked;

    public override void _Ready()
    {
        lock (Registry)
        {
            Registry.Add(new WeakReference<Engine_ClothBody>(this));
            if (!_hooked)
            {
                // Every body's solve lands BEFORE the frame draws, the way the guest's render
                // thread waited on its worker mid-frame. Collected a frame later, the pinned
                // seam wore last frame's pose beside a top skinned with this frame's, and the
                // skirt tore away from the coat whenever the hero moved.
                _hooked = true;
                RenderingServer.FramePreDraw += LandAll;
            }
        }

        SetProcess(true);
    }

    private static void LandAll()
    {
        lock (Registry)
        {
            foreach (WeakReference<Engine_ClothBody> reference in Registry)
            {
                if (reference.TryGetTarget(out Engine_ClothBody? body) && GodotObject.IsInstanceValid(body))
                {
                    body.Land();
                }
            }
        }
    }

    private static int _lagged;

    /// <summary>
    /// Writes the solve to the surface before the frame draws so cloth never lags one frame
    /// behind the character's skeleton and world transform. Main thread only.
    /// </summary>
    private void Land()
    {
        if (_job is null || _patch is null)
        {
            return;
        }

        if (!_job.IsCompleted)
        {
            System.Threading.Interlocked.Increment(ref _lagged);
        }

        Collect();
        long uploadFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        _patch.Upload();
        ApplyBounds();
        SyncCollapse();
        _lastUploadTicks = System.Diagnostics.Stopwatch.GetTimestamp() - uploadFrom;
        System.Threading.Interlocked.Add(ref _uploadTicks, _lastUploadTicks);
    }

    public override void _ExitTree()
    {
        Collect();
        lock (Registry)
        {
            Registry.RemoveAll(r => !r.TryGetTarget(out Engine_ClothBody? b) || ReferenceEquals(b, this));
        }
    }

    /// <summary>Waits for a running solve; the body's state is then quiescent.</summary>
    private void Collect()
    {
        if (_job is null)
        {
            return;
        }

        try
        {
            _job.Wait();
        }
        catch (AggregateException e)
        {
            GD.PushError($"[physics] cloth solve failed: {e.InnerException?.Message}");
        }

        _job = null;
    }

    public override void _Process(double delta)
    {
        long mainFrom = System.Diagnostics.Stopwatch.GetTimestamp();

        if (_patch is null || _instances.Count == 0 || GetParent() is not MeshInstance3D parent)
        {
            Collect();
            return;
        }

        // A solve normally lands in LandAll before the draw; one still pending here (a frame
        // that drew nothing) lands now. One still RUNNING owns this body's arrays: no new step
        // until it is done.
        Land();
        if (_job is not null)
        {
            return;
        }

        if (!Bound(Refii.Entities.Engine_BodyVariants.SourceMesh(parent)))
        {
            // The entity changed model; the owner re-attaches on that path. Draw nothing stale.
            Visible = false;
            return;
        }

        Visible = parent.IsVisibleInTree();
        if (!Visible)
        {
            return;
        }

        EnsureSkeleton(parent);
        bool inView = true;
        float weight = Simulate ? FadeWeight(parent, out inView) : 0.0f;
        bool step = weight > 0.0f;

        if (!step)
        {
            // Unrigged props (banners, bunting, cobwebs) have no skeleton to follow when out of
            // cloth range or off-screen, and rigged characters behind the camera need no upload.
            if (_skeleton is null || !inView)
            {
                System.Threading.Interlocked.Increment(ref _slept);
                return;
            }

            // The guest draws the GPU-skinned submesh instead; ours refreshes the skinned pose
            // on a cadence so a far character still wears its coat in the right place.
            if (++_farFrames < FarRefreshFrames)
            {
                return;
            }

            _farFrames = 0;
        }

        PhysMat4 world = Engine_PhysicsMatrix.FromTransform3D(parent.GlobalTransform);
        Vector3 wind = Refii.Level.Engine_Weather.WindWorld;
        if (step && wind.LengthSquared() > 1e-4f && HasWindResponse())
        {
            float t = (float)(Time.GetTicksMsec() * 0.001);
            Vector3 p = parent.GlobalPosition;
            float phase = (p.X * 0.17f) + (p.Z * 0.23f);
            float gust = 1.0f + (0.35f * MathF.Sin((t * 2.3f) + phase) * MathF.Cos((t * 1.1f) + (phase * 0.7f)));
            wind *= gust;
        }

        if (_sleeping)
        {
            if (!Woken(world, wind))
            {
                System.Threading.Interlocked.Increment(ref _slept);
                return;
            }

            _sleeping = false;
        }

        // dt = min(frame, 0.2) / max(1, ceil(dt * 20)), one step (sub_821E3180).
        float dt = Math.Clamp((float)delta, 0.0f, FrameDtClamp);
        float parts = MathF.Max(1.0f, MathF.Ceiling(dt * SubstepRate));
        float subDt = dt / parts;

        for (int i = 0; i < _instances.Count; i++)
        {
            ClothInstance inst = _instances[i];
            inst.LodWeight = weight;
            inst.WindDirWorld = PhysVec4.FromVector3(wind, 0.0f);
            inst.XformPrev = inst.XformCur;
            inst.XformCur = world;
            FillPalette(i);
        }

        _jobDt = step ? subDt : 0.0f;
        _jobStepped = step;
        _job = Task.Run(_solve ??= Solve);
        System.Threading.Interlocked.Increment(ref _stepped);
        System.Threading.Interlocked.Add(ref _mainTicks, System.Diagnostics.Stopwatch.GetTimestamp() - mainFrom);
    }

    private bool HasWindResponse()
    {
        for (int i = 0; i < _instances.Count; i++)
        {
            if (_instances[i].Asset is { WindStrength: > 0.0f })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The whole step for every piece, on a worker: pure C# over this body's own arrays.</summary>
    private void Solve()
    {
        long from = System.Diagnostics.Stopwatch.GetTimestamp();
        long allocFrom = System.GC.GetAllocatedBytesForCurrentThread();
        bool still = _jobStepped && _skeleton is null;

        for (int i = 0; i < _instances.Count; i++)
        {
            ClothInstance inst = _instances[i];

            PhysVec4 moved = PhysVec4.Sub(inst.XformCur.R3, inst.XformPrev.R3);
            if (PhysVec4.MSum3(moved, moved).X > TeleportResetDistanceSquared)
            {
                inst.ResetToSkinned();
            }

            Engine_ClothSolver.TransformCollidersToWorld(inst);
            Engine_ClothSolver.SimulateInstance(inst, _jobDt);

            int collided = 0;
            foreach (PhysVec4 flag in inst.CollidedFlags)
            {
                if (flag.X != 0.0f)
                {
                    collided++;
                }
            }

            inst.CollidedVertices = collided;

            PhysVec4[] drawn = _drawn[i];
            Engine_ClothSolver.DrawnPositions(inst, drawn);
            Engine_ClothSolver.ComputeNormals(inst, drawn);
            Engine_ClothSolver.ComputeTangents(inst, drawn);
            _patch!.Pack(i, drawn, inst.VertexNormals, inst.Tangents);

            if (inst.LastMoveSquared < SleepMoveSquared)
            {
                inst.StillSteps++;
            }
            else
            {
                inst.StillSteps = 0;
            }

            still &= inst.StillSteps >= SleepAfterSteps;
        }

        if (still)
        {
            _sleeping = true;
            _sleptAt = _instances[0].XformCur;
            _sleptWind = new Vector3(_instances[0].WindDirWorld.X, _instances[0].WindDirWorld.Y, _instances[0].WindDirWorld.Z);
        }

        _lastSolveTicks = System.Diagnostics.Stopwatch.GetTimestamp() - from;
        System.Threading.Interlocked.Add(ref _solveTicks, _lastSolveTicks);
        Refii.Core.Engine_GcFrames.Note(ref Refii.Core.Engine_GcFrames.ClothBytes,
            System.GC.GetAllocatedBytesForCurrentThread() - allocFrom);
    }

    private bool Woken(in PhysMat4 world, Vector3 wind)
    {
        if (wind.DistanceSquaredTo(_sleptWind) > WakeMoveSquared)
        {
            return true;
        }

        for (int row = 0; row < 4; row++)
        {
            PhysVec4 d = PhysVec4.Sub(world[row], _sleptAt[row]);
            if (PhysVec4.MSum4(d, d).X > WakeMoveSquared)
            {
                return true;
            }
        }

        return false;
    }

    private void PackAll()
    {
        for (int i = 0; i < _instances.Count; i++)
        {
            ClothInstance inst = _instances[i];
            PhysVec4[] drawn = _drawn[i];
            Engine_ClothSolver.DrawnPositions(inst, drawn);
            Engine_ClothSolver.ComputeNormals(inst, drawn);
            Engine_ClothSolver.ComputeTangents(inst, drawn);
            _patch!.Pack(i, drawn, inst.VertexNormals, inst.Tangents);
        }
    }

    /// <summary>
    /// An in-place vertex write does not move the mesh's bounds, so the instance's are set
    /// from the solver's own AABB of every piece, in the model's space.
    /// </summary>
    private void ApplyBounds()
    {
        bool any = false;
        var min = Vector3.Zero;
        var max = Vector3.Zero;
        foreach (ClothInstance inst in _instances)
        {
            var lo = new Vector3(inst.BoundsMin.X, inst.BoundsMin.Y, inst.BoundsMin.Z);
            var hi = new Vector3(inst.BoundsMax.X, inst.BoundsMax.Y, inst.BoundsMax.Z);
            if (!lo.IsFinite() || !hi.IsFinite())
            {
                continue;
            }

            if (!any)
            {
                min = lo;
                max = hi;
                any = true;
                continue;
            }

            min = min.Min(lo);
            max = max.Max(hi);
        }

        if (!any)
        {
            return;
        }

        var bounds = new Aabb(min, (max - min).Max(new Vector3(0.01f, 0.01f, 0.01f)));
        if (_bounds.Size != Vector3.Zero && _bounds.Encloses(bounds))
        {
            return;
        }

        _bounds = bounds.Grow(0.25f);
        CustomAabb = _bounds;
    }

    /// <summary>Each view's frustum, read once per drawn frame (a view is a slot of Engine_Views.Cameras).</summary>
    private static readonly ulong[] _frustumFrame = { ulong.MaxValue, ulong.MaxValue };
    private static readonly ulong[] _frustumCamera = new ulong[2];
    private static readonly Plane[][] _frustum = { Array.Empty<Plane>(), Array.Empty<Plane>() };

    private static Plane[] FrameFrustum(Camera3D camera, int view)
    {
        ulong frame = Engine.GetProcessFrames();
        ulong id = camera.GetInstanceId();
        if (frame != _frustumFrame[view] || id != _frustumCamera[view])
        {
            Godot.Collections.Array<Plane> planes = camera.GetFrustum();
            if (_frustum[view].Length != planes.Count) _frustum[view] = new Plane[planes.Count];
            for (int i = 0; i < planes.Count; i++) _frustum[view][i] = planes[i];
            _frustumFrame[view] = frame;
            _frustumCamera[view] = id;
        }

        return _frustum[view];
    }

    /// <summary>
    /// <c>sub_821E34B0</c>: 0 beyond the distance or out of view, 1 inside 85% of it, a ramp
    /// between. Every view counts: in view is in any view's frustum, distance is to the nearest eye.
    /// </summary>
    private float FadeWeight(MeshInstance3D parent, out bool inView)
    {
        inView = true;
        IReadOnlyList<Camera3D> cameras = Refii.Core.Engine_Views.Cameras(GetViewport()?.GetCamera3D());
        if (cameras.Count == 0)
        {
            return 1.0f;
        }

        float max = MaxClothDistance;
        Vector3 origin = parent.GlobalPosition;
        float distance = float.MaxValue;
        int views = Math.Min(cameras.Count, _frustum.Length);
        for (int v = 0; v < views; v++)
        {
            distance = MathF.Min(distance, origin.DistanceTo(cameras[v].GlobalPosition));
        }

        if (distance >= max + 6.0f)
        {
            inView = false;
            return 0.0f;
        }

        inView = InView(cameras, views, parent, out float pieceDistance);
        if (pieceDistance < distance)
        {
            distance = pieceDistance;
        }

        if (!inView || distance >= max)
        {
            return 0.0f;
        }

        float start = max * FadeStart;
        return distance <= start ? 1.0f : (max - distance) / (max - start);
    }

    /// <summary>
    /// The union of the pieces' bounding spheres against every view's frustum; the distance is
    /// the nearest piece's to the nearest eye.
    /// </summary>
    private bool InView(IReadOnlyList<Camera3D> cameras, int views, MeshInstance3D parent, out float minDistance)
    {
        minDistance = float.MaxValue;
        Transform3D world = parent.GlobalTransform;
        float scale = world.Basis.Scale.Length() * 0.577350269f;
        bool anyVisible = false;
        for (int v = 0; v < views; v++)
        {
            Camera3D camera = cameras[v];
            Vector3 eye = camera.GlobalPosition;
            Plane[] planes = FrameFrustum(camera, v);
            foreach (ClothInstance inst in _instances)
            {
                PhysVec4 sphere = inst.BoundsSphere;
                Vector3 center = world * new Vector3(sphere.X, sphere.Y, sphere.Z);
                float radius = MathF.Max(sphere.W * scale, 0.5f);
                float d = MathF.Max(0.0f, center.DistanceTo(eye) - radius);
                if (d < minDistance)
                {
                    minDistance = d;
                }

                bool inside = true;
                for (int p = 0; p < planes.Length; p++)
                {
                    if (planes[p].DistanceTo(center) > radius)
                    {
                        inside = false;
                        break;
                    }
                }

                if (inside)
                {
                    anyVisible = true;
                }
            }
        }

        return anyVisible;
    }

    private bool _skeletonChecked;

    private void EnsureSkeleton(MeshInstance3D parent)
    {
        if (_skeleton is not null && IsInstanceValid(_skeleton))
        {
            return;
        }

        if (_skeletonChecked && _skeleton is null && (Refii.Core.Engine_FramePhases.FrameIndex & 31) != 0)
        {
            return;
        }

        _skeletonChecked = true;
        using NodePath skeletonPath = parent.Skeleton;
        Skeleton3D? skeleton = skeletonPath.IsEmpty
            ? null
            : parent.GetNodeOrNull<Skeleton3D>(skeletonPath);

        if (ReferenceEquals(skeleton, _skeleton))
        {
            return;
        }

        _skeleton = skeleton;
        _skin = parent.Skin;
        for (int i = 0; i < _inverseRest.Count; i++)
        {
            _inverseRest[i] = Array.Empty<Transform3D>();
        }
    }

    /// <summary>
    /// The body's skin: its bind poses carry each bone's live scale (the morph), which the
    /// skeleton does not — see Engine_EntitySkeleton.SetBoneScales. Read per frame, because the
    /// binds are rewritten in place when the scale changes.
    /// </summary>
    private Skin? _skin;

    /// <summary>
    /// The guest's palette (<c>sub_8222E2B8</c>): pose × inverse bind per bone, in the cloth's
    /// bone ids. The bind is the skeleton's global rest, which is what the mesh's skin was made
    /// from (<c>CreateSkinFromRestTransforms</c>), so the cloth and the body skin agree. Only
    /// the bones the piece names are read from the rig.
    /// </summary>
    private void FillPalette(int index)
    {
        ClothInstance inst = _instances[index];
        ClothPiece piece = _pieces[index];
        Skeleton3D? skeleton = _skeleton;
        int[] used = _usedBones[index];

        if (skeleton is null || used.Length == 0)
        {
            inst.BoneMatrices = Array.Empty<PhysVec4>();
            inst.BonePoses = Array.Empty<PhysMat4>();
            return;
        }

        int bones = used[^1] + 1;
        if (inst.BonePoses.Length != bones)
        {
            inst.BonePoses = new PhysMat4[bones];
            inst.BoneMatrices = new PhysVec4[bones * 3];
            for (int b = 0; b < bones; b++)
            {
                Engine_PhysicsMatrix.LoadIdentity(ref inst.BonePoses[b]);
                Engine_PhysicsMatrix.SkinRows(
                    inst.BonePoses[b], out inst.BoneMatrices[b * 3], out inst.BoneMatrices[(b * 3) + 1], out inst.BoneMatrices[(b * 3) + 2]);
            }
        }

        Transform3D[] inverseRest = _inverseRest[index];
        if (inverseRest.Length != bones)
        {
            inverseRest = new Transform3D[bones];
            foreach (int b in used)
            {
                int bone = SkeletonBone(piece, b, skeleton);
                inverseRest[b] = bone >= 0
                    ? skeleton.GetBoneGlobalRest(bone).AffineInverse()
                    : Transform3D.Identity;
            }

            _inverseRest[index] = inverseRest;
        }

        // Two matrices per bone, as the guest keeps them: the colliders pose off the bone's own
        // model-space pose (owner+512, `sub_82A94A18`), their file values being bone-local; the
        // skin rows are that pose times the inverse bind (owner+628, `sub_8222E2B8`). Feeding
        // the skin matrix to the colliders put every capsule off its limb by the inverse rest.
        Skin? binds = _skin;
        int bindCount = binds?.GetBindCount() ?? 0;
        foreach (int b in used)
        {
            int bone = SkeletonBone(piece, b, skeleton);
            Transform3D pose = bone >= 0 ? skeleton.GetBoneGlobalPose(bone) : Transform3D.Identity;
            // The body's own bind, scale included, so the cloth skins exactly as the body does.
            Transform3D bind = bone >= 0 && bone < bindCount ? binds!.GetBindPose(bone) : inverseRest[b];
            Transform3D skin = bone >= 0 ? pose * bind : Transform3D.Identity;

            inst.BonePoses[b] = Engine_PhysicsMatrix.FromTransform3D(pose);
            Engine_PhysicsMatrix.SkinRows(
                Engine_PhysicsMatrix.FromTransform3D(skin),
                out inst.BoneMatrices[b * 3], out inst.BoneMatrices[(b * 3) + 1], out inst.BoneMatrices[(b * 3) + 2]);
        }
    }

    private static int SkeletonBone(ClothPiece piece, int clothBone, Skeleton3D skeleton)
    {
        int[] remap = piece.BoneRemap;
        int bone = clothBone < remap.Length ? remap[clothBone] : clothBone;
        return bone >= 0 && bone < skeleton.GetBoneCount() ? bone : -1;
    }

    /// <summary>A piece whose surfaces a garment hides on the body is hidden here too.</summary>
    private void SyncCollapse()
    {
        if (_patch is null || GetParent() is not MeshInstance3D parent)
        {
            return;
        }

        UInt128 collapsed = Refii.Entities.Engine_HiddenClusters.ClusterMask(parent);
        if (collapsed == _appliedCollapse)
        {
            return;
        }

        for (int s = 0; s < _patch.SurfaceCount && s < _pieces.Count; s++)
        {
            UInt128 mine = _pieces[s].HiddenSurfaces;
            bool hide = mine != UInt128.Zero && (collapsed & mine) == mine;
            bool was = mine != UInt128.Zero && (_appliedCollapse & mine) == mine;
            if (hide != was)
            {
                SetSurfaceOverrideMaterial(s, hide ? Refii.Entities.Engine_HiddenClusters.CollapsedMaterial : null);
            }
        }

        _appliedCollapse = collapsed;
    }

    public void ResetToSkinned()
    {
        Collect();
        foreach (ClothInstance instance in _instances)
        {
            instance.ResetToSkinned();
        }

        PackAll();
        _patch?.Upload();
        ApplyBounds();
    }

    private static int PopCount(UInt128 mask) =>
        System.Numerics.BitOperations.PopCount((ulong)(mask & ulong.MaxValue)) +
        System.Numerics.BitOperations.PopCount((ulong)(mask >> 64));

    /// <summary>The live bodies and the run's cost totals, for the debug channel.</summary>
    public static Dictionary<string, object?> Describe()
    {
        var bodies = new List<Dictionary<string, object?>>();
        double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        int sleeping = 0;
        int verts = 0;

        lock (Registry)
        {
            foreach (WeakReference<Engine_ClothBody> reference in Registry)
            {
                if (!reference.TryGetTarget(out Engine_ClothBody? body) || !GodotObject.IsInstanceValid(body))
                {
                    continue;
                }

                int bodyVerts = 0;
                int collided = 0;
                int iterations = 0;
                float residual = 0f;
                float weight = 0f;
                var models = new List<string>();
                for (int i = 0; i < body._instances.Count; i++)
                {
                    ClothInstance inst = body._instances[i];
                    bodyVerts += inst.PosCur.Length;
                    collided += inst.CollidedVertices;
                    iterations = Math.Max(iterations, inst.RelaxIterations);
                    residual = Math.Max(residual, inst.Residual);
                    weight = inst.LodWeight;
                    string leaf = body._pieces[i].ModelPath;
                    int slash = leaf.LastIndexOf('/');
                    models.Add(slash < 0 ? leaf : leaf[(slash + 1)..]);
                }

                verts += bodyVerts;
                if (body._sleeping)
                {
                    sleeping++;
                }

                bodies.Add(new Dictionary<string, object?>
                {
                    ["node"] = body.GetParent()?.Name.ToString() ?? string.Empty,
                    ["models"] = models,
                    ["pieces"] = body._instances.Count,
                    ["vertices"] = bodyVerts,
                    ["visible"] = body.Visible,
                    ["rigged"] = body._skeleton is not null,
                    ["weight"] = weight,
                    ["iterations"] = iterations,
                    ["residual"] = residual,
                    ["collided"] = collided,
                    ["sleeping"] = body._sleeping,
                    ["solveMs"] = body._lastSolveTicks * toMs,
                    ["uploadMs"] = body._lastUploadTicks * toMs,
                    ["position"] = body.GlobalPosition.ToString(),
                    ["aabb"] = body._bounds.ToString(),
                });
            }
        }

        int frames = Math.Max(1, Engine.GetFramesDrawn() - _frames);
        var totals = new Dictionary<string, object?>
        {
            ["bodies"] = bodies.Count,
            ["sleeping"] = sleeping,
            ["vertices"] = verts,
            ["framesSampled"] = frames,
            ["solveMsPerFrame"] = _solveTicks * toMs / frames,
            ["uploadMsPerFrame"] = _uploadTicks * toMs / frames,
            ["mainPrepMsPerFrame"] = _mainTicks * toMs / frames,
            ["stepsPerFrame"] = (double)_stepped / frames,
            ["sleptPerFrame"] = (double)_slept / frames,
            ["laggedPerFrame"] = (double)_lagged / frames,
        };

        _solveTicks = 0;
        _uploadTicks = 0;
        _mainTicks = 0;
        _stepped = 0;
        _slept = 0;
        _lagged = 0;
        _frames = Engine.GetFramesDrawn();

        return new Dictionary<string, object?>
        {
            ["totals"] = totals,
            ["bodies"] = bodies,
        };
    }

    public override Godot.Collections.Array<Godot.Collections.Dictionary> _GetPropertyList()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        if (_instances.Count == 0)
        {
            return list;
        }

        list.Add(new Godot.Collections.Dictionary
        {
            { "name", "Cloth" },
            { "type", (int)Variant.Type.Nil },
            { "usage", (int)PropertyUsageFlags.Group },
            { "hint_string", string.Empty },
        });

        for (int i = 0; i < _instances.Count; i++)
        {
            list.Add(new Godot.Collections.Dictionary
            {
                { "name", "Piece " + (i + 1) },
                { "type", (int)Variant.Type.String },
                { "usage", (int)(PropertyUsageFlags.Editor | PropertyUsageFlags.ReadOnly) },
            });
        }

        return list;
    }

    public override Variant _Get(StringName property)
    {
        string name = property.ToString();
        if (!name.StartsWith("Piece ", StringComparison.Ordinal) ||
            !int.TryParse(name["Piece ".Length..], out int index) ||
            index < 1 || index > _instances.Count)
        {
            return default;
        }

        ClothInstance instance = _instances[index - 1];
        ClothAsset? asset = instance.Asset;
        if (asset is null)
        {
            return default;
        }

        return $"{asset.VertexCount} verts, {asset.EdgeCount} edges, sim [{asset.SimBegin}..{asset.SimEnd}), " +
            $"{asset.Spheres.Length}s/{asset.Planes.Length}p/{asset.Capsules.Length}c/{asset.Boxes.Length}b " +
            $"colliders, {asset.WeldCount} welds, {instance.RelaxIterations} iter, " +
            $"residual {instance.Residual:0.####}, fade {instance.LodWeight:0.00}";
    }
}
