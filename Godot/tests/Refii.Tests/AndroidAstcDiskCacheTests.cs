using System;
using System.Security.Cryptography;
using GdUnit4;
using Godot;
using Refii.Textures;
using Refii.Titles.Fable2.PubGames;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The names of the boards' transcoded images, which must not change with how they are hashed.</summary>
[TestSuite]
public sealed class AndroidAstcDiskCacheTests
{
    private const string Stamp = "53b40db6";

    /// <summary>The name hashed from the image's own buffer is the name hashed from a copy of its
    /// data, which is the name on disk: SHA-1, first eight bytes, lower-case hex.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void FileNameIsWhatItWasWithACopy()
    {
        var payload = new byte[8 * 4];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 11 + 3);
        using Image image = Image.CreateFromData(16, 4, false, Image.Format.Dxt1, payload);

        string name = AndroidAstcDiskCache.FileName(image, Stamp, Engine_SwfAssets.Digest);

        string copied = Convert.ToHexString(SHA1.HashData(image.GetData()), 0, 8).ToLowerInvariant();
        AssertThat(name).IsEqual($"astc_{copied}_Dxt1_16x4_{Stamp}.bin");
        // Independently of .NET: Python's hashlib over the same bytes.
        AssertThat(copied).IsEqual("d4f3beb00fe3a04e");
    }

    /// <summary>A mipmapped image's name says so, and its whole chain is hashed.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void FileNameCoversTheMipChain()
    {
        // 8x8 BC1: four blocks, one, one, one: 7 x 8 = 56 bytes.
        var payload = new byte[56];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 5);
        using Image image = Image.CreateFromData(8, 8, true, Image.Format.Dxt1, payload);

        string name = AndroidAstcDiskCache.FileName(image, Stamp, Engine_SwfAssets.Digest);

        string copied = Convert.ToHexString(SHA1.HashData(image.GetData()), 0, 8).ToLowerInvariant();
        AssertThat(image.GetData().Length).IsEqual(56);
        AssertThat(name).IsEqual($"astc_{copied}_Dxt1_8x8m_{Stamp}.bin");
    }
}
