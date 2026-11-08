using System;
using System.Linq;
using System.Buffers.Binary;

using Refii.Core;

using Refii.Ui;

namespace Refii.Textures;

public readonly struct TexPlane
{
    public readonly uint CompFlag;

    public readonly uint DataOffset;

    public readonly uint Size;

    public TexPlane(uint compFlag, uint dataOffset, uint size)
    {
        CompFlag = compFlag;
        DataOffset = dataOffset;
        Size = size;
    }
}

public static class Engine_TexPlanes
{
    public const int RecordSize = 48;
    public const int PlanesPerRecord = 4;

    public const uint CompHuffman = 1;
    public const uint CompHuffmanNoSwap = 11;
    public const uint CompVariantA = 2;
    public const uint CompVariantB = 3;
    public const uint CompConstantFill = 6;
    public const uint CompRaw = 7;

    public static bool TryReadRecord(byte[] d, int recordOffset, out TexPlane[] planes)
    {
        planes = Array.Empty<TexPlane>();
        if (recordOffset < 0 || recordOffset + RecordSize > d.Length)
        {
            return false;
        }

        planes = new TexPlane[PlanesPerRecord];
        ReadPlanes(d, recordOffset, planes);
        return true;
    }

    private static void ReadPlanes(byte[] d, int recordOffset, Span<TexPlane> planes)
    {
        for (int p = 0; p < PlanesPerRecord; p++)
        {
            int b = recordOffset + p * 12;
            planes[p] = new TexPlane(
                BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(b, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(b + 4, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(b + 8, 4)));
        }
    }

    public static string DescribeRecord(byte[] d, int recordOffset)
    {
        if (!TryReadRecord(d, recordOffset, out TexPlane[] planes))
        {
            return "unreadable";
        }

        return string.Join(" ", planes.Select(p => $"comp={p.CompFlag},off={p.DataOffset},size={p.Size}"));
    }

    public static bool TryDecodeLevel(byte[] d, int recordOffset, TexHeader header,
        out byte[] surface, out string error) =>
        TryDecodeLevel(d, recordOffset, header, header.Width, header.Height, out surface, out error);

    public static bool TryDecodeLevel(byte[] d, int recordOffset, TexHeader header,
        int width, int height, out byte[] surface, out string error) =>
        TryDecodeLevel(d, recordOffset, header, width, height, null, 0, out surface, out error);

    /// <summary>Decodes a level into <paramref name="target"/> at <paramref name="targetOffset"/>, which has room for it.</summary>
    internal static bool TryDecodeLevelInto(byte[] d, int recordOffset, TexHeader header,
        int width, int height, byte[] target, int targetOffset, out string error) =>
        TryDecodeLevel(d, recordOffset, header, width, height, target, targetOffset, out _, out error);

    /// <summary>A null <paramref name="target"/> asks for a new array in <paramref name="surface"/>.</summary>
    private static bool TryDecodeLevel(byte[] d, int recordOffset, TexHeader header,
        int width, int height, byte[]? target, int targetOffset, out byte[] surface, out string error)
    {
        surface = Array.Empty<byte>();
        error = string.Empty;

        if (recordOffset < 0 || recordOffset + RecordSize > d.Length)
        {
            error = $"record at {recordOffset} is outside the file";
            return false;
        }

        Span<TexPlane> planes = stackalloc TexPlane[PlanesPerRecord];
        ReadPlanes(d, recordOffset, planes);

        int stride = header.BlockStride;
        int planeCount = stride == 16 ? 2 : 1;
        int blocks = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4);

        if (planes[0].CompFlag == CompRaw)
        {
            int rawOffset = recordOffset + (int)planes[0].DataOffset;
            int needed = blocks * stride;
            if (rawOffset < 0 || rawOffset + needed > d.Length)
            {
                error = $"raw record: need {needed} bytes at {rawOffset}, file is {d.Length}";
                return false;
            }

            int blocksWide = Math.Max(1, (width + 3) / 4);
            int blocksHigh = Math.Max(1, (height + 3) / 4);

            // 32 IS NOT A TUNING NUMBER, it is the macro-tile width, and the file sizes force the
            // branch rather than a preference choosing it.
            //
            // Untile addresses through `alignedWidth = (widthBlocks + 31) & ~31`. At 32 blocks or
            // more that alignment is a no-op for these power-of-two textures, so untiling reads
            // exactly the bytes that are there. Below 32 it rounds UP to 32, and a tiled payload
            // would then have to be alignedWidth * alignedHeight * stride bytes — but the shipped
            // records store the unaligned count. A 32x32 BC1 cube face is 8x8 blocks and its plane
            // says size=512 = 64 * 8; tile-padded it would be 8,192. The bytes for a tiled small
            // texture simply are not in the file, so it cannot be one.
            //
            // Untiling anyway would not fail loudly either: the loop skips blocks that fall
            // outside the buffer, so it would quietly leave most of the image zeroed.
            if (blocksWide < 32)
            {
                byte[] payload = target ?? new byte[needed];
                int at = target is null ? 0 : targetOffset;
                Buffer.BlockCopy(d, rawOffset, payload, at, needed);
                Engine_Xbox360Tiling.SwapWords16(payload.AsSpan(at, needed));
                surface = payload;
                return true;
            }

            // Only staging for the untile, which writes its own output: pooled.
            byte[] staged = System.Buffers.ArrayPool<byte>.Shared.Rent(needed);
            try
            {
                Span<byte> tiled = staged.AsSpan(0, needed);
                d.AsSpan(rawOffset, needed).CopyTo(tiled);
                Engine_Xbox360Tiling.SwapWords16(tiled);
                surface = Engine_Xbox360Tiling.Untile(tiled, blocksWide, blocksHigh, stride);
                if (target is not null)
                {
                    Buffer.BlockCopy(surface, 0, target, targetOffset, needed);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(staged);
            }

            return true;
        }

        byte[] outputBytes = target ?? new byte[blocks * stride];
        Span<byte> output = target is null ? outputBytes : outputBytes.AsSpan(targetOffset, blocks * stride);
        if (target is not null)
        {
            // A plane that is switched off leaves its half of each block zero.
            output.Clear();
        }

        bool wroteAnything = false;
        // The colour plane finds out which game wrote the record; the alpha plane beside it is
        // the same game's, and its layout cannot be told apart as safely on its own.
        Engine_LhTexCodec.TexScheme scheme = Engine_LhTexCodec.TexScheme.Auto;
        for (int p = 0; p < planeCount; p++)
        {
            TexPlane plane = planes[p];
            if (plane.CompFlag == 0)
            {
                continue;
            }

            int bias = PlaneBias(header.CodecId, p, stride);

            int bodyOffset = recordOffset + (int)plane.DataOffset;
            int bodySize = (int)Math.Min(plane.Size, (uint)Math.Max(0, d.Length - bodyOffset));

            switch (plane.CompFlag)
            {
                case CompHuffman:
                case CompHuffmanNoSwap:
                {
                    if (bodyOffset < 0 || bodySize <= 0)
                    {
                        error = $"plane {p}: body outside the file";
                        return false;
                    }

                    if (!Engine_LhTexCodec.TryDecodeCompressedMipPooled(d, bodyOffset, bodySize,
                            plane.CompFlag == CompHuffmanNoSwap, scheme,
                            out int w, out int h, out byte[] bc1, out _, out _, out scheme, out string err))
                    {
                        error = $"plane {p}: {err}";
                        return false;
                    }

                    try
                    {
                        if (w != width || h != height)
                        {
                            error = $"plane {p} decoded {w}x{h}, level is {width}x{height}";
                            return false;
                        }

                        Scatter(bc1.AsSpan(0, (w / 4) * (h / 4) * 8), output, blocks, stride, bias);
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(bc1);
                    }

                    wroteAnything = true;
                    break;
                }

                case CompVariantA:
                case CompVariantB:
                {
                    int mode = (int)plane.CompFlag - 1;
                    if (!Engine_LhTexCodec.TryDecodeVariantPooled(d, bodyOffset, bodySize, mode,
                            width, height, scheme, out byte[] vb, out string verr))
                    {
                        error = $"plane {p}: {verr}";
                        return false;
                    }

                    Scatter(vb.AsSpan(0, (width / 4) * (height / 4) * 8), output, blocks, stride, bias);
                    System.Buffers.ArrayPool<byte>.Shared.Return(vb);
                    wroteAnything = true;
                    break;
                }

                case CompConstantFill:
                {
                    FillConstant(output, blocks, stride, bias,
                        plane.DataOffset, ConstantKindFor(header.CodecId, p, stride));
                    wroteAnything = true;
                    break;
                }

                default:
                    error = $"plane {p}: unsupported compFlag {plane.CompFlag}";
                    return false;
            }
        }

        if (!wroteAnything)
        {
            error = "record has no live planes";
            return false;
        }

        surface = outputBytes;
        return true;
    }

    private static void Scatter(ReadOnlySpan<byte> src, Span<byte> dst, int blocks, int stride, int bias)
    {
        int count = Math.Min(blocks, src.Length / 8);
        if (stride == 8 && bias == 0)
        {
            int bytes = Math.Min(count * 8, dst.Length);
            src[..bytes].CopyTo(dst);
            return;
        }

        System.ReadOnlySpan<ulong> source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(src);
        System.Span<ulong> target = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(dst);
        int step = stride / 8;
        int offset = bias / 8;
        for (int i = 0; i < count; i++)
        {
            int t = (i * step) + offset;
            if (t >= target.Length)
            {
                return;
            }

            target[t] = source[i];
        }
    }

    private enum ConstantKind
    {
        Colour,

        AlphaBc4,

        AlphaExplicit,
    }

    private static int PlaneBias(int codecId, int planeIndex, int stride)
    {
        if (stride != 16)
        {
            return 0;
        }

        return codecId == 3 ? planeIndex * 8 : (planeIndex == 0 ? 8 : 0);
    }

    private static ConstantKind ConstantKindFor(int codecId, int planeIndex, int stride)
    {
        if (stride != 16 || (codecId != 3 && planeIndex == 0))
        {
            return ConstantKind.Colour;
        }

        return codecId == 1 ? ConstantKind.AlphaExplicit : ConstantKind.AlphaBc4;
    }

    private static void FillConstant(Span<byte> dst, int blocks, int stride, int bias,
        uint value, ConstantKind kind)
    {
        Span<byte> block = stackalloc byte[8];

        switch (kind)
        {
            case ConstantKind.Colour:
            {
                int r = (int)((value >> 16) & 0xFF) >> 3;
                int g = (int)((value >> 8) & 0xFF) >> 2;
                int b = (int)(value & 0xFF) >> 3;
                var c565 = (ushort)((r << 11) | (g << 5) | b);

                block[0] = (byte)(c565 & 0xFF);
                block[1] = (byte)(c565 >> 8);
                block[2] = block[0];
                block[3] = block[1];
                block[4] = block[5] = block[6] = block[7] = 0;
                break;
            }

            case ConstantKind.AlphaBc4:
            {
                var a = (byte)(value & 0xFF);
                block[0] = a;
                block[1] = a;
                block[2] = block[3] = block[4] = block[5] = block[6] = block[7] = 0;
                break;
            }

            case ConstantKind.AlphaExplicit:
            {
                var nibble = (byte)((value & 0xFF) >> 4);
                var packed = (byte)((nibble << 4) | nibble);
                for (int i = 0; i < 8; i++)
                {
                    block[i] = packed;
                }
                break;
            }
        }

        for (int i = 0; i < blocks; i++)
        {
            int t = i * stride + bias;
            if (t + 8 > dst.Length)
            {
                return;
            }

            block.CopyTo(dst.Slice(t, 8));
        }
    }
}
