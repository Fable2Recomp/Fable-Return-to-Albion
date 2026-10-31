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
        Path.Combine(Path.GetTempPath(), $"refii-prepared-{Guid.NewGuid():N}", "a.bin");

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
        AssertThat(Engine_PreparedImageFile.TryRead(TempPath(), 8, 4, 22, true)).IsNull();
    }
}
