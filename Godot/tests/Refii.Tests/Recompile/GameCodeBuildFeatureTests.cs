using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GdUnit4;
using Refii.Core;
using Refii.Launcher;
using Refii.Launcher.Core;
using Refii.Launcher.Recompile;
using Refii.Mods;
using Refii.Recompile;
using static GdUnit4.Assertions;

namespace Refii.Tests.Recompile;

/// <summary>
/// End-to-end feature tests driving <see cref="GameCodeBuild"/> and <see cref="Installer"/> across
/// cold/warm builds, header dependency &amp; shadowing checks, partition-seeded config edits,
/// Fable 2 native hard-mod chaining &amp; 3-generation zero-compile rollback, stop/resume recovery,
/// and Fable 2 + Fable 3 multi-title isolation.
/// </summary>
[TestSuite]
public sealed class GameCodeBuildFeatureTests
{
    /// <summary>
    /// Verifies a cold Desktop build runs codegen + PCH + all TUs, applies post-codegen transforms,
    /// skips <c>-include-pch</c> on units with extra flags, links in exact <c>0..N-1</c> order,
    /// and a subsequent warm build skips codegen, PCH compile, and every TU compile (<c>0</c> compiles).
    /// Also verifies <see cref="GameCodeBuild.NeedsBuild(Engine_Title, out string)"/> supports both
    /// <c>module-inputs:</c> and legacy <c>HashBuildInputs</c> build records.
    /// </summary>
    [TestCase]
    public async Task ColdBuildThenWarmRebuildSkipsCodegenPchAndUnitCompiles()
    {
        using var fixture = new DesktopBuildFixture();

        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out string initialReason)).IsTrue();
        AssertThat(initialReason.Contains("has not been built yet", StringComparison.Ordinal)).IsTrue();

        GameCodeBuild.Result cold = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());
        AssertOk(cold);
        AssertThat(GameCodeBuild.IsBuilt(Engine_Title.Fable2)).IsTrue();
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out string afterColdReason))
            .OverrideFailureMessage(NonEmpty(afterColdReason))
            .IsFalse();

        AssertThat(fixture.GenerateCalls).IsEqual(1);
        AssertThat(fixture.SawPartitionSeed).IsFalse();
        AssertThat(fixture.PchCompileCalls).IsEqual(1);
        AssertThat(fixture.LinkCalls).IsEqual(1);

        // 3 generated units + payload_export + 2 payload_core + 2 Fable2 hooks = 8 units.
        AssertThat(fixture.CompiledUnits.Count).IsEqual(8);

        // rex_bridge_payload.cpp carries -fvisibility=hidden so it must skip -include-pch; normal units use -include-pch.
        AssertThat(fixture.UnitUsedPch["rex_bridge_payload.cpp"]).IsFalse();
        AssertThat(fixture.UnitArgs["rex_bridge_payload.cpp"].Contains("-fvisibility=hidden")).IsTrue();
        AssertThat(fixture.UnitUsedPch["refii_recomp.0.cpp"]).IsTrue();
        AssertThat(fixture.UnitUsedPch["fable2_hook_a.cpp"]).IsTrue();

        // Macro prefix map flags keep __FILE__ cacheable across locations.
        AssertThat(fixture.UnitArgs["fable2_hook_a.cpp"].Any(a => a.StartsWith("-fmacro-prefix-map=", StringComparison.Ordinal))).IsTrue();

        // Post-codegen transforms upgraded refii_pch.h and chunked refii_register.cpp.
        string pchHeaderText = File.ReadAllText(Path.Combine(GameCodeBuild.GetGeneratedDirectory(Engine_Title.Fable2), "refii_pch.h"));
        AssertThat(pchHeaderText.Contains("ppc_global_lock_depth_()", StringComparison.Ordinal)).IsTrue();
        string regText = File.ReadAllText(Path.Combine(GameCodeBuild.GetGeneratedDirectory(Engine_Title.Fable2), "refii_register.cpp"));
        AssertThat(regText.Contains("refii_RegisterFunctions_PhonePart_0(registrar);", StringComparison.Ordinal)).IsTrue();

        // Persistent zig-cache environment and warmup stamp were populated.
        string zigCacheDir = GameCodeBuild.GetZigCacheDirectory(Engine_Title.Fable2);
        AssertThat(fixture.LastZigGlobalCacheDir).IsEqual(zigCacheDir);
        AssertThat(Directory.EnumerateFiles(zigCacheDir, "zig-warmed-*.stamp").Any()).IsTrue();

        // Exact 0..N-1 link order matches the source declaration order.
        string[] expectedLinkOrder =
        {
            "refii_recomp.0.cpp",
            "refii_recomp.1.cpp",
            "refii_register.cpp",
            "payload_export.cpp",
            "rex_bridge_payload.cpp",
            "guest_build_stamp.cpp",
            "fable2_hook_a.cpp",
            "fable2_hook_b.cpp",
        };
        AssertThat(fixture.LastLinkedUnitOrder.ToArray()).IsEqual(expectedLinkOrder);

        // Warm rebuild: 0 codegen runs, 0 PCH compiles, 0 unit compiles, 1 fast link.
        fixture.ResetCounters();
        GameCodeBuild.Result warm = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());
        AssertOk(warm);
        AssertThat(fixture.GenerateCalls).IsEqual(0);
        AssertThat(fixture.PchCompileCalls).IsEqual(0);
        AssertThat(fixture.CompiledUnits.Count).IsEqual(0);
        AssertThat(fixture.LinkCalls).IsEqual(1);
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsFalse();

        // Backwards compatibility: pre-upgrade build-info.json records storing raw HashBuildInputs remain valid.
        string buildInfoPath = GameCodeBuild.GetBuildInfoPath(Engine_Title.Fable2);
        var legacyDoc = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(buildInfoPath))!;
        AssertThat(legacyDoc["build_inputs"].StartsWith("module-inputs:", StringComparison.Ordinal)).IsTrue();
        legacyDoc["build_inputs"] = GameCodeBuild.HashBuildInputs(Engine_Title.Fable2);
        File.WriteAllText(buildInfoPath, JsonSerializer.Serialize(legacyDoc));
        GameCodeBuild.InvalidateStatus();
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out string legacyReason))
            .OverrideFailureMessage(NonEmpty(legacyReason))
            .IsFalse();
    }

    /// <summary>
    /// Verifies unread headers do not trigger <see cref="GameCodeBuild.NeedsBuild(Engine_Title, out string)"/>,
    /// editing a single hook recompiles only that hook (skipping codegen &amp; PCH), and adding a shadowing header
    /// in an earlier include slot invalidates both <c>NeedsBuild</c> and the affected unit via <c>SameNames</c>.
    /// </summary>
    [TestCase]
    public async Task ReadVsUnreadHeaderShadowingAndSingleHookIncrementalRebuild()
    {
        using var fixture = new DesktopBuildFixture();

        GameCodeBuild.Result initial = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());
        AssertOk(initial);

        // 1. Modifying an unread header under include/02 leaves NeedsBuild == false.
        File.WriteAllText(fixture.UnreadHeaderPath, "#pragma once\n#define UNREAD_VALUE 99\n");
        CompileCache.ClearMemoizedHashes();
        GameCodeBuild.InvalidateStatus();
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsFalse();

        // 2. Editing a single hook .cpp triggers NeedsBuild and recompiles ONLY that 1 hook.
        File.AppendAllText(fixture.Fable2HookAPath, "\n// incremental hook edit\n");
        CompileCache.ClearMemoizedHashes();
        GameCodeBuild.InvalidateStatus();
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsTrue();

        fixture.ResetCounters();
        GameCodeBuild.Result hookRebuild = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());
        AssertOk(hookRebuild);
        AssertThat(fixture.GenerateCalls).IsEqual(0);
        AssertThat(fixture.PchCompileCalls).IsEqual(0);
        AssertThat(fixture.CompiledUnits.ToArray()).IsEqual(new[] { "fable2_hook_a.cpp" });
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsFalse();

        // 3. Adding a new hook_api.h in include/01 shadows include/02/hook_api.h (included only by fable2_hook_a.cpp).
        string shadowingHeader = Path.Combine(fixture.Include01Dir, "hook_api.h");
        File.WriteAllText(shadowingHeader, "#pragma once\n#define SHADOWED_HOOK_API 1\n");
        CompileCache.ClearMemoizedHashes();
        GameCodeBuild.InvalidateStatus();
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsTrue();

        fixture.ResetCounters();
        GameCodeBuild.Result shadowRebuild = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());
        AssertOk(shadowRebuild);
        AssertThat(fixture.GenerateCalls).IsEqual(0);
        AssertThat(fixture.PchCompileCalls).IsEqual(0);
        AssertThat(fixture.CompiledUnits.ToArray()).IsEqual(new[] { "fable2_hook_a.cpp" });
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsFalse();
    }

    /// <summary>
    /// Verifies editing <c>refii_config.toml</c> seeds <c>codegen.partition.json</c> into codegen,
    /// reuses the cached binary <c>.pch</c>, and recompiles only the affected shard (<c>refii_recomp.1.cpp</c>).
    /// </summary>
    [TestCase]
    public async Task ConfigEditSeedsPartitionAndRecompilesOnlyChangedShard()
    {
        using var fixture = new DesktopBuildFixture();

        GameCodeBuild.Result initial = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());
        AssertOk(initial);
        AssertThat(File.Exists(Path.Combine(GameCodeBuild.GetObjectCacheDirectory(Engine_Title.Fable2), CodegenTransforms.PartitionFile))).IsTrue();

        // Append a shard-1 config comment/adjustment without altering declared function headers.
        File.AppendAllText(fixture.Fable2ConfigPath, "\n# shard-1 tuning tweak\n");
        CompileCache.ClearMemoizedHashes();
        GameCodeBuild.InvalidateStatus();
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsTrue();

        fixture.ResetCounters();
        GameCodeBuild.Result incremental = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());
        AssertOk(incremental);

        AssertThat(fixture.GenerateCalls).IsEqual(1);
        AssertThat(fixture.SawPartitionSeed).IsTrue();
        AssertThat(fixture.PchCompileCalls).IsEqual(0);
        AssertThat(fixture.CompiledUnits.ToArray()).IsEqual(new[] { "refii_recomp.1.cpp" });
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsFalse();
    }

    /// <summary>
    /// Verifies enabling a consented Fable 2 native hard mod (<c>.f2mod</c>) that wraps a Refii hook
    /// skips codegen &amp; PCH compilation, compiles only the mod TU, the chained Refii hook, and
    /// <c>refii_mod_chains.cpp</c> (skipping <c>-include-pch</c> on units with per-unit flags), and
    /// subsequently disabling the mod links in <c>0</c> compiles via 3-generation <see cref="CompileCache"/> retention.
    /// </summary>
    [TestCase]
    public async Task HardModEnableChainAndDisableZeroCompileRoundTrip()
    {
        using var fixture = new DesktopBuildFixture();

        // Generation 1: Vanilla Fable 2 build.
        GameCodeBuild.Result vanilla = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());
        AssertOk(vanilla);

        // Package + grant consent + enable souls.f2mod wrapping rex_SharedHook (also hooked by fable2_hook_a.cpp).
        string modSource =
            "#include \"combat.h\"\n" +
            "extern \"C\" REX_FUNC(__imp__rex_SharedHook);\n" +
            "REX_EXTERN(rex_SharedHook) {\n" +
            "  __imp__rex_SharedHook(ctx, base);\n" +
            "}\n";
        string packagePath = Path.Combine(fixture.Fable2ModsRoot, "souls.f2mod");
        var packageFiles = new List<(string, byte[])>
        {
            ("native/combat.cpp", Encoding.UTF8.GetBytes(modSource)),
            ("native/combat.h", Encoding.UTF8.GetBytes("#pragma once\n")),
        };
        AssertThat(F2ModPackage.Create(packagePath, new ModMetadata { Title = "souls" }, packageFiles, out string pkgErr))
            .OverrideFailureMessage(NonEmpty(pkgErr))
            .IsTrue();

        ModNativeSources? modNative = ModNative.Read(new ModInstall { Name = "souls", PackagePath = packagePath });
        AssertThat(modNative).IsNotNull();
        NativeModConsent.Grant(modNative!);
        File.WriteAllText(Path.Combine(fixture.Fable2ModsRoot, ModLoadOrder.Filename), "enabled = [\"souls\"]\n");

        GameCodeBuild.InvalidateStatus();
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out string modEnableReason)).IsTrue();
        AssertThat(modEnableReason.Contains("mods with native code", StringComparison.Ordinal)).IsTrue();

        // Generation 2: Modded build compiles ONLY combat.cpp, fable2_hook_a.cpp (re-aliased), and refii_mod_chains.cpp.
        fixture.ResetCounters();
        GameCodeBuild.Result modded = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());
        AssertOk(modded);
        AssertThat(fixture.GenerateCalls).IsEqual(0);
        AssertThat(fixture.PchCompileCalls).IsEqual(0);

        string[] compiledSorted = fixture.CompiledUnits.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        AssertThat(compiledSorted).IsEqual(new[] { "combat.cpp", "fable2_hook_a.cpp", "refii_mod_chains.cpp" });

        // Units with per-unit mod flags skip -include-pch; pure generated chain entry reuses cached .pch.
        AssertThat(fixture.UnitUsedPch["combat.cpp"]).IsFalse();
        AssertThat(fixture.UnitUsedPch["fable2_hook_a.cpp"]).IsFalse();
        AssertThat(fixture.UnitArgs["fable2_hook_a.cpp"].Contains("-Drex_SharedHook=rex_SharedHook__refii")).IsTrue();
        AssertThat(fixture.UnitUsedPch["refii_mod_chains.cpp"]).IsTrue();
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsFalse();

        // Generation 3: Disable souls.f2mod -> 0 codegen, 0 PCH compiles, 0 TU compiles (vanilla hook .o retained!).
        File.WriteAllText(Path.Combine(fixture.Fable2ModsRoot, ModLoadOrder.Filename), "enabled = []\n");
        GameCodeBuild.InvalidateStatus();
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out string modDisableReason)).IsTrue();
        AssertThat(modDisableReason.Contains("no longer enabled", StringComparison.Ordinal)).IsTrue();

        fixture.ResetCounters();
        GameCodeBuild.Result restored = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());
        AssertOk(restored);
        AssertThat(fixture.GenerateCalls).IsEqual(0);
        AssertThat(fixture.PchCompileCalls).IsEqual(0);
        AssertThat(fixture.CompiledUnits.Count).IsEqual(0);
        AssertThat(fixture.LinkCalls).IsEqual(1);
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsFalse();
    }

    /// <summary>
    /// Verifies stopping a build mid-compile cleans ephemeral scratch directories (<c>work/</c>, <c>mods/</c>)
    /// while preserving cached codegen, PCH, and completed <c>.o</c> artifacts so the next build resumes cleanly.
    /// </summary>
    [TestCase]
    public async Task StoppedBuildPreservesProgressAndResumesCleanly()
    {
        using var fixture = new DesktopBuildFixture(processorCount: 1);
        fixture.StopAfterUnitCompiles = 2;

        var stopControl = new BuildControl();
        GameCodeBuild.Result stopped = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, stopControl);

        AssertThat(stopped.Ok).IsFalse();
        AssertThat(stopped.Stopped).IsTrue();
        AssertThat(GameCodeBuild.IsBuilt(Engine_Title.Fable2)).IsFalse();

        // Ephemeral work/ and mods/ were cleaned, while generated/ and objects/ were preserved.
        string cacheRoot = GameCodeBuild.GetCompileCacheRoot(Engine_Title.Fable2);
        AssertThat(Directory.Exists(Path.Combine(cacheRoot, "work"))).IsFalse();
        AssertThat(Directory.Exists(Path.Combine(cacheRoot, "mods"))).IsFalse();
        AssertThat(Directory.Exists(GameCodeBuild.GetGeneratedDirectory(Engine_Title.Fable2))).IsTrue();
        AssertThat(Directory.Exists(GameCodeBuild.GetObjectCacheDirectory(Engine_Title.Fable2))).IsTrue();

        string[] completedBeforeStop = fixture.CompiledUnits.ToArray();
        AssertThat(completedBeforeStop.Length).IsEqual(2);

        // Resume on a fresh BuildControl: skips codegen, skips PCH compile, and compiles ONLY the remaining 6 units.
        fixture.ResetCounters();
        fixture.StopAfterUnitCompiles = 0;
        GameCodeBuild.Result resumed = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());

        AssertOk(resumed);
        AssertThat(fixture.GenerateCalls).IsEqual(0);
        AssertThat(fixture.PchCompileCalls).IsEqual(0);
        AssertThat(fixture.CompiledUnits.Count).IsEqual(6);
        foreach (string alreadyBuilt in completedBeforeStop)
        {
            AssertThat(fixture.CompiledUnits.Contains(alreadyBuilt)).IsFalse();
        }

        AssertThat(GameCodeBuild.IsBuilt(Engine_Title.Fable2)).IsTrue();
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsFalse();
    }

    /// <summary>
    /// Verifies <see cref="Engine_Title.Fable2"/> and <see cref="Engine_Title.Fable3"/> build into isolated
    /// modules and compile caches, editing a Fable 3 hook invalidates only Fable 3, and
    /// <see cref="Installer.UninstallManagedFiles(Engine_Title, out string)"/> removes that title's compile cache.
    /// </summary>
    [TestCase]
    public async Task Fable2AndFable3MultiTitleIsolationAndUninstallCleanup()
    {
        using var fixture = new DesktopBuildFixture();

        GameCodeBuild.Result builtF2 = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());
        AssertOk(builtF2);

        fixture.ResetCounters();
        GameCodeBuild.Result builtF3 = await GameCodeBuild.RunAsync(
            Engine_Title.Fable3, _ => { }, new BuildControl());
        AssertOk(builtF3);

        AssertThat(GameCodeBuild.IsBuilt(Engine_Title.Fable2)).IsTrue();
        AssertThat(GameCodeBuild.IsBuilt(Engine_Title.Fable3)).IsTrue();
        AssertThat(Path.GetFileName(GameCodeBuild.GetModulePath(Engine_Title.Fable2))
            .Contains("refii_generated", StringComparison.Ordinal)).IsTrue();
        AssertThat(Path.GetFileName(GameCodeBuild.GetModulePath(Engine_Title.Fable3))
            .Contains("refiii_generated", StringComparison.Ordinal)).IsTrue();

        // Fable 3's registration unit (refiii_register.cpp -> refiii_RegisterFunctions) was also chunked cleanly.
        string f3Reg = File.ReadAllText(Path.Combine(GameCodeBuild.GetGeneratedDirectory(Engine_Title.Fable3), "refiii_register.cpp"));
        AssertThat(f3Reg.Contains("refiii_RegisterFunctions_PhonePart_0(registrar);", StringComparison.Ordinal)).IsTrue();

        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsFalse();
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable3, out _)).IsFalse();

        // Editing a Fable 3 hook invalidates ONLY Fable 3; Fable 2 stays current.
        File.AppendAllText(fixture.Fable3HookPath, "\n// Fable 3 hook update\n");
        CompileCache.ClearMemoizedHashes();
        GameCodeBuild.InvalidateStatus();

        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable2, out _)).IsFalse();
        AssertThat(GameCodeBuild.NeedsBuild(Engine_Title.Fable3, out _)).IsTrue();

        // Incremental Fable 3 rebuild skips codegen & PCH and compiles only fable3_hook.cpp.
        fixture.ResetCounters();
        GameCodeBuild.Result rebuiltF3 = await GameCodeBuild.RunAsync(
            Engine_Title.Fable3, _ => { }, new BuildControl());
        AssertOk(rebuiltF3);
        AssertThat(fixture.GenerateCalls).IsEqual(0);
        AssertThat(fixture.PchCompileCalls).IsEqual(0);
        AssertThat(fixture.CompiledUnits.ToArray()).IsEqual(new[] { "fable3_hook.cpp" });

        // Uninstalling Fable 3 removes Fable 3's game/ and cache/Fable3/compile while leaving Fable 2's cache untouched.
        string f2CacheDir = Installer.GetCompileCacheDirectoryPath(Engine_Title.Fable2);
        string f3CacheDir = Installer.GetCompileCacheDirectoryPath(Engine_Title.Fable3);
        AssertThat(Directory.Exists(f2CacheDir)).IsTrue();
        AssertThat(Directory.Exists(f3CacheDir)).IsTrue();

        bool removed = Installer.UninstallManagedFiles(Engine_Title.Fable3, out string uninstallLog);
        AssertThat(removed).IsTrue();
        AssertThat(uninstallLog.Contains("Fable 3", StringComparison.Ordinal)).IsTrue();
        AssertThat(Directory.Exists(f3CacheDir)).IsFalse();
        AssertThat(Directory.Exists(f2CacheDir)).IsTrue();
    }

    /// <summary>
    /// Verifies cold game-code builds upgrade legacy <c>{prefix}_register.cpp</c> into a compact PCH-backed
    /// <c>PPCFuncMappings</c> loop when sibling <c>{prefix}_init.cpp</c> matches, and flush buffered build logs cleanly.
    /// </summary>
    [TestCase]
    public async Task ColdBuildUpgradesTableDrivenRegistrationAndFlushesBufferedLogs()
    {
        using var fixture = new DesktopBuildFixture { EmitMatchingInitCpp = true };

        GameCodeBuild.Result built = await GameCodeBuild.RunAsync(
            Engine_Title.Fable2, _ => { }, new BuildControl());
        AssertOk(built);

        string genDir = GameCodeBuild.GetGeneratedDirectory(Engine_Title.Fable2);
        string regText = File.ReadAllText(Path.Combine(genDir, "refii_register.cpp"));
        AssertThat(regText.Contains("#include \"refii_pch.h\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(regText.Contains("for (const PPCFuncMapping* m = PPCFuncMappings;", StringComparison.Ordinal)).IsTrue();
        AssertThat(regText.Contains("_PhonePart_", StringComparison.Ordinal)).IsFalse();
        AssertThat(fixture.UnitUsedPch["refii_init.cpp"]).IsTrue();
        AssertThat(fixture.UnitUsedPch["refii_register.cpp"]).IsTrue();

        string logPath = Path.Combine(fixture.TempRoot, "logs", "refii-launcher.log");
        AssertThat(File.Exists(logPath)).IsTrue();
        string logText = File.ReadAllText(logPath);
        AssertThat(logText.Contains("synthetic codegen into", StringComparison.Ordinal)).IsTrue();
        AssertThat(logText.Contains("=== done", StringComparison.Ordinal)).IsTrue();
    }

    private static void AssertOk(GameCodeBuild.Result result) =>
        AssertThat(result.Ok).OverrideFailureMessage(NonEmpty(result.Error)).IsTrue();

    private static string NonEmpty(string? message) =>
        string.IsNullOrEmpty(message) ? "Assertion failed" : message;

    /// <summary>
    /// Self-contained disposable test environment staging realistic Fable 2 and Fable 3 <c>bin/</c>,
    /// <c>recompile/</c>, <c>assets/</c>, and <c>mods/</c> trees under <see cref="TestTemp.Root"/>
    /// and routing <see cref="GameCodeBuild"/> through synthetic in-memory generator and compiler callbacks.
    /// </summary>
    private sealed class DesktopBuildFixture : IDisposable
    {
        private readonly string _previousBinDirectory;
        private readonly string? _previousLauncherOverride;
        private readonly Engine_Title _previousActiveTitle;
        private readonly IDisposable _driverScope;
        private readonly Dictionary<string, string> _objectToUnitName = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly object _stateLock = new();

        public string TempRoot { get; }
        public string BinDir { get; }
        public string RecompileDir { get; }
        public string Include01Dir { get; }
        public string Include02Dir { get; }
        public string UnreadHeaderPath { get; }
        public string Fable2HookAPath { get; }
        public string Fable2HookBPath { get; }
        public string Fable3HookPath { get; }
        public string Fable2ConfigPath { get; }
        public string Fable3ConfigPath { get; }
        public string Fable2ModsRoot { get; }

        public int GenerateCalls { get; private set; }
        public bool SawPartitionSeed { get; private set; }
        public int PchCompileCalls { get; private set; }
        public int LinkCalls { get; private set; }
        public int StopAfterUnitCompiles { get; set; }
        public bool EmitMatchingInitCpp { get; set; }
        public string LastZigGlobalCacheDir { get; private set; } = string.Empty;
        public List<string> CompiledUnits { get; } = new();
        public List<string> LastLinkedUnitOrder { get; } = new();
        public ConcurrentDictionary<string, bool> UnitUsedPch { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, IReadOnlyList<string>> UnitArgs { get; } = new(StringComparer.Ordinal);

        public DesktopBuildFixture(int processorCount = 4)
        {
            _previousBinDirectory = GameCodeBuild.BinDirectory;
            _previousLauncherOverride = LauncherPaths.OverrideLauncherDirectory;
            _previousActiveTitle = Engine_Title.Active;

            TempRoot = Path.Combine(TestTemp.Root, "gamecode_feature_" + Guid.NewGuid().ToString("N"));
            BinDir = Path.Combine(TempRoot, "bin");
            RecompileDir = Path.Combine(BinDir, "recompile");
            Include01Dir = Path.Combine(RecompileDir, "include", "01");
            Include02Dir = Path.Combine(RecompileDir, "include", "02");
            Fable2ModsRoot = Path.Combine(TempRoot, "assets", "Fable2", "mods");

            Directory.CreateDirectory(Include01Dir);
            Directory.CreateDirectory(Include02Dir);
            Directory.CreateDirectory(Fable2ModsRoot);

            LauncherPaths.OverrideLauncherDirectory = TempRoot;
            Ini.LoadFromText("", Path.Combine(TempRoot, "assets", "launcher", "config.cfg"));
            GameCodeBuild.BinDirectory = BinDir;

            // Install fake game executables and bank markers for Fable 2 and Fable 3 under TempRoot/assets/<Title>/game/.
            StageTitleInstall(Engine_Title.Fable2, "FABLE2-XEX-IMAGE-BYTES");
            StageTitleInstall(Engine_Title.Fable3, "FABLE3-XEX-IMAGE-BYTES");

            // Stage fake shared & per-title shipped native libraries + recompiler binary + fake Zig bundle.
            StageNativeLibraries();
            string zigRoot = Path.Combine(RecompileDir, "zig");
            string zigExe = Path.Combine(zigRoot, OperatingSystem.IsWindows() ? "zig.exe" : "zig");
            Directory.CreateDirectory(Path.Combine(zigRoot, "lib"));
            File.WriteAllText(zigExe, "synthetic-zig-0.16.0");
            File.WriteAllText(Path.Combine(zigRoot, "lib", "stdint.h"), "#pragma once\n");

            // Stage headers, payload_export, payload_core, and per-title hooks + manifests/configs.
            File.WriteAllText(Path.Combine(Include01Dir, "sdk.h"), "#pragma once\n");
            File.WriteAllText(Path.Combine(Include02Dir, "hook_api.h"), "#pragma once\n");
            UnreadHeaderPath = Path.Combine(Include02Dir, "unread.h");
            File.WriteAllText(UnreadHeaderPath, "#pragma once\n#define UNREAD_VALUE 1\n");

            File.WriteAllText(
                Path.Combine(RecompileDir, "include_dirs.cmake"),
                "set(REFII_PAYLOAD_INCLUDE_DIRS\n" +
                "  ${CMAKE_CURRENT_LIST_DIR}/include/01\n" +
                "  ${CMAKE_CURRENT_LIST_DIR}/include/02\n" +
                ")\n");

            string recompileSrcDir = Path.Combine(RecompileDir, "src");
            Directory.CreateDirectory(recompileSrcDir);
            File.WriteAllText(Path.Combine(recompileSrcDir, "libcxx_chrono_shim.h"), "#pragma once\n");
            File.WriteAllText(Path.Combine(recompileSrcDir, "payload_export.cpp"), "// payload_export\n");

            string payloadCoreDir = Path.Combine(RecompileDir, "payload_core");
            Directory.CreateDirectory(payloadCoreDir);
            File.WriteAllText(Path.Combine(payloadCoreDir, "rex_bridge_payload.cpp"), "// rex_bridge_payload\n");
            File.WriteAllText(Path.Combine(payloadCoreDir, "guest_build_stamp.cpp"), "// guest_build_stamp\n");
            File.WriteAllText(
                Path.Combine(RecompileDir, "payload_core_sources.cmake"),
                "set(REFII_PAYLOAD_CORE_SOURCES\n" +
                "  ${CMAKE_CURRENT_LIST_DIR}/payload_core/rex_bridge_payload.cpp\n" +
                "  ${CMAKE_CURRENT_LIST_DIR}/payload_core/guest_build_stamp.cpp\n" +
                ")\n");

            // Fable 2 hooks & version files.
            string f2HooksDir = Path.Combine(RecompileDir, "Fable2", "hooks");
            Directory.CreateDirectory(f2HooksDir);
            Fable2HookAPath = Path.Combine(f2HooksDir, "fable2_hook_a.cpp");
            Fable2HookBPath = Path.Combine(f2HooksDir, "fable2_hook_b.cpp");
            File.WriteAllText(
                Fable2HookAPath,
                "#include \"hook_api.h\"\n" +
                "extern \"C\" REX_FUNC(__imp__rex_SharedHook);\n" +
                "REX_EXTERN(rex_SharedHook) {\n" +
                "  __imp__rex_SharedHook(ctx, base);\n" +
                "}\n");
            File.WriteAllText(Fable2HookBPath, "// fable2_hook_b\n");
            File.WriteAllText(
                Path.Combine(RecompileDir, "Fable2", "hook_sources.cmake"),
                "set(REFII_HOOK_SOURCES\n" +
                "  ${CMAKE_CURRENT_LIST_DIR}/hooks/fable2_hook_a.cpp\n" +
                "  ${CMAKE_CURRENT_LIST_DIR}/hooks/fable2_hook_b.cpp\n" +
                ")\n");

            string f2VerDir = Path.Combine(RecompileDir, "versions", "Fable2");
            Directory.CreateDirectory(f2VerDir);
            Fable2ConfigPath = Path.Combine(f2VerDir, "refii_config.toml");
            File.WriteAllText(
                Path.Combine(f2VerDir, "refii_manifest.toml"),
                "file_path = \"default.xex\"\n" +
                "out_directory_path = \"generated\"\n" +
                "includes = [\n" +
                "  \"refii_config.toml\",\n" +
                "]\n");
            File.WriteAllText(
                Fable2ConfigPath,
                "[functions]\n" +
                "0x82000000 = { name = \"rex_SharedHook\" }\n" +
                "0x82000004 = { name = \"rex_Helper\" }\n");

            // Fable 3 hooks & version files.
            string f3HooksDir = Path.Combine(RecompileDir, "Fable3", "hooks");
            Directory.CreateDirectory(f3HooksDir);
            Fable3HookPath = Path.Combine(f3HooksDir, "fable3_hook.cpp");
            File.WriteAllText(Fable3HookPath, "// fable3_hook\n");
            File.WriteAllText(
                Path.Combine(RecompileDir, "Fable3", "hook_sources.cmake"),
                "set(REFII_HOOK_SOURCES\n" +
                "  ${CMAKE_CURRENT_LIST_DIR}/hooks/fable3_hook.cpp\n" +
                ")\n");

            string f3VerDir = Path.Combine(RecompileDir, "versions", "Fable3");
            Directory.CreateDirectory(f3VerDir);
            Fable3ConfigPath = Path.Combine(f3VerDir, "refiii_config.toml");
            File.WriteAllText(
                Path.Combine(f3VerDir, "refiii_manifest.toml"),
                "file_path = \"default.xex\"\n" +
                "out_directory_path = \"generated\"\n" +
                "includes = [\"refiii_config.toml\"]\n");
            File.WriteAllText(
                Fable3ConfigPath,
                "[functions]\n" +
                "0x82100000 = { name = \"rex_Fable3Entry\" }\n");

            Installer.InvalidateReinstallStepStatus();
            GameCodeBuild.InvalidateStatus();
            CompileCache.ClearMemoizedHashes();

            var toolchain = new Toolchain(zigExe, zigRoot);
            _driverScope = GameCodeBuild.PushDriver(new GameCodeBuild.Driver
            {
                ModsRootOverride = title => Path.Combine(TempRoot, "assets", title.Folder, "mods"),
                SkipWindowsBootstrap = true,
                ResolveToolchain = () => (true, toolchain, string.Empty),
                ToolchainVersion = _ => "0.16.0-test",
                SdkVersion = () => "1.0.0-test",
                ProcessorCountOverride = processorCount,
                AvailableMemoryBytesOverride = () => 32L * 1024 * 1024 * 1024,
                GenerateCode = HandleGenerateCode,
                RunProcessAsync = HandleRunProcessAsync,
            });
        }

        public void ResetCounters()
        {
            lock (_stateLock)
            {
                GenerateCalls = 0;
                SawPartitionSeed = false;
                PchCompileCalls = 0;
                LinkCalls = 0;
                CompiledUnits.Clear();
                LastLinkedUnitOrder.Clear();
                UnitUsedPch.Clear();
                UnitArgs.Clear();
            }
        }

        private void StageTitleInstall(Engine_Title title, string xexPayload)
        {
            string gameDir = Path.Combine(TempRoot, "assets", title.Folder, "game");
            string dataDir = Path.Combine(gameDir, "data");
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(gameDir, "default.xex"), xexPayload);
            File.WriteAllBytes(Path.Combine(dataDir, "globals.bnk"), new byte[] { 1, 2, 3, 4 });
        }

        private void StageNativeLibraries()
        {
            Directory.CreateDirectory(BinDir);
            File.WriteAllText(
                Path.Combine(BinDir, Engine_NativeLibraries.FileNameFor("refii_recompiler")),
                "recompiler-v1");

            foreach (string shared in new[] { "rexruntime", "refii_core", "refii_runtime" })
            {
                WriteFakeShippedLibrary(BinDir, shared);
            }

            foreach (Engine_Title title in Engine_Title.All)
            {
                string titleBin = Path.Combine(BinDir, title.Folder);
                Directory.CreateDirectory(titleBin);
                WriteFakeShippedLibrary(titleBin, "refii_hostbridge");
                WriteFakeShippedLibrary(titleBin, "refii_vfs");
            }
        }

        private static void WriteFakeShippedLibrary(string dir, string baseName)
        {
            File.WriteAllText(Path.Combine(dir, Engine_NativeLibraries.FileNameFor(baseName)), "dll-" + baseName);
            File.WriteAllText(Path.Combine(dir, baseName + ".lib"), "lib-" + baseName);
        }

        private GameCodeBuild.Result HandleGenerateCode(
            string manifestPath,
            BuildControl control,
            Action<string> onProgress,
            Action<string> onLog)
        {
            string manifestText = File.ReadAllText(manifestPath);
            string outDir = ExtractTomlQuoted(manifestText, "out_directory_path");
            string includeConfig = ExtractFirstInclude(manifestText);
            string configText = File.Exists(includeConfig) ? File.ReadAllText(includeConfig) : string.Empty;
            string prefix = Path.GetFileName(manifestPath).StartsWith("refiii_", StringComparison.Ordinal)
                ? "refiii"
                : "refii";

            bool seeded = File.Exists(Path.Combine(outDir, CodegenTransforms.PartitionFile));
            lock (_stateLock)
            {
                GenerateCalls++;
                SawPartitionSeed = seeded;
            }

            onProgress("emitting C++");
            onLog("synthetic codegen into " + outDir);

            // Emit refii_pch.h containing the legacy ProcessWideGuestLock so post-codegen transform is exercised.
            File.WriteAllText(
                Path.Combine(outDir, "refii_pch.h"),
                "#pragma once\n" + CodegenTransforms.ProcessWideGuestLock.ReplaceLineEndings("\n"));

            // Emit declared functions in <prefix>_init.h so hard-mod validation sees them.
            var initBuilder = new StringBuilder("#pragma once\n");
            foreach (string fn in ParseConfigFunctionNames(configText))
            {
                initBuilder.Append("DECLARE_REX_FUNC(").Append(fn).Append(");\n");
            }
            File.WriteAllText(Path.Combine(outDir, prefix + "_init.h"), initBuilder.ToString());

            // Shard 0 stays byte-identical across config tweaks; Shard 1 incorporates configText digest!
            string shard0Name = prefix + "_recomp.0.cpp";
            string shard1Name = prefix + "_recomp.1.cpp";
            string initCppName = prefix + "_init.cpp";
            string registerName = prefix + "_register.cpp";

            File.WriteAllText(Path.Combine(outDir, shard0Name), new string('a', 4096) + "\n// stable shard 0\n");
            File.WriteAllText(
                Path.Combine(outDir, shard1Name),
                new string('b', 2048) + "\n// shard 1 config=" + CompileCache.HashText(configText) + "\n");

            // Emit >512 SetFunction calls so BoundRegistration chunking (or OptimizeRegistration upgrade when
            // EmitMatchingInitCpp is true) is exercised for both refii and refiii.
            var regBuilder = new StringBuilder();
            regBuilder.Append("#include \"").Append(prefix).Append("_init.h\"\n");
            regBuilder.Append("void ").Append(prefix).Append("_RegisterFunctions(rex::runtime::IModuleRegistrar* registrar) {\n");
            var initCppBuilder = new StringBuilder();
            initCppBuilder.Append("#include \"").Append(prefix).Append("_pch.h\"\n");
            initCppBuilder.Append("PPCFuncMapping PPCFuncMappings[] = {\n");
            for (int i = 0; i < 600; i++)
            {
                uint addr = 0x82000000u + (uint)(i * 4);
                regBuilder.Append("  registrar->SetFunction(0x").Append(addr.ToString("X8")).Append(", sub_").Append(addr.ToString("X8")).Append(");\n");
                initCppBuilder.Append("    { 0x").Append(addr.ToString("X8")).Append(", sub_").Append(addr.ToString("X8")).Append(" },\n");
            }
            regBuilder.Append("}\n");
            initCppBuilder.Append("    { 0, nullptr }\n};\n");
            File.WriteAllText(Path.Combine(outDir, registerName), regBuilder.ToString());
            if (EmitMatchingInitCpp)
            {
                File.WriteAllText(Path.Combine(outDir, initCppName), initCppBuilder.ToString());
            }

            string extraInitSource = EmitMatchingInitCpp
                ? "  \"${CMAKE_CURRENT_LIST_DIR}/" + initCppName + "\"\n"
                : string.Empty;
            File.WriteAllText(
                Path.Combine(outDir, "sources.cmake"),
                "set(GENERATED_SOURCES\n" +
                extraInitSource +
                "  \"${CMAKE_CURRENT_LIST_DIR}/" + shard0Name + "\"\n" +
                "  \"${CMAKE_CURRENT_LIST_DIR}/" + shard1Name + "\"\n" +
                "  \"${CMAKE_CURRENT_LIST_DIR}/" + registerName + "\"\n" +
                ")\n");

            File.WriteAllText(
                Path.Combine(outDir, CodegenTransforms.PartitionFile),
                "{\"shards\":[\"" + shard0Name + "\",\"" + shard1Name + "\"]}\n");

            return new GameCodeBuild.Result(true, string.Empty);
        }

        private Task<(int Code, string Output)> HandleRunProcessAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            BuildControl control,
            CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (environment.TryGetValue("ZIG_GLOBAL_CACHE_DIR", out string? zigGlobal))
            {
                lock (_stateLock)
                {
                    LastZigGlobalCacheDir = zigGlobal;
                }
            }

            List<string> expanded = ExpandArguments(arguments);

            // 1. Precompiled header invocation (-x c++-header <header> -o <pch> -MF <dep>)
            int xIdx = IndexOfArg(expanded, "-x");
            if (xIdx >= 0 && xIdx + 1 < expanded.Count && expanded[xIdx + 1] == "c++-header")
            {
                string headerPath = expanded[xIdx + 2];
                string pchOut = expanded[IndexOfArg(expanded, "-o") + 1];
                string depOut = expanded[IndexOfArg(expanded, "-MF") + 1];
                string shimPath = Path.Combine(RecompileDir, "src", "libcxx_chrono_shim.h");
                string sdkHeader = Path.Combine(Include01Dir, "sdk.h");
                string stdintHeader = Path.Combine(RecompileDir, "zig", "lib", "stdint.h");

                Directory.CreateDirectory(Path.GetDirectoryName(pchOut)!);
                File.WriteAllText(pchOut, "PCH-BINARY:" + CompileCache.HashFileMemoized(headerPath));
                WriteDepfile(depOut, pchOut, new[] { headerPath, shimPath, sdkHeader, stdintHeader });

                lock (_stateLock)
                {
                    PchCompileCalls++;
                }

                return Task.FromResult((0, string.Empty));
            }

            // 2. Unit compile invocation (-c <source> -o <obj> -MF <dep>)
            int cIdx = IndexOfArg(expanded, "-c");
            if (cIdx >= 0 && cIdx + 1 < expanded.Count)
            {
                string sourcePath = expanded[cIdx + 1];
                string objOut = expanded[IndexOfArg(expanded, "-o") + 1];
                string depOut = expanded[IndexOfArg(expanded, "-MF") + 1];
                bool usedPch = IndexOfArg(expanded, "-include-pch") >= 0;
                string unitName = Path.GetFileName(sourcePath);

                var deps = new List<string>
                {
                    sourcePath,
                    Path.Combine(RecompileDir, "src", "libcxx_chrono_shim.h"),
                    Path.Combine(Include01Dir, "sdk.h"),
                    Path.Combine(RecompileDir, "zig", "lib", "stdint.h"),
                };

                string pchHeaderCandidate = Path.Combine(
                    Directory.GetDirectories(Path.Combine(TempRoot, "cache"), "generated", SearchOption.AllDirectories)
                        .FirstOrDefault() ?? string.Empty,
                    "refii_pch.h");
                foreach (string arg in expanded)
                {
                    if (arg.StartsWith("-I", StringComparison.Ordinal) && arg.Length > 2)
                    {
                        string dir = arg[2..];
                        string candidatePch = Path.Combine(dir, "refii_pch.h");
                        if (File.Exists(candidatePch))
                        {
                            pchHeaderCandidate = candidatePch;
                            break;
                        }
                    }
                }

                if (File.Exists(pchHeaderCandidate))
                {
                    deps.Add(pchHeaderCandidate);
                }

                // Resolve #include "..." lines in sourcePath across -I search directories in exact order!
                List<string> includeSearch = ExtractIncludeDirs(expanded);
                foreach (string incName in ParseQuotedIncludes(sourcePath))
                {
                    string? resolvedInc = ResolveHeader(incName, Path.GetDirectoryName(sourcePath)!, includeSearch);
                    if (resolvedInc != null)
                    {
                        deps.Add(resolvedInc);
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(objOut)!);
                File.WriteAllText(objOut, "OBJ:" + unitName + ":" + CompileCache.HashFileMemoized(sourcePath));
                WriteDepfile(depOut, objOut, deps);

                UnitUsedPch[unitName] = usedPch;
                UnitArgs[unitName] = expanded.ToArray();

                bool triggerStop = false;
                lock (_stateLock)
                {
                    CompiledUnits.Add(unitName);
                    if (StopAfterUnitCompiles > 0 && CompiledUnits.Count >= StopAfterUnitCompiles)
                    {
                        triggerStop = true;
                    }
                }

                if (triggerStop)
                {
                    control.Stop();
                }

                return Task.FromResult((0, string.Empty));
            }

            // 3. Shared library link invocation (-shared -o <module> <objs...> <libs...>)
            if (IndexOfArg(expanded, "-shared") >= 0)
            {
                string moduleOut = expanded[IndexOfArg(expanded, "-o") + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(moduleOut)!);

                var linkedUnits = new List<string>();
                foreach (string token in expanded)
                {
                    if (token.EndsWith(".o", StringComparison.OrdinalIgnoreCase) && File.Exists(token))
                    {
                        string text = File.ReadAllText(token);
                        string[] parts = text.Split(':', 3);
                        string unit = parts.Length >= 2 ? parts[1] : Path.GetFileName(token);
                        linkedUnits.Add(unit);
                    }
                }

                File.WriteAllText(moduleOut, "MODULE:" + string.Join(",", linkedUnits));
                lock (_stateLock)
                {
                    LinkCalls++;
                    LastLinkedUnitOrder.Clear();
                    LastLinkedUnitOrder.AddRange(linkedUnits);
                    foreach (string token in expanded)
                    {
                        if (token.EndsWith(".o", StringComparison.OrdinalIgnoreCase) && File.Exists(token))
                        {
                            string text = File.ReadAllText(token);
                            string[] parts = text.Split(':', 3);
                            if (parts.Length >= 2)
                            {
                                _objectToUnitName[token] = parts[1];
                            }
                        }
                    }
                }

                return Task.FromResult((0, string.Empty));
            }

            return Task.FromResult((1, "unrecognized synthetic zig command: " + string.Join(" ", expanded)));
        }

        private static List<string> ExpandArguments(IReadOnlyList<string> arguments)
        {
            var expanded = new List<string>();
            foreach (string arg in arguments)
            {
                if (arg.StartsWith('@') && arg.Length > 1 && File.Exists(arg[1..]))
                {
                    foreach (string line in File.ReadAllLines(arg[1..]))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
                        {
                            trimmed = trimmed[1..^1];
                        }

                        if (trimmed.Length > 0)
                        {
                            expanded.Add(trimmed);
                        }
                    }
                }
                else
                {
                    expanded.Add(arg);
                }
            }

            return expanded;
        }

        private static int IndexOfArg(IReadOnlyList<string> args, string flag)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == flag)
                {
                    return i;
                }
            }

            return -1;
        }

        private static List<string> ExtractIncludeDirs(IReadOnlyList<string> args)
        {
            var dirs = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == "-I" && i + 1 < args.Count)
                {
                    dirs.Add(args[++i]);
                }
                else if (args[i].StartsWith("-I", StringComparison.Ordinal) && args[i].Length > 2)
                {
                    dirs.Add(args[i][2..]);
                }
            }

            return dirs;
        }

        private static IEnumerable<string> ParseQuotedIncludes(string sourcePath)
        {
            if (!File.Exists(sourcePath))
            {
                yield break;
            }

            foreach (string raw in File.ReadLines(sourcePath))
            {
                string trimmed = raw.TrimStart();
                if (!trimmed.StartsWith("#include \"", StringComparison.Ordinal))
                {
                    continue;
                }

                int start = "#include \"".Length;
                int end = trimmed.IndexOf('"', start);
                if (end > start)
                {
                    yield return trimmed[start..end];
                }
            }
        }

        private static string? ResolveHeader(string includeName, string sourceDir, IReadOnlyList<string> includeDirs)
        {
            string local = Path.Combine(sourceDir, includeName);
            if (File.Exists(local))
            {
                return Path.GetFullPath(local);
            }

            foreach (string dir in includeDirs)
            {
                string candidate = Path.Combine(dir, includeName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        private static void WriteDepfile(string depPath, string targetPath, IEnumerable<string> dependencies)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(depPath)!);
            static string EscapeDep(string path) => path.Replace(" ", "\\ ");
            string joined = string.Join(" \\\n  ", dependencies.Distinct().Select(EscapeDep));
            File.WriteAllText(depPath, EscapeDep(targetPath) + ": " + joined + "\n");
        }

        private static string ExtractTomlQuoted(string text, string key)
        {
            foreach (string raw in text.Split('\n'))
            {
                string trimmed = raw.TrimStart();
                if (!trimmed.StartsWith(key, StringComparison.Ordinal))
                {
                    continue;
                }

                int open = trimmed.IndexOf('"');
                int close = trimmed.LastIndexOf('"');
                if (open >= 0 && close > open)
                {
                    return trimmed[(open + 1)..close].Replace("\\\\", "\\");
                }
            }

            return string.Empty;
        }

        private static string ExtractFirstInclude(string text)
        {
            foreach (string raw in text.Split('\n'))
            {
                string trimmed = raw.TrimStart();
                if (!trimmed.StartsWith("includes", StringComparison.Ordinal))
                {
                    continue;
                }

                int open = trimmed.IndexOf('"');
                int close = trimmed.IndexOf('"', open + 1);
                if (open >= 0 && close > open)
                {
                    return trimmed[(open + 1)..close].Replace("\\\\", "\\");
                }
            }

            return string.Empty;
        }

        private static List<string> ParseConfigFunctionNames(string configText)
        {
            var names = new List<string>();
            foreach (string raw in configText.Split('\n'))
            {
                int marker = raw.IndexOf("name = \"", StringComparison.Ordinal);
                if (marker < 0)
                {
                    continue;
                }

                int start = marker + "name = \"".Length;
                int end = raw.IndexOf('"', start);
                if (end > start)
                {
                    names.Add(raw[start..end]);
                }
            }

            return names;
        }

        public void Dispose()
        {
            _driverScope.Dispose();
            GameCodeBuild.BinDirectory = _previousBinDirectory;
            LauncherPaths.OverrideLauncherDirectory = _previousLauncherOverride;
            Engine_Title.Active = _previousActiveTitle;
            Ini.Load();
            Installer.InvalidateReinstallStepStatus();
            GameCodeBuild.InvalidateStatus();
            CompileCache.ClearMemoizedHashes();

            try
            {
                if (Directory.Exists(TempRoot))
                {
                    Directory.Delete(TempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
