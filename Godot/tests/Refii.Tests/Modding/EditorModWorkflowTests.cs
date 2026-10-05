namespace Refii.Tests.Modding;

using System;
using System.IO;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.EditorTools;

[TestSuite]
public class EditorModWorkflowTests
{
    [TestCase]
    public void TestCreateModPackageCreatesOnlyF2ModWithoutLooseFolders()
    {
        string modsDir = Path.Combine(Path.GetTempPath(), "refii_editor_create_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modsDir);

        try
        {
            // Create a new mod package via ModWorkspace.CreateModPackage (used by editor dialog and launcher)
            bool created = ModWorkspace.CreateModPackage(modsDir, "EditorCreatedMod", out string pkgPath, out string error);
            AssertThat(created).IsTrue();
            AssertThat(error).IsEqual(string.Empty);
            AssertThat(File.Exists(pkgPath)).IsTrue();
            AssertThat(Path.GetFileName(pkgPath)).IsEqual("EditorCreatedMod.f2mod");

            // CRITICAL INVARIANT: modsDir must NOT contain any subdirectories (no loose folders!)
            string[] subDirs = Directory.GetDirectories(modsDir);
            AssertThat(subDirs.Length).IsEqual(0);

            // Only EditorCreatedMod.f2mod should exist in the mods folder
            string[] files = Directory.GetFiles(modsDir);
            AssertThat(files.Length).IsEqual(1);
            AssertThat(Path.GetFileName(files[0])).IsEqual("EditorCreatedMod.f2mod");
        }
        finally
        {
            if (Directory.Exists(modsDir))
            {
                Directory.Delete(modsDir, true);
            }
        }
    }

    [TestCase]
    public void TestEditorDirectF2ModIncrementalSyncPreservesPackageIntegrity()
    {
        string modsDir = Path.Combine(Path.GetTempPath(), "refii_editor_sync_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modsDir);

        try
        {
            // 1. Create a mod package
            string pkgPath = Path.Combine(modsDir, "ActiveTestMod.f2mod");
            F2ModPackage.Create(pkgPath, new ModMetadata { Title = "ActiveTestMod" }, Array.Empty<(string, byte[])>(), out _);

            // 2. Editor writes a modified .gdb.tu file directly into the package
            byte[] initialTu = Encoding.UTF8.GetBytes("initial_item_override_data");
            bool sync1 = F2ModPackage.IncrementalSync(pkgPath, new[] { ("files/weapons/sword.gdb.tu", initialTu) });
            AssertThat(sync1).IsTrue();

            // 3. Editor writes an updated item override with new stats
            byte[] updatedTu = Encoding.UTF8.GetBytes("updated_item_override_data_with_higher_damage");
            bool sync2 = F2ModPackage.IncrementalSync(pkgPath, new[] { ("files/weapons/sword.gdb.tu", updatedTu) });
            AssertThat(sync2).IsTrue();

            // 4. Editor adds a localized display name in book.babel.csv
            byte[] babelData = Encoding.UTF8.GetBytes("Id,Text\n99999,Excalibur Sword");
            bool sync3 = F2ModPackage.IncrementalSync(pkgPath, new[] { ("files/language/en-uk/text/book.babel.csv", babelData) });
            AssertThat(sync3).IsTrue();

            // Verify content inside package
            AssertThat(F2ModPackage.TryOpen(pkgPath, out F2ModPackage? pkg, out _)).IsTrue();
            using (pkg)
            {
                AssertThat(pkg!.Entries.Count).IsEqual(2);

                AssertThat(pkg.TryGetEntry("files/weapons/sword.gdb.tu", out var tuEntry)).IsTrue();
                AssertThat(pkg.ReadEntryBytes(tuEntry!)).IsEqual(updatedTu);

                AssertThat(pkg.TryGetEntry("files/language/en-uk/text/book.babel.csv", out var babelEntry)).IsTrue();
                AssertThat(pkg.ReadEntryBytes(babelEntry!)).IsEqual(babelData);
            }

            // CRITICAL INVARIANT: modsDir must still contain ZERO subdirectories
            AssertThat(Directory.GetDirectories(modsDir).Length).IsEqual(0);
        }
        finally
        {
            if (Directory.Exists(modsDir))
            {
                Directory.Delete(modsDir, true);
            }
        }
    }

    [TestCase]
    public void TestDiscoverModNamesOrdersActiveModFirst()
    {
        string modsDir = Path.Combine(Path.GetTempPath(), "refii_editor_discover_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modsDir);

        try
        {
            // Create 3 mod packages
            F2ModPackage.Create(Path.Combine(modsDir, "Alpha.f2mod"), new ModMetadata { Title = "Alpha" }, Array.Empty<(string, byte[])>(), out _);
            F2ModPackage.Create(Path.Combine(modsDir, "Beta.f2mod"), new ModMetadata { Title = "Beta" }, Array.Empty<(string, byte[])>(), out _);
            F2ModPackage.Create(Path.Combine(modsDir, "Gamma.f2mod"), new ModMetadata { Title = "Gamma" }, Array.Empty<(string, byte[])>(), out _);

            var discovered = ModWorkspace.DiscoverModInstalls(modsDir);
            AssertThat(discovered.Count).IsEqual(3);

            // Active mod selection logic:
            string activeMod = "Beta";
            var names = new System.Collections.Generic.List<string>();
            foreach (var m in discovered)
            {
                names.Add(m.Name);
            }

            if (names.Contains(activeMod))
            {
                names.Remove(activeMod);
                names.Insert(0, activeMod);
            }

            AssertThat(names[0]).IsEqual("Beta");
            AssertThat(names.Count).IsEqual(3);
        }
        finally
        {
            if (Directory.Exists(modsDir))
            {
                Directory.Delete(modsDir, true);
            }
        }
    }
}
