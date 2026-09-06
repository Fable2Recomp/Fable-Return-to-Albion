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

    private readonly List<GeometryInstance3D> _pending = new();

    private readonly List<GeometryInstance3D> _remaining = new();

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
        _scan = 0;
        _done = 0;
        _loaded = 0;
        _failed = 0;
        _reported = false;

        foreach (Node child in GetChildren())
        {
            if (child is MultiMeshInstance3D multi &&
                multi.Multimesh is not null &&
                multi.Multimesh.Mesh is null &&
                multi.HasMeta(ModelPathMeta))
            {
                _pending.Add(multi);
            }
            else if (child is MeshInstance3D single &&
                single.Mesh is null &&
                single.HasMeta(ModelPathMeta))
            {
                _pending.Add(single);
            }
        }

        _pending.Sort((a, b) => InstanceCount(b).CompareTo(InstanceCount(a)));
        _remaining.AddRange(_pending);

        StartPrefetch();

        SetProcess(_pending.Count != 0);
        PublishProgress();
    }

    private volatile bool _prefetchStop;

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

        var job = new PrefetchJob(distinct.Count);

        int workers = Math.Min(Math.Max(2, System.Environment.ProcessorCount - 1), 8);
        workers = Math.Min(workers, distinct.Count);

        int cursor = -1;
        for (int w = 0; w < workers; ++w)
        {
            System.Threading.Tasks.Task.Run(() =>
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
                        Resolve(distinct[i]);
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

    public static List<string> PendingModelPaths(Node container)
    {
        var paths = new List<string>();
        foreach (Node child in container.GetChildren())
        {
            if (child is MultiMeshInstance3D multi &&
                multi.Visible &&
                multi.Multimesh is not null &&
                multi.Multimesh.Mesh is null &&
                multi.HasMeta(ModelPathMeta))
            {
                paths.Add(multi.GetMeta(ModelPathMeta).AsString());
            }
            else if (child is MeshInstance3D single &&
                single.Visible &&
                single.Mesh is null &&
                single.HasMeta(ModelPathMeta))
            {
                paths.Add(single.GetMeta(ModelPathMeta).AsString());
            }
        }

        return paths;
    }

    public override void _ExitTree()
    {
        _prefetchStop = true;
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

        _watch.Restart();

        double budget = Engine.IsEditorHint() ? double.PositiveInfinity : MillisecondsPerFrame;
        bool poolDone = _prefetch is null || _prefetch.IsComplete;
        int untouched = 0;
        while (_remaining.Count != 0 && untouched < _remaining.Count)
        {
            if (_scan >= _remaining.Count)
            {
                _scan = 0;
            }

            GeometryInstance3D instance = _remaining[_scan];

            // HIDDEN PROPS ARE NOT STREAMED. In the running game the guest owns most of these
            // and its own copy is what you see, so decoding their geometry is work for a mesh
            // nothing will draw — it was 45% of the streaming in BWSSlums. They are not dropped
            // though: `EnsureStreamed` loads one the moment it is shown again, which is what
            // happens when a layer activates and the guest hands a record back.
            bool settled = !GodotObject.IsInstanceValid(instance) || !instance.Visible;
            if (!settled)
            {
                string path = instance.GetMeta(ModelPathMeta).AsString();
                if (TryPeek(path, out ArrayMesh? ready, out bool knownFailure))
                {
                    Step(instance, ready, knownFailure);
                    settled = true;
                }
                else if (poolDone && _requeueRounds < 1)
                {
                    // NOT INLINE. The fallback decoded the prop on the main thread — a single
                    // Step measured 172 ms in the market, the hitch a player feels as stutter.
                    // Everything the pool missed goes back to it once; only what it still
                    // cannot deliver is decoded here.
                    ++_requeueRounds;
                    var again = new List<string>();
                    foreach (GeometryInstance3D pending in _remaining)
                    {
                        if (GodotObject.IsInstanceValid(pending) && pending.Visible)
                        {
                            again.Add(pending.GetMeta(ModelPathMeta).AsString());
                        }
                    }

                    _prefetch = PrefetchModels(again, () => _prefetchStop);
                    PublishProgress();
                    return;
                }
                else if (poolDone)
                {
                    Step(instance, null, false);
                    settled = true;
                }
            }

            if (settled)
            {
                _remaining.RemoveAt(_scan);
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

        SetProcess(false);
        PublishProgress();

        if (!_reported)
        {
            _reported = true;
            GD.Print($"[props] bakes applied={_bakeApplied} (texture={_bakeTextured}) ambient={_bakeAmbient} misfit={_bakeMisfit} keyless={_bakeKeyless}" +
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
    /// Streams one prop NOW, if it has not been already.
    ///
    /// For a prop that was hidden when the streaming pass reached it and has since been shown
    /// again — the guest activating a layer it had gated off. The pass is a one-way cursor, so
    /// it will never come back to it on its own.
    /// </summary>
    public void EnsureStreamed(MeshInstance3D node)
    {
        if (node.Mesh is null && node.HasMeta(ModelPathMeta))
        {
            Step(node, null, false);
        }
    }

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
        if (!node.HasMeta(Refii.Lighting.Engine_LevelBakedLighting.BakeKeyMeta) ||
            node.GetMeta(Refii.Lighting.Engine_LevelBakedLighting.BakeKeyMeta)
                is not { VariantType: Variant.Type.Int } stored)
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

        string gdbPath = GetMeta(
            Refii.Lighting.Engine_LevelBakedLighting.LevelGdbMeta, string.Empty).AsString();

        Refii.Lighting.LightmapContainer? container =
            Refii.Lighting.Engine_LevelBakedLighting.ContainerFor(gdbPath);
        ulong key = (ulong)stored.AsInt64();

        ArrayMesh? baked =
            Refii.Lighting.Engine_LevelBakedLighting.TryBuildVertexBake(container, key, mesh);

        if (baked is not null)
        {
            node.Mesh = baked;
            node.SetMeta(Refii.Lighting.Engine_LevelBakedLighting.BakeModeMeta, 3);
            ++_bakeApplied;
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
            return;
        }

        // THE THIRD RUNG: the guest's per-object ambient (the .lmp 48-byte blocks). This is
        // what lights a movable crate or rock — the artists never baked those, and the probe
        // FIELD is sparse, so without this they fell to flat global ambient (the mode-0 red
        // population in the bake-mode debug view). It rides the existing probe uniforms:
        // ApplyInstanceLighting below turns the meta into shader state as mode 1.
        if (Refii.Lighting.Engine_LevelBakedLighting.TryGetObjectAmbient(
                container, key,
                out Godot.Vector4 ar, out Godot.Vector4 ag, out Godot.Vector4 ab))
        {
            node.SetMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMeta,
                new Godot.Collections.Array<Godot.Vector4> { ar, ag, ab });
            ++_bakeAmbient;
            return;
        }

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

    private int _bakeApplied;

    private int _bakeKeyless;

    private readonly Dictionary<string, int> _keylessByModel = new();

    private int _bakeTextured;

    private int _bakeAmbient;

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
            if (mesh is null && !knownFailure)
            {
                mesh = Resolve(path, out present, out why);
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
                multi.Multimesh.Mesh = mesh;

                Aabb local = mesh.GetAabb();
                float reach = MathF.Max(local.Position.Length(), local.End.Length());
                if (float.IsFinite(reach) && reach > Engine_LevelProps.ModelReach)
                {
                    multi.Multimesh.CustomAabb = multi.Multimesh.CustomAabb.Grow(reach);
                }
            }
            else if (instance is MeshInstance3D single)
            {
                single.Mesh = mesh;
                ApplyVertexBake(single, mesh);
            }

            // A SWAYING PLANT CASTS NO SUN SHADOW. Its leaf cards are alpha-tested slivers
            // under a wind bend, sub-texel in the shadow cascades — what they cast is not a
            // shadow but temporal noise that FLASHES across themselves and the ground as the
            // bend moves. The guest never had this: its foliage cast nothing (the sun shadow
            // is the remaster's). Trees are unaffected — they do not sway (mode 2 holds still
            // until treematerial exists) and their canopy shadows are stable and worth having.
            if (SwaysInWind(mesh))
            {
                instance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            }

            static bool SwaysInWind(ArrayMesh candidate)
            {
                for (int s = 0; s < candidate.GetSurfaceCount(); s++)
                {
                    // `wind_amplitude` is only ever SET on mode-1 plants with an authored
                    // amplitude (Engine_MdlMaterial.ApplyWind); everything else reads back nil.
                    if (candidate.SurfaceGetMaterial(s) is ShaderMaterial shader &&
                        shader.GetShaderParameter("wind_amplitude").AsSingle() > 0f)
                    {
                        return true;
                    }
                }

                return false;
            }

            // LIGHT IT NOW, not at build time. Until this line the node had no material, and
            // Godot will not carry an instance shader parameter on a node whose material it
            // cannot see — so the lighting written while the scene was being built never
            // reached the saved file and never reached the screen. The probe sample rode along
            // as metadata instead; this is where it becomes shader state.
            Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
                instance, RegionLighting());

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
