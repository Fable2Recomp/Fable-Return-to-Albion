using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// `android_pipeline_warmup`: the Mobile renderer's light-count pipelines compiled under the
/// level's loading screen, so walking through a town compiles none.
/// </summary>
/// <remarks>
/// <para>WHY THE LOAD DOES NOT ALREADY COVER THEM. Godot Mobile specialises every colour-pass
/// pipeline on the instance's own light counts — `omni_lights` and `spot_lights`, each
/// quantised by `shader_count_for` to none / one / several (render_forward_mobile.cpp,
/// `_render_list_template`) — and where the driver cannot take ubershaders (the Adreno 640)
/// each new combination compiles synchronously at its first draw. The load draws every surface
/// once, under whatever lights reach it from the spawn; a prop that later walks into a second
/// lamp's reach is a new pipeline and a hitch of 0.4-2.9 s (31-39 of them across Bowerstone
/// Market in 40 s).</para>
/// <para>WHY NOT <see cref="Engine_ShaderWarmup"/>. That one draws each .gdshader on a
/// full-attribute triangle in its own 128 px SubViewport and world (and the Android host seeds
/// it off). Almost nothing of its pipeline keys matches a real draw: the framebuffer format (its
/// own target, no scaling pass, none of the world environment's post effects deciding the
/// subpass), the base specialisation (no environment: fog off, no ambient or reflection cube;
/// its own sun), the vertex format (the .mdl meshes' attribute set and compression flags), and
/// it has no placed lights, no multimeshes and no skins. It compiles the shaders, not the
/// pipelines.</para>
/// <para>SO THIS DRAWS IN THE REAL VIEWPORT, beside the real camera, in the real scenario: the
/// framebuffer format, render pass and subpass, environment, sun and luminance multiplier are
/// the ones play draws with because they are the same frame. What it adds is copies — a
/// RenderingServer instance on each source's own mesh or multimesh resource (so the vertex
/// format and the multimesh flags are the real ones), its own active materials, its skeleton
/// attached (skinned format), its sign of determinant (cull variant) and its transparency (the
/// list it sorts into) — shrunk to a few centimetres and given 0/1/2 test omni and spot lights
/// of their own, each copy in a cell the next cell's lights cannot reach. Light shadows, sizes and
/// projectors stay off: the placed fixtures have size 0 and no projector, and a shadow map is
/// not part of the receiver's key.</para>
/// <para>NOT COVERED: decals and reflection probes (their bits in the key are left as play finds
/// them), effect layers (RenderingServer instances of their own), and the shadow and depth
/// passes, whose keys carry no light counts and which the load's real draws already compile.</para>
/// <para>BOUNDED: one copy per distinct (shader chain, surface format, primitive, skin, mirror,
/// transparency, multimesh format) the level holds, times the light combinations it can meet
/// (no spot combinations in a level without spot lights), capped at <see cref="MaxDraws"/> with
/// the commoner combinations first; <see cref="CellsPerFrame"/> copies a frame (at most 64 alive
/// with 4 test lights each, well under the 512 lights a frame the renderer clusters), each freed
/// <see cref="KeepFrames"/> frames later. A specialisation pipeline is one driver binary for one
/// shader, far smaller than the ubershader set that ran the S10e out of memory, and a cached one
/// costs nothing to ask for again.</para>
/// <para>UNDER `stable_light_counts` ONE COMBINATION IS ALL OF THEM: the fillers put every instance
/// in the scenario, these copies too, at two or more paired omni and spot lights, and
/// `shader_count_for` folds any count from two up to "several". So a test light cannot change a
/// copy's pipeline key, and each copy is drawn once, with none. The other eight draws found
/// their pipeline cached (4-8 compiled of about 600 draws on both phones) and cost 16 frames of
/// the hold, at 64-78 ms each.</para>
/// </remarks>
public static class Engine_PipelineWarmup
{
    public const string ConfigKey = "android_pipeline_warmup";

    /// <summary>Copies drawn per region, at most: one for each source and light combination.</summary>
    public const int MaxDraws = 1024;

    /// <summary>
    /// Copies placed a frame. Each new combination compiles at its first draw; most draws find
    /// theirs cached (28 compiles in 1024 draws on the S10e), so a frame's cost is the held
    /// frame itself and the fewer frames the warm takes the sooner the guest is released.
    /// </summary>
    public const int CellsPerFrame = Columns * Rows * Slabs;

    /// <summary>A batch's block of cells: columns across, rows up, slabs away from the camera.</summary>
    private const int Columns = 4;
    private const int Rows = 2;
    private const int Slabs = 4;

    /// <summary>Frames a copy stays in the scenario, so the frame that draws it has run.</summary>
    public const int KeepFrames = 2;

    /// <summary>
    /// Metres between cells: more than a test light's reach plus half a copy (0.15), so no cell's
    /// lights touch a neighbour's copy, along any axis.
    /// </summary>
    private const float CellSpacing = 0.2f;

    /// <summary>A copy's largest extent, in metres.</summary>
    private const float CopySize = 0.06f;

    /// <summary>A test light's reach: its own cell's copy and nothing else.</summary>
    private const float LightRange = 0.12f;

    /// <summary>
    /// Metres in front of the camera the grid may stand at, nearest first: the first that no
    /// visible real light reaches, since a lamp over the grid would add itself to every copy's
    /// count. All past every near plane the game uses.
    /// </summary>
    private static readonly float[] Distances = { 2f, 3f, 5f, 8f, 13f, 21f, 34f, 55f };

    /// <summary>
    /// The grid's radius around its centre, cells and lights included: both live batches' blocks
    /// (one behind the other, <see cref="Slabs"/> deep each), their corner cell, its light's reach.
    /// </summary>
    private static readonly float GridRadius =
        new Vector3((Columns - 1) * 0.5f, (Rows - 1) * 0.5f, (2 * Slabs - 1) * 0.5f).Length() * CellSpacing +
        LightRange + CopySize * 0.5f;

    private static readonly (int Omni, int Spot)[] Combos =
    {
        (0, 0), (1, 0), (2, 0), (0, 1), (1, 1), (2, 1), (0, 2), (1, 2), (2, 2),
    };

    private sealed class Source
    {
        public Resource Base = null!;
        public Material?[] Surfaces = Array.Empty<Material?>();
        public Material? Override;
        public Material? Overlay;
        public SkinReference? Skin;
        public Aabb Bounds;
        public bool Mirror;
        public float Transparency;
        public uint Layers;
    }

    private static bool _holding;
    private static bool _enabled;
    private static bool _planned;
    private static bool _done;
    private static bool _pinned;
    private static readonly List<(Source Source, int Omni, int Spot)> _draws = new();
    private static readonly Queue<(int Frame, List<Rid> Rids)> _live = new();
    private static readonly List<Light3D> _lights = new();
    private static int _litFrames;
    private static int _next;
    private static int _frame;
    private static int _kinds;
    private static Rid _omni;
    private static Rid _spot;
    private static long _startTicks;
    private static double _specialisationsAtStart;
    private static double _surfacesAtStart;

    /// <summary>A region's hold begins: reads the key and forgets the last region.</summary>
    public static void Begin()
    {
        End();
        _holding = true;
        _enabled = RenderingServer.GetCurrentRenderingMethod() == "mobile" &&
            Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), ConfigKey, false);
    }

    /// <summary>The hold ended or the region went: every copy and test light is freed.</summary>
    public static void End()
    {
        while (_live.Count != 0)
        {
            Free(_live.Dequeue().Rids);
        }

        if (_omni.IsValid)
        {
            RenderingServer.FreeRid(_omni);
        }

        if (_spot.IsValid)
        {
            RenderingServer.FreeRid(_spot);
        }

        _omni = default;
        _spot = default;
        ReleaseSkins();
        _draws.Clear();
        _lights.Clear();
        _litFrames = 0;
        _next = 0;
        _frame = 0;
        _kinds = 0;
        _planned = false;
        _done = false;
        _pinned = false;
        _holding = false;
    }

    /// <summary>
    /// The light combinations a level's copies are drawn under, commonest first: those its lights
    /// can make, or the one combination there is when <paramref name="pinned"/> (fillers stand).
    /// </summary>
    internal static IEnumerable<(int Omni, int Spot)> CombosFor(bool omni, bool spot, bool pinned)
    {
        if (pinned)
        {
            yield return (0, 0);
            yield break;
        }

        foreach ((int o, int s) in Combos)
        {
            if ((o == 0 || omni) && (s == 0 || spot))
            {
                yield return (o, s);
            }
        }
    }

    /// <summary>
    /// One held frame: what the warm still waits for, or empty when done or off.
    /// <paramref name="world"/>: the node whose subtree is the level (props, terrain, trees,
    /// entities); <paramref name="worldSettled"/>: everything it will hold is in.
    /// </summary>
    public static string Pump(Node3D world, bool worldSettled)
    {
        if (!_holding || !_enabled || _done)
        {
            return string.Empty;
        }

        if (!worldSettled)
        {
            return "the world";
        }

        Camera3D? camera = world.GetViewport()?.GetCamera3D();
        World3D? world3D = world.GetWorld3D();
        if (camera is null || world3D is null)
        {
            GD.Print("[pipeline-warmup] no camera or world to draw beside; skipped");
            _done = true;
            return string.Empty;
        }

        ++_frame;
        if (!_planned)
        {
            _planned = true;
            _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _specialisationsAtStart = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization);
            _surfacesAtStart = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface);
            _pinned = Refii.Lighting.Engine_StableLightCounts.StandsIn(world3D.Scenario);
            Plan(world);
        }

        while (_live.Count != 0 && _frame - _live.Peek().Frame >= KeepFrames)
        {
            Free(_live.Dequeue().Rids);
        }

        if (_next < _draws.Count)
        {
            Place(camera.GlobalTransform, Clear(camera), world3D.Scenario);
            return $"{_draws.Count - _next} pipeline warm draw(s)";
        }

        if (_live.Count != 0)
        {
            return "the last pipeline warm draws";
        }

        if (Refii.AndroidRuntime.AndroidTreeCards.FirstCaptureWaiting)
        {
            // The tree pictures' first capture compiles the tree materials for its own target.
            return "the far tree pictures";
        }

        double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - _startTicks) /
            (double)System.Diagnostics.Stopwatch.Frequency;
        double specialisations = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization) - _specialisationsAtStart;
        double surfaces = Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface) - _surfacesAtStart;
        GD.Print($"[pipeline-warmup] {_draws.Count} draw(s) for {_kinds} surface kind(s) compiled " +
            $"{specialisations:0} specialisation and {surfaces:0} surface pipeline(s) in {seconds:0.00} s " +
            $"over {_frame} frame(s)" +
            (_litFrames != 0 ? $"; {_litFrames} frame(s) had a real light over the grid, their counts shifted" : ""));
        ReleaseSkins();
        _done = true;
        return string.Empty;
    }

    /// <summary>
    /// The kept sources' skin references, released on the main thread once their copies are gone;
    /// left to the finalizer, releasing one edits a live Skeleton3D from the finalizer thread.
    /// </summary>
    private static void ReleaseSkins()
    {
        foreach ((Source source, _, _) in _draws)
        {
            source.Skin?.Dispose();
            source.Skin = null;
        }
    }

    /// <summary>
    /// One copy per source that brings a surface kind no earlier source did, then every light
    /// combination the level's lights can make, combination-major so the cap drops the rarest.
    /// </summary>
    private static void Plan(Node3D world)
    {
        var kinds = new HashSet<int>();
        var sources = new List<Source>();
        bool omni = false;
        bool spot = false;
        var stack = new Stack<Node>();
        stack.Push(world);
        while (stack.Count != 0)
        {
            Node node = stack.Pop();
            for (int i = node.GetChildCount() - 1; i >= 0; --i)
            {
                stack.Push(node.GetChild(i));
            }

            switch (node)
            {
                case SpotLight3D spotLight:
                    spot = true;
                    _lights.Add(spotLight);
                    break;
                case OmniLight3D omniLight:
                    omni = true;
                    _lights.Add(omniLight);
                    break;
                case MeshInstance3D { Mesh: { } mesh } instance:
                    Consider(instance, mesh, null, kinds, sources, null);
                    ConsiderFar(instance, mesh, null, kinds, sources);
                    break;
                case MultiMeshInstance3D { Multimesh: { Mesh: { } mmMesh } multimesh } multi:
                    Consider(multi, mmMesh, multimesh, kinds, sources, null);
                    ConsiderFar(multi, mmMesh, multimesh, kinds, sources);
                    break;
            }
        }

        // The far tree cards (tree_billboards) stand outside the level's subtree.
        if (Refii.AndroidRuntime.AndroidTreeCards.WarmCards is { Multimesh: { Mesh: { } cardMesh } cardMulti } cards)
        {
            Consider(cards, cardMesh, cardMulti, kinds, sources, null);
        }

        _kinds = kinds.Count;
        foreach ((int o, int s) in CombosFor(omni, spot, _pinned))
        {
            foreach (Source source in sources)
            {
                if (_draws.Count < MaxDraws)
                {
                    _draws.Add((source, o, s));
                }
            }
        }

        if (omni && !_pinned)
        {
            _omni = RenderingServer.OmniLightCreate();
            Configure(_omni);
        }

        if (spot && !_pinned)
        {
            _spot = RenderingServer.SpotLightCreate();
            Configure(_spot);
            RenderingServer.LightSetParam(_spot, RenderingServer.LightParam.SpotAngle, 45.0f);
        }

        GD.Print($"[pipeline-warmup] {_kinds} surface kind(s) on {sources.Count} source(s), " +
            $"{(omni ? "omni" : "no omni")} and {(spot ? "spot" : "no spot")} lights" +
            $"{(_pinned ? " (counts pinned, one combination)" : "")}: " +
            $"{_draws.Count} draw(s){(_draws.Count == MaxDraws ? $" (capped at {MaxDraws})" : "")}" +
            (Refii.Level.Engine_PropFarShading.Active ? "; " + Refii.Level.Engine_PropFarShading.Census() : ""));
    }

    private static void Configure(Rid light)
    {
        RenderingServer.LightSetParam(light, RenderingServer.LightParam.Range, LightRange);
        RenderingServer.LightSetParam(light, RenderingServer.LightParam.Energy, 0.01f);
        RenderingServer.LightSetParam(light, RenderingServer.LightParam.Size, 0.0f);
        RenderingServer.LightSetShadow(light, false);
    }

    /// <summary>
    /// A prop that `prop_far_shading_metres` switches (Refii.Level.Engine_PropFarShading): drawn
    /// once more under each state it is not in now, its near materials and their far copies.
    /// </summary>
    private static void ConsiderFar(
        GeometryInstance3D instance, Mesh mesh, MultiMesh? multimesh, HashSet<int> kinds, List<Source> sources)
    {
        if (!Refii.Level.Engine_PropFarShading.Active || !Refii.Level.Engine_PropFarShading.IsRegistered(instance))
        {
            return;
        }

        Consider(instance, mesh, multimesh, kinds, sources, m => Refii.Level.Engine_PropFarShading.WarmMaterials(m).Near);
        Consider(instance, mesh, multimesh, kinds, sources,
            m => Refii.Level.Engine_PropFarShading.WarmMaterials(m) is var pair ? pair.Far ?? pair.Near : m);
    }

    private static void Consider(
        GeometryInstance3D instance, Mesh mesh, MultiMesh? multimesh, HashSet<int> kinds, List<Source> sources,
        Func<Material?, Material?>? map)
    {
        int count = mesh.GetSurfaceCount();
        // Godot skips an instance with nothing to draw, and so would its copy.
        if (count == 0 || multimesh is { InstanceCount: 0 } or { VisibleInstanceCount: 0 })
        {
            return;
        }

        bool mirror = instance.GlobalBasis.Determinant() < 0f;
        bool transparent = instance.Transparency > 0f;
        SkinReference? skin = (instance as MeshInstance3D)?.GetSkinReference();
        int multimeshKey = multimesh is null
            ? 0
            : 1 + (int)multimesh.TransformFormat * 2 + (multimesh.UseColors ? 4 : 0) + (multimesh.UseCustomData ? 8 : 0);
        int overlay = ChainKey(instance.MaterialOverlay);
        var materials = new Material?[count];
        bool adds = false;
        for (int i = 0; i < count; ++i)
        {
            Material? material = instance is MeshInstance3D meshInstance
                ? meshInstance.GetActiveMaterial(i)
                : instance.MaterialOverride ?? mesh.SurfaceGetMaterial(i);
            if (map is not null)
            {
                material = map(material);
            }

            materials[i] = material;
            long format = mesh is ArrayMesh arrays ? (long)arrays.SurfaceGetFormat(i) : -1L;
            int primitive = mesh is ArrayMesh arrays2 ? (int)arrays2.SurfaceGetPrimitiveType(i) : -1;
            int kind = HashCode.Combine(ChainKey(material), format, primitive, skin is not null, mirror,
                transparent, multimeshKey, overlay);
            adds |= kinds.Add(kind);
        }

        if (!adds)
        {
            // Released here, on the main thread: a SkinReference left to the finalizer thread
            // edits the live Skeleton3D's bindings from there (`~SkinReference`). Unless a kept
            // source holds it: meshes sharing a skin get the same C# wrapper back.
            if (skin is not null && !sources.Exists(kept => ReferenceEquals(kept.Skin, skin)))
            {
                skin.Dispose();
            }

            return;
        }

        sources.Add(new Source
        {
            Base = multimesh is null ? mesh : multimesh,
            Surfaces = multimesh is null ? materials : Array.Empty<Material?>(),
            Override = multimesh is null ? null : map is not null && count == 1 ? materials[0] : instance.MaterialOverride,
            Overlay = instance.MaterialOverlay,
            Skin = skin,
            Bounds = multimesh is null ? mesh.GetAabb() : multimesh.GetAabb(),
            Mirror = mirror,
            Transparency = instance.Transparency,
            Layers = instance.Layers,
        });
    }

    /// <summary>A material and its next passes, by the shader each draws with.</summary>
    private static int ChainKey(Material? material)
    {
        int key = 17;
        for (int depth = 0; material is not null && depth < 4; ++depth, material = material.NextPass)
        {
            // Materials of one Shader share its pipelines; anything else keys on itself.
            ulong id = material is ShaderMaterial { Shader: { } shader } ? shader.GetRid().Id : material.GetRid().Id;
            key = HashCode.Combine(key, id);
        }

        return key;
    }

    /// <summary>The nearest distance ahead no visible real light reaches; the nearest if none.</summary>
    private static float Clear(Camera3D camera)
    {
        if (_pinned)
        {
            // A lamp over the grid cannot change a pinned count.
            return Distances[0];
        }

        Transform3D eye = camera.GlobalTransform;
        foreach (float distance in Distances)
        {
            if (distance + GridRadius >= camera.Far)
            {
                break;
            }

            Vector3 centre = eye * new Vector3(0f, 0f, -distance);
            bool lit = false;
            foreach (Light3D light in _lights)
            {
                if (!GodotObject.IsInstanceValid(light) || !light.IsVisibleInTree())
                {
                    continue;
                }

                float range = light is OmniLight3D o ? o.OmniRange : ((SpotLight3D)light).SpotRange;
                if (light.GlobalPosition.DistanceTo(centre) < range + GridRadius)
                {
                    lit = true;
                    break;
                }
            }

            if (!lit)
            {
                return distance;
            }
        }

        ++_litFrames;
        return Distances[0];
    }

    private static void Place(Transform3D camera, float distance, Rid scenario)
    {
        var rids = new List<Rid>();
        Basis facing = camera.Basis.Orthonormalized();
        // Two batches are alive at once; each takes its own slabs of the grid, one behind the
        // other. A copy behind another is still drawn (no occlusion culling): its pipeline is
        // asked for at the draw, whatever the depth test then keeps of it.
        int slabBase = (_frame & 1) * Slabs;
        for (int cell = 0; cell < CellsPerFrame && _next < _draws.Count; ++cell, ++_next)
        {
            (Source source, int omni, int spot) = _draws[_next];
            int slab = slabBase + cell / (Columns * Rows);
            int face = cell % (Columns * Rows);
            var local = new Vector3(
                (face % Columns - (Columns - 1) * 0.5f) * CellSpacing,
                (face / Columns - (Rows - 1) * 0.5f) * CellSpacing,
                -distance + (slab - (2 * Slabs - 1) * 0.5f) * CellSpacing);
            Vector3 centre = camera * local;

            float extent = Mathf.Max(source.Bounds.Size[(int)source.Bounds.Size.MaxAxisIndex()], 1e-4f);
            float scale = CopySize / extent;
            Basis basis = facing * Basis.FromScale(new Vector3(source.Mirror ? -scale : scale, scale, scale));
            Rid copy = RenderingServer.InstanceCreate2(source.Base.GetRid(), scenario);
            for (int i = 0; i < source.Surfaces.Length; ++i)
            {
                if (source.Surfaces[i] is { } material)
                {
                    RenderingServer.InstanceSetSurfaceOverrideMaterial(copy, i, material.GetRid());
                }
            }

            if (source.Override is { } materialOverride)
            {
                RenderingServer.InstanceGeometrySetMaterialOverride(copy, materialOverride.GetRid());
            }

            if (source.Overlay is { } overlay)
            {
                RenderingServer.InstanceGeometrySetMaterialOverlay(copy, overlay.GetRid());
            }

            if (source.Skin is { } skin && GodotObject.IsInstanceValid(skin))
            {
                RenderingServer.InstanceAttachSkeleton(copy, skin.GetSkeleton());
            }

            RenderingServer.InstanceSetCustomAabb(copy, source.Bounds);
            RenderingServer.InstanceGeometrySetCastShadowsSetting(copy, RenderingServer.ShadowCastingSetting.Off);
            RenderingServer.InstanceGeometrySetTransparency(copy, source.Transparency);
            RenderingServer.InstanceSetLayerMask(copy, source.Layers);
            RenderingServer.InstanceSetTransform(copy, new Transform3D(basis, centre - basis * source.Bounds.GetCenter()));
            rids.Add(copy);

            for (int i = 0; i < omni; ++i)
            {
                rids.Add(Light(_omni, new Transform3D(facing, centre), scenario));
            }

            for (int i = 0; i < spot; ++i)
            {
                // Behind the copy and facing away from the camera, so the cone holds it.
                rids.Add(Light(_spot, new Transform3D(facing, centre + facing.Z * (CopySize * 0.5f)), scenario));
            }
        }

        _live.Enqueue((_frame, rids));
    }

    private static Rid Light(Rid light, Transform3D at, Rid scenario)
    {
        Rid instance = RenderingServer.InstanceCreate2(light, scenario);
        RenderingServer.InstanceSetTransform(instance, at);
        return instance;
    }

    private static void Free(List<Rid> rids)
    {
        foreach (Rid rid in rids)
        {
            RenderingServer.FreeRid(rid);
        }
    }
}
