using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GdUnit4;
using Godot;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// <see cref="Engine_ImageBytes"/> holds the image's own buffer, not a copy. It must read as
/// <see cref="Image.GetData"/> does, stay as it was for as long as it is held, and let go when disposed.
/// </summary>
[TestSuite]
public sealed class ImageBytesTests
{
    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static Image Rgba(int side, int seed) =>
        Image.CreateFromData(side, side, false, Image.Format.Rgba8, Bytes(side * side * 4, seed));

    private static long AddressOf(ReadOnlySpan<byte> span) =>
        (long)Unsafe.ByteOffset(ref Unsafe.NullRef<byte>(), ref MemoryMarshal.GetReference(span));

    [TestCase]
    [RequireGodotRuntime]
    public void ReadsAsGetDataDoes()
    {
        foreach (Image image in new[]
                 {
                     Rgba(16, 1),
                     Rgba(1, 2),
                     Image.CreateFromData(8, 8, true, Image.Format.Dxt1, Bytes(8 * 7, 3)),
                     Image.CreateFromData(8, 8, true, Image.Format.Dxt5, Bytes(16 * 7, 4)),
                     Image.CreateFromData(4, 4, false, Image.Format.RgtcRg, Bytes(16, 5)),
                 })
        {
            byte[] expected = image.GetData();
            using var bytes = new Engine_ImageBytes(image);
            AssertThat(bytes.Span.Length).IsEqual(expected.Length);
            AssertThat(bytes.Span.SequenceEqual(expected)).IsTrue();
            image.Dispose();
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AnEmptyImageHasNoBytes()
    {
        using var image = new Image();
        AssertThat(image.GetData().Length).IsEqual(0);
        var bytes = new Engine_ImageBytes(image);
        AssertThat(bytes.Span.Length).IsEqual(0);
        AssertThat(bytes.Span.IsEmpty).IsTrue();
        bytes.Dispose();
        AssertThat(image.IsEmpty()).IsTrue();
    }

    /// <summary>The image's next write takes its own copy; what was handed out does not change.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void TheBytesDoNotChangeWhenTheImageIsWritten()
    {
        using Image image = Rgba(16, 6);
        byte[] before = image.GetData();
        using var bytes = new Engine_ImageBytes(image);

        image.SetPixel(0, 0, new Color(1, 0, 1, 1));
        image.SetPixel(15, 15, new Color(0, 1, 0, 0));
        byte[] pixelsWritten = image.GetData();
        AssertThat(pixelsWritten.AsSpan().SequenceEqual(before)).IsFalse();
        AssertThat(bytes.Span.SequenceEqual(before)).IsTrue();

        image.Fill(new Color(0.5f, 0.5f, 0.5f, 0.5f));
        AssertThat(bytes.Span.SequenceEqual(before)).IsTrue();

        image.Resize(4, 4);
        AssertThat(bytes.Span.SequenceEqual(before)).IsTrue();

        image.SetData(2, 2, false, Image.Format.Rgba8, new byte[16]);
        AssertThat(bytes.Span.SequenceEqual(before)).IsTrue();
        AssertThat(image.GetData().Length).IsEqual(16);
    }

    /// <summary>Holding the bytes keeps the buffer alive past the image, with new images allocated
    /// over whatever would have been freed.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void TheBytesOutliveTheImage()
    {
        Image image = Rgba(64, 7);
        byte[] before = image.GetData();
        using var bytes = new Engine_ImageBytes(image);
        image.Dispose();

        for (int i = 0; i < 16; i++)
        {
            using Image other = Rgba(64, 100 + i);
            AssertThat(bytes.Span.SequenceEqual(before)).IsTrue();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        AssertThat(bytes.Span.SequenceEqual(before)).IsTrue();
    }

    /// <summary>Is the point of the type: it is the image's buffer, and two of them are one buffer.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void ItIsNotACopy()
    {
        using Image image = Rgba(1024, 8);
        long before = GC.GetAllocatedBytesForCurrentThread();
        using var one = new Engine_ImageBytes(image);
        using var two = new Engine_ImageBytes(image);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        AssertThat(AddressOf(one.Span)).IsEqual(AddressOf(two.Span));
        // The image is 4 MB. The marshalling around the call is a few small objects at most.
        AssertThat(allocated).IsLess(64 * 1024L);
    }

    /// <summary>Dispose gives the reference back: with nobody else holding the buffer the image writes
    /// in place, where a leaked reference would have made it copy.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void DisposeLetsGo()
    {
        using Image image = Rgba(64, 9);
        // Writing once first, so the image is the only owner of its buffer whatever made it.
        image.SetPixel(1, 1, new Color(1, 1, 1, 1));

        long first;
        using (var bytes = new Engine_ImageBytes(image))
            first = AddressOf(bytes.Span);

        image.SetPixel(2, 2, new Color(0, 0, 0, 1));
        long afterWrite;
        using (var bytes = new Engine_ImageBytes(image))
            afterWrite = AddressOf(bytes.Span);
        AssertThat(afterWrite).IsEqual(first);

        // And the same check does tell: held across a write, the image moves to a copy.
        var held = new Engine_ImageBytes(image);
        long heldAt = AddressOf(held.Span);
        image.SetPixel(3, 3, new Color(0, 0, 0, 1));
        long moved;
        using (var bytes = new Engine_ImageBytes(image))
            moved = AddressOf(bytes.Span);
        AssertThat(moved == heldAt).IsFalse();
        held.Dispose();
    }

    private static Image? Upload(string method, Image image, params object[] rest)
    {
        object[] args = new object[rest.Length + 1];
        args[0] = image;
        rest.CopyTo(args, 1);
        return (Image?)typeof(AndroidTextureUpload)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args);
    }

    /// <summary>The two upload paths that read through it give what they did from GetData's array.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void TheTranscodePathGivesWhatGetDataGave()
    {
        foreach ((Image.Format format, int size, bool bc3) in new[]
                 {
                     (Image.Format.Dxt1, 8, false),
                     (Image.Format.Dxt5, 16, true),
                 })
        {
            using Image image = Image.CreateFromData(8, 8, true, format, Bytes(size * 7, 10 + size));
            var astc = new byte[16 * 7];
            AssertThat(Engine_AstcTranscode.Transcode(image.GetData(), bc3, astc)).IsTrue();

            using Image? result = Upload("Transcode", image, bc3);
            AssertThat(result is null).IsFalse();
            AssertThat(result!.GetFormat()).IsEqual(Image.Format.Astc4X4);
            AssertThat(result.GetWidth()).IsEqual(8);
            AssertThat(result.HasMipmaps()).IsTrue();
            AssertThat(Convert.ToHexString(result.GetData())).IsEqual(Convert.ToHexString(astc));
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void DroppingTheTopMipGivesWhatGetDataGave()
    {
        // BC5, 16 bytes a block: 8x8 is four blocks, then 4x4, 2x2 and 1x1 at one block each.
        byte[] data = Bytes(16 * 7, 20);
        using Image image = Image.CreateFromData(8, 8, true, Image.Format.RgtcRg, data);
        int offset = (int)image.GetMipmapOffset(1);
        AssertThat(offset).IsEqual(16 * 4);

        using Image? result = Upload("WithoutTopMip", image);
        AssertThat(result is null).IsFalse();
        AssertThat(result!.GetWidth()).IsEqual(4);
        AssertThat(result.GetHeight()).IsEqual(4);
        AssertThat(result.GetFormat()).IsEqual(Image.Format.RgtcRg);
        AssertThat(Convert.ToHexString(result.GetData())).IsEqual(Convert.ToHexString(data.AsSpan(offset)));
    }
}
