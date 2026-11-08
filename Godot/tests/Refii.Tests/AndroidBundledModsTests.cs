using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Mods;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;

namespace Refii.Tests;

/// <summary>The mods that ship in the APK: mods-src zipped by the export, unpacked on the phone and packed switched off.</summary>
[TestSuite]
public sealed class AndroidBundledModsTests
{
    private static string Scratch()
    {
        string root = Path.Combine(Refii.Tests.TestTemp.Root, "refii-android-mods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach ((string name, string text) in entries)
                using (var stream = zip.CreateEntry(name).Open())
                    stream.Write(Encoding.UTF8.GetBytes(text));
        return bytes.ToArray();
    }

    /// <summary>The trees land as shipped, a second start leaves them, and a new archive replaces them whole.</summary>
    [TestCase]
    public void BundledArchiveUnpacksOncePerArchive()
    {
        string root = Scratch();
        try
        {
            string target = Path.Combine(root, "bundled-mods");
            byte[] first = Zip(("hotbar/mod.toml", "name = \"Hotbar\""), ("hotbar/mod/init.lua", "-- one"),
                ("stale/mod.toml", "name = \"Stale\""), ("manifest.txt", "hotbar/mod.toml\nhotbar/mod/init.lua\nstale/mod.toml\n"));
            AssertThat(AndroidBundledMods.Unpack(first, target)).IsTrue();
            AssertThat(File.ReadAllText(Path.Combine(target, "hotbar/mod/init.lua"))).IsEqual("-- one");
            AssertThat(AndroidBundledMods.Unpack(first, target)).IsFalse();

            byte[] second = Zip(("hotbar/mod.toml", "name = \"Hotbar\""), ("hotbar/mod/init.lua", "-- two"),
                ("manifest.txt", "hotbar/mod.toml\nhotbar/mod/init.lua\n"));
            AssertThat(AndroidBundledMods.Unpack(second, target)).IsTrue();
            AssertThat(File.ReadAllText(Path.Combine(target, "hotbar/mod/init.lua"))).IsEqual("-- two");
            AssertThat(Directory.Exists(Path.Combine(target, "stale"))).IsFalse();
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>An unpacked archive is what the desktop's bundled folder is: ModBundled packs it, switched off.</summary>
    [TestCase]
    public void UnpackedTreesInstallAsBundledMods()
    {
        string root = Scratch();
        try
        {
            string target = Path.Combine(root, "bundled-mods"), mods = Path.Combine(root, "mods");
            AndroidBundledMods.Unpack(Zip(("quicksave/mod.toml", "name = \"Quicksave\""), ("quicksave/mod/init.lua", "-- q"),
                ("manifest.txt", "quicksave/mod.toml\nquicksave/mod/init.lua\n")), target);
            List<string> report = ModBundled.Install(target, mods, Refii.Core.Engine_Title.Fable2.ModExtension);
            AssertThat(report.Count).IsEqual(1);
            AssertThat(File.Exists(Path.Combine(mods, "quicksave.f2mod"))).IsTrue();
            AssertThat(ModLoadOrder.Read(mods).Enabled.Contains("quicksave")).IsFalse();
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>An entry that climbs out of the folder is refused and the old trees stay.</summary>
    [TestCase]
    public void BundledArchiveRefusesAnEscapingEntry()
    {
        string root = Scratch();
        try
        {
            string target = Path.Combine(root, "bundled-mods");
            AndroidBundledMods.Unpack(Zip(("a/mod.toml", "name = \"A\"")), target);
            AssertThrown(() => AndroidBundledMods.Unpack(Zip(("../escaped.txt", "x")), target)).IsInstanceOf<InvalidDataException>();
            AssertThat(File.Exists(Path.Combine(root, "escaped.txt"))).IsFalse();
            AssertThat(File.Exists(Path.Combine(target, "a/mod.toml"))).IsTrue();
        }
        finally { Directory.Delete(root, true); }
    }
}
