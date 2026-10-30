using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Refii.Models;

namespace Refii.Level;

[Tool]
public partial class Engine_PropStreamer : Node3D
{
    public const string ModelPathMeta = "model_path";

    public const string BatchBoundsMeta = "batch_bounds";

    public const string BatchSpreadMeta = "batch_spread";

    /// <summary>
    /// The `.save` record one prop instance was built from, on a node that holds a single one.
    ///
    /// ONLY THE GDB ENTITY-PLACEMENT PASS SETS THIS, and that is the point: foliage, the
    /// engine_level blocks, the GMD children, the companions and the shells are all level
    /// geometry that no `.save` record names, so they carry nothing. The presence of a record
    /// hash is therefore exactly the statement "the guest owns this one too", which is what
    /// <see cref="Refii.Entities.Engine_StaticTwins"/> needs to tell a duplicate from a prop
    /// only we know about.
    /// </summary>
    public const string RecordMeta = "record_hash";

    /// <summary>
    /// The `.save` records behind a batched node, parallel to its MultiMesh instances.
    ///
    /// A MultiMesh instance is not a node and cannot carry metadata of its own, so the whole
    /// column rides on the node and is indexed by instance. Zero means the same thing it means
    /// on a single: this instance came from something other than an entity placement.
    /// </summary>
    public const string RecordsMeta = "record_hashes";

    private static readonly Dictionary<string, ArrayMesh> Cache = new();

    private static readonly List<Engine_PropStreamer> Live = new();

    static Engine_PropStreamer()
    {
        Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
        {
            if (name.Equals("prop_cull_distance_scale", StringComparison.OrdinalIgnoreCase))
            {
                UpdateAllVisibilityRanges();
            }
            else if (name.Equals(Engine_PropDrawRange.TreeFarReachKey, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(Engine_PropDrawRange.TreeLodBiasKey, StringComparison.OrdinalIgnoreCase))
            {
                Engine_PropDrawRange.RefreshLive();
                UpdateAllVisibilityRanges();
            }
        };
    }

    private readonly List<GeometryInstance3D> _pending = new();

    private readonly List<GeometryInstance3D> _remaining = new();

    /// <summary>
    /// Each remaining instance's model path, held alongside <see cref="_remaining"/> and kept at
    /// the same indices.
    /// </summary>
    /// <remarks>
    /// The path is fixed for the life of the instance; reading it from metadata on every scan
    /// visit marshals a Variant and allocates a string per prop per frame. Materialised once
    /// when the instance is queued. The two lists are mutated only together.
    /// </remarks>
    private readonly List<string> _remainingPaths = new();

    private readonly Stopwatch _watch = new();

    private int _scan;

    private int _done;

    private PrefetchJob? _prefetch;
    private int _requeueRounds;

    private int _loaded;

    private int _failed;

    /// <summary>Failures whose path is in no bank — the index invented the name.</summary>
    private int _absent;

    /// <summary>Failures whose bytes are there and would not decode — a reader defect.</summary>
    private int _undecodable;

    private bool _reported;

    /// <summary>
    /// How long a frame may spend streaming props.
    ///
    /// THE BUDGET IS A FRAME'S SHARE, NOT A DEADLINE TO FILL. At 25 ms it was most of the
    /// frame: the phase averaged 101 ms in the market with a 104 ms peak, because the budget is
    /// tested AFTER a step and one step can be tens of milliseconds. Streaming then owned the
    /// frame for as long as props kept arriving, which is the hitching a player feels while
    /// walking into a new part of a region. Six leaves the rest of the frame to the game and
    /// costs only more frames to finish, which nothing is waiting on.
    /// </summary>
    public const double DefaultMsPerFrame = 6.0;

    [Export] public double MillisecondsPerFrame { get; set; } = DefaultMsPerFrame;

    [Export] public bool Enabled { get; set; } = true;

    public int Total => _pending.Count;

    public int Done => _done;

    public static event Action<int, int>? ProgressChanged;

    public static void ClearCache()
    {
        lock (Cache)
        {
            Cache.Clear();
            Failed.Clear();
        }
    }

    public static void PublishProgress()
    {
        TryGetProgress(out int done, out int total);
        ProgressChanged?.Invoke(done, total);
    }

    public static bool TryGetProgress(out int done, out int total)
    {
        done = 0;
        total = 0;

        for (int i = Live.Count - 1; i >= 0; i--)
        {
            Engine_PropStreamer streamer = Live[i];
            if (!GodotObject.IsInstanceValid(streamer))
            {
                Live.RemoveAt(i);
                continue;
            }

            done += streamer.Done;
            total += streamer.Total;
        }

        return total > 0;
    }

    public override void _Ready()
    {
        if (!Live.Contains(this))
        {
            Live.Add(this);
        }

        _pending.Clear();
        _remaining.Clear();
        _remainingPaths.Clear();
        _scan = 0;
        _done = 0;
        _loaded = 0;
        _failed = 0;
        _reported = false;

        Engine_MdlMaterial.WarmShaders();
        Refii.Entities.Engine_ShadowProxy.WarmShader();

        CollectPendingInstances(this, _pending, recursive: Engine.IsEditorHint() || Engine_LevelScene.ForceEditorMode);

        _pending.Sort((a, b) => InstanceCount(b).CompareTo(InstanceCount(a)));
        _remaining.AddRange(_pending);
        foreach (GeometryInstance3D queued in _pending)
        {
            _remainingPaths.Add(queued.GetMeta(ModelPathMeta).AsString());
        }

        StartPrefetch();

        SetProcess(_pending.Count != 0);
        PublishProgress();
    }

    private volatile bool _prefetchStop;

    /// <summary>Counts outgoing vertex-bake producers through completion publication, independently of native node deletion.</summary>
    private readonly Refii.Core.Engine_RegionWorkers _retirementWorkers = new();

    private void StartPrefetch()
    {
        var paths = new List<string>(_pending.Count);
        foreach (GeometryInstance3D instance in _pending)
        {
            if (instance.Visible)
            {
                paths.Add(instance.GetMeta(ModelPathMeta).AsString());
            }
        }

        _prefetchStop = false;
        _prefetch = PrefetchModels(paths, () => _prefetchStop);
    }

    public sealed class PrefetchJob
    {
        private int _remaining;

        internal PrefetchJob(int count) => _remaining = count;

        public bool IsComplete => System.Threading.Volatile.Read(ref _remaining) <= 0;

        internal void MarkOne() => System.Threading.Interlocked.Decrement(ref _remaining);

        internal void MarkRest(int count) => System.Threading.Interlocked.Add(ref _remaining, -count);

        public static readonly PrefetchJob Empty = new(0);
    }

    /// <summary>`prop_cull_min_pixels`: a prop's on-screen height below which it stops drawing; 0 never culls.</summary>
    private static readonly float CullMinPixels = ReadCullMinPixels();

    /// <summary>Pixels per metre at one metre on a 1080-line display with a 75° vertical fov.</summary>
    private const float PixelsPerMetreAtOneMetre = 1080f / (2f * 0.76733f);

    /// <summary>A range past this is left unset: the sun's cascades end at 300 m and nothing beyond is worth a fade.</summary>
    private const float CullFarthestMetres = 600f;

    /// <summary>A batch whose bounds span more than this keeps drawing: the range is measured to the bounds' centre.</summary>
    private const float CompactBatchMetres = 30f;

    private static float ReadCullMinPixels()
    {
        try
        {
            return Refii.Core.Engine_RefiiConfig.Read(out _).TryGetValue("prop_cull_min_pixels", out string? v) &&
                float.TryParse(v.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float pixels)
                ? MathF.Max(0f, pixels)
                : 4f;
        }
        catch (Exception)
        {
            return 4f;
        }
    }

    public static float ReadCullDistanceScale()
    {
        try
        {
            return Refii.Core.Engine_RefiiConfig.Read(out _).TryGetValue("prop_cull_distance_scale", out string? v) &&
                float.TryParse(v.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float scale) && float.IsFinite(scale)
                ? scale
                : 1f;
        }
        catch (Exception)
        {
            return 1f;
        }
    }

    /// <summary>
    /// Checks whether a model path represents a building or primary architectural structure.
    /// </summary>
    public static bool IsBuildingPath(string modelPath, float extent = 0f)
    {
        if (string.IsNullOrEmpty(modelPath))
        {
            return false;
        }

        // Small dressing models (<2.5m) are NEVER exempt as buildings
        if (extent > 0f && extent < 2.5f)
        {
            return false;
        }

        string p = modelPath.Replace('\\', '/').ToLowerInvariant();

        // Building shells and major structures
        if (p.EndsWith("/exterior.mdl", StringComparison.Ordinal) ||
            p.EndsWith("/interior.mdl", StringComparison.Ordinal) ||
            p.Contains("townhouse", StringComparison.Ordinal) ||
            p.Contains("clocktower", StringComparison.Ordinal) ||
            p.Contains("gatehouse", StringComparison.Ordinal) ||
            p.Contains("bridge", StringComparison.Ordinal) ||
            p.Contains("castle", StringComparison.Ordinal))
        {
            return true;
        }

        // Large regional structures (walls, towers, platforms) >= 4m
        if (extent >= 4.0f && (p.Contains("/buildings/", StringComparison.Ordinal) || p.Contains("/structures/", StringComparison.Ordinal)))
        {
            if (!p.Contains("stall", StringComparison.Ordinal) &&
                !p.Contains("cart", StringComparison.Ordinal) &&
                !p.Contains("crate", StringComparison.Ordinal) &&
                !p.Contains("barrel", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether an instance represents a building or primary architectural structure.
    /// Buildings are exempt from size-based prop distance culling so town silhouettes remain visible.
    /// </summary>
    public static bool IsBuilding(GeometryInstance3D instance, string? modelPath = null, float extent = 0f)
    {
        if (extent > 0f && extent < 2.5f)
        {
            return false;
        }

        if (string.IsNullOrEmpty(modelPath) && instance.HasMeta(ModelPathMeta))
        {
            modelPath = instance.GetMeta(ModelPathMeta, string.Empty).AsString();
        }

        if (IsBuildingPath(modelPath ?? string.Empty, extent))
        {
            return true;
        }

        string name = instance.Name.ToString().ToLowerInvariant();
        if (name.Contains("townhouse", StringComparison.Ordinal) ||
            name.Contains("clocktower", StringComparison.Ordinal) ||
            name.Contains("gatehouse", StringComparison.Ordinal) ||
            name.Contains("bridge", StringComparison.Ordinal) ||
            name.Contains("exterior", StringComparison.Ordinal) ||
            name.Contains("interior", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Computes the tiered distance cull threshold for a non-building prop based on its longest extent.
    /// &lt;0.5m: 15m * scale, &lt;1.0m: 20m * scale, &lt;1.5m: 30m * scale, &lt;2.5m: 40m * scale.
    /// Returns 0 for objects &gt;=2.5m or when scale &lt;= 0 (no size clamp).
    /// </summary>
    public static float CalculateTieredCullDistance(float extent, float cullScale)
    {
        if (cullScale <= 0f)
        {
            return 0f;
        }

        // Three times the tiers the cull shipped with (15/20/30/40 m): the stated range still
        // caps each prop, so this restores the guest's own distance for most foliage and the
        // per-copy dither (Engine_PropFade) takes the edge; the tiers only trim what the guest
        // states far beyond what a prop of that size can show.
        return extent switch
        {
            < 0.5f => 45f * cullScale,
            < 1.0f => 60f * cullScale,
            < 1.5f => 90f * cullScale,
            < 2.5f => 120f * cullScale,
            _ => 0f
        };
    }

    /// <summary>
    /// Applies draw distance and fade range to a prop instance. First checks guest stated range,
    /// then applies size-tiered distance culling for non-building dressing props scaled by
    /// <c>prop_cull_distance_scale</c>:
    /// &lt;0.5m: 15m * scale, &lt;1.0m: 20m * scale, &lt;1.5m: 30m * scale, &lt;2.5m: 40m * scale.
    /// Buildings and objects &gt;=2.5m keep their stated distances.
    ///
    /// The tiered cull is one test per node, so its edge is dithered per copy by
    /// <see cref="Engine_PropFade"/> at the same radius; a tree keeps its own stated band.
    /// </summary>
    public static void ApplyVisibilityRange(GeometryInstance3D instance, Mesh mesh)
    {
        float radius = ApplyRange(instance, mesh);
        if (!instance.HasMeta(Engine_PropDrawRange.TreeBandMeta))
        {
            Engine_PropFade.SetRadius(instance, mesh, radius);
        }
        else if (instance.HasMeta(Engine_PropDrawRange.TreeFarModelMeta) &&
            instance.LodBias != Engine_PropDrawRange.TreeLodBias)
        {
            // A far tree's levels drop only leaf cards (tree_card_lod): tree_lod_bias lets them switch sooner.
            instance.LodBias = Engine_PropDrawRange.TreeLodBias;
        }
    }

    /// <summary>
    /// The range above; returns the radius each copy is drawn to under the tiered cull (the node's
    /// end less the batch's spread), or zero when the node is not tier-culled.
    /// </summary>
    private static float ApplyRange(GeometryInstance3D instance, Mesh mesh)
    {
        // First apply what the guest states (draw range, tree instance uniforms, billboard meta)
        bool hasStated = Engine_PropDrawRange.Apply(instance, mesh);

        Vector3 size = mesh.GetAabb().Size;
        float extent = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        Vector3 scale = instance.Transform.Basis.Scale;
        extent *= MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z)));
        if (!float.IsFinite(extent) || extent <= 0f)
        {
            return 0f;
        }

        // Buildings are exempt from size-based distance culling
        if (IsBuilding(instance, null, extent))
        {
            return 0f;
        }

        float spread = 0f;
        if (instance is MultiMeshInstance3D batch)
        {
            if (batch.Multimesh is not { } mm)
            {
                return 0f;
            }

            if (batch.HasMeta(BatchBoundsMeta))
            {
                Aabb rawBounds = (Aabb)batch.GetMeta(BatchBoundsMeta);
                float meshRadius = extent * 0.5f;
                mm.CustomAabb = rawBounds.Grow(MathF.Max(meshRadius, 0.5f));
            }

            if (batch.HasMeta(BatchSpreadMeta))
            {
                spread = batch.GetMeta(BatchSpreadMeta).AsSingle();
            }
            else
            {
                Vector3 span = mm.CustomAabb.Size;
                spread = span.Length() * 0.5f;
            }
        }

        float cullScale = ReadCullDistanceScale();
        float tieredEnd = CalculateTieredCullDistance(extent, cullScale);
        if (tieredEnd > 0f)
        {
            float end = tieredEnd + spread;

            // If the node carried a stated range, clamp it so small objects cull earlier,
            // but never push it further than the stated range.
            if (instance.HasMeta(Engine_PropDrawRange.Meta) &&
                instance.GetMeta(Engine_PropDrawRange.Meta).AsFloat32Array() is { Length: >= 3 } statedMeta &&
                statedMeta[2] > 0f)
            {
                end = MathF.Min(statedMeta[2], end);
            }

            if (instance.VisibilityRangeBegin > 0f)
            {
                end = MathF.Max(end, instance.VisibilityRangeBegin + 1f);
            }

            instance.VisibilityRangeEnd = end;
            instance.VisibilityRangeEndMargin = 0f;
            instance.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
            // The node cuts at end = radius + spread; each copy dithers to the radius itself.
            return MathF.Max(end - spread, 0f);
        }

        // For non-buildings with extent >= 2.5m (or when cullScale <= 0):
        // If it already had a stated range from the guest, keep that stated range!
        if (hasStated || CullMinPixels <= 0f)
        {
            return 0f;
        }

        float legacyEnd = extent * PixelsPerMetreAtOneMetre / CullMinPixels;
        if (legacyEnd >= CullFarthestMetres)
        {
            return 0f;
        }

        instance.VisibilityRangeEnd = legacyEnd;
        instance.VisibilityRangeEndMargin = 0f;
        instance.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
        return 0f;
    }

    /// <summary>
    /// Re-evaluates visibility ranges on all streamed props across all live streamers
    /// (e.g. when <c>prop_cull_distance_scale</c> is updated at runtime).
    /// </summary>
    /// <summary>A batch's instance transforms read back from its buffer (one render-thread sync).</summary>
    private static Transform3D[] BatchTransforms(MultiMesh batch)
    {
        int count = batch.InstanceCount;
        float[] buffer = count > 0 ? Refii.Core.Engine_ServerSyncs.Buffer(batch) : Array.Empty<float>();
        int stride = count > 0 ? buffer.Length / count : 0;
        var placed = new Transform3D[stride >= 12 ? count : 0];
        for (int i = 0; i < placed.Length; i++)
        {
            int o = i * stride;
            placed[i] = new Transform3D(
                new Vector3(buffer[o], buffer[o + 4], buffer[o + 8]),
                new Vector3(buffer[o + 1], buffer[o + 5], buffer[o + 9]),
                new Vector3(buffer[o + 2], buffer[o + 6], buffer[o + 10]),
                new Vector3(buffer[o + 3], buffer[o + 7], buffer[o + 11]));
        }
        return placed;
    }

    public static void UpdateAllVisibilityRanges()
    {
        lock (Live)
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                Engine_PropStreamer streamer = Live[i];
                if (!GodotObject.IsInstanceValid(streamer))
                {
                    Live.RemoveAt(i);
                    continue;
                }
                UpdateContainerVisibilityRanges(streamer);
            }
        }

        Refii.Game.GameEntities.Current?.UpdateAllVisibilityRanges();
    }

    private static void UpdateContainerVisibilityRanges(Node container)
    {
        foreach (Node child in container.GetChildren())
        {
            if (child is MeshInstance3D single && single.Mesh is not null)
            {
                ApplyVisibilityRange(single, single.Mesh);
            }
            else if (child is MultiMeshInstance3D multi && multi.Multimesh?.Mesh is not null)
            {
                ApplyVisibilityRange(multi, multi.Multimesh.Mesh);
                foreach (Node sub in multi.GetChildren())
                {
                    if (sub is GeometryInstance3D gi)
                    {
                        gi.VisibilityRangeEnd = multi.VisibilityRangeEnd;
                        gi.VisibilityRangeEndMargin = multi.VisibilityRangeEndMargin;
                        gi.VisibilityRangeFadeMode = multi.VisibilityRangeFadeMode;
                        Engine_PropFade.Copy(multi, gi);
                    }
                }
            }

            if (child.GetChildCount() > 0)
            {
                UpdateContainerVisibilityRanges(child);
            }
        }
    }

    public static PrefetchJob PrefetchModels(IReadOnlyList<string> paths, Func<bool>? stop = null)
    {
        var distinct = new List<string>(paths.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            if (path.Length != 0 && seen.Add(path))
            {
                distinct.Add(path);
            }
        }

        if (distinct.Count == 0)
        {
            return PrefetchJob.Empty;
        }

        Engine_MdlMaterial.WarmShaders();
        Refii.Entities.Engine_ShadowProxy.WarmShader();

        var job = new PrefetchJob(distinct.Count);

        int workers = Math.Min(Math.Max(2, System.Environment.ProcessorCount - 1), 8);
        workers = Math.Min(workers, distinct.Count);

        int cursor = -1;
        var trackedWorkers = new Refii.Core.Engine_RegionWorkers();
        for (int w = 0; w < workers; ++w)
        {
            trackedWorkers.TryQueue(() =>
            {
                for (;;)
                {
                    int i = System.Threading.Interlocked.Increment(ref cursor);
                    if (i >= distinct.Count)
                    {
                        return;
                    }

                    if (stop is not null && stop())
                    {
                        job.MarkRest(distinct.Count - i);
                        return;
                    }

                    try
                    {
                        // The shadow caster is built here, not on the main thread. The step that
                        // places a prop merges its surfaces into a cast-only mesh (the batch twin,
                        // or a single prop's proxy) and that merge reads every surface back. The
                        // merge is cached per mesh, so building it on this worker makes the step's
                        // own call a lookup. A plant that sways casts nothing: skipped.
                        // Classic casts through its own joined mesh (Engine_ClassicCasterMerge),
                        // built on the main thread: it reads the shared materials' metadata.
                        if (!Refii.Models.Engine_ClassicMaterial.Active &&
                            Resolve(distinct[i]) is { } resolved && !SwaysInWind(resolved))
                        {
                            Refii.Entities.Engine_ShadowProxy.MergedCaster(resolved, out _);
                        }
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        job.MarkOne();
                    }
                }
            });
        }

        return job;
    }

    private static void CollectPendingInstances(
        Node container, List<GeometryInstance3D> list, bool recursive)
    {
        foreach (Node child in container.GetChildren())
        {
            if (child is MultiMeshInstance3D multi &&
                multi.Multimesh is not null &&
                multi.Multimesh.Mesh is null &&
                multi.HasMeta(ModelPathMeta))
            {
                list.Add(multi);
            }
            else if (child is MeshInstance3D single &&
                single.Mesh is null &&
                single.HasMeta(ModelPathMeta))
            {
                list.Add(single);
            }

            if (recursive && child.GetChildCount() > 0)
            {
                CollectPendingInstances(child, list, recursive: true);
            }
        }
    }

    public static List<string> PendingModelPaths(Node container)
    {
        var paths = new List<string>();
        bool recursive = Engine.IsEditorHint() || Engine_LevelScene.ForceEditorMode;
        CollectPaths(container, paths, recursive);
        return paths;

        static void CollectPaths(Node parent, List<string> result, bool recurse)
        {
            foreach (Node child in parent.GetChildren())
            {
                if (child is MultiMeshInstance3D multi &&
                    multi.Visible &&
                    multi.Multimesh is not null &&
                    multi.Multimesh.Mesh is null &&
                    multi.HasMeta(ModelPathMeta))
                {
                    result.Add(multi.GetMeta(ModelPathMeta).AsString());
                }
                else if (child is MeshInstance3D single &&
                    single.Visible &&
                    single.Mesh is null &&
                    single.HasMeta(ModelPathMeta))
                {
                    result.Add(single.GetMeta(ModelPathMeta).AsString());
                }

                if (recurse && child.GetChildCount() > 0)
                {
                    CollectPaths(child, result, recurse: true);
                }
            }
        }
    }

    /// <summary>Stops further prefetch and closes Android bake publication without disposing shared results.</summary>
    public override void _ExitTree()
    {
        _prefetchStop = true;
        if (Refii.Core.Engine_Platform.IsAndroid)
            _retirementWorkers.Retire(() => { while (_bakeCompletions.TryDequeue(out _)) { } });
        Live.Remove(this);
        PublishProgress();
    }

    public override void _Process(double delta)
    {
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("propstreamer");
        if (!Enabled)
        {
            return;
        }

        Engine_PropFade.Tick();
        Engine_PropFarShading.Tick(GetViewport()?.GetCamera3D());
        Engine_BuildingOccluders.Pump();

        long drainFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        DrainVertexBakes();
        Refii.Core.Engine_FramePhases.Record("props.drain",
            (System.Diagnostics.Stopwatch.GetTimestamp() - drainFrom) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, plot: false);
        _watch.Restart();

        double budget = (Engine.IsEditorHint() || Engine_LevelScene.ForceEditorMode) ? 16.0 : MillisecondsPerFrame;
        bool poolDone = _prefetch is null || _prefetch.IsComplete;
        int untouched = 0;
        while (_remaining.Count != 0 && untouched < _remaining.Count)
        {
            if (_scan >= _remaining.Count)
            {
                _scan = 0;
            }

            GeometryInstance3D instance = _remaining[_scan];

            // Hidden props are streamed too: a placed prop is load work, done before control is
            // handed over, not deferred until a layer activates or a twin is shown mid-play.
            // Only what the guest creates later (a spawn, a quest object) arrives on demand,
            // through `EnsureStreamed`.
            bool settled = !GodotObject.IsInstanceValid(instance);
            if (!settled)
            {
                string path = _remainingPaths[_scan];
                bool peeked = TryPeek(path, out ArrayMesh? ready, out bool knownFailure);
                if (peeked && !knownFailure && !Refii.Models.Engine_ClassicMaterial.MeshSettled(ready))
                {
                    // Loaded, its classic shaders still compiling on the pool: it waits its
                    // turn rather than stall this frame on the compile (Engine_ClassicMaterial.MeshSettled).
                }
                else if (peeked)
                {
                    long stepFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                    TimeSpan stepPause = GC.GetTotalPauseDuration();
                    Step(instance, ready, knownFailure);
                    long stepTicks = System.Diagnostics.Stopwatch.GetTimestamp() - stepFrom;
                    Refii.Core.Engine_LoadTimeline.Add(Refii.Core.Engine_LoadTimeline.Category.PropStep, stepTicks);
                    double stepMs = stepTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    Refii.Core.Engine_FramePhases.Record("props.step", stepMs, plot: false);
                    if (stepMs > 8.0 && _slowStepsLogged++ < 48)
                    {
                        GD.Print($"[props] one step took {stepMs:0.0} ms on the main thread " +
                            $"(gc pause {(GC.GetTotalPauseDuration() - stepPause).TotalMilliseconds:0.0}) " +
                            $"(resolve {_stepResolveMs:0.0}, multimesh {_stepMultiMs:0.0}, bounds {_stepBoundsMs:0.0}, assign {_stepAssignMs:0.0}, bake {_stepBakeMs:0.0}, range {_stepRangeMs:0.0}, " +
                            $"sway {_stepSwayMs:0.0} [merge wait {_stepMergeWaitMs:0.0}, build {_stepMergeBuildMs:0.0}], light {_stepLightMs:0.0} [{Refii.Lighting.Engine_LevelBakedLighting.LastLightSplit}]): {path}");
                    }

                    settled = true;
                }
                else if (poolDone && _requeueRounds < RequeueRounds)
                {
                    // NOT INLINE. The fallback decoded the prop on the main thread — a single
                    // Step measured 172 ms in the market, the hitch a player feels as stutter.
                    // Everything the pool missed goes back to it once; only what it still
                    // cannot deliver is decoded here.
                    ++_requeueRounds;
                    var again = new List<string>(_remaining.Count);
                    for (int i = 0; i < _remaining.Count; ++i)
                    {
                        GeometryInstance3D pending = _remaining[i];
                        if (GodotObject.IsInstanceValid(pending) && pending.Visible)
                        {
                            again.Add(_remainingPaths[i]);
                        }
                    }

                    _prefetch = PrefetchModels(again, () => _prefetchStop);
                    PublishProgress();
                    return;
                }
                else if (poolDone)
                {
                    // Last resort: load on the main thread, after RequeueRounds prefetch rounds,
                    // and log slow loads by path.
                    long from = System.Diagnostics.Stopwatch.GetTimestamp();
                    Step(instance, null, false);
                    long syncLoadTicks = System.Diagnostics.Stopwatch.GetTimestamp() - from;
                    Refii.Core.Engine_LoadTimeline.Add(Refii.Core.Engine_LoadTimeline.Category.PropSyncLoad, syncLoadTicks);
                    double ms = syncLoadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    Refii.Core.Engine_FramePhases.Record("props.sync_load", ms, plot: true);
                    if (ms > 4.0 && _syncLoadsLogged++ < 24)
                    {
                        GD.Print($"[props] loaded on the main thread in {ms:0.0} ms after {RequeueRounds} prefetch rounds: {path}");
                    }

                    settled = true;
                }
            }

            if (settled)
            {
                _remaining.RemoveAt(_scan);
                _remainingPaths.RemoveAt(_scan);
                _done++;
                untouched = 0;
            }
            else
            {
                _scan++;
                untouched++;
            }

            if (_watch.Elapsed.TotalMilliseconds >= budget)
            {
                PublishProgress();
                return;
            }
        }

        if (_remaining.Count != 0)
        {
            PublishProgress();
            return;
        }

        PublishProgress();

        // Keep processing until every bake has drained and every ambient variant is bound: the
        // applied/ambient/misfit counts are incremented in the drain, so reporting earlier
        // undercounts, and a retry left queued would keep its prop on the base for good. And
        // until the last arrival has dithered in, or it would stand half-drawn for good.
        if (_bakesInFlight != 0 || _ambientRetries.Count != 0 || Engine_PropFade.Fading)
        {
            return;
        }

        SetProcess(false);

        if (!_reported)
        {
            _reported = true;
            GD.Print($"[props] bakes applied={_bakeApplied} (texture={_bakeTextured}) misfit={_bakeMisfit} keyless={_bakeKeyless}" +
                (Refii.Lighting.Engine_LevelBakedLighting.VertexBakeSamples > 0
                    ? $" vertexBake mean={Refii.Lighting.Engine_LevelBakedLighting.VertexBakeSum / Refii.Lighting.Engine_LevelBakedLighting.VertexBakeSamples:0.000} max={Refii.Lighting.Engine_LevelBakedLighting.VertexBakeMax:0.000} over {Refii.Lighting.Engine_LevelBakedLighting.VertexBakeSamples} vertices"
                    : ""));

            if (_keylessByModel.Count != 0)
            {
                var worst = new List<KeyValuePair<string, int>>(_keylessByModel);
                worst.Sort((a, b) => b.Value.CompareTo(a.Value));
                var line = new System.Text.StringBuilder("[props] keyless by model:");
                for (int i = 0; i < worst.Count && i < 16; i++)
                {
                    line.Append($" {worst[i].Key}x{worst[i].Value}");
                }

                if (worst.Count > 16)
                {
                    line.Append($" (+{worst.Count - 16} more models)");
                }

                GD.Print(line.ToString());
            }
            GD.Print($"[props] streamed {_loaded} model(s)" +
                (_failed == 0
                    ? string.Empty
                    : $", {_failed} failed ({_absent} absent from every bank, " +
                      $"{_undecodable} present but would not decode)"));
        }
    }

    /// <summary>
    /// Queues a prop whose geometry is not up yet — the guest revealed something we have not
    /// streamed.
    /// </summary>
    /// <remarks>
    /// Queued, never decoded inline: it is called from `Engine_StaticTwins.ShowTwin`, and an
    /// inline decode is unbounded main-thread work. Inserted at the front of the queue, so the
    /// prop appears a frame or two later. With hidden props streamed during the load, this
    /// should only see objects the guest created afterwards.
    /// </remarks>
    public void EnsureStreamed(MeshInstance3D node)
    {
        if (node.Mesh is not null || !node.HasMeta(ModelPathMeta) || _remaining.Contains(node))
        {
            return;
        }

        _remaining.Insert(0, node);
        _remainingPaths.Insert(0, node.GetMeta(ModelPathMeta).AsString());
        _scan = 0;
        SetProcess(true);
    }

    /// <summary>Prefetch rounds before a missing model is loaded on the main thread.</summary>
    private const int RequeueRounds = 3;

    private int _syncLoadsLogged;
    private int _slowStepsLogged;
    private int _slowDrainsLogged;
    private int _readbacksLogged;
    private double _stepBoundsMs;
    private double _stepResolveMs, _stepMultiMs, _stepAssignMs, _stepBakeMs, _stepRangeMs, _stepSwayMs, _stepLightMs;
    private double _stepMergeWaitMs, _stepMergeBuildMs;

    private static double Ms(long from) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static bool TryPeek(string path, out ArrayMesh? mesh, out bool knownFailure)
    {
        mesh = null;
        knownFailure = false;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out ArrayMesh? cached) && GodotObject.IsInstanceValid(cached))
            {
                mesh = cached;
                return true;
            }

            if (Failed.ContainsKey(path))
            {
                knownFailure = true;
                return true;
            }
        }

        return false;
    }

    private static int InstanceCount(GeometryInstance3D instance) =>
        instance is MultiMeshInstance3D multi && multi.Multimesh is not null
            ? multi.Multimesh.InstanceCount
            : 1;

    /// <summary>
    /// The region's lighting, from the one node that owns it.
    ///
    /// Resolved per call rather than cached: streaming runs for many frames and the time of day
    /// can move while it does, so a prop that arrives late has to be lit for the moment it
    /// arrives rather than for the moment streaming started.
    /// </summary>
    private static Refii.Lighting.LightingFrame? RegionLighting() =>
        Engine_SkyEnvironmentNode.Current?.Lighting;

    /// <summary>
    /// Swaps in a per-placement copy of the mesh carrying the level's AUTHORED per-vertex bake,
    /// when this placement has one and it fits.
    ///
    /// This is the guest's highest-priority lighting mode and it is real shipped data: 586 of
    /// them for this region, every one resolvable to a placement. It outranks the probe field,
    /// so the mode flag is raised to 3 and the probe sample the node is also carrying goes
    /// unused — matching the guest, which picks one mode per instance rather than blending.
    /// </summary>
    private void ApplyVertexBake(MeshInstance3D node, ArrayMesh mesh)
    {
        // HasMeta first — a nil default makes `GetMeta` take its ERR_FAIL path rather than return
        // the fallback, and most nodes reaching here carry no bake key at all.
        if (!node.HasMeta(Refii.Lighting.Engine_LevelBakedLighting.BakeKeyMeta))
        {
            // No bake key at all. If the probe sample also failed this node renders at mode 0 —
            // flat global ambient, the blue-grey slab in every dawn screenshot. Aggregated by
            // model rather than naming the first eight: the summary needs the SHAPE of the
            // population (which models, how many of each) to say which appender is dropping
            // keys, and eight arbitrary names could not.
            _bakeKeyless++;
            string keylessModel = node.GetMeta(ModelPathMeta, string.Empty).AsString();
            int slash = keylessModel.LastIndexOf('/');
            string leaf = slash >= 0 ? keylessModel[(slash + 1)..] : keylessModel;
            _keylessByModel.TryGetValue(leaf, out int seen);
            _keylessByModel[leaf] = seen + 1;

            return;
        }

        Variant stored = node.GetMeta(Refii.Lighting.Engine_LevelBakedLighting.BakeKeyMeta);
        ulong key = 0;
        if (stored.VariantType == Variant.Type.String)
        {
            string s = stored.AsString();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                s = s[2..];
            }
            ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out key);
        }
        else if (stored.VariantType == Variant.Type.Int)
        {
            key = (ulong)stored.AsInt64();
        }

        if (key == 0)
        {
            return;
        }

        string gdbPath = GetMeta(
            Refii.Lighting.Engine_LevelBakedLighting.LevelGdbMeta, string.Empty).AsString();

        // Built on a worker: the bake's per-vertex colours in the shared mesh's order. The
        // node shows the shared mesh unbaked until they land; the fallbacks and the accounting
        // run in the completion on the main thread.
        ulong nodeId = node.GetInstanceId();
        ++_bakesInFlight;
        _retirementWorkers.TryQueue(() =>
        {
            Color[][]? built = null;
            Refii.Lighting.LightmapContainer? container = null;
            try
            {
                // On the worker: resolving the container parses the level's .lmp on first call.
                container = Refii.Lighting.Engine_LevelBakedLighting.ContainerFor(gdbPath);
                built = Refii.Lighting.Engine_LevelBakedLighting.TryBuildVertexColours(container, key, mesh);
            }
            catch (Exception e)
            {
                GD.PushWarning($"[props] vertex bake failed: {e.GetType().Name}: {e.Message}");
            }

            _retirementWorkers.TryPublish(() => _bakeCompletions.Enqueue((nodeId, container, key, mesh, built)));
        });
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<(ulong NodeId, Refii.Lighting.LightmapContainer? Container, ulong Key, ArrayMesh Mesh, Color[][]? Baked)> _bakeCompletions = new();

    private int _bakesInFlight;

    /// <summary>Vertex bakes not yet applied and ambient variants not yet bound (the load hold waits for none).</summary>
    public int Settling => _bakesInFlight + _ambientRetries.Count;

    /// <summary>Bakes built and not yet applied; reported.</summary>
    public int BakesInFlight => _bakesInFlight;

    /// <summary>
    /// How long a frame may spend applying finished bakes. Each apply is a mesh swap plus an
    /// instance-lighting write, so the drain is bounded by time rather than count.
    /// </summary>
    private const double DrainMillisecondsPerFrame = 2.0;
    private double _finishSwapMs, _finishAmbientMs;
    private string _ambientSplit = string.Empty;
    private readonly Queue<(ulong Id, int Ambient, Texture2D? Lightmap, int[]? BakeStarts)> _ambientRetries = new();

    private void DrainVertexBakes()
    {
        long drainStart = System.Diagnostics.Stopwatch.GetTimestamp();
        // Under the load hold nothing is on screen: the drain takes the streamer's held budget.
        double drainMs = Refii.Models.Engine_ClassicLoad.Holding ? Math.Max(MillisecondsPerFrame, DrainMillisecondsPerFrame) : DrainMillisecondsPerFrame;
        double drainLimit = drainMs * System.Diagnostics.Stopwatch.Frequency / 1000.0;

        // Ambient variants that waited for their shader's compile: asked again, each once a frame.
        for (int pending = _ambientRetries.Count; pending > 0; pending--)
        {
            (ulong id, int ambient, Texture2D? lightmap, int[]? bakeStarts) = _ambientRetries.Dequeue();
            if (GodotObject.InstanceFromId(id) is MeshInstance3D retry && GodotObject.IsInstanceValid(retry) &&
                !ApplyClassicAmbient(retry, ambient, lightmap, bakeStarts))
            {
                _ambientRetries.Enqueue((id, ambient, lightmap, bakeStarts));
            }
        }

        int held = 0, queued = _bakeCompletions.Count;
        while (held < queued && _bakeCompletions.TryDequeue(out (ulong NodeId, Refii.Lighting.LightmapContainer? Container, ulong Key, ArrayMesh Mesh, Color[][]? Baked) done))
        {
            if (!Refii.Models.Engine_ClassicMaterial.MeshSettled(done.Mesh))
            {
                // Its shaders are still compiling: the completion waits a frame (see MeshSettled).
                _bakeCompletions.Enqueue(done);
                ++held;
                continue;
            }
            --_bakesInFlight;
            if (GodotObject.InstanceFromId(done.NodeId) is not MeshInstance3D node ||
                !GodotObject.IsInstanceValid(node))
            {
                continue;
            }

            long itemFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            TimeSpan itemPause = GC.GetTotalPauseDuration();
            _finishSwapMs = _finishAmbientMs = 0;
            FinishVertexBake(node, done.Container, done.Key, done.Mesh, done.Baked);
            double finishMs = Ms(itemFrom);

            // The bake mode the shader reads rides the instance parameters, which were set
            // when the shared mesh went up: set again now the mode is known.
            long lightFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
                node, RegionLighting(), staticProp: true);
            double lightMs = Ms(lightFrom);
            Refii.Lighting.Engine_GiRefresh.StaticGeometryArrived(node);
            Refii.Core.Engine_LoadTimeline.Add(Refii.Core.Engine_LoadTimeline.Category.PropBake,
                System.Diagnostics.Stopwatch.GetTimestamp() - itemFrom);

            // The budget below can only stop the next item, so a single slow apply is logged
            // with its mesh/light split.
            if (finishMs + lightMs > 8.0 && _slowDrainsLogged++ < 48)
            {
                GD.Print($"[props] one bake applied in {finishMs + lightMs:0.0} ms " +
                    $"(gc pause {(GC.GetTotalPauseDuration() - itemPause).TotalMilliseconds:0.0}) " +
                    $"(mesh {finishMs:0.0} = swap {_finishSwapMs:0.0} + classic ambient {_finishAmbientMs:0.0} [{_ambientSplit}], light {lightMs:0.0} [{Refii.Lighting.Engine_LevelBakedLighting.LastLightSplit}]): " +
                    node.GetMeta(ModelPathMeta, string.Empty).AsString());
            }

            // The rest wait a frame. A prop shows its shared mesh until its own bake lands,
            // which is what it was already doing while the bake was being built.
            if (System.Diagnostics.Stopwatch.GetTimestamp() - drainStart >= drainLimit)
            {
                return;
            }
        }
    }

    private void FinishVertexBake(MeshInstance3D node, Refii.Lighting.LightmapContainer? container, ulong key, ArrayMesh mesh, Color[][]? baked)
    {

        if (baked is not null)
        {
            long swapFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            // The colours into the shared texture; each surface's start inside the block is its material's.
            int bakeBase = Refii.Models.Engine_ClassicVertexBakes.Add(baked);
            var bakeStarts = new int[baked.Length];
            for (int s = 1; s < baked.Length; s++)
            {
                bakeStarts[s] = bakeStarts[s - 1] + baked[s - 1].Length;
            }
            node.SetMeta(BakeBaseMeta, bakeBase);
            node.SetMeta(Refii.Lighting.Engine_LevelBakedLighting.BakeModeMeta, 3);
            ++_bakeApplied;
            _finishSwapMs = Ms(swapFrom);
            // The title's shader for a vertex-baked instance is the AMB1 pair, reading the bake
            // as its vertex colour: here from the placement's block of the shared texture.
            long ambientFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            long f0 = Refii.Models.Engine_ClassicMaterial.AmbientFindTicks, c0 = Refii.Models.Engine_ClassicMaterial.AmbientComposeTicks,
                d0 = Refii.Models.Engine_ClassicMaterial.AmbientDuplicateTicks, b0 = Refii.Models.Engine_ClassicMaterial.AmbientBindTicks;
            if (!ApplyClassicAmbient(node, 1, null, bakeStarts))
            {
                _ambientRetries.Enqueue((node.GetInstanceId(), 1, null, bakeStarts));
            }
            _finishAmbientMs = Ms(ambientFrom);
            double k = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _ambientSplit = $"find {(Refii.Models.Engine_ClassicMaterial.AmbientFindTicks - f0) * k:0.0}, compose {(Refii.Models.Engine_ClassicMaterial.AmbientComposeTicks - c0) * k:0.0}, " +
                $"duplicate {(Refii.Models.Engine_ClassicMaterial.AmbientDuplicateTicks - d0) * k:0.0}, bind {(Refii.Models.Engine_ClassicMaterial.AmbientBindTicks - b0) * k:0.0}";
            return;
        }

        // THE TEXTURE BAKE — the amb3 path, building interiors. One 64x64 per object in the
        // .lmp, served from the region atlas with a per-instance rect; the UVs are the mesh's
        // own TexUV2 channel. Vertex bake outranks it, matching the guest's packet ctor
        // (`sub_82219978`: mode 1, else 3, else 2) — for this key the maps are disjoint anyway.
        if (Refii.Lighting.Engine_LevelBakedLighting.TryGetObjectLightmapRect(
                container, key, out Godot.Rect2 rect))
        {
            node.SetInstanceShaderParameter("lightmap_rect",
                new Godot.Vector4(rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y));
            node.SetMeta(Refii.Lighting.Engine_LevelBakedLighting.BakeModeMeta, 4);
            ++_bakeApplied;
            ++_bakeTextured;
            // The AMB3 pair with this object's own lightmap bound, as the title binds it
            // (`sub_82225100`, sampler 3) — one texture per drawable, no atlas.
            Texture2D? objectLightmap = Refii.Lighting.Engine_LevelBakedLighting.ObjectLightmapTexture(container, key);
            if (!ApplyClassicAmbient(node, 3, objectLightmap, null))
            {
                _ambientRetries.Enqueue((node.GetInstanceId(), 3, objectLightmap, null));
            }
            return;
        }

        // NO THIRD RUNG. The .lmp's 48-byte blocks are a precomputed PROBE RESULT the title
        // reads for skinned models only (`sub_82219178` 0x8221920c, `*(model+100) == 1`); a
        // static with neither stream nor texture is Amb 0, flat ambient (`sub_82219978`), and
        // one with PRT data takes the probe field at its position — the sample the build stored.

        if (_bakeMisfit++ < 8)
        {
            // THE TWO COUNTS, because "misfit" alone cannot distinguish a bake authored for a
            // DIFFERENT model (counts a world apart) from a splitting detail (counts a few
            // vertices apart) — the facade eight have sat unexplained for exactly this lack.
            int bakeLength = container is not null &&
                container.VertexBakes.TryGetValue(key, out uint[]? colours)
                ? colours.Length
                : -1;
            int meshTotal = mesh.GetMeta(
                Refii.Models.Engine_MdlMesh.SourceVertexTotalMeta, default) is
                { VariantType: Godot.Variant.Type.Int } totalMeta
                ? totalMeta.AsInt32()
                : -1;
            GD.Print($"[props] BAKE MISFIT: key=0x{key:X16} bake={bakeLength} " +
                $"model_verts={meshTotal} '{node.Name}' model=" +
                $"'{node.GetMeta(ModelPathMeta, string.Empty).AsString()}'");
        }
    }

    /// <summary>
    /// Under the classic renderer, re-keys this instance's surfaces to the ambient source the
    /// title would pick for it, as per-surface overrides over the shared material; with
    /// <paramref name="bakeStarts"/> the vertex bake's pair (Engine_ClassicMaterial.WithVertexBake)
    /// and the placement's block named on the node. False when a pair's shader is still
    /// compiling: nothing is applied, the caller asks again.
    /// </summary>
    private static bool ApplyClassicAmbient(MeshInstance3D node, int ambient, Texture2D? lightmap, int[]? bakeStarts)
    {
        if (!Refii.Models.Engine_ClassicMaterial.Active || node.Mesh is null)
        {
            return true;
        }
        int surfaces = node.Mesh.GetSurfaceCount();
        Refii.Models.Engine_ClassicMaterial.AmbientPending = false;
        var variants = new ShaderMaterial?[surfaces];
        for (int i = 0; i < surfaces; i++)
        {
            if (node.Mesh.SurfaceGetMaterial(i) is not ShaderMaterial shared ||
                !shared.HasMeta(Refii.Models.Engine_ClassicMaterial.ClassicMeta) ||
                shared.GetMeta(Refii.Models.Engine_ClassicMaterial.FamilyMetaName, string.Empty).AsString() == "TREEMATERIAL")
            {
                // A tree keeps its AMB2 pair whatever the placement baked (`sub_822092B0`).
                continue;
            }
            variants[i] = bakeStarts is not null && i < bakeStarts.Length
                ? Refii.Models.Engine_ClassicMaterial.WithVertexBake(shared, bakeStarts[i])
                : Refii.Models.Engine_ClassicMaterial.WithAmbient(shared, ambient, lightmap);
        }
        // A variant whose shader is still compiling: nothing is applied, the caller asks again.
        if (Refii.Models.Engine_ClassicMaterial.AmbientPending)
        {
            Refii.Models.Engine_ClassicMaterial.AmbientPending = false;
            return false;
        }
        for (int i = 0; i < surfaces; i++)
        {
            if (variants[i] is { } variant)
            {
                node.SetSurfaceOverrideMaterial(i, variant);
            }
        }
        // After the overrides: an instance parameter needs a material that declares it.
        if (bakeStarts is not null && node.HasMeta(BakeBaseMeta))
        {
            node.SetInstanceShaderParameter(Refii.Models.Engine_ClassicVertexBakes.BaseUniform,
                node.GetMeta(BakeBaseMeta).AsInt32());
        }
        return true;
    }

    /// <summary>A vertex-baked placement's block in the shared bake texture.</summary>
    private const string BakeBaseMeta = "classic_bake_base";

    private int _bakeApplied;

    private int _bakeKeyless;

    private readonly Dictionary<string, int> _keylessByModel = new();

    private int _bakeTextured;


    private int _bakeMisfit;

    private void Step(GeometryInstance3D instance, ArrayMesh? ready, bool knownFailure)
    {
        string path = string.Empty;

        try
        {
            if (!GodotObject.IsInstanceValid(instance))
            {
                return;
            }

            path = instance.GetMeta(ModelPathMeta).AsString();

            bool present;
            string why;
            ArrayMesh? mesh = ready;
            _stepResolveMs = 0.0;
            _stepMultiMs = 0.0;
            _stepBoundsMs = 0.0;
            if (mesh is null && !knownFailure)
            {
                long tr = System.Diagnostics.Stopwatch.GetTimestamp();
                mesh = Resolve(path, out present, out why);
                _stepResolveMs = Ms(tr);
            }
            else
            {
                (present, why) = FailureOf(path);
            }

            if (mesh is null)
            {
                _failed++;

                // WHICH failure, not just that there was one. "Absent" means the path we
                // asked for is in no bank — an index that synthesised a name by convention
                // rather than reading one, which is a different defect from a model that is
                // there and will not decode. The two were indistinguishable and the counts
                // got attributed to the parser.
                if (present)
                {
                    _undecodable++;
                }
                else
                {
                    _absent++;
                }

                if (_failed <= 32)
                {
                    GD.Print($"[props] stream failed ({(present ? why : "absent from every bank")}): {path}");
                }

                return;
            }

            if (instance is MultiMeshInstance3D multi && multi.Multimesh is not null)
            {
                long tm = System.Diagnostics.Stopwatch.GetTimestamp();
                multi.Multimesh.Mesh = mesh;
                _stepMultiMs = Ms(tm);
                tm = System.Diagnostics.Stopwatch.GetTimestamp();

                // The provisional bounds give way to the union of every instance's transformed
                // mesh box. ONE buffer read, not a transform read per instance: each is a
                // round trip to the rendering server. A collapsed (hidden) instance measures
                // as a point; `Engine_LevelProps.CoverInstance` widens for it when it is shown.
                int count = multi.Multimesh.InstanceCount;
                if (Engine_PropShadow.StatedTransforms(multi.Multimesh) is { } stated)
                {
                    // From the build's own record: no buffer read, no render-thread sync.
                    Aabb local = mesh.GetAabb();
                    Aabb bounds = default;
                    for (int i = 0; i < count; i++)
                    {
                        Aabb box = stated[i] * local;
                        bounds = i == 0 ? box : bounds.Merge(box);
                    }

                    Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.PropBounds);
                    multi.Multimesh.CustomAabb = bounds.Grow(SwaysInWind(mesh)
                        ? Engine_LevelProps.ModelReach : 1f);
                    count = 0;
                }

                if (count > 0 && _readbacksLogged++ < 8)
                {
                    GD.Print($"[props] batch bounds read back from the render thread ({count} instance(s), " +
                        $"{(Engine_PropShadow.HasRecord(multi.Multimesh) ? "record length differs" : "no record")}): {path}");
                }
                float[] buffer = count > 0 ? Refii.Core.Engine_ServerSyncs.Buffer(multi.Multimesh) : Array.Empty<float>();
                int stride = count > 0 ? buffer.Length / count : 0;
                if (stride >= 12)
                {
                    Aabb local = mesh.GetAabb();
                    Aabb bounds = default;
                    for (int i = 0; i < count; i++)
                    {
                        int o = i * stride;
                        var placed = new Transform3D(
                            new Vector3(buffer[o], buffer[o + 4], buffer[o + 8]),
                            new Vector3(buffer[o + 1], buffer[o + 5], buffer[o + 9]),
                            new Vector3(buffer[o + 2], buffer[o + 6], buffer[o + 10]),
                            new Vector3(buffer[o + 3], buffer[o + 7], buffer[o + 11]));
                        Aabb box = placed * local;
                        bounds = i == 0 ? box : bounds.Merge(box);
                    }

                    // A wind-bent plant leaves its mesh box; it keeps the wide margin.
                    Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.PropBounds);
                    multi.Multimesh.CustomAabb = bounds.Grow(SwaysInWind(mesh)
                        ? Engine_LevelProps.ModelReach : 1f);
                }
                _stepBoundsMs = Ms(tm);
            }
            else if (instance is MeshInstance3D single)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                single.Mesh = mesh;
                _stepAssignMs = Ms(t0);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp();

                // A VERTEX BAKE IS A PRIVATE COPY OF THE MESH, and only the fable model reads it: under
                // the unified model the shader multiplies the bake by `1 - fable_unified` = 0
                // (`fable_ambient`). Built anyway, it gave every baked prop a mesh of its own, and
                // Godot merges neighbouring draws into one instanced draw only when they share
                // mesh and material (`_fill_render_list`, equal sort keys) — so no two copies of a
                // model ever merged. The lighting model is fixed for the run.
                // In Editor mode, skip private mesh clones to keep RAM lean and UI responsive.
                if (!Engine.IsEditorHint() && !Engine_LevelScene.ForceEditorMode && !Refii.Lighting.Engine_LightingFrame.ModernModel)
                {
                    ApplyVertexBake(single, mesh);
                }

                _stepBakeMs = Ms(t1);
            }

            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            ApplyVisibilityRange(instance, mesh);
            if (Engine_BuildingOccluders.Enabled)
            {
                Vector3 size = mesh.GetAabb().Size;
                if (IsBuilding(instance, path, MathF.Max(size.X, MathF.Max(size.Y, size.Z))))
                {
                    Engine_BuildingOccluders.Add(instance, mesh, instance is MultiMeshInstance3D placed
                        ? Engine_PropShadow.StatedTransforms(placed.Multimesh) ?? BatchTransforms(placed.Multimesh)
                        : null);
                }
            }
            Refii.Models.Engine_ClassicBillboards.Register(instance, mesh);
            Engine_PropFarShading.Register(instance, mesh);
            if (instance is MeshInstance3D displaced && displaced.HasMeta(RecordMeta))
            {
                uint displacedRecord = (uint)displaced.GetMeta(RecordMeta).AsInt64();
                if (!Refii.Models.Engine_ClassicDisplacement.AdoptRecord(displacedRecord, displaced, mesh))
                {
                    Refii.Fx.Engine_DisplacementMeshes.Adopt(displacedRecord, displaced, mesh);
                }
            }
            _stepRangeMs = Ms(t2);

            // A SWAYING PLANT CASTS NO SUN SHADOW. Its leaf cards are alpha-tested slivers
            // under a wind bend, sub-texel in the shadow cascades — what they cast is not a
            // shadow but temporal noise that FLASHES across themselves and the ground as the
            // bend moves. The guest never had this: its foliage cast nothing (the sun shadow
            // is the remaster's). Trees are unaffected — they do not sway (mode 2 holds still
            // until treematerial exists) and their canopy shadows are stable and worth having.
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            Refii.Entities.Engine_ShadowProxy.TakeMergeTimes();
            if (Refii.Models.Engine_ClassicMaterial.Active)
            {
                // Under classic Godot's lights cast nothing; the guest's casters go into the
                // cascades' own scene, as the node was marked when it was built.
                Refii.Models.Engine_ClassicCasters.Register(instance);
            }
            else if (instance.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)
            {
                // Already cast-only — a tree's `_shadow` model. Every hand-off below turns the
                // node's own casting Off so something else can cast for it, and Off on a
                // shadows-only node would make it drawn and not casting.
            }
            else if (SwaysInWind(mesh) || Refii.Models.Engine_ClassicCasters.NeverCasts(instance))
            {
                // A swaying plant, or a prop the guest states casts nothing.
                Refii.Lighting.Engine_ShadowReach.Exclude(instance);
            }
            else if (instance is MultiMeshInstance3D castingBatch)
            {
                // After the visibility range above, which the twin copies.
                Engine_PropShadow.Attach(castingBatch, mesh);
            }
            else if (instance is MeshInstance3D castingProp &&
                castingProp.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off)
            {
                // Built from the shared mesh: a vertex bake later swaps in a per-instance copy of
                // the same shape, which would merge once per prop instead of once per model.
                // In Editor mode, skip attaching ShadowProxy child nodes to keep the scene hierarchy clean.
                if (!Engine.IsEditorHint() && !Engine_LevelScene.ForceEditorMode)
                {
                    Refii.Entities.Engine_ShadowProxy.Sync(castingProp);
                }
            }

            // Modern: the prop's light zone, once the walk has joined its record to an entity.
            if (Refii.Lighting.Engine_LightZones.Active && instance.HasMeta(RecordMeta))
            {
                Refii.Lighting.Engine_LightZones.AddProp(instance, (uint)instance.GetMeta(RecordMeta).AsInt64());
            }

            _stepSwayMs = Ms(t3);  // "sway": the shadow-caster hand-off above, named for its first branch
            (_stepMergeWaitMs, _stepMergeBuildMs) = Refii.Entities.Engine_ShadowProxy.TakeMergeTimes();


            // LIGHT IT NOW, not at build time. Until this line the node had no material, and
            // Godot will not carry an instance shader parameter on a node whose material it
            // cannot see — so the lighting written while the scene was being built never
            // reached the saved file and never reached the screen. The probe sample rode along
            // as metadata instead; this is where it becomes shader state.
            long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
            Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
                instance, RegionLighting(), staticProp: true);
            _stepLightMs = Ms(t4);
            Refii.Lighting.Engine_GiRefresh.StaticGeometryArrived(instance);

            // It stood this frame, wherever the eye is: a copy delivered inside its radius has no
            // edge to cross, so it dithers in from now. After the hand-off, so its caster goes too.
            Engine_PropFade.BeginArrival(instance);

            _loaded++;
        }
        catch (Exception exception)
        {
            _failed++;
            if (_failed <= 8)
            {
                GD.Print($"[props] stream error on '{path}': {exception.Message}");
            }
        }
    }

    /// <summary>Whether a plant sways: only mode-1 plants with an authored amplitude carry the wind
    /// meta (Engine_MdlMaterial.WindMetaName). A swaying plant casts no shadow.</summary>
    private static bool SwaysInWind(ArrayMesh candidate)
    {
        for (int s = 0; s < candidate.GetSurfaceCount(); s++)
        {
            if (candidate.SurfaceGetMaterial(s) is ShaderMaterial shader &&
                shader.HasMeta(Refii.Models.Engine_MdlMaterial.WindMetaName))
            {
                return true;
            }
        }

        return false;
    }

    public static ArrayMesh? Resolve(string virtualPath) => Resolve(virtualPath, out _);

    /// <summary>Why the last <see cref="Resolve(string, out bool)"/> returned null.</summary>
    public static string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// The mesh at a virtual path, or null.
    ///
    /// <paramref name="present"/> separates the two ways null happens: false when the banks
    /// hold no such entry at all, true when they do and it would not decode. Only the second
    /// is a reader defect; the first says whatever produced the path invented it.
    /// </summary>
    public static ArrayMesh? Resolve(string virtualPath, out bool present) =>
        Resolve(virtualPath, out present, out _);

    private static readonly Dictionary<string, (bool Present, string Reason)> Failed = new();

    private static readonly Dictionary<string, object> InFlight = new();

    private static (bool Present, string Reason) FailureOf(string path)
    {
        lock (Cache)
        {
            return Failed.TryGetValue(path, out (bool Present, string Reason) failure)
                ? failure
                : (false, "absent from every bank");
        }
    }

    public static ArrayMesh? Resolve(string virtualPath, out bool present, out string reason)
    {
        present = false;
        reason = string.Empty;
        if (virtualPath.Length == 0)
        {
            return null;
        }

        // LOCKED, because a level change starts the next region's build while this region's
        // props are still paging in. A `Dictionary` written by two threads does not merely lose
        // an entry — a resize caught mid-flight corrupts its buckets and the next lookup spins
        // forever, so the failure is a hang rather than a wrong mesh.
        //
        // Only the lookup and the store are held. Decoding stays outside: it is the expensive
        // half, and the worst a race there can do is build the same mesh twice and keep the
        // second, which costs one allocation and is otherwise indistinguishable.
        object gate;
        lock (Cache)
        {
            if (Cache.TryGetValue(virtualPath, out ArrayMesh? cached) &&
                GodotObject.IsInstanceValid(cached))
            {
                present = true;
                return cached;
            }

            if (Failed.TryGetValue(virtualPath, out (bool Present, string Reason) failure))
            {
                present = failure.Present;
                reason = failure.Reason;
                LastError = reason;
                return null;
            }

            if (!InFlight.TryGetValue(virtualPath, out gate!))
            {
                gate = new object();
                InFlight[virtualPath] = gate;
            }
        }

        lock (gate)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(virtualPath, out ArrayMesh? cached) &&
                    GodotObject.IsInstanceValid(cached))
                {
                    present = true;
                    return cached;
                }

                if (Failed.TryGetValue(virtualPath, out (bool Present, string Reason) failure))
                {
                    present = failure.Present;
                    reason = failure.Reason;
                    LastError = reason;
                    return null;
                }
            }

            ArrayMesh? mesh = null;
            try
            {
                byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(virtualPath);
                if (bytes is null)
                {
                    reason = "absent from every bank";
                    return null;
                }

                present = true;

                // The reader says WHY it gave up; discarding that leaves the fifteen or so models a
                // level fails to load with no reason recorded anywhere.
                mesh = Engine_MdlMesh.TryCreateMesh(bytes, virtualPath, out reason, out _);
                if (mesh is null)
                {
                    reason = reason.Length != 0 ? reason : "no reason given";
                    LastError = reason;
                    return null;
                }

                mesh.ResourceName = Engine_LevelProps.LeafOf(virtualPath);
                return mesh;
            }
            finally
            {
                lock (Cache)
                {
                    if (mesh is not null)
                    {
                        Cache[virtualPath] = mesh;
                    }
                    else
                    {
                        Failed[virtualPath] = (present, reason);
                    }

                    InFlight.Remove(virtualPath);
                }
            }
        }
    }
}
