using System;
using System.Buffers.Binary;

using Refii.Core;

using Refii.Ui;

namespace Refii.Textures;

public enum TexPixelFormat
{
    A8R8G8B8 = 2,
    X8R8G8B8 = 4,
    Dxt1 = 35,
    Dxt5 = 39,
    Dxn = 40,

    Bc4 = 100,
}

public sealed class TexHeader
{
    /// <summary>The smallest header, Fable 2's: what a size check before parsing can rely on.</summary>
    public const int Size = 84;

    /// <summary>
    /// Fable 3's header (version 4) leads with this magic. The guest's texture streamer gates on
    /// it (`hdr+0 == 0xABCBBBF3 && hdr+4 == 4` in sub_82D46DB0, Fable 3 IDB).
    /// </summary>
    public const uint Fable3Magic = 0xABCBBBF3;

    /// <summary>Fable 3's header, 92 bytes: its first mip starts at offset 0x5C.</summary>
    public const int Fable3Size = 92;

    public int Version;

    /// <summary>This header's own length, where the payload follows it.</summary>
    public int HeaderSize = Size;

    public int PayloadSize;

    public int TextureKind;

    public int Flags;

    public int Width;
    public int Height;

    public int PixelFormat;

    public int LevelCount;

    public int[] LevelOffsets = new int[12];

    public int CodecId;

    public bool IsCompressed => (Flags & 0x10) != 0;
    public bool IsRaw => (Flags & 0x02) != 0;
    public bool IsLegacyV1 => (Flags & 0x01) != 0;
    public bool IsZlibWrapped => (Flags & 0x04) != 0;
    public bool IsCube => TextureKind == 2;

    public int BlockStride => CodecId is 1 or 2 or 3 ? 16 : 8;

    public int BlocksWide => (Width + 3) / 4;
    public int BlocksHigh => (Height + 3) / 4;

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

    public static bool LooksLikeHeader(byte[] d) =>
        d.Length >= Size && BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(0, 4)) < 0;

    /// <summary>Fable 3's layout: its magic, then version 4.</summary>
    public static bool IsFable3Header(byte[] d) =>
        d.Length >= Fable3Size && BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(0, 4)) == Fable3Magic;

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

        if (IsFable3Header(d))
        {
            // Fable 2's fields behind the magic and the version, with the codec moved up beside
            // the format and one word before the level count. The guest reads them where this does:
            // the codec at +32 is the transcode format sub_82D6E9C8 switches on (12 an 8-byte block,
            // 1..3 16-byte), the level count at +40 and the mip offsets from +44 (sub_82D678D0).
            header = new TexHeader
            {
                Version = I32(0x04),
                HeaderSize = Fable3Size,
                PayloadSize = I32(0x08),
                TextureKind = I32(0x0C),
                Flags = I32(0x10),
                Width = I32(0x14),
                Height = I32(0x18),
                PixelFormat = I32(0x1C),
                CodecId = I32(0x20),
                LevelCount = I32(0x28),
            };

            for (int i = 0; i < 12; i++)
            {
                header.LevelOffsets[i] = I32(0x2C + i * 4);
            }
        }
        else
        {
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
        }

        if (header.Width <= 0 || header.Height <= 0)
        {
            error = $"header declares {header.Width}x{header.Height}";
            return false;
        }

        return true;
    }
}
