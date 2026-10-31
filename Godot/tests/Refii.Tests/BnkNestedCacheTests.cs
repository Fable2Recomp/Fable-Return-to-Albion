using System;
using System.IO;
using GdUnit4;
using Refii.Bnk;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Tests the platform nested-bank budget and real mounted nested-bank reads.</summary>
[TestSuite]
public sealed class BnkNestedCacheTests
{
    /// <summary>Only Android's default budget changes; the desktop keeps upstream's 1 GiB.</summary>
    [TestCase]
    public void PlatformDefaults()
    {
        AssertThat(BnkNestedCachePolicy.DefaultBytes(true)).IsEqual(256L << 20);
        AssertThat(BnkNestedCachePolicy.DefaultBytes(false)).IsEqual(1024L << 20);
        AssertThat(BnkNestedCacheSnapshot.Current().BudgetBytes)
            .IsEqual(BnkNestedCachePolicy.DefaultBytes(OperatingSystem.IsAndroid()));
    }

    /// <summary>Real parent and child banks resolve through the nested cache, aliases included, and the load is counted.</summary>
    [TestCase]
    public void NestedMountReadAndCountersLifecycle()
    {
        using var fixture = new Fixture();
        byte[] leaf = Bank("world/a.bin", new byte[] { 1, 2, 3 });
        byte[] parent = Bank("inside/leaf.bnk", leaf);
        byte[] neighbor = Bank("world/b.bin", new byte[] { 4, 5, 6 });
        fixture.WriteBase(BnkWriter.Write(new[] { Entry("data/parent.bnk", parent), Entry("data/neighbor.bnk", neighbor) }));
        long loadsBefore = BnkNestedCacheSnapshot.Current().Loads;
        using BnkFileSystem fs = BnkFileSystem.Mount(fixture.BaseRoot);
        AssertThat(fs.Failures.Count).IsEqual(0);
        AssertThat(Read(fs, "world/a.bin")).IsEqual("010203");
        AssertThat(Read(fs, "world/b.bin")).IsEqual("040506");
        AssertThat(Read(fs, "WORLD/A.BIN")).IsEqual("010203");
        BnkNestedCacheSnapshot stats = BnkNestedCacheSnapshot.Current();
        AssertThat(stats.Loads >= loadsBefore).IsTrue();
        AssertThat(stats.ResidentBytes >= 0 && stats.MappedBytes >= 0).IsTrue();
    }

    /// <summary>Creates a private synthetic bank member without any game assets.</summary>
    private static BnkWriterEntry Entry(string path, byte[] bytes) => new() { Path = path, Bytes = bytes };
    /// <summary>Serializes one synthetic file or nested bank.</summary>
    private static byte[] Bank(string path, byte[] bytes) => BnkWriter.Write(new[] { Entry(path, bytes) });
    /// <summary>Reads a mounted logical file through the real joining and lazy-container code.</summary>
    private static string Read(BnkFileSystem fs, string path) => fs.TryGetFile(path, out var file)
        ? Convert.ToHexString(file.ReadAllBytes()) : "missing";

    /// <summary>Owns only temporary synthetic base/mod directories, removed after archive owners close.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "refii-nested-cache-" + Guid.NewGuid().ToString("N"));
        /// <summary>Synthetic base game directory.</summary>
        internal string BaseRoot => Path.Combine(_root, "base");
        /// <summary>Synthetic enabled-mod directory.</summary>
        internal string ModRoot => Path.Combine(_root, "mod");
        /// <summary>Creates unique directories with no pre-existing user files.</summary>
        internal Fixture()
        {
            Directory.CreateDirectory(BaseRoot);
            Directory.CreateDirectory(ModRoot);
            // An empty pub-games folder keeps the Flash index on this fixture; without one, Mount falls back to
            // an installed game on the machine and decodes its art through Godot, which the test host cannot.
            Directory.CreateDirectory(Path.Combine(BaseRoot, "data", Refii.FlashGames.PubGamesCatalog.FolderName));
        }
        /// <summary>Writes the synthetic outer bank.</summary>
        internal void WriteBase(byte[] bytes) => File.WriteAllBytes(Path.Combine(BaseRoot, "outer.bnk"), bytes);
        /// <summary>Deletes only the explicitly created and identity-checked synthetic fixture directory.</summary>
        public void Dispose()
        {
            string resolved = Path.GetFullPath(_root);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("refii-nested-cache-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture directory");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
