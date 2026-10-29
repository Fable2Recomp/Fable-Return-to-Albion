namespace Refii.Tests.Core;

using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;

/// <summary>
/// The move from the flat layout into the title folders: a pre-split install's data, saves, mods
/// and module land in Fable2's folders with their contents intact, the launcher's shared config
/// stays where it is, a second run moves nothing, and an item already in its title folder is never
/// overwritten by an older copy beside it. Every tree is built here.
/// </summary>
[TestSuite]
public class TitleLayoutTests
{
    private static string NewRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "refii_title_layout_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [TestCase]
    public void LegacyInstallMovesIntoFable2AndKeepsEveryFile()
    {
        string root = NewRoot();
        try
        {
            string assets = Path.Combine(root, "assets");
            string bin = Path.Combine(root, "bin");
            Put(Path.Combine(assets, "game", "data", "levels.bnk"), "banks");
            Put(Path.Combine(assets, "game", "default.xex"), "xex");
            Put(Path.Combine(assets, "save", "profile", "mainsave.bin"), "the player's save");
            Put(Path.Combine(assets, "mods", "souls_combat.f2mod"), "mod");
            Put(Path.Combine(assets, "metadata", "rexnet.toml"), "net");
            Put(Path.Combine(assets, "unpacked", "shaders", "compile_report.tsv"), "shaders");
            Put(Path.Combine(assets, "launcher", "config.cfg"), "shared launcher config");
            Put(Path.Combine(bin, "librefii_generated.so"), "module");
            Put(Path.Combine(bin, "build-info.json"), "record");
            Put(Path.Combine(bin, "librefii_core.so"), "shared native");

            List<string> report = Engine_TitleLayout.MigrateLegacyLayout(assets, bin, repoAssets: null);

            string fable2 = Path.Combine(assets, "Fable2");
            AssertThat(File.ReadAllText(Path.Combine(fable2, "game", "data", "levels.bnk"))).IsEqual("banks");
            AssertThat(File.ReadAllText(Path.Combine(fable2, "game", "default.xex"))).IsEqual("xex");
            AssertThat(File.ReadAllText(Path.Combine(fable2, "save", "profile", "mainsave.bin"))).IsEqual("the player's save");
            AssertThat(File.ReadAllText(Path.Combine(fable2, "mods", "souls_combat.f2mod"))).IsEqual("mod");
            AssertThat(File.ReadAllText(Path.Combine(fable2, "metadata", "rexnet.toml"))).IsEqual("net");
            AssertThat(File.Exists(Path.Combine(fable2, "unpacked", "shaders", "compile_report.tsv"))).IsTrue();
            AssertThat(File.ReadAllText(Path.Combine(bin, "Fable2", "librefii_generated.so"))).IsEqual("module");
            AssertThat(File.ReadAllText(Path.Combine(bin, "Fable2", "build-info.json"))).IsEqual("record");

            // Nothing is left behind twice, and what is shared stays shared.
            AssertThat(Directory.Exists(Path.Combine(assets, "game"))).IsFalse();
            AssertThat(Directory.Exists(Path.Combine(assets, "save"))).IsFalse();
            AssertThat(File.Exists(Path.Combine(bin, "librefii_generated.so"))).IsFalse();
            AssertThat(File.ReadAllText(Path.Combine(assets, "launcher", "config.cfg"))).IsEqual("shared launcher config");
            AssertThat(File.Exists(Path.Combine(bin, "librefii_core.so"))).IsTrue();
            AssertThat(report.Count).IsEqual(7);

            // A migrated install is left alone.
            AssertThat(Engine_TitleLayout.MigrateLegacyLayout(assets, bin, repoAssets: null).Count).IsEqual(0);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    /// Half-migrated: the title folder already holds a save. The flat copy beside it is older
    /// state and must not replace it — both are kept for the player to sort out.
    /// </summary>
    [TestCase]
    public void AnItemAlreadyInItsTitleFolderIsNeverOverwritten()
    {
        string root = NewRoot();
        try
        {
            string assets = Path.Combine(root, "assets");
            string bin = Path.Combine(root, "bin");
            Put(Path.Combine(assets, "Fable2", "save", "mainsave.bin"), "current");
            Put(Path.Combine(assets, "save", "mainsave.bin"), "older");
            Put(Path.Combine(assets, "game", "default.xex"), "xex");

            List<string> report = Engine_TitleLayout.MigrateLegacyLayout(assets, bin, repoAssets: null);

            AssertThat(File.ReadAllText(Path.Combine(assets, "Fable2", "save", "mainsave.bin"))).IsEqual("current");
            AssertThat(File.ReadAllText(Path.Combine(assets, "save", "mainsave.bin"))).IsEqual("older");
            AssertThat(File.Exists(Path.Combine(assets, "Fable2", "game", "default.xex"))).IsTrue();
            AssertThat(report.Count).IsEqual(1);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestCase]
    public void InterimFable3FolderAndDevRepoDataMove()
    {
        string root = NewRoot();
        try
        {
            string assets = Path.Combine(root, "app", "assets");
            string bin = Path.Combine(root, "app", "bin");
            string repoAssets = Path.Combine(root, "repo", "assets");
            Put(Path.Combine(assets, "refiii", "game", "default.xex"), "fable 3");
            Put(Path.Combine(repoAssets, "game", "data", "levels.bnk"), "dev banks");
            Put(Path.Combine(repoAssets, "update", ".gitignore"), "*");

            Engine_TitleLayout.MigrateLegacyLayout(assets, bin, repoAssets);

            AssertThat(File.ReadAllText(Path.Combine(assets, "Fable3", "game", "default.xex"))).IsEqual("fable 3");
            AssertThat(Directory.Exists(Path.Combine(assets, "refiii"))).IsFalse();
            AssertThat(File.ReadAllText(Path.Combine(repoAssets, "Fable2", "game", "data", "levels.bnk"))).IsEqual("dev banks");
            AssertThat(File.Exists(Path.Combine(repoAssets, "Fable2", "update", ".gitignore"))).IsTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
