using System;
using System.Collections.Generic;

namespace Refii.Level;

public sealed class EhfRenderTile
{
    public int CellX { get; set; }

    public int CellY { get; set; }

    public int CellWidth { get; init; }

    public int CellHeight { get; init; }

    public int SubWidth { get; init; }

    public int SubHeight { get; init; }

    public float MinX { get; init; }

    public float MinY { get; init; }

    public float MaxX { get; init; }

    public float MaxY { get; init; }

    public bool BoundsUsable { get; init; }
}

public sealed class EhfAlbedo
{
    public int Width { get; init; }

    public int Height { get; init; }

    public byte[] Rgba { get; init; } = Array.Empty<byte>();

    public int TilesUsed { get; init; }

    public int TilesTotal { get; init; }
}

public static class Engine_EhfAlbedo
{
    private const int CellStride = 160;

    public static bool TryBuild(
        byte[] ehf, int terrainVerticesX, int terrainVerticesZ,
        out EhfAlbedo albedo, out string error)
    {
        albedo = null!;
        error = string.Empty;

        int cellsX = terrainVerticesX > 1 ? terrainVerticesX - 1 : terrainVerticesX;
        int cellsY = terrainVerticesZ > 1 ? terrainVerticesZ - 1 : terrainVerticesZ;
        if (cellsX <= 0 || cellsY <= 0)
        {
            error = "terrain has no cells";
            return false;
        }

        List<EhfRenderTile> tiles = ParseRenderTiles(ehf, cellsX);
        if (tiles.Count == 0)
        {
            error = "no render tiles in the .ehf";
            return false;
        }

        List<EhfTile> pages = Engine_EhfAtlas.FindTiles(ehf);
        if (pages.Count < tiles.Count)
        {
            error = $"{pages.Count} texture pages for {tiles.Count} render tiles";
            return false;
        }

        var decoded = new (byte[] Rgba, int W, int H)[tiles.Count];
        var ratiosX = new List<float>();
        var ratiosY = new List<float>();
        int usable = 0;

        for (int i = 0; i < tiles.Count; i++)
        {
            if (!Engine_EhfAtlas.TryDecodeAt(ehf, pages[i].Offset, out EhfAtlasImage image, out _))
            {
                continue;
            }

            byte[] rgba = Engine_EhfAtlas.Bc1ToRgba(image.Blocks, image.Width, image.Height);
            decoded[i] = (rgba, image.Width, image.Height);
            usable++;

            if (tiles[i].CellWidth > 0 && tiles[i].CellHeight > 0)
            {
                ratiosX.Add((float)image.Width / tiles[i].CellWidth);
                ratiosY.Add((float)image.Height / tiles[i].CellHeight);
            }
        }

        // A quarter of the tiles, and at least one: a 4 m vista is a single page.
        if (usable < Math.Max(1, tiles.Count / 4))
        {
            error = $"only {usable} of {tiles.Count} tile pages decoded";
            return false;
        }

        float medianX = Median(ratiosX);
        float medianY = Median(ratiosY);
        if (medianX < 0.5f || medianY < 0.5f || medianX > 8f || medianY > 8f)
        {
            error = $"tile pages look strip-like (median scale {medianX:0.##}x{medianY:0.##})";
            return false;
        }

        int scaleX = Math.Clamp((int)Math.Round(medianX), 1, 8);
        int scaleY = Math.Clamp((int)Math.Round(medianY), 1, 8);

        long width = (long)cellsX * scaleX;
        long height = (long)cellsY * scaleY;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
        {
            error = $"composite would be {width}x{height}";
            return false;
        }

        var canvas = new byte[width * height * 4];
        for (long i = 3; i < canvas.Length; i += 4)
        {
            canvas[i] = 0xFF;
        }

        for (int i = 0; i < tiles.Count; i++)
        {
            if (decoded[i].Rgba is null)
            {
                continue;
            }

            EhfRenderTile tile = tiles[i];
            Blit(
                decoded[i].Rgba, decoded[i].W, decoded[i].H,
                canvas, (int)width, (int)height,
                tile.CellX * scaleX, tile.CellY * scaleY,
                tile.CellWidth * scaleX, tile.CellHeight * scaleY);
        }

        albedo = new EhfAlbedo
        {
            Width = (int)width,
            Height = (int)height,
            Rgba = canvas,
            TilesUsed = usable,
            TilesTotal = tiles.Count,
        };

        return true;
    }

    public static List<EhfRenderTile> ParseRenderTiles(byte[] ehf, int terrainCellsX)
    {
        var tiles = new List<EhfRenderTile>();
        if (ehf.Length < 63)
        {
            return tiles;
        }

        long bodyOffset = ReadU32(ehf, 55);
        long bodyEnd = bodyOffset + ReadU32(ehf, 59);
        if (bodyEnd > ehf.Length)
        {
            return tiles;
        }

        int cursor = (int)bodyOffset;
        if (!TrySkipTexBlob(ehf, ref cursor, bodyEnd) ||
            !TrySkipTexBlob(ehf, ref cursor, bodyEnd) ||
            cursor + 8 > bodyEnd)
        {
            return tiles;
        }

        cursor += 4;
        uint count = ReadU32(ehf, cursor);
        cursor += 4;
        if (count == 0 || count > 4096)
        {
            return tiles;
        }

        for (uint i = 0; i < count; i++)
        {
            if (cursor + 16 > bodyEnd)
            {
                tiles.Clear();
                return tiles;
            }

            uint cellW = ReadU32(ehf, cursor);
            uint cellH = ReadU32(ehf, cursor + 4);
            uint subW = ReadU32(ehf, cursor + 8);
            uint subH = ReadU32(ehf, cursor + 12);
            cursor += 16;

            if (cellW == 0 || cellH == 0 || subW == 0 || subH == 0 ||
                subW > 1024 || subH > 1024)
            {
                tiles.Clear();
                return tiles;
            }

            long gridBytes = ((long)subW * subH * CellStride) + 24;
            if (cursor + gridBytes > bodyEnd)
            {
                tiles.Clear();
                return tiles;
            }

            long bounds = cursor + gridBytes - 24;
            float minX = ReadF32(ehf, (int)bounds);
            float minY = ReadF32(ehf, (int)bounds + 4);
            float maxX = ReadF32(ehf, (int)bounds + 12);
            float maxY = ReadF32(ehf, (int)bounds + 16);

            bool boundsOk = float.IsFinite(minX) && float.IsFinite(minY) &&
                            float.IsFinite(maxX) && float.IsFinite(maxY) &&
                            maxX > minX && maxY > minY &&
                            Math.Abs(minX) < 100000f && Math.Abs(minY) < 100000f;

            tiles.Add(new EhfRenderTile
            {
                CellWidth = (int)cellW,
                CellHeight = (int)cellH,
                SubWidth = (int)subW,
                SubHeight = (int)subH,
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY,
                BoundsUsable = boundsOk,
            });

            cursor = (int)(cursor + gridBytes);
        }

        if (!PlaceFromBounds(tiles))
        {
            PlaceByPacking(tiles, terrainCellsX);
        }

        return tiles;
    }

    private static bool PlaceFromBounds(List<EhfRenderTile> tiles)
    {
        var scalesX = new List<float>();
        var scalesY = new List<float>();
        float originX = float.PositiveInfinity;
        float originY = float.PositiveInfinity;

        foreach (EhfRenderTile tile in tiles)
        {
            if (!tile.BoundsUsable)
            {
                return false;
            }

            scalesX.Add(tile.CellWidth / (tile.MaxX - tile.MinX));
            scalesY.Add(tile.CellHeight / (tile.MaxY - tile.MinY));
            originX = MathF.Min(originX, tile.MinX);
            originY = MathF.Min(originY, tile.MinY);
        }

        if (scalesX.Count == 0)
        {
            return false;
        }

        float scaleX = Median(scalesX);
        float scaleY = Median(scalesY);
        if (!float.IsFinite(scaleX) || !float.IsFinite(scaleY) ||
            scaleX <= 0.01f || scaleY <= 0.01f)
        {
            return false;
        }

        foreach (EhfRenderTile tile in tiles)
        {
            long x = (long)Math.Round((tile.MinX - originX) * scaleX);
            long y = (long)Math.Round((tile.MinY - originY) * scaleY);
            if (x < 0 || y < 0 || x > 65535 || y > 65535)
            {
                return false;
            }

            tile.CellX = (int)x;
            tile.CellY = (int)y;
        }

        return true;
    }

    private static void PlaceByPacking(List<EhfRenderTile> tiles, int terrainCellsX)
    {
        int x = 0;
        int y = 0;
        int rowHeight = 0;

        foreach (EhfRenderTile tile in tiles)
        {
            if (terrainCellsX > 0 && x > 0 && x + tile.CellWidth > terrainCellsX)
            {
                y += rowHeight;
                x = 0;
                rowHeight = 0;
            }

            tile.CellX = x;
            tile.CellY = y;
            x += tile.CellWidth;
            rowHeight = Math.Max(rowHeight, tile.CellHeight);

            if (terrainCellsX > 0 && x >= terrainCellsX)
            {
                y += rowHeight;
                x = 0;
                rowHeight = 0;
            }
        }
    }

    private static void Blit(
        byte[] source, int sourceWidth, int sourceHeight,
        byte[] destination, int destinationWidth, int destinationHeight,
        int atX, int atY, int copyWidth, int copyHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || copyWidth <= 0 || copyHeight <= 0)
        {
            return;
        }

        int clippedWidth = Math.Min(copyWidth, destinationWidth - atX);
        int clippedHeight = Math.Min(copyHeight, destinationHeight - atY);
        if (atX < 0 || atY < 0 || clippedWidth <= 0 || clippedHeight <= 0)
        {
            return;
        }

        for (int y = 0; y < clippedHeight; y++)
        {
            float sy = (((y + 0.5f) * sourceHeight) / copyHeight) - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(sy), 0, sourceHeight - 1);
            int y1 = Math.Min(y0 + 1, sourceHeight - 1);
            float fy = Math.Clamp(sy - y0, 0f, 1f);

            for (int x = 0; x < clippedWidth; x++)
            {
                float sx = (((x + 0.5f) * sourceWidth) / copyWidth) - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(sx), 0, sourceWidth - 1);
                int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                float fx = Math.Clamp(sx - x0, 0f, 1f);

                long p00 = (((long)y0 * sourceWidth) + x0) * 4;
                long p10 = (((long)y0 * sourceWidth) + x1) * 4;
                long p01 = (((long)y1 * sourceWidth) + x0) * 4;
                long p11 = (((long)y1 * sourceWidth) + x1) * 4;
                long at = ((((long)atY + y) * destinationWidth) + atX + x) * 4;

                float w00 = (1f - fx) * (1f - fy);
                float w10 = fx * (1f - fy);
                float w01 = (1f - fx) * fy;
                float w11 = fx * fy;

                for (int c = 0; c < 4; c++)
                {
                    destination[at + c] = (byte)Math.Clamp(
                        (w00 * source[p00 + c]) + (w10 * source[p10 + c]) +
                        (w01 * source[p01 + c]) + (w11 * source[p11 + c]),
                        0f, 255f);
                }
            }
        }
    }

    private static float Median(List<float> values)
    {
        if (values.Count == 0)
        {
            return 1f;
        }

        values.Sort();
        return values[values.Count / 2];
    }

    private static bool TrySkipTexBlob(byte[] data, ref int cursor, long limit)
    {
        long start = cursor;
        if (start + 0x60 > limit || ReadU32(data, (int)start) != 0xFFFFFFFEu)
        {
            return false;
        }

        uint pixelFormat = ReadU32(data, (int)start + 0x18);
        uint metaSize = ReadU32(data, (int)start + 0x20);
        if (metaSize < 0x54 || metaSize > 0x200)
        {
            return false;
        }

        long table = start + metaSize;
        if (table + 8 > limit)
        {
            return false;
        }

        long next;
        if (pixelFormat == 98u)
        {
            uint w = ReadU32(data, (int)start + 0x10);
            uint h = ReadU32(data, (int)start + 0x14);
            next = start + metaSize + ((long)w * h * 2);
        }
        else
        {
            next = table + 8 + ReadU32(data, (int)table + 4);
        }

        if (next > limit || next <= start)
        {
            return false;
        }

        cursor = (int)next;
        return true;
    }

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];

    private static float ReadF32(byte[] data, int at) =>
        BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(data, at)));
}
