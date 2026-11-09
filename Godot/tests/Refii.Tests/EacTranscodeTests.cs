using System;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>BC5 normal maps rewritten as EAC RG11 for GPUs that sample no BC format.</summary>
[TestSuite]
public sealed class EacTranscodeTests
{
    /// <summary>Largest difference from the BC4 source, as 8-bit steps, across a channel.</summary>
    private static double WorstError(byte[] bc4)
    {
        Span<byte> source = stackalloc byte[16];
        Span<byte> eac = stackalloc byte[8];
        Span<int> decoded = stackalloc int[16];
        Engine_EacTranscode.DecodeBc4ForTests(bc4, source);
        Engine_EacTranscode.Channel(bc4, eac);
        Engine_EacTranscode.DecodeForTests(eac, decoded);
        double worst = 0;
        for (int i = 0; i < 16; i++) worst = Math.Max(worst, Math.Abs(decoded[i] * 255.0 / 2047.0 - source[i]));
        return worst;
    }

    /// <summary>A flat block stays flat, a smooth one close: the common cases in a normal map.</summary>
    [TestCase]
    public void ChannelsStayCloseToTheSource()
    {
        AssertThat(WorstError(new byte[] { 128, 128, 0, 0, 0, 0, 0, 0 })).IsLess(1.5);
        // Endpoints 200 and 90, indices 0-7 in order: a ramp across the block.
        AssertThat(WorstError(new byte[] { 200, 90, 0b10001000, 0b11000110, 0b11111010, 0b10001000, 0b11000110, 0b11111010 })).IsLess(14.0);
        AssertThat(WorstError(new byte[] { 140, 120, 0b01010101, 0b01001001, 0b10010010, 0b00100100, 0b01001001, 0b10010010 })).IsLess(6.0);
    }

    /// <summary>A whole payload converts two blocks at a time; anything else is refused.</summary>
    [TestCase]
    public void TranscodeNeedsWholeBlocks()
    {
        AssertThat(Engine_EacTranscode.Transcode(new byte[16], new byte[16])).IsTrue();
        AssertThat(Engine_EacTranscode.Transcode(new byte[15], new byte[16])).IsFalse();
        AssertThat(Engine_EacTranscode.Transcode(new byte[16], new byte[8])).IsFalse();
    }

    /// <summary>Only a GPU without BC5 that has EAC, and only with the setting on, gets EAC normals.</summary>
    [TestCase]
    public void PolicyPicksEacOnlyWhenAskedAndAvailable()
    {
        AndroidTextureUploadPolicy Policy(bool on, bool eac, bool bc5) => AndroidTextureUploadPolicy.Capture(true,
            format => format == RenderingDevice.DataFormat.EacR11G11UnormBlock ? eac
                : format == RenderingDevice.DataFormat.Bc5UnormBlock && bc5, transcodeEac: on);
        AssertThat(Policy(true, true, false).Target(Image.Format.RgtcRg)).IsEqual(Image.Format.Etc2Rg11);
        AssertThat(Policy(false, true, false).Target(Image.Format.RgtcRg)).IsEqual(Image.Format.Rg8);
        AssertThat(Policy(true, false, false).Target(Image.Format.RgtcRg)).IsEqual(Image.Format.Rg8);
        AssertThat(Policy(true, true, true).Target(Image.Format.RgtcRg)).IsEqual(Image.Format.RgtcRg);
    }
}
