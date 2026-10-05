namespace Refii.Tests.Modding;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

[TestSuite]
public class ModLoadOrderTests
{
    [TestCase]
    public void TestReadAndWriteTomlRoundTrip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_loadorder_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var original = new ModLoadOrder();
            original.Enabled.Add("high-priority-mod");
            original.Enabled.Add("medium-priority-mod");
            original.Enabled.Add("low-priority-mod");

            original.Pins["files/weapons/toy_gun.gdb.tu"] = "low-priority-mod";

            bool written = original.Write(tempDir, out string writeError);
            AssertThat(written).IsTrue();
            AssertThat(writeError).IsEqual(string.Empty);

            string tomlPath = Path.Combine(tempDir, ModLoadOrder.Filename);
            AssertThat(File.Exists(tomlPath)).IsTrue();

            var loaded = ModLoadOrder.Read(tempDir);
            AssertThat(loaded.Enabled.Count).IsEqual(3);
            AssertThat(loaded.Enabled[0]).IsEqual("high-priority-mod");
            AssertThat(loaded.Enabled[1]).IsEqual("medium-priority-mod");
            AssertThat(loaded.Enabled[2]).IsEqual("low-priority-mod");

            AssertThat(loaded.Pins.Count).IsEqual(1);
            AssertThat(loaded.Pins.ContainsKey("files/weapons/toy_gun.gdb.tu")).IsTrue();
            AssertThat(loaded.Pins["files/weapons/toy_gun.gdb.tu"]).IsEqual("low-priority-mod");
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
    public void TestLegacyEnabledTxtFallback()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_legacy_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create legacy enabled.txt file:
            string legacyPath = Path.Combine(tempDir, ModLoadOrder.LegacyFilename);
            File.WriteAllText(legacyPath, "# Comment line\nmod-alpha\nmod-beta\nmod-gamma\n");

            var loaded = ModLoadOrder.Read(tempDir);
            AssertThat(loaded.Enabled.Count).IsEqual(3);
            AssertThat(loaded.Enabled[0]).IsEqual("mod-alpha");
            AssertThat(loaded.Enabled[1]).IsEqual("mod-beta");
            AssertThat(loaded.Enabled[2]).IsEqual("mod-gamma");
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
    public void TestPathNormalization()
    {
        AssertThat(ModLoadOrder.Normalize("FILES\\GLOBALS\\WEAPONS.GDB.TU"))
            .IsEqual("files/globals/weapons.gdb.tu");

        AssertThat(ModLoadOrder.Normalize("/art/gui/hud.bgf/"))
            .IsEqual("art/gui/hud.bgf");

        AssertThat(ModLoadOrder.Normalize("mixed/Path\\Separator/Test.DDS"))
            .IsEqual("mixed/path/separator/test.dds");
    }
}
