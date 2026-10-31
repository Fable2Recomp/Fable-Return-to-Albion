using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

/// <summary>
/// A landscape drawn the way the guest draws it past 64 m: its BACKGROUND path, textured from
/// pre-baked colour pages — the only texturing a vista ever shows (its material palette is an
/// editor default).
/// </summary>
/// <remarks>
/// Background patch i (`sub_82A90120`, res+164) is paired with page set i (`sub_82A90558`;
/// `sub_82236508` sets `*a3 = res+124[i]`). A page is an ATLAS: every 16-cell chunk owns its own
/// rectangle, and two 16-bit maps (res+24 U, res+32 V; LIN_L16, (17·chunksX) x chunksY) give the
/// page coordinate per vertex. VS 15 (binding 393 g_TexCoordScale, 283/284 the 1/17 texel steps):
///
///     u = U[cy][17·cx + k] / 65535      v = V[cy][17·cx + j] / 65535      k, j = 0..16
///
/// so a chunk-boundary vertex carries a different UV per chunk and each chunk is built with its
/// own 17x17 vertices. The chunk origin is `(patchMin − origin) / (16·spacing)`
/// (`sub_822042D0`, binding 282 g_BackgroundMapCellOffset). PS 7 squares the page colour before
/// lighting — the sRGB albedo read is that decode. Page 0 is the finest mip; the guest streams
/// toward it from count−1.
/// </remarks>
public static class Engine_EhfPages
{
    private const int ChunkCells = 16;
    private const int MapStride = ChunkCells + 1;

    /// <summary>CUSTOM0 as four floats: the chunk's atlas rectangle.</summary>
    private static readonly Mesh.ArrayFormat CustomRectFormat =
        (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);

    public sealed class Built
    {
        /// <summary>One mesh per background patch, so each is culled on its own.</summary>
        public List<ArrayMesh> Meshes { get; init; } = new();

        public int Patches { get; init; }

        public int Chunks { get; init; }

        /// <summary>16-cell chunks in the whole grid; those beyond <see cref="Chunks"/> have no page.</summary>
        public int GridChunks { get; init; }
    }

    public static bool TryBuild(byte[] ehf, GhfHeights heights, out Built built, out string error)
    {
        built = null!;
        if (!Engine_EhfBody.TryParse(ehf, out EhfParsedBody body, out error))
        {
            return false;
        }

        if (body.WeightMasks.Count < 2 ||
            body.WeightMasks[0] is not { Data.Length: > 0 } uMap ||
            body.WeightMasks[1] is not { Data.Length: > 0 } vMap)
        {
            error = "no background U/V maps";
            return false;
        }

        int mapWidth = (int)uMap.Width;
        int mapHeight = (int)uMap.Height;
        if (vMap.Width != uMap.Width || vMap.Height != uMap.Height || mapWidth % MapStride != 0)
        {
            error = $"U/V maps {uMap.Width}x{uMap.Height} / {vMap.Width}x{vMap.Height} are not 17-texel chunk rows";
            return false;
        }

        float spacing = heights.Spacing;
        float chunkSize = ChunkCells * spacing;
        var meshes = new List<ArrayMesh>();
        int chunks = 0;

        for (int p = 0; p < body.BackgroundPatches.Count; p++)
        {
            EhfBackgroundPatch patch = body.BackgroundPatches[p];
            if (patch.Pages.Count == 0 ||
                !Engine_EhfAtlas.TryDecodeAt(ehf, (int)patch.Pages[0].Offset, out EhfAtlasImage page, out string pageError))
            {
                error = $"patch {p}: page did not decode ({(patch.Pages.Count == 0 ? "no pages" : "decode")})";
                return false;
            }

            int cx0 = (int)MathF.Round((patch.AabbMin[0] - heights.OriginX) / chunkSize);
            int cy0 = (int)MathF.Round((patch.AabbMin[1] - heights.OriginZ) / chunkSize);
            int cxs = Math.Max(1, (int)MathF.Round((patch.AabbMax[0] - patch.AabbMin[0]) / chunkSize));
            int cys = Math.Max(1, (int)MathF.Round((patch.AabbMax[1] - patch.AabbMin[1]) / chunkSize));

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var rects = new List<float>();
            var indices = new List<int>();

            for (int b = 0; b < cys; b++)
            {
                for (int a = 0; a < cxs; a++)
                {
                    int cx = cx0 + a;
                    int cy = cy0 + b;
                    if (cy < 0 || cy >= mapHeight || cx < 0 || ((cx + 1) * MapStride) > mapWidth)
                    {
                        continue;
                    }

                    int first = positions.Count;
                    for (int j = 0; j <= ChunkCells; j++)
                    {
                        for (int k = 0; k <= ChunkCells; k++)
                        {
                            int gx = (cx * ChunkCells) + k;
                            int gz = (cy * ChunkCells) + j;
                            positions.Add(new Vector3(
                                heights.OriginX + (gx * spacing), At(heights, gx, gz), heights.OriginZ + (gz * spacing)));
                            float left = At(heights, gx - 1, gz);
                            float right = At(heights, gx + 1, gz);
                            float down = At(heights, gx, gz - 1);
                            float up = At(heights, gx, gz + 1);
                            normals.Add(new Vector3(left - right, 2f * spacing, down - up).Normalized());
                            uvs.Add(new Vector2(
                                Unorm(uMap.Data, (cy * mapWidth) + (MapStride * cx) + k),
                                Unorm(vMap.Data, (cy * mapWidth) + (MapStride * cx) + j)));
                        }
                    }

                    // The chunk's atlas rectangle, on every one of its vertices (CUSTOM0).
                    var lo = new Vector2(float.MaxValue, float.MaxValue);
                    var hi = new Vector2(float.MinValue, float.MinValue);
                    for (int n = first; n < uvs.Count; n++)
                    {
                        lo = new Vector2(MathF.Min(lo.X, uvs[n].X), MathF.Min(lo.Y, uvs[n].Y));
                        hi = new Vector2(MathF.Max(hi.X, uvs[n].X), MathF.Max(hi.Y, uvs[n].Y));
                    }

                    for (int n = first; n < uvs.Count; n++)
                    {
                        rects.Add(lo.X);
                        rects.Add(lo.Y);
                        rects.Add(hi.X);
                        rects.Add(hi.Y);
                    }

                    for (int j = 0; j < ChunkCells; j++)
                    {
                        for (int k = 0; k < ChunkCells; k++)
                        {
                            int i00 = first + (j * MapStride) + k;
                            int i10 = i00 + 1;
                            int i01 = i00 + MapStride;
                            int i11 = i01 + 1;
                            indices.Add(i00);
                            indices.Add(i01);
                            indices.Add(i10);
                            indices.Add(i10);
                            indices.Add(i01);
                            indices.Add(i11);
                        }
                    }

                    chunks++;
                }
            }

            if (indices.Count == 0)
            {
                continue;
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
            arrays[(int)Mesh.ArrayType.Custom0] = rects.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
            var mesh = new ArrayMesh();
            Refii.Core.Engine_ArrayMeshes.AddSurface(mesh,
                Mesh.PrimitiveType.Triangles, arrays, null, null, CustomRectFormat);
            mesh.SurfaceSetMaterial(0, ClassicPageMaterial(ehf, patch, heights) ?? PageMaterial(page, heights));
            meshes.Add(mesh);
        }

        if (meshes.Count == 0)
        {
            error = "no chunk landed on the U/V maps";
            return false;
        }

        built = new Built
        {
            Meshes = meshes,
            Patches = meshes.Count,
            Chunks = chunks,
            GridChunks = ((heights.Width - 1) / ChunkCells) * ((heights.Height - 1) / ChunkCells),
        };
        return true;
    }

    /// <summary>Each background patch's finest page as an image, for inspection.</summary>
    public static List<(string Name, Image Image)> DecodePages(byte[] ehf)
    {
        var images = new List<(string, Image)>();
        if (!Engine_EhfBody.TryParse(ehf, out EhfParsedBody body, out _))
        {
            return images;
        }

        for (int p = 0; p < body.BackgroundPatches.Count; p++)
        {
            List<(uint Offset, uint Length)> pages = body.BackgroundPatches[p].Pages;
            for (int i = 0; i < pages.Count; i++)
            {
                if (Engine_EhfAtlas.TryDecodeAt(ehf, (int)pages[i].Offset, out EhfAtlasImage page, out _))
                {
                    images.Add(($"p{p}_m{i}", Image.CreateFromData(page.Width, page.Height, false, Image.Format.Rgba8,
                        Engine_EhfAtlas.Bc1ToRgba(page.Blocks, page.Width, page.Height))));
                }
            }
        }

        return images;
    }

    /// <summary>
    /// The terrain shader in page mode: the ground's own bake, darkening and atmosphere, with the
    /// page as albedo. The bake mapping is the grid's: one lightmap texel per cell over
    /// (W−1)x(H−1) cells from the origin, which `chunk_origin_extent` states in 32-cell chunks.
    /// </summary>
    /// <summary>
    /// Under the classic renderer, the title's background shader over the page the title loads:
    /// the LAST record of the patch's page set (`sub_82A90628` 0x82A90D18..D20).
    /// </summary>
    private static ShaderMaterial? ClassicPageMaterial(byte[] ehf, EhfBackgroundPatch patch, GhfHeights heights)
    {
        if (!Refii.Models.Engine_ClassicMaterial.Active ||
            !Engine_EhfAtlas.TryDecodeAt(ehf, (int)patch.Pages[^1].Offset, out EhfAtlasImage page, out _))
        {
            return null;
        }
        Image image = Image.CreateFromData(
            page.Width, page.Height, false, Image.Format.Rgba8,
            Engine_EhfAtlas.Bc1ToRgba(page.Blocks, page.Width, page.Height));
        ShaderMaterial? material = Engine_ClassicVista.PageMaterial(ImageTexture.CreateFromImage(image));
        material?.SetMeta(Engine_ClassicVista.GridMeta, new Vector3(heights.OriginX, heights.OriginZ, heights.Spacing));
        return material;
    }

    private static ShaderMaterial PageMaterial(EhfAtlasImage page, GhfHeights heights)
    {
        Image image = Image.CreateFromData(
            page.Width, page.Height, false, Image.Format.Rgba8,
            Engine_EhfAtlas.Bc1ToRgba(page.Blocks, page.Width, page.Height));
        float chunk = 32f * heights.Spacing;
        var material = new ShaderMaterial
        {
            Shader = Engine_TerrainSplatMaterial.PageShader(),
        };
        material.SetShaderParameter("page_mode", true);
        if (!image.IsEmpty())
        {
            material.SetShaderParameter("page_albedo", ImageTexture.CreateFromImage(image));
        }
        material.SetShaderParameter("chunk_origin_extent",
            new Vector4(heights.OriginX, heights.OriginZ, chunk, chunk));
        material.SetShaderParameter("chunk_grid_size",
            new Vector4((heights.Width - 1) / 32f, (heights.Height - 1) / 32f, 0f, 255f));
        material.SetShaderParameter("mesh_xform", new Vector4(1f, 1f, 0f, 0f));
        material.SetShaderParameter("weight_size", new Vector4(1f, 1f, 1f, 1f));
        material.SetShaderParameter("splat_params", new Vector4(3f, 0.125f, 0f, 0f));
        return material;
    }

    private static float Unorm(byte[] map, int texel) =>
        texel * 2 + 1 < map.Length ? ((map[texel * 2] << 8) | map[(texel * 2) + 1]) / 65535f : 0f;

    private static float At(GhfHeights heights, int x, int z)
    {
        x = Math.Clamp(x, 0, heights.Width - 1);
        z = Math.Clamp(z, 0, heights.Height - 1);
        return heights.Values[(z * heights.Width) + x];
    }
}
