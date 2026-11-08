using System;
using System.IO;
using GdUnit4;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public class KeyedFileCacheTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "refii-keyed-" + Guid.NewGuid().ToString("N"), "carry.bin");

    [TestCase]
    public void TestTheBytesComeBackForTheSameInputOnly()
    {
        string path = TempPath();
        try
        {
            byte[] key = Engine_KeyedFileCache.Key(1, typeof(KeyedFileCacheTests), new byte[]?[] { new byte[] { 1, 2, 3 }, null });
            byte[] patch = { 9, 8, 7, 6 };
            Engine_KeyedFileCache.Write(path, key, patch);
            AssertThat(Engine_KeyedFileCache.TryRead(path, key, out byte[] read)).IsTrue();
            AssertThat(read).IsEqual(patch);

            byte[] other = Engine_KeyedFileCache.Key(1, typeof(KeyedFileCacheTests), new byte[]?[] { new byte[] { 1, 2, 4 }, null });
            AssertThat(other).IsNotEqual(key);
            AssertThat(Engine_KeyedFileCache.TryRead(path, other, out _)).IsFalse();
            AssertThat(Engine_KeyedFileCache.Key(2, typeof(KeyedFileCacheTests), new byte[]?[] { new byte[] { 1, 2, 3 }, null })).IsNotEqual(key);
            // A blob that is missing is not one that is empty.
            AssertThat(Engine_KeyedFileCache.Key(1, typeof(KeyedFileCacheTests), new byte[]?[] { new byte[] { 1, 2, 3 }, Array.Empty<byte>() })).IsNotEqual(key);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [TestCase]
    public void TestDamagedBytesAreMadeAgain()
    {
        string path = TempPath();
        try
        {
            byte[] key = Engine_KeyedFileCache.Key(1, typeof(KeyedFileCacheTests), new byte[]?[] { new byte[] { 5 } });
            Engine_KeyedFileCache.Write(path, key, new byte[] { 1, 2, 3, 4 });
            byte[] file = File.ReadAllBytes(path);
            file[^1] ^= 0xFF;
            File.WriteAllBytes(path, file);
            AssertThat(Engine_KeyedFileCache.TryRead(path, key, out _)).IsFalse();
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }
}
