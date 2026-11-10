using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;
using Refii.Textures;

namespace Refii.Level;

/// <summary>
/// The classic renderer's terrain: the heightfield as the title draws it, patch by patch on
/// its own LANDSCAPEMATERIAL shaders, each patch's splat layers as its materials.
/// </summary>
/// <remarks>
/// THE TITLE'S DRAW (`sub_821DD618`, `sub_8221FC40`, `sub_822101F8`, `sub_822159D8`): a
/// foreground patch is 32x32 cells drawn as four 16-cell quadrants of hardware quad patches
/// with no vertex buffer; the vertex shader takes (quadrant, u, v), decodes the quadrant with
/// g_GridWidth = (2, .5, .25, 0), places the vertex at (q + uv) * g_PositionScale.xy +
/// g_PositionOffSetXY, and samples the level's height (s16) and normal (s17) textures at
/// pos * g_TexCoordScale.xy and the layer's blend map (s18). The pixel shader takes one splat
/// layer per draw — diffuse, normal, cliff colour and cliff normal, g_UVCliffFactor... for
/// their tiling — and layers are additive over the first. Here each patch is a mesh whose
/// vertices carry (q, u, v) in CUSTOM0 and their real position for Godot's bounds; the
/// height texture holds metres with g_PositionScale.z = 1, which the shader multiplies to the
/// same value.
///
/// Deviations, stated: the normal texture is derived from the heights (the .ehf's DXN blob is
/// not decoded yet); every patch is drawn in the foreground pass (the title switches to its
/// page-textured background pass beyond 48..64 m); the blend-map lanes and the cliff UV
/// factors follow the host parser's reading of the layer record and are to be checked on
/// screen.
/// </remarks>
public static class Engine_ClassicTerrain
{
    /// <summary>The heightfield's metres per cell on a patch material (the header float the
    /// title keeps at +0x50), for the lightmap factor bound with the bake.</summary>
    public static readonly StringName CellSizeMeta = "classic_cell_size";

    private const string Family = "LANDSCAPEMATERIAL";
    // THE TITLE'S LAYER BLENDS (`sub_8223DB60`, `sub_8221FC40`): every layer draws SRCALPHA with
    // depth written and no alpha test; the base layer's destination factor is ZERO (its colour
    // times its weight REPLACES the background pass) and every later layer's is ONE (weighted
    // additive). Godot: the base is opaque with the colour premultiplied by the weight, the
    // layers above are blend_add. Every layer's first pass carries the ambient and the
    // in-scatter at its weight (PS c11, c28.y). Per quadrant the record's second byte gates the
    // draw and its first byte selects dest ONE and the 1/255 threshold (`sub_8221FC40`), so a
    // layer is split into its base quadrants and its additive ones, and an unpainted vertex of an
    // additive quadrant is dropped by the threshold. The background pass is not drawn yet, so the foreground does not
    // fade out at 48..64 units: it stays the ground at every distance.
    // CULL_FRONT, NOT CULL_BACK. The landscape vertex stage places every vertex from its own
    // constants (grid index x g_PositionScale + g_PositionOffSetXY, height from s16) and never
    // reads MODEL_MATRIX, but the patch node sits under the level root's Scale(-1, 1, 1), and
    // Godot flips the culled face for a mirrored instance. The geometry is not mirrored, so the
    // flip culls the top: cull_back showed the ground only from below.
    private const string FirstRenderMode = "unshaded, depth_draw_opaque, cull_front";
    private const string FirstDefines = "#define XR_COLOUR_OUT(c) ((c).rgb * (c).a)\n" + QuadrantGate;
    // The title draws a layer per quadrant only where the record's second byte is set
    // (`sub_8221FC40`: `if (*(layer + 36 + q))`); the paint atlas under a skipped quadrant is
    // never read. One draw per layer here, so the skipped quadrants' vertices leave the clip
    // volume (CUSTOM0.x is the quadrant).
    //
    // A vertex under the layer's discard threshold gets the title's NaN position (VS c255), which
    // Xenos answers by dropping every primitive that uses it. A NaN vertex is undefined in
    // Vulkan (a triangle can rasterise as a sheet across the screen), so the vertex is placed
    // at its mesh position instead and flagged, and the pixel stage discards every fragment of
    // a triangle that touches it. The flag is the stage's own kill predicate: every translated
    // LANDSCAPEMATERIAL vertex stage sets `p0` once, to `threshold > weight`, just before the
    // NaN write (an `isnan` on the result can be folded away). The macro is the tail of the
    // translator's `POSITION = XR_POSITION_OUT(oPos);`.
    private const string QuadrantGate =
        "uniform uint xr_quadrant_mask = 15u;\n" +
        "varying float xr_killed;\n" +
        "#define XR_POSITION_OUT(p) (p); " +
        "xr_killed = p0 ? 1.0 : 0.0; " +
        "if (xr_killed > 0.5) { POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0); } " +
        "if (((xr_quadrant_mask >> uint(CUSTOM0.x)) & 1u) == 0u) { POSITION = vec4(2.0, 2.0, 2.0, 1.0); }\n" +
        "#define XR_BEFORE_OUTPUT(c) if (xr_killed > 0.0) { discard; }";

    private const string LayerRenderMode = "unshaded, blend_add, depth_draw_never, cull_front";

    /// <summary>
    /// THE TITLE'S PATCH IS 32 x 32 CELLS whatever the host grid's spacing: four quadrants of 16,
    /// one vertex a cell (`sub_8221FC40`, tessellation level 15), the cell being the chunk's extent
    /// over 32. Deriving the cell count from the finer heightfield grid made each patch a slab of
    /// hundreds of thousands of triangles.
    /// </summary>
    private const int PatchCells = 32;

    /// <summary>Transparent sort order of every landscape layer: before the shadow spheres and the water.</summary>
    public const int TerrainRenderPriority = -120;

    /// <summary>
    /// Builds one mesh per patch with its layer materials set, from the .ehf and its decoded
    /// heights. False with the reason when the shaders or the data are not there.
    /// </summary>
    public static bool TryBuild(byte[] ehf, GhfHeights heights, string name, out List<ArrayMesh> patches, out string error)
    {
        patches = new List<ArrayMesh>();
        error = string.Empty;
        if (!Engine_ClassicShaders.Available)
        {
            error = "no translated shaders";
            return false;
        }
        if (!Engine_EhfBody.TryParse(ehf, out EhfParsedBody body, out error))
        {
            return false;
        }
        if (body.Chunks.Count == 0 || body.Lods.Count == 0)
        {
            error = "no chunks or no layer palette";
            return false;
        }
        string pixelEntry = Engine_ClassicShaders.FindEntry("PSHADER", Family, new[] { "BLT1", "GLT1", "MIST0" });
        string vertexEntry = Engine_ClassicShaders.FindEntry("VSHADER", Family, new[] { "BLT1", "GLT1", "MIST0" });
        if (pixelEntry.Length == 0 || vertexEntry.Length == 0)
        {
            error = "no landscape pair in the bank";
            return false;
        }

        float spacing = heights.Spacing;
        int samplesX = heights.Width, samplesY = heights.Height;
        float extentX = (samplesX - 1) * spacing, extentY = (samplesY - 1) * spacing;
        float originX = heights.OriginX, originY = heights.OriginZ;

        // The level textures the vertex shader samples (heights in metres, normals from them) and
        // every patch's geometry are pure functions of the heights and the chunk records, so they
        // are built side by side; the materials below stay serial in chunk order (the shared paint
        // and texture tables, Engine_ClassicMaterial.Register).
        ImageTexture? heightTexture = null;
        ImageTexture? normalTexture = null;
        var patchMeshes = new ArrayMesh?[body.Chunks.Count];
        System.Threading.Tasks.Parallel.Invoke(
            () => heightTexture = HeightTexture(heights),
            () => normalTexture = NormalTexture(heights),
            () => System.Threading.Tasks.Parallel.For(0, body.Chunks.Count, ci =>
            {
                EhfChunk chunk = body.Chunks[ci];
                float size = chunk.Extent[0] - chunk.Origin[0];
                if (size > 0f)
                {
                    patchMeshes[ci] = PatchMesh(heights, chunk.Origin[0], chunk.Origin[1], PatchCells / 2, size / PatchCells);
                }
            }));
        var paints = new Dictionary<int, ImageTexture>();
        var layerTextures = new Dictionary<uint, (Texture2D? Diffuse, Texture2D? Normal, Texture2D? Cliff, Texture2D? CliffNormal)>();

        // g_CliffBlendOffsetAndScale: the header floats after the spacing (res+84, res+88).
        float cliffOffset = ReadF32(ehf, 47), cliffScale = ReadF32(ehf, 51);

        int chunkWidth = (int)body.ChunkWidth;
        int layersMin = int.MaxValue, layersMax = 0, layersTotal = 0, blendDisagreements = 0;
        for (int ci = 0; ci < body.Chunks.Count; ci++)
        {
            EhfChunk chunk = body.Chunks[ci];
            const int cells = PatchCells;
            // The chunk record's second vector is its FAR CORNER, not its size: chunk 1 spans
            // (0, 16) to (16, 32), a 16 m patch of 32 cells at the heightfield's 0.5 m. Dividing
            // the corner made every patch past the first row hundreds of metres wide, stacked
            // over the others.
            float size = chunk.Extent[0] - chunk.Origin[0];
            if (!(size > 0f))
            {
                continue;
            }
            float patchSpacing = size / cells;
            int quadrant = cells / 2;
            float ox = chunk.Origin[0], oy = chunk.Origin[1];
            ArrayMesh mesh = patchMeshes[ci]!;
            if (mesh.GetSurfaceCount() == 0)
            {
                continue;
            }

            ShaderMaterial? first = null, previous = null;
            int layersHere = 0;
            for (int li = 0; li < chunk.Layers.Count; li++)
            {
                EhfChunkLayer layer = chunk.Layers[li];
                if (layer.MaterialIndex >= body.Lods.Count)
                {
                    continue;
                }
                bool cliff = ((layer.Blend[0] | layer.Blend[1] | layer.Blend[2] | layer.Blend[3]) & 2) != 0;
                var pixelTokens = new List<string> { "BLT1", "GLT1", "MIST0" };
                var vertexTokens = new List<string> { "BLT1", "GLT1", "MIST0" };
                if (cliff)
                {
                    pixelTokens.Add("CLF1");
                    vertexTokens.Add("CLF1");
                }
                string ps = Engine_ClassicShaders.FindEntry("PSHADER", Family, pixelTokens);
                string vs = Engine_ClassicShaders.FindEntry("VSHADER", Family, vertexTokens);
                // PER QUADRANT, NOT PER LAYER (`sub_8221FC40` 0x8222031C..0x822203A8): a layer draws a
                // quadrant when its gate byte (+36 + q) is set, and it is that quadrant's BASE (dest
                // ZERO, threshold -1) unless its first byte (+32 + q) is set, which makes it additive
                // (dest ONE, threshold 1/255). A quadrant the first layer skips takes its base from a
                // later layer; drawn all-additive it had no opaque ground, and the water under the
                // heightfield showed through with the layers' colour added on top.
                uint baseQuadrants = 0, addQuadrants = 0;
                for (int q = 0; q < 4; q++)
                {
                    if (layer.Blend[q] == 0)
                    {
                        continue;
                    }
                    if (layer.TextureIndex[q] == 0)
                    {
                        baseQuadrants |= 1u << q;
                    }
                    else
                    {
                        addQuadrants |= 1u << q;
                    }
                    if ((layer.TextureIndex[q] != 0) == (previous is null))
                    {
                        ++blendDisagreements;
                    }
                }
                foreach ((bool isBase, uint quadrants) in new[] { (true, baseQuadrants), (false, addQuadrants) })
                {
                    if (quadrants == 0)
                    {
                        continue;
                    }
                    string renderMode = isBase ? FirstRenderMode : LayerRenderMode;
                    string defines = isBase ? FirstDefines : QuadrantGate;
                    Shader? shader = ps.Length != 0 && vs.Length != 0
                        ? Engine_ClassicShaders.Compose(vs, ps, renderMode, 0, defines)
                        : null;
                    if (shader is null)
                    {
                        continue;
                    }
                    // THE TERRAIN DRAWS BEFORE THE WATER. The title's main scene runs
                    // LandscapeForeground (0x82A86710), then Shadow Spheres, then the Water Layer
                    // (0x82A86898); our additive layers sorted after the water (priority -2/-1) and
                    // added the lake bed's paint over the finished surface, square by quadrant.
                    // Below the shadow spheres (-100), above the sky (-127).
                    var material = new ShaderMaterial { Shader = shader, RenderPriority = TerrainRenderPriority };
                    Engine_ClassicMaterial.Register(material, Family, pixelTokens, vertexTokens, renderMode);
                    material.SetMeta(Engine_ClassicMaterial.DefinesMeta, defines);

                    // The vertex stage: the level textures and the patch's placement.
                    material.SetShaderParameter("vs_s16", heightTexture!);
                    material.SetShaderParameter("vs_s17", normalTexture!);
                    material.SetShaderParameter("vs_s18", Paint(body, layer, paints));
                    material.SetShaderParameter("vs_g_PositionOffSetXY", new Vector4(ox, oy, 0f, 0f));
                    material.SetShaderParameter("vs_g_PositionScale", new Vector4(quadrant * patchSpacing, quadrant * patchSpacing, 1f, 1f));
                    // uv = (world - zw) * xy (the VS: r1.yz = offset - zw, r2.xy = r0.zw * xy): u runs
                    // across the height texture's Width, the X samples, so x scales by 1/extentX.
                    // Swapped, every square level drew right and a non-square one (bwsmarket,
                    // 577x769) read its heights from the wrong place: terrain floating over the square.
                    material.SetShaderParameter("vs_g_TexCoordScale", new Vector4(1f / extentX, 1f / extentY, originX, originY));
                    material.SetMeta(CellSizeMeta, spacing);
                    material.SetShaderParameter("vs_g_GridWidth", new Vector4(2f, 0.5f, 0.25f, 0f));
                    // c115 (`sub_82B2BF50`): (16 / paint width, 16 / paint height, a, b), a and b the
                    // layer record's two floats, the window's origin in normalised UV in the shared
                    // paint atlas. 16 texels per quadrant, one per cell.
                    material.SetShaderParameter("vs_g_LandscapeBlendMapUVScaleAndOffset",
                        new Vector4(layer.MaskScaleU, layer.MaskScaleV, layer.TileU, layer.TileV));
                    // Binding 396: -1 on a base quadrant (never discard), 1/255 on an additive one (a
                    // vertex the layer does not paint is dropped).
                    float discard = isBase ? -1f : 1f / 255f;
                    material.SetShaderParameter("vs_g_ThemeWeightDiscardThreshold", new Vector4(discard, 0f, 0f, 0f));
                    material.SetShaderParameter("xr_quadrant_mask", quadrants);

                    // The pixel stage: the splat layer's textures and tiling, the pass flag.
                    EhfLodEntry lod = body.Lods[(int)layer.MaterialIndex];
                    (Texture2D? diffuse, Texture2D? normal, Texture2D? cliffColour, Texture2D? cliffNormal) =
                        LayerTextures(layer.MaterialIndex, lod, layerTextures);
                    if (diffuse is not null) material.SetShaderParameter("ps_g_DiffuseTextureSampler", diffuse);
                    if (normal is not null) material.SetShaderParameter("ps_g_NormalTextureSampler", normal);
                    if (cliffColour is not null) material.SetShaderParameter("ps_g_CliffSampler", cliffColour);
                    if (cliffNormal is not null) material.SetShaderParameter("ps_g_CliffNormalMapSampler", cliffNormal);
                    // VS c8, binding 402 (`Render_MeshMaterialSubmit_INFERRED_8`): (material+212,
                    // +188, +216, +192), the cliff and ground tiling the title also binds its cliff
                    // and ground textures with. The vertex stage builds every layer UV from .x and
                    // .y; unset, the whole ground sampled one texel. No landscape stage reads .zw.
                    material.SetShaderParameter("vs_g_UVCliffFactor_UVGroundFactor_UVCliffMacroMapFactor_UVGroundMacroMapFactor",
                        new Vector4(lod.DetailTileScale, lod.BaseTileScale, 0f, 0f));
                    // c11 and c28 (`sub_822159D8`): 1 and (1, 1, 0, 0) on each layer's first pass,
                    // 0 and (1, 0, 0, 0) on its later light passes. The pass counter is reset per
                    // layer (`sub_8221A7A0` zeroes context+84), so every layer carries its weighted
                    // share of the ambient and the in-scatter. Only first passes are drawn here.
                    const float ambient = 1f;
                    material.SetShaderParameter("ps_g_LandscapeAmbientLightFactor", new Vector4(ambient, ambient, ambient, ambient));
                    material.SetShaderParameter("ps_g_CliffBlendOffsetAndScale", new Vector4(cliffOffset, cliffScale, 0f, 0f));
                    material.SetShaderParameter("ps_g_BackgroundFadeDistanceSquaredFactorAndOffset", new Vector4(0f, 0f, 0f, 0f));
                    material.SetShaderParameter("ps_g_AtmosphericFactors", new Vector4(1f, ambient, 0f, 0f));
                    // g_ScreenShadowMap: the title's screen-space sun mask (L8), which the landscape
                    // PS multiplies into its sun term in place of the cascade samplers. The mask pass
                    // is not built yet, so the terrain is sunlit without shadows rather than unlit.
                    material.SetShaderParameter("ps_g_ScreenShadowMap", Engine_ClassicScreenShadow.Mask);
                    material.SetShaderParameter("ps_g_Extinction", Engine_ClassicAtmosphere.ExtinctionTexture);
                    material.SetShaderParameter("ps_g_InScattering", Engine_ClassicAtmosphere.InScatteringTexture);
                    if (Engine_ClassicShadows.Instance is { } shadows)
                    {
                        material.SetShaderParameter("ps_g_DirectionalLightShadowMapSampler0", shadows.CascadeTexture(0));
                        material.SetShaderParameter("ps_g_DirectionalLightShadowMapSampler1", shadows.CascadeTexture(1));
                    }
                    material.SetShaderParameter(Refii.Models.Engine_ClassicSpotShadows.SamplerName, Refii.Models.Engine_ClassicSpotShadows.Maps);
                    material.SetShaderParameter("xr_booleans", (1u << (16 + 4)) | (1u << (16 + 1)) | Refii.Models.Engine_ClassicSpotShadows.DoSpotShadows);

                    if (first is null)
                    {
                        first = material;
                    }
                    else
                    {
                        previous!.NextPass = material;
                    }
                    previous = material;
                    ++layersHere;
                }
            }
            if (layersHere != 0)
            {
                layersMin = Math.Min(layersMin, layersHere);
                layersMax = Math.Max(layersMax, layersHere);
                layersTotal += layersHere;
            }
            if (first is null)
            {
                continue;
            }
            first.SetMeta(Engine_ClassicMaterial.LayerChainMeta, true);
            mesh.SurfaceSetMaterial(0, first);
            mesh.ResourceName = $"{name}{ci}";
            patches.Add(mesh);
        }
        if (patches.Count == 0)
        {
            error = "no patch took a layer";
            return false;
        }
        GD.Print($"[classic] terrain layers per patch: min {layersMin} max {layersMax} total {layersTotal} over {patches.Count} patches; " +
            $"distinct paints {paints.Count}; quadrants whose base is not the first layer's: {blendDisagreements}");
        return true;
    }

    /// <summary>
    /// A patch's mesh: four quadrants of (quadrant + 1)^2 vertices carrying (q, u, v) in
    /// CUSTOM0 and their place on the heightfield as the position, in the grid's winding.
    /// </summary>
    private static ArrayMesh PatchMesh(GhfHeights heights, float ox, float oy, int quadrant, float spacing)
    {
        int edge = quadrant + 1;
        int perQuadrant = edge * edge;
        var positions = new Vector3[4 * perQuadrant];
        var custom = new float[4 * perQuadrant * 4];
        var indices = new int[4 * quadrant * quadrant * 6];
        int k = 0;
        for (int q = 0; q < 4; q++)
        {
            int qx = q & 1, qy = q >> 1;
            int baseVertex = q * perQuadrant;
            for (int y = 0; y < edge; y++)
            {
                for (int x = 0; x < edge; x++)
                {
                    int v = baseVertex + y * edge + x;
                    float u = (float)x / quadrant, w = (float)y / quadrant;
                    float gx = ox + (qx * quadrant + x) * spacing;
                    float gy = oy + (qy * quadrant + y) * spacing;
                    positions[v] = new Vector3(gx, HeightAt(heights, gx, gy), gy);
                    custom[v * 4] = q;
                    custom[v * 4 + 1] = u;
                    custom[v * 4 + 2] = w;
                    custom[v * 4 + 3] = 0f;
                }
            }
            for (int y = 0; y < quadrant; y++)
            {
                for (int x = 0; x < quadrant; x++)
                {
                    int i00 = baseVertex + y * edge + x;
                    int i10 = i00 + 1;
                    int i01 = i00 + edge;
                    int i11 = i01 + 1;
                    indices[k++] = i00; indices[k++] = i01; indices[k++] = i10;
                    indices[k++] = i10; indices[k++] = i01; indices[k++] = i11;
                }
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        arrays[(int)Mesh.ArrayType.Custom0] = custom;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        Refii.Core.Engine_ArrayMeshes.AddSurface(mesh, Mesh.PrimitiveType.Triangles, arrays, null, null,
            (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
        return mesh;
    }

    private static float HeightAt(GhfHeights heights, float gx, float gy)
    {
        int x = Math.Clamp((int)MathF.Round((gx - heights.OriginX) / heights.Spacing), 0, heights.Width - 1);
        int y = Math.Clamp((int)MathF.Round((gy - heights.OriginZ) / heights.Spacing), 0, heights.Height - 1);
        return heights.Values[y * heights.Width + x];
    }

    private static ImageTexture HeightTexture(GhfHeights heights)
    {
        var bytes = new byte[heights.Width * heights.Height * 4];
        Buffer.BlockCopy(heights.Values, 0, bytes, 0, Math.Min(bytes.Length, heights.Values.Length * 4));
        var image = Image.CreateFromData(heights.Width, heights.Height, false, Image.Format.Rf, bytes);
        return Refii.Textures.Engine_TextureUpload.CreateTexture(image);
    }

    /// <summary>The guest's normal map as the shader decodes it: x and y of the unit normal in (0..1), z rebuilt.</summary>
    private static ImageTexture NormalTexture(GhfHeights heights)
    {
        int w = heights.Width, h = heights.Height;
        var bytes = new byte[w * h * 4];
        float[] v = heights.Values;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float left = v[y * w + Math.Max(x - 1, 0)], right = v[y * w + Math.Min(x + 1, w - 1)];
                float down = v[Math.Max(y - 1, 0) * w + x], up = v[Math.Min(y + 1, h - 1) * w + x];
                var n = new Vector3(-(right - left) / (2f * heights.Spacing), -(up - down) / (2f * heights.Spacing), 1f).Normalized();
                int at = (y * w + x) * 4;
                bytes[at] = (byte)Math.Clamp((n.X * 0.5f + 0.5f) * 255f, 0f, 255f);
                bytes[at + 1] = (byte)Math.Clamp((n.Y * 0.5f + 0.5f) * 255f, 0f, 255f);
                bytes[at + 2] = 255;
                bytes[at + 3] = 255;
            }
        }
        return Refii.Textures.Engine_TextureUpload.CreateTexture(Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes));
    }

    /// <summary>A layer's paint map as a texture whose every channel is the weight byte.</summary>
    private static ImageTexture Paint(EhfParsedBody body, EhfChunkLayer layer, Dictionary<int, ImageTexture> cache)
    {
        int index = (int)layer.NameIndex;
        if (cache.TryGetValue(index, out ImageTexture? cached))
        {
            return cached;
        }
        byte[] map = body.SplatIndices;
        int w = (int)body.SplatWidth, h = (int)body.SplatHeight;
        bool resourceMap = index < body.PaintResources.Count && body.PaintResources[index].Data.Length != 0;
        if (resourceMap)
        {
            EhfPaintResource resource = body.PaintResources[index];
            map = resource.Data;
            w = (int)resource.Width;
            h = (int)resource.Height;
        }
        ImageTexture texture;
        if (map.Length == (long)w * h && w > 0 && h > 0)
        {
            // The shader reads the weight from the map's alpha: a paint resource IS the
            // layer's weights; the splat index map names the layer painted on each cell, so it
            // becomes this layer's mask.
            var bytes = new byte[w * h * 4];
            long sum = 0;
            int max = 0;
            for (int i = 0; i < w * h; i++)
            {
                byte weight = resourceMap ? map[i] : (byte)(map[i] == index ? 255 : 0);
                bytes[i * 4] = bytes[i * 4 + 1] = bytes[i * 4 + 2] = bytes[i * 4 + 3] = weight;
                sum += weight;
                max = Math.Max(max, weight);
            }
            GD.Print($"[classic] terrain paint {index}: {(resourceMap ? "resource" : "splat mask")} {w}x{h}, " +
                     $"mean {sum / (double)(w * h) / 255.0:0.000}, max {max}");
            texture = Refii.Textures.Engine_TextureUpload.CreateTexture(Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes));
        }
        else
        {
            var white = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            white.Fill(Colors.White);
            texture = Refii.Textures.Engine_TextureUpload.CreateTexture(white);
        }
        cache[index] = texture;
        return texture;
    }

    private static (Texture2D?, Texture2D?, Texture2D?, Texture2D?) LayerTextures(
        uint index, EhfLodEntry lod,
        Dictionary<uint, (Texture2D? Diffuse, Texture2D? Normal, Texture2D? Cliff, Texture2D? CliffNormal)> cache)
    {
        if (cache.TryGetValue(index, out var cached))
        {
            return cached;
        }
        var loaded = (
            Load(lod.BaseDiffuse, TexSemantic.Albedo), Load(lod.BaseNormal, TexSemantic.Normal),
            Load(lod.DetailDiffuse, TexSemantic.Albedo), Load(lod.DetailNormal, TexSemantic.Normal));
        cache[index] = loaded;
        GD.Print($"[classic] terrain material {index}: diffuse '{lod.BaseDiffuse}' {(loaded.Item1 is null ? "MISSING" : "ok")}, " +
            $"normal '{lod.BaseNormal}' {(loaded.Item2 is null ? "MISSING" : "ok")}, cliff '{lod.DetailDiffuse}' {(loaded.Item3 is null ? "MISSING" : "ok")}, " +
            $"cliff normal '{lod.DetailNormal}' {(loaded.Item4 is null ? "MISSING" : "ok")}, tiling ground {lod.BaseTileScale} cliff {lod.DetailTileScale}");
        return loaded;
    }

    private static Texture2D? Load(string name, TexSemantic semantic) =>
        name.Length == 0 ? null : Engine_TexLibrary.Get(name, semantic, Engine_BnkMeshLoader.ReadBytes, out _);

    private static float ReadF32(byte[] data, int at) =>
        at + 4 <= data.Length
            ? BitConverter.Int32BitsToSingle((data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3])
            : 0f;
}
