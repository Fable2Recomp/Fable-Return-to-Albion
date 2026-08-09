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
    public const int Size = 84;

    public int Version;

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
