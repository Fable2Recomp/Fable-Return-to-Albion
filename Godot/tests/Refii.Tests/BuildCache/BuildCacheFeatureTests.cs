namespace Refii.Tests.BuildCache;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using GdUnit4;
using Refii.BuildCache;
using static GdUnit4.Assertions;

/// <summary>
/// End-to-end multi-subsystem feature tests exercising the full <c>Refii.BuildCache</c> staging
/// and incremental ZIP packaging lifecycle across cold runs, warm no-ops, single-DLL updates,
/// orphan pruning, Zig fast-path stamp skipping, and forbidden-artefact safety checks.
/// </summary>
[TestSuite]
public sealed class BuildCacheFeatureTests
{
    /// <summary>
    /// Exercises a realistic 5-stage build iteration lifecycle and verifies byte-for-byte archive
    /// integrity at every transition.
    /// </summary>
    [TestCase]
    public void PackageLifecycle_ColdStage_WarmNoOp_SingleDllDelta_OrphanPrune_AndSafetyGate()
    {
        string sandbox = Path.Combine(TestTemp.Root, $"pkg-e2e-{Guid.NewGuid():N}");
        string repoRoot = Path.Combine(sandbox, "repo");
        string projectDir = Path.Combine(repoRoot, "Godot");
        string stagingRoot = Path.Combine(projectDir, "build", "x86_64");
        string zigCacheDir = Path.Combine(sandbox, "zig-cache");
        string workDir = Path.Combine(sandbox, "out", "package", "linux-x86_64");
        string cacheDir = Path.Combine(sandbox, "out", "cache");
        const string appName = "Fable 2 Return to Albion";
        const string outBinary = "Fable 2 Return to Albion.x86_64";
        string appDir = Path.Combine(workDir, appName);
        string zipPath = Path.Combine(sandbox, "out", "package", "refii-linux-x86_64.zip");

        Directory.CreateDirectory(Path.Combine(projectDir, "src"));
        Directory.CreateDirectory(Path.Combine(projectDir, ".godot"));
        Directory.CreateDirectory(Path.Combine(projectDir, "levels"));
        Directory.CreateDirectory(Path.Combine(stagingRoot, "bin", "Fable2"));
        Directory.CreateDirectory(Path.Combine(stagingRoot, "bin", "recompile", "include", "00"));
        Directory.CreateDirectory(Path.Combine(stagingRoot, "cache"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "Fable2", "metadata"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "Fable2", "mods-src", "hotbar"));
        Directory.CreateDirectory(Path.Combine(zigCacheDir, "lib"));
        Directory.CreateDirectory(appDir);

        try
        {
            // Populate repository & staging inputs (including developer/forbidden files that MUST be excluded).
            File.WriteAllText(Path.Combine(repoRoot, "LICENSE"), "MIT License\n");
            File.WriteAllText(Path.Combine(repoRoot, "THIRD_PARTY_NOTICES.txt"), "Notices\n");
            File.WriteAllText(Path.Combine(projectDir, "project.godot"), "config_version=5\n");
            File.WriteAllText(Path.Combine(projectDir, "src", "Host.cs"), "public class Host {}\n");
            File.WriteAllText(Path.Combine(projectDir, ".godot", "ignored_editor_cache.bin"), "cache");
            File.WriteAllText(Path.Combine(projectDir, "levels", "baked_level.scn"), "non-redistributable");

            File.WriteAllText(Path.Combine(repoRoot, "Fable2", "metadata", "rexnet.toml"), "title_id = 0x4D5307F1\n");
            File.WriteAllText(Path.Combine(repoRoot, "Fable2", "mods-src", "hotbar", "mod.toml"), "id = 'hotbar'\n");
            string extraModFile = Path.Combine(repoRoot, "Fable2", "mods-src", "hotbar", "extra.lua");
            File.WriteAllText(extraModFile, "return true\n");

            string coreSo = Path.Combine(stagingRoot, "bin", "librefii_core.so");
            string bridgeSo = Path.Combine(stagingRoot, "bin", "Fable2", "librefii_hostbridge.so");
            File.WriteAllText(coreSo, string.Concat(System.Linq.Enumerable.Repeat("CORE_V1_", 400)));
            File.WriteAllText(bridgeSo, string.Concat(System.Linq.Enumerable.Repeat("BRIDGE_V1_", 400)));
            File.WriteAllText(Path.Combine(stagingRoot, "bin", "recompile", "include", "00", "rex.h"), "#pragma once\n");

            // Forbidden & developer-state files in staging root that must NEVER ship.
            File.WriteAllText(Path.Combine(stagingRoot, "bin", "Fable2", "librefii_generated.so"), "GAME_DERIVED");
            File.WriteAllText(Path.Combine(stagingRoot, "refii.toml"), "game_data_root = '/dev/machine'");
            File.WriteAllText(Path.Combine(stagingRoot, "cache", "converted_tex.bin"), "2GB_CACHE");

            // Cached unpacked Zig toolchain with pin stamp.
            File.WriteAllText(Path.Combine(zigCacheDir, "zig"), "#!/bin/sh\necho zig\n");
            File.WriteAllText(Path.Combine(zigCacheDir, "lib", "std.zig"), "pub const std = {};\n");
            File.WriteAllText(Path.Combine(zigCacheDir, ".refii-zig-stamp"), "0.16.0 linux x86_64 deadbeef\n");

            // Simulate exported launcher binary already placed by GodotExportCommand.
            File.WriteAllText(
                Path.Combine(appDir, outBinary),
                "ELF.../.godot/mono/publish/linux_x86_64/refii-launcher.dll");
            var preservedExport = new HashSet<string>(StringComparer.Ordinal) { outBinary };

            var hashIndex = new FileHashIndex();
            var stageOpts = new StagePackageOptions(
                RepoRoot: repoRoot,
                ProjectDir: projectDir,
                StagingRoot: stagingRoot,
                WorkDir: workDir,
                AppName: appName,
                OutBinary: outBinary,
                ToolPlatform: "linux",
                ToolArch: "x86_64",
                BundleZig: true,
                ZigUnpackedDir: zigCacheDir,
                CrtDirs: Array.Empty<string>(),
                ObjdumpPath: string.Empty,
                StripPath: string.Empty,
                GodotFloor: "4.7",
                GodotFlavour: "mono",
                DotnetMajor: "8",
                CacheDir: cacheDir,
                PreservedExportFiles: preservedExport);

            // ---------------- STAGE 1: COLD STAGE + COLD PARALLEL ZIP ----------------
            int rc1 = StagePackageCommand.Execute(stageOpts, hashIndex, out StagePackageStats stageStats1);
            AssertThat(rc1).IsEqual(0);
            AssertThat(stageStats1.ZigFastPathHit).IsFalse();
            AssertThat(stageStats1.CopiedFiles).IsGreater(0);

            // Verify exclusions and generated manifests.
            AssertThat(File.Exists(Path.Combine(appDir, "bin", "Fable2", "librefii_generated.so"))).IsFalse();
            AssertThat(File.Exists(Path.Combine(appDir, "refii.toml"))).IsFalse();
            AssertThat(Directory.Exists(Path.Combine(appDir, "cache"))).IsFalse();
            AssertThat(Directory.Exists(Path.Combine(appDir, "sdk", "levels"))).IsFalse();
            AssertThat(File.Exists(Path.Combine(appDir, "bin", "recompile", "zig", ".refii-zig-stamp"))).IsFalse();
            AssertThat(File.Exists(Path.Combine(appDir, "bin", "recompile", "zig", "zig"))).IsTrue();
            AssertThat(File.Exists(Path.Combine(appDir, "sdk", "pins.json"))).IsTrue();
            AssertThat(File.ReadAllText(Path.Combine(appDir, "bundled-mods", "Fable2", "manifest.txt")))
                .IsEqual("hotbar/extra.lua\nhotbar/mod.toml\n");

            ZipPackStats zipStats1 = IncrementalZipPacker.PackDirectory(appDir, appName, zipPath, hashIndex);
            AssertThat(zipStats1.NoOp).IsFalse();
            AssertThat(zipStats1.CompressedEntries).IsEqual(zipStats1.TotalEntries);
            AssertThat(zipStats1.ReusedStreamEntries).IsEqual(0);

            // ---------------- STAGE 2: WARM NO-OP REBUILD ----------------
            int rc2 = StagePackageCommand.Execute(stageOpts, hashIndex, out StagePackageStats stageStats2);
            AssertThat(rc2).IsEqual(0);
            AssertThat(stageStats2.ZigFastPathHit).IsTrue();
            AssertThat(stageStats2.CopiedFiles).IsEqual(0);
            AssertThat(stageStats2.DeletedOrphanFiles).IsEqual(0);

            ZipPackStats zipStats2 = IncrementalZipPacker.PackDirectory(appDir, appName, zipPath, hashIndex);
            AssertThat(zipStats2.NoOp).IsTrue();
            AssertThat(zipStats2.CompressedEntries).IsEqual(0);

            // ---------------- STAGE 3: SINGLE NATIVE BINARY EDIT ----------------
            string updatedBridgePayload = string.Concat(System.Linq.Enumerable.Repeat("BRIDGE_V2_UPDATED_", 450));
            File.WriteAllText(bridgeSo, updatedBridgePayload);
            File.SetLastWriteTimeUtc(bridgeSo, DateTime.UtcNow.AddMinutes(3));

            int rc3 = StagePackageCommand.Execute(stageOpts, hashIndex, out StagePackageStats stageStats3);
            AssertThat(rc3).IsEqual(0);
            AssertThat(stageStats3.ZigFastPathHit).IsTrue();
            AssertThat(stageStats3.CopiedFiles).IsEqual(1);

            ZipPackStats zipStats3 = IncrementalZipPacker.PackDirectory(appDir, appName, zipPath, hashIndex);
            AssertThat(zipStats3.NoOp).IsFalse();
            AssertThat(zipStats3.CompressedEntries).IsEqual(1);
            AssertThat(zipStats3.ReusedStreamEntries).IsEqual(zipStats3.TotalEntries - 1);

            using (ZipArchive verifyDelta = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry? bridgeEntry = verifyDelta.GetEntry($"{appName}/bin/Fable2/librefii_hostbridge.so");
                AssertThat(bridgeEntry).IsNotNull();
                using var sr = new StreamReader(bridgeEntry!.Open(), Encoding.UTF8);
                AssertThat(sr.ReadToEnd()).IsEqual(updatedBridgePayload);
            }

            // ---------------- STAGE 4: FILE DELETION & ORPHAN PRUNING ----------------
            File.Delete(extraModFile);
            string plantedOrphan = Path.Combine(appDir, "bin", "obsolete_stale.so");
            File.WriteAllText(plantedOrphan, "stale");

            int rc4 = StagePackageCommand.Execute(stageOpts, hashIndex, out StagePackageStats stageStats4);
            AssertThat(rc4).IsEqual(0);
            AssertThat(File.Exists(plantedOrphan)).IsFalse();
            AssertThat(File.Exists(Path.Combine(appDir, "bundled-mods", "Fable2", "hotbar", "extra.lua"))).IsFalse();
            AssertThat(File.ReadAllText(Path.Combine(appDir, "bundled-mods", "Fable2", "manifest.txt")))
                .IsEqual("hotbar/mod.toml\n");
            AssertThat(stageStats4.DeletedOrphanFiles).IsEqual(2);

            ZipPackStats zipStats4 = IncrementalZipPacker.PackDirectory(appDir, appName, zipPath, hashIndex);
            AssertThat(zipStats4.TotalEntries).IsEqual(zipStats3.TotalEntries - 1);
            using (ZipArchive verifyPruned = ZipFile.OpenRead(zipPath))
            {
                AssertThat(verifyPruned.GetEntry($"{appName}/bin/obsolete_stale.so")).IsNull();
                AssertThat(verifyPruned.GetEntry($"{appName}/bundled-mods/Fable2/hotbar/extra.lua")).IsNull();
                AssertThat(verifyPruned.GetEntry($"{appName}/bundled-mods/Fable2/hotbar/mod.toml")).IsNotNull();
            }
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
