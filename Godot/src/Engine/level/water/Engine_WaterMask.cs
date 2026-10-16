using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

/// <summary>
/// The water coverage mask — what makes a shoreline fade instead of ending on a polygon edge.
///
/// THE GUEST DOES NOT TEST DEPTH FOR THIS, and that is worth stating because the symptom invites
/// it. A lake that reads as opaque to its own edge looks like a missing depth-thickness term, and
/// Fable 2 has no such term: translating all 589 shipped shader blobs shows the scene depth texture
/// is read by eight particle materials and eight depth-of-field variants, and by no water shader at
/// all. Coverage is authored per cell in the `.water` file and we already parse it — see
/// <see cref="WaterTile.Mask"/> — we simply never built a texture from it.
///
/// THE FILL IS A TEST, NOT A COPY. `sub_82B3B4F8` is the D3DXFillTexture callback:
///
///     r7 = mask[row*width + col]
///     r6 = cntlzw(r7)
///     r4 = (r6 &gt;&gt; 5) &amp; 1        ; 1 iff cntlzw == 32, i.e. iff the byte is zero
///     f1 = (float)r4             ; stored to all four channels
///
/// so the texel is `(byte == 0) ? 1.0 : 0.0` — INVERTED, and expanded to full scale rather than
/// carried through as 1/255. That inversion is what resolves the polarity puzzle: in the file a 1
/// means water, and in the texture a 1 means land. The vertex tessellator kills a vertex whose
/// sampled mask is exactly 1.0, and the pixel shader's coverage term is `1 - mask`.
///
/// THEN IT IS BLURRED THREE TIMES, and without that the whole edge ramp is a no-op.
/// `sub_82B3B2A0` builds a "GAUSSBLUR" effect with sigma = width/12.8 and loops exactly three
/// times (`li r29, 3` at 0x82B3B420), resolving back into the mask each pass. The kernel
/// (`sub_82B1CA68`, mode 2) keeps the taps with `|dx| + |dy| &lt;= 2` — thirteen of them, at offsets
/// of one and two texels. Three passes of a two-texel diamond spread the edge over about six
/// cells, which is the width the EdgeBlendMin/Max ramp is authored against. A hard 0/1 mask would
/// give a hard edge however the ramp is tuned.
///
/// THE TEXTURE IS PER BODY, NOT PER PATCH — and that IS a deviation. The guest gives every patch
/// its own texture sized to that patch's cells and re-binds per patch, because it draws per patch.
/// We merge a body's tiles into one mesh, so one texture spans the body's bounds at the tiles' own
/// cell size. The texels are identical where a tile covers, and land elsewhere; what changes is the
/// draw count, not the values.
/// </summary>
public static class Engine_WaterMask
{
    /// <summary>Blur passes — `li r29, 3` at 0x82B3B420.</summary>
    private const int BlurPasses = 3;

    /// <summary>`sigma = textureWidth * 0.078125` — the reciprocal of 12.8.</summary>
    private const float SigmaPerTexel = 0.078125f;

    /// <summary>A cell with no tile over it is land, so the mesh there fades out rather than in.</summary>
    private const float Land = 1.0f;

    public readonly record struct Mask(ImageTexture Texture, Vector4 Bounds);

    /// <summary>
    /// Builds one body's mask. <c>Bounds</c> is <c>(minX, minZ, 1/sizeX, 1/sizeZ)</c> so the shader
    /// can turn a local XZ position into the texture's UV without the mesh carrying one.
    /// </summary>
    public static Mask? TryBuild(WaterBody body)
    {
        if (body.Tiles.Count == 0)
        {
            return null;
        }

        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        float cell = float.PositiveInfinity;

        foreach (WaterTile tile in body.Tiles)
        {
            if (tile.CellsX <= 0 || tile.CellsZ <= 0)
            {
                continue;
            }

            minX = MathF.Min(minX, tile.CentreX - (tile.ExtentX * 0.5f));
            maxX = MathF.Max(maxX, tile.CentreX + (tile.ExtentX * 0.5f));
            minZ = MathF.Min(minZ, tile.CentreZ - (tile.ExtentZ * 0.5f));
            maxZ = MathF.Max(maxZ, tile.CentreZ + (tile.ExtentZ * 0.5f));

            // The smallest cell any tile uses, so no tile is undersampled by the shared grid.
            cell = MathF.Min(cell, MathF.Min(
                tile.ExtentX / tile.CellsX, tile.ExtentZ / tile.CellsZ));
        }

        if (!float.IsFinite(minX) || !float.IsFinite(cell) || cell <= 0f)
        {
            return null;
        }

        float sizeX = MathF.Max(maxX - minX, cell);
        float sizeZ = MathF.Max(maxZ - minZ, cell);

        // Capped: a body spanning a whole region at a five-unit cell would otherwise allocate a
        // texture nobody asked for. The blur radius is in texels, so a coarser grid simply gives a
        // proportionally wider shore, which is the right failure direction.
        int width = Math.Clamp((int)MathF.Ceiling(sizeX / cell), 4, 1024);
        int height = Math.Clamp((int)MathF.Ceiling(sizeZ / cell), 4, 1024);

        var texels = new float[width * height];
        Array.Fill(texels, Land);

        foreach (WaterTile tile in body.Tiles)
        {
            Stamp(tile, texels, width, height, minX, minZ, sizeX, sizeZ);
        }

        // COUNTED ONCE, AFTER STAMPING — tiles overlap, so counting inside the fill loop tallies
        // texel WRITES and can exceed the texel count.
        int covered = 0;
        foreach (float texel in texels)
        {
            if (texel < 0.5f)
            {
                covered++;
            }
        }

        // NO COVERAGE MEANS THE BUILD FAILED, NOT THAT THERE IS NO WATER.
        //
        // A body exists because the file states tiles for it, so a mask that comes out entirely
        // land is this code being wrong — a mask array shorter than its cell count, a cell count
        // that did not parse, a tile outside the bounds. Returning it drives coverage to zero,
        // ALPHA to zero, and the body DISAPPEARS. Refuse and keep the opaque surface instead.
        if (covered == 0)
        {
            GD.Print(
                $"[water] mask built {width}x{height} from {body.Tiles.Count} tile(s) but no cell " +
                "reads as water — keeping the surface opaque rather than erasing it");
            return null;
        }

        float sigma = MathF.Max(width * SigmaPerTexel, 1e-3f);
        for (int pass = 0; pass < BlurPasses; pass++)
        {
            texels = Blur(texels, width, height, sigma);
        }

        var bytes = new byte[texels.Length];
        for (int i = 0; i < texels.Length; i++)
        {
            bytes[i] = (byte)Math.Clamp((int)MathF.Round(texels[i] * 255f), 0, 255);
        }

        // R8 with no mips — `levels = 1` in the create call, and format table entry 23 is FMT_8,
        // a single 8-bit channel.
        var image = Image.CreateFromData(width, height, false, Image.Format.R8, bytes);
        if (image.IsEmpty())
        {
            return null;
        }
        ImageTexture texture = ImageTexture.CreateFromImage(image);

        // SAY WHAT WAS BUILT. A vanished surface, an inverted mask and wrong bounds all look
        // identical on screen; the coverage percentage separates them in one run — near 0 is a
        // stamping fault, near 100 a polarity fault, a plausible middle means the ramp.
        GD.Print(
            $"[water] mask {width}x{height} cell={cell:0.##} " +
            $"coverage={100f * covered / (width * height):0.#}% " +
            $"from {body.Tiles.Count} tile(s)");

        return new Mask(texture, new Vector4(minX, minZ, 1f / sizeX, 1f / sizeZ));
    }

    /// <summary>
    /// Writes one tile's cells into the shared grid, inverted as the guest's fill does.
    /// </summary>
    private static void Stamp(
        WaterTile tile, float[] texels, int width, int height,
        float minX, float minZ, float sizeX, float sizeZ)
    {
        if (tile.CellsX <= 0 || tile.CellsZ <= 0 ||
            tile.Mask.Length < tile.CellsX * tile.CellsZ)
        {
            return;
        }

        float tileMinX = tile.CentreX - (tile.ExtentX * 0.5f);
        float tileMinZ = tile.CentreZ - (tile.ExtentZ * 0.5f);

        for (int row = 0; row < tile.CellsZ; row++)
        {
            for (int col = 0; col < tile.CellsX; col++)
            {
                // (byte == 0) ? 1 : 0 — `sub_82B3B4F8`. 1 is LAND.
                float value = tile.Mask[(row * tile.CellsX) + col] == 0 ? 1f : 0f;

                // FILL THE RECTANGLE THE CELL COVERS, NOT ONE TEXEL.
                //
                // The shared grid is as fine as the FINEST tile in the body and tiles do NOT agree
                // on cell size, so a coarse tile's cells span many texels each. Painting one texel
                // per source cell leaves the rest at their default of LAND — a mask of scattered
                // dots that three blur passes erase entirely.
                //
                // The half-open span is correct whichever way the resolutions differ: coarse cells
                // fill many texels, and cells finer than the grid collapse to at least one.
                float cellX = tile.ExtentX / tile.CellsX;
                float cellZ = tile.ExtentZ / tile.CellsZ;

                float worldX0 = tileMinX + (col * cellX);
                float worldZ0 = tileMinZ + (row * cellZ);

                int x0 = (int)MathF.Floor((worldX0 - minX) / sizeX * width);
                int y0 = (int)MathF.Floor((worldZ0 - minZ) / sizeZ * height);
                int x1 = (int)MathF.Ceiling((worldX0 + cellX - minX) / sizeX * width);
                int y1 = (int)MathF.Ceiling((worldZ0 + cellZ - minZ) / sizeZ * height);

                x0 = Math.Clamp(x0, 0, width - 1);
                y0 = Math.Clamp(y0, 0, height - 1);
                x1 = Math.Clamp(Math.Max(x1, x0 + 1), 1, width);
                y1 = Math.Clamp(Math.Max(y1, y0 + 1), 1, height);

                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        texels[(y * width) + x] = value;
                    }
                }
            }
        }
    }

    /// <summary>
    /// One pass of the guest's 13-tap diamond: taps with <c>|dx| + |dy| &lt;= 2</c>, weighted
    /// <c>exp(-(dx² + dy²) / 2σ²)</c> and normalised by their sum. Edges clamp, matching the
    /// sampler state the water draw sets (AddressU/V = 2 = k_ClampToEdge).
    /// </summary>
    private static float[] Blur(float[] source, int width, int height, float sigma)
    {
        var taps = new List<(int Dx, int Dy, float Weight)>(13);
        float total = 0f;
        float twoSigmaSq = 2f * sigma * sigma;

        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                if (Math.Abs(dx) + Math.Abs(dy) > 2)
                {
                    continue;
                }

                float w = MathF.Exp(-((dx * dx) + (dy * dy)) / twoSigmaSq);
                taps.Add((dx, dy, w));
                total += w;
            }
        }

        var result = new float[source.Length];
        float inv = total > 1e-8f ? 1f / total : 1f;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0f;
                foreach ((int dx, int dy, float w) in taps)
                {
                    int sx = Math.Clamp(x + dx, 0, width - 1);
                    int sy = Math.Clamp(y + dy, 0, height - 1);
                    sum += source[(sy * width) + sx] * w;
                }

                result[(y * width) + x] = sum * inv;
            }
        }

        return result;
    }
}
