using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Refii.AndroidRuntime;

/// <summary>Compiles privately supplied game executables with APK-distributed Android tools.</summary>
/// <remarks>Each attempt owns a new directory. It never changes the installed module,
/// runtime configuration, source game or saves. Activation is a separate launcher action.
/// One compiler job limits peak memory; cancellation terminates only the owned child.</remarks>
public static class AndroidPhoneCompiler
{
    /// <summary>Physical roots for shipped tools, user inputs and new private output.</summary>
    /// <param name="BundleDirectory">Extracted public headers, sources, sysroot and manifest.</param>
    /// <param name="NativeDirectory">Read-only APK native library directory, containing executable tools.</param>
    /// <param name="GameDirectory">Installed game containing the user's default.xex and optional default.xexp.</param>
    /// <param name="PrivateDirectory">Parent for unique private compilation attempts.</param>
    public sealed record Request(string BundleDirectory, string NativeDirectory,
        string GameDirectory, string PrivateDirectory);

    /// <summary>Validated output of a completed attempt; no active installation is replaced.</summary>
    /// <param name="Directory">Attempt directory, also retained after failures for diagnostics.</param>
    /// <param name="Module">Compiled ARM64 module in that directory.</param>
    /// <param name="Sha256">Hash of the completed module bytes.</param>
    /// <param name="Version">Exact executable layout compiled by the generator.</param>
    public sealed record Result(string Directory, string Module, string Sha256, string Version)
    {
        /// <summary>Identity of the APK compiler bundle that built the module (<see cref="BundleIdentity"/>);
        /// empty for a module from before this was recorded. A new APK that ships other hooks or another
        /// config makes the module stale (<see cref="AndroidPhoneLauncher"/>).</summary>
        // Source-generated JSON writes every init property, null when the field is absent (an
        // installer state from an older APK), so null is read as unknown here.
        public string Bundle { get => _bundle; init => _bundle = value ?? ""; }
        private readonly string _bundle = "";
    }

    /// <summary>A compiler bundle's identity: what it holds, not how it was zipped.</summary>
    /// <remarks>Two APK builds from the same sources packed byte-different data.zip files (the entries'
    /// times), and an identity from the zip's bytes made every update recompile for about 25 minutes.
    /// The sorted entry names, sizes and CRC-32s change only when a file does.</remarks>
    /// <param name="archive">The APK's res://phonecompiler/data.zip.</param>
    public static string BundleIdentity(System.IO.Compression.ZipArchive archive)
    {
        var text = new StringBuilder();
        foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            text.Append(entry.FullName).Append('\0').Append(entry.Length).Append('\0').Append(entry.Crc32).Append('\n');
        // Engine_Hash, never System.Security.Cryptography: the NativeAOT Android build has no OpenSSL,
        // and the launcher aborted at start ("libssl.so.10" not found) when this called SHA256.HashData.
        return Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>The identity an extracted bundle was written with ("extracted.complete"), or empty.</summary>
    /// <param name="bundleDirectory">Extracted compiler bundle.</param>
    public static string RecordedBundle(string bundleDirectory)
    {
        string marker = Path.Combine(bundleDirectory, "extracted.complete");
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
    }

    /// <summary>A tool invocation without shell interpretation or inherited desktop search paths.</summary>
    /// <param name="Executable">APK-distributed executable path.</param>
    /// <param name="Arguments">Individual arguments, including paths with spaces.</param>
    /// <param name="Directory">Fresh attempt's working directory.</param>
    /// <param name="NativeDirectory">Dependency lookup for the child only.</param>
    /// <param name="Log">Unique output log path.</param>
    public sealed record Command(string Executable, IReadOnlyList<string> Arguments,
        string Directory, string NativeDirectory, string Log);

    /// <summary>Runs a real child or an injected synthetic lifecycle fixture.</summary>
    /// <param name="command">Validated command and owned log.</param>
    /// <param name="cancellation">Cancellation of this attempt.</param>
    /// <returns>Actual tool exit status.</returns>
    public delegate Task<int> Execute(Command command, CancellationToken cancellation);

    /// <summary>Version-one payload accessor; returns immutable image metadata without starting guest execution.</summary>
    /// <returns>The descriptor pointer owned by the loaded module.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ImageConfig();

    /// <summary>Android ARM64 RTLD_NOW | RTLD_GLOBAL, matching the production native payload loader.</summary>
    public const int AndroidPayloadOpenFlags = 0x00002 | 0x00100;

    /// <summary>Opens a module through Android's public native loader with explicit production flags.</summary>
    [DllImport("libdl.so", EntryPoint = "dlopen", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr AndroidDlopen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    /// <summary>Resolves an export from a raw Android dlopen handle without Mono handle translation.</summary>
    [DllImport("libdl.so", EntryPoint = "dlsym", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr AndroidDlsym(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string symbol);

    /// <summary>Reads and clears this thread's Android loader error.</summary>
    [DllImport("libdl.so", EntryPoint = "dlerror", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr AndroidDlerror();

    /// <summary>Reads one auxiliary-vector entry of this process.</summary>
    [DllImport("libc.so", EntryPoint = "getauxval", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong AndroidGetauxval(ulong type);

    /// <summary>Whether this phone's CPU has ARMv8.1 LSE atomics (AT_HWCAP bit HWCAP_ATOMICS).</summary>
    /// <remarks>The module is compiled on the phone that runs it, so it can target that CPU, as the
    /// desktop installer targets its own with -march=native. Every ARMv8.2 phone core has LSE.</remarks>
    public static bool HasLseAtomics() => Refii.Core.Engine_Platform.IsAndroid &&
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 && (AndroidGetauxval(16) & (1UL << 8)) != 0;

    /// <summary>Uses the same native loader contract as guest boot, retaining ownership at the caller.</summary>
    /// <param name="module">Hash-validated absolute APK runtime or private payload path.</param>
    /// <returns>The platform loader handle; its lifetime is the engine process.</returns>
    private static IntPtr OpenPlatformModule(string module)
    {
        if (!Refii.Core.Engine_Platform.IsAndroid) return NativeLibrary.Load(module);
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("Phone compilation requires Android ARM64");
        IntPtr handle = AndroidDlopen(module, AndroidPayloadOpenFlags);
        if (handle == IntPtr.Zero)
            throw new DllNotFoundException(Marshal.PtrToStringUTF8(AndroidDlerror()) ?? "Android dlopen failed");
        return handle;
    }

    /// <summary>Resolves the metadata accessor using the handle format returned by the selected loader.</summary>
    /// <param name="handle">Opaque platform handle retained by the lifetime table.</param>
    /// <param name="symbol">Fixed versioned payload ABI export.</param>
    /// <returns>The native entry point, or an exception containing the loader diagnostic.</returns>
    private static IntPtr PlatformExport(IntPtr handle, string symbol)
    {
        if (!Refii.Core.Engine_Platform.IsAndroid) return NativeLibrary.GetExport(handle, symbol);
        AndroidDlerror();
        IntPtr entry = AndroidDlsym(handle, symbol);
        if (entry == IntPtr.Zero)
            throw new EntryPointNotFoundException(Marshal.PtrToStringUTF8(AndroidDlerror()) ?? "Android dlsym failed");
        return entry;
    }

    /// <summary>Pins verified native modules while the owning engine process remains alive.</summary>
    /// <remarks>Verification transfers each loader reference to this table so native
    /// descriptors and registered callbacks remain valid for the engine process.
    /// A small explicit limit bounds retained mappings; process exit performs cleanup.</remarks>
    public sealed class ModuleLifetime
    {
        private readonly Func<string, IntPtr> _load;
        private readonly Func<string, IntPtr, bool> _check;
        private readonly int _limit;
        private readonly object _gate = new();
        private readonly Dictionary<string, (string Hash, IntPtr Handle, bool Verified)> _modules = new();

        /// <summary>Creates an engine-lifetime binding table with injectable native operations.</summary>
        /// <param name="load">Loads one module and transfers its reference to this table.</param>
        /// <param name="check">Checks the metadata accessor without starting guest execution.</param>
        /// <param name="limit">Maximum distinct mappings before the launcher must restart.</param>
        public ModuleLifetime(Func<string, IntPtr> load, Func<string, IntPtr, bool> check, int limit = 4)
        {
            if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
            _load = load; _check = check; _limit = limit;
        }

        /// <summary>Checks a stable module once, retaining even failed loaded handles for safe engine lifetime.</summary>
        /// <param name="module">Canonical owned module path.</param>
        /// <param name="digest">Hash of those exact bytes; replacing a mapped file is rejected.</param>
        /// <returns>Whether the native metadata check succeeds.</returns>
        public bool Verify(string module, string digest)
        {
            lock (_gate)
            {
                if (_modules.TryGetValue(module, out var existing))
                {
                    if (existing.Hash != digest) throw new InvalidDataException("A loaded module's bytes changed; restart the launcher");
                    if (existing.Verified) return true;
                }
                else
                {
                    if (_modules.Count >= _limit) throw new InvalidDataException("Restart the launcher before verifying another build");
                    IntPtr handle = _load(module);
                    if (handle == IntPtr.Zero) throw new InvalidDataException("Native loader returned an empty handle");
                    existing = (digest, handle, false);
                    _modules.Add(module, existing);
                }
                bool verified = _check(module, existing.Handle);
                _modules[module] = (digest, existing.Handle, verified);
                return verified;
            }
        }
    }

    private static readonly ModuleLifetime LoadedRuntime = new(OpenPlatformModule, (_, _) => true, 1);

    private static readonly ModuleLifetime LoadedModules = new(module =>
    {
        LoadEvent(module, "load_begin");
        IntPtr handle = OpenPlatformModule(module);
        LoadEvent(module, "load_returned");
        return handle;
    }, (module, handle) =>
    {
        LoadEvent(module, "accessor_lookup_begin");
        IntPtr entry = PlatformExport(handle, "refii_payload_v1_image_config");
        LoadEvent(module, "accessor_lookup_returned");
        IntPtr descriptor = Marshal.GetDelegateForFunctionPointer<ImageConfig>(entry)();
        LoadEvent(module, descriptor != IntPtr.Zero ? "accessor_nonnull" : "accessor_null");
        return descriptor != IntPtr.Zero;
    });

    /// <summary>Public bundle identities and source recipe; game-derived files are absent.</summary>
    public sealed class BundleManifest
    {
        /// <summary>Supported manifest format.</summary>
        public int Schema { get; set; }
        /// <summary>Target architecture.</summary>
        public string Abi { get; set; } = "";
        /// <summary>Native API floor, matching the runtime.</summary>
        public int Api { get; set; }
        /// <summary>Pinned NDK revision used for tools and sysroot.</summary>
        public string Ndk { get; set; } = "";
        /// <summary>The ReXGlue SDK revision the prebuilt phone tools were built from. The bundle
        /// carries it forward from the bundle it reuses; it is not the checkout's SDK submodule
        /// and is not checked against it.</summary>
        public string SdkPin { get; set; } = "";
        /// <summary>Ordered public include directories, with bridge headers first.</summary>
        public string[] Includes { get; set; } = Array.Empty<string>();
        /// <summary>Public hook source paths, relative to the bundle.</summary>
        public string[] Hooks { get; set; } = Array.Empty<string>();
        /// <summary>Hashes of all extracted public build inputs.</summary>
        public Dictionary<string, string> Files { get; set; } = new();
        /// <summary>Hashes of the matched APK runtime closure and executable tools.</summary>
        public Dictionary<string, string> NativeFiles { get; set; } = new();
    }

    /// <summary>Validates bundle pins and hashes before invoking any compiler or generator.</summary>
    /// <param name="request">Physical, separate input/output roots.</param>
    /// <returns>The complete matched public build recipe.</returns>
    public static BundleManifest Validate(Request request)
    {
        foreach (string root in new[] { request.BundleDirectory, request.NativeDirectory,
                     request.GameDirectory, request.PrivateDirectory })
            AndroidRuntimeOptions.PhysicalPath(root, "phone compilation root");
        if (Overlaps(request.PrivateDirectory, request.GameDirectory) ||
            Overlaps(request.PrivateDirectory, request.BundleDirectory) ||
            Overlaps(request.PrivateDirectory, request.NativeDirectory))
            throw new InvalidDataException("Compilation output must be separate from its inputs");
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(request.BundleDirectory, "bundle.json")), AndroidJsonContext.Default.BundleManifest)
            ?? throw new InvalidDataException("Missing compiler bundle manifest");
        if (manifest.Schema != 1 || manifest.Abi != "arm64-v8a" || manifest.Api != 31 ||
            manifest.Ndk != "28.2.13676358" ||
            manifest.SdkPin != "bc812c08ab2efd4ed8d7f306bd1911c703771732")
            throw new InvalidDataException("Compiler bundle does not match the pinned ARM64/API31 runtime");
        if (manifest.Files.Count == 0 || manifest.Hooks.Length == 0 || manifest.Includes.Length == 0)
            throw new InvalidDataException("Incomplete compiler bundle recipe");
        foreach (var pair in manifest.Files) Verify(request.BundleDirectory, pair.Key, pair.Value);
        foreach (var pair in manifest.NativeFiles) Verify(request.NativeDirectory, pair.Key, pair.Value);
        foreach (string name in new[] { "librefii_clang.so", "librefii_lld.so", "librefii_codegen.so",
                     "librefii_core.so", "librefii_hostbridge.so", "librexruntime.so", "librefii_lua.so", "librefii_vfs.so", "libc++_shared.so" })
            if (!manifest.NativeFiles.ContainsKey(name)) throw new InvalidDataException("Unverified tool/runtime: " + name);
        foreach (string path in manifest.Includes)
            if (!System.IO.Directory.Exists(Below(request.BundleDirectory, path)))
                throw new InvalidDataException("Missing include directory: " + path);
        foreach (string path in manifest.Hooks)
            if (!manifest.Files.ContainsKey(path) || !path.EndsWith(".cpp", StringComparison.Ordinal))
                throw new InvalidDataException("Unverified hook source: " + path);
        foreach (string path in new[] { "src/payload/src/payload_export.cpp", "tools/android/payload_abi_guard.h",
                     "versions/Fable2/refii_manifest.toml", "versions/Fable2TU1/refii_manifest.toml" })
            if (!manifest.Files.ContainsKey(path)) throw new InvalidDataException("Missing build input: " + path);
        return manifest;
    }

    /// <summary>Generates, precompiles, compiles and links a new module without changing the active install.</summary>
    /// <param name="request">Explicit private inputs and public compiler roots.</param>
    /// <param name="progress">Receives phase descriptions and completed translation unit counts.</param>
    /// <param name="cancellation">Terminates owned work; partial output is retained.</param>
    /// <param name="execute">Optional synthetic runner for unit/feature tests.</param>
    /// <param name="verifyLoad">Optional synthetic loader check for failure and publication lifecycle tests.</param>
    /// <returns>The completed, structurally verified module and provenance hash.</returns>
    public static async Task<Result> CompileAsync(Request request, Action<string> progress,
        CancellationToken cancellation, Execute? execute = null, Func<string, bool>? verifyLoad = null)
    {
        cancellation.ThrowIfCancellationRequested();
        BundleManifest bundle = Validate(request);
        string source = Path.Combine(request.GameDirectory, "default.xex");
        if (!File.Exists(source)) throw new FileNotFoundException("Select game files containing default.xex", source);
        string attempt = Path.Combine(request.PrivateDirectory, "phone-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attempt);
        File.WriteAllText(Path.Combine(attempt, ".refii-build-owner"), Path.GetFileName(attempt));
        string input = Path.Combine(attempt, "input");
        Directory.CreateDirectory(input);
        foreach (string name in new[] { "default.xex", "default.xexp" })
            if (File.Exists(Path.Combine(request.GameDirectory, name)))
                File.Copy(Path.Combine(request.GameDirectory, name), Path.Combine(input, name), false);
        string version = File.Exists(Path.Combine(input, "default.xexp")) ? "Fable2TU1" : "Fable2";
        string generated = Path.Combine(attempt, "generated");
        string config = Below(request.BundleDirectory, "versions/" + version);
        File.Copy(Path.Combine(config, "refii_config.toml"), Path.Combine(attempt, "refii_config.toml"), false);
        string manifest = File.ReadAllText(Path.Combine(config, "refii_manifest.toml"));
        manifest = RewriteAssignment(manifest, "file_path", Path.Combine(input, "default.xex"));
        manifest = RewriteAssignment(manifest, "out_directory_path", generated);
        string manifestPath = Path.Combine(attempt, "refii_manifest.toml");
        File.WriteAllText(manifestPath, manifest);
        Execute runner = execute ?? RunChildAsync;
        int commandIndex = 0;
        async Task Run(string tool, IReadOnlyList<string> args)
        {
            cancellation.ThrowIfCancellationRequested();
            var command = new Command(Below(request.NativeDirectory, tool), args, attempt,
                request.NativeDirectory, Path.Combine(attempt, "command-" + commandIndex++ + ".log"));
            int status = await runner(command, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (status != 0) throw new InvalidDataException($"{tool} exited {status}; diagnostic files: {attempt}");
        }
        progress("Generating game code on this phone");
        await Run("librefii_codegen.so", new[] { "--force", "codegen", manifestPath }).ConfigureAwait(false);
        string[] units = ReadSources(Path.Combine(generated, "sources.cmake"), generated);
        string header = Path.Combine(generated, "refii_pch.h");
        string generatedHeader = File.ReadAllText(header);
        string lockDepth = ThreadLocalLockDepth(generatedHeader);
        if (lockDepth != generatedHeader)
        {
            File.Copy(header, header + ".process-lock", false);
            File.WriteAllText(header, lockDepth);
        }
        foreach (string unit in units.Where(path => Path.GetFileName(path) == "refii_register.cpp"))
        {
            string original = File.ReadAllText(unit);
            string bounded = BoundRegistration(original);
            if (bounded != original)
            {
                // Retain the generator's exact output beside the rewritten private attempt file.
                File.Copy(unit, unit + ".unsplit", false);
                File.WriteAllText(unit, bounded);
            }
        }
        var flags = CompilerFlags(request, bundle, generated, version, HasLseAtomics()).ToList();
        string pch = Path.Combine(attempt, "refii.pch");
        progress("Precompiling game headers");
        await Run("librefii_clang.so", flags.Concat(new[] { "-x", "c++-header",
            Path.Combine(generated, "refii_pch.h"), "-o", pch }).ToArray()).ConfigureAwait(false);
        var all = units.Concat(bundle.Hooks.Select(path => Below(request.BundleDirectory, path)))
            .Append(Below(request.BundleDirectory, "src/payload/src/payload_export.cpp")).ToArray();
        var objects = new List<string>();
        for (int i = 0; i < all.Length; i++)
        {
            string obj = Path.Combine(attempt, "unit-" + i + ".o");
            progress($"Compiling game code {i + 1}/{all.Length}");
            var args = new List<string>(flags);
            if (i < units.Length) args.AddRange(new[] { "-include-pch", pch });
            args.AddRange(new[] { "-c", all[i], "-o", obj });
            await Run("librefii_clang.so", args).ConfigureAwait(false);
            objects.Add(obj);
        }
        progress("Linking game module");
        string module = Path.Combine(attempt, "librefii_generated.so");
        var link = flags.Concat(new[] { "-shared", "-Wl,--no-undefined", "-Wl,-z,max-page-size=16384",
            "-Wl,-z,common-page-size=16384", ModuleLocalCalls,
            "-Wl,-soname,librefii_generated.so", "-L" + request.NativeDirectory,
            "-Wl,-rpath-link," + request.NativeDirectory }).Concat(objects)
            .Concat(new[] { "-lrefii_core", "-lrefii_lua", "-lrefii_vfs", "-lrexruntime", "-o", module }).ToArray();
        await Run("librefii_clang.so", link).ConfigureAwait(false);
        ValidateModule(File.ReadAllBytes(module));
        bool nativeLoad = Refii.Core.Engine_Platform.IsAndroid && execute == null && verifyLoad == null;
        if (!(verifyLoad?.Invoke(module) ?? (!nativeLoad || VerifyLoadedModule(module, request.NativeDirectory))))
            throw new InvalidDataException("Compiled module failed its loader check; private output retained: " + attempt);
        cancellation.ThrowIfCancellationRequested();
        if (nativeLoad)
            File.WriteAllText(Path.Combine(attempt, "module-load-proof.json"), Refii.Core.Engine_Json.Serialize(new {
                phone_executed = true, native_load_verified = true,
                entry_symbol = "refii_payload_v1_image_config", descriptor_nonnull = true,
                module_retained_for_engine_lifetime = true,
                loader = "android_dlopen_rtld_now_global_runtime_first",
                scope = "Loaded the complete module and called its metadata accessor; no guest boot or gameplay" }));
        string digest = Hash(module);
        var result = new Result(attempt, module, digest, version) { Bundle = RecordedBundle(request.BundleDirectory) };
        File.WriteAllText(Path.Combine(attempt, "completed.json"), Refii.Core.Engine_Json.Serialize(result));
        progress("Game code compiled on this phone");
        return result;
    }

    /// <summary>Loads the actual completed Android module and checks its versioned accessor before publication.</summary>
    /// <param name="module">Fresh private module with previously validated ELF layout and matched runtime headers.</param>
    /// <param name="nativeDirectory">Matched APK runtime closure, loaded before the payload as in native guest boot.</param>
    /// <returns>Whether the payload descriptor accessor returns a non-null pointer.</returns>
    /// <remarks>No descriptor fields are marshalled, registration function is called, guest is started,
    /// configuration is changed or save is read. Verified handles remain pinned for engine lifetime.</remarks>
    private static bool VerifyLoadedModule(string module, string nativeDirectory)
    {
        // Runtime-first lookup preserves the SDK's global flag/function owners;
        // loading the payload as the root closure can interpose its bridge wrappers.
        LoadEvent(module, "runtime_preload_begin");
        string runtime = Below(nativeDirectory, "librefii_hostbridge.so");
        LoadedRuntime.Verify(runtime, Hash(runtime));
        LoadEvent(module, "runtime_preload_returned");
        bool verified = LoadedModules.Verify(module, Hash(module));
        LoadEvent(module, "module_retained_for_engine_lifetime");
        return verified;
    }

    /// <summary>Flushes each native stage before invoking code that could terminate the process.</summary>
    private static void LoadEvent(string module, string stage) => File.AppendAllText(
        Path.Combine(Path.GetDirectoryName(module)!, "module-load-events.log"), stage + "\n");

    /// <summary>Diagnoses a hash-approved previously phone-compiled module without repeating compilation.</summary>
    /// <param name="request">Verified matched APK tools/runtime and owned private output root.</param>
    /// <param name="sourceModule">Previously compiled module beneath that private root.</param>
    /// <param name="expectedHash">Exact externally audited source module hash.</param>
    /// <param name="version">Known executable layout of the original phone compilation.</param>
    /// <param name="progress">Status for the explicit diagnostic action.</param>
    /// <param name="cancellation">Stops before native verification or publication.</param>
    /// <param name="verifyLoad">Synthetic metadata checker for lifecycle tests.</param>
    /// <returns>A fresh verified copy; no source module, configuration or save is changed.</returns>
    public static Task<Result> VerifyExistingAsync(Request request, string sourceModule, string expectedHash,
        string version, Action<string> progress, CancellationToken cancellation, Func<string, bool>? verifyLoad = null)
    {
        cancellation.ThrowIfCancellationRequested();
        Validate(request);
        if (version is not ("Fable2" or "Fable2TU1")) throw new InvalidDataException("Unknown guest layout");
        string relative = Path.GetRelativePath(request.PrivateDirectory, sourceModule).Replace('\\', '/');
        sourceModule = Below(request.PrivateDirectory, relative);
        if (Path.GetFileName(sourceModule) != "librefii_generated.so" || !Regex.IsMatch(expectedHash, "^[0-9a-f]{64}$") ||
            Hash(sourceModule) != expectedHash) throw new InvalidDataException("Diagnostic module bytes do not match the approved hash");
        ValidateModule(File.ReadAllBytes(sourceModule));
        string attempt = Path.Combine(request.PrivateDirectory, "phone-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attempt);
        string module = Path.Combine(attempt, "librefii_generated.so");
        File.Copy(sourceModule, module, false);
        if (Hash(module) != expectedHash) throw new InvalidDataException("Diagnostic module copy changed bytes");
        File.WriteAllText(Path.Combine(attempt, "compilation-origin.json"), Refii.Core.Engine_Json.Serialize(new {
            kind = "verification_only", source_module = sourceModule, source_sha256 = expectedHash,
            scope = "Copied a previously phone-compiled module; no generator, compiler or linker invoked in this check" }));
        progress("Verifying previously phone-compiled game module");
        cancellation.ThrowIfCancellationRequested();
        bool native = Refii.Core.Engine_Platform.IsAndroid && verifyLoad == null;
        if (!(verifyLoad?.Invoke(module) ?? (!native || VerifyLoadedModule(module, request.NativeDirectory))))
            throw new InvalidDataException("Previously compiled module failed its metadata check");
        cancellation.ThrowIfCancellationRequested();
        if (native) File.WriteAllText(Path.Combine(attempt, "module-load-proof.json"), Refii.Core.Engine_Json.Serialize(new {
            phone_executed = true, native_load_verified = true, descriptor_nonnull = true,
            entry_symbol = "refii_payload_v1_image_config", module_retained_for_engine_lifetime = true,
            loader = "android_dlopen_rtld_now_global_runtime_first",
            scope = "Loaded a copy of the previously phone-compiled module and called its metadata accessor; no guest boot or gameplay" }));
        var result = new Result(attempt, module, expectedHash, version) { Bundle = RecordedBundle(request.BundleDirectory) };
        File.WriteAllText(Path.Combine(attempt, "completed.json"), Refii.Core.Engine_Json.Serialize(result));
        return Task.FromResult(result);
    }

    /// <summary>Binds the module's calls to its own functions at link time instead of through the PLT.</summary>
    /// <remarks>Every generated function is exported, so each guest-to-guest call was a PLT stub and a
    /// GOT load (34,729 slots), about 3% of guest CPU. Symbols stay exported, so name lookups and the
    /// one entry the host resolves are unchanged; only data symbols keep interposition. This also keeps
    /// rex_bridge_payload.cpp's forwarders module-internal, as -fvisibility=hidden does on desktop.</remarks>
    internal const string ModuleLocalCalls = "-Wl,-Bsymbolic-functions";

    /// <summary>Bounds optimizer work for generated registration without changing calls or their order.</summary>
    /// <param name="source">Fresh generator output containing only the known registration function.</param>
    /// <returns>Equivalent ordered noinline helpers, or the unchanged small function.</returns>
    /// <remarks>Large games contain tens of thousands of virtual calls in this one function.
    /// Helpers retain full optimization while preventing inlining from rebuilding that enormous graph.
    /// Unknown generated layouts fail closed; this never rewrites guest arithmetic or hook code.</remarks>
    public static string BoundRegistration(string source)
    {
        var match = Regex.Match(source,
            @"\A(?<head>.*?)void refii_RegisterFunctions\(rex::runtime::IModuleRegistrar\* registrar\)\s*\{(?<body>.*?)\}\s*\z",
            RegexOptions.Singleline);
        if (!match.Success) throw new InvalidDataException("Unexpected generated registration function");
        string[] calls = match.Groups["body"].Value.Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length != 0).ToArray();
        foreach (string call in calls)
            if (!Regex.IsMatch(call, @"\Aregistrar->SetFunction\(0x[0-9a-fA-F]{8},\s*[A-Za-z_][A-Za-z0-9_]*\);\z"))
                throw new InvalidDataException("Unexpected generated registration statement");
        const int perPart = 512;
        if (calls.Length <= perPart) return source;
        var text = new StringBuilder(match.Groups["head"].Value);
        int parts = (calls.Length + perPart - 1) / perPart;
        for (int part = 0; part < parts; part++)
        {
            text.Append("static __attribute__((noinline)) void refii_RegisterFunctions_PhonePart_")
                .Append(part).Append("(rex::runtime::IModuleRegistrar* registrar) {\n");
            for (int index = part * perPart; index < Math.Min(calls.Length, (part + 1) * perPart); index++)
                text.Append("  ").Append(calls[index]).Append('\n');
            text.Append("}\n\n");
        }
        text.Append("void refii_RegisterFunctions(rex::runtime::IModuleRegistrar* registrar) {\n");
        for (int part = 0; part < parts; part++)
            text.Append("  refii_RegisterFunctions_PhonePart_").Append(part).Append("(registrar);\n");
        return text.Append("}\n").ToString();
    }

    /// <summary>The guest's floating-point model: IEEE round-to-nearest without contraction.</summary>
    /// <remarks>This is the x86_64 desktop build's model with contraction pinned off, because simde's
    /// portable fallbacks would otherwise fuse vmaddfp's separate multiply and add on ARM only.
    /// -ffp-model=strict cost about 3% of guest CPU: every compare and conversion stayed a constrained
    /// operation, so CR compares never merged and exact float/double round trips stayed (686/581
    /// conversions in a sampled unit, 488/210 now, 450/200 on x86_64). Guest results are unchanged in
    /// round-to-nearest. FPCR writes are asm with a memory clobber and guest registers live in ctx, so
    /// flush-mode and rounding changes still order against guest FP work. Nothing reads FP exception flags.</remarks>
    internal static readonly string[] GuestFloatingPoint = { "-ffp-model=precise", "-ffp-contract=off" };

    /// <summary>Uses the same payload ABI guard, floating-point model and ARM64 target as the PC Android build.</summary>
    /// <param name="request">Explicit tool and public input roots.</param>
    /// <param name="bundle">Validated ordered include recipe.</param>
    /// <param name="generated">Private generated source directory.</param>
    /// <param name="version">Patched or unpatched guest layout.</param>
    /// <param name="lseAtomics">Target ARMv8.2 for a CPU with LSE atomics; otherwise the ARMv8.0 baseline.</param>
    /// <returns>Shell-free Clang arguments for all payload translation units.</returns>
    /// <remarks>ARMv8.2 turns the guest's ~8,500 compare-and-swap sites (stwcx.) from calls to
    /// __aarch64_cas4_acq_rel into inline CASAL. The S10e's Cortex-A76/A55 are ARMv8.2 too.</remarks>
    public static IReadOnlyList<string> CompilerFlags(Request request, BundleManifest bundle,
        string generated, string version, bool lseAtomics = false)
    {
        var flags = new List<string> { "--driver-mode=g++", "--target=aarch64-linux-android31",
            "--sysroot=" + Below(request.BundleDirectory, "sysroot"),
            "-resource-dir=" + Below(request.BundleDirectory, "resource"),
            "--ld-path=" + Below(request.NativeDirectory, "librefii_lld.so"),
            "-fintegrated-cc1", "-std=gnu++23", "-O2", "-fPIC", "-DNDEBUG", "-DANDROID",
            "-DREX_PLATFORM_ANDROID=1", "-DREX_HAS_VULKAN=1", "-DSPDLOG_COMPILED_LIB", "-DSPDLOG_FMT_EXTERNAL",
            "-DREFII_PAYLOAD_LOG_BRIDGE=1", "-fno-strict-aliasing", "-fno-char8_t",
            "-include", Below(request.BundleDirectory, "src/payload/src/libcxx_chrono_shim.h"),
            "-include", Below(request.BundleDirectory, "tools/android/payload_abi_guard.h"), "-I" + generated };
        flags.AddRange(GuestFloatingPoint);
        foreach (string macro in new[] { "TRACY_ENABLE", "TRACY_MANUAL_LIFETIME", "TRACY_DELAYED_INIT",
                     "TRACY_ON_DEMAND", "TRACY_FIBERS", "TRACY_IMPORTS", "REXGLUE_ENABLE_PROFILING" })
            flags.Add("-U" + macro);
        if (version == "Fable2") flags.Add("-DREFII_GUEST_BUILD_UNPATCHED=1");
        if (lseAtomics) flags.Add("-march=armv8.2-a");
        foreach (string path in bundle.Includes) flags.Add("-I" + Below(request.BundleDirectory, path));
        return flags;
    }

    /// <summary>Accepts only literal generated source paths contained in the private generation directory.</summary>
    /// <param name="list">Generator's sources.cmake file.</param>
    /// <param name="root">Private generation root.</param>
    /// <returns>Validated unique C++ translation units, without executing CMake.</returns>
    public static string[] ReadSources(string list, string root)
    {
        var block = Regex.Match(File.ReadAllText(list), @"set\(GENERATED_SOURCES\s+([^)]*)\)", RegexOptions.Singleline);
        if (!block.Success) throw new InvalidDataException("Generator produced no source list");
        string[] sources = block.Groups[1].Value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Trim('"').Replace("${CMAKE_CURRENT_LIST_DIR}", root, StringComparison.Ordinal))
            .Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
        if (sources.Length == 0 || sources.Distinct(StringComparer.Ordinal).Count() != sources.Length)
            throw new InvalidDataException("Missing or duplicate generated sources");
        foreach (string source in sources)
            if (!source.EndsWith(".cpp", StringComparison.Ordinal) || !File.Exists(source) ||
                !Path.GetFullPath(source).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Generated source escapes its private root: " + source);
        return sources.Select(Path.GetFullPath).ToArray();
    }

    /// <summary>Rejects host modules, malformed ELF tables and unsafe 16KiB LOAD layouts before publication.</summary>
    /// <param name="data">Completed private module bytes.</param>
    public static void ValidateModule(byte[] data)
    {
        if (data.Length < 64 || data[0] != 0x7f || data[1] != 'E' || data[2] != 'L' || data[3] != 'F' ||
            data[4] != 2 || data[5] != 1 || BitConverter.ToUInt16(data, 16) != 3 || BitConverter.ToUInt16(data, 18) != 183)
            throw new InvalidDataException("Expected a little-endian ARM64 shared ELF module");
        ulong offset = BitConverter.ToUInt64(data, 32);
        ushort size = BitConverter.ToUInt16(data, 54), count = BitConverter.ToUInt16(data, 56);
        if (size != 56 || count == 0 || offset > (ulong)data.Length ||
            (ulong)size * count > (ulong)data.Length - offset)
            throw new InvalidDataException("Malformed module program headers");
        int loads = 0;
        var writable = new List<(ulong Start, ulong End)>();
        var protectedRanges = new List<(ulong Start, ulong End)>();
        for (int i = 0; i < count; i++)
        {
            int at = checked((int)offset + i * size);
            uint type = BitConverter.ToUInt32(data, at);
            if (type is not (1 or 0x6474e552)) continue;
            ulong file = BitConverter.ToUInt64(data, at + 8), address = BitConverter.ToUInt64(data, at + 16),
                length = BitConverter.ToUInt64(data, at + 32), memory = BitConverter.ToUInt64(data, at + 40),
                alignment = BitConverter.ToUInt64(data, at + 48);
            if (memory > ulong.MaxValue - address)
                throw new InvalidDataException("Module segment address overflows");
            if (type == 0x6474e552)
            {
                if (address + memory > ulong.MaxValue - 16383)
                    throw new InvalidDataException("Module RELRO rounding overflows");
                protectedRanges.Add((address, address + memory));
                continue;
            }
            loads++;
            uint permissions = BitConverter.ToUInt32(data, at + 4);
            if (alignment < 16384 || (alignment & (alignment - 1)) != 0 || address % 16384 != file % 16384 ||
                file > (ulong)data.Length || length > (ulong)data.Length - file || length > memory || (permissions & 3) == 3)
                throw new InvalidDataException("Module has invalid or writable executable LOAD segments");
            if ((permissions & 2) != 0) writable.Add((address, address + memory));
        }
        if (loads == 0) throw new InvalidDataException("Module has no LOAD segments");
        // Android rounds RELRO to the device's page size. Protecting adjacent writable
        // data would make an aligned module fail at runtime on a 16KiB device.
        foreach (var range in protectedRanges)
        {
            ulong roundedStart = range.Start / 16384 * 16384;
            ulong roundedEnd = (range.End + 16383) / 16384 * 16384;
            foreach (var segment in writable)
            {
                ulong start = Math.Max(roundedStart, segment.Start), end = Math.Min(roundedEnd, segment.End);
                if (start < end && (start < range.Start || end > range.End))
                    throw new InvalidDataException("Module RELRO overlaps writable data after 16KiB rounding");
            }
        }
    }

    /// <summary>The interrupt-lock block generators before the thread-local depth emitted into refii_pch.h.</summary>
    internal const string ProcessWideGuestLock = """
        inline std::atomic<i32>& ppc_global_lock_count_() {
          static std::atomic<i32> count{0};
          return count;
        }

        #define REX_CHECK_GLOBAL_LOCK()                                        \
          ([&]() -> u64 {                                                      \
            auto lock_ = rex::thread::global_critical_region::AcquireDirect(); \
            return ppc_global_lock_count_().load() ? 0 : 0x8000;               \
          }())

        #define REX_ENTER_GLOBAL_LOCK()                          \
          do {                                                   \
            rex::thread::global_critical_region::mutex().lock(); \
            ppc_global_lock_count_().fetch_add(1);               \
          } while (0)

        #define REX_LEAVE_GLOBAL_LOCK()                                                           \
          do {                                                                                    \
            auto old_count_ = ppc_global_lock_count_().fetch_sub(1);                              \
            assert(old_count_ >= 1 && "LeaveGlobalLock called without matching EnterGlobalLock"); \
            rex::thread::global_critical_region::mutex().unlock();                                \
          } while (0)

        """;

    /// <summary>The same block as the SDK shadow's pch_h.inja now emits it.</summary>
    internal const string ThreadLocalGuestLock = """
        // The guest's own interrupt-disable depth on THIS thread. mfmsr only asks whether the
        // calling thread is inside such a section, and only the thread holding the global critical
        // region can be, so its own depth answers without taking the lock. A process-wide count read
        // under the lock gave the same answer for a mutex round trip per guest interlocked operation.
        inline i32& ppc_global_lock_depth_() {
          static thread_local i32 depth = 0;
          return depth;
        }

        #define REX_CHECK_GLOBAL_LOCK() (ppc_global_lock_depth_() ? u64(0) : u64(0x8000))

        #define REX_ENTER_GLOBAL_LOCK()                          \
          do {                                                   \
            rex::thread::global_critical_region::mutex().lock(); \
            ++ppc_global_lock_depth_();                          \
          } while (0)

        #define REX_LEAVE_GLOBAL_LOCK()                                                    \
          do {                                                                             \
            assert(ppc_global_lock_depth_() >= 1 &&                                        \
                   "LeaveGlobalLock called without matching EnterGlobalLock");             \
            --ppc_global_lock_depth_();                                                    \
            rex::thread::global_critical_region::mutex().unlock();                         \
          } while (0)

        """;

    /// <summary>Gives an older generator's header the thread-local interrupt-lock depth.</summary>
    /// <param name="header">Fresh generator output for refii_pch.h.</param>
    /// <returns>The header with the process-wide block replaced, or unchanged when it is absent.</returns>
    /// <remarks>The phone's generator predates the template change. Its mfmsr took the global
    /// critical region (two runtime calls and a recursive mutex) for every guest interlocked
    /// operation, about 5% of guest CPU at the 30 fps cap. A header without the exact old block
    /// (a newer generator, or one already rewritten) keeps the generator's own correct code.</remarks>
    public static string ThreadLocalLockDepth(string header)
    {
        string old = ProcessWideGuestLock.ReplaceLineEndings("\n");
        int at = header.IndexOf(old, StringComparison.Ordinal);
        if (at < 0 || header.IndexOf(old, at + 1, StringComparison.Ordinal) >= 0) return header;
        return header.Remove(at, old.Length).Insert(at, ThreadLocalGuestLock.ReplaceLineEndings("\n"));
    }

    /// <summary>Runs one tool with bounded lifetime, real exit status and cancellation cleanup.</summary>
    /// <param name="command">APK-native executable and private working/log paths.</param>
    /// <param name="cancellation">Cancellation token for the attempt.</param>
    /// <returns>Actual process exit code.</returns>
    public static async Task<int> RunChildAsync(Command command, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var start = new ProcessStartInfo(command.Executable) { WorkingDirectory = command.Directory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in command.Arguments) start.ArgumentList.Add(argument);
        start.Environment["LD_LIBRARY_PATH"] = command.NativeDirectory;
        start.Environment["TMPDIR"] = command.Directory;
        using var child = Process.Start(start) ?? throw new IOException("Cannot start bundled compiler");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return child.ExitCode;
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("Bundled compiler exceeded its 15-minute command limit; diagnostics: " + command.Log, error);
        }
        finally
        {
            if (!child.HasExited)
            {
                try { child.Kill(entireProcessTree: true); }
                catch (PlatformNotSupportedException) { child.Kill(); }
                await child.WaitForExitAsync().ConfigureAwait(false);
            }
            File.WriteAllText(command.Log, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false) +
                "\ntool_exit=" + child.ExitCode + "; interrupted=" + timeout.IsCancellationRequested + "\n");
        }
    }

    /// <summary>Replaces exactly one bundled TOML path using TOML-compatible escaped strings.</summary>
    private static string RewriteAssignment(string text, string name, string value)
    {
        string pattern = @"(?m)^" + Regex.Escape(name) + @"\s*=.*$";
        if (Regex.Matches(text, pattern).Count != 1) throw new InvalidDataException("Ambiguous manifest field " + name);
        return Regex.Replace(text, pattern, _ => name + " = " + Refii.Core.Engine_Json.Serialize(value.Replace('\\', '/')));
    }

    /// <summary>Rejects unsafe bundle paths, including symbolic links in any component.</summary>
    private static string Below(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\') ||
            relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Invalid bundle path: " + relative);
        string path = Path.GetFullPath(root);
        foreach (string part in relative.Split('/'))
        {
            path = Path.Combine(path, part);
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Compiler inputs may not traverse symbolic links");
        }
        return path;
    }

    /// <summary>Verifies one ordinary file's recorded bytes before compilation.</summary>
    private static void Verify(string root, string path, string expected)
    {
        if (!Regex.IsMatch(expected, "^[0-9a-f]{64}$") || Hash(Below(root, path)) != expected)
            throw new InvalidDataException("Compiler input hash mismatch: " + path);
    }

    /// <summary>Hashes a file with bounded memory.</summary>
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(stream)).ToLowerInvariant();
    }

    /// <summary>Rejects nested input/output roots using the host filesystem's case rules.</summary>
    private static bool Overlaps(string a, string b)
    {
        string left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        string right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return left.Equals(right, comparison) || left.StartsWith(right + Path.DirectorySeparatorChar, comparison) ||
            right.StartsWith(left + Path.DirectorySeparatorChar, comparison);
    }
}
