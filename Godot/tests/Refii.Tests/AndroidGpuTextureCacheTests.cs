using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using GdUnit4;
using Godot;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Exercises prepared-cache selection, strict bounded parsing and source-change lifecycles in the CLI.</summary>
[TestSuite]
public sealed class AndroidGpuTextureCacheTests
{
    /// <summary>Terminal mips retain a whole ASTC block, including rectangular chains.</summary>
    [TestCase]
    public void ExactMipStorageAndProfileBounds()
    {
        AssertThat(AndroidGpuTextureContainer.PayloadBytes(new(256, 256, true, false))).IsEqual(87408);
        AssertThat(AndroidGpuTextureContainer.PayloadBytes(new(8, 4, true, true))).IsEqual(80);
        AssertThat(AndroidGpuTextureContainer.PayloadBytes(new(8, 4, false, true))).IsEqual(32);
        foreach (var descriptor in new[] { new AndroidGpuTextureDescriptor(0, 4, false, false),
            new(3, 4, true, false), new(4, 6, true, false), new(8192, 4, false, false), new(4, -4, true, false) })
            AssertThat(AndroidGpuTextureContainer.PayloadBytes(descriptor)).IsEqual(0);
        AssertThat(AndroidGpuTextureContainer.PayloadBytes(new(4096, 4096, true, false)) > 0).IsTrue();
    }

    /// <summary>Only original color albedos use this profile; normals, UI and unsupported GPUs never consult it.</summary>
    [TestCase]
    public void SemanticAndDeviceEligibilityIsConservative()
    {
        foreach (Image.Format format in new[] { Image.Format.Dxt1, Image.Format.Dxt5, Image.Format.Rgba8 })
            AssertThat(Engine_AndroidGpuTextureCache.Eligible(true, true, TexSemantic.Albedo, false, false, format)).IsTrue();
        foreach (TexSemantic semantic in Enum.GetValues<TexSemantic>())
            if (semantic != TexSemantic.Albedo)
                AssertThat(Engine_AndroidGpuTextureCache.Eligible(true, true, semantic, false, false, Image.Format.Dxt1)).IsFalse();
        AssertThat(Engine_AndroidGpuTextureCache.Eligible(false, true, TexSemantic.Albedo, false, false, Image.Format.Dxt1)).IsFalse();
        AssertThat(Engine_AndroidGpuTextureCache.Eligible(true, false, TexSemantic.Albedo, false, false, Image.Format.Dxt1)).IsFalse();
        AssertThat(Engine_AndroidGpuTextureCache.Eligible(true, true, TexSemantic.Albedo, true, false, Image.Format.Dxt1)).IsFalse();
        AssertThat(Engine_AndroidGpuTextureCache.Eligible(true, true, TexSemantic.Albedo, false, true, Image.Format.Dxt1)).IsFalse();
        AssertThat(Engine_AndroidGpuTextureCache.Eligible(true, true, TexSemantic.Albedo, false, false, Image.Format.RgtcRg)).IsFalse();
    }

    /// <summary>Source bytes, recipe and converter version select a separate namespace from CPU decodes.</summary>
    [TestCase]
    public void PreparedKeyCannotReplaceSharedCpuEntry()
    {
        byte[] original = { 1, 2, 3, 4 };
        byte[] changed = { 1, 2, 3, 5 };
        string key = Engine_AndroidGpuTextureCache.Key(original);
        AssertThat(key.Length).IsEqual(64);
        AssertThat(key).IsEqual(Engine_TexCache.DerivedKey(Engine_AndroidGpuTextureCache.Recipe, original));
        AssertThat(key == Engine_TexCache.BaseKey(original)).IsFalse();
        AssertThat(key == Engine_AndroidGpuTextureCache.Key(changed)).IsFalse();
        AssertThat(key == Engine_TexCache.DerivedKey(Engine_AndroidGpuTextureCache.Recipe + "-changed", original)).IsFalse();
    }

    /// <summary>A valid single-face RFTX round trip preserves exact mip bytes and leaves the caller's stream open.</summary>
    [TestCase]
    public void ValidSingleFaceRoundTrip()
    {
        var expected = new AndroidGpuTextureDescriptor(8, 4, true, true);
        byte[] raw = Raw(expected);
        using var input = new MemoryStream(Container(expected, raw));
        AssertThat(AndroidGpuTextureContainer.TryRead(input, expected, out byte[] actual)).IsTrue();
        AssertThat(Convert.ToHexString(actual)).IsEqual(Convert.ToHexString(raw));
        AssertThat(input.CanRead).IsTrue();
    }

    /// <summary>Incorrect dimensions, alpha, mips, version, format, face count and declared lengths are rejected.</summary>
    [TestCase]
    public void InvalidHeadersFallBackBeforeNativeImageCreation()
    {
        var expected = new AndroidGpuTextureDescriptor(8, 4, true, true);
        byte[] valid = Container(expected, Raw(expected));
        foreach (int offset in new[] { 0, 4, 8, 12, 16, 20, 21, 22, 23, 25, 29 })
        {
            byte[] invalid = (byte[])valid.Clone();
            invalid[offset] = offset == 22 ? (byte)255 : (byte)(invalid[offset] ^ 0x40);
            using var input = new MemoryStream(invalid);
            AssertThat(AndroidGpuTextureContainer.TryRead(input, expected, out byte[] output)).IsFalse();
            AssertThat(output.Length).IsEqual(0);
        }
        using var mismatch = new MemoryStream(valid);
        AssertThat(AndroidGpuTextureContainer.TryRead(mismatch, expected with { HasAlpha = false }, out _)).IsFalse();
    }

    /// <summary>Truncated, trailing and over-expanding payloads cannot enter the GPU path.</summary>
    [TestCase]
    public void CorruptAndOversizedPayloadsStayBounded()
    {
        var expected = new AndroidGpuTextureDescriptor(8, 4, true, false);
        byte[] raw = Raw(expected);
        byte[] valid = Container(expected, raw);
        foreach (byte[] invalid in new[] { valid[..20], valid[..^1], Append(valid, 1),
            Container(expected, raw[..^1]), Container(expected, Append(raw, 1)) })
        {
            using var input = new MemoryStream(invalid);
            AssertThat(AndroidGpuTextureContainer.TryRead(input, expected, out byte[] output)).IsFalse();
            AssertThat(output.Length).IsEqual(0);
        }
        byte[] bogus = (byte[])valid.Clone();
        Array.Fill(bogus, (byte)255, 33, bogus.Length - 33);
        using var corrupt = new MemoryStream(bogus);
        AssertThat(AndroidGpuTextureContainer.TryRead(corrupt, expected, out _)).IsFalse();
    }

    /// <summary>Changing and disabling an asset overlay selects its own prepared output or ordinary fallback.</summary>
    [TestCase]
    public void SourceOverlayLifecycleKeepsCpuAndGpuVariantsIndependent()
    {
        byte[] original = { 1, 2, 3 }, modded = { 1, 2, 4 };
        var expected = new AndroidGpuTextureDescriptor(8, 4, true, false);
        byte[] originalPayload = Raw(expected), modPayload = Raw(expected);
        modPayload[0] ^= 0x7f;
        var prepared = new Dictionary<string, byte[]> { [Engine_AndroidGpuTextureCache.Key(original)] = Container(expected, originalPayload) };
        AssertThat(Resolve(prepared, original, expected)).IsEqual(Convert.ToHexString(originalPayload));
        AssertThat(Resolve(prepared, modded, expected)).IsEqual("ordinary-fallback");
        prepared[Engine_AndroidGpuTextureCache.Key(modded)] = Container(expected, modPayload);
        AssertThat(Resolve(prepared, modded, expected)).IsEqual(Convert.ToHexString(modPayload));
        AssertThat(Resolve(prepared, original, expected)).IsEqual(Convert.ToHexString(originalPayload));
        AssertThat(Resolve(prepared, original, expected with { Mipmaps = false })).IsEqual("ordinary-fallback");
    }

    /// <summary>Models the production source-key lookup and strict parse without constructing native images.</summary>
    private static string Resolve(Dictionary<string, byte[]> prepared, byte[] source, AndroidGpuTextureDescriptor expected)
    {
        if (!prepared.TryGetValue(Engine_AndroidGpuTextureCache.Key(source), out byte[]? bytes)) return "ordinary-fallback";
        using var input = new MemoryStream(bytes);
        return AndroidGpuTextureContainer.TryRead(input, expected, out byte[] payload) ? Convert.ToHexString(payload) : "ordinary-fallback";
    }

    /// <summary>Builds scalar codec test data; these arbitrary bytes are never submitted as an ASTC image.</summary>
    private static byte[] Raw(AndroidGpuTextureDescriptor expected)
    {
        byte[] raw = new byte[AndroidGpuTextureContainer.PayloadBytes(expected)];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i * 13);
        return raw;
    }

    /// <summary>Constructs the existing RFTX v4 layout with a true raw-DEFLATE stream.</summary>
    private static byte[] Container(AndroidGpuTextureDescriptor expected, byte[] raw)
    {
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(raw);
        byte[] payload = compressed.ToArray();
        byte[] bytes = new byte[33 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x58544652u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)expected.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)expected.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)Image.Format.Astc4X4);
        bytes[20] = expected.Mipmaps ? (byte)1 : (byte)0;
        bytes[21] = expected.HasAlpha ? (byte)1 : (byte)0;
        bytes[22] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(23), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(25), (uint)AndroidGpuTextureContainer.PayloadBytes(expected));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(29), (uint)payload.Length);
        payload.CopyTo(bytes, 33);
        return bytes;
    }

    /// <summary>Creates a distinct payload for exact-length validation.</summary>
    private static byte[] Append(byte[] source, byte value)
    {
        byte[] result = new byte[source.Length + 1];
        source.CopyTo(result, 0);
        result[^1] = value;
        return result;
    }
}
