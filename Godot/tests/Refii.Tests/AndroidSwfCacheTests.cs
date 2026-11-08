using System;
using System.IO;
using GdUnit4;
using Godot;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The phone's handling of the pub games' cache folder: only this build's files stay,
/// and the triangles read back as they were written.</summary>
[TestSuite]
public sealed class AndroidSwfCacheTests
{
    private const string Stamp = "53b40db6";

    private static string TempFolder()
    {
        string folder = Path.Combine(Refii.Tests.TestTemp.Root, $"refii-swfcache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static Vector2[] Vectors()
    {
        var vectors = new Vector2[1000];
        for (int i = 0; i < vectors.Length; i++) vectors[i] = new Vector2(i * 0.25f - 17.3f, MathF.Sqrt(i) * -3.1f);
        return vectors;
    }

    private static Color[] Colours()
    {
        var colours = new Color[1000];
        for (int i = 0; i < colours.Length; i++)
            colours[i] = Color.Color8((byte)i, (byte)(i * 3), (byte)(255 - i), (byte)(i * 7));
        return colours;
    }

    /// <summary>The build is read from all three kinds of name, whatever else the name holds.</summary>
    [TestCase]
    public void ReadsTheBuildFromEachKindOfName()
    {
        AssertThat(AndroidSwfCache.BuildStampOf($"astc_73a377106921cc57_Dxt1_1800x1300_{Stamp}.bin")).IsEqual(Stamp);
        AssertThat(AndroidSwfCache.BuildStampOf($"astc_73a377106921cc57_Dxt5_450x104m_{Stamp}.bin")).IsEqual(Stamp);
        AssertThat(AndroidSwfCache.BuildStampOf($"astc_73a377106921cc57_Dxt1_8x4_{Stamp}.bin.0123456789abcdef0123456789abcdef.tmp")).IsEqual(Stamp);
        AssertThat(AndroidSwfCache.BuildStampOf($"Magedice_24347ecafa9318e8_{Stamp}_shapes.bin")).IsEqual(Stamp);
        AssertThat(AndroidSwfCache.BuildStampOf($"fonts_en_7f01457cfcaca52b_{Stamp}_Maiandra GD Fable_bold.res")).IsEqual(Stamp);

        AssertThat(AndroidSwfCache.BuildStampOf("notes.txt")).IsNull();
        AssertThat(AndroidSwfCache.BuildStampOf("astc_.bin")).IsNull();
        AssertThat(AndroidSwfCache.BuildStampOf("astc_73a377106921cc57_Dxt1_8x4_notastmp.bin")).IsNull();
        AssertThat(AndroidSwfCache.BuildStampOf("Keystone_shapes.bin")).IsNull();
        AssertThat(AndroidSwfCache.BuildStampOf("a_nothexnothexnoth_53b40db6_face.res")).IsNull();
        AssertThat(AndroidSwfCache.BuildStampOf("something.tmp")).IsNull();
    }

    /// <summary>Other builds' files go, this build's and anything unrecognised stay.</summary>
    [TestCase]
    public void DeletesOnlyOtherBuilds()
    {
        string folder = TempFolder();
        try
        {
            string[] kept =
            {
                $"astc_73a377106921cc57_Dxt1_8x4_{Stamp}.bin",
                $"Keystone_90c740381753565d_{Stamp}_shapes.bin",
                $"fonts_en_7f01457cfcaca52b_{Stamp}_Maiandra GD Fable.res",
                $"astc_73a377106921cc57_Dxt1_8x4_{Stamp}.bin.0123456789abcdef0123456789abcdef.tmp",
                "notes.txt",
            };
            string[] gone =
            {
                "astc_73a377106921cc57_Dxt1_8x4_31e1fbe2.bin",
                "Keystone_90c740381753565d_f41f5ef8_shapes.bin",
                "fonts_en_7f01457cfcaca52b_d9faf2b0_Maiandra GD Fable_bold.res",
                "astc_73a377106921cc57_Dxt1_8x4_31e1fbe2.bin.0123456789abcdef0123456789abcdef.tmp",
            };
            foreach (string name in kept) File.WriteAllBytes(Path.Combine(folder, name), new byte[10]);
            foreach (string name in gone) File.WriteAllBytes(Path.Combine(folder, name), new byte[100]);

            (int files, long bytes) = AndroidSwfCache.DeleteOtherBuilds(folder, Stamp);

            AssertThat(files).IsEqual(gone.Length);
            AssertThat(bytes).IsEqual(gone.Length * 100L);
            foreach (string name in kept) AssertThat(File.Exists(Path.Combine(folder, name))).IsTrue();
            foreach (string name in gone) AssertThat(File.Exists(Path.Combine(folder, name))).IsFalse();
        }
        finally { Directory.Delete(folder, true); }
    }

    /// <summary>Off Android the folder is not asked for: its path comes from Godot, and a mount can
    /// run with no engine behind it, as the tests that mount do.</summary>
    [TestCase]
    public void OffAndroidTheSweepAsksForNothing()
    {
        AndroidSwfCache.SweepOtherBuilds(() => throw new InvalidOperationException("asked"), Stamp);
    }

    /// <summary>A folder not made yet is nothing to do.</summary>
    [TestCase]
    public void AMissingFolderIsNothing()
    {
        (int files, long bytes) = AndroidSwfCache.DeleteOtherBuilds(
            Path.Combine(Refii.Tests.TestTemp.Root, $"refii-swfcache-{Guid.NewGuid():N}"), Stamp);
        AssertThat(files).IsEqual(0);
        AssertThat(bytes).IsEqual(0L);
    }

    /// <summary>Written at once, the bytes are those the per-element loop wrote.</summary>
    [TestCase]
    public void WritesTheSameBytesAsElementByElement()
    {
        Vector2[] vectors = Vectors();
        Color[] colours = Colours();

        var loop = new MemoryStream();
        using (var writer = new BinaryWriter(loop, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            foreach (Vector2 v in vectors)
            {
                writer.Write(v.X);
                writer.Write(v.Y);
            }

            foreach (Color c in colours)
            {
                writer.Write((byte)c.R8);
                writer.Write((byte)c.G8);
                writer.Write((byte)c.B8);
                writer.Write((byte)c.A8);
            }
        }

        var bulk = new MemoryStream();
        Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Write(bulk, vectors);
        Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Write(bulk, colours);

        AssertThat(bulk.ToArray()).IsEqual(loop.ToArray());
    }

    /// <summary>Read at once, the values are those the per-element loop read.</summary>
    [TestCase]
    public void ReadsTheSameValuesAsElementByElement()
    {
        var stream = new MemoryStream();
        Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Write(stream, Vectors());
        Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Write(stream, Colours());
        byte[] file = stream.ToArray();

        var vectors = new Vector2[1000];
        var colours = new Color[1000];
        using (var reader = new BinaryReader(new MemoryStream(file)))
        {
            for (int v = 0; v < vectors.Length; v++) vectors[v] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            for (int v = 0; v < colours.Length; v++)
                colours[v] = Color.Color8(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
        }

        var source = new MemoryStream(file);
        var bulkVectors = new Vector2[1000];
        var bulkColours = new Color[1000];
        Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Read(source, bulkVectors);
        Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Read(source, bulkColours);

        AssertThat(bulkVectors.AsSpan().SequenceEqual(vectors)).IsTrue();
        AssertThat(bulkColours.AsSpan().SequenceEqual(colours)).IsTrue();
    }

    /// <summary>The deflated file reads back bit for bit, headers between the arrays included,
    /// and takes less room for repetitive triangles.</summary>
    [TestCase]
    public void DeflatedPartsRoundTrip()
    {
        string folder = TempFolder();
        try
        {
            string plain = Path.Combine(folder, "plain.bin"), deflated = Path.Combine(folder, "deflated.bin");
            foreach ((string path, bool compress) in new[] { (plain, false), (deflated, true) })
            {
                using Stream stream = AndroidSwfCache.CreateParts(path, compress);
                using var writer = new BinaryWriter(stream);
                for (int part = 0; part < 20; part++)
                {
                    writer.Write(part);
                    Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Write(stream, Vectors());
                    Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Write(stream, Colours());
                }
            }

            AssertThat(new FileInfo(deflated).Length).IsLess(new FileInfo(plain).Length);

            using Stream read = AndroidSwfCache.OpenParts(deflated, deflated: true);
            using var reader = new BinaryReader(read);
            for (int part = 0; part < 20; part++)
            {
                AssertThat(reader.ReadInt32()).IsEqual(part);
                var vectors = new Vector2[1000];
                var colours = new Color[1000];
                Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Read(read, vectors);
                Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Read(read, colours);
                AssertThat(vectors.AsSpan().SequenceEqual(Vectors())).IsTrue();
                AssertThat(colours.AsSpan().SequenceEqual(Colours())).IsTrue();
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    /// <summary>A file cut short or not deflated at all throws, which the cache's reader takes as a
    /// reason to rebuild.</summary>
    [TestCase]
    public void DamageThrows()
    {
        string folder = TempFolder();
        try
        {
            string path = Path.Combine(folder, "parts.bin");
            using (Stream stream = AndroidSwfCache.CreateParts(path, deflated: true))
            {
                Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Write(stream, Vectors());
            }

            byte[] whole = File.ReadAllBytes(path);
            File.WriteAllBytes(path, whole[..(whole.Length / 2)]);
            AssertThrown(() =>
            {
                using Stream read = AndroidSwfCache.OpenParts(path, deflated: true);
                Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Read(read, new Vector2[1000]);
            }).IsInstanceOf<Exception>();

            File.WriteAllBytes(path, new byte[1000]);
            AssertThrown(() =>
            {
                using Stream read = AndroidSwfCache.OpenParts(path, deflated: true);
                Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Read(read, new Vector2[1000]);
            }).IsInstanceOf<Exception>();

            AssertThrown(() => Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Read(new MemoryStream(new byte[7]), new Vector2[1]))
                .IsInstanceOf<EndOfStreamException>();
            AssertThrown(() => Refii.Titles.Fable2.PubGames.Engine_SwfPartsIo.Read(new MemoryStream(new byte[3]), new Color[1]))
                .IsInstanceOf<EndOfStreamException>();
        }
        finally { Directory.Delete(folder, true); }
    }

    /// <summary>Boards are released on a low-memory phone only, and the tier is not read elsewhere.</summary>
    [TestCase]
    public void BoardsAreReleasedOnALowMemoryPhoneOnly()
    {
        AssertBool(AndroidSwfCache.ReleasesBoardsFor(true, () => true)).IsTrue();
        AssertBool(AndroidSwfCache.ReleasesBoardsFor(true, () => false)).IsFalse();
        AssertBool(AndroidSwfCache.ReleasesBoardsFor(false, () => throw new InvalidOperationException())).IsFalse();
        AssertBool(AndroidSwfCache.ReleasesBoards).IsFalse();
    }

    /// <summary>A part's managed size: 8 bytes a position, 16 a colour, 8 a UV, without overflow.</summary>
    [TestCase]
    public void PartBytesCountEachArray()
    {
        AssertThat(AndroidSwfCache.PartBytes(1000, colours: true, uvs: false)).IsEqual(24000L);
        AssertThat(AndroidSwfCache.PartBytes(1000, colours: false, uvs: true)).IsEqual(16000L);
        AssertThat(AndroidSwfCache.PartBytes(0, colours: true, uvs: true)).IsEqual(0L);
        AssertThat(AndroidSwfCache.PartBytes(200_000_000, colours: true, uvs: false)).IsEqual(4_800_000_000L);
    }
}
