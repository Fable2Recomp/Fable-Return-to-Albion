namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
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

    [TestCase]
    public void TestPruneMissingModsPreservesInstalledAndRemovesStale()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_loadorder_prune_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Initial load order contains two mods, but one is a stale "ghost" mod that was deleted or doesn't exist on disk
            var order = new ModLoadOrder();
            order.Enabled.Add("InstalledMod");
            order.Enabled.Add("GhostMod");
            order.Write(tempDir, out _);

            // Simulate installed mods discovered on disk
            var installedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "InstalledMod" };

            // Read order back and prune missing mods (same logic used in ModdingPanel.RefreshState)
            var loadedOrder = ModLoadOrder.Read(tempDir);
            AssertThat(loadedOrder.Enabled.Count).IsEqual(2);

            loadedOrder.Enabled.RemoveAll(name => !installedNames.Contains(name));

            AssertThat(loadedOrder.Enabled.Count).IsEqual(1);
            AssertThat(loadedOrder.Enabled[0]).IsEqual("InstalledMod");
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
    public void TestSingleModLoadOrderBoundsCannotMoveUpOrDown()
    {
        var order = new ModLoadOrder();
        order.Enabled.Add("OnlyMod");

        // When there is only 1 enabled mod, position is 0
        int position = order.Enabled.IndexOf("OnlyMod");
        AssertThat(position).IsEqual(0);

        // Can move up: position > 0 => false
        bool canMoveUp = position > 0;
        AssertThat(canMoveUp).IsFalse();

        // Can move down: position + 1 < order.Enabled.Count => 0 + 1 < 1 => false
        bool canMoveDown = position + 1 < order.Enabled.Count;
        AssertThat(canMoveDown).IsFalse();
    }

    [TestCase]
    public void TestDeleteModUpdatesOrderAndDiscoversNone()
    {
        string modsDir = Path.Combine(Path.GetTempPath(), "refii_delete_mod_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modsDir);

        try
        {
            // Create a .f2mod package and enable it
            string pkgPath = Path.Combine(modsDir, "ToDelete.f2mod");
            F2ModPackage.Create(pkgPath, new ModMetadata { Title = "ToDelete" }, Array.Empty<(string, byte[])>(), out _);

            var order = new ModLoadOrder();
            order.Enabled.Add("ToDelete");
            order.Write(modsDir, out _);

            AssertThat(File.Exists(pkgPath)).IsTrue();
            AssertThat(ModWorkspace.DiscoverModInstalls(modsDir).Count).IsEqual(1);

            // Delete the mod file (same logic as ModdingPanel.OnDeleteMod)
            File.Delete(pkgPath);
            order.Enabled.Remove("ToDelete");
            order.Write(modsDir, out _);

            // Verify mod is gone from disk and from discover list
            AssertThat(File.Exists(pkgPath)).IsFalse();
            AssertThat(ModWorkspace.DiscoverModInstalls(modsDir).Count).IsEqual(0);

            // Verify loadorder.toml reflects the deletion
            var reloadedOrder = ModLoadOrder.Read(modsDir);
            AssertThat(reloadedOrder.Enabled.Count).IsEqual(0);
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
    public void TestOrderListPreservesDisabledModPositionAndRoundTrips()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_order_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var order = new ModLoadOrder();
            // 4 mods: mod1, mod2, mod3, mod4
            order.Order.Add("mod1");
            order.Order.Add("mod2");
            order.Order.Add("mod3");
            order.Order.Add("mod4");

            // mod2 is disabled:
            order.Enabled.Add("mod1");
            order.Enabled.Add("mod3");
            order.Enabled.Add("mod4");

            bool written = order.Write(tempDir, out string writeError);
            AssertThat(written).IsTrue();
            AssertThat(writeError).IsEqual(string.Empty);

            var loaded = ModLoadOrder.Read(tempDir);
            // Verify Order has all 4 mods in position
            AssertThat(loaded.Order.Count).IsEqual(4);
            AssertThat(loaded.Order[0]).IsEqual("mod1");
            AssertThat(loaded.Order[1]).IsEqual("mod2");
            AssertThat(loaded.Order[2]).IsEqual("mod3");
            AssertThat(loaded.Order[3]).IsEqual("mod4");

            // Verify Enabled only has 3 mods
            AssertThat(loaded.Enabled.Count).IsEqual(3);
            AssertThat(loaded.Enabled[0]).IsEqual("mod1");
            AssertThat(loaded.Enabled[1]).IsEqual("mod3");
            AssertThat(loaded.Enabled[2]).IsEqual("mod4");
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
    public void TestLegacyTomlWithoutOrderArrayPopulatesOrderFromEnabled()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_legacy_order_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string tomlWithoutOrder = "enabled = [\"modA\", \"modB\"]\n";
            File.WriteAllText(Path.Combine(tempDir, ModLoadOrder.Filename), tomlWithoutOrder);

            var loaded = ModLoadOrder.Read(tempDir);
            AssertThat(loaded.Enabled.Count).IsEqual(2);
            AssertThat(loaded.Order.Count).IsEqual(2);
            AssertThat(loaded.Order[0]).IsEqual("modA");
            AssertThat(loaded.Order[1]).IsEqual("modB");
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

