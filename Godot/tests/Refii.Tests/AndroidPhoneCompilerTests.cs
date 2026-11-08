using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Unit contracts and synthetic generate/compile/link/failure lifecycles for the Android installer.</summary>
[TestSuite]
public sealed class AndroidPhoneCompilerTests
{
    /// <summary>Native verification retains failed mappings, reuses stable successful handles and bounds engine lifetime.</summary>
    [TestCase]
    public void NativeMappingsRemainStableAcrossVerificationLifecycle()
    {
        int loads = 0, checks = 0;
        var lifetime = new AndroidPhoneCompiler.ModuleLifetime(_ => new IntPtr(++loads), (_, handle) =>
        { AssertThat(handle.ToInt64()).IsEqual(1L); return ++checks >= 2; }, 1);
        AssertThat(lifetime.Verify("owned-module", "digest-one")).IsFalse();
        AssertThat(lifetime.Verify("owned-module", "digest-one")).IsTrue();
        AssertThat(lifetime.Verify("owned-module", "digest-one")).IsTrue();
        AssertThat(loads).IsEqual(1);
        AssertThat(checks).IsEqual(2);
        bool changed = false, bounded = false;
        try { lifetime.Verify("owned-module", "digest-two"); } catch (InvalidDataException) { changed = true; }
        try { lifetime.Verify("another-module", "digest-one"); } catch (InvalidDataException) { bounded = true; }
        AssertThat(changed).IsTrue();
        AssertThat(bounded).IsTrue();
        AssertThat(loads).IsEqual(1);
    }

    /// <summary>A diagnostic copy preserves a previous compiler result and never publishes failed or unapproved verification.</summary>
    [TestCase]
    public async Task PriorPhoneModuleVerificationPreservesInstallationAndProvenance()
    {
        using var fixture = new Fixture();
        var first = await AndroidPhoneCompiler.CompileAsync(fixture.Request, _ => { }, CancellationToken.None, fixture.Generate);
        var second = await AndroidPhoneCompiler.VerifyExistingAsync(fixture.Request, first.Module, first.Sha256,
            first.Version, _ => { }, CancellationToken.None, _ => true);
        AssertThat(second.Directory == first.Directory).IsFalse();
        AssertThat(File.ReadAllBytes(second.Module)).IsEqual(File.ReadAllBytes(first.Module));
        AssertThat(File.ReadAllText(Path.Combine(second.Directory, "compilation-origin.json")).Contains("verification_only", StringComparison.Ordinal)).IsTrue();
        AssertThat(AndroidPhoneLauncher.CanActivate(second)).IsTrue();
        bool failed = false, unapproved = false;
        try { await AndroidPhoneCompiler.VerifyExistingAsync(fixture.Request, first.Module, first.Sha256,
            first.Version, _ => { }, CancellationToken.None, _ => false); }
        catch (InvalidDataException) { failed = true; }
        try { await AndroidPhoneCompiler.VerifyExistingAsync(fixture.Request, first.Module, new string('0', 64),
            first.Version, _ => { }, CancellationToken.None, _ => true); }
        catch (InvalidDataException) { unapproved = true; }
        AssertThat(failed).IsTrue();
        AssertThat(unapproved).IsTrue();
        AssertThat(Directory.GetFiles(fixture.Request.PrivateDirectory, "completed.json", SearchOption.AllDirectories).Length).IsEqual(2);
        AssertThat(File.Exists(Path.Combine(first.Directory, "completed.json"))).IsTrue();
        fixture.AssertPreserved();
    }

    /// <summary>A module records the APK bundle that built it, and only another bundle, or an unknown
    /// one, makes it stale: an update that ships new hooks or config must not run the old module.</summary>
    [TestCase]
    public async Task ModuleFromAnotherBundleIsStale()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Request.BundleDirectory, "extracted.complete"), "aaaa\n");
        var built = await AndroidPhoneCompiler.CompileAsync(fixture.Request, _ => { }, CancellationToken.None, fixture.Generate);
        AssertThat(built.Bundle).IsEqual("aaaa");
        AssertThat(File.ReadAllText(Path.Combine(built.Directory, "completed.json")).Contains("\"aaaa\"", StringComparison.Ordinal)).IsTrue();
        AssertThat(AndroidPhoneLauncher.IsStale(built, "aaaa")).IsFalse();
        AssertThat(AndroidPhoneLauncher.IsStale(built, "bbbb")).IsTrue();
        AssertThat(AndroidPhoneLauncher.IsStale(built with { Bundle = "" }, "aaaa")).IsTrue();
        AssertThat(AndroidPhoneLauncher.IsStale(null, "aaaa")).IsTrue();
        AssertThat(AndroidPhoneLauncher.IsStale(built with { Bundle = "" }, "")).IsFalse();
        fixture.AssertPreserved();
    }

    /// <summary>The bundle identity follows the files, not the zip: the same files packed at another
    /// time keep it, so an update that leaves the game code alone does not recompile.</summary>
    [TestCase]
    public void BundleIdentityIgnoresPackingTime()
    {
        static string Identity(DateTimeOffset packed, string hook)
        {
            using var stream = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
                foreach (var (name, text) in new[] { ("versions/Fable2TU1/refii_config.toml", "[functions]"), ("src/hooks/a.cpp", hook) })
                {
                    var entry = zip.CreateEntry(name);
                    entry.LastWriteTime = packed;
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(text);
                }
            stream.Position = 0;
            using var read = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
            return AndroidPhoneCompiler.BundleIdentity(read);
        }

        string first = Identity(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero), "guard");
        AssertThat(Identity(new DateTimeOffset(2026, 10, 29, 19, 0, 0, TimeSpan.Zero), "guard")).IsEqual(first);
        AssertThat(Identity(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero), "guarded")).IsNotEqual(first);
    }

    /// <summary>Successful repeated attempts preserve user inputs, old modules, saves and independent outputs.</summary>
    [TestCase]
    public async Task CompletedAttemptsPreserveExistingInstallation()
    {
        using var fixture = new Fixture();
        var commands = new List<AndroidPhoneCompiler.Command>();
        Task<int> Run(AndroidPhoneCompiler.Command command, CancellationToken token)
        { commands.Add(command); return fixture.Generate(command, token); }
        var first = await AndroidPhoneCompiler.CompileAsync(fixture.Request, _ => { }, CancellationToken.None, Run);
        var second = await AndroidPhoneCompiler.CompileAsync(fixture.Request, _ => { }, CancellationToken.None, Run);
        AssertThat(first.Version).IsEqual("Fable2TU1");
        AssertThat(first.Directory == second.Directory).IsFalse();
        AssertThat(File.ReadAllBytes(first.Module)).IsEqual(Elf());
        AssertThat(File.Exists(Path.Combine(first.Directory, "completed.json"))).IsTrue();
        AssertThat(AndroidPhoneLauncher.CanActivate(first)).IsTrue();
        File.AppendAllText(first.Module, "corrupted after completion");
        AssertThat(AndroidPhoneLauncher.CanActivate(first)).IsFalse();
        File.WriteAllBytes(first.Module, new byte[] { 0 });
        AssertThat(AndroidPhoneLauncher.CanActivate(first)).IsFalse();
        AssertThat(AndroidPhoneLauncher.CanActivate(null)).IsFalse();
        string registration = Path.Combine(second.Directory, "generated", "refii_register.cpp");
        AssertThat(File.Exists(registration + ".unsplit")).IsTrue();
        AssertThat(File.ReadAllText(registration).Contains("__attribute__((noinline))", StringComparison.Ordinal)).IsTrue();
        AssertThat(commands.Count).IsEqual(12);
        AssertThat(commands.All(command => command.NativeDirectory == fixture.Request.NativeDirectory)).IsTrue();
        AssertThat(commands[2].Arguments.Contains("-include-pch")).IsTrue();
        AssertThat(commands[3].Arguments.Contains("-include-pch")).IsFalse();
        AssertThat(commands[^1].Arguments.Contains("-Wl,-z,max-page-size=16384")).IsTrue();
        AssertThat(commands[^1].Arguments.Contains("-Wl,-z,common-page-size=16384")).IsTrue();
        AssertThat(commands[^1].Arguments.Contains("-Wl,-Bsymbolic-functions")).IsTrue();
        AssertThat(commands[^2].Arguments.Contains("-Wl,-Bsymbolic-functions")).IsFalse();
        fixture.AssertPreserved();
    }

    /// <summary>Unpatched generation selects the separate manifest and explicit guest layout definition.</summary>
    [TestCase]
    public async Task UnpatchedInputsUseTheirOwnGuestLayout()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.Request.GameDirectory, "default.xexp"));
        var result = await AndroidPhoneCompiler.CompileAsync(fixture.Request, _ => { }, CancellationToken.None,
            (command, token) =>
            {
                if (command.Executable.EndsWith("librefii_clang.so", StringComparison.Ordinal))
                    AssertThat(command.Arguments.Contains("-DREFII_GUEST_BUILD_UNPATCHED=1")).IsTrue();
                return fixture.Generate(command, token);
            });
        AssertThat(result.Version).IsEqual("Fable2");
        AssertThat(File.Exists(Path.Combine(result.Directory, "input", "default.xexp"))).IsFalse();
    }

    /// <summary>A genuine nonzero compiler exit stops the pipeline and never publishes success.</summary>
    [TestCase]
    public async Task CompilerExitStopsThePipelineWithoutPublishing()
    {
        using var fixture = new Fixture();
        int calls = 0;
        bool failed = false;
        try
        {
            await AndroidPhoneCompiler.CompileAsync(fixture.Request, _ => { }, CancellationToken.None,
                (command, token) => ++calls == 2 ? Task.FromResult(7) : fixture.Generate(command, token));
        }
        catch (InvalidDataException) { failed = true; }
        AssertThat(failed).IsTrue();
        AssertThat(calls).IsEqual(2);
        AssertThat(Directory.GetFiles(fixture.Request.PrivateDirectory, "completed.json", SearchOption.AllDirectories).Length).IsEqual(0);
        fixture.AssertPreserved();
    }

    /// <summary>A linker that dies from a signal is run again on one thread; a link error is not.</summary>
    [TestCase]
    public async Task LinkerCrashIsLinkedAgain()
    {
        using var fixture = new Fixture();
        var links = new List<AndroidPhoneCompiler.Command>();
        var result = await AndroidPhoneCompiler.CompileAsync(fixture.Request, _ => { }, CancellationToken.None,
            (command, token) =>
            {
                if (!command.Arguments.Contains("-shared")) return fixture.Generate(command, token);
                links.Add(command);
                if (links.Count > 1) return fixture.Generate(command, token);
                File.WriteAllText(command.Log, "librefii_clang: error: linker command failed due to signal (use -v to see invocation)\n");
                return Task.FromResult(1);
            });
        AssertThat(File.ReadAllBytes(result.Module)).IsEqual(Elf());
        AssertThat(links.Count).IsEqual(2);
        AssertThat(links[0].Arguments.Contains(AndroidPhoneCompiler.SingleThreadLink)).IsFalse();
        AssertThat(links[1].Arguments.Contains(AndroidPhoneCompiler.SingleThreadLink)).IsTrue();

        using var errors = new Fixture();
        int errorLinks = 0;
        bool failed = false;
        try
        {
            await AndroidPhoneCompiler.CompileAsync(errors.Request, _ => { }, CancellationToken.None,
                (command, token) =>
                {
                    if (!command.Arguments.Contains("-shared")) return errors.Generate(command, token);
                    ++errorLinks;
                    File.WriteAllText(command.Log, "ld.lld: error: undefined symbol: refii_missing\n");
                    return Task.FromResult(1);
                });
        }
        catch (InvalidDataException) { failed = true; }
        AssertThat(failed).IsTrue();
        AssertThat(errorLinks).IsEqual(1);
        errors.AssertPreserved();
    }

    /// <summary>A linked ELF that fails its loader check is retained for diagnosis and never published for activation.</summary>
    [TestCase]
    public async Task LoaderFailurePreventsCompletionWithoutChangingInstallation()
    {
        using var fixture = new Fixture();
        bool checkedModule = false, failed = false;
        try
        {
            await AndroidPhoneCompiler.CompileAsync(fixture.Request, _ => { }, CancellationToken.None,
                fixture.Generate, module =>
                {
                    checkedModule = File.Exists(module);
                    AndroidPhoneCompiler.ValidateModule(File.ReadAllBytes(module));
                    return false;
                });
        }
        catch (InvalidDataException) { failed = true; }
        AssertThat(checkedModule).IsTrue();
        AssertThat(failed).IsTrue();
        AssertThat(Directory.GetFiles(fixture.Request.PrivateDirectory, "completed.json", SearchOption.AllDirectories).Length).IsEqual(0);
        AssertThat(Directory.GetFiles(fixture.Request.PrivateDirectory, "librefii_generated.so", SearchOption.AllDirectories).Length).IsEqual(1);
        fixture.AssertPreserved();
    }

    /// <summary>Cancellation between phases or during loader verification stops before output can be published.</summary>
    [TestCase]
    public async Task CancellationStopsOwnedAttemptAndPreservesInputs()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        bool stopped = false;
        try
        {
            await AndroidPhoneCompiler.CompileAsync(fixture.Request, _ => { }, cancellation.Token,
                async (command, token) =>
                { calls++; int result = await fixture.Generate(command, token); cancellation.Cancel(); return result; });
        }
        catch (OperationCanceledException) { stopped = true; }
        AssertThat(stopped).IsTrue();
        AssertThat(calls).IsEqual(1);
        AssertThat(Directory.GetFiles(fixture.Request.PrivateDirectory, "completed.json", SearchOption.AllDirectories).Length).IsEqual(0);
        fixture.AssertPreserved();
        using var duringLoad = new CancellationTokenSource();
        stopped = false;
        try
        {
            await AndroidPhoneCompiler.CompileAsync(fixture.Request, _ => { }, duringLoad.Token,
                fixture.Generate, _ => { duringLoad.Cancel(); return true; });
        }
        catch (OperationCanceledException) { stopped = true; }
        AssertThat(stopped).IsTrue();
        AssertThat(Directory.GetFiles(fixture.Request.PrivateDirectory, "completed.json", SearchOption.AllDirectories).Length).IsEqual(0);
        fixture.AssertPreserved();
    }

    /// <summary>Changed headers, malformed pins and traversal recipes are refused before executing tools.</summary>
    [TestCase]
    public void BundleIntegrityAndPathChecksPrecedeExecution()
    {
        using var fixture = new Fixture();
        AssertThat(AndroidPhoneCompiler.Validate(fixture.Request).Api).IsEqual(31);
        fixture.Manifest.Api = 33;
        fixture.SaveManifest();
        AssertThat(Invalid(() => AndroidPhoneCompiler.Validate(fixture.Request))).IsTrue();
        fixture.Manifest.Api = 31;
        fixture.Manifest.Files["../outside"] = new string('a', 64);
        fixture.SaveManifest();
        AssertThat(Invalid(() => AndroidPhoneCompiler.Validate(fixture.Request))).IsTrue();
        fixture.Manifest.Files.Remove("../outside");
        fixture.SaveManifest();
        File.AppendAllText(Path.Combine(fixture.Request.BundleDirectory, "tools/android/payload_abi_guard.h"), "changed");
        AssertThat(Invalid(() => AndroidPhoneCompiler.Validate(fixture.Request))).IsTrue();
    }

    /// <summary>No attempt may recursively place build output inside an input or runtime directory.</summary>
    [TestCase]
    public void NestedRootsAreRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii-phone-paths-" + Guid.NewGuid().ToString("N"));
        var request = new AndroidPhoneCompiler.Request(Path.Combine(root, "public"),
            Path.Combine(root, "native"), Path.Combine(root, "game"), Path.Combine(root, "private"));
        // Containment must reject the request before opening any bundle file.
        AssertThat(Invalid(() => AndroidPhoneCompiler.Validate(request with
            { PrivateDirectory = Path.Combine(request.GameDirectory, "build") }))).IsTrue();
        AssertThat(Invalid(() => AndroidPhoneCompiler.Validate(request with
            { PrivateDirectory = request.NativeDirectory }))).IsTrue();
    }

    /// <summary>Generated lists cannot inject CMake variables, duplicate units or select external paths.</summary>
    [TestCase]
    public void GeneratedListIsLiteralAndContained()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii-phone-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "fixture.cpp"), list = Path.Combine(root, "sources.cmake");
        try
        {
        File.WriteAllText(source, "synthetic");
        File.WriteAllText(list, "set(GENERATED_SOURCES\n${CMAKE_CURRENT_LIST_DIR}/fixture.cpp\n)");
        AssertThat(AndroidPhoneCompiler.ReadSources(list, root)).IsEqual(new[] { source });
        foreach (string path in new[] { "${OTHER}/fixture.cpp", source.Replace('\\', '/') + "\n" + source.Replace('\\', '/'), "../outside.cpp" })
        {
            File.WriteAllText(list, "set(GENERATED_SOURCES\n" + path + "\n)");
            AssertThat(Invalid(() => AndroidPhoneCompiler.ReadSources(list, root))).IsTrue();
        }
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (File.Exists(list)) File.Delete(list);
            Directory.Delete(root, recursive: false);
        }
    }

    /// <summary>ARM64/16KiB ELF verification refuses host binaries, writable code and truncated segments.</summary>
    [TestCase]
    public void ModuleContractRejectsIncompatibleElf()
    {
        AndroidPhoneCompiler.ValidateModule(Elf());
        foreach (int offset in new[] { 4, 18, 54, 56, 68, 113 })
        {
            byte[] invalid = Elf();
            invalid[offset] = offset == 68 ? (byte)7 : (byte)0;
            AssertThat(Invalid(() => AndroidPhoneCompiler.ValidateModule(invalid))).IsTrue();
        }
        AssertThat(Invalid(() => AndroidPhoneCompiler.ValidateModule(new byte[2]))).IsTrue();
    }

    /// <summary>Compilation keeps IEEE arithmetic without contraction, mutex guards and no desktop code-model/fast-math switches.</summary>
    [TestCase]
    public void FlagsRetainAndroidArithmeticAndAbiRequirements()
    {
        // This unit checks a recipe, not filesystem integrity; lifecycle fixtures
        // cover the hashed files separately without creating them for this case.
        string root = Path.Combine(Path.GetTempPath(), "refii-phone-flags-" + Guid.NewGuid().ToString("N"));
        var request = new AndroidPhoneCompiler.Request(Path.Combine(root, "public"),
            Path.Combine(root, "native"), Path.Combine(root, "game"), Path.Combine(root, "private"));
        var manifest = new AndroidPhoneCompiler.BundleManifest { Includes = new[] { "sdk/include" } };
        var flags = AndroidPhoneCompiler.CompilerFlags(request, manifest, root, "Fable2TU1");
        AssertThat(flags.Contains("-ffp-model=precise")).IsTrue();
        AssertThat(flags.Contains("-ffp-contract=off")).IsTrue();
        AssertThat(flags.Contains("-fno-strict-aliasing")).IsTrue();
        AssertThat(flags.Contains("--target=aarch64-linux-android31")).IsTrue();
        AssertThat(flags.Any(flag => flag.StartsWith("-march", StringComparison.Ordinal))).IsFalse();
        var lse = AndroidPhoneCompiler.CompilerFlags(request, manifest, root, "Fable2TU1", lseAtomics: true);
        AssertThat(lse.Except(flags).ToArray()).IsEqual(new[] { "-march=armv8.2-a" });
        AssertThat(AndroidPhoneCompiler.HasLseAtomics()).IsFalse();
        AssertThat(flags.Any(flag => flag.EndsWith("payload_abi_guard.h", StringComparison.Ordinal))).IsTrue();
        AssertThat(flags.Contains("-ffast-math") || flags.Contains("-ffp-model=fast") || flags.Contains("-mcmodel=large")).IsFalse();
        AssertThat(AndroidPhoneCompiler.AndroidPayloadOpenFlags).IsEqual(0x102);
    }

    /// <summary>Fable 3 compiles with its own include list and loads after its own bridge; Fable 2's are unchanged.</summary>
    [TestCase]
    public void Fable3UsesItsOwnIncludesAndBridge()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii-phone-fable3-" + Guid.NewGuid().ToString("N"));
        var manifest = new AndroidPhoneCompiler.BundleManifest
        {
            Includes = new[] { "Fable2/src/vfs/include" }, Fable3Includes = new[] { "Fable3/src/hooks/src" },
        };
        var fable2 = new AndroidPhoneCompiler.Request(root, root, root, root);
        var fable3 = fable2 with { Title = Refii.Core.Engine_Title.Fable3 };
        var flags2 = AndroidPhoneCompiler.CompilerFlags(fable2, manifest, root, "Fable2TU1");
        var flags3 = AndroidPhoneCompiler.CompilerFlags(fable3, manifest, root, "Fable3");
        // The include paths are joined with the host's separator, so a PC run sees backslashes.
        string vfs2 = Path.Combine("Fable2", "src", "vfs", "include");
        string hooks3 = Path.Combine("Fable3", "src", "hooks", "src");
        AssertThat(flags2.Any(flag => flag.EndsWith(vfs2, StringComparison.Ordinal))).IsTrue();
        AssertThat(flags2.Any(flag => flag.EndsWith(hooks3, StringComparison.Ordinal))).IsFalse();
        AssertThat(flags3.Any(flag => flag.EndsWith(hooks3, StringComparison.Ordinal))).IsTrue();
        AssertThat(flags3.Any(flag => flag.EndsWith(vfs2, StringComparison.Ordinal))).IsFalse();
        AssertThat(flags3.Contains("-DREFII_GUEST_BUILD_UNPATCHED=1")).IsFalse();
        AssertThat(fable2.HostBridge).IsEqual("librefii_hostbridge.so");
        AssertThat(fable3.HostBridge).IsEqual("librefiii_hostbridge.so");
        // Fable 3's module links its VFS, which the APK ships under its own key beside Fable 2's.
        AssertThat(AndroidPhoneCompiler.Fable3Vfs).IsEqual("librefiii_vfs.so");
    }

    /// <summary>Fable 2's installs stay in the app's data directory; Fable 3's get a folder of their own.</summary>
    [TestCase]
    public void EachTitleKeepsItsOwnInstallFolder()
    {
        string user = Path.Combine(Path.GetTempPath(), "refii-user");
        AssertThat(AndroidRuntimeOptions.TitleDirectory(user, Refii.Core.Engine_Title.Fable2)).IsEqual(user);
        AssertThat(AndroidRuntimeOptions.TitleDirectory(user, Refii.Core.Engine_Title.Fable3))
            .IsEqual(Path.Combine(user, "Fable3"));
    }

    /// <summary>An older generator's process-wide interrupt lock becomes the template's thread-local depth, once.</summary>
    [TestCase]
    public void InterruptLockBecomesTheTemplatesThreadLocalDepth()
    {
        string old = AndroidPhoneCompiler.ProcessWideGuestLock.ReplaceLineEndings("\n");
        string header = "// head\n" + old + "// tail\n";
        string rewritten = AndroidPhoneCompiler.ThreadLocalLockDepth(header);
        AssertThat(rewritten).IsEqual("// head\n" + AndroidPhoneCompiler.ThreadLocalGuestLock.ReplaceLineEndings("\n") + "// tail\n");
        AssertThat(rewritten.Contains("AcquireDirect")).IsFalse();
        AssertThat(AndroidPhoneCompiler.ThreadLocalLockDepth(rewritten)).IsEqual(rewritten);
        AssertThat(AndroidPhoneCompiler.ThreadLocalLockDepth("synthetic header")).IsEqual("synthetic header");
        AssertThat(AndroidPhoneCompiler.ThreadLocalLockDepth(old + old)).IsEqual(old + old);
        AssertThat(TemplateSource().ReplaceLineEndings("\n")
            .Contains(AndroidPhoneCompiler.ThreadLocalGuestLock.ReplaceLineEndings("\n"))).IsTrue();
    }

    /// <summary>The SDK shadow's generator template, located relative to this test.</summary>
    private static string TemplateSource([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!,
            "../../../rexglue-net/shadow/rexglue-sdk/resources/templates/codegen/pch_h.inja")));

    /// <summary>Bounded helpers preserve all calls, including duplicates, in exact registration order.</summary>
    [TestCase]
    public void RegistrationHelpersPreserveOrderedCallsAndBoundaries()
    {
        foreach (int count in new[] { 0, 512, 513, 1025 })
        {
            string source = Registration(count);
            string result = AndroidPhoneCompiler.BoundRegistration(source);
            string[] before = Regex.Matches(source, @"registrar->SetFunction\([^;]+;").Select(match => match.Value).ToArray();
            string[] after = Regex.Matches(result, @"registrar->SetFunction\([^;]+;").Select(match => match.Value).ToArray();
            AssertThat(after).IsEqual(before);
            if (count <= 512) AssertThat(result).IsEqual(source);
            else
            {
                int parts = (count + 511) / 512;
                AssertThat(Regex.Matches(result, "static __attribute__").Count).IsEqual(parts);
                AssertThat(Regex.Matches(result, @"  refii_RegisterFunctions_PhonePart_\d+\(registrar\);")
                    .Select(match => match.Value).ToArray()).IsEqual(Enumerable.Range(0, parts)
                    .Select(part => $"  refii_RegisterFunctions_PhonePart_{part}(registrar);").ToArray());
            }
        }
    }

    /// <summary>Unexpected expressions or generator layouts cannot silently lose initialization behavior.</summary>
    [TestCase]
    public void RegistrationRejectsUnknownGeneratedStatements()
    {
        foreach (string source in new[] { "unknown", Registration(1).Replace("sub_82170000", "foo()", StringComparison.Ordinal),
                     Registration(1).Replace("registrar->SetFunction", "if (true) registrar->SetFunction", StringComparison.Ordinal),
                     Registration(1) + "void other() {}", Registration(1).Replace("refii_RegisterFunctions", "different", StringComparison.Ordinal) })
            AssertThat(Invalid(() => AndroidPhoneCompiler.BoundRegistration(source))).IsTrue();
    }

    /// <summary>Builds synthetic duplicate registration calls to test order without game-derived source.</summary>
    private static string Registration(int count) => "// synthetic generator output\n#include \"refii_init.h\"\n" +
        "void refii_RegisterFunctions(rex::runtime::IModuleRegistrar* registrar) {\n" +
        string.Concat(Enumerable.Range(0, count).Select(index =>
            $"  registrar->SetFunction(0x{0x82170000L + index % 17 * 4:X8}, sub_{0x82170000L + index % 17 * 4:X8});\n")) + "}\n";

    /// <summary>Real child execution retains output and exit status; prior cancellation launches nothing.</summary>
    [TestCase]
    public async Task ChildDiagnosticsRecordActualExitAndRespectPriorCancellation()
    {
        // Child execution only needs an owned working directory and diagnostic
        // file, rather than a complete synthetic compiler installation.
        string root = Path.Combine(Path.GetTempPath(), "refii-phone-child-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string log = Path.Combine(root, "child.log");
        try
        {
        string executable = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
        string[] args = OperatingSystem.IsWindows() ? new[] { "/d", "/c", "echo fixture-output&exit /b 7" }
            : new[] { "-c", "printf fixture-output; exit 7" };
        var command = new AndroidPhoneCompiler.Command(executable, args, root, root, log);
        int exit = await AndroidPhoneCompiler.RunChildAsync(command, CancellationToken.None);
        AssertThat(exit).IsEqual(7);
        string text = File.ReadAllText(log);
        AssertThat(text.Contains("fixture-output", StringComparison.Ordinal)).IsTrue();
        AssertThat(text.Contains("tool_exit=7; interrupted=False", StringComparison.Ordinal)).IsTrue();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool stopped = false;
        try { await AndroidPhoneCompiler.RunChildAsync(command with { Executable = "must-not-launch" }, cancellation.Token); }
        catch (OperationCanceledException) { stopped = true; }
        AssertThat(stopped).IsTrue();
        AssertThat(File.ReadAllText(log)).IsEqual(text);
        }
        finally
        {
            if (File.Exists(log)) File.Delete(log);
            Directory.Delete(root, recursive: false);
        }
    }

    /// <summary>RELRO page rounding may never protect adjacent writable bytes, even with aligned LOAD headers.</summary>
    [TestCase]
    public void ModuleProtectionChecksRoundedRelroAndMemoryBounds()
    {
        byte[] data = new byte[176];
        Elf().CopyTo(data, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(data, 56);
        BitConverter.GetBytes(6U).CopyTo(data, 68);
        BitConverter.GetBytes(0x4000UL).CopyTo(data, 80);
        BitConverter.GetBytes(0x8000UL).CopyTo(data, 104);
        BitConverter.GetBytes(0x6474e552U).CopyTo(data, 120);
        BitConverter.GetBytes(0x4000UL).CopyTo(data, 136);
        BitConverter.GetBytes(0x1000UL).CopyTo(data, 160);
        AssertThat(Invalid(() => AndroidPhoneCompiler.ValidateModule(data))).IsTrue();
        BitConverter.GetBytes(0x4000UL).CopyTo(data, 160);
        AndroidPhoneCompiler.ValidateModule(data);
        BitConverter.GetBytes(ulong.MaxValue).CopyTo(data, 160);
        AssertThat(Invalid(() => AndroidPhoneCompiler.ValidateModule(data))).IsTrue();
        byte[] shortMemory = Elf();
        BitConverter.GetBytes(1UL).CopyTo(shortMemory, 104);
        AssertThat(Invalid(() => AndroidPhoneCompiler.ValidateModule(shortMemory))).IsTrue();
    }

    /// <summary>Creates a structurally valid synthetic ELF; this is not phone execution evidence.</summary>
    private static byte[] Elf()
    {
        byte[] data = new byte[128];
        data[0] = 0x7f; data[1] = (byte)'E'; data[2] = (byte)'L'; data[3] = (byte)'F'; data[4] = 2; data[5] = 1;
        BitConverter.GetBytes((ushort)3).CopyTo(data, 16);
        BitConverter.GetBytes((ushort)183).CopyTo(data, 18);
        BitConverter.GetBytes(64UL).CopyTo(data, 32);
        BitConverter.GetBytes((ushort)56).CopyTo(data, 54);
        BitConverter.GetBytes((ushort)1).CopyTo(data, 56);
        BitConverter.GetBytes(1U).CopyTo(data, 64);
        BitConverter.GetBytes(5U).CopyTo(data, 68);
        BitConverter.GetBytes(128UL).CopyTo(data, 96);
        BitConverter.GetBytes(128UL).CopyTo(data, 104);
        BitConverter.GetBytes(16384UL).CopyTo(data, 112);
        return data;
    }

    /// <summary>Checks the explicit input failure contract in a standalone test process.</summary>
    private static bool Invalid(Action action)
    { try { action(); return false; } catch (InvalidDataException) { return true; } }

    /// <summary>Owns only synthetic temporary inputs, with ordinary files and no Godot engine objects.</summary>
    internal sealed class Fixture : IDisposable
    {
        /// <summary>Fresh temporary fixture root.</summary>
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "refii-phone-test-" + Guid.NewGuid().ToString("N"));
        /// <summary>Explicit separated build roots.</summary>
        public AndroidPhoneCompiler.Request Request { get; }
        /// <summary>Mutable synthetic manifest for rejection tests.</summary>
        public AndroidPhoneCompiler.BundleManifest Manifest { get; } = new()
        { Schema = 1, Abi = "arm64-v8a", Api = 31, Ndk = "28.2.13676358", SdkPin = "bc812c08ab2efd4ed8d7f306bd1911c703771732",
          Includes = new[] { "include" }, Hooks = new[] { "src/hooks/fixture.cpp" } };

        /// <summary>Constructs small public and private stand-ins, never game-derived bytes.</summary>
        public Fixture()
        {
            Request = new(Path.Combine(Root, "bundle"), Path.Combine(Root, "native"),
                Path.Combine(Root, "game"), Path.Combine(Root, "attempts"));
            foreach (string path in new[] { Request.BundleDirectory, Request.NativeDirectory, Request.GameDirectory,
                         Request.PrivateDirectory, Path.Combine(Request.BundleDirectory, "include") }) Directory.CreateDirectory(path);
            foreach (string path in new[] { "src/hooks/fixture.cpp", "src/payload/src/payload_export.cpp",
                         "tools/android/payload_abi_guard.h", "src/payload/src/libcxx_chrono_shim.h",
                         "versions/Fable2/refii_manifest.toml", "versions/Fable2TU1/refii_manifest.toml",
                         "versions/Fable2/refii_config.toml", "versions/Fable2TU1/refii_config.toml" })
            {
                string file = Path.Combine(Request.BundleDirectory, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, path.EndsWith("manifest.toml", StringComparison.Ordinal)
                    ? "file_path = \"old\"\nout_directory_path = \"old\"\n" : "synthetic");
                Manifest.Files[path] = Hash(file);
            }
            foreach (string name in new[] { "librefii_clang.so", "librefii_lld.so", "librefii_codegen.so", "librefii_core.so",
                         "librefii_runtime.so", "librefii_hostbridge.so", "librexruntime.so", "librefii_lua.so", "librefii_vfs.so", "libc++_shared.so" })
            {
                string file = Path.Combine(Request.NativeDirectory, name);
                File.WriteAllText(file, "synthetic tool");
                Manifest.NativeFiles[name] = Hash(file);
            }
            File.WriteAllText(Path.Combine(Request.GameDirectory, "default.xex"), "synthetic disc");
            File.WriteAllText(Path.Combine(Request.GameDirectory, "default.xexp"), "synthetic update");
            File.WriteAllText(Path.Combine(Root, "old-module"), "working original");
            File.WriteAllText(Path.Combine(Root, "save"), "existing progress");
            SaveManifest();
        }

        /// <summary>Writes only the fixture's public manifest.</summary>
        public void SaveManifest() => File.WriteAllText(Path.Combine(Request.BundleDirectory, "bundle.json"), JsonSerializer.Serialize(Manifest));

        /// <summary>Produces expected generator/compiler outputs for the complete synthetic subsystem lifecycle.</summary>
        public Task<int> Generate(AndroidPhoneCompiler.Command command, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (command.Executable.EndsWith("librefii_codegen.so", StringComparison.Ordinal))
            {
                string root = Path.Combine(command.Directory, "generated");
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "refii_register.cpp"), Registration(513));
                File.WriteAllText(Path.Combine(root, "refii_pch.h"), "synthetic header");
                File.WriteAllText(Path.Combine(root, "sources.cmake"), "set(GENERATED_SOURCES\n${CMAKE_CURRENT_LIST_DIR}/refii_register.cpp\n)");
            }
            else
            {
                int output = command.Arguments.ToList().IndexOf("-o") + 1;
                File.WriteAllBytes(command.Arguments[output], command.Arguments.Contains("-shared") ? Elf() : new byte[] { 1 });
            }
            return Task.FromResult(0);
        }

        /// <summary>Checks that the feature pipeline preserved unrelated installed/private data exactly.</summary>
        public void AssertPreserved()
        {
            AssertThat(File.ReadAllText(Path.Combine(Root, "old-module"))).IsEqual("working original");
            AssertThat(File.ReadAllText(Path.Combine(Root, "save"))).IsEqual("existing progress");
            AssertThat(File.ReadAllText(Path.Combine(Request.GameDirectory, "default.xex"))).IsEqual("synthetic disc");
            AssertThat(File.ReadAllText(Path.Combine(Request.GameDirectory, "default.xexp"))).IsEqual("synthetic update");
        }

        /// <summary>Deletes only this fixture's freshly created synthetic temporary tree.</summary>
        public void Dispose() => Directory.Delete(Root, true);

        /// <summary>Hashes a tiny synthetic input for bundle validation.</summary>
        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }
}
