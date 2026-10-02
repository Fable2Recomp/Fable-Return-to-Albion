using System;
using System.IO;
using System.IO.Compression;

namespace Refii.Level;

/// <summary>
/// A landscape's height grid out of its .ehf — the grid the guest's landscape vertex shader
/// samples, for a landscape the scenario states no .ghf for (a vista).
/// </summary>
/// <remarks>
/// Header (`sub_82A8FC88`, after the 23-byte magic and the version): +27 origin X (res+68),
/// +31 origin Y (res+64), +35 samples X (res+72), +39 samples Y (res+76), +43 sample spacing
/// (res+80). The body starts with tex0, the height texture (L16, Xbox-tiled, big-endian words,
/// zlib behind an [uncompressed][compressed] size pair), then tex1 (DXN normals), then the
/// height scale (res+176 → PositionScale.z). The vertex shader is `h = tfetch(L16).x * scale`
/// with no bias, so a texel is <c>value / 65535 * scale</c> at <c>origin + index * spacing</c>.
/// Checked against the 769x769 Bower Lake .ghf: within one 16-bit step everywhere.
/// </remarks>
public static class Engine_EhfHeights
{
    private const string Magic = "HeightFieldGraphicsFile";
    private const int HeaderLength = 63;
    private const uint TexMagic = 0xFFFFFFFEu;

    public static bool TryDecode(byte[] ehf, out GhfHeights heights, out string error)
    {
        heights = new GhfHeights();
        error = string.Empty;

        if (ehf.Length < HeaderLength ||
            System.Text.Encoding.ASCII.GetString(ehf, 0, Magic.Length) != Magic)
        {
            error = "not a HeightFieldGraphicsFile";
            return false;
        }

        float originX = ReadF32(ehf, 27);
        float originY = ReadF32(ehf, 31);
        uint samplesX = ReadU32(ehf, 35);
        uint samplesY = ReadU32(ehf, 39);
        float spacing = ReadF32(ehf, 43);
        long bodyOffset = ReadU32(ehf, 55);
        long bodyEnd = bodyOffset + ReadU32(ehf, 59);

        if (samplesX < 2 || samplesY < 2 || samplesX > 4097 || samplesY > 4097 ||
            !(spacing > 0f) || !float.IsFinite(spacing) ||
            !float.IsFinite(originX) || !float.IsFinite(originY) || bodyEnd > ehf.Length)
        {
            error = $"implausible header ({samplesX}x{samplesY} at {spacing})";
            return false;
        }

        int heightAt = (int)bodyOffset;
        if (!TryBlobEnd(ehf, heightAt, bodyEnd, out long normalsAt) ||
            !TryBlobEnd(ehf, (int)normalsAt, bodyEnd, out long scaleAt) ||
            scaleAt + 4 > bodyEnd)
        {
            error = "could not walk the leading textures";
            return false;
        }

        float scale = ReadF32(ehf, (int)scaleAt);
        uint width = ReadU32(ehf, heightAt + 0x10);
        uint height = ReadU32(ehf, heightAt + 0x14);
        uint pixelFormat = ReadU32(ehf, heightAt + 0x18);
        uint metaSize = ReadU32(ehf, heightAt + 0x20);
        if (width != samplesX || height != samplesY)
        {
            error = $"height texture {width}x{height} is not the header's {samplesX}x{samplesY}";
            return false;
        }

        if (pixelFormat == 98u || !float.IsFinite(scale))
        {
            error = $"height texture format {pixelFormat} / scale {scale} not decoded";
            return false;
        }

        long table = heightAt + metaSize;
        uint rawSize = ReadU32(ehf, (int)table);
        uint compressed = ReadU32(ehf, (int)table + 4);
        long zlibAt = table + 8;
        if (zlibAt + compressed > bodyEnd || rawSize == 0 || rawSize > 64u * 1024 * 1024)
        {
            error = "height texture payload out of range";
            return false;
        }

        byte[] raw = new byte[rawSize];
        int filled = 0;
        try
        {
            // Read up to the stated size and stop: the stream lacks a clean end, so a read past
            // the last byte throws even though every byte is present.
            using var source = new MemoryStream(ehf, (int)zlibAt, (int)compressed);
            using var inflate = new ZLibStream(source, CompressionMode.Decompress);
            while (filled < raw.Length)
            {
                int n = inflate.Read(raw, filled, raw.Length - filled);
                if (n <= 0)
                {
                    break;
                }

                filled += n;
            }
        }
        catch (InvalidDataException) when (filled == raw.Length)
        {
        }
        catch (Exception exception)
        {
            error = $"height texture inflate failed: {exception.Message}";
            return false;
        }

        byte[] linear = Refii.Textures.Engine_Xbox360Tiling.Untile(raw, (int)width, (int)height, 2);
        if (linear.Length < (long)width * height * 2)
        {
            error = "height texture untile came up short";
            return false;
        }

        var values = new float[(long)width * height];
        float lowest = float.PositiveInfinity;
        float highest = float.NegativeInfinity;
        float perStep = scale / 65535f;
        for (long i = 0; i < values.Length; i++)
        {
            float value = ((linear[i * 2] << 8) | linear[(i * 2) + 1]) * perStep;
            values[i] = value;
            lowest = MathF.Min(lowest, value);
            highest = MathF.Max(highest, value);
        }

        heights = new GhfHeights
        {
            Width = (int)width,
            Height = (int)height,
            Values = values,
            MinHeight = lowest,
            MaxHeight = highest,
            OriginX = originX,
            OriginZ = originY,
            Spacing = spacing,
        };
        return true;
    }

    /// <summary>The sample counts the header states (res+72 / res+76), for a landscape with no .ghf.</summary>
    public static bool TryReadSize(byte[] ehf, out int samplesX, out int samplesY)
    {
        samplesX = 0;
        samplesY = 0;
        if (ehf.Length < HeaderLength ||
            System.Text.Encoding.ASCII.GetString(ehf, 0, Magic.Length) != Magic)
        {
            return false;
        }

        uint x = ReadU32(ehf, 35);
        uint y = ReadU32(ehf, 39);
        if (x < 2 || y < 2 || x > 4097 || y > 4097)
        {
            return false;
        }

        samplesX = (int)x;
        samplesY = (int)y;
        return true;
    }

    /// <summary>The offset just past one texture blob (the walk `Engine_EhfBody` does).</summary>
    private static bool TryBlobEnd(byte[] data, int start, long limit, out long next)
    {
        next = 0;
        if (start + 0x60 > limit || ReadU32(data, start) != TexMagic)
        {
            return false;
        }

        uint pixelFormat = ReadU32(data, start + 0x18);
        uint metaSize = ReadU32(data, start + 0x20);
        if (metaSize > 0x200 || start + metaSize + 8 > limit)
        {
            return false;
        }

        next = pixelFormat == 98u
            ? start + metaSize + ((long)ReadU32(data, start + 0x10) * ReadU32(data, start + 0x14) * 2)
            : start + metaSize + 8 + ReadU32(data, (int)(start + metaSize + 4));
        return next <= limit && next > start;
    }

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];

    private static float ReadF32(byte[] data, int at) =>
        BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(data, at)));
}
