using System;
using System.IO;
using GdUnit4;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The on-disk record of a prepared (ASTC) upload: read back whole, or a miss.</summary>
[TestSuite]
public sealed class PreparedImageFileTests
{
    private static byte[] Payload()
    {
        var data = new byte[16 * 5];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7);
        return data;
    }

    private static string TempPath() =>
        Path.Combine(Refii.Tests.TestTemp.Root, $"refii-prepared-{Guid.NewGuid():N}", "a.bin");

    /// <summary>What was written is what is read, and no temporary file is left beside it.</summary>
    [TestCase]
    public void RoundTripsAndLeavesOnlyTheFile()
    {
        string path = TempPath();
        try
        {
            AssertThat(Engine_PreparedImageFile.TryWrite(path, 8, 4, 22, true, Payload())).IsTrue();
            AssertThat(Engine_PreparedImageFile.TryRead(path, 8, 4, 22, true)).IsEqual(Payload());
            AssertThat(Directory.GetFiles(Path.GetDirectoryName(path)!).Length).IsEqual(1);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    /// <summary>The pooled read takes the record's own description, and a short or absent file is a miss.</summary>
    [TestCase]
    public void PooledReadDescribesTheRecord()
    {
        string path = TempPath();
        try
        {
            AssertThat(Engine_PreparedImageFile.TryReadPooled(path, out _, out _, out _, out _, out _, out _)).IsFalse();
            AssertThat(Engine_PreparedImageFile.TryWrite(path, 8, 4, 22, true, Payload())).IsTrue();
            AssertThat(Engine_PreparedImageFile.TryReadPooled(path, out int width, out int height, out int format,
                out bool mipmaps, out byte[] payload, out int length)).IsTrue();
            AssertThat((width, height, format, mipmaps)).IsEqual((8, 4, 22, true));
            AssertThat(payload[..length]).IsEqual(Payload());
            System.Buffers.ArrayPool<byte>.Shared.Return(payload);

            byte[] whole = File.ReadAllBytes(path);
            File.WriteAllBytes(path, whole[..^1]);
            AssertThat(Engine_PreparedImageFile.TryReadPooled(path, out _, out _, out _, out _, out _, out _)).IsFalse();
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    /// <summary>A record of another image, a truncated one, or none at all is a miss.</summary>
    [TestCase]
    public void MismatchesAndDamageAreMisses()
    {
        var stream = new MemoryStream();
        Engine_PreparedImageFile.Write(stream, 8, 4, 22, true, Payload());
        byte[] whole = stream.ToArray();

        AssertThat(Engine_PreparedImageFile.TryRead(new MemoryStream(whole), 8, 4, 22, true)).IsEqual(Payload());
        AssertThat(Engine_PreparedImageFile.TryRead(new MemoryStream(whole), 4, 4, 22, true)).IsNull();
        AssertThat(Engine_PreparedImageFile.TryRead(new MemoryStream(whole), 8, 8, 22, true)).IsNull();
        AssertThat(Engine_PreparedImageFile.TryRead(new MemoryStream(whole), 8, 4, 21, true)).IsNull();
        AssertThat(Engine_PreparedImageFile.TryRead(new MemoryStream(whole), 8, 4, 22, false)).IsNull();
        AssertThat(Engine_PreparedImageFile.TryRead(new MemoryStream(whole[..^1]), 8, 4, 22, true)).IsNull();
        AssertThat(Engine_PreparedImageFile.TryRead(new MemoryStream(whole[..10]), 8, 4, 22, true)).IsNull();
        byte[] badMagic = (byte[])whole.Clone();
        badMagic[0] ^= 0xFF;
        AssertThat(Engine_PreparedImageFile.TryRead(new MemoryStream(badMagic), 8, 4, 22, true)).IsNull();

        // The right length with the data lost, as a power cut can leave it.
        byte[] zeroed = (byte[])whole.Clone();
        Array.Clear(zeroed, zeroed.Length - Payload().Length, Payload().Length);
        AssertThat(Engine_PreparedImageFile.TryRead(new MemoryStream(zeroed), 8, 4, 22, true)).IsNull();
        string path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, zeroed);
            AssertThat(Engine_PreparedImageFile.TryReadPooled(path, out _, out _, out _, out _, out _, out _)).IsFalse();
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
        AssertThat(Engine_PreparedImageFile.TryRead(TempPath(), 8, 4, 22, true)).IsNull();
    }
}
