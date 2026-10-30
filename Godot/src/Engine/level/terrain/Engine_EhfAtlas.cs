using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Refii.Core;
using Refii.Textures;

namespace Refii.Level;

public sealed class EhfAtlasImage
{
    public int Width { get; init; }

    public int Height { get; init; }

    public TexSurfaceFormat Format { get; init; }

    public byte[] Blocks { get; init; } = Array.Empty<byte>();
}

public sealed class EhfTile
{
    public int Offset { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}

public static class Engine_EhfAtlas
{
    private const uint TexMagic = 0xFFFFFFFEu;

    private const uint PfLightmap = 24u;
    private const uint PfBc1 = 35u;
    private const uint PfBc3 = 39u;
    private const uint PfBc5 = 40u;

    public static bool TryDecodeAt(byte[] data, int offset, out EhfAtlasImage image, out string error) =>
        TryDecodeAt(data, offset, Engine_Title.Active, out image, out error);

    /// <param name="offset">The page's .tex header, as <see cref="EhfBackgroundPatch.Pages"/> states it.</param>
    public static bool TryDecodeAt(
        byte[] data, int offset, Engine_Title title, out EhfAtlasImage image, out string error)
    {
        image = null!;
        error = string.Empty;

        uint width;
        uint height;
        uint pixelFormat;
        long table;
        if (title == Engine_Title.Fable3)
        {
            if (!Engine_EhfBody.TryReadFable3Tex(data, offset, data.Length, out EhfTexBlob blob, out error))
            {
                return false;
            }

            width = (uint)blob.Width;
            height = (uint)blob.Height;
            pixelFormat = (uint)blob.PixelFormat;
            table = blob.Data;
        }
        else
        {
            if (offset < 0 || offset + 0x60 > data.Length)
            {
                error = "blob header past end of file";
                return false;
            }

            if (ReadU32(data, offset) != TexMagic)
            {
                error = "not a .tex blob";
                return false;
            }

            width = ReadU32(data, offset + 0x10);
            height = ReadU32(data, offset + 0x14);
            pixelFormat = ReadU32(data, offset + 0x18);
            uint mipTable = ReadU32(data, offset + 0x20);
            table = offset + mipTable;
        }

        if (width == 0 || height == 0 || width > 16384 || height > 16384)
        {
            error = $"bad dimensions {width}x{height}";
            return false;
        }

        if (pixelFormat is not (PfBc1 or PfBc3 or PfBc5))
        {
            error = $"unsupported pixel format {pixelFormat}";
            return false;
        }

        if (table + 8 > data.Length)
        {
            error = "mip table past end of file";
            return false;
        }

        uint rawSize = ReadU32(data, (int)table);
        uint compressedSize = ReadU32(data, (int)table + 4);

        int blockBytes = pixelFormat == PfBc1 ? 8 : 16;
        int blocksWide = ((int)width + 3) / 4;
        int blocksHigh = ((int)height + 3) / 4;
        long logical = (long)blocksWide * blocksHigh * blockBytes;
        long padded = (long)((blocksWide + 31) & ~31) * ((blocksHigh + 31) & ~31) * blockBytes;

        if (rawSize == 0 || rawSize < logical || rawSize > padded)
        {
            error = $"raw size {rawSize} outside [{logical}..{padded}] for {width}x{height}";
            return false;
        }

        long zlibAt = table + 8;
        if (zlibAt + 2 > data.Length || data[zlibAt] != 0x78)
        {
            error = "no zlib stream at the mip offset";
            return false;
        }

        long available = data.Length - zlibAt;
        int comp = (int)Math.Min(compressedSize, available);

        byte[] tiled;
        try
        {
            using var source = new MemoryStream(data, (int)zlibAt, comp);
            using var inflate = new ZLibStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream((int)rawSize);
            inflate.CopyTo(output);
            tiled = output.ToArray();
        }
        catch (Exception exception)
        {
            error = $"inflate failed: {exception.Message}";
            return false;
        }

        byte[] blocks = Untile(tiled, (int)width, (int)height, 4, blockBytes);

        for (int i = 0; i + 2 <= blocks.Length; i += 2)
        {
            (blocks[i], blocks[i + 1]) = (blocks[i + 1], blocks[i]);
        }

        image = new EhfAtlasImage
        {
            Width = (int)width,
            Height = (int)height,
            Format = pixelFormat switch
            {
                PfBc1 => TexSurfaceFormat.Bc1,
                PfBc3 => TexSurfaceFormat.Bc3,
                _ => TexSurfaceFormat.Bc5,
            },
            Blocks = blocks,
        };

        return true;
    }

    public static List<EhfTile> FindTiles(byte[] data) => FindTiles(data, Engine_Title.Active);

    /// <summary>
    /// The finest page of each background patch, in patch order. Fable 3 reads them from the page
    /// tables the body states (every set is finest first, 2 to 5 pages, each half the last);
    /// Fable 2 finds them by scanning past the body for three-page sets.
    /// </summary>
    public static List<EhfTile> FindTiles(byte[] data, Engine_Title title)
    {
        if (title == Engine_Title.Fable3)
        {
            return StatedTiles(data);
        }

        var all = new List<EhfTile>();
        var primaries = new List<EhfTile>();

        if (data.Length < 63)
        {
            return primaries;
        }

        long bodyEnd = (long)ReadU32(data, 55) + ReadU32(data, 59);
        if (bodyEnd > data.Length)
        {
            return primaries;
        }

        for (long i = bodyEnd; i + 0x60 < data.Length; i++)
        {
            if (data[i] != 0xFF || data[i + 1] != 0xFF ||
                data[i + 2] != 0xFF || data[i + 3] != 0xFE)
            {
                continue;
            }

            uint w = ReadU32(data, (int)i + 0x10);
            uint h = ReadU32(data, (int)i + 0x14);
            uint pf = ReadU32(data, (int)i + 0x18);
            uint mt = ReadU32(data, (int)i + 0x20);

            if (pf != PfBc1 || w == 0 || h == 0 || w > 8192 || h > 8192 ||
                mt < 0x54 || mt > 0x200)
            {
                continue;
            }

            long table = i + mt;
            if (table + 8 > data.Length)
            {
                continue;
            }

            uint comp = ReadU32(data, (int)table + 4);
            long zlibAt = table + 8;
            if (comp < 2 || zlibAt + comp > data.Length || data[zlibAt] != 0x78)
            {
                continue;
            }

            all.Add(new EhfTile { Offset = (int)i, Width = (int)w, Height = (int)h });
        }

        for (int i = 0; i < all.Count;)
        {
            if (i + 2 < all.Count &&
                all[i + 1].Width * 2 == all[i].Width &&
                all[i + 2].Width * 4 == all[i].Width)
            {
                primaries.Add(all[i]);
                i += 3;
            }
            else
            {
                i++;
            }
        }

        return primaries;
    }

    /// <remarks>A patch with no page keeps its slot with offset −1, so tile i stays patch i.</remarks>
    private static List<EhfTile> StatedTiles(byte[] data)
    {
        var tiles = new List<EhfTile>();
        if (!Engine_EhfBody.TryParse(data, Engine_Title.Fable3, out EhfParsedBody body, out _))
        {
            return tiles;
        }

        foreach (EhfBackgroundPatch patch in body.BackgroundPatches)
        {
            if (patch.Pages.Count == 0 ||
                !Engine_EhfBody.TryReadFable3Tex(data, patch.Pages[0].Offset, data.Length, out EhfTexBlob page, out _))
            {
                tiles.Add(new EhfTile { Offset = -1 });
                continue;
            }

            tiles.Add(new EhfTile { Offset = page.Header, Width = page.Width, Height = page.Height });
        }

        return tiles;
    }

    internal static uint TiledX(uint blockOffset, uint widthBlocks, uint texelPitch)
    {
        uint alignedWidth = (widthBlocks + 31u) & ~31u;
        uint logBpp = (texelPitch >> 2) + ((texelPitch >> 1) >> (int)(texelPitch >> 2));
        uint offByte = blockOffset << (int)logBpp;
        uint offTile = ((offByte & ~0xFFFu) >> 3) + ((offByte & 0x700u) >> 2) + (offByte & 0x3Fu);
        uint offMacro = offTile >> (int)(7 + logBpp);

        uint macroX = (offMacro % (alignedWidth >> 5)) << 2;
        uint tile = (((offTile >> (int)(5 + logBpp)) & 2) + (offByte >> 6)) & 3;
        uint macro = (macroX + tile) << 3;
        uint micro = ((((offTile >> 1) & ~0xFu) + (offTile & 0xFu)) &
                      ((texelPitch << 3) - 1)) >> (int)logBpp;
        return macro + micro;
    }

    internal static uint TiledY(uint blockOffset, uint widthBlocks, uint texelPitch)
    {
        uint alignedWidth = (widthBlocks + 31u) & ~31u;
        uint logBpp = (texelPitch >> 2) + ((texelPitch >> 1) >> (int)(texelPitch >> 2));
        uint offByte = blockOffset << (int)logBpp;
        uint offTile = ((offByte & ~0xFFFu) >> 3) + ((offByte & 0x700u) >> 2) + (offByte & 0x3Fu);
        uint offMacro = offTile >> (int)(7 + logBpp);

        uint macroY = (offMacro / (alignedWidth >> 5)) << 2;
        uint tile = ((offTile >> (int)(6 + logBpp)) & 1) + ((offByte & 0x800u) >> 10);
        uint macro = (macroY + tile) << 3;
        uint micro = (((offTile & (((texelPitch << 6) - 1) & ~0x1Fu)) +
                       ((offTile & 0xFu) << 1)) >> (int)(3 + logBpp)) & ~1u;
        return macro + micro + ((offTile & 0x10u) >> 4);
    }

    private static byte[] Untile(
        byte[] tiled, int widthPixels, int heightPixels, int blockPixels, int texelPitch)
    {
        uint widthBlocks = (uint)((widthPixels / blockPixels) +
                                  (widthPixels % blockPixels != 0 ? 1 : 0));
        uint heightBlocks = (uint)((heightPixels / blockPixels) +
                                   (heightPixels % blockPixels != 0 ? 1 : 0));

        uint offsetX = 0;
        uint offsetY = 0;
        if (widthPixels <= 16 && heightPixels <= 16)
        {
            offsetX = (uint)(16 / blockPixels);
            offsetY = (uint)(16 / blockPixels);
        }
        else if (heightPixels <= 16)
        {
            offsetY = (uint)(16 / blockPixels);
        }
        else if (widthPixels <= 16)
        {
            offsetX = (uint)(16 / blockPixels);
        }

        uint paddedWidth = (offsetX + widthBlocks + 31u) & ~31u;
        uint paddedHeight = (offsetY + heightBlocks + 31u) & ~31u;
        uint total = paddedWidth * paddedHeight;

        var linear = new byte[(long)widthBlocks * heightBlocks * texelPitch];

        for (uint off = 0; off < total; off++)
        {
            uint x = TiledX(off, paddedWidth, (uint)texelPitch);
            uint y = TiledY(off, paddedWidth, (uint)texelPitch);
            if (x < offsetX || y < offsetY)
            {
                continue;
            }

            uint lx = x - offsetX;
            uint ly = y - offsetY;
            if (lx >= widthBlocks || ly >= heightBlocks)
            {
                continue;
            }

            long source = (long)off * texelPitch;
            if (source + texelPitch > tiled.Length)
            {
                continue;
            }

            long destination = (((long)ly * widthBlocks) + lx) * texelPitch;
            Array.Copy(tiled, source, linear, destination, texelPitch);
        }

        return linear;
    }

    public static byte[] Bc1ToRgba(byte[] blocks, int width, int height)
    {
        var rgba = new byte[(long)width * height * 4];
        int blocksWide = (width + 3) / 4;
        int blocksHigh = (height + 3) / 4;
        var colour = new byte[4 * 4];

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                int at = ((by * blocksWide) + bx) * 8;
                if (at + 8 > blocks.Length)
                {
                    continue;
                }

                ushort c0 = (ushort)(blocks[at] | (blocks[at + 1] << 8));
                ushort c1 = (ushort)(blocks[at + 2] | (blocks[at + 3] << 8));

                Expand565(c0, colour, 0);
                Expand565(c1, colour, 4);

                if (c0 > c1)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        colour[8 + k] = (byte)(((2 * colour[k]) + colour[4 + k]) / 3);
                        colour[12 + k] = (byte)((colour[k] + (2 * colour[4 + k])) / 3);
                    }

                    colour[11] = 255;
                    colour[15] = 255;
                }
                else
                {
                    for (int k = 0; k < 3; k++)
                    {
                        colour[8 + k] = (byte)((colour[k] + colour[4 + k]) / 2);
                        colour[12 + k] = 0;
                    }

                    colour[11] = 255;
                    colour[15] = 0;
                }

                uint bits = (uint)(blocks[at + 4] | (blocks[at + 5] << 8) |
                                   (blocks[at + 6] << 16) | (blocks[at + 7] << 24));

                for (int py = 0; py < 4; py++)
                {
                    int y = (by * 4) + py;
                    if (y >= height)
                    {
                        break;
                    }

                    for (int px = 0; px < 4; px++)
                    {
                        int x = (bx * 4) + px;
                        if (x >= width)
                        {
                            break;
                        }

                        int index = (int)((bits >> (2 * ((py * 4) + px))) & 3) * 4;
                        long destination = (((long)y * width) + x) * 4;
                        rgba[destination + 0] = colour[index + 0];
                        rgba[destination + 1] = colour[index + 1];
                        rgba[destination + 2] = colour[index + 2];
                        rgba[destination + 3] = colour[index + 3];
                    }
                }
            }
        }

        return rgba;
    }

    private static void Expand565(ushort c, byte[] destination, int at)
    {
        int r = (c >> 11) & 0x1F;
        int g = (c >> 5) & 0x3F;
        int b = c & 0x1F;
        destination[at + 0] = (byte)((r << 3) | (r >> 2));
        destination[at + 1] = (byte)((g << 2) | (g >> 4));
        destination[at + 2] = (byte)((b << 3) | (b >> 2));
        destination[at + 3] = 255;
    }

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];
}
