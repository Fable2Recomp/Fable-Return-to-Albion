using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Godot;
using Refii.Core;

namespace Refii.Textures;

/// <summary>Exact metadata required before a PC-prepared ASTC variant may replace a world albedo.</summary>
/// <param name="Width">Unchanged decoded width.</param>
/// <param name="Height">Unchanged decoded height.</param>
/// <param name="Mipmaps">Whether the production decoder produced a complete mip chain.</param>
/// <param name="HasAlpha">The production decoder's authored transparency classification.</param>
public sealed record AndroidGpuTextureDescriptor(int Width, int Height, bool Mipmaps, bool HasAlpha);

/// <summary>Validates the existing RFTX v4 container before constructing any native GPU image.</summary>
/// <remarks>This deliberately accepts only one ASTC4x4 UNORM face with unchanged metadata.
/// CPU decode/BaseKey entries remain untouched, and malformed or absent private preparation
/// falls back to the ordinary runtime texture. Bounds are specific to this initial albedo profile.</remarks>
public static class AndroidGpuTextureContainer
{
    /// <summary>Maximum supported edge for the initial offline albedo profile.</summary>
    public const int MaximumEdge = 4096;
    /// <summary>Maximum uncompressed ASTC block payload accepted from a prepared file.</summary>
    public const int MaximumPayloadBytes = 32 * 1024 * 1024;

    /// <summary>Computes exact ASTC block storage, including every terminal mip block.</summary>
    /// <param name="descriptor">The original decoded image metadata.</param>
    /// <returns>Zero for out-of-profile dimensions, otherwise the exact bounded payload length.</returns>
    public static int PayloadBytes(AndroidGpuTextureDescriptor descriptor)
    {
        int width = descriptor.Width, height = descriptor.Height;
        if (width <= 0 || height <= 0 || width > MaximumEdge || height > MaximumEdge ||
            width % 4 != 0 || height % 4 != 0) return 0;
        long bytes = 0;
        do
        {
            bytes += (long)((width + 3) / 4) * ((height + 3) / 4) * 16;
            if (!descriptor.Mipmaps || (width == 1 && height == 1)) break;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        } while (true);
        return bytes <= MaximumPayloadBytes ? (int)bytes : 0;
    }

    /// <summary>Reads a single prepared variant with exact lengths and a bounded DEFLATE expansion.</summary>
    /// <param name="input">A readable seekable file or test stream, left open.</param>
    /// <param name="expected">Metadata from the current production decode, including current mod bytes.</param>
    /// <param name="payload">Validated ASTC blocks, or an empty array on any invalid container.</param>
    /// <returns>True only for the exact supported single-face RFTX representation.</returns>
    public static bool TryRead(Stream input, AndroidGpuTextureDescriptor expected, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        int bytes = PayloadBytes(expected);
        if (bytes == 0 || !input.CanRead || !input.CanSeek) return false;
        try
        {
            if (input.Length < 33 || input.Length > MaximumPayloadBytes + 1024 * 1024) return false;
            input.Position = 0;
            Span<byte> header = stackalloc byte[33];
            input.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x58544652u ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 4 ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != expected.Width ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) != expected.Height ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) != (uint)Image.Format.Astc4X4 ||
                header[20] != (expected.Mipmaps ? 1 : 0) ||
                header[21] != (expected.HasAlpha ? 1 : 0) || header[22] > 2 ||
                BinaryPrimitives.ReadUInt16LittleEndian(header[23..]) != 1 ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[25..]) != bytes) return false;
            uint deflated = BinaryPrimitives.ReadUInt32LittleEndian(header[29..]);
            if (deflated == 0 || 33L + deflated != input.Length) return false;
            var decoded = new byte[bytes];
            using var inflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
            inflate.ReadExactly(decoded);
            if (inflate.ReadByte() != -1) return false;
            payload = decoded;
            return true;
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>Optional, separately prepared Android GPU variants; never a replacement for shared CPU decoding.</summary>
public static class AndroidGpuTextureCache
{
    /// <summary>All output-changing inputs, combined with source bytes and the shared decoder version.</summary>
    public const string Recipe = "android-gpu/astc4x4-unorm/rgba-identity/godot-4.7.1-medium/albedo/full-authored-tail/v1";
    private static long _hits, _misses, _invalid, _payloadBytes;

    /// <summary>Number of successfully loaded prepared albedos, not a count of resident textures.</summary>
    public static long Hits => Interlocked.Read(ref _hits);
    /// <summary>Eligible lookups without a prepared file.</summary>
    public static long Misses => Interlocked.Read(ref _misses);
    /// <summary>Prepared files rejected because their data or current metadata is incompatible.</summary>
    public static long Invalid => Interlocked.Read(ref _invalid);
    /// <summary>Cumulative compressed payload bytes loaded successfully.</summary>
    public static long PayloadBytes => Interlocked.Read(ref _payloadBytes);

    /// <summary>Computes a GPU-only key without modifying any shared CPU BaseKey entry.</summary>
    /// <param name="source">Exact original or modded bytes decoded for this request.</param>
    /// <returns>The lowercase SHA-256 derived key.</returns>
    public static string Key(ReadOnlySpan<byte> source) => Engine_TexCache.DerivedKey(Recipe, source);

    /// <summary>Limits this initial profile to original world albedos on an ASTC-capable Android GPU.</summary>
    /// <param name="android">Whether the active host is Android.</param>
    /// <param name="astc">Captured sampled/update support for ASTC4x4 UNORM.</param>
    /// <param name="semantic">Actual material binding, never inferred from the filename.</param>
    /// <param name="enhancementEnabled">Prepared originals cannot stand in for enhanced output.</param>
    /// <param name="mipsSuppressed">An explicit no-mip mode requires a different preparation recipe.</param>
    /// <param name="sourceFormat">The decoder's original color representation.</param>
    /// <returns>Whether a separate prepared variant may be consulted.</returns>
    public static bool Eligible(bool android, bool astc, TexSemantic semantic, bool enhancementEnabled,
        bool mipsSuppressed, Image.Format sourceFormat) => android && astc && semantic == TexSemantic.Albedo &&
        !enhancementEnabled && !mipsSuppressed &&
        sourceFormat is Image.Format.Dxt1 or Image.Format.Dxt5 or Image.Format.Rgba8;

    /// <summary>Loads a separately prepared albedo only when it exactly matches the current decoded image.</summary>
    /// <param name="source">Bytes used by the production decoder, including active mod changes.</param>
    /// <param name="semantic">The actual material texture slot.</param>
    /// <param name="original">Borrowed CPU image, left untouched for all other consumers.</param>
    /// <param name="hasAlpha">The current decoder's transparency classification.</param>
    /// <returns>A caller-disposed ASTC image on a valid hit, otherwise null for normal fallback.</returns>
    public static Image? TryLoad(ReadOnlySpan<byte> source, TexSemantic semantic, Image original, bool hasAlpha)
    {
        AndroidTextureUploadPolicy policy = AndroidTextureUpload.Policy;
        if (!Eligible(policy.Android, policy.Astc4x4, semantic, Engine_TexEnhance.Enabled,
            OS.GetEnvironment("REFII_TEX_NO_MIPS") == "1", original.GetFormat())) return null;
        var descriptor = new AndroidGpuTextureDescriptor(original.GetWidth(), original.GetHeight(),
            original.HasMipmaps(), hasAlpha);
        if (AndroidGpuTextureContainer.PayloadBytes(descriptor) == 0) return null;
        string key = Key(source);
        string path = System.IO.Path.Combine(Engine_AppRoot.Path, "gpu-textures", "astc4x4-v1", key[..2], key + ".rftx");
        try
        {
            if (!File.Exists(path)) { Interlocked.Increment(ref _misses); return null; }
            using var input = File.OpenRead(path);
            if (!AndroidGpuTextureContainer.TryRead(input, descriptor, out byte[] payload))
            { Interlocked.Increment(ref _invalid); return null; }
            Image image = Image.CreateFromData(descriptor.Width, descriptor.Height, descriptor.Mipmaps,
                Image.Format.Astc4X4, payload);
            if (image.IsEmpty()) { image.Dispose(); Interlocked.Increment(ref _invalid); return null; }
            Interlocked.Increment(ref _hits);
            Interlocked.Add(ref _payloadBytes, payload.Length);
            return image;
        }
        catch (IOException) { Interlocked.Increment(ref _invalid); return null; }
        catch (UnauthorizedAccessException) { Interlocked.Increment(ref _invalid); return null; }
    }
}
