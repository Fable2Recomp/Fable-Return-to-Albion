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
        for (int p = 0; p < PlanesPerRecord; p++)
        {
            int b = recordOffset + p * 12;
            planes[p] = new TexPlane(
                BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(b, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(b + 4, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(b + 8, 4)));
        }

        return true;
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
        int width, int height, out byte[] surface, out string error)
    {
        surface = Array.Empty<byte>();
        error = string.Empty;

        if (!TryReadRecord(d, recordOffset, out TexPlane[] planes))
        {
            error = $"record at {recordOffset} is outside the file";
            return false;
        }

        int stride = header.BlockStride;
        int planeCount = stride == 16 ? 2 : 1;
        int blocks = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4);
        var output = new byte[blocks * stride];

        if (planes[0].CompFlag == CompRaw)
        {
            int rawOffset = recordOffset + (int)planes[0].DataOffset;
            int needed = blocks * stride;
            if (rawOffset < 0 || rawOffset + needed > d.Length)
            {
                error = $"raw record: need {needed} bytes at {rawOffset}, file is {d.Length}";
                return false;
            }

            var payload = new byte[needed];
            Buffer.BlockCopy(d, rawOffset, payload, 0, needed);
            Engine_Xbox360Tiling.SwapWords16(payload);

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
            surface = blocksWide >= 32
                ? Engine_Xbox360Tiling.Untile(payload, blocksWide, blocksHigh, stride)
                : payload;
            return true;
        }

        bool wroteAnything = false;

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

                    if (!Engine_LhTexCodec.TryDecodeCompressedMip(d, bodyOffset, bodySize,
                            plane.CompFlag == CompHuffmanNoSwap,
                            out int w, out int h, out byte[] bc1, out string err))
                    {
                        error = $"plane {p}: {err}";
                        return false;
                    }

                    if (w != width || h != height)
                    {
                        error = $"plane {p} decoded {w}x{h}, level is {width}x{height}";
                        return false;
                    }

                    Scatter(bc1, output, blocks, stride, bias);
                    wroteAnything = true;
                    break;
                }

                case CompVariantA:
                case CompVariantB:
                {
                    int mode = (int)plane.CompFlag - 1;
                    if (!Engine_LhTexCodec.TryDecodeVariant(d, bodyOffset, bodySize, mode,
                            width, height, out byte[] vb, out string verr))
                    {
                        error = $"plane {p}: {verr}";
                        return false;
                    }

                    Scatter(vb, output, blocks, stride, bias);
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

        surface = output;
        return true;
    }

    private static void Scatter(byte[] src, byte[] dst, int blocks, int stride, int bias)
    {
        int count = Math.Min(blocks, src.Length / 8);
        if (stride == 8 && bias == 0)
        {
            Buffer.BlockCopy(src, 0, dst, 0, Math.Min(count * 8, dst.Length));
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

    private static void FillConstant(byte[] dst, int blocks, int stride, int bias,
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

            block.CopyTo(dst.AsSpan(t, 8));
        }
    }
}
