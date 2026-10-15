using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's radiance-transfer ambient (AMB2): an instance whose mesh carries the
/// model's transfer and whose placement has a harmonic environment draws on the title's AMB2
/// pair with that environment folded into the mesh's clusters.
/// </summary>
/// <remarks>
/// THE TITLE'S RULE (`sub_82219978`, `sub_8225DC80`, `sub_8220BA88`): a drawable with no vertex
/// bake and no lightmap but a transfer stream takes mode 2 when probe data is loaded; its L1
/// environment is the placement's 48-byte .lmp record, else the probe grid at its position
/// (blended over 30 frames when it moves); g_PRTConstants (VS c28..39) are, per cluster, the
/// mean and the four basis rows dotted with that environment. The environment is what the
/// modern path already keeps on the node (<see cref="Refii.Lighting.Engine_LevelBakedLighting.StoreProbeSample"/>
/// and the object-ambient rung, as the probe metadata); the vertex bake and the lightmap keep
/// their precedence through the bake-mode metadata.
/// </remarks>
public static class Engine_ClassicTransfer
{
    private sealed class State
    {
        public Vector4 R, G, B;
        /// <summary>The mesh the overrides were built for: a swap (a body variant) re-indexes the surfaces.</summary>
        public ulong MeshId;
        public ShaderMaterial?[] Overrides = Array.Empty<ShaderMaterial?>();
        /// <summary>The surface material each override was built from: a new one (the hero's composites) rebuilds it.</summary>
        public ulong[] Sources = Array.Empty<ulong>();
        /// <summary>The xr_tree_ambient this node was given (trees only).</summary>
        public Vector4 Ambient;
        /// <summary>The placement's first row of the shared constants (Engine_ClassicPrtRows) and how many; 0 = none.</summary>
        public int PrtRow, PrtRows, PrtGeneration;
        public bool ReleaseHooked;
    }

    private static readonly ConditionalWeakTable<MeshInstance3D, State> States = new();
    private static int _applied;

    /// <summary>Instances re-keyed to the transfer pair so far, for the log.</summary>
    public static int Applied => _applied;

    /// <summary>
    /// Applies or refreshes the instance's transfer lighting from its probe metadata. Called
    /// from the per-instance lighting seam; cheap when nothing changed.
    /// </summary>
    public static void Apply(GeometryInstance3D geometry)
    {
        ApplyTreeAmbient(geometry);
        ApplyRepeatedConstants(geometry);
        if (!Engine_ClassicMaterial.Active || geometry is not MeshInstance3D node || node.Mesh is not { } mesh)
        {
            return;
        }
        State state = States.GetOrCreateValue(node);
        int surfaces = mesh.GetSurfaceCount();
        if (state.MeshId != mesh.GetInstanceId())
        {
            // A different mesh: this feed's overrides sit at the old surface indices. Take back
            // only our own (a collapsed surface keeps its collapse) and build afresh.
            for (int i = 0; i < state.Overrides.Length && i < surfaces; i++)
            {
                if (state.Overrides[i] is { } ours && ReferenceEquals(node.GetSurfaceOverrideMaterial(i), ours))
                {
                    node.SetSurfaceOverrideMaterial(i, null);
                }
            }
            state.Overrides = Array.Empty<ShaderMaterial?>();
            state.MeshId = mesh.GetInstanceId();
        }
        bool whole = mesh.HasMeta(Engine_MdlTransfer.DescriptorMeta);
        bool perSurface = !whole && mesh.HasMeta(Engine_MdlTransfer.SurfaceDescriptorsMeta);
        if (!whole && !perSurface)
        {
            return;
        }
        // A vertex bake (3) or a lightmap (4) outranks the transfer, as the packet ctor orders them.
        if (node.HasMeta(Refii.Lighting.Engine_LevelBakedLighting.BakeModeMeta) &&
            node.GetMeta(Refii.Lighting.Engine_LevelBakedLighting.BakeModeMeta) is { VariantType: Variant.Type.Int } mode &&
            mode.AsInt32() is 3 or 4)
        {
            return;
        }
        if (!ProbeOf(node, out Vector4 r, out Vector4 g, out Vector4 b))
        {
            return;
        }

        if (state.Overrides.Length == surfaces && state.R == r && state.G == g && state.B == b && SourcesHold(state, mesh))
        {
            return;
        }

        // Per surface: the whole model's header and table, or (a composite) each part's own.
        Decoded decoded = DecodedOf.GetValue(mesh, Decode);
        uint[][]? descriptors = decoded.Descriptors;
        float[][]? tables = decoded.Tables;
        uint[]? wholeDescriptor = decoded.WholeDescriptor;
        float[]? wholeTable = decoded.WholeTable;

        bool fresh = state.Overrides.Length != surfaces;
        if (fresh)
        {
            state.Overrides = new ShaderMaterial?[surfaces];
            state.Sources = new ulong[surfaces];
        }
        // The placement's rows: one for a whole model's constants, one per surface for a
        // composite's parts (each part's own table), read through the shared pairs.
        int rows = whole ? 1 : surfaces;
        if (state.PrtRow == 0 || state.PrtRows != rows)
        {
            if (state.PrtRow != 0)
            {
                Engine_ClassicPrtRows.Release(state.PrtRow, state.PrtRows, state.PrtGeneration);
            }
            state.PrtRow = Engine_ClassicPrtRows.Acquire(rows);
            state.PrtRows = rows;
            state.PrtGeneration = Engine_ClassicPrtRows.Generation;
            if (!state.ReleaseHooked)
            {
                state.ReleaseHooked = true;
                node.TreeExiting += () =>
                {
                    Engine_ClassicPrtRows.Release(state.PrtRow, state.PrtRows, state.PrtGeneration);
                    // Back in the tree it is applied afresh, with rows of its own again.
                    state.PrtRow = 0;
                    state.Overrides = Array.Empty<ShaderMaterial?>();
                };
            }
        }
        for (int i = 0; i < surfaces; i++)
        {
            uint[] descriptor;
            float[] table;
            if (whole)
            {
                descriptor = wholeDescriptor!;
                table = wholeTable!;
            }
            else
            {
                if (i >= descriptors!.Length || i >= tables!.Length)
                {
                    continue;
                }
                descriptor = descriptors[i];
                table = tables[i];
                if (descriptor.Length < 5 || table.Length == 0)
                {
                    continue;
                }
            }
            bool rebuilt = mesh.SurfaceGetMaterial(i) is ShaderMaterial current && current.GetInstanceId() != state.Sources[i];
            if ((fresh || rebuilt) && mesh.SurfaceGetMaterial(i) is ShaderMaterial shared &&
                shared.HasMeta(Engine_ClassicMaterial.ClassicMeta))
            {
                Engine_ClassicMaterial.AmbientPending = false;
                // The pair every placement of this part shares, reading the placement's row for
                // this surface (all surfaces of a whole model read its one row).
                ShaderMaterial? own2 = Engine_ClassicMaterial.WithPrtRows(shared, whole ? 0 : i);
                if (Engine_ClassicMaterial.AmbientPending)
                {
                    // Its shader is still compiling: this surface is asked again (PumpPending).
                    Engine_ClassicMaterial.AmbientPending = false;
                    Pending.Add(node);
                    continue;
                }
                state.Sources[i] = shared.GetInstanceId();
                state.Overrides[i] = own2;
                // A surface the hidden-cluster pass has collapsed stays collapsed; it takes this
                // material back when it is shown (Engine_HiddenClusters asks SurfaceOverride).
                if (state.Overrides[i] is not null &&
                    !Refii.Entities.Engine_HiddenClusters.IsCollapsed(node.GetSurfaceOverrideMaterial(i)))
                {
                    node.SetSurfaceOverrideMaterial(i, state.Overrides[i]);
                    ++_applied;
                }
            }
            if (!whole && state.Overrides[i] is not null)
            {
                Engine_ClassicPrtRows.Write(state.PrtRow + i, Engine_MdlTransfer.Constants(descriptor, table, r, g, b));
            }
        }
        if (whole)
        {
            Engine_ClassicPrtRows.Write(state.PrtRow, Engine_MdlTransfer.Constants(wholeDescriptor!, wholeTable!, r, g, b));
        }
        if (Array.Exists(state.Overrides, o => o is not null))
        {
            // After the overrides: an instance parameter needs a material that declares it.
            node.SetInstanceShaderParameter(Engine_ClassicPrtRows.RowUniform, state.PrtRow);
        }
        state.R = r;
        state.G = g;
        state.B = b;
    }

    /// <summary>Whether every overridden surface still has the material its override was built from.</summary>
    private static bool SourcesHold(State state, Mesh mesh)
    {
        for (int i = 0; i < state.Overrides.Length; i++)
        {
            if (state.Overrides[i] is not null && mesh.SurfaceGetMaterial(i) is ShaderMaterial current &&
                current.GetInstanceId() != state.Sources[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The transfer material this feed put on a surface, or null.</summary>
    public static Material? SurfaceOverride(MeshInstance3D node, int surface) =>
        States.TryGetValue(node, out State? state) && surface < state.Overrides.Length ? state.Overrides[surface] : null;

    /// <summary>Instances whose AMB2 pair waited for its shader's parse (Engine_ClassicShaders.Settled).</summary>
    private static readonly HashSet<MeshInstance3D> Pending = new();
    private static readonly List<MeshInstance3D> Retrying = new();

    /// <summary>Instances whose pair waits for its shader (the load hold waits for none).</summary>
    public static int PendingCount => Pending.Count;

    /// <summary>Applies again every instance whose pair waited. Main thread, once a frame.</summary>
    public static void PumpPending()
    {
        if (Pending.Count == 0)
        {
            return;
        }
        Retrying.Clear();
        Retrying.AddRange(Pending);
        Pending.Clear();
        foreach (MeshInstance3D node in Retrying)
        {
            if (GodotObject.IsInstanceValid(node) && node.IsInsideTree())
            {
                Apply(node);
            }
        }
    }

    private static readonly StringName TreeAmbientName = "xr_tree_ambient";
    private static readonly ConditionalWeakTable<GeometryInstance3D, State> TreeStates = new();
    private static int _treeAmbients;

    /// <summary>Tree instances given their flat ambient so far, for the log.</summary>
    public static int TreeAmbients => _treeAmbients;

    /// <summary>The xr_tree_ambient given to <paramref name="geometry"/>, if any; read without a render-thread round trip.</summary>
    public static bool TreeAmbientOf(GeometryInstance3D geometry, out Vector4 ambient)
    {
        if (TreeStates.TryGetValue(geometry, out State? state) && state.Overrides.Length != 0)
        {
            ambient = state.Ambient;
            return true;
        }
        ambient = Vector4.Zero;
        return false;
    }

    /// <summary>
    /// A tree's AMB2 cluster-mean row (VS c28, `sub_82238330` uploads `*(item+0x40)`), the same
    /// placement environment for its main and LOD models (`sub_822DAB20` builds both through
    /// `sub_8220B870` from obj+444). With the model's transfer it is `sub_8225DC80`'s cluster-0
    /// mean dotted with the environment. A model with no transfer (every shipped Brightwood
    /// tree) uploads nothing in the title and its c28 keeps the previous draw's value, which
    /// nothing can reproduce; it takes the environment's flat term instead, the unoccluded
    /// surface's mean: L0 x 0.2820948 per channel, the D3DX band-0 constant the title evaluates
    /// its probes with (fable_lighting.gdshaderinc). The sample is the node's probe (a batch's
    /// is taken at its bounds' centre).
    /// </summary>
    private static void ApplyTreeAmbient(GeometryInstance3D geometry)
    {
        if (!Engine_ClassicMaterial.Active)
        {
            return;
        }
        Mesh? mesh = geometry switch
        {
            MeshInstance3D single => single.Mesh,
            MultiMeshInstance3D batch => batch.Multimesh?.Mesh,
            _ => null,
        };
        if (mesh is null || !UsesTreeFamily(mesh))
        {
            return;
        }
        if (!geometry.HasMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMetaName) ||
            geometry.GetMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMetaName).AsGodotArray() is not { Count: >= 3 } sample)
        {
            // A batch whose copies carry their own placement's environment as instance colours
            // (Engine_LevelBakedLighting.ApplyMultiMeshProbes: L0 x 0.2820948 x the ambient scale)
            // reads it per copy.
            if (geometry is MultiMeshInstance3D { Multimesh.UseColors: true } &&
                TreeStates.GetOrCreateValue(geometry) is { Overrides.Length: 0 } fresh)
            {
                float unscale = 1f / Refii.Lighting.Engine_LevelBakedLighting.AmbientScale;
                fresh.Ambient = new Vector4(unscale, unscale, unscale, 2f);
                geometry.SetInstanceShaderParameter(TreeAmbientName, fresh.Ambient);
                fresh.Overrides = new ShaderMaterial?[1];
                ++_treeAmbients;
            }
            return;
        }
        var r = sample[0].AsVector4();
        var g = sample[1].AsVector4();
        var b = sample[2].AsVector4();
        State state = TreeStates.GetOrCreateValue(geometry);
        if (state.Overrides.Length != 0 && state.R == r && state.G == g && state.B == b)
        {
            return;
        }
        Vector4 row;
        if (mesh.HasMeta(Engine_MdlTransfer.DescriptorMeta))
        {
            uint[] descriptor = Array.ConvertAll(mesh.GetMeta(Engine_MdlTransfer.DescriptorMeta).AsInt32Array(), v => (uint)v);
            float[] table = mesh.GetMeta(Engine_MdlTransfer.TableMeta).AsFloat32Array();
            row = Engine_MdlTransfer.Constants(descriptor, table, r, g, b)[0];
        }
        else
        {
            row = new Vector4(r.X, g.X, b.X, 0f) * Refii.Lighting.Engine_LevelBakedLighting.ShBand0;
        }
        state.Ambient = new Vector4(row.X, row.Y, row.Z, 1f);
        geometry.SetInstanceShaderParameter(TreeAmbientName, state.Ambient);
        if (state.Overrides.Length == 0)
        {
            state.Overrides = new ShaderMaterial?[1];
            ++_treeAmbients;
        }
        state.R = r;
        state.G = g;
        state.B = b;
    }

    /// <summary>A model's transfer descriptors and tables, decoded from its meta once (a relight reads them on every move).</summary>
    private sealed class Decoded
    {
        public uint[]? WholeDescriptor;
        public float[]? WholeTable;
        public uint[][]? Descriptors;
        public float[][]? Tables;
    }

    private static readonly ConditionalWeakTable<Mesh, Decoded> DecodedOf = new();

    private static Decoded Decode(Mesh mesh)
    {
        var decoded = new Decoded();
        if (mesh.HasMeta(Engine_MdlTransfer.DescriptorMeta))
        {
            using Variant descriptor = mesh.GetMeta(Engine_MdlTransfer.DescriptorMeta);
            using Variant table = mesh.GetMeta(Engine_MdlTransfer.TableMeta);
            decoded.WholeDescriptor = Array.ConvertAll(descriptor.AsInt32Array(), v => (uint)v);
            decoded.WholeTable = table.AsFloat32Array();
            return decoded;
        }
        using Variant descriptorsMeta = mesh.GetMeta(Engine_MdlTransfer.SurfaceDescriptorsMeta);
        using Variant tablesMeta = mesh.GetMeta(Engine_MdlTransfer.SurfaceTablesMeta);
        using Godot.Collections.Array descriptors = descriptorsMeta.AsGodotArray();
        using Godot.Collections.Array tables = tablesMeta.AsGodotArray();
        decoded.Descriptors = new uint[descriptors.Count][];
        decoded.Tables = new float[tables.Count][];
        for (int i = 0; i < descriptors.Count; i++)
        {
            using Variant one = descriptors[i];
            decoded.Descriptors[i] = Array.ConvertAll(one.AsInt32Array(), v => (uint)v);
        }
        for (int i = 0; i < tables.Count; i++)
        {
            using Variant one = tables[i];
            decoded.Tables[i] = one.AsFloat32Array();
        }
        return decoded;
    }

    /// <summary>The placement's probe sample (Engine_LevelBakedLighting.ProbeMetaName), its wrappers disposed.</summary>
    private static bool ProbeOf(GodotObject node, out Vector4 r, out Vector4 g, out Vector4 b)
    {
        r = g = b = default;
        if (!node.HasMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMetaName))
        {
            return false;
        }
        using Variant meta = node.GetMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMetaName);
        if (meta.VariantType != Variant.Type.Array)
        {
            return false;
        }
        using Godot.Collections.Array sample = meta.AsGodotArray();
        if (sample.Count < 3)
        {
            return false;
        }
        r = sample[0].AsVector4();
        g = sample[1].AsVector4();
        b = sample[2].AsVector4();
        return true;
    }

    /// <summary>Whether any surface draws the tree family; cached per mesh (it is asked on every relight).</summary>
    private static readonly ConditionalWeakTable<Mesh, StrongBox<bool>> TreeFamily = new();

    private static bool UsesTreeFamily(Mesh mesh) => TreeFamily.GetValue(mesh, m => new StrongBox<bool>(ReadTreeFamily(m))).Value;

    private static bool ReadTreeFamily(Mesh mesh)
    {
        for (int i = 0; i < mesh.GetSurfaceCount(); i++)
        {
            if (mesh.SurfaceGetMaterial(i) is ShaderMaterial material &&
                material.HasMeta(Engine_ClassicMaterial.FamilyMetaName) &&
                material.GetMeta(Engine_ClassicMaterial.FamilyMetaName).AsString() == "TREEMATERIAL")
            {
                return true;
            }
        }
        return false;
    }

    private static readonly StringName RepeatedConstantsName = "vs_g_RepeatedMeshConstants";

    /// <summary>
    /// g_RepeatedMeshConstants (VS c13) for a scatter batch, as `sub_82224578` writes it per
    /// repeated object: (0.2, 15, (2 r + D)^2, 0), r the model's bounding radius and D the
    /// object's draw distance. The vertex stage drops an instance whose squared distance from
    /// the eye exceeds .z. D is the batch's stated range end; with none the default kills nothing.
    /// </summary>
    private static void ApplyRepeatedConstants(GeometryInstance3D geometry)
    {
        if (!Engine_ClassicMaterial.Active || geometry is not MultiMeshInstance3D batch ||
            batch.Multimesh?.Mesh is not { } mesh || !UsesFamily(mesh, "REPEATEDMATERIAL") ||
            !batch.HasMeta(Refii.Level.Engine_PropDrawRange.Meta) ||
            batch.GetMeta(Refii.Level.Engine_PropDrawRange.Meta).AsFloat32Array() is not { Length: >= 3 } stated ||
            !(stated[2] > 0f) || !float.IsFinite(stated[2]))
        {
            return;
        }
        // The stated range is (.., D - m, m, ..) with m = f D / 2 (Engine_PropDrawRange): the
        // guest's own D is obj+0x124 and its fade begins at D (1 - f) (`sub_82AA2B30`).
        float draw = stated[2] + (stated.Length >= 4 ? stated[3] : 0f);
        float fade = stated.Length >= 4 && draw > 0f ? 2f * stated[3] / draw : 0f;
        float radius = mesh.GetAabb().Size.Length() * 0.5f;
        float reach = 2f * radius + draw;
        batch.SetInstanceShaderParameter(RepeatedConstantsName, new Vector4(0.2f, 15f, reach * reach, 0f));
        // PS c15 (`sub_821FCCB0`): (D(1-f), 1/(fD), D(1-f), 0) — the stipple fade over the last
        // f of the range. No guard on f = 0 in the title either: it never fades, by infinity.
        float begin = draw * (1f - fade);
        batch.SetInstanceShaderParameter(FadeName, new Vector4(begin, 1f / (fade * draw), begin, 0f));
        // The stipple IS the fade; the batch itself is cut at D as the CPU cull cuts it, not
        // faded a second time by Godot.
        batch.VisibilityRangeEnd = draw;
        batch.VisibilityRangeEndMargin = 0f;
        batch.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
    }

    private static readonly StringName FadeName = "ps_g_FadeBeginDistanceAndRange";

    private static bool UsesFamily(Mesh mesh, string family)
    {
        for (int i = 0; i < mesh.GetSurfaceCount(); i++)
        {
            if (mesh.SurfaceGetMaterial(i) is ShaderMaterial material &&
                material.HasMeta(Engine_ClassicMaterial.FamilyMetaName) &&
                material.GetMeta(Engine_ClassicMaterial.FamilyMetaName).AsString() == family)
            {
                return true;
            }
        }
        return false;
    }
}
