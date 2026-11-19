namespace Refii.Tests;

using System;
using System.Buffers.Binary;
using System.IO;
using GdUnit4;
using Refii.Bnk;
using Refii.Textures;
using static GdUnit4.Assertions;

/// <summary>
/// Regression tests for bare-chain raw Xbox 360 mips (<c>compFlag == 7</c>):
/// 16-bit word byte-swapping in <see cref="Engine_TexData"/> (eliminating false DXT1 punch-through
/// alpha promotions), full BC3 block sizing, BC4 de-interleaving from BC5 pairs, and the
/// <see cref="Engine_TexCache.ConverterVersion"/> cache epoch bump.
/// Pure .NET: no Godot type reaches the test host.
/// </summary>
[TestSuite]
public sealed class TexRawMipEndianTests
{
    private static void WriteBe32(byte[] buf, int at, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at, 4), value);

    private static void WriteBe16(byte[] buf, int at, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(at, 2), value);

    /// <summary>
    /// Builds a two-record bare-chain <c>.tex</c> whose mip 0 is non-decodable <c>compFlag == 1</c>
    /// (supplying <paramref name="width"/> x <paramref name="height"/>) so
    /// <see cref="Engine_TexData.TryDecode"/> falls through to <c>TryDecodeSingle</c> on mip 1
    /// (<c>compFlag == 7</c>).
    /// </summary>
    private static byte[] BuildBareChainSingleRawMip(int width, int height, byte[] rawMipBytes)
    {
        const int recordSize = 48;
        const int mip0BodySize = 444;
        int mip1Offset = recordSize + mip0BodySize;
        var buf = new byte[mip1Offset + recordSize + rawMipBytes.Length];

        // Mip 0: compFlag = 1, bodyOffset = 48, bodySize = 444, HasSize = true
        WriteBe32(buf, 0, 1u);
        WriteBe32(buf, 4, (uint)recordSize);
        WriteBe32(buf, 8, (uint)mip0BodySize);
        WriteBe16(buf, recordSize, (ushort)width);
        WriteBe16(buf, recordSize + 2, (ushort)height);

        // Mip 1: compFlag = 7 (raw Xbox 360 mip)
        WriteBe32(buf, mip1Offset + 0, 7u);
        WriteBe32(buf, mip1Offset + 4, (uint)recordSize);
        WriteBe32(buf, mip1Offset + 8, (uint)rawMipBytes.Length);
        Buffer.BlockCopy(rawMipBytes, 0, buf, mip1Offset + recordSize, rawMipBytes.Length);

        return buf;
    }

    [TestCase]
    public void ConverterVersionIsBumpedForCompFlag7EndianFix()
    {
        AssertThat(Engine_TexCache.ConverterVersion).IsEqual(4);
    }

    [TestCase]
    public void TryDecodeSingleBc1CompFlag7SwapsBigEndianWordsAndClearsFalsePunchThrough()
    {
        // 8x8 -> 2x2 = 4 BC1 blocks = 32 bytes (< bc3 = 64 bytes).
        // Each block carries big-endian words whose unswapped LE interpretation has c0 (0x00FF) <= c1 (0x2000)
        // with index bits == 3 (triggering false punch-through without SwapWords16), whereas swapped
        // LE has c0 (0xFF00) > c1 (0x0020) (opaque 4-color mode).
        byte[] rawBlocks = new byte[32];
        for (int b = 0; b < 4; ++b)
        {
            int o = b * 8;
            rawBlocks[o + 0] = 0xFF; // c0 BE = 0xFF00 (unswapped LE = 0x00FF)
            rawBlocks[o + 1] = 0x00;
            rawBlocks[o + 2] = 0x00; // c1 BE = 0x0020 (unswapped LE = 0x2000)
            rawBlocks[o + 3] = 0x20;
            rawBlocks[o + 4] = 0xFF; // selector word 0 BE
            rawBlocks[o + 5] = 0xAA;
            rawBlocks[o + 6] = 0xFF; // selector word 1 BE
            rawBlocks[o + 7] = 0x55;
        }

        AssertThat(Engine_TexImage.Bc1HasPunchThrough(rawBlocks)).IsTrue();

        byte[] texBytes = BuildBareChainSingleRawMip(8, 8, rawBlocks);
        AssertThat(Engine_TexData.TryDecode(texBytes, out TexSurface surface, out _)).IsTrue();
        AssertThat(surface.Format).IsEqual(TexSurfaceFormat.Bc1);
        AssertThat(surface.Width).IsEqual(8);
        AssertThat(surface.Height).IsEqual(8);
        AssertThat(surface.Data.Length).IsEqual(32);

        // Verify 16-bit word swaps and no false punch-through.
        AssertThat(surface.Data[0]).IsEqual((byte)0x00);
        AssertThat(surface.Data[1]).IsEqual((byte)0xFF);
        AssertThat(surface.Data[2]).IsEqual((byte)0x20);
        AssertThat(surface.Data[3]).IsEqual((byte)0x00);
        AssertThat(surface.Data[4]).IsEqual((byte)0xAA);
        AssertThat(surface.Data[5]).IsEqual((byte)0xFF);
        AssertThat(surface.Data[6]).IsEqual((byte)0x55);
        AssertThat(surface.Data[7]).IsEqual((byte)0xFF);
        AssertThat(Engine_TexImage.Bc1HasPunchThrough(surface.Data)).IsFalse();
    }

    [TestCase]
    public void TryDecodeSingleBc3CompFlag7PreservesFull16ByteBlocksAndSwapsWords()
    {
        // 8x8 -> 2x2 = 4 BC3 blocks = 64 bytes.
        byte[] rawBlocks = new byte[64];
        for (int i = 0; i < rawBlocks.Length; i += 2)
        {
            rawBlocks[i] = (byte)(0x80 + (i & 0x3F));
            rawBlocks[i + 1] = (byte)(0x10 + (i & 0x1F));
        }

        byte[] texBytes = BuildBareChainSingleRawMip(8, 8, rawBlocks);
        AssertThat(Engine_TexData.TryDecode(texBytes, out TexSurface surface, out _)).IsTrue();
        AssertThat(surface.Format).IsEqual(TexSurfaceFormat.Bc3);
        AssertThat(surface.Data.Length).IsEqual(64);
        AssertThat(surface.Data[0]).IsEqual(rawBlocks[1]);
        AssertThat(surface.Data[1]).IsEqual(rawBlocks[0]);
        AssertThat(surface.Data[62]).IsEqual(rawBlocks[63]);
        AssertThat(surface.Data[63]).IsEqual(rawBlocks[62]);
    }

    [TestCase]
    public void ShippedTexturesWithCompFlag7TailsHaveZeroFalseBc1PunchThroughAndDecodeBc3AndBc4Cleanly()
    {
        const string devGlobalsTex = @"C:\Users\Miki\Documents\fable\Fable Return to Albion\assets\Fable2\game\data\globals\globals_textures.bnk";
        if (!File.Exists(devGlobalsTex))
        {
            Console.WriteLine("ShippedTexturesWithCompFlag7TailsHaveZeroFalseBc1PunchThroughAndDecodeBc3AndBc4Cleanly: globals_textures.bnk not present; skipped");
            return;
        }

        AssertThat(BnkArchive.TryOpen(devGlobalsTex, out BnkArchive? bnk, out string bnkErr)).IsTrue();
        AssertThat(bnkErr).IsEqual(string.Empty);
        if (bnk is null)
        {
            return;
        }

        using (bnk)
        {
            byte[] ReadTex(string relPath)
            {
                foreach (BnkEntry entry in bnk.Entries)
                {
                    if (entry.RelativePath.Equals(relPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return bnk.ReadEntryBytes(entry);
                    }
                }

                throw new FileNotFoundException(relPath);
            }

            // 1. Shard groundglow BC1 texture previously had 54 false punch-through holes in mip 3 (compFlag == 7).
            byte[] glowBytes = ReadTex("Art/FX/PFX_Textures/Decals/FXTD_Shard_Groundglow_01.tex");
            AssertThat(Engine_TexData.TryDecode(glowBytes, out TexSurface glow, out _)).IsTrue();
            AssertThat(glow.Format).IsEqual(TexSurfaceFormat.Bc1);
            AssertThat(glow.MipLevels).IsGreaterEqual(5);
            AssertThat(Engine_TexImage.Bc1HasPunchThrough(glow.Data)).IsFalse();

            // 2. BC3 hair texture with compFlag == 7 32x32 + 16x16 mips keeps full 16-byte blocks for mips 4 & 5.
            byte[] hairBytes = ReadTex("Art/Characters/Heros/DLC/Hair/Pictures/CH_HeroHair_Female_glamour/CH_HeroHair_Female_glamour.tex");
            AssertThat(Engine_TexData.TryDecode(hairBytes, out TexSurface hair, out _)).IsTrue();
            AssertThat(hair.Format).IsEqual(TexSurfaceFormat.Bc3);
            AssertThat(hair.MipLevels).IsEqual(6);

            // 3. BC4 normal map with double-size compFlag == 7 tail de-interleaves plane 0 cleanly.
            byte[] normBytes = ReadTex("Art/Characters/Npc/Specific Npc/Scripted/Pictures/CH_OldGreg_head_01/NORM_OldGreg_head_01.tex");
            AssertThat(Engine_TexData.TryDecode(normBytes, out TexSurface norm, out _)).IsTrue();
            AssertThat(norm.Format).IsEqual(TexSurfaceFormat.Bc4);
            AssertThat(norm.MipLevels).IsGreaterEqual(6);
        }
    }
}
