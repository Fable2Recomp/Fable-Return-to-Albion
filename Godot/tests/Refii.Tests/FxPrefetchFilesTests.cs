using System;
using GdUnit4;
using Refii.Fx;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The low-memory tier's effect prefetch files: named by source and build, and only other builds' purged.</summary>
[TestSuite]
public sealed class FxPrefetchFilesTests
{
    private static byte[] Hash(byte seed)
    {
        var hash = new byte[32];
        for (int i = 0; i < hash.Length; i++) hash[i] = (byte)(seed + i);
        return hash;
    }

    /// <summary>The name carries the source hash, format, size, mipmaps and build.</summary>
    [TestCase]
    public void NamesBySourceAndBuild()
    {
        AssertThat(Engine_FxPrefetchFiles.FileName(Hash(0), 17, 256, 128, true, "ab12cd34"))
            .IsEqual("fx_000102030405060708090a0b0c0d0e0f_17_256x128m_ab12cd34.bin");
        AssertThat(Engine_FxPrefetchFiles.FileName(Hash(0), 17, 256, 128, false, "ab12cd34"))
            .IsEqual("fx_000102030405060708090a0b0c0d0e0f_17_256x128_ab12cd34.bin");
        AssertThat(Engine_FxPrefetchFiles.FileName(Hash(1), 17, 256, 128, true, "ab12cd34"))
            .IsNotEqual(Engine_FxPrefetchFiles.FileName(Hash(0), 17, 256, 128, true, "ab12cd34"));
    }

    /// <summary>Another build's files and temporaries go; this build's, and anything not a prefetch file, stay.</summary>
    [TestCase]
    public void PurgesOnlyOtherBuilds()
    {
        string mine = Engine_FxPrefetchFiles.FileName(Hash(0), 19, 64, 64, true, "ab12cd34");
        string theirs = Engine_FxPrefetchFiles.FileName(Hash(0), 19, 64, 64, true, "99999999");
        AssertThat(Engine_FxPrefetchFiles.IsStale(mine, "ab12cd34")).IsFalse();
        AssertThat(Engine_FxPrefetchFiles.IsStale(mine + "." + Guid.NewGuid().ToString("N") + ".tmp", "ab12cd34")).IsFalse();
        AssertThat(Engine_FxPrefetchFiles.IsStale(theirs, "ab12cd34")).IsTrue();
        AssertThat(Engine_FxPrefetchFiles.IsStale(theirs + "." + Guid.NewGuid().ToString("N") + ".tmp", "ab12cd34")).IsTrue();
        AssertThat(Engine_FxPrefetchFiles.IsStale("astc_00_19_64x64_99999999.bin", "ab12cd34")).IsFalse();
    }
}
