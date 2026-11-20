using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using Godot;

namespace Refii.Physics;

/// <summary>
/// Lock-free generation-matched work-stealing gate for per-frame cloth solves.
/// Queues the step onto the <see cref="ThreadPool"/> without per-frame <c>Task</c> allocations; if the
/// ThreadPool is saturated when <c>Land()</c> runs at frame end, the main thread atomically claims the
/// generation and executes the ~0.05 ms solve inline instead of stalling on a starved worker queue.
/// </summary>
public sealed class ClothSolveGate : IThreadPoolWorkItem
{
    private readonly Action _solveAction;
    private readonly object _waitGate = new();
    private long _issuedGen;
    private long _claimedGen;
    private long _completedGen;
    private long _uploadedGen;
    private int _waiters;
    private Exception? _error;

    /// <summary>Creates a work-stealing solve gate bound to <paramref name="solveAction"/>.</summary>
    public ClothSolveGate(Action solveAction)
    {
        _solveAction = solveAction ?? throw new ArgumentNullException(nameof(solveAction));
    }

    /// <summary>True while an issued cloth solve step is still running or queued and has not yet completed.</summary>
    public bool HasPendingStep => Volatile.Read(ref _completedGen) < Volatile.Read(ref _issuedGen);

    /// <summary>True while an issued cloth solve step has not yet been uploaded to the mesh surface on the main thread.</summary>
    public bool HasUnuploadedStep => Volatile.Read(ref _uploadedGen) < Volatile.Read(ref _issuedGen);

    /// <summary>True once a worker (or main-thread steal) has claimed the latest issued step.</summary>
    public bool IsLatestClaimed => Volatile.Read(ref _claimedGen) >= Volatile.Read(ref _issuedGen);

    /// <summary>
    /// Atomically marks the latest completed solve generation as uploaded on the main thread.
    /// Returns <c>true</c> when a newly completed step is ready to be uploaded.
    /// </summary>
    public bool TryConsumeUpload()
    {
        long completed = Volatile.Read(ref _completedGen);
        long uploaded = Volatile.Read(ref _uploadedGen);
        if (completed <= uploaded)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _uploadedGen, completed, uploaded) == uploaded;
    }

    /// <summary>Issues a new generation and schedules this gate on the <see cref="ThreadPool"/>.</summary>
    public long Schedule()
    {
        long gen = Interlocked.Increment(ref _issuedGen);
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        return gen;
    }

    /// <summary>Issues a new generation without queueing onto the <see cref="ThreadPool"/> (for deterministic work-stealing tests).</summary>
    public long IssueWithoutQueueing() => Interlocked.Increment(ref _issuedGen);

    void IThreadPoolWorkItem.Execute() => TryExecuteWorkerStep();

    /// <summary>Attempts to claim and run the latest unstarted generation on a worker thread.</summary>
    public bool TryExecuteWorkerStep()
    {
        long gen = Volatile.Read(ref _issuedGen);
        if (gen <= 0 || Interlocked.CompareExchange(ref _claimedGen, gen, gen - 1) != gen - 1)
        {
            return false;
        }

        RunClaimedStep(gen);
        return true;
    }

    /// <summary>
    /// Ensures the latest issued step is finished: steals and runs it inline if no worker has claimed it yet,
    /// or waits briefly if a worker is already mid-solve. Returns <c>true</c> when stolen inline.
    /// </summary>
    public bool CompleteOrSteal()
    {
        long gen = Volatile.Read(ref _issuedGen);
        if (gen <= 0 || Volatile.Read(ref _completedGen) >= gen)
        {
            ThrowCapturedIfAny();
            return false;
        }

        if (Interlocked.CompareExchange(ref _claimedGen, gen, gen - 1) == gen - 1)
        {
            RunClaimedStep(gen);
            ThrowCapturedIfAny();
            return true;
        }

        var spin = new SpinWait();
        while (Volatile.Read(ref _completedGen) < gen)
        {
            if (spin.NextSpinWillYield)
            {
                Interlocked.Increment(ref _waiters);
                try
                {
                    lock (_waitGate)
                    {
                        if (Volatile.Read(ref _completedGen) < gen)
                        {
                            Monitor.Wait(_waitGate, 1);
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _waiters);
                }
            }
            else
            {
                spin.SpinOnce();
            }
        }

        ThrowCapturedIfAny();
        return false;
    }

    private void RunClaimedStep(long gen)
    {
        try
        {
            _error = null;
            _solveAction();
        }
        catch (Exception ex)
        {
            _error = ex;
        }
        finally
        {
            Volatile.Write(ref _completedGen, gen);
            if (Volatile.Read(ref _waiters) > 0)
            {
                lock (_waitGate)
                {
                    Monitor.PulseAll(_waitGate);
                }
            }
        }
    }

    private void ThrowCapturedIfAny()
    {
        Exception? err = Interlocked.Exchange(ref _error, null);
        if (err is not null)
        {
            ExceptionDispatchInfo.Capture(err).Throw();
        }
    }
}

/// <summary>
/// The cloth of one drawn entity: a child of the entity's mesh node that draws the cloth
/// surfaces itself and steps them the way the guest's cloth proxy does, once per rendered frame.
///
/// The contract, all of it the guest's:
/// - dt is the frame time clamped to 0.2 s and divided by ceil(dt * 20) — ONE step of at most
///   50 ms per frame, never a loop (<c>sub_821E3180</c>; the substep count is computed and
///   never iterated).
/// - dt is the DISPLAY clock, not the sim clock: <c>sub_821E3180</c> differences
///   <c>dbl_83496E18</c>, which <c>sub_821C3858</c> sets from <c>lhRenderer_RenderFrame</c>'s time —
///   the vsync count over the refresh rate (<c>sub_82B6FCE8</c>), capped at the last posted frame
///   request (renderer+0xA0, which <c>lhGameLoop_Tick</c> posts every tick from its wall clock).
///   Nothing on that chain reads a pause, so a stated pause (<see cref="Refii.Core.Engine_StatedPause"/>)
///   does not stop the cloth: owners hold, and the cloth settles under the held wind.
/// - The owner's world matrix is the transform; a jump of more than one unit resets the cloth
///   to the skinned pose (<c>sub_8218D9A0</c>).
/// - The skin palette is the rig's pose times inverse bind, in the model's space, in the
///   cloth block's own bone ids (<c>sub_8222E2B8</c>); colliders pose off the same bones.
/// - Wind is the scene's wind, direction (scene+0xD38) times speed (scene+0xD44), unaltered:
///   <c>sub_821D1FD8</c> (disc <c>sub_821D1C98</c>) adds a gust (f(3t), f(7t), 0) of the clock
///   t = fmod(dbl_83496E18 / 6, 26880) scaled by |wind| times <c>flt_8331AC90</c> (disc
///   <c>flt_8331AB20</c>), which is 0.0 in the image and written by nothing, so the wind the step
///   reads is exactly direction times speed. Gravity is world down.
/// - The distance is the entity's own stated <c>MaxClothDistance</c>
///   (<see cref="Refii.Host.HostBridge.GetEntityClothDistance"/>, twin+0x128). Measured from the
///   owner's origin to the eye, at or beyond it, or out of the camera's view, nothing is simulated
///   and the skinned pose is drawn; inside the last 15% of it the drawn pose is lerped between
///   skinned and simulated (<c>sub_821E34B0</c>).
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
///
/// A TITLE WHOSE GUEST STATES ITS CLOTH (<see cref="Refii.Host.HostBridge.ClothStated"/>: Fable 3)
/// is never simulated here. Each frame the body takes the pieces the guest's draw published for
/// its entity (Fable3/src/hooks/src/cloth_hooks.cpp), joined by model and record, and draws their
/// vertices blended between the last pose of the previous game tick and the newest at the shared
/// draw tick (<see cref="Engine_ClothStated"/>). A piece the guest did not draw as cloth in its last
/// frame shows the parent's own skinned surfaces, as the guest then draws its skinned submesh.
/// </summary>
[Tool]
[GlobalClass]
public partial class Engine_ClothBody : MeshInstance3D
{
    public const string NodeName = "ClothBody";

    /// <summary>The <c>refii.toml</c> CVar scaling each entity's stated maximum cloth simulation range.</summary>
    public const string DistanceScaleKey = "cloth_distance_scale";

    /// <summary>Optional test/debug override for <see cref="ClothDistanceScale"/>; null reads <c>refii.toml</c>.</summary>
    public static float? DistanceScaleOverride;

    private static long _distanceScaleGeneration = -1;
    private static float _cachedDistanceScale = 1.0f;

    /// <summary>
    /// Live multiplier (<c>cloth_distance_scale</c>) on each entity's stated cloth simulation distance,
    /// cached per <see cref="Refii.Core.Engine_Switches.Generation"/> so per-frame checks cost zero allocations.
    /// </summary>
    public static float ClothDistanceScale
    {
        get
        {
            if (DistanceScaleOverride is { } forced)
            {
                return Math.Clamp(forced, 0.0f, 10.0f);
            }

            long gen = Refii.Core.Engine_Switches.Generation;
            if (gen == Volatile.Read(ref _distanceScaleGeneration))
            {
                return _cachedDistanceScale;
            }

            float scale = 1.0f;
            try
            {
                scale = Math.Clamp(
                    Refii.Core.Engine_RefiiConfig.Float(
                        Refii.Core.Engine_RefiiConfig.Read(out _), DistanceScaleKey, 1.0f),
                    0.0f, 10.0f);
            }
            catch (Exception)
            {
                scale = 1.0f;
            }

            _cachedDistanceScale = scale;
            Volatile.Write(ref _distanceScaleGeneration, gen);
            return scale;
        }
    }

    /// <summary>
    /// Computes the effective cloth simulation cutoff distance in metres from the entity's stated
    /// range and <paramref name="scale"/>. Pure helper safe for headless unit tests.
    /// </summary>
    public static float EffectiveMaxDistance(float statedDistance, float scale)
    {
        if (!float.IsFinite(statedDistance) || statedDistance <= 0.0f ||
            !float.IsFinite(scale) || scale <= 0.0f)
        {
            return 0.0f;
        }

        return statedDistance * Math.Clamp(scale, 0.0f, 10.0f);
    }

    /// <summary>
    /// <c>flt_83317D74</c>: the distance a twin's constructor (<c>sub_82AA2658</c>) gives it before its
    /// renderable states one. Used only for a body whose entity states none.
    /// </summary>
    public const float DefaultMaxClothDistance = 20.0f;

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

    private ClothSolveGate? _solveGate;

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

    /// <summary>Whether the guest states this body's pose (<see cref="Refii.Host.HostBridge.ClothStated"/>).</summary>
    private bool _stated;

    /// <summary>Whether this cloth body casts its own shadow rather than delegating garment shadows to the parent's shadow proxy.</summary>
    private bool _castsOwnShadow;

    /// <summary>Per piece, the guest's stated pose as last read, in the stated mode.</summary>
    private readonly List<StatedPose> _statedPoses = new();

    /// <summary>The surfaces of the pieces the guest draws as cloth now, in the stated mode.</summary>
    private UInt128 _statedDrawn;

    /// <summary>Whether this body logged the guest's statement: its first drawn piece, an unjoined one.</summary>
    private bool _statedLogged;

    private bool _unjoinedLogged;

    private static Refii.Host.HostBridge.GuestClothPiece[] _statedRows =
        new Refii.Host.HostBridge.GuestClothPiece[32];

    /// <summary>The entity a body's parent stands for: GameEntities' meta on the entity node.</summary>
    private static readonly StringName EntityAddressMeta = "entity_address";

    /// <summary>One piece's stated poses: the published buffers newest first, the guest frame each
    /// was posed for, the pair last drawn and the drawn arrays.</summary>
    private sealed class StatedPose
    {
        public uint Model;
        public uint Key;
        public uint Sequence;
        public float Fraction = -1.0f;
        public int Older = -1;
        public int Newer = -1;
        public bool Drawn;
        public readonly float[][] Poses = { Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>() };
        public readonly double[] Frames = new double[4];
        public int PoseCount;
        public PhysVec4[] Positions = Array.Empty<PhysVec4>();
        public PhysVec4[] Normals = Array.Empty<PhysVec4>();
        public PhysVec4[] Tangents = Array.Empty<PhysVec4>();
        public PhysVec4 Min;
        public PhysVec4 Max;
    }

    [Export]
    public bool Simulate { get; set; } = true;

    public IReadOnlyList<ClothInstance> Instances => _instances;

    public int PieceCount => _pieces.Count;

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
            existing.RefreshSkeleton(node);
            return existing;
        }

        Detach(node);

        if (mesh is not ArrayMesh source)
        {
            return null;
        }

        // A morphed model carries no cloth definition in Fable 2's guest (its record copies drop
        // the pointer), so its garments draw GPU-skinned; the same here, for a title whose guest
        // does that (Engine_TitleGame.MorphedModelsKeepCloth). A title whose guest states its cloth
        // says itself which pieces it draws.
        bool stated = Refii.Host.HostBridge.ClothStated;
        if (!stated && !Refii.Core.Engine_TitleGame.Active.MorphedModelsKeepCloth &&
            source.HasMeta(Refii.Hero.Engine_HeroBodyMorph.MorphedMeta))
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
            modelPaths, source, Refii.Game.AndroidMemoryRelease.ResolveClothPart);
        if (pieces.Count == 0)
        {
            return null;
        }

        // A piece with no buildable surface would shift every later piece onto its neighbour's.
        pieces.RemoveAll(piece => !ClothPatch.Capturable(piece));
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
        bool castsOwnShadow = !rigged && node.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off;
        var body = new Engine_ClothBody
        {
            Name = NodeName,
            Layers = node.Layers,
            CastShadow = rigged ? GeometryInstance3D.ShadowCastingSetting.Off : node.CastShadow,
            _castsOwnShadow = castsOwnShadow,
            GIMode = node.GIMode,
            VisibilityRangeBegin = node.VisibilityRangeBegin,
            VisibilityRangeEnd = node.VisibilityRangeEnd,
            VisibilityRangeBeginMargin = node.VisibilityRangeBeginMargin,
            VisibilityRangeEndMargin = node.VisibilityRangeEndMargin,
            VisibilityRangeFadeMode = node.VisibilityRangeFadeMode,
        };

        body.Bind(source, pieces, patch, stated);
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

        // A stated body drives no surface until the guest draws a piece as cloth.
        Refii.Entities.Engine_HiddenClusters.SetClothSurfaces(node, stated ? UInt128.Zero : patch.DrivenMask, castsOwnShadow);
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

    private void Bind(ArrayMesh source, List<ClothPiece> pieces, ClothPatch patch, bool stated)
    {
        _source = source;
        _patch = patch;
        _stated = stated;
        _pieces.Clear();
        _instances.Clear();
        _drawn.Clear();
        _inverseRest.Clear();
        _usedBones.Clear();
        _statedPoses.Clear();
        _statedDrawn = UInt128.Zero;

        if (stated)
        {
            foreach (ClothPiece piece in pieces)
            {
                int count = piece.Asset.VertexCount;
                _pieces.Add(piece);
                _statedPoses.Add(new StatedPose
                {
                    Model = Refii.Level.Engine_PropModelIndex.PathHash(piece.ModelPath),
                    Positions = new PhysVec4[count],
                    Normals = new PhysVec4[count],
                    Tangents = new PhysVec4[count],
                });
            }

            Mesh = patch.Built;
            Visible = false;
            GD.Print($"[cloth] {source.ResourceName}: {_pieces.Count} piece(s) bound to the guest's own cloth " +
                $"(no host solve), standing in for {PopCount(patch.DrivenMask)} body surface(s) while it draws them");
            return;
        }

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

    public override void _Ready()
    {
        lock (Registry)
        {
            Registry.Add(new WeakReference<Engine_ClothBody>(this));
        }

        SetProcess(true);
    }

    /// <summary>
    /// Collects and uploads all active cloth solves at the end of the main-thread <c>_Process</c>
    /// phase (via <see cref="Refii.Core.Engine_FrameEndNode"/>) before <c>RenderingServer::draw()</c> begins.
    /// </summary>
    public static void LandAll()
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
        if (_solveGate is not { HasUnuploadedStep: true } || _patch is null)
        {
            return;
        }

        if (!_solveGate.IsLatestClaimed)
        {
            System.Threading.Interlocked.Increment(ref _lagged);
        }

        Collect();
        if (!_solveGate.TryConsumeUpload())
        {
            return;
        }

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

    /// <summary>Completes or steals a pending solve; the body's state is then quiescent.</summary>
    private void Collect()
    {
        if (_solveGate is not { HasPendingStep: true })
        {
            return;
        }

        try
        {
            _solveGate.CompleteOrSteal();
        }
        catch (Exception e)
        {
            GD.PushError($"[physics] cloth solve failed: {e.Message}");
        }
    }

    public override void _Process(double delta)
    {
        long mainFrom = System.Diagnostics.Stopwatch.GetTimestamp();

        if (_stated)
        {
            if (_patch is not null && GetParent() is MeshInstance3D statedParent)
            {
                DrawStated(statedParent);
            }

            return;
        }

        if (_patch is null || _instances.Count == 0 || GetParent() is not MeshInstance3D parent)
        {
            Collect();
            return;
        }

        // A solve normally lands in LandAll before the draw; one still pending here (a frame
        // that drew nothing) lands now. One still RUNNING owns this body's arrays: no new step
        // until it is done.
        Land();
        if (_solveGate is { HasPendingStep: true })
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
        (_solveGate ??= new ClothSolveGate(Solve)).Schedule();
        System.Threading.Interlocked.Increment(ref _stepped);
        System.Threading.Interlocked.Add(ref _mainTicks, System.Diagnostics.Stopwatch.GetTimestamp() - mainFrom);
    }

    /// <summary>
    /// The stated mode's frame: the guest's pieces for this body's entity, each matched to a body
    /// piece by its model hash, record and vertex count; a piece the guest drew as cloth is blended
    /// at the draw tick and packed, one the guest did not leaves its surfaces to the parent.
    /// Main thread; nothing runs off it.
    /// </summary>
    private void DrawStated(MeshInstance3D parent)
    {
        if (!Bound(Refii.Entities.Engine_BodyVariants.SourceMesh(parent)))
        {
            // The mesh this body was built for is gone: give its surfaces back to the parent.
            Visible = false;
            if (_statedDrawn != UInt128.Zero)
            {
                _statedDrawn = UInt128.Zero;
                Refii.Entities.Engine_HiddenClusters.SetClothSurfaces(parent, UInt128.Zero);
            }

            return;
        }

        uint entity = parent.HasMeta(EntityAddressMeta) ? (uint)parent.GetMeta(EntityAddressMeta).AsInt64() : 0u;
        int rows = entity != 0u ? Refii.Host.HostBridge.GetEntityClothPieces(entity, _statedRows) : 0;
        double drawTick = Refii.Entities.Engine_DrawnFrame.DrawTick;

        UInt128 drawn = UInt128.Zero;
        bool packed = false;
        int joined = 0;
        Span<double> stamps = stackalloc double[4];
        for (int i = 0; i < _pieces.Count; i++)
        {
            ClothPiece piece = _pieces[i];
            StatedPose pose = _statedPoses[i];
            int row = -1;
            for (int r = 0; r < rows; r++)
            {
                Refii.Host.HostBridge.GuestClothPiece candidate = _statedRows[r];
                if (candidate.Model == pose.Model && candidate.VertexCount == piece.Asset.VertexCount &&
                    Engine_ClothStated.Record(candidate.Skinned, candidate.Slot, piece.RigidRecords) == piece.Record)
                {
                    row = r;
                    break;
                }
            }

            joined += row >= 0 ? 1 : 0;
            pose.Drawn = row >= 0 && _statedRows[row].Drawn &&
                ((pose.Key == _statedRows[row].Key && pose.Sequence != 0u) || Fetch(pose, _statedRows[row]));
            if (!pose.Drawn)
            {
                continue;
            }

            Refii.Host.HostBridge.GuestClothPiece stated = _statedRows[row];
            bool fresh = (stated.Key != pose.Key || stated.Sequence != pose.Sequence) && Fetch(pose, stated);

            // Sampled at the draw point between the two poses stamped around it, as a body's ring is.
            for (int p = 0; p < pose.PoseCount; p++)
            {
                stamps[p] = Refii.Core.Engine_TickClock.StampOfGuestFrame(pose.Frames[p]);
            }

            (int older, int newer, float t) = Engine_ClothStated.Locate(stamps[..pose.PoseCount], drawTick);
            if (newer >= 0 && (fresh || t != pose.Fraction || older != pose.Older || newer != pose.Newer))
            {
                int floats = piece.Asset.VertexCount * Refii.Host.HostBridge.ClothVertexFloats;
                ReadOnlySpan<float> from = older != newer ? pose.Poses[older].AsSpan(0, floats) : ReadOnlySpan<float>.Empty;
                (pose.Min, pose.Max) = Engine_ClothStated.Blend(
                    from, pose.Poses[newer], older != newer ? t : 1.0f, piece.Asset.VertexCount,
                    pose.Positions, pose.Normals, pose.Tangents);
                _patch!.Pack(i, pose.Positions, pose.Normals, pose.Tangents);
                pose.Fraction = t;
                pose.Older = older;
                pose.Newer = newer;
                packed = true;
            }

            drawn |= piece.HiddenSurfaces;
        }

        if (joined < rows && !_unjoinedLogged)
        {
            _unjoinedLogged = true;
            for (int r = 0; r < rows; r++)
            {
                Refii.Host.HostBridge.GuestClothPiece p = _statedRows[r];
                GD.Print($"[cloth] {_source?.ResourceName}: entity 0x{entity:X8} states piece model 0x{p.Model:X8} " +
                    $"{(p.Skinned ? "skinned" : "rigid")} slot {p.Slot}, {p.VertexCount} verts — " +
                    $"{joined} of {rows} joined to the body's {_pieces.Count} block(s)");
            }
        }

        if (drawn != UInt128.Zero && !_statedLogged)
        {
            _statedLogged = true;
            int count = 0;
            foreach (StatedPose pose in _statedPoses)
            {
                count += pose.Drawn ? 1 : 0;
            }

            GD.Print($"[cloth] {_source?.ResourceName}: the guest states {rows} piece(s) for entity 0x{entity:X8}, " +
                $"{count} drawn as cloth from its own simulation");
        }

        if (drawn != _statedDrawn)
        {
            _statedDrawn = drawn;
            Refii.Entities.Engine_HiddenClusters.SetClothSurfaces(parent, drawn, _castsOwnShadow);
        }

        if (packed)
        {
            _patch!.Upload();
            ApplyStatedBounds();
        }

        Visible = drawn != UInt128.Zero && parent.IsVisibleInTree();
        SyncCollapse();
    }

    /// <summary>Reads a piece's published poses, newest first; false when the bridge no longer has them.</summary>
    private static bool Fetch(StatedPose pose, in Refii.Host.HostBridge.GuestClothPiece stated)
    {
        int floats = stated.VertexCount * Refii.Host.HostBridge.ClothVertexFloats;
        int wanted = Math.Min(stated.PoseCount, pose.Poses.Length);
        int count = 0;
        for (int p = 0; p < wanted; p++)
        {
            if (pose.Poses[p].Length < floats)
            {
                pose.Poses[p] = new float[floats];
            }

            if (Refii.Host.HostBridge.GetClothPieceVertices(stated.Key, p, pose.Poses[p], out double frame) != stated.VertexCount)
            {
                break;
            }

            pose.Frames[p] = frame;
            count++;
        }

        if (count == 0)
        {
            return false;
        }

        pose.PoseCount = count;
        pose.Key = stated.Key;
        pose.Sequence = stated.Sequence;
        pose.Fraction = -1.0f;
        pose.Older = -1;
        pose.Newer = -1;
        return true;
    }

    /// <summary>The stated mode's bounds: the drawn pieces' positions, in the model's space.</summary>
    private void ApplyStatedBounds()
    {
        bool any = false;
        var min = Vector3.Zero;
        var max = Vector3.Zero;
        foreach (StatedPose pose in _statedPoses)
        {
            if (!pose.Drawn)
            {
                continue;
            }

            var lo = new Vector3(pose.Min.X, pose.Min.Y, pose.Min.Z);
            var hi = new Vector3(pose.Max.X, pose.Max.Y, pose.Max.Z);
            if (!lo.IsFinite() || !hi.IsFinite() || lo.X > hi.X)
            {
                continue;
            }

            min = any ? min.Min(lo) : lo;
            max = any ? max.Max(hi) : hi;
            any = true;
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
    /// <c>sub_821E34B0</c>: d = |twin+0x150 translation − view+0x50 eye|; 0 when d is at or beyond
    /// the twin's distance (<c>lfs f31, 0x128(r30)</c> / <c>fcmpu f30, f31</c> / <c>bge</c>) or the
    /// pieces' sphere is outside the frustum (<c>sub_821FF138</c>), 1 within 85% of it, a ramp
    /// between. Every view counts: in view is in any view's frustum, d is to the nearest eye.
    /// </summary>
    private float FadeWeight(MeshInstance3D parent, out bool inView)
    {
        float scale = ClothDistanceScale;
        if (scale <= 0.0f)
        {
            inView = false;
            return 0.0f;
        }

        inView = true;
        IReadOnlyList<Camera3D> cameras = Refii.Core.Engine_Views.Cameras(GetViewport()?.GetCamera3D());
        if (cameras.Count == 0)
        {
            return 1.0f;
        }

        float max = EffectiveMaxDistance(StatedMaxDistance(parent), scale);
        if (max <= 0.0f)
        {
            inView = false;
            return 0.0f;
        }

        Vector3 origin = parent.GlobalPosition;
        float minDistanceSq = float.MaxValue;
        int views = Math.Min(cameras.Count, _frustum.Length);
        for (int v = 0; v < views; v++)
        {
            minDistanceSq = MathF.Min(minDistanceSq, origin.DistanceSquaredTo(cameras[v].GlobalPosition));
        }

        float outerCutoff = max + 6.0f;
        if (minDistanceSq >= outerCutoff * outerCutoff)
        {
            inView = false;
            return 0.0f;
        }

        float distance = MathF.Sqrt(minDistanceSq);
        inView = InView(cameras, views, parent);
        if (!inView || distance >= max)
        {
            return 0.0f;
        }

        float start = max * FadeStart;
        return distance <= start ? 1.0f : (max - distance) / (max - start);
    }

    /// <summary>
    /// The guest's twin+0x128 for this body's entity (<see cref="Refii.Host.HostBridge.GetEntityClothDistance"/>);
    /// <see cref="DefaultMaxClothDistance"/>, the twin constructor's own, where none is stated.
    /// </summary>
    private static float StatedMaxDistance(MeshInstance3D parent)
    {
        uint entity = parent.HasMeta(EntityAddressMeta) ? (uint)parent.GetMeta(EntityAddressMeta).AsInt64() : 0u;
        return entity != 0u && Refii.Host.HostBridge.GetEntityClothDistance(entity, out float stated)
            ? stated
            : DefaultMaxClothDistance;
    }

    /// <summary>The union of the pieces' bounding spheres against every view's frustum.</summary>
    private bool InView(IReadOnlyList<Camera3D> cameras, int views, MeshInstance3D parent)
    {
        Transform3D world = parent.GlobalTransform;
        float scale = world.Basis.Scale.Length() * 0.577350269f;
        bool anyVisible = false;
        for (int v = 0; v < views; v++)
        {
            Camera3D camera = cameras[v];
            Plane[] planes = FrameFrustum(camera, v);
            foreach (ClothInstance inst in _instances)
            {
                PhysVec4 sphere = inst.BoundsSphere;
                Vector3 center = world * new Vector3(sphere.X, sphere.Y, sphere.Z);
                float radius = MathF.Max(sphere.W * scale, 0.5f);
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

    /// <summary>
    /// Refreshes the bound <see cref="Skeleton3D"/> and <see cref="Skin"/> immediately after the
    /// parent entity re-rigs or clears its skeleton.
    /// </summary>
    public void RefreshSkeleton(MeshInstance3D parent)
    {
        _skeletonChecked = false;
        EnsureSkeleton(parent);
    }

    private void EnsureSkeleton(MeshInstance3D parent)
    {
        if (_skeleton is not null && IsInstanceValid(_skeleton))
        {
            using NodePath currentPath = parent.Skeleton;
            Skeleton3D? current = currentPath.IsEmpty ? null : parent.GetNodeOrNull<Skeleton3D>(currentPath);
            if (ReferenceEquals(current, _skeleton))
            {
                return;
            }
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

    /// <summary>
    /// A piece whose surfaces a garment hides on the body is hidden here too, and in the stated mode
    /// so is a piece the guest does not draw as cloth now (its surfaces are the parent's again).
    /// </summary>
    private void SyncCollapse()
    {
        if (_patch is null || GetParent() is not MeshInstance3D parent)
        {
            return;
        }

        UInt128 collapsed = Refii.Entities.Engine_HiddenClusters.ClusterMask(parent);
        UInt128 hidden = UInt128.Zero;
        for (int s = 0; s < _patch.SurfaceCount && s < _pieces.Count && s < 128; s++)
        {
            UInt128 mine = _pieces[s].HiddenSurfaces;
            bool hide = (mine != UInt128.Zero && (collapsed & mine) == mine) ||
                (_stated && !_statedPoses[s].Drawn);
            if (hide)
            {
                hidden |= UInt128.One << s;
            }
        }

        if (hidden == _appliedCollapse)
        {
            return;
        }

        for (int s = 0; s < _patch.SurfaceCount && s < _pieces.Count && s < 128; s++)
        {
            UInt128 bit = UInt128.One << s;
            if ((hidden & bit) != (_appliedCollapse & bit))
            {
                SetSurfaceOverrideMaterial(s, (hidden & bit) != UInt128.Zero
                    ? Refii.Entities.Engine_HiddenClusters.CollapsedMaterial
                    : null);
            }
        }

        _appliedCollapse = hidden;
    }

    public void ResetToSkinned()
    {
        if (_stated)
        {
            // The guest resets its own pieces; the next publish carries the reset pose.
            return;
        }

        Collect();
        foreach (ClothInstance instance in _instances)
        {
            instance.ResetToSkinned();
        }

        PackAll();
        _patch?.Upload();
        ApplyBounds();
    }

    /// <summary>
    /// Stops per-frame cloth simulation while retaining the final draped vertex state (e.g. on a
    /// retained corpse whose skeleton is settled and waiting to fade out).
    /// </summary>
    public void Freeze()
    {
        Land();
        _sleeping = true;
        SetProcess(false);
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
                int statedDrawn = 0;
                for (int i = 0; i < body._statedPoses.Count; i++)
                {
                    bodyVerts += body._pieces[i].Asset.VertexCount;
                    statedDrawn += body._statedPoses[i].Drawn ? 1 : 0;
                    string leaf = body._pieces[i].ModelPath;
                    int slash = leaf.LastIndexOf('/');
                    models.Add(slash < 0 ? leaf : leaf[(slash + 1)..]);
                }

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
                    ["pieces"] = body._pieces.Count,
                    ["stated"] = body._stated,
                    ["statedDrawn"] = statedDrawn,
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
            ["distanceScale"] = ClothDistanceScale,
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
        if (_pieces.Count == 0)
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

        for (int i = 0; i < _pieces.Count; i++)
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
            index < 1 || index > _pieces.Count)
        {
            return default;
        }

        ClothAsset asset = _pieces[index - 1].Asset;
        if (_stated)
        {
            StatedPose pose = _statedPoses[index - 1];
            return $"{asset.VertexCount} verts, record {_pieces[index - 1].Record}, stated by the guest, " +
                (pose.Drawn ? $"drawn as cloth (publish {pose.Sequence}, {pose.PoseCount} pose(s), newest at guest frame {pose.Frames[0]:0.00})" : "drawn skinned by the body");
        }

        ClothInstance instance = _instances[index - 1];

        return $"{asset.VertexCount} verts, {asset.EdgeCount} edges, sim [{asset.SimBegin}..{asset.SimEnd}), " +
            $"{asset.Spheres.Length}s/{asset.Planes.Length}p/{asset.Capsules.Length}c/{asset.Boxes.Length}b " +
            $"colliders, {asset.WeldCount} welds, {instance.RelaxIterations} iter, " +
            $"residual {instance.Residual:0.####}, fade {instance.LodWeight:0.00}";
    }
}
