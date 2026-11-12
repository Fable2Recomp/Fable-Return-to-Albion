namespace Refii.Tests.Launcher;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Launcher.Core;
using Refii.Launcher.Editing;
using Refii.Mods;

/// <summary>
/// End-to-end feature tests for per-title modding scope (<c>.f2mod</c> vs <c>.f3mod</c>),
/// mod input keybinding ownership in <see cref="RuntimeCVarSet"/> and <see cref="RuntimeSettingsModel"/>,
/// and <see cref="EditorWorkspace.BuildLaunchEnvironment"/> passing <c>REFII_TITLE</c>.
/// </summary>
[TestSuite]
public class ModdingAndDrawerScopeTests
{
    private string _tempDir = string.Empty;
    private Engine_Title _savedActive = Engine_Title.Fable2;

    [BeforeTest]
    public void SetUp()
    {
        _savedActive = Engine_Title.Active;
        _tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_mod_scope_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [AfterTest]
    public void TearDown()
    {
        Engine_Title.Active = _savedActive;
        RuntimeCVarSet.Refresh();
        if (_tempDir.Length != 0 && Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    [TestCase]
    public void TestModPackageDiscoveryInstallAndCreationRespectSelectedTitleExtension()
    {
        string f2Mods = Path.Combine(_tempDir, "Fable2", "mods");
        string f3Mods = Path.Combine(_tempDir, "Fable3", "mods");
        Directory.CreateDirectory(f2Mods);
        Directory.CreateDirectory(f3Mods);

        // Even while Engine_Title.Active is Fable2, creating and discovering Fable3 mods uses .f3mod:
        Engine_Title.Active = Engine_Title.Fable2;

        bool ok2 = ModWorkspace.CreateModPackage(Engine_Title.Fable2, f2Mods, "AlbionTweaks", out string f2Pkg, out string err2);
        AssertThat(ok2).OverrideFailureMessage("CreateModPackage Fable2 failed: " + err2).IsTrue();
        AssertThat(f2Pkg.EndsWith(".f2mod", StringComparison.OrdinalIgnoreCase)).IsTrue();

        bool ok3 = ModWorkspace.CreateModPackage(Engine_Title.Fable3, f3Mods, "AuroraTweaks", out string f3Pkg, out string err3);
        AssertThat(ok3).OverrideFailureMessage("CreateModPackage Fable3 failed: " + err3).IsTrue();
        AssertThat(f3Pkg.EndsWith(".f3mod", StringComparison.OrdinalIgnoreCase)).IsTrue();

        // Cross-contaminate stray wrong-extension files to verify strict extension filtering:
        File.Copy(f3Pkg, Path.Combine(f2Mods, "StrayAurora.f3mod"));
        File.Copy(f2Pkg, Path.Combine(f3Mods, "StrayAlbion.f2mod"));

        List<ModInstall> f2Discovered = ModWorkspace.DiscoverModInstalls(f2Mods, Engine_Title.Fable2.ModExtension);
        List<ModInstall> f3Discovered = ModWorkspace.DiscoverModInstalls(f3Mods, Engine_Title.Fable3.ModExtension);

        AssertThat(f2Discovered.Select(m => m.Name).ToArray()).ContainsExactly("AlbionTweaks");
        AssertThat(f3Discovered.Select(m => m.Name).ToArray()).ContainsExactly("AuroraTweaks");

        // ModInstaller with explicit .f3mod extension accepts .f3mod and rejects .f2mod even while Active == Fable2:
        string stagingDir = Path.Combine(_tempDir, "staging");
        Directory.CreateDirectory(stagingDir);
        string candidateF3 = Path.Combine(stagingDir, "RoyalGuard.f3mod");
        F2ModPackage.Create(candidateF3, new ModMetadata { Title = "Royal Guard", Version = "1.0.0" }, Array.Empty<(string, byte[])>(), out _);

        ModInstallResult inspected = ModInstaller.Inspect(candidateF3, f3Mods, Engine_Title.Fable3.ModExtension);
        AssertThat(inspected.Ok).OverrideFailureMessage("F3 inspect failed: " + inspected.Error).IsTrue();

        ModInstallResult wrongExt = ModInstaller.Inspect(f2Pkg, f3Mods, Engine_Title.Fable3.ModExtension);
        AssertThat(wrongExt.Ok).IsFalse();

        ModInstallResult installed = ModInstaller.InstallFromFile(candidateF3, f3Mods, Engine_Title.Fable3.ModExtension);
        AssertThat(installed.Ok).OverrideFailureMessage("F3 install failed: " + installed.Error).IsTrue();
        AssertThat(File.Exists(Path.Combine(f3Mods, "RoyalGuard.f3mod"))).IsTrue();
    }

    [TestCase]
    public void TestPerTitleModInputBindingsCarryOwnerTitleAndRouteToOwningToml()
    {
        string f2Mods = Path.Combine(_tempDir, "Fable2", "mods");
        string f3Mods = Path.Combine(_tempDir, "Fable3", "mods");
        Directory.CreateDirectory(f2Mods);
        Directory.CreateDirectory(f3Mods);

        byte[] f2InputJson = Encoding.UTF8.GetBytes("""
            {
              "quick_lantern": { "label": "Quick Lantern", "keys": ["L"] }
            }
            """);
        string f2Pkg = Path.Combine(f2Mods, "lantern_mod.f2mod");
        bool created2 = F2ModPackage.Create(
            f2Pkg,
            new ModMetadata { Title = "Lantern Mod", Version = "1.0" },
            new[] { ("input.json", f2InputJson) },
            out string cErr2);
        AssertThat(created2).OverrideFailureMessage("F2 package create failed: " + cErr2).IsTrue();

        var f2Order = new ModLoadOrder();
        f2Order.RegisterMod("lantern_mod", enabled: true);
        f2Order.Write(f2Mods, out _);

        byte[] f3InputJson = Encoding.UTF8.GetBytes("""
            {
              "wings_toggle": { "label": "Toggle Wings", "keys": ["G"] }
            }
            """);
        string f3Pkg = Path.Combine(f3Mods, "sanctuary_wings.f3mod");
        bool created3 = F2ModPackage.Create(
            f3Pkg,
            new ModMetadata { Title = "Sanctuary Wings", Version = "1.0" },
            new[] { ("input.json", f3InputJson) },
            out string cErr3);
        AssertThat(created3).OverrideFailureMessage("F3 package create failed: " + cErr3).IsTrue();

        var f3Order = new ModLoadOrder();
        f3Order.RegisterMod("sanctuary_wings", enabled: true);
        f3Order.Write(f3Mods, out _);

        var roots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Engine_Title.Fable2.Key] = f2Mods,
            [Engine_Title.Fable3.Key] = f3Mods,
        };

        string f2Toml = Path.Combine(_tempDir, "refii.toml");
        string f3Toml = Path.Combine(_tempDir, "refiii.toml");

        var model = new RuntimeSettingsModel();
        model.ReloadFromPaths(f2Toml, f3Toml, modsRootsByTitleKey: roots, ensureDefaults: false);

        string f2Key = "key_lantern_mod.quick_lantern";
        string f3Key = "key_sanctuary_wings.wings_toggle";

        RuntimeCVarDefinition? f2Def = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == f2Key);
        RuntimeCVarDefinition? f3Def = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == f3Key);

        AssertThat(f2Def).IsNotNull();
        AssertThat(f3Def).IsNotNull();
        AssertThat(f2Def!.OwnerTitle).IsEqual(Engine_Title.Fable2.Folder);
        AssertThat(f3Def!.OwnerTitle).IsEqual(Engine_Title.Fable3.Folder);

        model.Values[f2Key] = "K";
        model.Values[f3Key] = "H";
        model.Save();

        SortedDictionary<string, string> f2Saved = RuntimeConfig.ReadValues(f2Toml);
        SortedDictionary<string, string> f3Saved = RuntimeConfig.ReadValues(f3Toml);

        AssertThat(f2Saved[f2Key]).IsEqual("K");
        AssertThat(f2Saved.ContainsKey(f3Key)).IsFalse();

        AssertThat(f3Saved[f3Key]).IsEqual("H");
        AssertThat(f3Saved.ContainsKey(f2Key)).IsFalse();
    }

    [TestCase]
    public void TestEditorWorkspaceLaunchEnvironmentPropagatesSelectedTitleKey()
    {
        Dictionary<string, string> f2Env = EditorWorkspace.BuildLaunchEnvironment(Engine_Title.Fable2);
        Dictionary<string, string> f3Env = EditorWorkspace.BuildLaunchEnvironment(Engine_Title.Fable3);

        AssertThat(f2Env[Engine_Title.EnvironmentVariable]).IsEqual(Engine_Title.Fable2.Key);
        AssertThat(f3Env[Engine_Title.EnvironmentVariable]).IsEqual(Engine_Title.Fable3.Key);
        AssertThat(f2Env.ContainsKey("REFII_APP_ROOT")).IsTrue();
        AssertThat(f3Env.ContainsKey("REFII_APP_ROOT")).IsTrue();
    }
}
