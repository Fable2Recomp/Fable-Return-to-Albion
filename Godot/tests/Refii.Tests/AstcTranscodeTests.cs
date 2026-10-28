using System;
using Godot;
using GdUnit4;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>BC1/BC3 to ASTC4x4 against a reference BC decode, on synthetic blocks.</summary>
/// <remarks>The ASTC side is read with the transcoder's own test decoder; its bit layout was
/// checked against astcenc (Godot editor) on the game's cached textures when it was written.</remarks>
[TestSuite]
public sealed class AstcTranscodeTests
{
    private static void Expand(int c, Span<int> rgb)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        rgb[0] = (r << 3) | (r >> 2); rgb[1] = (g << 2) | (g >> 4); rgb[2] = (b << 3) | (b >> 2);
    }

    /// <summary>BC1 decoded as D3D does, read as RGB (index 3 of a three-colour block is black; alpha is written opaque here).</summary>
    private static void DecodeColour(ReadOnlySpan<byte> block, bool alwaysFour, Span<byte> rgba)
    {
        int c0 = block[0] | block[1] << 8, c1 = block[2] | block[3] << 8;
        Span<int> e0 = stackalloc int[3], e1 = stackalloc int[3];
        Expand(c0, e0); Expand(c1, e1);
        uint indices = BitConverter.ToUInt32(block[4..]);
        for (int t = 0; t < 16; t++)
        {
            int index = (int)(indices >> (2 * t)) & 3;
            for (int c = 0; c < 3; c++)
            {
                int v = index switch
                {
                    0 => e0[c], 1 => e1[c],
                    2 => alwaysFour || c0 > c1 ? (2 * e0[c] + e1[c] + 1) / 3 : (e0[c] + e1[c]) / 2,
                    _ => alwaysFour || c0 > c1 ? (e0[c] + 2 * e1[c] + 1) / 3 : 0,
                };
                rgba[t * 4 + c] = (byte)v;
            }
            rgba[t * 4 + 3] = 255;
        }
    }

    private static void DecodeAlpha(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        int a0 = block[0], a1 = block[1];
        Span<int> p = stackalloc int[8];
        p[0] = a0; p[1] = a1;
        if (a0 > a1) for (int k = 1; k < 7; k++) p[k + 1] = ((7 - k) * a0 + k * a1 + 3) / 7;
        else { for (int k = 1; k < 5; k++) p[k + 1] = ((5 - k) * a0 + k * a1 + 2) / 5; p[6] = 0; p[7] = 255; }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)block[2 + i] << (8 * i);
        for (int t = 0; t < 16; t++) rgba[t * 4 + 3] = (byte)p[(int)(bits >> (3 * t)) & 7];
    }

    private static (int Colour, int Alpha, double AlphaRms) Compare(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> astc)
    {
        Span<byte> got = stackalloc byte[64];
        Engine_AstcTranscode.DecodeForTests(astc, got);
        int colour = 0, alpha = 0;
        double sum = 0;
        for (int t = 0; t < 16; t++)
        {
            for (int c = 0; c < 3; c++) colour = Math.Max(colour, Math.Abs(reference[t * 4 + c] - got[t * 4 + c]));
            int d = Math.Abs(reference[t * 4 + 3] - got[t * 4 + 3]);
            alpha = Math.Max(alpha, d);
            sum += d * d;
        }
        return (colour, alpha, Math.Sqrt(sum / 16));
    }

    private static byte[] RandomBc1(Random random, bool fourColour, bool useIndex3)
    {
        var block = new byte[8];
        int c0, c1;
        do { c0 = random.Next(65536); c1 = random.Next(65536); } while (c0 == c1);
        if (fourColour != c0 > c1) (c0, c1) = (c1, c0);
        block[0] = (byte)c0; block[1] = (byte)(c0 >> 8); block[2] = (byte)c1; block[3] = (byte)(c1 >> 8);
        uint indices = 0;
        for (int t = 0; t < 16; t++) indices |= (uint)random.Next(useIndex3 ? 4 : 3) << (2 * t);
        BitConverter.TryWriteBytes(block.AsSpan(4), indices);
        return block;
    }

    [TestCase]
    public void FourColourBc1IsWithinTwoLevels()
    {
        var random = new Random(1);
        Span<byte> reference = stackalloc byte[64], astc = stackalloc byte[16];
        for (int i = 0; i < 20000; i++)
        {
            byte[] bc1 = RandomBc1(random, fourColour: true, useIndex3: true);
            DecodeColour(bc1, false, reference);
            Engine_AstcTranscode.Bc1Block(bc1, astc);
            AssertThat(Compare(reference, astc).Colour).IsLessEqual(2);
        }
    }

    [TestCase]
    public void ThreeColourBc1KeepsItsMidpoint()
    {
        var random = new Random(2);
        Span<byte> reference = stackalloc byte[64], astc = stackalloc byte[16];
        for (int i = 0; i < 20000; i++)
        {
            byte[] bc1 = RandomBc1(random, fourColour: false, useIndex3: false);
            DecodeColour(bc1, false, reference);
            Engine_AstcTranscode.Bc1Block(bc1, astc);
            AssertThat(Compare(reference, astc).Colour).IsLessEqual(2);
        }
    }

    /// <summary>Three-colour black is off the endpoint line: the nearest point stands in.</summary>
    [TestCase]
    public void ThreeColourBlackBecomesTheDarkestPoint()
    {
        // c0 = c1 = mid grey; every texel index 3 (black).
        byte[] bc1 = { 0x10, 0x84, 0x10, 0x84, 0xFF, 0xFF, 0xFF, 0xFF };
        Span<byte> got = stackalloc byte[64], astc = stackalloc byte[16];
        Engine_AstcTranscode.Bc1Block(bc1, astc);
        Engine_AstcTranscode.DecodeForTests(astc, got);
        AssertThat((int)got[0]).IsEqual(132);
        AssertThat((int)got[3]).IsEqual(255);
    }

    [TestCase]
    public void Bc3WithUniformAlphaIsExactInAlpha()
    {
        var random = new Random(3);
        Span<byte> reference = stackalloc byte[64], astc = stackalloc byte[16];
        var bc3 = new byte[16];
        for (int i = 0; i < 20000; i++)
        {
            bc3[0] = bc3[1] = (byte)random.Next(256);
            random.NextBytes(bc3.AsSpan(2, 6));
            RandomBc1(random, random.Next(2) == 0, true).CopyTo(bc3, 8);
            // Uniform: a0 == a1 makes every interpolant equal except the 0/255 pair; use index 0 only.
            Array.Clear(bc3, 2, 6);
            DecodeColour(bc3.AsSpan(8), true, reference);
            DecodeAlpha(bc3, reference);
            Engine_AstcTranscode.Bc3Block(bc3, astc);
            (int colour, int alpha, _) = Compare(reference, astc);
            AssertThat(colour).IsLessEqual(2);
            AssertThat(alpha).IsEqual(0);
        }
    }

    [TestCase]
    public void Bc3CutoutAlphaIsExact()
    {
        var random = new Random(4);
        Span<byte> reference = stackalloc byte[64], astc = stackalloc byte[16];
        var bc3 = new byte[16];
        for (int i = 0; i < 20000; i++)
        {
            // a0 <= a1 mode: indices 6 and 7 are 0 and 255.
            bc3[0] = 10; bc3[1] = 20;
            ulong bits = 0;
            for (int t = 0; t < 16; t++) bits |= (ulong)(6 + random.Next(2)) << (3 * t);
            for (int b = 0; b < 6; b++) bc3[2 + b] = (byte)(bits >> (8 * b));
            RandomBc1(random, true, true).CopyTo(bc3, 8);
            DecodeColour(bc3.AsSpan(8), true, reference);
            DecodeAlpha(bc3, reference);
            Engine_AstcTranscode.Bc3Block(bc3, astc);
            (int colour, int alpha, _) = Compare(reference, astc);
            AssertThat(alpha).IsEqual(0);
            AssertThat(colour).IsLessEqual(6);
        }
    }

    /// <summary>Arbitrary BC3 alpha: 2-bit weights against 3-bit indices, bounded on average.</summary>
    [TestCase]
    public void Bc3VaryingAlphaStaysClose()
    {
        var random = new Random(5);
        Span<byte> reference = stackalloc byte[64], astc = stackalloc byte[16];
        var bc3 = new byte[16];
        double total = 0;
        const int blocks = 20000;
        for (int i = 0; i < blocks; i++)
        {
            random.NextBytes(bc3);
            DecodeColour(bc3.AsSpan(8), true, reference);
            DecodeAlpha(bc3, reference);
            Engine_AstcTranscode.Bc3Block(bc3, astc);
            (int colour, _, double rms) = Compare(reference, astc);
            AssertThat(colour).IsLessEqual(6);
            total += rms;
        }
        // Random indices are the worst case; the game's textures measured 33-42 dB.
        AssertThat(total / blocks).IsLess(14.0);
    }

    [TestCase]
    public void WholePayloadsMapBlockForBlock()
    {
        var random = new Random(6);
        var bc1 = new byte[8 * 5];
        random.NextBytes(bc1);
        var astc = new byte[16 * 5];
        AssertThat(Engine_AstcTranscode.Transcode(bc1, false, astc)).IsTrue();
        Span<byte> one = stackalloc byte[16];
        Engine_AstcTranscode.Bc1Block(bc1.AsSpan(16, 8), one);
        AssertThat(one.SequenceEqual(astc.AsSpan(32, 16))).IsTrue();
        AssertThat(Engine_AstcTranscode.Transcode(bc1.AsSpan(1), false, new byte[16 * 4])).IsFalse();
        AssertThat(Engine_AstcTranscode.Transcode(bc1, true, new byte[16 * 5])).IsFalse();
    }

    [TestCase]
    public void PolicyPrefersAstcForUnsupportedBc1AndBc3()
    {
        var policy = AndroidTextureUploadPolicy.Capture(true,
            format => format == RenderingDevice.DataFormat.Astc4X4UnormBlock);
        AssertThat(policy.Target(Image.Format.Dxt1)).IsEqual(Image.Format.Astc4X4);
        AssertThat(policy.Target(Image.Format.Dxt5)).IsEqual(Image.Format.Astc4X4);
        AssertThat(policy.Target(Image.Format.Dxt3)).IsEqual(Image.Format.Rgba8);
        AssertThat(policy.Target(Image.Format.RgtcRg)).IsEqual(Image.Format.Rg8);
        var off = AndroidTextureUploadPolicy.Capture(true,
            format => format == RenderingDevice.DataFormat.Astc4X4UnormBlock, transcodeAstc: false);
        AssertThat(off.Target(Image.Format.Dxt1)).IsEqual(Image.Format.Rgba8);
        AssertThat(off.Target(Image.Format.Dxt5)).IsEqual(Image.Format.Rgba8);
    }
}
