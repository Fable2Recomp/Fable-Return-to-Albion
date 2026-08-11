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

    private readonly Stopwatch _watch = new();

    private int _cursor;

    private int _loaded;

    private int _failed;

    /// <summary>Failures whose path is in no bank — the index invented the name.</summary>
    private int _absent;

    /// <summary>Failures whose bytes are there and would not decode — a reader defect.</summary>
    private int _undecodable;

    private bool _reported;

    [Export] public double MillisecondsPerFrame { get; set; } = 25.0;

    [Export] public bool Enabled { get; set; } = true;

    public int Total => _pending.Count;

    public int Done => _cursor;

    public static event Action<int, int>? ProgressChanged;

    public static void ClearCache()
    {
        lock (Cache)
        {
            Cache.Clear();
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
        _cursor = 0;
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

        SetProcess(_pending.Count != 0);
        PublishProgress();
    }

    public override void _ExitTree()
    {
        Live.Remove(this);
        PublishProgress();
    }

    public override void _Process(double delta)
    {
        if (!Enabled)
        {
            return;
        }

        _watch.Restart();

        while (_cursor < _pending.Count)
        {
            GeometryInstance3D instance = _pending[_cursor++];

            // HIDDEN PROPS ARE NOT STREAMED. In the running game the guest owns most of these
            // and its own copy is what you see, so decoding their geometry is work for a mesh
            // nothing will draw — it was 45% of the streaming in BWSSlums. They are not dropped
            // though: `EnsureStreamed` loads one the moment it is shown again, which is what
            // happens when a layer activates and the guest hands a record back.
            if (!instance.Visible)
            {
                continue;
            }

            Step(instance);

            if (_watch.Elapsed.TotalMilliseconds >= MillisecondsPerFrame)
            {
                PublishProgress();
                return;
            }
        }

        SetProcess(false);
        PublishProgress();

        if (!_reported)
        {
            _reported = true;
            GD.Print($"[props] vertex bakes applied={_bakeApplied} misfit={_bakeMisfit}");
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
            Step(node);
        }
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
        if (node.GetMeta(Refii.Lighting.Engine_LevelBakedLighting.BakeKeyMeta, default)
            is not { VariantType: Variant.Type.Int } stored)
        {
            return;
        }

        string gdbPath = GetMeta(
            Refii.Lighting.Engine_LevelBakedLighting.LevelGdbMeta, string.Empty).AsString();

        ArrayMesh? baked = Refii.Lighting.Engine_LevelBakedLighting.TryBuildVertexBake(
            Refii.Lighting.Engine_LevelBakedLighting.ContainerFor(gdbPath),
            (ulong)stored.AsInt64(),
            mesh);

        if (baked is null)
        {
            ++_bakeMisfit;
            return;
        }

        node.Mesh = baked;
        node.SetMeta(Refii.Lighting.Engine_LevelBakedLighting.BakeModeMeta, 3);
        ++_bakeApplied;
    }

    private int _bakeApplied;

    private int _bakeMisfit;

    private void Step(GeometryInstance3D instance)
    {
        string path = string.Empty;

        try
        {
            if (!GodotObject.IsInstanceValid(instance))
            {
                return;
            }

            path = instance.GetMeta(ModelPathMeta).AsString();

            ArrayMesh? mesh = Resolve(path, out bool present);
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
                    GD.Print($"[props] stream failed ({(present ? LastError : "absent from every bank")}): {path}");
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
    public static ArrayMesh? Resolve(string virtualPath, out bool present)
    {
        present = false;
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
        lock (Cache)
        {
            if (Cache.TryGetValue(virtualPath, out ArrayMesh? cached) &&
                GodotObject.IsInstanceValid(cached))
            {
                present = true;
                return cached;
            }
        }

        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(virtualPath);
        if (bytes is null)
        {
            return null;
        }

        present = true;

        // The reader says WHY it gave up; discarding that leaves the fifteen or so models a
        // level fails to load with no reason recorded anywhere.
        ArrayMesh? mesh = Engine_MdlMesh.TryCreateMesh(
            bytes, virtualPath, out string reason, out _);
        if (mesh is null)
        {
            LastError = reason.Length != 0 ? reason : "no reason given";
        }

        if (mesh is not null)
        {
            mesh.ResourceName = Engine_LevelProps.LeafOf(virtualPath);
            lock (Cache)
            {
                Cache[virtualPath] = mesh;
            }
        }

        return mesh;
    }
}
