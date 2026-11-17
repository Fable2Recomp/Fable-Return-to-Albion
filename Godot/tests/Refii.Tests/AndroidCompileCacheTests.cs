using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>What survives between phone builds: reused objects, resumed builds, side-by-side compilers, and updates that need no build.</summary>
[TestSuite]
public sealed class AndroidCompileCacheTests
{
    /// <summary>A build with nothing changed generates and links, and compiles nothing.</summary>
    [TestCase]
    public async Task UnchangedBuildCompilesNothing()
    {
        using var build = new Build();
        var first = await build.RunAsync();
        AssertThat(build.Compiled.ToArray()).IsEqual(new[] { "refii_register.cpp", "fixture.cpp", "payload_export.cpp" });
        AssertThat(build.PrecompiledHeaders).IsEqual(1);
        build.Reset();
        var second = await build.RunAsync();
        AssertThat(build.Compiled.Count).IsEqual(0);
        AssertThat(build.PrecompiledHeaders).IsEqual(0);
        AssertThat(build.Generated).IsEqual(1);
        AssertThat(build.Linked).IsEqual(1);
        AssertThat(second.Directory == first.Directory).IsFalse();
        // In unit order, and from the cache, not from the attempt.
        AssertThat(build.LinkedObjects.Count).IsEqual(3);
        AssertThat(build.LinkedObjects.All(path => path.StartsWith(build.Request.ObjectCache!, StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>A hard mod's units and the hooks its chains rename compile every time: the cache keys a unit on its
    /// source, not on its own arguments. The rest is reused, and the module records no inputs for an APK to adopt.</summary>
    [TestCase]
    public async Task HardModsAndTheHooksTheyRenameCompileEveryTime()
    {
        using var build = new Build();
        string hook = Path.Combine(build.Request.BundleDirectory, "src", "hooks", "fixture.cpp");
        build.Mods = (generated, _, _) =>
        {
            string mod = Path.Combine(generated, "mods", "souls", "native", "gate.cpp");
            Directory.CreateDirectory(Path.GetDirectoryName(mod)!);
            File.WriteAllText(mod, "synthetic mod");
            return new AndroidPhoneCompiler.ModSources(new[] { mod }, new Dictionary<string, List<string>>
            {
                [mod] = new() { "-DREFII_MOD_NAME=\"souls\"" },
                [hook] = new() { "-Drex_A=rex_A__refii" },
            }, "mods-hash");
        };
        await build.RunAsync();
        AssertThat(build.Compiled.ToArray()).IsEqual(new[] { "refii_register.cpp", "fixture.cpp", "payload_export.cpp", "gate.cpp" });
        build.Reset();
        var second = await build.RunAsync();
        AssertThat(build.Compiled.ToArray()).IsEqual(new[] { "fixture.cpp", "gate.cpp" });
        AssertThat(second.Mods).IsEqual("mods-hash");
        AssertThat(File.Exists(Path.Combine(second.Directory, AndroidPhoneCompiler.ModuleInputsFile))).IsFalse();
    }

    /// <summary>A changed hook recompiles itself alone, without the precompiled header.</summary>
    [TestCase]
    public async Task ChangedHookRecompilesOnlyItself()
    {
        using var build = new Build();
        await build.RunAsync();
        build.Reset();
        build.ChangeBundleFile("src/hooks/fixture.cpp");
        await build.RunAsync();
        AssertThat(build.Compiled.ToArray()).IsEqual(new[] { "fixture.cpp" });
        AssertThat(build.PrecompiledHeaders).IsEqual(0);
    }

    /// <summary>A header that only some units read recompiles those; a header the units never read recompiles none.</summary>
    [TestCase]
    public async Task HeadersRecompileTheUnitsThatReadThem()
    {
        using var build = new Build();
        await build.RunAsync();
        build.Reset();
        build.ChangeBundleFile("include/hook.h");
        await build.RunAsync();
        AssertThat(build.Compiled.ToArray()).IsEqual(new[] { "fixture.cpp" });
        build.Reset();
        build.ChangeBundleFile("include/pch.h");
        await build.RunAsync();
        AssertThat(build.Compiled.ToArray()).IsEqual(new[] { "refii_register.cpp" });
        AssertThat(build.PrecompiledHeaders).IsEqual(1);
        build.Reset();
        build.ChangeBundleFile("include/unread.h");
        await build.RunAsync();
        AssertThat(build.Compiled.Count).IsEqual(0);
    }

    /// <summary>Another compiler means nothing can be reused.</summary>
    [TestCase]
    public async Task AnotherCompilerRecompilesEverything()
    {
        using var build = new Build();
        await build.RunAsync();
        build.Reset();
        build.ChangeNativeFile("librefii_clang.so", "another compiler");
        await build.RunAsync();
        AssertThat(build.Compiled.Count).IsEqual(3);
    }

    /// <summary>A file of the same name may now come first on the include path, which no recorded hash would show.</summary>
    [TestCase]
    public async Task NewFileOfTheSameNameIsAChange()
    {
        using var build = new Build();
        await build.RunAsync();
        build.Reset();
        build.AddBundleFile("include/early/hook.h");
        await build.RunAsync();
        AssertThat(build.Compiled.ToArray()).IsEqual(new[] { "fixture.cpp" });
        build.Reset();
        build.AddBundleFile("include/early/nothing-reads-this.h");
        await build.RunAsync();
        AssertThat(build.Compiled.Count).IsEqual(0);
        // Not reachable by any include: a third-party tree's own copy of the name.
        build.Reset();
        build.AddBundleFile("src/thirdparty/library/hook.h");
        await build.RunAsync();
        AssertThat(build.Compiled.Count).IsEqual(0);
        // Beside a source that includes it: found before the include directories.
        build.Reset();
        build.AddBundleFile("src/hooks/hook.h");
        await build.RunAsync();
        AssertThat(build.Compiled.ToArray()).IsEqual(new[] { "fixture.cpp" });
    }

    /// <summary>A generated source that changes recompiles, and the same bytes in a new attempt do not.</summary>
    [TestCase]
    public async Task GeneratedSourcesAreComparedByTheirBytes()
    {
        using var build = new Build();
        await build.RunAsync();
        build.Reset();
        build.RegistrationCount = 600;
        await build.RunAsync();
        AssertThat(build.Compiled.ToArray()).IsEqual(new[] { "refii_register.cpp" });
    }

    /// <summary>A build stopped part way keeps what it finished, and the next compiles the rest.</summary>
    [TestCase]
    public async Task InterruptedBuildResumes()
    {
        using var build = new Build();
        build.FailOn = "payload_export.cpp";
        bool failed = false;
        try { await build.RunAsync(); }
        catch (InvalidDataException) { failed = true; }
        AssertThat(failed).IsTrue();
        build.Reset();
        build.FailOn = "";
        await build.RunAsync();
        AssertThat(build.Compiled.ToArray()).IsEqual(new[] { "payload_export.cpp" });
    }

    /// <summary>A damaged object is a miss, not a broken module.</summary>
    [TestCase]
    public async Task DamagedObjectIsCompiledAgain()
    {
        using var build = new Build();
        await build.RunAsync();
        foreach (string file in Directory.GetFiles(build.Request.ObjectCache!, "*.o")) File.WriteAllText(file, "damaged");
        build.Reset();
        await build.RunAsync();
        AssertThat(build.Compiled.Count).IsEqual(3);
        AssertThat(build.PrecompiledHeaders).IsEqual(1);
    }

    /// <summary>A unit that read something a record cannot name is compiled every time, and its module is never called current.</summary>
    [TestCase]
    public async Task UnnameableInputIsNotCached()
    {
        using var build = new Build { ExtraDependency = "/somewhere/else/header.h" };
        var result = await build.RunAsync();
        AssertThat(File.Exists(Path.Combine(result.Directory, AndroidPhoneCompiler.ModuleInputsFile))).IsFalse();
        build.Reset();
        await build.RunAsync();
        AssertThat(build.Compiled.Count).IsEqual(3);
    }

    /// <summary>Success keeps only what it used; a failure removes nothing, so that it can resume.</summary>
    [TestCase]
    public async Task OnlySuccessPrunes()
    {
        using var build = new Build();
        await build.RunAsync();
        string[] Files() => Directory.GetFiles(build.Request.ObjectCache!).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray()!;
        string[] before = Files();
        build.ChangeBundleFile("src/hooks/fixture.cpp");
        build.FailOn = "fixture.cpp";
        try { await build.RunAsync(); }
        catch (InvalidDataException) { }
        AssertThat(Files()).IsEqual(before);
        build.FailOn = "";
        await build.RunAsync();
        AssertThat(Files().Length).IsEqual(before.Length);
        AssertThat(Files().SequenceEqual(before)).IsFalse();
    }

    /// <summary>The generator finds the last build's placement of functions, and a build goes on if it chokes on it.</summary>
    [TestCase]
    public async Task GeneratorKeepsItsPlacement()
    {
        using var build = new Build();
        await build.RunAsync();
        AssertThat(build.SeededPartitions.Single()).IsEqual("");
        build.Reset();
        await build.RunAsync();
        AssertThat(build.SeededPartitions.Single()).IsEqual("placement 1");
        build.Reset();
        File.WriteAllText(Path.Combine(build.Request.ObjectCache!, AndroidPhoneCompiler.PartitionFile), "garbage");
        build.RejectPartition = "garbage";
        await build.RunAsync();
        AssertThat(build.SeededPartitions.ToArray()).IsEqual(new[] { "garbage", "" });
        AssertThat(File.ReadAllText(Path.Combine(build.Request.ObjectCache!, AndroidPhoneCompiler.PartitionFile)).StartsWith("placement", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Scratch dependency files go with the rest of a build's scratch; what the update check reads stays.</summary>
    [TestCase]
    public async Task TrimKeepsTheModuleInputs()
    {
        using var build = new Build();
        var result = await build.RunAsync();
        AssertThat(Directory.GetFiles(result.Directory, "*.d").Length > 0).IsTrue();
        File.WriteAllText(Path.Combine(result.Directory, ".refii-build-owner"), Path.GetFileName(result.Directory));
        AndroidInstallState.TrimBuild(result.Directory, true);
        AssertThat(Directory.GetFiles(result.Directory, "*.d").Length).IsEqual(0);
        AssertThat(File.Exists(Path.Combine(result.Directory, AndroidPhoneCompiler.ModuleInputsFile))).IsTrue();
        AssertThat(File.Exists(result.Module)).IsTrue();
    }

    /// <summary>A new APK that leaves the module's inputs alone needs no build, however its bundle identity moved.</summary>
    [TestCase]
    public async Task UnrelatedUpdateLeavesTheModuleCurrent()
    {
        using var build = new Build();
        var result = await build.RunAsync();
        AssertThat(build.StillCurrent(result)).IsTrue();
        // A header nothing reads, and a rebuilt library exporting all it did.
        build.ChangeBundleFile("include/unread.h");
        build.AddBundleFile("include/new/unrelated.h");
        build.RebuildLibrary("librefii_core.so", "guest_call", "extra_export");
        AssertThat(build.StillCurrent(result)).IsTrue();
    }

    /// <summary>Everything that does reach the module asks for a build.</summary>
    [TestCase]
    public async Task UpdatesThatReachTheModuleAreStale()
    {
        async Task Stale(Action<Build> change)
        {
            using var build = new Build();
            var result = await build.RunAsync();
            AssertThat(build.StillCurrent(result)).IsTrue();
            change(build);
            AssertThat(build.StillCurrent(result)).IsFalse();
        }

        await Stale(b => b.ChangeBundleFile("src/hooks/fixture.cpp"));
        await Stale(b => b.ChangeBundleFile("include/hook.h"));
        await Stale(b => b.ChangeBundleFile("include/pch.h"));
        await Stale(b => b.ChangeBundleFile("versions/Fable2TU1/refii_config.toml"));
        await Stale(b => b.AddBundleFile("include/early/hook.h"));
        await Stale(b => b.ChangeNativeFile("librefii_codegen.so", "another generator"));
        await Stale(b => b.ChangeNativeFile("librefii_clang.so", "another compiler"));
        await Stale(b => b.ChangeNativeFile("librefii_lld.so", "another linker"));
        await Stale(b => b.Manifest.Hooks = b.Manifest.Hooks.Append("src/hooks/other.cpp").ToArray());
        await Stale(b => b.Manifest.Includes = new[] { "include/first" }.Concat(b.Manifest.Includes).ToArray());
        // The library stopped exporting what the module binds: RTLD_NOW would refuse it at load.
        await Stale(b => b.RebuildLibrary("librefii_core.so", "extra_export"));
        await Stale(b => File.Delete(Path.Combine(b.Request.NativeDirectory, "librefii_lua.so")));
    }

    /// <summary>A module from before inputs were recorded, or a record cut short, is not assumed current.</summary>
    [TestCase]
    public async Task MissingOrTornRecordIsStale()
    {
        using var build = new Build();
        var result = await build.RunAsync();
        string record = Path.Combine(result.Directory, AndroidPhoneCompiler.ModuleInputsFile);
        string text = File.ReadAllText(record);
        File.WriteAllText(record, text[..(text.Length - 8)]);
        AssertThat(build.StillCurrent(result)).IsFalse();
        File.Delete(record);
        AssertThat(build.StillCurrent(result)).IsFalse();
    }

    /// <summary>Recipes leave out the folders that move between builds and APK installs.</summary>
    [TestCase]
    public void RecipeIgnoresFolders()
    {
        using var one = new Build();
        using var two = new Build();
        string Recipe(Build build) => AndroidPhoneCompiler.ModuleRecipe(build.Manifest, null, "Fable2TU1", true);
        AssertThat(Recipe(two)).IsEqual(Recipe(one));
        AssertThat(AndroidPhoneCompiler.UnitRecipe(one.Manifest, null, "Fable2", true)).IsNotEqual(AndroidPhoneCompiler.UnitRecipe(one.Manifest, null, "Fable2TU1", true));
        AssertThat(AndroidPhoneCompiler.UnitRecipe(one.Manifest, null, "Fable2TU1", false)).IsNotEqual(AndroidPhoneCompiler.UnitRecipe(one.Manifest, null, "Fable2TU1", true));
    }

    /// <summary>Reads what clang writes, including the escapes of a path with spaces.</summary>
    [TestCase]
    public void DependencyFilesParse()
    {
        string text = "/out/unit\\ 1.o: /a/b.cpp \\\n  /a/with\\ space.h /a/dollar$$.h \\\r\n  /a/hash\\#.h\n";
        AssertThat(AndroidCompileCache.ParseDependencies(text).ToArray())
            .IsEqual(new[] { "/a/b.cpp", "/a/with space.h", "/a/dollar$.h", "/a/hash#.h" });
        AssertThat(AndroidCompileCache.ParseDependencies("").Count).IsEqual(0);
    }

    /// <summary>The ELF reader sees exports and imports, and refuses what is not ELF.</summary>
    [TestCase]
    public void ModuleImportsReadDynamicSymbols()
    {
        string root = Path.Combine(Refii.Tests.TestTemp.Root, "refii-elf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string module = Path.Combine(root, "module.so"), library = Path.Combine(root, "library.so"), junk = Path.Combine(root, "junk.so");
            File.WriteAllBytes(module, DynamicElf(new[] { "own_export" }, new[] { "from_library", "from_libc" }));
            File.WriteAllBytes(library, DynamicElf(new[] { "from_library", "unused" }, new[] { "from_libc" }));
            File.WriteAllText(junk, "not an elf file, and long enough to be read as its header............");
            AndroidPhoneCompiler.ValidateModule(File.ReadAllBytes(module));
            var read = AndroidModuleImports.Read(module)!;
            AssertThat(read.Defined.ToArray()).IsEqual(new[] { "own_export" });
            AssertThat(read.Undefined.ToArray()).IsEqual(new[] { "from_library", "from_libc" });
            AssertThat(AndroidModuleImports.Bound(module, new[] { library })!.ToArray()).IsEqual(new[] { "from_library" });
            AssertThat(AndroidModuleImports.Read(junk) == null).IsTrue();
            AssertThat(AndroidModuleImports.Read(Path.Combine(root, "absent.so")) == null).IsTrue();
            AssertThat(AndroidModuleImports.Bound(module, new[] { junk }) == null).IsTrue();
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Jobs follow free memory and the core count, and an unknown amount runs one as before.</summary>
    [TestCase]
    public void JobsFollowMemoryAndCores()
    {
        const long gb = 1024L * 1024 * 1024;
        AssertThat(AndroidPhoneCompiler.PlanJobs(8, -1)).IsEqual(1);
        AssertThat(AndroidPhoneCompiler.PlanJobs(8, gb / 2)).IsEqual(1);
        AssertThat(AndroidPhoneCompiler.PlanJobs(8, gb * 2)).IsEqual(2);
        AssertThat(AndroidPhoneCompiler.PlanJobs(8, gb * 3)).IsEqual(4);
        AssertThat(AndroidPhoneCompiler.PlanJobs(8, gb * 12)).IsEqual(8);
        AssertThat(AndroidPhoneCompiler.PlanJobs(4, gb * 12)).IsEqual(4);
        AssertThat(AndroidPhoneCompiler.PlanJobs(16, gb * 64)).IsEqual(AndroidPhoneCompiler.MostJobs);
        AssertThat(AndroidPhoneCompiler.PlanJobs(0, gb * 12)).IsEqual(1);
    }

    /// <summary>Units run side by side up to the limit, and all complete.</summary>
    [TestCase]
    public async Task UnitsRunSideBySide()
    {
        var pool = new FakePool();
        var units = Enumerable.Range(0, 12).ToList();
        var completed = new List<int>();
        await AndroidPhoneCompiler.RunUnitsAsync(units, 3, () => long.MaxValue, pool.Compile, completed.Add, Failure, CancellationToken.None);
        AssertThat(completed.OrderBy(unit => unit).ToArray()).IsEqual(units.ToArray());
        AssertThat(pool.Most).IsEqual(3);
    }

    /// <summary>With memory short only one runs, and more start when it is back.</summary>
    [TestCase]
    public async Task ShortMemoryHoldsJobsBack()
    {
        var pool = new FakePool();
        long free = 0;
        var units = Enumerable.Range(0, 4).ToList();
        await AndroidPhoneCompiler.RunUnitsAsync(units, 4, () => Interlocked.Read(ref free), pool.Compile, _ => { }, Failure, CancellationToken.None);
        AssertThat(pool.Most).IsEqual(1);
        pool = new FakePool();
        free = long.MaxValue;
        await AndroidPhoneCompiler.RunUnitsAsync(units, 4, () => Interlocked.Read(ref free), pool.Compile, _ => { }, Failure, CancellationToken.None);
        AssertThat(pool.Most).IsEqual(4);
    }

    /// <summary>A killed compiler is memory pressure, not a compile error: it runs again with fewer beside it.</summary>
    [TestCase]
    public async Task KilledUnitRunsAgainWithFewerJobs()
    {
        var pool = new FakePool { Statuses = { [5] = new Queue<int>(new[] { 137 }) } };
        var completed = new List<int>();
        var units = Enumerable.Range(0, 12).ToList();
        await AndroidPhoneCompiler.RunUnitsAsync(units, 4, () => long.MaxValue, pool.Compile, completed.Add, Failure, CancellationToken.None);
        AssertThat(completed.OrderBy(unit => unit).ToArray()).IsEqual(units.ToArray());
        AssertThat(pool.Attempts[5]).IsEqual(2);
        AssertThat(pool.Most).IsEqual(4);
        // A unit killed every time does end the build.
        var always = new FakePool { Statuses = { [0] = new Queue<int>(Enumerable.Repeat(137, 50)) } };
        bool failed = false;
        try { await AndroidPhoneCompiler.RunUnitsAsync(new[] { 0 }, 2, () => long.MaxValue, always.Compile, _ => { }, Failure, CancellationToken.None); }
        catch (InvalidDataException) { failed = true; }
        AssertThat(failed).IsTrue();
        AssertThat(always.Attempts[0]).IsEqual(4);
    }

    /// <summary>A compile error stops the others and is what the caller sees.</summary>
    [TestCase]
    public async Task CompileErrorStopsTheRest()
    {
        var pool = new FakePool { Statuses = { [1] = new Queue<int>(new[] { 7 }) } };
        var completed = new List<int>();
        bool failed = false;
        try { await AndroidPhoneCompiler.RunUnitsAsync(Enumerable.Range(0, 30).ToList(), 3, () => long.MaxValue, pool.Compile, completed.Add, Failure, CancellationToken.None); }
        catch (InvalidDataException error) { failed = error.Message == "unit 1 exited 7"; }
        AssertThat(failed).IsTrue();
        AssertThat(pool.Attempts.Count < 30).IsTrue();
        AssertThat(completed.Contains(1)).IsFalse();
    }

    /// <summary>Cancelling stops the running compilers and is reported as cancellation.</summary>
    [TestCase]
    public async Task CancellationStopsRunningJobs()
    {
        using var cancellation = new CancellationTokenSource();
        var pool = new FakePool { AfterStart = cancellation.Cancel };
        bool stopped = false;
        try { await AndroidPhoneCompiler.RunUnitsAsync(Enumerable.Range(0, 30).ToList(), 3, () => long.MaxValue, pool.Compile, _ => { }, Failure, cancellation.Token); }
        catch (OperationCanceledException) { stopped = true; }
        AssertThat(stopped).IsTrue();
        AssertThat(pool.Attempts.Count < 30).IsTrue();
    }

    private static Exception Failure(int unit, int status) => new InvalidDataException($"unit {unit} exited {status}");

    /// <summary>Stands in for compilers, counting how many run at once.</summary>
    private sealed class FakePool
    {
        private int _running;
        public int Most;
        public Dictionary<int, Queue<int>> Statuses { get; } = new();
        public Dictionary<int, int> Attempts { get; } = new();
        public Action? AfterStart { get; init; }

        public async Task<int> Compile(int unit, CancellationToken token)
        {
            lock (Attempts) Attempts[unit] = Attempts.GetValueOrDefault(unit) + 1;
            int now = Interlocked.Increment(ref _running);
            lock (Attempts) Most = Math.Max(Most, now);
            AfterStart?.Invoke();
            try
            {
                await Task.Delay(15, token);
                lock (Attempts) return Statuses.TryGetValue(unit, out var queue) && queue.Count > 0 ? queue.Dequeue() : 0;
            }
            finally { Interlocked.Decrement(ref _running); }
        }
    }

    /// <summary>An ELF that passes module validation and has dynamic symbols, as lld would lay them out.</summary>
    private static byte[] DynamicElf(string[] defined, string[] undefined)
    {
        var names = new MemoryStream();
        names.WriteByte(0);
        var symbols = new MemoryStream();
        symbols.Write(new byte[24]);
        void Add(string name, bool isDefined)
        {
            byte[] entry = new byte[24];
            BitConverter.GetBytes((uint)names.Length).CopyTo(entry, 0);
            names.Write(Encoding.UTF8.GetBytes(name));
            names.WriteByte(0);
            entry[4] = 1 << 4; // STB_GLOBAL
            if (isDefined) BitConverter.GetBytes((ushort)1).CopyTo(entry, 6);
            symbols.Write(entry);
        }
        foreach (string name in defined) Add(name, true);
        foreach (string name in undefined) Add(name, false);
        const int headers = 128;
        int symbolsAt = headers, namesAt = symbolsAt + (int)symbols.Length, sectionsAt = namesAt + (int)names.Length;
        byte[] data = new byte[sectionsAt + 3 * 64];
        data[0] = 0x7f; data[1] = (byte)'E'; data[2] = (byte)'L'; data[3] = (byte)'F'; data[4] = 2; data[5] = 1;
        BitConverter.GetBytes((ushort)3).CopyTo(data, 16);
        BitConverter.GetBytes((ushort)183).CopyTo(data, 18);
        BitConverter.GetBytes(64UL).CopyTo(data, 32);
        BitConverter.GetBytes((long)sectionsAt).CopyTo(data, 40);
        BitConverter.GetBytes((ushort)56).CopyTo(data, 54);
        BitConverter.GetBytes((ushort)1).CopyTo(data, 56);
        BitConverter.GetBytes((ushort)64).CopyTo(data, 58);
        BitConverter.GetBytes((ushort)3).CopyTo(data, 60);
        BitConverter.GetBytes(1U).CopyTo(data, 64);
        BitConverter.GetBytes(5U).CopyTo(data, 68);
        BitConverter.GetBytes((ulong)data.Length).CopyTo(data, 96);
        BitConverter.GetBytes((ulong)data.Length).CopyTo(data, 104);
        BitConverter.GetBytes(16384UL).CopyTo(data, 112);
        symbols.ToArray().CopyTo(data, symbolsAt);
        names.ToArray().CopyTo(data, namesAt);
        int dynsym = sectionsAt + 64;
        BitConverter.GetBytes(11U).CopyTo(data, dynsym + 4);
        BitConverter.GetBytes((long)symbolsAt).CopyTo(data, dynsym + 24);
        BitConverter.GetBytes(symbols.Length).CopyTo(data, dynsym + 32);
        BitConverter.GetBytes(2).CopyTo(data, dynsym + 40);
        int dynstr = sectionsAt + 128;
        BitConverter.GetBytes(3U).CopyTo(data, dynstr + 4);
        BitConverter.GetBytes((long)namesAt).CopyTo(data, dynstr + 24);
        BitConverter.GetBytes(names.Length).CopyTo(data, dynstr + 32);
        return data;
    }

    /// <summary>A phone build with a synthetic compiler that reports dependencies as Clang does, and a cache directory.</summary>
    private sealed class Build : IDisposable
    {
        private readonly AndroidPhoneCompilerTests.Fixture _fixture = new();
        private int _generatorRuns;

        public AndroidPhoneCompiler.Request Request { get; }
        public AndroidPhoneCompiler.BundleManifest Manifest => _fixture.Manifest;
        public List<string> Compiled { get; } = new();
        public List<string> LinkedObjects { get; } = new();
        public List<string> SeededPartitions { get; } = new();
        public int PrecompiledHeaders, Generated, Linked;
        public string FailOn = "", RejectPartition = "", ExtraDependency = "";
        public int RegistrationCount = 513;

        public Build()
        {
            Request = _fixture.Request with { ObjectCache = Path.Combine(_fixture.Root, "cache") };
            foreach (string path in new[] { "include/pch.h", "include/hook.h", "include/unread.h" }) AddBundleFile(path);
            foreach (string name in _fixture.Manifest.NativeFiles.Keys.ToList())
                if (name is not ("librefii_clang.so" or "librefii_lld.so" or "librefii_codegen.so"))
                    RebuildLibrary(name, name == "librefii_core.so" ? new[] { "guest_call" } : new[] { "export_of_" + name });
            _fixture.SaveManifest();
        }

        public void Reset()
        {
            Compiled.Clear();
            LinkedObjects.Clear();
            SeededPartitions.Clear();
            PrecompiledHeaders = Generated = Linked = 0;
        }

        public void AddBundleFile(string path)
        {
            string file = Path.Combine(Request.BundleDirectory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "original " + path);
            Manifest.Files[path] = Hash(file);
            _fixture.SaveManifest();
        }

        public void ChangeBundleFile(string path)
        {
            string file = Path.Combine(Request.BundleDirectory, path);
            File.AppendAllText(file, " changed");
            Manifest.Files[path] = Hash(file);
            _fixture.SaveManifest();
        }

        public void ChangeNativeFile(string name, string text)
        {
            string file = Path.Combine(Request.NativeDirectory, name);
            File.WriteAllText(file, text);
            Manifest.NativeFiles[name] = Hash(file);
            _fixture.SaveManifest();
        }

        public void RebuildLibrary(string name, params string[] exports)
        {
            string file = Path.Combine(Request.NativeDirectory, name);
            File.WriteAllBytes(file, DynamicElf(exports, Array.Empty<string>()));
            Manifest.NativeFiles[name] = Hash(file);
            _fixture.SaveManifest();
        }

        public AndroidPhoneCompiler.AddMods? Mods;

        public Task<AndroidPhoneCompiler.Result> RunAsync() =>
            AndroidPhoneCompiler.CompileAsync(Request with { Mods = Mods }, _ => { }, CancellationToken.None, Execute, _ => true);

        public bool StillCurrent(AndroidPhoneCompiler.Result result) =>
            AndroidPhoneCompiler.StillCurrent(result, Manifest, Request.NativeDirectory, null);

        private Task<int> Execute(AndroidPhoneCompiler.Command command, CancellationToken token)
        {
            var args = command.Arguments.ToList();
            string generated = Path.Combine(command.Directory, "generated");
            if (command.Executable.EndsWith("librefii_codegen.so", StringComparison.Ordinal))
            {
                Generated++;
                string seed = Path.Combine(generated, AndroidPhoneCompiler.PartitionFile);
                string seen = File.Exists(seed) ? File.ReadAllText(seed) : "";
                SeededPartitions.Add(seen);
                if (seen.Length > 0 && seen == RejectPartition) return Task.FromResult(3);
                Directory.CreateDirectory(generated);
                File.WriteAllText(Path.Combine(generated, "refii_register.cpp"), Registration(RegistrationCount));
                File.WriteAllText(Path.Combine(generated, "refii_pch.h"), "synthetic header");
                File.WriteAllText(Path.Combine(generated, "sources.cmake"), "set(GENERATED_SOURCES\n${CMAKE_CURRENT_LIST_DIR}/refii_register.cpp\n)");
                File.WriteAllText(seed, "placement " + ++_generatorRuns);
                return Task.FromResult(0);
            }
            string output = args[args.IndexOf("-o") + 1];
            if (args.Contains("-shared"))
            {
                Linked++;
                LinkedObjects.AddRange(args.Where(arg => arg.EndsWith(".o", StringComparison.Ordinal)));
                File.WriteAllBytes(output, DynamicElf(new[] { "module_export" }, new[] { "guest_call", "memcpy" }));
                return Task.FromResult(0);
            }
            string header = Path.Combine(Request.BundleDirectory, "include");
            int depFlag = args.IndexOf("-MF");
            if (args.Contains("c++-header"))
            {
                PrecompiledHeaders++;
                File.WriteAllText(args[depFlag + 1], output + ": " + Path.Combine(generated, "refii_pch.h") + " \\\n  " + Path.Combine(header, "pch.h") + "\n");
                File.WriteAllBytes(output, new byte[] { 1 });
                return Task.FromResult(0);
            }
            string source = args[args.IndexOf("-c") + 1];
            string unit = Path.GetFileName(source);
            if (unit == FailOn) return Task.FromResult(9);
            Compiled.Add(unit);
            var dependencies = new List<string> { source };
            if (args.Contains("-include-pch"))
                dependencies.AddRange(new[] { args[args.IndexOf("-include-pch") + 1], Path.Combine(generated, "refii_pch.h"), Path.Combine(header, "pch.h") });
            else if (unit == "fixture.cpp") dependencies.Add(Path.Combine(header, "hook.h"));
            if (ExtraDependency.Length > 0) dependencies.Add(ExtraDependency);
            File.WriteAllText(args[depFlag + 1], output + ": " + string.Join(" \\\n  ", dependencies) + "\n");
            File.WriteAllBytes(output, Encoding.UTF8.GetBytes("object of " + source + File.ReadAllText(source)));
            return Task.FromResult(0);
        }

        private static string Registration(int count) =>
            "#include \"refii_pch.h\"\nvoid refii_RegisterFunctions(rex::runtime::IModuleRegistrar* registrar) {\n" +
            string.Concat(Enumerable.Range(0, count).Select(i => $"  registrar->SetFunction(0x{i:x8}, sub_{i});\n")) + "}\n";

        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        public void Dispose() => _fixture.Dispose();
    }
}
