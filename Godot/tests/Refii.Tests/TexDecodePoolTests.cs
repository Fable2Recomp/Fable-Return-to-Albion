using System;
using System.Buffers;
using System.Buffers.Binary;
using GdUnit4;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>A decode reads and writes pooled buffers: nothing a previous texture left in them may show.</summary>
[TestSuite]
public sealed class TexDecodePoolTests
{
    private const int HeaderBytes = 84;

    /// <summary>A Fable 2 header and one level: two planes of constant fill, or one with the other off.</summary>
    private static byte[] ConstantTexture(int width, int height, int codec, uint plane0, uint plane1)
    {
        var d = new byte[HeaderBytes + 48];
        void Put(int at, int value) => BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(at), value);
        Put(0x00, -2);
        Put(0x04, 48);
        Put(0x10, width);
        Put(0x14, height);
        Put(0x18, 39);
        Put(0x1C, 1);
        Put(0x20, HeaderBytes);
        Put(0x50, codec);
        Put(HeaderBytes, (int)plane0);
        Put(HeaderBytes + 4, 0x7F);
        Put(HeaderBytes + 12, (int)plane1);
        Put(HeaderBytes + 16, 0x7F);
        return d;
    }

    private static void DirtyPool(int bytes)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(bytes);
        rented.AsSpan().Fill(0xCD);
        ArrayPool<byte>.Shared.Return(rented);
    }

    /// <summary>A plane that is off leaves its half of every block zero, whatever the pooled chain held.</summary>
    [TestCase]
    public void ASwitchedOffPlaneStaysZeroInADirtyBuffer()
    {
        DirtyPool(64 * 16);
        AssertThat(Engine_TexData.TryDecode(
            ConstantTexture(32, 32, 2, Engine_TexPlanes.CompConstantFill, 0), out TexSurface surface, out string error)).IsTrue();
        AssertThat(error).IsEqual(string.Empty);
        AssertThat(surface.Data.Length).IsEqual(64 * 16);
        for (int block = 0; block < 64; ++block)
        {
            // Codec 2 writes plane 0 to the block's second half: blue 0x7F as a 565 colour.
            AssertThat(BitConverter.ToUInt64(surface.Data, block * 16)).IsEqual(0UL);
            AssertThat(surface.Data[(block * 16) + 8]).IsEqual((byte)(0x7F >> 3));
        }
    }

    /// <summary>The pooled surface holds the same bytes as the kept one, however long its buffer is.</summary>
    [TestCase]
    public void APooledSurfaceIsTheSameBytesAsAKeptOne()
    {
        byte[] texture = ConstantTexture(32, 32, 2, Engine_TexPlanes.CompConstantFill, Engine_TexPlanes.CompConstantFill);
        AssertThat(Engine_TexData.TryDecode(texture, out TexSurface kept, out _)).IsTrue();
        AssertThat(Engine_TexData.TryDecodePooled(texture, out TexSurface pooled, out _)).IsTrue();
        AssertThat(pooled.Bytes.Length).IsEqual(kept.Data.Length);
        AssertThat(pooled.Bytes.SequenceEqual(kept.Data)).IsTrue();
        Engine_TexData.Release(pooled);
    }

    /// <summary>The BC4 index table agrees with the search it replaced, in every range an image can reach.</summary>
    [TestCase]
    public void Bc4IndicesAreTheNearestPaletteEntry()
    {
        var values = new byte[16];
        Span<int> palette = stackalloc int[8];
        for (int max = 1; max < 256; max += 3)
        {
            for (int min = 0; min < max; min += 5)
            {
                for (int i = 0; i < 16; ++i)
                {
                    values[i] = (byte)(min + ((max - min) * i / 15));
                }

                palette[0] = max;
                palette[1] = min;
                for (int p = 1; p <= 6; ++p)
                {
                    palette[p + 1] = (((7 - p) * max) + (p * min)) / 7;
                }

                ulong block = Engine_BcEncoder.Bc4Block(values, 1);
                for (int i = 0; i < 16; ++i)
                {
                    int best = 0;
                    int bestDistance = int.MaxValue;
                    for (int p = 0; p < 8; ++p)
                    {
                        int distance = Math.Abs(values[i] - palette[p]);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = p;
                        }
                    }

                    AssertThat((int)((block >> (16 + (i * 3))) & 7)).IsEqual(best);
                }
            }
        }
    }

    /// <summary>Shrinking a plane where it lies gives what shrinking it into a new array gives.</summary>
    [TestCase]
    public void DownscaleInPlaceMatchesACopy()
    {
        var random = new Random(11);
        foreach ((int width, int height) in new[] { (1, 1), (1, 9), (9, 1), (8, 8), (7, 5), (32, 16) })
        {
            foreach (int channels in new[] { 1, 4 })
            {
                var source = new byte[width * height * channels];
                random.NextBytes(source);
                byte[] copied = Engine_TexMipGen.Downscale(source, width, height, channels);
                Engine_TexMipGen.Downscale(source, width, height, channels, source);
                AssertThat(source.AsSpan(0, copied.Length).SequenceEqual(copied)).IsTrue();
            }
        }
    }
}
