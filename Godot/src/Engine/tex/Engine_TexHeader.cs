using System;
using System.Buffers.Binary;

namespace Refii.Textures;

/// <summary>Pixel formats a <c>.tex</c> can declare. The value indexes the engine's render-format table.</summary>
public enum TexPixelFormat
{
    A8R8G8B8 = 2,
    X8R8G8B8 = 4,
    Dxt1 = 35,
    Dxt5 = 39,
    Dxn = 40,

    /// <summary>Single-channel coverage, used by the GUI font atlases. Identified by inspection.</summary>
    Bc4 = 100,
}

/// <summary>
/// The 84-byte header a <c>.tex</c> opens with.
///
/// The chain parser in <see cref="Engine_TexData"/> sniffs the payload structurally,
/// which works only for a file whose bytes BEGIN with a mip-record chain. Most do not:
/// they open with this header, and it is the authority on three things the payload
/// cannot be trusted for —
///
/// <list type="bullet">
/// <item><b>Format.</b> Guessing it from the record shape reads a DXT5 surface as BC1,
///   which decodes to something that looks plausible and has lost its alpha. That is
///   how UI icons ended up drawn as opaque black boxes.</item>
/// <item><b>Dimensions.</b> Deriving them from the payload's own halfwords gives the
///   axes SWAPPED, because the codec's bit reader consumes 32-bit big-endian words
///   LSB-first and so reads the second halfword first.</item>
/// <item><b>Container.</b> <c>Flags &amp; 0x02</c> means the payload is a raw Xbox 360
///   surface — tiled and word-swapped — with no mip chain in it at all.</item>
/// </list>
///
/// <c>LevelOffsets</c> are absolute into the whole file, which is why entry 0 is always
/// 84: a value easily mistaken for a "header size" field.
/// </summary>
public sealed class TexHeader
{
    public const int Size = 84;

    /// <summary>Header dword 0 holds the NEGATED version, so version 2 reads as 0xFFFFFFFE.</summary>
    public int Version;

    /// <summary>Payload size, excluding this 84-byte header.</summary>
    public int PayloadSize;

    /// <summary>0 = 2D, 2 = cube map.</summary>
    public int TextureKind;

    /// <summary>
    /// Container selector plus material bits. <c>0x02</c> = raw payload, <c>0x10</c> =
    /// compressed, <c>0x01</c> = legacy version-1 layout, <c>0x04</c> = zlib-wrapped.
    /// Bits <c>0x20/0x40/0x80</c> are material properties rather than container ones.
    /// </summary>
    public int Flags;

    public int Width;
    public int Height;

    /// <summary>Index into the engine's render-format table — see <see cref="TexPixelFormat"/>.</summary>
    public int PixelFormat;

    public int LevelCount;

    /// <summary>Absolute offsets of each mip level into the whole file. Twelve slots, zero when unused.</summary>
    public int[] LevelOffsets = new int[12];

    /// <summary>
    /// Decoder family. 0 and 12 are single-plane 8-byte DXT1 blocks; 1, 2 and 3 are
    /// two-plane 16-byte blocks (DXT3, DXT5, DXN). 13 is the default and only appears on
    /// raw-path files.
    /// </summary>
    public int CodecId;

    public bool IsCompressed => (Flags & 0x10) != 0;
    public bool IsRaw => (Flags & 0x02) != 0;
    public bool IsLegacyV1 => (Flags & 0x01) != 0;
    public bool IsZlibWrapped => (Flags & 0x04) != 0;
    public bool IsCube => TextureKind == 2;

    /// <summary>Blocks are 16 bytes for the two-plane families, 8 for single-plane.</summary>
    public int BlockStride => CodecId is 1 or 2 or 3 ? 16 : 8;

    public int BlocksWide => (Width + 3) / 4;
    public int BlocksHigh => (Height + 3) / 4;

    /// <summary>The surface format this header declares, or Unsupported.</summary>
    public TexSurfaceFormat SurfaceFormat => PixelFormat switch
    {
        (int)TexPixelFormat.Dxt1 => TexSurfaceFormat.Bc1,
        (int)TexPixelFormat.Dxt5 => TexSurfaceFormat.Bc3,
        (int)TexPixelFormat.Dxn => TexSurfaceFormat.Bc5,
        (int)TexPixelFormat.Bc4 => TexSurfaceFormat.Bc4,
        (int)TexPixelFormat.A8R8G8B8 => TexSurfaceFormat.Rgba8,
        (int)TexPixelFormat.X8R8G8B8 => TexSurfaceFormat.Rgba8,
        _ => TexSurfaceFormat.Unsupported,
    };

    /// <summary>Bytes per tiling unit for the declared format, or 0 when unknown.</summary>
    public int TilingBlockBytes => PixelFormat switch
    {
        (int)TexPixelFormat.Dxt1 => 8,
        (int)TexPixelFormat.Bc4 => 8,
        (int)TexPixelFormat.Dxt5 => 16,
        (int)TexPixelFormat.Dxn => 16,
        (int)TexPixelFormat.A8R8G8B8 => 4,
        (int)TexPixelFormat.X8R8G8B8 => 4,
        _ => 0,
    };

    /// <summary>
    /// True when <paramref name="d"/> opens with a header rather than a mip chain. The
    /// version word is stored negated, so a negative first dword is the marker.
    /// </summary>
    public static bool LooksLikeHeader(byte[] d) =>
        d.Length >= Size && BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(0, 4)) < 0;

    public static bool TryParse(byte[] d, out TexHeader header, out string error)
    {
        header = default!;
        error = string.Empty;

        if (!LooksLikeHeader(d))
        {
            error = "no .tex header";
            return false;
        }

        int I32(int off) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(off, 4));

        header = new TexHeader
        {
            Version = -I32(0x00),
            PayloadSize = I32(0x04),
            TextureKind = I32(0x08),
            Flags = I32(0x0C),
            Width = I32(0x10),
            Height = I32(0x14),
            PixelFormat = I32(0x18),
            LevelCount = I32(0x1C),
            CodecId = I32(0x50),
        };

        for (int i = 0; i < 12; i++)
        {
            header.LevelOffsets[i] = I32(0x20 + i * 4);
        }

        if (header.Width <= 0 || header.Height <= 0)
        {
            error = $"header declares {header.Width}x{header.Height}";
            return false;
        }

        return true;
    }
}
