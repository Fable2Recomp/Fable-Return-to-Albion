using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Mods;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>A .f2mod picked in Android's document picker, brought into the mods folder's downloads for the Modding panel's install.</summary>
[TestSuite]
public sealed class AndroidModPickerTests
{
    private static string Scratch()
    {
        string root = Path.Combine(Refii.Tests.TestTemp.Root, "refii-android-mods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Package(string directory, string file, string title)
    {
        string path = Path.Combine(directory, file);
        bool ok = F2ModPackage.Create(path, new ModMetadata { Title = title, Version = "1.0" },
            new List<(string, byte[])> { ("mod/init.lua", Encoding.UTF8.GetBytes("-- hello")) }, out string error);
        AssertThat(error).IsEmpty();
        AssertThat(ok).IsTrue();
        return path;
    }

    /// <summary>A file manager's URI carries the file name; the Downloads list's bare id does not.</summary>
    [TestCase]
    public void PickedUriNamesTheMod()
    {
        AssertThat(AndroidModPicker.NameFromUri("content://com.android.externalstorage.documents/document/primary%3ADownload%2Fhotbar.f2mod"))
            .IsEqual("hotbar");
        AssertThat(AndroidModPicker.NameFromUri("content://com.android.providers.downloads.documents/document/raw%3A%2Fstorage%2Femulated%2F0%2FDownload%2FFirst%20Person.F2MOD"))
            .IsEqual("First Person");
        AssertThat(AndroidModPicker.NameFromUri("content://com.android.providers.downloads.documents/document/msf%3A1000000123")).IsNull();
        AssertThat(AndroidModPicker.NameFromUri("content://x/document/primary%3ADownload%2Fnotes.txt")).IsNull();
        AssertThat(AndroidModPicker.NameFromTitle("Toggle Sprint!")).IsEqual("toggle_sprint");
        AssertThat(AndroidModPicker.NameFromTitle("  ")).IsEqual("mod");
    }

    /// <summary>The copy lands in the downloads folder under the URI's name, or the title's, inspected and ready to install.</summary>
    [TestCase]
    public void PickedPackageIsFetchedForInstall()
    {
        string root = Scratch();
        try
        {
            string mods = Path.Combine(root, "mods");
            string picked = Package(root, "anything.f2mod", "Toggle Sprint");
            void Copy(string target) => File.Copy(picked, target);

            ModInstallResult named = AndroidModPicker.Fetch("content://x/document/primary%3ADownload%2Fsprint.f2mod", mods, Copy);
            AssertThat(named.Error).IsEmpty();
            AssertThat(named.Ok).IsTrue();
            AssertThat(named.Path).IsEqual(Path.Combine(ModInstaller.DownloadsDirectory(mods), "sprint.f2mod"));

            ModInstallResult titled = AndroidModPicker.Fetch("content://x/document/msf%3A42", mods, Copy);
            AssertThat(titled.Error).IsEmpty();
            AssertThat(titled.Ok).IsTrue();
            AssertThat(Path.GetFileName(titled.Path)).IsEqual("toggle_sprint.f2mod");

            ModInstallResult installed = ModInstaller.InstallFromFile(titled.Path, mods);
            AssertThat(installed.Error).IsEmpty();
            AssertThat(installed.Ok).IsTrue();
            AssertThat(File.Exists(Path.Combine(mods, "toggle_sprint.f2mod"))).IsTrue();
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A picked file that is no package fails with a reason and leaves nothing behind.</summary>
    [TestCase]
    public void PickedNonPackageLeavesNothing()
    {
        string root = Scratch();
        try
        {
            string mods = Path.Combine(root, "mods");
            ModInstallResult result = AndroidModPicker.Fetch("content://x/document/msf%3A7", mods,
                target => File.WriteAllText(target, "not a package"));
            AssertThat(result.Ok).IsFalse();
            AssertThat(Directory.GetFiles(ModInstaller.DownloadsDirectory(mods)).Length).IsEqual(0);

            result = AndroidModPicker.Fetch("content://x/document/primary%3Abad.f2mod", mods,
                target => File.WriteAllText(target, "not a package"));
            AssertThat(result.Ok).IsFalse();
            AssertThat(Directory.GetFiles(ModInstaller.DownloadsDirectory(mods)).Length).IsEqual(0);
        }
        finally { Directory.Delete(root, true); }
    }
}
