namespace Refii.Tests.Modding;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

[TestSuite]
public class ModBundledTests
{
    private static string MakeSource(string root)
    {
        string tree = Path.Combine(root, "src", "demo");
        Directory.CreateDirectory(Path.Combine(tree, "mod"));
        File.WriteAllText(Path.Combine(tree, "mod.toml"), "name = \"Demo\"\nversion = \"0.1.0\"\n");
        File.WriteAllText(Path.Combine(tree, "README.md"), "not packed");
        File.WriteAllText(Path.Combine(tree, "mod", "init.lua"), "Demo = 1\n");
        return tree;
    }

    [TestCase]
    public void CollectTakesEveryFileButTheMetadataAndReadme()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii_bundled_" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = ModBundled.Collect(MakeSource(root));
            AssertThat(files.Count).IsEqual(1);
            AssertThat(files[0].Path).IsEqual("mod/init.lua");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase]
    public void InstallsUpdatesAndLeavesEditsAlone()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii_bundled_" + Guid.NewGuid().ToString("N"));
        try
        {
            string tree = MakeSource(root);
            string mods = Path.Combine(root, "mods");
            string package = Path.Combine(mods, "demo.f2mod");

            AssertThat(ModBundled.Install(Path.Combine(root, "src"), mods)[0]).Contains("installed");
            AssertThat(F2ModPackage.TryOpen(package, out F2ModPackage? packed, out _)).IsTrue();
            using (packed!)
            {
                AssertThat(packed!.Metadata.Title).IsEqual("Demo");
                AssertThat(packed.Entries.Count).IsEqual(1);
                AssertThat(packed.TryGetEntry("mod/init.lua", out _)).IsTrue();
            }

            AssertThat(ModBundled.Install(Path.Combine(root, "src"), mods)[0]).Contains("current");

            File.WriteAllText(Path.Combine(tree, "mod", "init.lua"), "Demo = 2\n");
            AssertThat(ModBundled.Install(Path.Combine(root, "src"), mods)[0]).Contains("installed");

            // An edit in the Mod Studio: the next update must not overwrite it.
            F2ModPackage.IncrementalSync(package, new[] { ("mod/extra.lua", new byte[] { 1 }) });
            File.WriteAllText(Path.Combine(tree, "mod", "init.lua"), "Demo = 3\n");
            AssertThat(ModBundled.Install(Path.Combine(root, "src"), mods)[0]).Contains("left alone");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestCase]
    public void AStaleFileLeftByAnUnpackIsNotPacked()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii_bundled_" + Guid.NewGuid().ToString("N"));
        try
        {
            string tree = MakeSource(root);
            // The release that shipped this tree no longer has mod/camera.lua; an unpack over the
            // previous install left it behind.
            File.WriteAllText(Path.Combine(tree, "mod", "camera.lua"), "Stale = 1\n");
            File.WriteAllText(Path.Combine(root, "src", "manifest.txt"),
                "demo/mod.toml\ndemo/README.md\ndemo/mod/init.lua\n");
            string mods = Path.Combine(root, "mods");

            AssertThat(ModBundled.Install(Path.Combine(root, "src"), mods)[0]).Contains("installed");
            AssertThat(F2ModPackage.TryOpen(Path.Combine(mods, "demo.f2mod"), out F2ModPackage? packed, out _)).IsTrue();
            using (packed!)
            {
                AssertThat(packed!.TryGetEntry("mod/init.lua", out _)).IsTrue();
                AssertThat(packed.TryGetEntry("mod/camera.lua", out _)).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
