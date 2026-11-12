using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using Godot;
using Refii.Models;
using Refii.Textures;
using static GdUnit4.Assertions;
using Layer = Refii.Host.HostBridge.GuestTextureLayer;

namespace Refii.Tests;

/// <summary>Finished composites on disk: the bytes that were uploaded, under a name that follows what they were built from.</summary>
[TestSuite]
public sealed class AndroidCompositeCacheTests
{
    /// <summary>What comes off the disk is what was uploaded; a damaged file is a miss.</summary>
    [TestCase, RequireGodotRuntime]
    public void AStoredCompositeIsTheBytesThatWereUploaded()
    {
        string folder = Path.Combine(Refii.Tests.TestTemp.Root, $"refii-composites-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        AndroidCompositeCache.UseFolderForTests(folder);
        try
        {
            using Image image = Image.CreateFromData(16, 16, false, Image.Format.Rgba8, Noise());
            image.GenerateMipmaps(false);
            Engine_TexImage.CompressBlocks(image, false);
            ImageTexture uploaded = AndroidCompositeCache.UploadAndStore(image, "a.bin");
            ImageTexture? read = AndroidCompositeCache.TryLoad("a.bin");
            AssertThat(read).IsNotNull();
            using Image was = uploaded.GetImage();
            using Image now = read!.GetImage();
            AssertThat(now.GetFormat()).IsEqual(was.GetFormat());
            AssertThat(now.HasMipmaps()).IsEqual(was.HasMipmaps());
            AssertThat(now.GetData()).IsEqual(was.GetData());

            AssertThat(AndroidCompositeCache.TryLoad("none.bin")).IsNull();
            byte[] whole = File.ReadAllBytes(Path.Combine(folder, "a.bin"));
            File.WriteAllBytes(Path.Combine(folder, "short.bin"), whole[..(whole.Length - 1)]);
            AssertThat(AndroidCompositeCache.TryLoad("short.bin")).IsNull();
        }
        finally
        {
            AndroidCompositeCache.UseFolderForTests(null);
            Directory.Delete(folder, true);
        }
    }

    /// <summary>A stored composite's name changes with its key and the bytes of any texture it names, and with nothing else.</summary>
    [TestCase]
    public void TheNameFollowsWhatItWasBuiltFrom()
    {
        const string first = "fixture/first.tex", second = "fixture/second.tex";
        var files = new Dictionary<string, byte[]> { [first] = new byte[] { 1 }, [second] = new byte[] { 2 } };
        string folder = Path.Combine(Refii.Tests.TestTemp.Root, $"refii-composites-{Guid.NewGuid():N}");
        Engine_TexPathIndex.SetPathSource(() => new[] { first, second });
        Engine_BnkMeshLoader.SetResolver(path => files.GetValueOrDefault(path.Replace('\\', '/')));
        AndroidCompositeCache.UseFolderForTests(folder);
        try
        {
            uint a = Engine_TexPathIndex.PathHash(first), b = Engine_TexPathIndex.PathHash(second);
            Layer[] layers = { new() { Texture = a }, new() { Texture = b } };
            Layer[] none = Array.Empty<Layer>();
            string? name = AndroidCompositeCache.Name("key", a, layers, none);
            AssertThat(name).IsNotNull();
            AssertThat(AndroidCompositeCache.Name("key", a, layers, none)).IsEqual(name!);
            AssertThat(AndroidCompositeCache.Name("other", a, layers, none)).IsNotEqual(name!);

            // A file that changes under the same name, as a live remount does, renames at once.
            files[second] = new byte[] { 3 };
            string? changed = AndroidCompositeCache.Name("key", a, layers, none);
            AssertThat(changed).IsNotNull();
            AssertThat(changed).IsNotEqual(name!);
            files.Remove(second);
            AssertThat(AndroidCompositeCache.Name("key", a, layers, none)).IsNotEqual(name!);
            AssertThat(AndroidCompositeCache.Name("key", a, layers, none)).IsNotEqual(changed!);
        }
        finally
        {
            AndroidCompositeCache.UseFolderForTests(null);
            Engine_TexPathIndex.SetPathSource(null);
            Engine_BnkMeshLoader.SetResolver(null);
        }
    }

    /// <summary>A shipped entry is named without a read; a new mount of changed banks, or a mod's file over it, names it anew.</summary>
    [TestCase]
    public void AShippedSourceIsNamedWithoutAReadAndAMountChangeRenamesIt()
    {
        const string texture = "art/t.tex";
        string root = Path.Combine(Refii.Tests.TestTemp.Root, $"refii-composite-mount-{Guid.NewGuid():N}");
        string game = Path.Combine(root, "game"), mod = Path.Combine(root, "mod");
        Directory.CreateDirectory(Path.Combine(game, "data", Refii.Titles.Fable2.PubGames.PubGamesCatalog.FolderName));
        Directory.CreateDirectory(Path.Combine(mod, "art"));
        void Bank(byte seed)
        {
            var bytes = new byte[64 + seed];
            new Random(seed).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(game, "a.bnk"),
                Refii.Bnk.BnkWriter.Write(new List<Refii.Bnk.BnkWriterEntry> { new() { Path = texture, Bytes = bytes } }));
        }

        Refii.Bnk.BnkFileSystem? mount = null;
        int reads = 0;
        Engine_TexPathIndex.SetPathSource(() => new[] { texture });
        Engine_BnkMeshLoader.SetResolver(path => { ++reads; return mount!.TryGetFile(path.Replace('\\', '/'), out var f) ? f.ReadAllBytes() : null; });
        AndroidTexSourceKeys.MountLookup previous = AndroidTexSourceKeys.Lookup;
        AndroidTexSourceKeys.Lookup = (string path, Func<string, byte[]?> read, out Refii.Bnk.BnkFileSystem? m, out Refii.Bnk.BnkFile? f) =>
        {
            m = mount;
            f = null;
            return mount is not null && mount.TryGetFile(path.Replace('\\', '/'), out f!);
        };
        AndroidCompositeCache.UseFolderForTests(Path.Combine(root, "cache"));
        try
        {
            uint t = Engine_TexPathIndex.PathHash(texture);
            Layer[] layers = { new() { Texture = t } };
            Bank(1);
            mount = Refii.Bnk.BnkFileSystem.Mount(game);
            string? shipped = AndroidCompositeCache.Name("key", t, layers, Array.Empty<Layer>());
            AssertThat(shipped).IsNotNull();
            AssertThat(reads).IsEqual(0);
            AssertThat(AndroidCompositeCache.Name("key", t, layers, Array.Empty<Layer>())).IsEqual(shipped!);

            mount.Dispose();
            Bank(2);
            mount = Refii.Bnk.BnkFileSystem.Mount(game);
            string? remounted = AndroidCompositeCache.Name("key", t, layers, Array.Empty<Layer>());
            AssertThat(remounted).IsNotNull();
            AssertThat(remounted).IsNotEqual(shipped!);

            mount.Dispose();
            File.WriteAllBytes(Path.Combine(mod, texture), new byte[] { 9 });
            mount = Refii.Bnk.BnkFileSystem.Mount(game, overlays: new[] { ("mod", mod) });
            long hashed = AndroidCompositeCache.SourcesHashed;
            string? modded = AndroidCompositeCache.Name("key", t, layers, Array.Empty<Layer>());
            AssertThat(modded).IsNotEqual(remounted!);
            AssertThat(reads).IsGreater(0);
            AssertThat(AndroidCompositeCache.SourcesHashed).IsGreater(hashed);
        }
        finally
        {
            mount?.Dispose();
            AndroidTexSourceKeys.Lookup = previous;
            AndroidCompositeCache.UseFolderForTests(null);
            Engine_TexPathIndex.SetPathSource(null);
            Engine_BnkMeshLoader.SetResolver(null);
            Directory.Delete(root, true);
        }
    }

    /// <summary>A composite built without one of its layers is shown but never stored; a whole one is stored.</summary>
    [TestCase, RequireGodotRuntime]
    public void ACompositeShortOfALayerIsNotStored()
    {
        const string first = "fixture/base.tex", second = "fixture/layer.tex";
        string folder = Path.Combine(Refii.Tests.TestTemp.Root, $"refii-composites-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        Engine_TexPathIndex.SetPathSource(() => new[] { first, second });
        Engine_BnkMeshLoader.SetResolver(path => new byte[] { 1 });
        AndroidCompositeCache.UseFolderForTests(folder);
        System.Reflection.MethodInfo blend = typeof(Refii.Hero.Engine_HeroTextureMorph).GetMethod("BlendAndUpload",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Type decodedType = typeof(Refii.Hero.Engine_HeroTextureMorph).GetNestedType("DecodedTexture", System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            uint a = Engine_TexPathIndex.PathHash(first), b = Engine_TexPathIndex.PathHash(second);
            Layer[] none = Array.Empty<Layer>();
            foreach ((Layer[] layers, string label) in new[] { (new Layer[] { new() { Texture = a } }, "whole"), (new Layer[] { new() { Texture = a }, new() { Texture = b, Weight = 1, Mode = 1 } }, "short") })
            {
                string name = AndroidCompositeCache.Name(label, a, layers, none)!;
                Array decoded = Array.CreateInstance(decodedType, layers.Length);
                byte[] output = Noise();
                blend.Invoke(null, new object?[] { output, 16, 16, a, layers, decoded, none, false, label, -1L, name });
                AssertThat(File.Exists(Path.Combine(folder, name))).IsEqual(label == "whole");
            }
        }
        finally
        {
            AndroidCompositeCache.UseFolderForTests(null);
            Engine_TexPathIndex.SetPathSource(null);
            Engine_BnkMeshLoader.SetResolver(null);
            Directory.Delete(folder, true);
        }
    }

    /// <summary>Another build's folder goes, and past the budget the longest unused files go.</summary>
    [TestCase]
    public void TheSweepKeepsTheBuildAndTheBudget()
    {
        string parent = Path.Combine(Refii.Tests.TestTemp.Root, $"refii-composites-{Guid.NewGuid():N}");
        string current = Path.Combine(parent, "now"), old = Path.Combine(parent, "was");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(old);
        try
        {
            File.WriteAllBytes(Path.Combine(old, "x.bin"), new byte[10]);
            DateTime start = DateTime.UtcNow.AddHours(-1);
            for (int i = 0; i < 4; i++)
            {
                string file = Path.Combine(current, $"{i}.bin");
                File.WriteAllBytes(file, new byte[100]);
                File.SetLastWriteTimeUtc(file, start.AddMinutes(i));
            }

            AndroidCompositeCache.Sweep(parent, current, 250);
            AssertThat(Directory.Exists(old)).IsFalse();
            AssertThat(File.Exists(Path.Combine(current, "0.bin"))).IsFalse();
            AssertThat(File.Exists(Path.Combine(current, "1.bin"))).IsFalse();
            AssertThat(File.Exists(Path.Combine(current, "2.bin"))).IsTrue();
            AssertThat(File.Exists(Path.Combine(current, "3.bin"))).IsTrue();
        }
        finally { Directory.Delete(parent, true); }
    }

    private static byte[] Noise()
    {
        var pixels = new byte[16 * 16 * 4];
        new Random(9).NextBytes(pixels);
        return pixels;
    }
}
