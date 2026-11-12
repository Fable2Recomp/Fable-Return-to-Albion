namespace Refii.Tests.BuildCache;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using GdUnit4;
using Refii.BuildCache;
using static GdUnit4.Assertions;

/// <summary>
/// Isolated unit tests for <see cref="Crc32Fast"/>, <see cref="FileHashIndex"/>,
/// <see cref="TestGateCommand"/>, <see cref="GodotExportCommand"/>, <see cref="GodotTestShim"/>,
/// and <see cref="IncrementalZipPacker"/>.
/// </summary>
[TestSuite]
public sealed class BuildCacheUnitTests
{
    /// <summary>
    /// Verifies IEEE 802.3 PKZIP CRC-32 test vectors, unaligned slice-by-8 boundaries,
    /// incremental chunked updates, and stream hashing.
    /// </summary>
    [TestCase]
    public void Crc32Fast_MatchesStandardIeee8023VectorsAndChunkedStreams()
    {
        AssertThat(Crc32Fast.Compute(ReadOnlySpan<byte>.Empty)).IsEqual(0x00000000u);

        // Canonical IEEE 802.3 check value for ASCII "123456789".
        byte[] canonical = Encoding.ASCII.GetBytes("123456789");
        AssertThat(Crc32Fast.Compute(canonical)).IsEqual(0xCBF43926u);

        // Large deterministic buffer spanning multiple 8-byte lanes plus odd tail bytes.
        var payload = new byte[17_419];
        new Random(42).NextBytes(payload);
        uint singleShot = Crc32Fast.Compute(payload);

        uint chunked = 0u;
        int offset = 0;
        int[] stepSizes = { 1, 3, 7, 8, 15, 64, 511, 4096 };
        int stepIdx = 0;
        while (offset < payload.Length)
        {
            int take = Math.Min(stepSizes[stepIdx % stepSizes.Length], payload.Length - offset);
            chunked = Crc32Fast.Update(chunked, payload.AsSpan(offset, take));
            offset += take;
            stepIdx++;
        }

        AssertThat(chunked).IsEqual(singleShot);

        using var ms = new MemoryStream(payload);
        AssertThat(Crc32Fast.Compute(ms)).IsEqual(singleShot);
    }

    /// <summary>
    /// Verifies that <see cref="FileHashIndex"/> avoids redundant file reads on warm runs,
    /// persists cleanly across process loads, detects same-size byte mutations when timestamps change,
    /// and preserves downstream tree digests when only timestamps advance on identical bytes.
    /// </summary>
    [TestCase]
    public void FileHashIndex_CachesByMetadataAndInvalidatesOnMutation()
    {
        string tempRoot = Path.Combine(TestTemp.Root, $"fhi-unit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string fileA = Path.Combine(tempRoot, "alpha.txt");
            File.WriteAllText(fileA, "hello-world-v1");

            string indexPath = Path.Combine(tempRoot, "index.bin");
            var index = new FileHashIndex();

            byte[] firstHash = index.GetOrComputeFileSha256(fileA);
            AssertThat(index.HashComputations).IsEqual(1);

            // Second lookup with unchanged size & mtime performs zero disk reads.
            byte[] cachedHash = index.GetOrComputeFileSha256(fileA);
            AssertThat(index.HashComputations).IsEqual(1);
            AssertThat(Convert.ToHexString(cachedHash)).IsEqual(Convert.ToHexString(firstHash));

            index.SaveIfDirty(indexPath);

            // Reloaded index also hits cache with zero disk reads.
            FileHashIndex reloaded = FileHashIndex.Load(indexPath);
            byte[] fromDiskCache = reloaded.GetOrComputeFileSha256(fileA);
            AssertThat(reloaded.HashComputations).IsEqual(0);
            AssertThat(Convert.ToHexString(fromDiskCache)).IsEqual(Convert.ToHexString(firstHash));

            // Touching mtime with IDENTICAL content re-hashes once, producing the exact same digest.
            File.SetLastWriteTimeUtc(fileA, DateTime.UtcNow.AddMinutes(2));
            byte[] touchedHash = reloaded.GetOrComputeFileSha256(fileA);
            AssertThat(reloaded.HashComputations).IsEqual(1);
            AssertThat(Convert.ToHexString(touchedHash)).IsEqual(Convert.ToHexString(firstHash));

            // Same-size byte mutation with bumped mtime invalidates the cached digest.
            File.WriteAllText(fileA, "hello-world-v2");
            File.SetLastWriteTimeUtc(fileA, DateTime.UtcNow.AddMinutes(4));
            byte[] mutatedHash = reloaded.GetOrComputeFileSha256(fileA);
            AssertThat(reloaded.HashComputations).IsEqual(2);
            AssertThat(Convert.ToHexString(mutatedHash)).IsNotEqual(Convert.ToHexString(firstHash));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that <see cref="FileHashIndex.ScanDirectory"/> prunes excluded directories before descending.
    /// </summary>
    [TestCase]
    public void FileHashIndex_ScanDirectory_PrunesExcludedSubtrees()
    {
        string tempRoot = Path.Combine(TestTemp.Root, $"scan-unit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, ".godot", "mono"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "build", "x86_64"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "src", "ui"));
        try
        {
            File.WriteAllText(Path.Combine(tempRoot, ".godot", "mono", "ignored.dll"), "ignore");
            File.WriteAllText(Path.Combine(tempRoot, "build", "x86_64", "ignored.so"), "ignore");
            File.WriteAllText(Path.Combine(tempRoot, "src", "ui", "Screen.cs"), "class Screen {}");
            File.WriteAllText(Path.Combine(tempRoot, "src", "ui", "Screen.cs.uid"), "uid://123");
            File.WriteAllText(Path.Combine(tempRoot, "project.godot"), "config_version=5");

            List<ScannedEntry> scanned = FileHashIndex.ScanDirectory(
                tempRoot,
                shouldIncludeDirectory: static (_, name) => name is not ".godot" and not "build",
                shouldIncludeFile: static (_, name) => !name.EndsWith(".uid", StringComparison.OrdinalIgnoreCase));

            AssertThat(scanned.Count).IsEqual(2);
            AssertThat(scanned[0].RelativePath).IsEqual("project.godot");
            AssertThat(scanned[1].RelativePath).IsEqual("src/ui/Screen.cs");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// Verifies <see cref="GodotExportCommand.VerifyExportedPayload"/> against missing binaries,
    /// missing <c>.NET</c> assemblies, loose <c>data_*</c> assemblies, and embedded publish payloads.
    /// </summary>
    [TestCase]
    public void GodotExportCommand_VerifyExportedPayload_EnforcesDotNetAssemblies()
    {
        string tempRoot = Path.Combine(TestTemp.Root, $"export-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            const string binaryName = "Fable 2 Return to Albion.exe";
            AssertThat(GodotExportCommand.VerifyExportedPayload(tempRoot, binaryName, out _)).IsFalse();

            string binaryPath = Path.Combine(tempRoot, binaryName);
            File.WriteAllBytes(binaryPath, Encoding.ASCII.GetBytes("MZ-stub-without-managed-assemblies"));
            AssertThat(GodotExportCommand.VerifyExportedPayload(tempRoot, binaryName, out string missingDllErr)).IsFalse();
            AssertThat(missingDllErr).Contains("without the .NET assemblies");

            // Embedded .NET assembly path inside binary satisfies verification.
            File.WriteAllBytes(
                binaryPath,
                Encoding.ASCII.GetBytes("HEADER.../.godot/mono/publish/x86_64/refii-launcher.dll...TRAILER"));
            AssertThat(GodotExportCommand.VerifyExportedPayload(tempRoot, binaryName, out _)).IsTrue();

            // Loose data_* folder beside a plain binary also satisfies verification.
            File.WriteAllBytes(binaryPath, Encoding.ASCII.GetBytes("MZ-plain-launcher-stub"));
            string looseDir = Path.Combine(tempRoot, "data_refii-launcher_windows_x86_64");
            Directory.CreateDirectory(looseDir);
            File.WriteAllBytes(Path.Combine(looseDir, "refii-launcher.dll"), new byte[] { 1, 2, 3, 4 });
            AssertThat(GodotExportCommand.VerifyExportedPayload(tempRoot, binaryName, out _)).IsTrue();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// Verifies <see cref="TestGateCommand.ComputeInputDigest"/> ignores transient <c>.uid</c>/<c>.godot</c>
    /// artefacts while reacting immediately to C# source or external template changes.
    /// </summary>
    [TestCase]
    public void TestGateCommand_ComputeInputDigest_TracksSourceAndTemplateInputs()
    {
        string repoRoot = Path.Combine(TestTemp.Root, $"testgate-unit-{Guid.NewGuid():N}");
        string godotDir = Path.Combine(repoRoot, "Godot");
        string templatePath = Path.Combine(
            repoRoot, "rexglue-net", "shadow", "rexglue-sdk", "resources", "templates", "codegen", "pch_h.inja");
        Directory.CreateDirectory(Path.Combine(godotDir, "src"));
        Directory.CreateDirectory(Path.Combine(godotDir, ".godot"));
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);

        try
        {
            string csFile = Path.Combine(godotDir, "src", "Sample.cs");
            File.WriteAllText(csFile, "namespace Sample; public class Foo {}");
            File.WriteAllText(templatePath, "// template v1");

            var index = new FileHashIndex();
            var opts = new TestGateOptions(
                RepoRoot: repoRoot,
                ProjectDir: godotDir,
                TestCsproj: Path.Combine(godotDir, "tests", "Refii.Tests", "Refii.Tests.csproj"),
                Configuration: "Debug",
                GodotBin: string.Empty,
                CacheDir: Path.Combine(repoRoot, "out", "cache"),
                DisableBuildServers: false,
                ForceRun: false);

            string d1 = TestGateCommand.ComputeInputDigest(opts, index);

            // Ignored .uid and .godot cache noise must NOT invalidate the test stamp.
            File.WriteAllText(Path.Combine(godotDir, "src", "Sample.cs.uid"), "uid://abc");
            File.WriteAllText(Path.Combine(godotDir, ".godot", "editor_state.cfg"), "state=1");
            string d2 = TestGateCommand.ComputeInputDigest(opts, index);
            AssertThat(d2).IsEqual(d1);

            // Editing a C# source file MUST invalidate the test stamp.
            File.WriteAllText(csFile, "namespace Sample; public class Foo { public int X; }");
            File.SetLastWriteTimeUtc(csFile, DateTime.UtcNow.AddMinutes(1));
            string d3 = TestGateCommand.ComputeInputDigest(opts, index);
            AssertThat(d3).IsNotEqual(d1);

            // Editing the external pch_h.inja template MUST also invalidate the test stamp.
            File.WriteAllText(templatePath, "// template v2");
            File.SetLastWriteTimeUtc(templatePath, DateTime.UtcNow.AddMinutes(2));
            string d4 = TestGateCommand.ComputeInputDigest(opts, index);
            AssertThat(d4).IsNotEqual(d3);
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that <see cref="IncrementalZipPacker"/> produces spec-compliant PKZIP archives
    /// readable by <see cref="ZipArchive"/> across empty, stored, deflated, and Unicode-named files.
    /// </summary>
    [TestCase]
    public void IncrementalZipPacker_ProducesSpecCompliantArchiveReadableByZipArchive()
    {
        string tempRoot = Path.Combine(TestTemp.Root, $"zip-unit-{Guid.NewGuid():N}");
        string appDir = Path.Combine(tempRoot, "MyApp");
        Directory.CreateDirectory(Path.Combine(appDir, "sub"));
        try
        {
            File.WriteAllBytes(Path.Combine(appDir, "empty.bin"), Array.Empty<byte>());
            File.WriteAllText(Path.Combine(appDir, "tiny.txt"), "short");

            string compressibleText = string.Concat(System.Linq.Enumerable.Repeat("Refii incremental zip test line!\n", 500));
            File.WriteAllText(Path.Combine(appDir, "sub", "compressible_üñîçødé.txt"), compressibleText, Encoding.UTF8);

            string zipPath = Path.Combine(tempRoot, "out.zip");
            var hashIndex = new FileHashIndex();

            ZipPackStats stats = IncrementalZipPacker.PackDirectory(appDir, "MyApp", zipPath, hashIndex);
            AssertThat(stats.TotalEntries).IsEqual(3);
            AssertThat(stats.CompressedEntries).IsEqual(3);
            AssertThat(stats.NoOp).IsFalse();

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                AssertThat(archive.Entries.Count).IsEqual(3);

                ZipArchiveEntry? emptyEntry = archive.GetEntry("MyApp/empty.bin");
                AssertThat(emptyEntry).IsNotNull();
                AssertThat(emptyEntry!.Length).IsEqual(0L);

                ZipArchiveEntry? compEntry = archive.GetEntry("MyApp/sub/compressible_üñîçødé.txt");
                AssertThat(compEntry).IsNotNull();
                AssertThat(compEntry!.CompressedLength).IsLess(compEntry.Length);
                using var reader = new StreamReader(compEntry.Open(), Encoding.UTF8);
                AssertThat(reader.ReadToEnd()).IsEqual(compressibleText);
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// Verifies <see cref="GodotTestShim.CanSkipEditorWarmup"/> skips only warm
    /// <c>-e --headless --quit-after</c> invocations and falls back to full Godot execution
    /// whenever UID caches are cold, scripts are run via <c>-s</c>, or unimported assets exist.
    /// </summary>
    [TestCase]
    public void GodotTestShim_CanSkipEditorWarmup_VerifiesUidCacheAndUnimportedAssets()
    {
        string tempRoot = Path.Combine(TestTemp.Root, $"shim-unit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string[] warmupArgs = { "--path", ".", "-e", "--headless", "--quit-after", "100", "--verbose" };

            // Cold project without .godot/uid_cache.bin cannot skip warmup.
            AssertThat(GodotTestShim.CanSkipEditorWarmup(warmupArgs, tempRoot)).IsFalse();

            Directory.CreateDirectory(Path.Combine(tempRoot, ".godot"));
            File.WriteAllBytes(Path.Combine(tempRoot, ".godot", "uid_cache.bin"), new byte[] { 1, 2, 3, 4 });

            // Missing GdUnit4TestRunnerScene.cs.uid still requires warmup.
            AssertThat(GodotTestShim.CanSkipEditorWarmup(warmupArgs, tempRoot)).IsFalse();

            Directory.CreateDirectory(Path.Combine(tempRoot, "gdunit4_testadapter_v5"));
            File.WriteAllText(
                Path.Combine(tempRoot, "gdunit4_testadapter_v5", "GdUnit4TestRunnerScene.cs.uid"),
                "uid://runner");

            // Warm project skips redundant editor warmup.
            AssertThat(GodotTestShim.CanSkipEditorWarmup(warmupArgs, tempRoot)).IsTrue();

            // Cache-disabled override forces full warmup.
            AssertThat(GodotTestShim.CanSkipEditorWarmup(warmupArgs, tempRoot, cacheDisabled: true)).IsFalse();

            // Actual scene runner (-s) or --help must NEVER be skipped.
            string[] runnerArgs = { "--path", ".", "-s", "res://gdunit4_testadapter_v5/GdUnit4TestRunnerScene.cs", "--headless" };
            AssertThat(GodotTestShim.CanSkipEditorWarmup(runnerArgs, tempRoot)).IsFalse();
            AssertThat(GodotTestShim.CanSkipEditorWarmup(new[] { "--help" }, tempRoot)).IsFalse();

            // Unimported .tscn without .uid/.import forces warmup until imported.
            string newScene = Path.Combine(tempRoot, "Unimported.tscn");
            File.WriteAllText(newScene, "[gd_scene format=3]");
            AssertThat(GodotTestShim.CanSkipEditorWarmup(warmupArgs, tempRoot)).IsFalse();

            File.WriteAllText(newScene + ".uid", "uid://scene");
            AssertThat(GodotTestShim.CanSkipEditorWarmup(warmupArgs, tempRoot)).IsTrue();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
