namespace Refii.Tests.Modding;

using System;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

[TestSuite]
public class ModManifestTests
{
    [TestCase]
    public void TestAssetEnumerationIgnoresMetadataAndImports()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_manifest_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Valid asset files:
            string validAsset1 = Path.Combine(tempDir, "texture.png");
            string validAsset2 = Path.Combine(tempDir, "globals.gdb.tu");
            File.WriteAllText(validAsset1, "dummy_texture");
            File.WriteAllText(validAsset2, "dummy_tu");

            // Bookkeeping files that must be ignored:
            string metaFile = Path.Combine(tempDir, ModWorkspace.ModMetadataFilename);
            string manifestFile = Path.Combine(tempDir, ModWorkspace.ModManifestFilename);
            string importFile = Path.Combine(tempDir, "texture.png.import");
            File.WriteAllText(metaFile, "name = 'test'");
            File.WriteAllText(manifestFile, "{}");
            File.WriteAllText(importFile, "remap");

            var enumerated = ModManifest.EnumerateAssets(tempDir).ToList();

            // Must include valid assets:
            AssertThat(enumerated.Count).IsEqual(2);
            AssertThat(enumerated.Exists(p => p.EndsWith("texture.png"))).IsTrue();
            AssertThat(enumerated.Exists(p => p.EndsWith("globals.gdb.tu"))).IsTrue();

            // Must NOT include bookkeeping or import metadata:
            AssertThat(enumerated.Exists(p => p.EndsWith(ModWorkspace.ModMetadataFilename))).IsFalse();
            AssertThat(enumerated.Exists(p => p.EndsWith(ModWorkspace.ModManifestFilename))).IsFalse();
            AssertThat(enumerated.Exists(p => p.EndsWith(".import"))).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestTuAssetClassificationAsKindTu()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_manifest_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string tuFile = Path.Combine(tempDir, "globals.gdb.tu");
            string regularFile = Path.Combine(tempDir, "model.mdl");
            File.WriteAllText(tuFile, "dummy_gdb_tu");
            File.WriteAllText(regularFile, "dummy_model");

            var mod = new ModInstall
            {
                Name = "TestMod",
                Root = tempDir,
                HasFilesFolder = false,
            };

            var manifest = ModManifest.Build(mod);

            AssertThat(manifest.Entries.Count).IsEqual(2);

            var tuEntry = manifest.Entries.Find(e => e.Path.EndsWith("globals.gdb.tu", StringComparison.OrdinalIgnoreCase));
            AssertThat(tuEntry).IsNotNull();
            AssertThat(tuEntry!.Kind).IsEqual(ModManifestEntry.KindTu);

            var fileEntry = manifest.Entries.Find(e => e.Path.EndsWith("model.mdl", StringComparison.OrdinalIgnoreCase));
            AssertThat(fileEntry).IsNotNull();
            AssertThat(fileEntry!.Kind).IsEqual(ModManifestEntry.KindFile);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestManifestJsonSerializationRoundTrip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_manifest_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string manifestPath = Path.Combine(tempDir, "manifest.json");

            var original = new ModManifest
            {
                Name = "CoolMod",
                Version = "1.2.3",
                Generated = "2026-10-05T12:00:00Z",
                Stamp = "3:999999",
            };

            original.Entries.Add(new ModManifestEntry
            {
                Path = "files/globals.gdb.tu",
                Kind = ModManifestEntry.KindTu,
                Source = "files/globals.gdb.tu",
                Size = 1024,
                Sha1 = "abcdef1234567890",
            });

            original.Entries.Add(new ModManifestEntry
            {
                Path = "files/weapons/sword.mdl",
                Kind = ModManifestEntry.KindFile,
                Size = 2048,
                Sha1 = "1234567890abcdef",
            });

            original.Save(manifestPath);
            AssertThat(File.Exists(manifestPath)).IsTrue();

            var loaded = ModManifest.Load(manifestPath);
            AssertThat(loaded).IsNotNull();
            AssertThat(loaded!.Name).IsEqual("CoolMod");
            AssertThat(loaded.Version).IsEqual("1.2.3");
            AssertThat(loaded.Stamp).IsEqual("3:999999");
            AssertThat(loaded.Entries.Count).IsEqual(2);

            var tuEntry = loaded.Entries.Find(e => e.Path == "files/globals.gdb.tu");
            AssertThat(tuEntry).IsNotNull();
            AssertThat(tuEntry!.Kind).IsEqual(ModManifestEntry.KindTu);
            AssertThat(tuEntry.Size).IsEqual(1024L);
            AssertThat(tuEntry.Sha1).IsEqual("abcdef1234567890");

            var fileEntry = loaded.Entries.Find(e => e.Path == "files/weapons/sword.mdl");
            AssertThat(fileEntry).IsNotNull();
            AssertThat(fileEntry!.Kind).IsEqual(ModManifestEntry.KindFile);
            AssertThat(fileEntry.Size).IsEqual(2048L);
            AssertThat(fileEntry.Sha1).IsEqual("1234567890abcdef");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestSidecarsExcludedFromEnumerateAssets()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_manifest_sidecars_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "globals.gdb.tu"), "valid");
            File.WriteAllText(Path.Combine(tempDir, "globals.gdb.tu.uid"), "sidecar");
            File.WriteAllText(Path.Combine(tempDir, "model.mesh"), "valid");
            File.WriteAllText(Path.Combine(tempDir, "model.mesh.import"), "sidecar");
            File.WriteAllText(Path.Combine(tempDir, "old.f2zip"), "archive");
            File.WriteAllText(Path.Combine(tempDir, "old.bnk"), "archive");
            File.WriteAllText(Path.Combine(tempDir, "pkg.f2mod"), "package");

            var assets = ModManifest.EnumerateAssets(tempDir).Select(Path.GetFileName).ToList();

            AssertThat(assets.Count).IsEqual(2);
            AssertThat(assets.Contains("globals.gdb.tu")).IsTrue();
            AssertThat(assets.Contains("model.mesh")).IsTrue();
            AssertThat(assets.Contains("globals.gdb.tu.uid")).IsFalse();
            AssertThat(assets.Contains("model.mesh.import")).IsFalse();
            AssertThat(assets.Contains("old.f2zip")).IsFalse();
            AssertThat(assets.Contains("old.bnk")).IsFalse();
            AssertThat(assets.Contains("pkg.f2mod")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestManifestBuildFromF2ModPackage()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_manifest_pkg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string pkgPath = Path.Combine(tempDir, "MyMod.f2mod");

        try
        {
            var meta = new ModMetadata { Title = "MyMod", Version = "2.0.0" };
            var files = new[]
            {
                ("globals/globals.gdb.tu", System.Text.Encoding.UTF8.GetBytes("tu_data")),
                ("language/en-uk/text/book.babel.csv", System.Text.Encoding.UTF8.GetBytes("tag,text\n1,one")),
                ("textures/icon.png", System.Text.Encoding.UTF8.GetBytes("png_bytes"))
            };

            F2ModPackage.Create(pkgPath, meta, files, out _);

            var mod = new ModInstall
            {
                Name = "MyMod",
                PackagePath = pkgPath,
                Metadata = meta,
            };

            var manifest = ModManifest.Build(mod);

            AssertThat(manifest.Name).IsEqual("MyMod");
            AssertThat(manifest.Version).IsEqual("2.0.0");
            AssertThat(manifest.Entries.Count).IsEqual(3);

            var tuEntry = manifest.Entries.Find(e => e.Path.EndsWith("globals.gdb.tu"));
            AssertThat(tuEntry).IsNotNull();
            AssertThat(tuEntry!.Kind).IsEqual(ModManifestEntry.KindTu);

            var rowsEntry = manifest.Entries.Find(e => e.Path.EndsWith("book.babel"));
            AssertThat(rowsEntry).IsNotNull();
            AssertThat(rowsEntry!.Kind).IsEqual(ModManifestEntry.KindRows);

            var fileEntry = manifest.Entries.Find(e => e.Path.EndsWith("icon.png"));
            AssertThat(fileEntry).IsNotNull();
            AssertThat(fileEntry!.Kind).IsEqual(ModManifestEntry.KindFile);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
