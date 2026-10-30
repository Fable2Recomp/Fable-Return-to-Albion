namespace Refii.Tests.Level;

using System;
using System.IO;
using System.IO.Compression;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Level;

/// <summary>
/// The .ghf cell: one header and one 14-byte stride for both titles, the height at +0 in Fable 2's
/// files and at +8 in Fable 3's. Each field is built here in its title's layout, with the other
/// title's height slot holding a value that is not a height, so reading the wrong slot fails.
/// </summary>
[TestSuite]
public class GhfCellLayoutTests
{
    private static byte[] Field(int heightOffset, float[] heights, int width, int height)
    {
        var raw = new byte[Engine_GhfTerrain.HeaderSize + (heights.Length * Engine_GhfTerrain.CellStride)];
        WriteU32(raw, 0x0C, (uint)width);
        WriteU32(raw, 0x10, (uint)height);
        for (int i = 0; i < heights.Length; i++)
        {
            int cell = Engine_GhfTerrain.HeaderSize + (i * Engine_GhfTerrain.CellStride);
            // The slot this layout does not use holds a NaN pattern, as Fable 3's leading fields can.
            WriteU32(raw, cell + (8 - heightOffset), 0xFFC00000u);
            WriteU32(raw, cell + heightOffset, BitConverter.SingleToUInt32Bits(heights[i]));
        }

        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(raw);
        }

        return packed.ToArray();
    }

    private static void WriteU32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    [TestCase]
    public void EachTitleReadsItsOwnHeightSlot()
    {
        float[] heights = { 3f, 107.4f, 12.5f, 117.83f };
        foreach (Engine_Title title in new[] { Engine_Title.Fable2, Engine_Title.Fable3 })
        {
            byte[] field = Field(Engine_GhfTerrain.HeightOffsetFor(title), heights, 2, 2);

            AssertThat(Engine_GhfTerrain.TryDecode(field, title, out GhfHeights decoded, out string error)).IsTrue();
            AssertThat(error).IsEqual(string.Empty);
            AssertThat(decoded.Width).IsEqual(2);
            AssertThat(decoded.Height).IsEqual(2);
            AssertThat(decoded.Values).ContainsExactly(heights);
            AssertThat(decoded.MinHeight).IsEqual(3f);
            AssertThat(decoded.MaxHeight).IsEqual(117.83f);
        }
    }

    [TestCase]
    public void TheOtherTitlesLayoutIsRefused()
    {
        float[] heights = { 3f, 4f, 5f, 6f };
        byte[] fable3 = Field(Engine_GhfTerrain.HeightOffsetFor(Engine_Title.Fable3), heights, 2, 2);

        AssertThat(Engine_GhfTerrain.TryDecode(fable3, Engine_Title.Fable2, out _, out string error)).IsFalse();
        AssertThat(error).Contains("cell 0");
    }
}
