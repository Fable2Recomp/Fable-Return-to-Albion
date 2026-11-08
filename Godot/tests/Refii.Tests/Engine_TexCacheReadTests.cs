using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Refii.Core;
using Refii.Textures;
using static GdUnit4.Assertions;
using FileAccess = System.IO.FileAccess;

namespace Refii.Tests;

/// <summary>
/// <see cref="Engine_TexCache"/>'s read of its own container: what <c>Store</c> wrote comes back byte
/// for byte, and an entry that cannot be used is a miss, whatever is wrong with it. The cache is
/// regenerable, so nothing in a file on disk may be able to throw out of a load.
/// </summary>
/// <remarks>In Godot's runtime, since faces are <see cref="Image"/>s. Everything happens under a
/// temporary app root.</remarks>
[TestSuite]
public sealed class Engine_TexCacheReadTests
{
    private const int MagicAt = 0, VersionAt = 4, WidthAt = 8, HeightAt = 12, FormatAt = 16;
    private const int FacesAt = 23, LengthAt = 25, DeflatedAt = 29, HeaderSize = 33;
    private const string Key = "ab34567890abcdef";

    /// <summary>The app root moved somewhere temporary, for as long as a test holds it.</summary>
    private sealed class TemporaryRoot : IDisposable
    {
        private readonly string? _previous = System.Environment.GetEnvironmentVariable(Engine_AppRoot.EnvironmentVariable);
        private readonly string _directory = Path.Combine(Refii.Tests.TestTemp.Root, $"refii-texcache-{Guid.NewGuid():N}");

        public TemporaryRoot()
        {
            Directory.CreateDirectory(_directory);
            System.Environment.SetEnvironmentVariable(Engine_AppRoot.EnvironmentVariable, _directory);
            Engine_AppRoot.Invalidate();

            // Never let a test write into a real cache.
            if (!Path.GetFullPath(Engine_TexCache.CacheRoot).StartsWith(Path.GetFullPath(_directory), StringComparison.Ordinal))
                throw new InvalidOperationException($"the cache is not under the temporary root: {Engine_TexCache.CacheRoot}");
            if (!Engine_TexSettings.CacheEnabled)
                throw new InvalidOperationException("the texture cache is off in this run's settings");
        }

        public void Dispose()
        {
            System.Environment.SetEnvironmentVariable(Engine_AppRoot.EnvironmentVariable, _previous);
            Engine_AppRoot.Invalidate();
            Directory.Delete(_directory, true);
        }
    }

    private static string PathOf(string key) =>
        Path.Combine(Engine_TexCache.CacheRoot, $"v{Engine_TexCache.ConverterVersion}", key[..2], key + ".rftx");

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static Image Rgba(int width, int height, int seed) =>
        Image.CreateFromData(width, height, false, Image.Format.Rgba8, Bytes(width * height * 4, seed));

    /// <summary>An 8x8 chain of 4 levels, 8x8 to 1x1, as block-compressed data: 4, 1, 1 and 1 blocks.</summary>
    private static Image Bc1WithMips(int seed) =>
        Image.CreateFromData(8, 8, true, Image.Format.Dxt1, Bytes(8 * 7, seed));

    /// <summary>A stored entry's file, with each damaged form then tried in its place.</summary>
    private static byte[] StoredEntry(int faces = 1)
    {
        Image[] images = Enumerable.Range(0, faces).Select(f => Rgba(8, 8, 100 + f)).ToArray();
        Engine_TexCache.Store(Key, images, true);
        return File.ReadAllBytes(PathOf(Key));
    }

    private static byte[] Patched(byte[] entry, int at, uint value)
    {
        var copy = (byte[])entry.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(at), value);
        return copy;
    }

    private static byte[] Patched16(byte[] entry, int at, ushort value)
    {
        var copy = (byte[])entry.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(at), value);
        return copy;
    }

    private static byte[] Deflated(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);
        return output.ToArray();
    }

    /// <summary>The entry's own header in front of a different payload, with the lengths that fit it.</summary>
    private static byte[] WithPayload(byte[] entry, byte[] raw, ushort faces = 1)
    {
        byte[] deflated = Deflated(raw);
        var whole = new byte[HeaderSize + deflated.Length];
        entry.AsSpan(0, HeaderSize).CopyTo(whole);
        BinaryPrimitives.WriteUInt16LittleEndian(whole.AsSpan(FacesAt), faces);
        BinaryPrimitives.WriteUInt32LittleEndian(whole.AsSpan(LengthAt), (uint)(raw.Length / faces));
        BinaryPrimitives.WriteUInt32LittleEndian(whole.AsSpan(DeflatedAt), (uint)deflated.Length);
        deflated.CopyTo(whole, HeaderSize);
        return whole;
    }

    /// <summary>What a read of the entry on disk does, as one line, so one failure shows them all.</summary>
    private static string Outcome(string name)
    {
        long misses = Engine_TexCache.Misses, hits = Engine_TexCache.Hits;
        try
        {
            bool loaded = Engine_TexCache.TryLoadFaces(Key, out Image[] faces, out _);
            return $"{name}: loaded {loaded}, {faces.Length} faces, misses +{Engine_TexCache.Misses - misses}, hits +{Engine_TexCache.Hits - hits}";
        }
        catch (Exception e)
        {
            return $"{name}: threw {e.GetType().Name}";
        }
    }

    private static string Missed(string name) => $"{name}: loaded False, 0 faces, misses +1, hits +0";

    private static void AssertAllMiss(IEnumerable<(string Name, byte[] File)> damaged)
    {
        var expected = new List<string>();
        var actual = new List<string>();
        foreach ((string name, byte[] file) in damaged)
        {
            File.WriteAllBytes(PathOf(Key), file);
            expected.Add(Missed(name));
            actual.Add(Outcome(name));
        }

        AssertThat(string.Join("\n", actual)).IsEqual(string.Join("\n", expected));
    }

    private static void AssertSameImage(Image got, Image wanted)
    {
        AssertThat(got.GetWidth()).IsEqual(wanted.GetWidth());
        AssertThat(got.GetHeight()).IsEqual(wanted.GetHeight());
        AssertThat(got.GetFormat()).IsEqual(wanted.GetFormat());
        AssertThat(got.HasMipmaps()).IsEqual(wanted.HasMipmaps());
        AssertThat(Convert.ToHexString(got.GetData())).IsEqual(Convert.ToHexString(wanted.GetData()));
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ASingleFaceComesBackByteForByte()
    {
        using var root = new TemporaryRoot();
        foreach ((Image image, bool alpha) in new[]
                 {
                     (Rgba(16, 8, 1), true),
                     (Rgba(1, 1, 2), false),
                     (Bc1WithMips(3), false),
                     (Image.CreateFromData(4, 4, false, Image.Format.Dxt5, Bytes(16, 4)), true),
                 })
        {
            long hits = Engine_TexCache.Hits, stores = Engine_TexCache.Stores;
            Engine_TexCache.Store(Key, new[] { image }, alpha);
            AssertThat(Engine_TexCache.Stores).IsEqual(stores + 1);

            AssertThat(Engine_TexCache.TryLoadFaces(Key, out Image[] faces, out bool hasAlpha)).IsTrue();
            AssertThat(faces.Length).IsEqual(1);
            AssertThat(hasAlpha).IsEqual(alpha);
            AssertSameImage(faces[0], image);
            AssertThat(Engine_TexCache.Hits).IsEqual(hits + 1);

            // The one-image form of the same read.
            Image? one = Engine_TexCache.TryLoad(Key, out bool oneAlpha);
            AssertThat(one is null).IsFalse();
            AssertThat(oneAlpha).IsEqual(alpha);
            AssertSameImage(one!, image);
        }
    }

    /// <summary>A cube's six faces, each its own bytes and in its own place, in one entry.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void ACubeComesBackFaceByFace()
    {
        using var root = new TemporaryRoot();
        Image[] cube = Enumerable.Range(0, 6).Select(f => Rgba(8, 8, 10 + f)).ToArray();
        Engine_TexCache.Store(Key, cube, true);

        AssertThat(Engine_TexCache.TryLoadFaces(Key, out Image[] faces, out bool hasAlpha)).IsTrue();
        AssertThat(faces.Length).IsEqual(6);
        AssertThat(hasAlpha).IsTrue();
        for (int f = 0; f < 6; f++)
            AssertSameImage(faces[f], cube[f]);

        // The one-image form only serves a single face.
        AssertThat(Engine_TexCache.TryLoad(Key, out _) is null).IsTrue();
    }

    /// <summary>The buffers are pooled and rented larger than asked: what an earlier, bigger entry left
    /// in them must not reach a later, smaller one.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void PooledBuffersKeepEntriesApart()
    {
        using var root = new TemporaryRoot();
        const string big = "bb34567890abcdef";
        Image large = Rgba(512, 512, 21);
        Engine_TexCache.Store(big, new[] { large }, false);
        AssertThat(Engine_TexCache.TryLoadFaces(big, out Image[] first, out _)).IsTrue();
        AssertSameImage(first[0], large);

        // Lengths either side of the pool's bucket sizes.
        foreach (int side in new[] { 3, 4, 5, 16, 17, 64 })
        {
            Image small = Rgba(side, side, side);
            Engine_TexCache.Store(Key, new[] { small }, false);
            AssertThat(Engine_TexCache.TryLoadFaces(Key, out Image[] faces, out _)).IsTrue();
            AssertSameImage(faces[0], small);
        }
    }

    /// <summary>Workers read side by side during a load.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void ReadsSideBySideGetTheirOwnBytes()
    {
        using var root = new TemporaryRoot();
        string[] keys = Enumerable.Range(0, 8).Select(i => $"{i:x2}34567890abcdef").ToArray();
        string[] expected = new string[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            Image image = Rgba(32, 32, 50 + i);
            expected[i] = Convert.ToHexString(image.GetData());
            Engine_TexCache.Store(keys[i], new[] { image }, false);
        }

        int wrong = 0;
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, n =>
        {
            int i = n % keys.Length;
            if (!Engine_TexCache.TryLoadFaces(keys[i], out Image[] faces, out _) ||
                Convert.ToHexString(faces[0].GetData()) != expected[i])
            {
                System.Threading.Interlocked.Increment(ref wrong);
            }
        });
        AssertThat(wrong).IsEqual(0);
    }

    [TestCase]
    [RequireGodotRuntime]
    public void NoFileIsAMiss()
    {
        using var root = new TemporaryRoot();
        long misses = Engine_TexCache.Misses;
        AssertThat(Engine_TexCache.TryLoadFaces(Key, out Image[] faces, out bool alpha)).IsFalse();
        AssertThat(faces.Length).IsEqual(0);
        AssertThat(alpha).IsFalse();
        AssertThat(Engine_TexCache.Misses).IsEqual(misses + 1);
        AssertThat(Engine_TexCache.TryLoad(Key, out _) is null).IsTrue();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ATruncatedFileIsAMiss()
    {
        using var root = new TemporaryRoot();
        byte[] entry = StoredEntry();
        AssertThat(Outcome("whole")).IsEqual("whole: loaded True, 1 faces, misses +0, hits +1");
        var damaged = new List<(string, byte[])>();
        foreach (int length in new[]
                 {
                     0, 1, 3, 4, 7, 8, HeaderSize - 1, HeaderSize, HeaderSize + 1,
                     HeaderSize + (entry.Length - HeaderSize) / 2, entry.Length - 1,
                 })
        {
            damaged.Add(($"cut to {length}", entry[..length]));
        }

        AssertAllMiss(damaged);
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AWrongMagicIsAMiss()
    {
        using var root = new TemporaryRoot();
        byte[] entry = StoredEntry();
        AssertAllMiss(new[]
        {
            ("zero", Patched(entry, MagicAt, 0)),
            ("big endian", Patched(entry, MagicAt, 0x52465458)),
            ("another format", Patched(entry, MagicAt, 0x20534444)),
            ("not a texture at all", Bytes(4096, 7)),
        });
    }

    /// <summary>Containers from before this layout, whose fields would not even line up.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void AnotherContainerVersionIsAMiss()
    {
        using var root = new TemporaryRoot();
        byte[] entry = StoredEntry();
        AssertAllMiss(new[]
        {
            ("version 1", Patched(entry, VersionAt, 1)),
            ("version 2", Patched(entry, VersionAt, 2)),
            ("version before this one", Patched(entry, VersionAt, Engine_TexCache.ContainerVersion - 1)),
            ("version after this one", Patched(entry, VersionAt, Engine_TexCache.ContainerVersion + 1)),
            ("version 0", Patched(entry, VersionAt, 0)),
        });

        // The version above read as a miss, and not from something else being wrong with the entry.
        File.WriteAllBytes(PathOf(Key), entry);
        AssertThat(Outcome("as stored")).IsEqual("as stored: loaded True, 1 faces, misses +0, hits +1");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ADeflatedLengthThatDoesNotFitIsAMiss()
    {
        using var root = new TemporaryRoot();
        byte[] entry = StoredEntry();
        var deflated = (uint)(entry.Length - HeaderSize);
        AssertAllMiss(new[]
        {
            ("zero", Patched(entry, DeflatedAt, 0)),
            ("one short of half", Patched(entry, DeflatedAt, deflated / 2 - 1)),
            ("one", Patched(entry, DeflatedAt, 1)),
            ("one more than the file", Patched(entry, DeflatedAt, deflated + 1)),
            ("a megabyte more than the file", Patched(entry, DeflatedAt, deflated + 1048576)),
            ("a gigabyte", Patched(entry, DeflatedAt, 1u << 30)),
            ("the largest array", Patched(entry, DeflatedAt, (uint)Array.MaxLength)),
            ("past the largest array", Patched(entry, DeflatedAt, (uint)Array.MaxLength + 1)),
            ("int.MaxValue", Patched(entry, DeflatedAt, int.MaxValue)),
            ("past int.MaxValue", Patched(entry, DeflatedAt, 0x80000000u)),
            ("all ones", Patched(entry, DeflatedAt, uint.MaxValue)),
        });
    }

    [TestCase]
    [RequireGodotRuntime]
    public void AnInflatedLengthThatDoesNotFitIsAMiss()
    {
        using var root = new TemporaryRoot();
        byte[] entry = StoredEntry();
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(LengthAt));
        AssertAllMiss(new[]
        {
            ("zero", Patched(entry, LengthAt, 0)),
            ("one short", Patched(entry, LengthAt, length - 1)),
            ("one long", Patched(entry, LengthAt, length + 1)),
            ("a gigabyte", Patched(entry, LengthAt, 1u << 30)),
            ("the largest array", Patched(entry, LengthAt, (uint)Array.MaxLength)),
            ("past the largest array", Patched(entry, LengthAt, (uint)Array.MaxLength + 1)),
            ("int.MaxValue", Patched(entry, LengthAt, int.MaxValue)),
            ("past int.MaxValue", Patched(entry, LengthAt, 0x80000000u)),
            ("all ones", Patched(entry, LengthAt, uint.MaxValue)),
            ("six faces past int.MaxValue", Patched16(Patched(entry, LengthAt, 0x20000000u), FacesAt, 6)),
        });
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ABadHeaderIsAMiss()
    {
        using var root = new TemporaryRoot();
        byte[] entry = StoredEntry();
        AssertAllMiss(new[]
        {
            ("no faces", Patched16(entry, FacesAt, 0)),
            ("seven faces", Patched16(entry, FacesAt, 7)),
            ("all the faces", Patched16(entry, FacesAt, ushort.MaxValue)),
            ("two faces of one's data", Patched16(entry, FacesAt, 2)),
            ("no width", Patched(entry, WidthAt, 0)),
            ("no height", Patched(entry, HeightAt, 0)),
            ("negative width", Patched(entry, WidthAt, 0x80000000u)),
            ("negative height", Patched(entry, HeightAt, uint.MaxValue)),
            ("wider than the data", Patched(entry, WidthAt, 16)),
            ("narrower than the data", Patched(entry, WidthAt, 4)),
            ("a huge size", Patched(Patched(entry, WidthAt, 1 << 24), HeightAt, 1 << 24)),
            ("another format's size", Patched(entry, FormatAt, (uint)Image.Format.Rgb8)),
            ("no such format", Patched(entry, FormatAt, 9999)),
            ("a negative format", Patched(entry, FormatAt, uint.MaxValue)),
        });
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ABadPayloadIsAMiss()
    {
        using var root = new TemporaryRoot();
        byte[] entry = StoredEntry();
        byte[] right = Rgba(8, 8, 100).GetData();

        // Not deflate at all: every byte set makes an invalid block type straight away.
        var notDeflate = (byte[])entry.Clone();
        notDeflate.AsSpan(HeaderSize).Fill(0xFF);

        // Then proper deflate, of the wrong amount of data.
        AssertAllMiss(new[]
        {
            ("not deflate", notDeflate),
            ("inflates short", Patched(WithPayload(entry, right[..^1]), LengthAt, (uint)right.Length)),
            ("inflates long", Patched(WithPayload(entry, right.Concat(new byte[] { 1 }).ToArray()), LengthAt, (uint)right.Length)),
            ("inflates to nothing", Patched(WithPayload(entry, Array.Empty<byte>()), LengthAt, (uint)right.Length)),
            ("two faces, cut short", WithPayload(entry, new byte[right.Length * 2], 2)[..^1]),
        });
    }

    /// <summary>A wild length is turned down on sight, not after renting the buffer it asks for:
    /// on a phone a gigabyte is the difference between a miss and a dead process.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void AWildLengthAllocatesNothingBig()
    {
        using var root = new TemporaryRoot();
        byte[] entry = StoredEntry();
        foreach ((string name, byte[] file) in new[]
                 {
                     ("deflated", Patched(entry, DeflatedAt, 1u << 30)),
                     ("inflated", Patched(entry, LengthAt, 1u << 30)),
                     ("inflated, largest array", Patched(entry, LengthAt, (uint)Array.MaxLength)),
                     ("six faces", Patched16(Patched(entry, LengthAt, 1u << 26), FacesAt, 6)),
                 })
        {
            File.WriteAllBytes(PathOf(Key), file);
            long before = GC.GetAllocatedBytesForCurrentThread();
            AssertThat(Outcome(name)).IsEqual(Missed(name));
            AssertThat(GC.GetAllocatedBytesForCurrentThread() - before).IsLess(1024 * 1024L);
        }
    }

    /// <summary>The other side of that check: a flat texture deflates about as far as deflate can, and
    /// must still fit within what a length is allowed to claim.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void AFlatTextureStillReads()
    {
        using var root = new TemporaryRoot();
        foreach (Image.Format format in new[] { Image.Format.Rgba8, Image.Format.R8 })
        {
            Image flat = Image.CreateEmpty(2048, 2048, false, format);
            Engine_TexCache.Store(Key, new[] { flat }, false);
            int stored = (int)new FileInfo(PathOf(Key)).Length - HeaderSize;
            AssertThat((double)flat.GetData().Length / stored).IsGreater(1000.0);

            AssertThat(Engine_TexCache.TryLoadFaces(Key, out Image[] faces, out _)).IsTrue();
            AssertSameImage(faces[0], flat);
        }

        // A very small flat one, where the block overhead is a larger share.
        foreach (int side in new[] { 1, 2, 16, 64 })
        {
            Image flat = Image.CreateEmpty(side, side, false, Image.Format.R8);
            Engine_TexCache.Store(Key, new[] { flat }, false);
            AssertThat(Engine_TexCache.TryLoadFaces(Key, out Image[] faces, out _)).IsTrue();
            AssertSameImage(faces[0], flat);
        }

        // Six flat faces in one deflate.
        Image[] cube = Enumerable.Range(0, 6).Select(_ => Image.CreateEmpty(1024, 1024, false, Image.Format.Rgba8)).ToArray();
        Engine_TexCache.Store(Key, cube, false);
        AssertThat(Engine_TexCache.TryLoadFaces(Key, out Image[] all, out _)).IsTrue();
        AssertThat(all.Length).IsEqual(6);
        AssertSameImage(all[5], cube[5]);
    }

    /// <summary>A file the process may not open is a miss too, as the old FileAccess open returning
    /// null was. Root can open anything, and then there is nothing to test.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void AnUnreadableFileIsAMiss()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var root = new TemporaryRoot();
        StoredEntry();
        string path = PathOf(Key);
        File.SetUnixFileMode(path, UnixFileMode.None);
        bool readable = true;
        try
        {
            using FileStream _ = File.Open(path, FileMode.Open, FileAccess.Read);
        }
        catch (UnauthorizedAccessException)
        {
            readable = false;
        }

        if (!readable)
            AssertThat(Outcome("no permission")).IsEqual(Missed("no permission"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>A directory where the entry should be, which a sweep or a bad unpack could leave.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void ADirectoryInTheEntrysPlaceIsAMiss()
    {
        using var root = new TemporaryRoot();
        Directory.CreateDirectory(PathOf(Key));
        AssertThat(Outcome("directory")).IsEqual(Missed("directory"));
    }

    /// <summary>A miss leaves the entry to be rebuilt: the next store replaces it and reads again.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void ADamagedEntryIsRebuiltInPlace()
    {
        using var root = new TemporaryRoot();
        byte[] entry = StoredEntry();
        File.WriteAllBytes(PathOf(Key), entry[..(HeaderSize + 1)]);
        AssertThat(Outcome("damaged")).IsEqual(Missed("damaged"));

        Image image = Rgba(8, 8, 100);
        Engine_TexCache.Store(Key, new[] { image }, true);
        AssertThat(Engine_TexCache.TryLoadFaces(Key, out Image[] faces, out _)).IsTrue();
        AssertSameImage(faces[0], image);
        AssertThat(Directory.GetFiles(Path.GetDirectoryName(PathOf(Key))!).Length).IsEqual(1);
    }
}
