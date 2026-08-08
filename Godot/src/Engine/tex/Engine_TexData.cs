using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Refii.Textures;

public enum TexSurfaceFormat
{
    Unsupported = 0,
    Bc1,
    Bc3,
    Bc4,
}

public sealed class TexSurface
{
    public int Width { get; init; }

    public int Height { get; init; }

    public TexSurfaceFormat Format { get; init; }

    public byte[] Data { get; init; } = Array.Empty<byte>();

    public int MipLevels { get; init; } = 1;

    public bool HasMipmaps => MipLevels > 1;
}

public sealed class TexMip
{
    public uint CompFlag { get; init; }

    public int BodyOffset { get; init; }

    public int BodySize { get; init; }

    public bool HasSize { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}

public static class Engine_TexData
{
    private const int RecordSize = 48;
    private const int CompressedHeaderBytes = 444;

    public static bool TryParseChain(byte[] d, out List<TexMip> mips, out string error)
    {
        mips = new List<TexMip>();
        error = string.Empty;

        if (d.Length < RecordSize)
        {
            error = "too small for a mip record";
            return false;
        }

        if (!(Be(d, 0) <= 24 && Be(d, 4) == RecordSize))
        {
            error = "not a bare-chain .tex";
            return false;
        }

        int off = 0;
        while (off + RecordSize <= d.Length)
        {
            uint compFlag = Be(d, off);
            uint dataOffset = Be(d, off + 4);
            uint dataSize = Be(d, off + 8);
            uint u3 = Be(d, off + 12);
            uint u4 = Be(d, off + 16);
            uint u5 = Be(d, off + 20);

            if (dataOffset != RecordSize || compFlag > 24)
            {
                break;
            }

            if ((long)off + RecordSize + dataSize > (long)d.Length + 32)
            {
                break;
            }

            int bodyStart = off + RecordSize;
            if (compFlag == 7)
            {
                int available = Math.Max(0, d.Length - bodyStart);
                mips.Add(new TexMip
                {
                    CompFlag = compFlag,
                    BodyOffset = bodyStart,
                    BodySize = (int)Math.Min(dataSize, (uint)available),
                    HasSize = false,
                });
            }
            else
            {
                if (bodyStart + 4 > d.Length || dataSize < CompressedHeaderBytes)
                {
                    break;
                }

                mips.Add(new TexMip
                {
                    CompFlag = compFlag,
                    BodyOffset = bodyStart,
                    BodySize = (int)Math.Min(dataSize, (uint)(d.Length - bodyStart)),
                    HasSize = true,
                    Width = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(bodyStart)),
                    Height = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(bodyStart + 2)),
                });
            }

            long advance = RecordSize + dataSize;
            if (u3 <= 24 && u4 == RecordSize + dataSize && u5 > 0 &&
                off + (long)u4 + u5 <= (long)d.Length + 32)
            {
                advance = (long)u4 + u5;
            }

            if (advance <= 0)
            {
                break;
            }

            off += (int)advance;
        }

        if (mips.Count == 0)
        {
            error = "no mip records";
            return false;
        }

        return true;
    }

    private const int BlockBytes = 8;

    private static int ExpectedLevelCount(int width, int height)
    {
        int levels = 1;
        int w = width;
        int h = height;
        while (w > 1 || h > 1)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            ++levels;
        }

        return levels;
    }

    private static int LevelBytes(int width, int height) =>
        Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * BlockBytes;

    public static bool TryDecode(byte[] d, out TexSurface surface, out string error)
    {
        if (TryDecodePyramid(d, out surface, out error))
        {
            return true;
        }

        return TryDecodeSingle(d, out surface, out error);
    }

    private static bool TryDecodePyramid(byte[] d, out TexSurface surface, out string error)
    {
        surface = default!;
        error = string.Empty;

        if (!TryParseChain(d, out List<TexMip> mips, out error) || mips.Count < 1)
        {
            return false;
        }

        var levels = new List<byte[]>();
        TexSurfaceFormat format = TexSurfaceFormat.Unsupported;
        int width = 0;
        int height = 0;
        int nextWidth = 0;
        int nextHeight = 0;

        foreach (TexMip mip in mips)
        {
            byte[]? data = null;
            int levelWidth;
            int levelHeight;

            if (mip.CompFlag is 1 or 11)
            {
                if (!Engine_LhTexCodec.TryDecodeCompressedMip(
                        d, mip.BodyOffset, mip.BodySize, mip.CompFlag == 11,
                        out levelWidth, out levelHeight, out byte[] blocks, out _))
                {
                    break;
                }

                data = blocks;
                if (format == TexSurfaceFormat.Unsupported) format = TexSurfaceFormat.Bc1;
                else if (format != TexSurfaceFormat.Bc1) break;
            }
            else if (mip.CompFlag is 2 or 3 or 4 && mip.HasSize)
            {
                levelWidth = mip.Width;
                levelHeight = mip.Height;
                if (!Engine_LhTexCodec.TryDecodeVariant(
                        d, mip.BodyOffset, mip.BodySize, 2, levelWidth, levelHeight,
                        out byte[] blocks, out _))
                {
                    break;
                }

                data = blocks;
                if (format == TexSurfaceFormat.Unsupported) format = TexSurfaceFormat.Bc4;
                else if (format != TexSurfaceFormat.Bc4) break;
            }
            else if (mip.CompFlag == 7 && nextWidth > 0)
            {
                levelWidth = nextWidth;
                levelHeight = nextHeight;
                int want = LevelBytes(levelWidth, levelHeight);
                if (mip.BodySize < want)
                {
                    break;
                }

                data = Slice(d, mip.BodyOffset, want);
            }
            else
            {
                break;
            }

            if (data is null)
            {
                break;
            }

            if (levels.Count == 0)
            {
                width = levelWidth;
                height = levelHeight;
            }
            else if (levelWidth != nextWidth || levelHeight != nextHeight)
            {
                break;
            }

            int expected = LevelBytes(levelWidth, levelHeight);
            if (data.Length != expected)
            {
                if (data.Length < expected)
                {
                    break;
                }

                Array.Resize(ref data, expected);
            }

            levels.Add(data);
            nextWidth = Math.Max(1, levelWidth / 2);
            nextHeight = Math.Max(1, levelHeight / 2);

            if (levelWidth == 1 && levelHeight == 1)
            {
                break;
            }
        }

        if (levels.Count < 1 || format == TexSurfaceFormat.Unsupported)
        {
            return false;
        }

        int levelWidthNow = width;
        int levelHeightNow = height;
        for (int i = 1; i < levels.Count; ++i)
        {
            levelWidthNow = Math.Max(1, levelWidthNow / 2);
            levelHeightNow = Math.Max(1, levelHeightNow / 2);
        }

        int expectedLevels = ExpectedLevelCount(width, height);
        if (levels.Count < expectedLevels)
        {
            bool bc4 = format == TexSurfaceFormat.Bc4;
            int channels = bc4 ? 1 : 4;
            byte[] pixels = bc4
                ? Engine_TexMipGen.DecodeBc4(levels[^1], levelWidthNow, levelHeightNow)
                : Engine_TexMipGen.DecodeBc1(levels[^1], levelWidthNow, levelHeightNow);

            while (levels.Count < expectedLevels)
            {
                pixels = Engine_TexMipGen.Downscale(pixels, levelWidthNow, levelHeightNow, channels);
                levelWidthNow = Math.Max(1, levelWidthNow / 2);
                levelHeightNow = Math.Max(1, levelHeightNow / 2);
                levels.Add(bc4
                    ? Engine_TexMipGen.EncodeBc4(pixels, levelWidthNow, levelHeightNow)
                    : Engine_TexMipGen.EncodeBc1(pixels, levelWidthNow, levelHeightNow));
            }
        }

        int total = 0;
        foreach (byte[] level in levels) total += level.Length;
        var combined = new byte[total];
        int written = 0;
        foreach (byte[] level in levels)
        {
            Buffer.BlockCopy(level, 0, combined, written, level.Length);
            written += level.Length;
        }

        surface = new TexSurface
        {
            Width = width,
            Height = height,
            Format = format,
            Data = combined,
            MipLevels = levels.Count,
        };
        return true;
    }

    private static bool TryDecodeSingle(byte[] d, out TexSurface surface, out string error)
    {
        surface = default!;

        if (!TryParseChain(d, out List<TexMip> mips, out error))
        {
            return false;
        }

        int fallbackWidth = 0;
        int fallbackHeight = 0;
        foreach (TexMip mip in mips)
        {
            if (mip.HasSize)
            {
                fallbackWidth = mip.Width;
                fallbackHeight = mip.Height;
                break;
            }
        }

        foreach (TexMip mip in mips)
        {
            if (mip.CompFlag is 1 or 11)
            {
                if (Engine_LhTexCodec.TryDecodeCompressedMip(
                        d, mip.BodyOffset, mip.BodySize, mip.CompFlag == 11,
                        out int w, out int h, out byte[] colour, out string decodeError))
                {
                    surface = new TexSurface
                    {
                        Width = w,
                        Height = h,
                        Format = TexSurfaceFormat.Bc1,
                        Data = colour,
                    };
                    return true;
                }

                error = decodeError;
                continue;
            }

            if (mip.CompFlag is 2 or 3 or 4 && mip.HasSize)
            {
                if (Engine_LhTexCodec.TryDecodeVariant(
                        d, mip.BodyOffset, mip.BodySize, 2, mip.Width, mip.Height,
                        out byte[] single, out string variantError))
                {
                    surface = new TexSurface
                    {
                        Width = mip.Width,
                        Height = mip.Height,
                        Format = TexSurfaceFormat.Bc4,
                        Data = single,
                    };
                    return true;
                }

                error = variantError;
                continue;
            }

            if (mip.CompFlag == 7 && fallbackWidth > 0 && fallbackHeight > 0)
            {
                int blocksW = (fallbackWidth + 3) / 4;
                int blocksH = (fallbackHeight + 3) / 4;
                long bc1 = (long)blocksW * blocksH * 8;
                long bc3 = (long)blocksW * blocksH * 16;

                if (mip.BodySize >= bc1 && mip.BodySize < bc3)
                {
                    surface = new TexSurface
                    {
                        Width = fallbackWidth,
                        Height = fallbackHeight,
                        Format = TexSurfaceFormat.Bc1,
                        Data = Slice(d, mip.BodyOffset, (int)bc1),
                    };
                    return true;
                }

                if (mip.BodySize >= bc3)
                {
                    surface = new TexSurface
                    {
                        Width = fallbackWidth,
                        Height = fallbackHeight,
                        Format = TexSurfaceFormat.Bc3,
                        Data = Slice(d, mip.BodyOffset, (int)bc3),
                    };
                    return true;
                }
            }
        }

        if (error.Length == 0)
        {
            error = "no decodable mip (all records use an unported variant)";
        }

        return false;
    }

    private static byte[] Slice(byte[] source, int offset, int length)
    {
        var result = new byte[length];
        Buffer.BlockCopy(source, offset, result, 0, length);
        return result;
    }

    private static uint Be(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
}
