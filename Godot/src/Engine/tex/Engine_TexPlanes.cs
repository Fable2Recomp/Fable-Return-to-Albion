using System;
using System.Linq;
using System.Buffers.Binary;

namespace Refii.Textures;

/// <summary>
/// One plane descriptor from a mip record. A record holds four; how many are live is
/// decided by the header's codec family, not by anything in the record.
/// </summary>
public readonly struct TexPlane
{
    /// <summary>Codec for this plane. 0 means absent.</summary>
    public readonly uint CompFlag;

    /// <summary>
    /// Offset of the plane body, relative to the START of the 48-byte record. For
    /// <see cref="Engine_TexPlanes.CompConstantFill"/> this field is instead a literal
    /// <c>0x00RRGGBB</c> colour rather than an offset.
    /// </summary>
    public readonly uint DataOffset;

    /// <summary>Body length.</summary>
    public readonly uint Size;

    public TexPlane(uint compFlag, uint dataOffset, uint size)
    {
        CompFlag = compFlag;
        DataOffset = dataOffset;
        Size = size;
    }
}

/// <summary>
/// Decodes a mip level from its PLANE RECORD, which is what a <c>.tex</c> payload
/// actually holds.
///
/// The payload is <b>not</b> a chain of mips. There is one 48-byte record per level,
/// located through the header's <c>LevelOffsets</c>, and each record is an array of four
/// 12-byte <see cref="TexPlane"/> descriptors with the bodies following.
///
/// Reading it as a chain — walking record to record and taking the first thing that
/// decodes — happens to work for the single-plane files that dominate the set, because
/// plane 0 of a record looks exactly like the head of a chain. It silently truncates the
/// TWO-plane families: it returns the colour plane on its own, as 8-byte BC1 blocks, and
/// the alpha is never decoded at all. 709 of the shipped textures are two-plane DXT5,
/// which is why UI icons drew as opaque black boxes.
///
/// Which planes are live, and where each writes, comes from the header's codec id:
/// <list type="bullet">
/// <item>0 and 12 — one plane, 8-byte DXT1 blocks.</item>
/// <item>1 (DXT2/3) and 2 (DXT5) — two planes, 16-byte blocks. Plane 0 is the COLOUR
///   half at <c>block + 8</c>, plane 1 is ALPHA at <c>block + 0</c>. That is exactly
///   BC2/BC3 order, which is why the assembled surface hands to Godot as <c>Dxt5</c>
///   with no further shuffling.</item>
/// <item>3 (DXN) — two planes, 16-byte blocks, but the OPPOSITE mapping: the engine
///   computes <c>base + 8 * planeIndex</c> rather than branching, so plane 0 is at
///   <c>+0</c>. See <see cref="PlaneBias"/>.</item>
/// </list>
/// </summary>
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

    /// <summary>Reads the four plane descriptors of the record at <paramref name="recordOffset"/>.</summary>
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

    /// <summary>
    /// Decodes one level into a block surface laid out as the engine would write it.
    /// <paramref name="d"/> is the whole file and <paramref name="recordOffset"/> is
    /// absolute into it.
    /// </summary>
    /// <summary>
    /// The plane codecs a level's record declares, for diagnostics. A level that decodes to
    /// noise is usually announcing a codec we mishandle rather than being corrupt.
    /// </summary>
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

    /// <summary>
    /// Decodes one level at an EXPLICIT size.
    ///
    /// Mip level N is half the header's dimensions N times over, and the header only
    /// states level 0. Every size in here — the block count, the dimensions handed to the
    /// untiler, the sanity check against what the codec reports — has to be the level's
    /// own, or level 1 is decoded as though it were the size of level 0 and reads past
    /// its record into the next one.
    /// </summary>
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

        // compFlag 7 is a WHOLE-RECORD mode, not a per-plane one: sub_82B94DE0 tests
        // plane 0's flag for 7 before the plane loop and memcpys `stride` bytes per
        // block, so there is no second plane to read and the copy is 16 bytes wide on a
        // two-plane surface, not 8.
        if (planes[0].CompFlag == CompRaw)
        {
            int rawOffset = recordOffset + (int)planes[0].DataOffset;
            int needed = blocks * stride;
            if (rawOffset < 0 || rawOffset + needed > d.Length)
            {
                error = $"raw record: need {needed} bytes at {rawOffset}, file is {d.Length}";
                return false;
            }

            // Raw means UNCOMPRESSED, not "already in our layout". These bytes are exactly
            // what the Xenos was handed: tiled, and big-endian per 16-bit field. Copying
            // them straight through leaves both untouched, and the level decodes to
            // green-and-magenta noise.
            //
            // It reads as a mip bug because Fable stores its small levels raw and its large
            // ones Huffman-coded — bs_wood_rough is comp=1 down to 64x64 and comp=7 at 32x32
            // and below — so the corruption only appears on levels nothing sampled until
            // mipmaps were turned on. Any texture whose LEVEL 0 is raw was equally wrong
            // before that, just more visibly.
            var payload = new byte[needed];
            Buffer.BlockCopy(d, rawOffset, payload, 0, needed);
            Engine_Xbox360Tiling.SwapWords16(payload);

            int blocksWide = Math.Max(1, (width + 3) / 4);
            int blocksHigh = Math.Max(1, (height + 3) / 4);

            // A raw level is stored at its LOGICAL size, with no padding to the 32-block
            // tile alignment: this record is exactly blocksWide*blocksHigh*stride bytes and
            // no more. Untiling assumes the padded stride, so it reads past the end for
            // anything narrower than 32 blocks and leaves most of the surface zeroed — a
            // 32x32 level came out as correct wood on top and black underneath.
            //
            // Below one tile the layout is therefore linear and only the byte order needs
            // undoing. At or above it, tiling applies as usual.
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
                    // Only 2 and 3 are valid in a BLOCK codec, mapping to modes 1 and 2.
                    // compFlag 4 lands in the linear-codec handler and indexes its jump
                    // table negatively — it is not a third block mode.
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
                    // DataOffset is a literal colour, not an offset. WHICH constant block
                    // gets written depends on the half being filled — the engine has three
                    // encodings for this one flag, and writing the colour form into an
                    // alpha half puts an R5G6B5 pair where the alpha endpoints belong.
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

    /// <summary>Copies 8-byte decoded blocks into a strided destination at the given bias.</summary>
    private static void Scatter(byte[] src, byte[] dst, int blocks, int stride, int bias)
    {
        for (int i = 0; i < blocks; i++)
        {
            int s = i * 8;
            int t = i * stride + bias;
            if (s + 8 > src.Length || t + 8 > dst.Length)
            {
                return;
            }

            Buffer.BlockCopy(src, s, dst, t, 8);
        }
    }

    /// <summary>Which constant-block encoding a half takes.</summary>
    private enum ConstantKind
    {
        /// <summary>R5G6B5 endpoint pair, zero indices — a colour half.</summary>
        Colour,

        /// <summary>BC4 block: both endpoints the same byte, zero indices — a DXT5/DXN alpha half.</summary>
        AlphaBc4,

        /// <summary>DXT3 explicit alpha: sixteen 4-bit values.</summary>
        AlphaExplicit,
    }

    /// <summary>
    /// Destination offset of a plane within its block.
    ///
    /// Codecs 1 (DXT2/3) and 2 (DXT5) put the COLOUR half second — plane 0 at
    /// <c>block + 8</c>, plane 1 at <c>block + 0</c> — which is BC2/BC3 order.
    /// **Codec 3 (DXN) is the other way round**: <c>sub_82B94DE0</c> computes its
    /// destination as <c>base + 8 * planeIndex</c> rather than branching on the index, so
    /// plane 0 lands at <c>+0</c>. Applying the 1/2 rule to DXN swaps its two BC4 halves,
    /// which reads as red and green exchanged in a normal map — plausible enough to ship
    /// unnoticed.
    /// </summary>
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

        // Codec 1 is DXT2/3, whose alpha half is sixteen explicit 4-bit values rather
        // than a BC4 endpoint block.
        return codecId == 1 ? ConstantKind.AlphaExplicit : ConstantKind.AlphaBc4;
    }

    /// <summary>Writes a constant block into every block of a half.</summary>
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
                // Sixteen 4-bit values, two per byte.
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
