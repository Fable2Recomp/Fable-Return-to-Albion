using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace Refii.Models;

public static class Engine_MdlMesh
{
    /// <summary>
    /// Metadata key: for each vertex of the mesh, surface by surface in order, which vertex of
    /// the MODEL's own buffers it came from. -1 where it is not known.
    /// </summary>
    public const string SourceVertexMapMeta = "mdl_source_vertices";

    /// <summary>
    /// Metadata key: how many vertices the model's buffers hold in total, which is the count
    /// anything indexed against the file — a per-vertex bake — will have been built for.
    /// </summary>
    public const string SourceVertexTotalMeta = "mdl_source_vertex_total";

    /// <summary>
    /// One region (body cluster) name per surface, in surface order; empty where the model
    /// states none. What a dressed character's garments hide is matched against these.
    /// </summary>
    public const string SurfaceRegionsMeta = "mdl_surface_regions";

    /// <summary>
    /// The header's bounding sphere {x, y, z, radius} in the file's own (Z-up) axes: the model's
    /// bound the title's tree billboards normalise by (Engine_ClassicBillboards).
    /// </summary>
    public const string BoundSphereMeta = "mdl_bound_sphere";

    /// <summary>
    /// The model's fur, one Dictionary per furred primitive (<see cref="MdlFurBlock"/>):
    /// "base" (the primitive's first vertex in the model's numbering, <see cref="SourceVertexMapMeta"/>'s),
    /// "count", "fur" (the fur material's 17 file values), "colour" (Color per vertex, the record's
    /// D3DCOLOR) and "lanes" (float4 per vertex, the record's four floats).
    /// </summary>
    public const string FurMeta = "mdl_fur";

    /// <summary>
    /// Cached per mesh: the names are fixed when the mesh is built, and the cluster pass asks
    /// per entity on every reconcile. Callers only read the result.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, string[]> RegionCache = new();

    /// <summary>The region names a mesh carries per surface, or an empty array.</summary>
    public static string[] SurfaceRegions(Mesh mesh)
    {
        if (RegionCache.TryGetValue(mesh, out string[]? cached))
        {
            return cached;
        }

        string[] regions = mesh.HasMeta(SurfaceRegionsMeta)
            ? mesh.GetMeta(SurfaceRegionsMeta).AsStringArray()
            : System.Array.Empty<string>();
        RegionCache.AddOrUpdate(mesh, regions);
        return regions;
    }

    /// <summary>
    /// Metadata key: per surface in order, the `lods` dictionary its surface was added with —
    /// key = mean triangle edge length in model units, value = that level's index array over
    /// the surface's own vertices. Empty dictionary where the surface has no levels; absent
    /// when no surface has any. Anything that re-adds a surface from
    /// <c>SurfaceGetArrays</c> passes <see cref="SurfaceLods"/> through, or the levels are lost.
    /// </summary>
    public const string SurfaceLodsMeta = "mdl_surface_lods";

    /// <summary>The levels one surface was added with, or null when it has none.</summary>
    public static Godot.Collections.Dictionary? SurfaceLods(Mesh mesh, int surface)
    {
        if (!mesh.HasMeta(SurfaceLodsMeta))
        {
            return null;
        }

        Godot.Collections.Array all = mesh.GetMeta(SurfaceLodsMeta).AsGodotArray();
        if (surface < 0 || surface >= all.Count ||
            all[surface].VariantType != Variant.Type.Dictionary)
        {
            return null;
        }

        Godot.Collections.Dictionary lods = all[surface].AsGodotDictionary();
        return lods.Count == 0 ? null : lods;
    }

    /// <summary>Surfaces added with at least one coarser level. Run total.</summary>
public static int SurfacesWithLods;

    /// <summary>Surfaces built with the model's expanded radiance transfer in CUSTOM0..2.</summary>
    public static int SurfacesWithTransfer;

    /// <summary>
    /// Surfaces of a model that ships coarser LOD payloads and got no level — every level
    /// unmatched, refused for layout, or not coarser than the one before. Run total.
    /// </summary>
    public static int SurfacesWithoutLods;

    /// <summary>Levels handed to Godot, over all surfaces. Run total.</summary>
    public static int LodLevelsAttached;

    /// <summary>Levels a model shipped that were dropped for a surface. Run total.</summary>
    public static int LodLevelsDropped;

    private static bool _lodsReported;

    public static ArrayMesh? TryCreateMesh(byte[] data, out string error) =>
        TryCreateMesh(data, string.Empty, out error);

    /// <summary>
    /// The furthest a vertex may sit from a model's own origin before its depth can overflow.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY LOOSE, and matched to <c>Engine_EhfPatches.IsSane</c> and
    /// <c>MaxInstanceReach</c> rather than to <c>ModelReach</c> (64). This exists ONLY to stop
    /// a value that would overflow to +inf when squared for a distance and become NaN a
    /// subtraction later; it is not a judgement about whether the geometry looks right.
    ///
    /// A tight bound would be the wrong tool. A merely wrong-but-finite vertex draws as visibly
    /// broken geometry — which is diagnosable, and which the editor viewers exist to show. Only
    /// NaN and Inf hang the process, so only they are refused here. Rejecting more would hide
    /// models from the viewers and the game alike, and a missing model teaches nothing.
    /// </remarks>
    private const float MaxVertexReach = 1_000_000f;

    private static readonly HashSet<string> _fallbacksReported = new();

    /// <summary>
    /// A surface drawn untextured: its record named no texture that loaded. Once per model and
    /// surface, with what the record states, so the missing texture is named rather than seen.
    /// </summary>
    private static void ReportFallback(string model, string surface, MdlMaterial? record)
    {
        lock (_fallbacksReported)
        {
            if (_fallbacksReported.Count >= 256 || !_fallbacksReported.Add($"{model}|{surface}"))
            {
                return;
            }
        }
        // A RECORD THAT NAMES NO TEXTURE IS THE DATA'S STATEMENT, NOT A READER'S MISS. Fable 3's
        // texture set reader (sub_82CBA610) binds an empty path to a procedural default: the
        // diffuse and specular to the 16x16 white (sub_82182B70, 0x82CBA6D0 / 0x82CBA8F8), the
        // normal to the flat (255,127,127) (sub_82C7D430, 0x82C7D4C4), the environment map and
        // slot 4 to null (0x82CBAB78 / 0x82CBAC64); the mesh still draws, DiffuseColour (default
        // (1,1,1,1), sub_82CEE200 0x82CEE298) over white. 223 of the 11,276 shipped v47 materials
        // are like this, 221 of them with no DiffuseColour stated. Said once as the stated
        // contract, so the next reader does not go looking for a sampler the file never named.
        bool namesNothing = record is not null &&
            record.Diffuse.Length == 0 && record.Normal.Length == 0 && record.Specular.Length == 0 &&
            record.SelfIllumination.Length == 0 && record.EnvironmentMap.Length == 0;
        GD.Print(record is null
            ? $"[material] UNTEXTURED {model} '{surface}': no material record"
            : namesNothing
                ? $"[material] untextured by the data {model} '{surface}': the texture set names no " +
                  "texture; the guest binds its white and flat-normal defaults and draws DiffuseColour"
                : $"[material] UNTEXTURED {model} '{surface}': diffuse '{record.Diffuse}' normal '{record.Normal}' " +
                  $"specular '{record.Specular}' self-illum '{record.SelfIllumination}' envmap '{record.EnvironmentMap}'");
    }

    /// <summary>Surfaces refused for a non-finite or absurd vertex. Run total.</summary>
    public static int NonFiniteSurfaces;

    public static ArrayMesh? TryCreateMesh(byte[] data, string virtualPath, out string error) =>
        TryCreateMesh(data, virtualPath, out error, out _);

    public static ArrayMesh? TryCreateMesh(
        byte[] data, string virtualPath, out string error, out bool isStub)
    {
        isStub = false;

        if (!Engine_MdlData.TryParseInfo(data, virtualPath, out MdlInfo info, out error))
        {
            return null;
        }

        // WHICH kind of "nothing", quoting the header's own numbers.
        //
        // A stub is not a parse failure: the header is a complete record that states how many
        // bytes of LOD payload follow it, and when the banks carry no body entry for the name
        // there is nothing for any reader to decode. Print the shortfall so the next reader can
        // tell a hole in the shipped data from a decoder that gave up.
        //
        // THE GUEST DRAWS NOTHING FOR THEM EITHER. Fable 3 joins header and body in its VFS: a
        // Composite bank of the header bank and globals_models.bnk, both Required, whose table
        // keeps a name only when every required part has it (sub_82D90320; the emit condition
        // `v24 && v25 >= a1[10]`, v24 cleared at 0x82D90628 for a part with index 0). The open
        // then finds no row (sub_8312EF08 returns 0 at 0x8312EF94, sub_82C9E428 at 0x82C9E518),
        // the mesh keeps LOD count 0 and each renderer null-checks it (sub_821FDA80 0x821FDBF8).
        // Fable 2's header-only names are the same kind of hole.
        if (info.IsStub)
        {
            isStub = true;
            error = info.BodyBytesStated > 0
                ? $"no body entry in any bank: the {16 + info.HeaderSize}-byte header " +
                  $"states {info.BodyBytesStated:N0} byte(s) of geometry across " +
                  $"{info.LodCount} LOD(s), which the shipped banks never cooked in; the guest's " +
                  "composite bank drops the name and draws nothing"
                : "header only, no geometry";
            return null;
        }

        if (!Engine_MdlGeometry.TryBuild(data, info, out List<MdlGeometry> geometry))
        {
            // A model that declares no meshes was read correctly and holds nothing. Three
            // shipped models are like this — an all-but-empty 133-byte LOD 0 — and counting
            // them as undecodable pointed at the reader instead of at the data.
            isStub = info.MeshCount == 0;
            error = info.MeshCount == 0
                ? "the model declares no meshes"
                : "no decodable geometry";
            return null;
        }

        var mesh = new ArrayMesh();
        int added = 0;

        // Fable 3's header byte 88 is the mesh's GlobalLightingMode (sub_82D70950 0x82D709B8 ->
        // mesh+132; the twin's sub_82C88C88 reads it: 1 probe-lit 0x82C88CC8, 2 lightmap
        // 0x82C88D14, 4 grass). Carried for the classic draw's lighting choice; Fable 2's byte 88
        // lies inside its own, differently laid-out tail, so only a v46+ header states it.
        if (info.Version >= 46)
        {
            mesh.SetMeta(Refii.Lighting.Engine_LevelBakedLighting.Fable3LightingModeMeta, (int)info.GlobalLightingMode);
        }

        // The model's own vertex numbering, surface by surface in the order they are added, so
        // anything the file states per vertex can still be looked up after splitting and
        // merging have moved everything. Carried as resource metadata because an ArrayMesh has
        // nowhere else to put a per-surface fact, and because the streamer needs it long after
        // this function has returned.
        var surfaceRegions = new List<string>();
        int sourceVertexTotal = 0;
        foreach (MdlMeshBuffer buffer in info.MeshBuffers)
        {
            sourceVertexTotal += (int)buffer.VertexCount;
        }

        List<MdlGeometry> surfaces = Engine_MdlMerge.ByMaterial(geometry);
        (int lodsAttached, int lodsDropped) = Engine_MdlLods.Attach(data, info, surfaces);
        var surfaceLods = new Godot.Collections.Array();
        int meshVertices = 0;
        foreach (MdlGeometry geo in surfaces)
        {
            meshVertices += geo.VertexCount;
        }

        var sourceVertices = new List<int>(meshVertices);
        bool modernStaticAo = !Engine_ClassicMaterial.Active &&
            Refii.Lighting.Engine_LightingFrame.ModernModel &&
            info.SkinnedCount == 0 &&
            info.MovementMode != 2 &&
            sourceVertexTotal > 0;
        float[] modelSelfAo = modernStaticAo &&
            Engine_ModelSelfAo.TryGetModelSelfAoFast(data, info, sourceVertexTotal, virtualPath, out float[] fastSelfAo)
            ? fastSelfAo
            : System.Array.Empty<float>();
        var pendingSurfaces = new List<(Godot.Collections.Array Arrays, Godot.Collections.Dictionary? Lods, float[]? LevelTable, string Name, Material? Material)>(surfaces.Count);
        long totalEstimatedBytes = 0L;

        foreach (MdlGeometry geo in surfaces)
        {
            if (geo.VertexCount == 0 || geo.TriangleCount == 0)
            {
                continue;
            }

            int vertexCount = geo.VertexCount;

            // A NON-FINITE VERTEX HANGS THE PROCESS, and this is the chokepoint every surface
            // passes through on its way to Godot.
            //
            // Godot derives an ArrayMesh's AABB from these positions, and an instance's render
            // depth from that AABB. One NaN makes the depth NaN, and Godot sorts its render list
            // with std::sort, whose final pass in libstdc++ is __unguarded_linear_insert: no
            // bounds check, relying on the minimum sitting in the first partition. EVERY
            // comparison against NaN is false, so the invariant dies, the insert walks off the
            // FRONT of the array, dereferences what lies below it and faults — and the fault
            // re-executes forever. Observed live: a core pinned, no frames, SIGTERM ignored.
            //
            // WHERE THE NaN COMES FROM. MdlReader.HalfToFloat reproduces IEEE specials exactly,
            // as it must, so 2,048 of the 65,536 half words (3.125%) decode to NaN or +/-Inf —
            // every half whose leading byte is 0x7C..0x7F or 0xFC..0xFF. Those bytes are what
            // padding, 0xFFFF strip-restart sentinels and unparsed table data are made of, so a
            // buffer offset that is off by anything decodes a run of them straight into
            // positions. The decode is not wrong; trusting it blind is.
            //
            // REFUSED, NOT REPAIRED. Clamping would hand Godot geometry we know we mis-read, and
            // it would draw as a spike to the origin. The name in the log is the diagnostic:
            // it says WHICH asset, which is what a silent clamp would cost us.
            // The float arrays reach Godot as they are, viewed as vectors: the checks need nothing
            // else, and a surface's packed arrays are then the only copy made of them here.
            Span<Vector3> vertices = MemoryMarshal.Cast<float, Vector3>(geo.Positions.AsSpan(0, vertexCount * 3));
            Span<Vector3> normals = MemoryMarshal.Cast<float, Vector3>(geo.Normals.AsSpan(0, vertexCount * 3));
            Span<Vector2> uvs = MemoryMarshal.Cast<float, Vector2>(geo.Uvs.AsSpan(0, vertexCount * 2));
            bool finite = true;
            for (int v = 0; v < vertices.Length; ++v)
            {
                // Two shipped buffers (b_left_hand, b_ribcage) hold 15 half-float specials in
                // their UVs. A UV moves nothing, so it is zeroed rather than the surface lost.
                if (!uvs[v].IsFinite())
                {
                    uvs[v] = Vector2.Zero;
                }

                if (!vertices[v].IsFinite() || vertices[v].Length() > MaxVertexReach)
                {
                    finite = false;
                    break;
                }
            }

            if (!finite)
            {
                System.Threading.Interlocked.Increment(ref NonFiniteSurfaces);
                GD.PushWarning(
                    $"[mdl] surface '{geo.Name}' of '{virtualPath}' has a non-finite vertex; " +
                    "the surface is dropped rather than handed to the renderer, which would " +
                    "hang on the depth sort.");
                continue;
            }

            // THE FILE'S OWN ORDER. The models are wound clockwise-front against their normals,
            // which is Godot's convention too, and `Engine_MdlGeometry` only ROTATES them into
            // Y-up, which keeps a winding. Measured 2026-09-22 over every model in the banks
            // (`WindingCensus`): 14,564 of 14,928 surfaces wound that one way, 5 the other, the
            // rest modelled with both sides. Reversing it here, as this did, drew the whole world
            // as back faces — which is what made back-face culling impossible.
            //
            // Every index is carried, a malformed tail too. A count that is not a multiple of
            // three is malformed for a triangle list, but leaving it out would stand those indices
            // at the packed array's default of ZERO — degenerate triangles all anchored on
            // vertex 0, which is a silent corruption rather than a complaint.
            Span<int> indices = MemoryMarshal.Cast<uint, int>(geo.Indices.AsSpan());

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            SetArray(arrays, Mesh.ArrayType.Vertex, Variant.CreateFrom(vertices));
            SetArray(arrays, Mesh.ArrayType.Normal, Variant.CreateFrom(normals));
            SetArray(arrays, Mesh.ArrayType.TexUV, Variant.CreateFrom(uvs));
            SetArray(arrays, Mesh.ArrayType.Index, Variant.CreateFrom(indices));

            // The amb3 lightmap UVs, routed by the SAME per-file-vertex map the vertex bakes
            // use — never a running cursor, because split/merge re-orders emitted vertices.
            // Skinned models are excluded: the tail's vertex order skips skinned records, so
            // the file indices would misalign.
            if (info.LightmapUvs.Length > 0 && info.SkinnedCount == 0 &&
                geo.SourceVertices.Length == geo.VertexCount)
            {
                Vector2[] rented = ArrayPool<Vector2>.Shared.Rent(vertexCount);
                Span<Vector2> lightmapUv = rented.AsSpan(0, vertexCount);
                for (int v = 0; v < vertexCount; v++)
                {
                    int at = geo.SourceVertices[v] * 2;
                    lightmapUv[v] = at >= 0 && at + 1 < info.LightmapUvs.Length
                        ? new Vector2(info.LightmapUvs[at], info.LightmapUvs[at + 1])
                        : default;
                }

                SetArray(arrays, Mesh.ArrayType.TexUV2, Variant.CreateFrom(lightmapUv));
                ArrayPool<Vector2>.Shared.Return(rented);
            }

            // A tangent array that is zero throughout is no basis: see TryCreateShared.
            bool tangentBasis = false;
            for (int t = 0; t + 3 < geo.Tangents.Length && !tangentBasis; t += 4)
            {
                tangentBasis = geo.Tangents[t] != 0f || geo.Tangents[t + 1] != 0f || geo.Tangents[t + 2] != 0f;
            }

            if (geo.HasTangents && tangentBasis)
            {
                SetArray(arrays, Mesh.ArrayType.Tangent, Variant.CreateFrom(geo.Tangents.AsSpan(0, vertexCount * 4)));
            }

            if (geo.HasBones)
            {
                SetArray(arrays, Mesh.ArrayType.Bones, Variant.CreateFrom(geo.BoneIds.AsSpan(0, vertexCount * 4)));
                SetArray(arrays, Mesh.ArrayType.Weights, Variant.CreateFrom(geo.BoneWeights.AsSpan(0, vertexCount * 4)));
            }

            // The coarser levels, in the same reversed winding as the surface's own triangles.
            //
            // A level must have strictly fewer indices than the surface: Godot's
            // `mesh_add_surface` rejects it otherwise (`ERR_CONTINUE(index_count >=
            // index_array_len)`), and again on every re-add from the stored dictionary.
            // `Engine_MdlLods.Attach` only compares a level with the one before it, so the first
            // level is checked here against `indices`. A failing level is dropped and counted.
            Engine_MdlSurfaceArrays.Levels? levels = null;
            if (geo.Lods.Count != 0)
            {
                int baseIndices = indices.Length;
                foreach ((float edgeLength, uint[] level) in geo.Lods)
                {
                    // `Attach` already counted this level as attached: move it to dropped.
                    if (level.Length == 0 || level.Length >= baseIndices)
                    {
                        --lodsAttached;
                        ++lodsDropped;
                        continue;
                    }

                    levels ??= new Engine_MdlSurfaceArrays.Levels();
                    levels.Add(edgeLength, MemoryMarshal.Cast<uint, int>(level.AsSpan()));
                }
            }

            // Still no level: a tree's thinned cards or generated ones, when set (Engine_MdlExtraLods).
            levels ??= Engine_MdlExtraLods.For(info, geo, vertices, arrays, indices);
            Godot.Collections.Dictionary? lods = levels?.Lods;

            // The model's radiance transfer, expanded per vertex: CUSTOM0..2 are the red, green
            // and blue transfer vectors the shader dots with the entity's probe. Absent on a
            // model that ships none, where the shader keeps its lookup along the normal.
            // Read by the FABLE model alone: under the unified model Godot carries the fill and
            // these 48 bytes a vertex would be uploaded for nothing.
            // Under the classic renderer the title's own AMB2 shader takes the transfer as the
            // file carries it: the PCA weights in CUSTOM0 and the cluster offset in CUSTOM3.x
            // (the position's w), with the cluster table on the mesh for the constants.
            // A tree part's wind lanes for the title's tree shaders: the colours in CUSTOM1 and
            // CUSTOM2, POSITION1's xyz in CUSTOM3.yzw beside the cluster offset (Engine_MdlTreeStreams).
            float[] treePivots = System.Array.Empty<float>(), treeColour1 = treePivots, treeColour2 = treePivots;
            // The modern renderer takes them for a tree alone, whose surfaces all draw the tree
            // shader (standard_material_tree): anywhere else CUSTOM1/2 are read as transfer.
            bool treeStreams = (Engine_ClassicMaterial.Active || info.MovementMode == 2) &&
                geo.SourceVertices.Length == geo.VertexCount &&
                Engine_MdlTreeStreams.TryRead(
                    data, info, geo.SourceVertices, out treePivots, out treeColour1, out treeColour2);
            if (treeStreams)
            {
                arrays[(int)Mesh.ArrayType.Custom1] = treeColour1;
                arrays[(int)Mesh.ArrayType.Custom2] = treeColour2;
                arrays[(int)Mesh.ArrayType.Custom3] = treePivots;
            }
            if (Engine_ClassicMaterial.Active &&
                geo.SourceVertices.Length == geo.VertexCount &&
                Engine_MdlTransfer.TryRaw(
                    data, info, geo.SourceVertices, out float[] rawWeights, out float[] rawClusters))
            {
                arrays[(int)Mesh.ArrayType.Custom0] = rawWeights;
                if (treeStreams)
                {
                    for (int v = 0; v < geo.VertexCount; v++)
                    {
                        treePivots[v * 4] = rawClusters[v * 4];
                    }

                    // The array slot took a copy of the pivots when it was set above.
                    arrays[(int)Mesh.ArrayType.Custom3] = treePivots;
                }
                else
                {
                    arrays[(int)Mesh.ArrayType.Custom3] = rawClusters;
                }
                mesh.SetMeta(Engine_MdlTransfer.DescriptorMeta, System.Array.ConvertAll(info.ShDescriptor, v => (int)v));
                mesh.SetMeta(Engine_MdlTransfer.TableMeta, info.ShProbeData);
                System.Threading.Interlocked.Increment(ref SurfacesWithTransfer);
            }
            else if (!treeStreams && !Refii.Lighting.Engine_LightingFrame.ModernModel &&
                geo.SourceVertices.Length == geo.VertexCount &&
                Engine_MdlTransfer.TryExpand(
                    data, info, geo.SourceVertices, out float[] tr, out float[] tg, out float[] tb))
            {
                arrays[(int)Mesh.ArrayType.Custom0] = tr;
                arrays[(int)Mesh.ArrayType.Custom1] = tg;
                arrays[(int)Mesh.ArrayType.Custom2] = tb;
                System.Threading.Interlocked.Increment(ref SurfacesWithTransfer);
            }
            else if (modernStaticAo && modelSelfAo.Length > 0 && !treeStreams && geo.SourceVertices.Length > 0)
            {
                arrays[(int)Mesh.ArrayType.Color] = Engine_ModelSelfAo.BuildColorStream(modelSelfAo, geo);
            }

            // Wind comes from the mesh header, not the material: the file states a movement mode
            // per mesh (1 low plant, 2 tree), and every mesh carrying it is vegetation
            // (src/tools/WindGate.cs). So the mesh's statement is part of the shared material's
            // identity, never a mutation of a material another surface also holds.
            Material? material =
                Engine_MdlMaterial.TryCreateShared(geo.Material, info, geo.AuthoredNormals, tangentBasis);
            if (material is null)
            {
                ReportFallback(virtualPath, geo.Name, geo.Material);
                material = Engine_MdlMaterial.Fallback(geo.Material);
            }

            totalEstimatedBytes += Refii.Core.Engine_ArrayMeshes.EstimateSurfaceBytes(arrays);
            pendingSurfaces.Add((arrays, lods, levels?.Packed(), geo.Name, material));
            surfaceLods.Add(lods ?? new Godot.Collections.Dictionary());
            if (lods is not null)
            {
                System.Threading.Interlocked.Increment(ref SurfacesWithLods);
            }
            else if (info.Lods.Count != 0)
            {
                System.Threading.Interlocked.Increment(ref SurfacesWithoutLods);
            }

            if (geo.SourceVertices.Length == vertexCount)
            {
                sourceVertices.AddRange(geo.SourceVertices);
            }
            else
            {
                sourceVertices.AddRange(System.Linq.Enumerable.Repeat(-1, vertexCount));
            }

            surfaceRegions.Add(geo.Region);
            ++added;
        }

        if (added == 0)
        {
            error = "no surfaces produced";
            return null;
        }

        Refii.Core.Engine_ServerSyncs.RunGpuUpload(System.Math.Max(4096L, totalEstimatedBytes), () =>
        {
            for (int i = 0; i < pendingSurfaces.Count; i++)
            {
                (Godot.Collections.Array arrays, Godot.Collections.Dictionary? lods, float[]? levelTable, string surfaceName, Material? surfaceMaterial) = pendingSurfaces[i];
                int surfaceIndex = Engine_MdlSurfaceArrays.AddDirect(mesh, Mesh.PrimitiveType.Triangles, arrays, lods, levelTable);
                if (surfaceName.Length != 0)
                {
                    mesh.SurfaceSetName(surfaceIndex, surfaceName);
                }
                if (surfaceMaterial is not null)
                {
                    mesh.SurfaceSetMaterial(surfaceIndex, surfaceMaterial);
                }
            }
        });

        using (Variant sourceMap = Variant.CreateFrom(CollectionsMarshal.AsSpan(sourceVertices)))
        {
            mesh.SetMeta(SourceVertexMapMeta, sourceMap);
        }
        mesh.SetMeta(SourceVertexTotalMeta, sourceVertexTotal);
        mesh.SetMeta(SurfaceRegionsMeta, surfaceRegions.ToArray());
        if (info.FileBoundingSphere.Length == 4)
        {
            mesh.SetMeta(BoundSphereMeta, info.FileBoundingSphere);
        }
        if (FurOf(data, info) is { } fur)
        {
            mesh.SetMeta(FurMeta, fur);
        }

        System.Threading.Interlocked.Add(ref LodLevelsAttached, lodsAttached);
        System.Threading.Interlocked.Add(ref LodLevelsDropped, lodsDropped);
        if (lodsAttached != 0)
        {
            mesh.SetMeta(SurfaceLodsMeta, surfaceLods);
            if (!_lodsReported)
            {
                _lodsReported = true;
                var keys = new System.Text.StringBuilder();
                foreach (MdlGeometry geo in surfaces)
                {
                    foreach ((float edgeLength, uint[] level) in geo.Lods)
                    {
                        keys.Append(keys.Length == 0 ? " " : ", ")
                            .Append($"{edgeLength:0.####}:{level.Length / 3}tri");
                    }
                }

                GD.Print($"[mdl] lods: first model with surface LODs is '{virtualPath}': " +
                    $"{lodsAttached} level(s) over {added} surface(s) from {info.Lods.Count} " +
                    $"coarser payload(s), {lodsDropped} dropped; edge-length keys{keys}. " +
                    "Further models are counted, not printed.");
            }
        }

        // THE SKELETON THE BONE INDICES REFER TO, carried on the mesh.
        //
        // A skinned mesh's BONES array indexes THIS model's bone table, and the live pose comes
        // from the guest, which numbers its own. Nothing downstream could check that those two
        // agree because the table was parsed and then thrown away — so a rig built in the wrong
        // order attaches every vertex to the wrong bone and looks like a broken character rather
        // than like a mismatched index space.
        if (info.Bones.Count != 0)
        {
            var parents = new int[info.Bones.Count];
            var names = new string[info.Bones.Count];
            for (int i = 0; i < info.Bones.Count; i++)
            {
                parents[i] = info.Bones[i].ParentId;
                names[i] = info.Bones[i].Name;
            }

            mesh.SetMeta(BoneParentsMeta, parents);
            mesh.SetMeta(BoneNamesMeta, names);

            // AND THE MODEL'S OWN REST POSE, which the reader has always parsed and nothing has
            // ever read.
            //
            // It is the missing half of binding a skin. The bone table alone says what the
            // indices MEAN; this says where those bones are, so a skeleton can be built in the
            // model's numbering without asking the guest for anything. That matters because the
            // guest numbers its rig differently — 161 bones against this model's 143 for the
            // child hero — and a skeleton built in the guest's order binds every vertex to the
            // wrong bone.
            //
            // Flat, stride 11, exactly as the file stores it: quaternion xyzw at 0, translation
            // at 4, scale at 7, one pad float at 10. Reshaping it here would be a second layout
            // to keep in agreement with the reader, and the reader is the one that matches the
            // 44-byte record on disk.
            if (info.HasBoneTransforms && info.BoneTransforms.Count == info.Bones.Count)
            {
                var rest = new float[info.Bones.Count * BoneRestStride];
                for (int i = 0; i < info.Bones.Count; i++)
                {
                    System.Array.Copy(
                        info.BoneTransforms[i], 0, rest, i * BoneRestStride, BoneRestStride);
                }

                mesh.SetMeta(BoneRestMeta, rest);
            }
        }

        error = string.Empty;
        return mesh;
    }

    /// <summary>Hands a surface array to Godot's collection and drops this side's hold on it at once.</summary>
    private static void SetArray(Godot.Collections.Array arrays, Mesh.ArrayType slot, Variant value)
    {
        arrays[(int)slot] = value;
        value.Dispose();
    }

    /// <summary>Metadata key: the model's bone parent indices, in the order BONES indexes.</summary>
    /// <summary>The fur blocks as <see cref="FurMeta"/> carries them; null for a model without fur.</summary>
    private static Godot.Collections.Array? FurOf(byte[] data, MdlInfo info)
    {
        if (info.FurBlocks.Count == 0)
        {
            return null;
        }
        data = info.Body ?? data;
        var blocks = new Godot.Collections.Array();
        foreach (MdlFurBlock block in info.FurBlocks)
        {
            if (block.FurMaterial >= info.Meshes.Count || info.Meshes[(int)block.FurMaterial].Fur is not { } furValues ||
                block.DataOffset < 0 || block.DataOffset + block.Count * 20L > data.Length)
            {
                continue;
            }
            int bufferBase = 0;
            for (int b = 0; b < block.RecordIndex && b < info.MeshBuffers.Count; b++)
            {
                bufferBase += (int)info.MeshBuffers[b].VertexCount;
            }
            int count = (int)block.Count;
            var colours = new Color[count];
            var lanes = new float[count * 4];
            var span = new System.ReadOnlySpan<byte>(data);
            for (int v = 0; v < count; v++)
            {
                int at = block.DataOffset + v * 20;
                // D3DCOLOR, ARGB in the word: the stage reads (r, g, b, a).
                uint argb = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(span.Slice(at, 4));
                colours[v] = new Color(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f,
                    (argb & 0xFF) / 255f, (argb >> 24) / 255f);
                for (int k = 0; k < 4; k++)
                {
                    lanes[v * 4 + k] = System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(span.Slice(at + 4 + k * 4, 4));
                }
            }
            blocks.Add(new Godot.Collections.Dictionary
            {
                ["base"] = bufferBase,
                ["count"] = count,
                ["fur"] = furValues,
                ["colour"] = colours,
                ["lanes"] = lanes,
            });
        }
        return blocks.Count != 0 ? blocks : null;
    }

    public const string BoneParentsMeta = "bone_parents";

    /// <summary>Metadata key: the model's bone names, same order.</summary>
    public const string BoneNamesMeta = "bone_names";

    /// <summary>
    /// Metadata key: the model's LOCAL rest transforms, same order, <see cref="BoneRestStride"/>
    /// floats each. Absent when the model ships a bone table with no transforms to go with it.
    /// </summary>
    public const string BoneRestMeta = "bone_rest";

    /// <summary>
    /// Floats per bone in <see cref="BoneRestMeta"/>: quaternion xyzw, translation, scale, pad.
    /// </summary>
    public const int BoneRestStride = 11;
}
