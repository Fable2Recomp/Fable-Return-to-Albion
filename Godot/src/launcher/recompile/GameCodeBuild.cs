using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Refii.Launcher.Core;

namespace Refii.Launcher.Recompile;

/// <summary>
/// Turns the player's own game executable into the native module the runtime loads.
/// </summary>
/// <remarks>
/// AN INSTALL STEP, and the one that cannot be skipped: the recompiled guest is derived
/// from the player's game image, so it is not redistributable and no release can contain
/// it. What we ship is the generator, the runtime, our own libraries and a bundled Zig
/// under <c>recompile/</c>; this drives them.
///
/// Five phases, in order:
///   1. generate  — rexglue reads assets/game/default.xex and writes C++ to assets/generated
///   2. precompile — the ~61,000-line generated header, once instead of ~148 times
///   3. compile   — ~150k functions, JobCount() translation units at a time
///   4. link      — one shared module, next to the other native libraries
///   5. clean     — the generated sources and the object tree are deleted
///
/// NO BUILD SYSTEM. Phase 5 deletes the sources after every successful build, so the
/// payload is never rebuilt incrementally and there is no dependency graph worth keeping —
/// cmake and ninja were maintaining one for a build that is always clean. Driving the
/// compiler directly also means the progress a player sees is the real count of finished
/// translation units rather than a parsed line of someone else's output.
///
/// Phase 5 is why nothing this writes may live under assets/generated: the sources and
/// their objects are several gigabytes and are worthless once the module exists.
/// </remarks>
public static class GameCodeBuild
{
	/// <summary>
	/// Where the native libraries live.
	/// </summary>
	/// <remarks>
	/// The build has TWO roots and they are not interchangeable. This one is where the
	/// shipped native closure sits, and the only thing that belongs here is the module
	/// itself plus what compiles it — <c>refii_payload</c> resolves the payload beside the
	/// library that loaded it, so a module written anywhere else is a module the runtime
	/// cannot find.
	///
	/// Everything derived from the player's game — the executable, the generated sources —
	/// lives with the INSTALL instead, and is resolved through <see cref="Installer"/>.
	/// </remarks>
	public static string BinDirectory { get; set; } = string.Empty;

	public static string RecompileDirectory => Path.Combine(BinDirectory, "recompile");

	/// <summary>The generator's output. Deleted once the module is built.</summary>
	public static string GeneratedDirectory => Installer.GetGeneratedCodeDirectoryPath();

	private static string BuildDirectory => Path.Combine(GeneratedDirectory, "build");

	/// <summary>The executable the installer wrote, not a path of our own.</summary>
	public static string GameExecutablePath => Installer.GetGameExecutablePath();

	public static string ModulePath =>
		Path.Combine(BinDirectory, Refii.Core.Engine_NativeLibraries.FileNameFor("refii_generated"));

	public static string BuildInfoPath => Path.Combine(BinDirectory, "build-info.json");

	private const string SettingsSection = "Recompile";
	private const string UseAllCoresKey = "BuildUseAllCores";

	/// <summary>
	/// Whether to compile with every core rather than half of them.
	/// </summary>
	/// <remarks>
	/// DEFAULTS OFF. Compiling ~150,000 functions saturates every core for the length of
	/// the build, and an install that makes the machine unusable while it runs is a worse
	/// default than one that takes longer — the player did not ask to stop using their PC.
	/// Turning it on roughly halves the wait and costs them the machine while it does.
	/// </remarks>
	public static bool UseAllCores
	{
		get => Ini.GetBool(UseAllCoresKey, false, SettingsSection);
		set
		{
			Ini.SetBool(UseAllCoresKey, value, SettingsSection);
			Ini.Save();
		}
	}

	/// <summary>
	/// Compile jobs to run at once. At least one, however few cores are reported.
	/// </summary>
	public static int JobCount()
	{
		int cores = Environment.ProcessorCount;
		if (UseAllCores)
		{
			return Math.Max(1, cores);
		}

		return Math.Max(1, cores / 2);
	}

	public readonly record struct Progress(string Status, float Fraction);

	public readonly record struct Result(bool Ok, string Error);

	/// <summary>True when a module exists at all.</summary>
	public static bool IsBuilt() => File.Exists(ModulePath);

	private const long StatusTtlMs = 2000;
	private static bool _cachedNeedsBuild = true;
	private static string _cachedReason = string.Empty;
	private static bool _cacheValid;
	private static long _cacheStamp;

	public static void InvalidateStatus() => _cacheValid = false;

	/// <summary>
	/// Cached <see cref="NeedsBuild"/>, for the UI.
	/// </summary>
	/// <remarks>
	/// The uncached check hashes a 21 MB executable, and the panel that shows this
	/// refreshes continuously — reading and hashing that file on every frame would be a
	/// self-inflicted stall on a question whose answer changes only when an install runs.
	/// </remarks>
	public static bool NeedsBuildCached(out string reason)
	{
		long now = Environment.TickCount64;
		if (_cacheValid && now - _cacheStamp < StatusTtlMs)
		{
			reason = _cachedReason;
			return _cachedNeedsBuild;
		}

		_cachedNeedsBuild = NeedsBuild(out _cachedReason);
		_cacheValid = true;
		_cacheStamp = now;
		reason = _cachedReason;
		return _cachedNeedsBuild;
	}

	/// <summary>
	/// Whether the module has to be produced again, and why.
	/// </summary>
	/// <remarks>
	/// The game hash is the load-bearing check: a module built from a different image than
	/// the one currently installed is not merely out of date, it is wrong — every function
	/// address in it belongs to another executable. Comparing the SHA-256 of default.xex
	/// against what the build recorded is what catches a player reinstalling from a
	/// different disc or applying a title update.
	/// </remarks>
	public static bool NeedsBuild(out string reason)
	{
		if (!File.Exists(ModulePath))
		{
			reason = "The recompiled game module has not been built yet.";
			return true;
		}

		if (!File.Exists(BuildInfoPath))
		{
			reason = "The recompiled game module has no build record beside it.";
			return true;
		}

		string recordedHash;
		try
		{
			using FileStream stream = File.OpenRead(BuildInfoPath);
			using JsonDocument document = JsonDocument.Parse(stream);
			recordedHash = document.RootElement.TryGetProperty("game_hash", out JsonElement value)
				? value.GetString() ?? string.Empty
				: string.Empty;
		}
		catch (Exception exception)
		{
			reason = "The build record could not be read: " + exception.Message;
			return true;
		}

		string currentHash = HashGameExecutable();
		if (currentHash.Length == 0)
		{
			reason = "The installed game executable could not be read.";
			return true;
		}

		if (!string.Equals(recordedHash, currentHash, StringComparison.OrdinalIgnoreCase))
		{
			reason = "The installed game executable has changed since the module was built.";
			return true;
		}

		reason = string.Empty;
		return false;
	}

	public static string HashGameExecutable()
	{
		try
		{
			using FileStream stream = File.OpenRead(GameExecutablePath);
			using var sha = SHA256.Create();
			return Convert.ToHexString(sha.ComputeHash(stream));
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	/// <summary>
	/// Runs the whole pipeline. Reports progress as a status line and a 0..1 fraction.
	/// </summary>
	public static async Task<Result> RunAsync(
		Action<Progress> progress, CancellationToken cancellation)
	{
		if (BinDirectory.Length == 0)
		{
			return new Result(false, "The native library directory has not been resolved.");
		}

		if (!File.Exists(GameExecutablePath))
		{
			return new Result(false,
				"No game executable at " + GameExecutablePath +
				". Install the game before building its code.");
		}

		// EVERYTHING missing, in one message. Reported one at a time this takes a build
		// attempt per fault to discover, and each attempt looks like "it just doesn't work".
		string generator = Path.Combine(RecompileDirectory, "bin",
			OperatingSystem.IsWindows() ? "rexglue.exe" : "rexglue");

		var missing = new List<string>();
		if (!Directory.Exists(RecompileDirectory))
		{
			missing.Add("the build inputs directory (" + RecompileDirectory + ")");
		}
		else
		{
			if (!File.Exists(generator))
			{
				missing.Add("the code generator (" + generator + ")");
			}

			if (!Directory.Exists(Path.Combine(RecompileDirectory, "lib")))
			{
				missing.Add("the link libraries (" + RecompileDirectory + "/lib)");
			}

			if (!File.Exists(Path.Combine(RecompileDirectory, "include_dirs.cmake")))
			{
				missing.Add("the staged headers (" + RecompileDirectory + "/include)");
			}
		}

		if (missing.Count > 0)
		{
			return new Result(false,
				"This install cannot build the game code because it is missing " +
				string.Join("; ", missing) +
				". These ship beside the app under bin/recompile — if you copied the " +
				"executable on its own, copy the whole exported folder instead.");
		}

		if (!Toolchain.TryResolve(out Toolchain resolved, out string toolchainError))
		{
			return new Result(false, toolchainError);
		}

		// 0. CLEAN. EVERY BUILD IS A FULL BUILD, BY DELETING THE ONLY THING THAT COULD BE
		//    REUSED. GeneratedDirectory holds the generated sources, and build/ under it holds
		//    the objects, the precompiled header and zig's compilation cache — so one delete
		//    puts the whole pipeline back to nothing and codegen, the PCH and all ~167 TUs
		//    genuinely re-run. Without it zig serves objects from cache by content hash and a
		//    "rebuild" can complete in seconds having compiled nothing, which is how a stale
		//    precompiled header survived across builds and only surfaced when one TU changed.
		//
		//    HARD FAILURE, not best effort: if the old output cannot be removed then the build
		//    that follows is partly the previous one, and that is the outcome this step exists
		//    to make impossible.
		LogPhase("clean");
		progress(new Progress("Cleaning the previous game code build...", 0.02f));
		try
		{
			if (Directory.Exists(GeneratedDirectory))
			{
				Directory.Delete(GeneratedDirectory, recursive: true);
			}
		}
		catch (Exception exception)
		{
			return new Result(false,
				"Could not remove the previous game code build at " + GeneratedDirectory +
				": " + exception.Message +
				"\nEvery build is a full build, so this has to be cleared first. Close anything " +
				"reading that folder and try again.");
		}

		// The module itself is output too. A failed build must not leave the previous one in
		// place looking current, and IsBuilt()/NeedsBuild() key off exactly this file.
		try
		{
			if (File.Exists(ModulePath))
			{
				File.Delete(ModulePath);
			}
		}
		catch (Exception exception)
		{
			return new Result(false,
				"Could not remove the previous game module at " + ModulePath + ": " + exception.Message);
		}

		InvalidateStatus();

		try
		{
			Directory.CreateDirectory(GeneratedDirectory);
		}
		catch (Exception exception)
		{
			return new Result(false, "Could not create " + GeneratedDirectory + ": " + exception.Message);
		}

		string manifest = WriteEffectiveManifest(out string manifestError);
		if (manifest.Length == 0)
		{
			return new Result(false, manifestError);
		}

		// 1. Generate. The generator prints its own phases; the fraction advances only per
		//    phase because it does not report a count we could scale.
		Result generated = await RunStepAsync(
			generator, new[] { "--force", "codegen", manifest }, RecompileDirectory,
			line => progress(new Progress("Generating game code... " + Tail(line), 0.05f)),
			cancellation);
		if (!generated.Ok)
		{
			return new Result(false, "Code generation failed: " + generated.Error);
		}

		if (!File.Exists(Path.Combine(GeneratedDirectory, "sources.cmake")))
		{
			return new Result(false,
				"The generator finished without producing sources.cmake in " + GeneratedDirectory + ".");
		}

		List<string> sources = ReadCMakeList(
			Path.Combine(GeneratedDirectory, "sources.cmake"), "GENERATED_SOURCES");
		if (sources.Count == 0)
		{
			return new Result(false,
				"The generator produced sources.cmake but it lists no translation units.");
		}

		// The host ABI entry point, the one hand-written TU in the module. Compiled here
		// exactly as our own build compiles it.
		sources.Add(Path.Combine(RecompileDirectory, "src", "payload_export.cpp"));

		// THE HOOKS ARE COMPILED, NOT LINKED. They are strong PPC overrides that belong
		// inside the payload, and an archive of them built by OUR compiler cannot link into
		// a module built by the player's — on Windows those are MSVC and MinGW, whose C++
		// objects do not mix. Building them from source here makes the whole payload one
		// compiler's output. See cmake/refii_stage_hook_sources.cmake.
		List<string> hookSources = ReadCMakeList(
			Path.Combine(RecompileDirectory, "hook_sources.cmake"), "REFII_HOOK_SOURCES");
		if (hookSources.Count == 0)
		{
			return new Result(false,
				"The hook sources are missing from this install (" + RecompileDirectory +
				"/hook_sources.cmake). Copy the whole extracted folder, not just the executable.");
		}

		sources.AddRange(hookSources);

		List<string> includeDirectories = ReadCMakeList(
			Path.Combine(RecompileDirectory, "include_dirs.cmake"), "REFII_PAYLOAD_INCLUDE_DIRS");
		if (includeDirectories.Count == 0)
		{
			return new Result(false,
				"The staged include list (" + RecompileDirectory + "/include_dirs.cmake) is empty.");
		}

		List<string> flags = CompileFlags(includeDirectories);
		flags.AddRange(StandardLibraryFlags());

		try
		{
			Directory.CreateDirectory(ObjectDirectory);
		}
		catch (Exception exception)
		{
			return new Result(false, "Could not create " + ObjectDirectory + ": " + exception.Message);
		}

		// 2. Precompile the generated header.
		//
		//    THE GENERATED HEADER IS THE COST. refii_init.h is ~61,000 lines and every one of
		//    the ~148 generated TUs includes it — measured at 6.25s of each TU's 17.7s, so
		//    the same header is parsed 148 times, about 15 minutes of pure CPU on a player's
		//    machine. Precompiling takes a TU from 17.7s to 10.6s, a 40% cut, and the object
		//    files come out byte-identical.
		LogPhase("precompile");
		progress(new Progress("Preparing the game code build...", 0.36f));

		string header = Path.Combine(GeneratedDirectory, "refii_init.h");
		string pch = Path.Combine(BuildDirectory, "refii_init.pch");
		string pchUsed = string.Empty;

		if (File.Exists(header))
		{
			// -fno-pch-timestamp: DO NOT VALIDATE refii_init.h's MTIME FROM THE PCH.
			// Codegen rewrites the header every run, so its mtime always moves even when the
			// content is identical — while zig caches the PCH by CONTENT and hands back the
			// artifact built the first time, whose embedded mtime is then permanently stale.
			// Every other TU hides this by being a cache hit, so clang never runs to check;
			// the moment one source actually changes it compiles for real and dies on
			// "file has been modified since the precompiled header was built". Content
			// correctness still comes from zig's own content hash, which is the real check.
			var pchArguments = new List<string>(flags)
			{
				"-Xclang", "-fno-pch-timestamp",
				"-x", "c++-header", header, "-o", pch,
			};

			(int pchCode, string pchOutput) =
				await RunCaptureAsync(resolved.Zig, Prefixed("c++", pchArguments), cancellation);

			// NOT FATAL. A precompiled header is a speed optimisation, and the TUs include the
			// header themselves regardless — so if clang refuses it, the right outcome is a
			// slower build rather than a failed install.
			if (pchCode == 0 && File.Exists(pch))
			{
				pchUsed = pch;
			}
			else
			{
				progress(new Progress(
					"Preparing the game code build... (no precompiled header; this will be slower)",
					0.36f));
				Godot.GD.PushWarning(
					"Precompiled header unavailable, continuing without it: " + pchOutput);
			}
		}

		// 3. Compile, JobCount() at a time.
		//
		//    Explicitly limited: left unbounded this takes every core for however long the
		//    build runs, and the player cannot use their machine meanwhile. See JobCount.
		Result compiled = await CompileAllAsync(
			resolved, sources, flags, pchUsed, progress, cancellation);
		if (!compiled.Ok)
		{
			return new Result(false, "Compiling the game code failed: " + compiled.Error);
		}

		// 4. Link.
		LogPhase("link");
		progress(new Progress("Linking the game module...", 0.95f));
		Result linked = await LinkAsync(resolved, sources.Count, cancellation);
		if (!linked.Ok)
		{
			return new Result(false, "Linking the game module failed: " + linked.Error);
		}

		if (!File.Exists(ModulePath))
		{
			return new Result(false,
				"The build reported success but produced no module at " + ModulePath + ".");
		}

		WriteBuildInfo(resolved);

		// 4. Clean up. Best effort by design: the module is already built and usable, so
		//    failing to reclaim the space must not fail the install.
		progress(new Progress("Cleaning up generated sources...", 0.97f));
		try
		{
			Directory.Delete(GeneratedDirectory, recursive: true);
		}
		catch (Exception)
		{
		}

		LogPhase("done");
		progress(new Progress("Game code ready.", 1.0f));
		return new Result(true, string.Empty);
	}

	/// <summary>
	/// Writes the manifest the generator actually reads, with absolute paths.
	/// </summary>
	/// <remarks>
	/// The staged manifest is a verbatim copy of the repo's, whose paths are relative to
	/// the repository. Resolving those against an installed app would put the generated
	/// sources somewhere inside the application directory. Only the launcher knows where
	/// the player's game and the output belong, so it states both absolutely here.
	/// </remarks>
	private static string WriteEffectiveManifest(out string error)
	{
		string source = Path.Combine(RecompileDirectory, "refii_manifest.toml");
		if (!File.Exists(source))
		{
			error = "The code generation manifest is missing from this install (" + source + ").";
			return string.Empty;
		}

		try
		{
			string[] lines = File.ReadAllLines(source);
			var rewritten = new StringBuilder();
			bool sawFilePath = false;
			bool sawOutDirectory = false;

			// `includes` is written as a MULTI-LINE array. Replacing just the line it starts
			// on leaves its entries and closing bracket behind as stray top-level text, and
			// the generator rejects the whole manifest with a parse error.
			bool insideIncludes = false;
			var includeNames = new List<string>();

			foreach (string line in lines)
			{
				if (insideIncludes)
				{
					includeNames.AddRange(QuotedValues(line));
					if (line.Contains(']'))
					{
						insideIncludes = false;
						AppendIncludes(rewritten, includeNames);
					}

					continue;
				}

				string trimmed = line.TrimStart();

				if (trimmed.StartsWith("file_path", StringComparison.Ordinal))
				{
					rewritten.AppendLine("file_path = \"" + Escape(GameExecutablePath) + "\"");
					sawFilePath = true;
					continue;
				}

				if (trimmed.StartsWith("out_directory_path", StringComparison.Ordinal))
				{
					rewritten.AppendLine("out_directory_path = \"" + Escape(GeneratedDirectory) + "\"");
					sawOutDirectory = true;
					continue;
				}

				if (trimmed.StartsWith("includes", StringComparison.Ordinal))
				{
					// Every declared include is repointed at its staged copy, rather than
					// one hardcoded name: the manifest owns which files codegen reads, and
					// silently dropping one it added would change the generated code
					// without changing anything the launcher can see.
					includeNames.AddRange(QuotedValues(line));
					insideIncludes = line.Contains('[') && !line.Contains(']');
					if (!insideIncludes)
					{
						AppendIncludes(rewritten, includeNames);
					}

					continue;
				}

				rewritten.AppendLine(line);
			}

			if (!sawFilePath || !sawOutDirectory)
			{
				error = "The code generation manifest does not declare the entrypoint paths " +
						"this launcher knows how to repoint.";
				return string.Empty;
			}

			// Inside the generated directory: it is created before this runs and removed
			// with the sources afterwards, so nothing of the player's install is left behind.
			string destination = Path.Combine(GeneratedDirectory, "refii_manifest.effective.toml");
			File.WriteAllText(destination, rewritten.ToString());
			error = string.Empty;
			return destination;
		}
		catch (Exception exception)
		{
			error = "Could not prepare the code generation manifest: " + exception.Message;
			return string.Empty;
		}
	}

	/// <summary>Every double-quoted value on a line, in order.</summary>
	private static List<string> QuotedValues(string line)
	{
		var values = new List<string>();
		int index = 0;

		while (true)
		{
			int open = line.IndexOf('"', index);
			if (open < 0)
			{
				break;
			}

			int close = line.IndexOf('"', open + 1);
			if (close < 0)
			{
				break;
			}

			string value = line[(open + 1)..close];
			if (value.Length > 0)
			{
				values.Add(value);
			}

			index = close + 1;
		}

		return values;
	}

	/// <summary>
	/// Writes the includes array, each entry repointed at its staged copy.
	/// </summary>
	private static void AppendIncludes(StringBuilder rewritten, List<string> names)
	{
		var absolute = new List<string>();
		foreach (string name in names)
		{
			// By file NAME: the staged SDK is flat, and the repo path an include is
			// written as does not exist on a player's machine.
			absolute.Add("\"" +
				Escape(Path.Combine(RecompileDirectory, Path.GetFileName(name))) + "\"");
		}

		rewritten.AppendLine("includes = [" + string.Join(", ", absolute) + "]");
	}

	private static string Escape(string path) => path.Replace("\\", "\\\\");

	private static string Tail(string line) =>
		line.Length <= 60 ? line : line[^60..];

	/// <summary>
	/// Reads a <c>set(VARIABLE a b c)</c> list out of one of the staged .cmake files.
	/// </summary>
	/// <remarks>
	/// The generator writes sources.cmake and packaging writes include_dirs.cmake, both in
	/// CMake's list syntax, and both remain the authority on what to compile and in which
	/// include order. Nothing here configures CMake — this only reads two lists out of a
	/// format we do not control.
	///
	/// TOKENS ARE SPLIT BEFORE <c>${CMAKE_CURRENT_LIST_DIR}</c> IS EXPANDED. The install
	/// directory is "Fable 2 Return to Albion", so expanding first and splitting after would
	/// tear every path in half at its spaces.
	/// </remarks>
	internal static List<string> ReadCMakeList(string path, string variable)
	{
		var values = new List<string>();
		if (!File.Exists(path))
		{
			return values;
		}

		string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
		bool inside = false;

		foreach (string rawLine in File.ReadAllLines(path))
		{
			string line = rawLine;

			int comment = line.IndexOf('#');
			if (comment >= 0)
			{
				line = line[..comment];
			}

			if (!inside)
			{
				int start = line.IndexOf("set(" + variable, StringComparison.Ordinal);
				if (start < 0)
				{
					continue;
				}

				inside = true;
				line = line[(start + 4 + variable.Length)..];
			}

			bool last = false;
			int close = line.IndexOf(')');
			if (close >= 0)
			{
				line = line[..close];
				last = true;
			}

			foreach (string token in line.Split(
				(char[]?)null, StringSplitOptions.RemoveEmptyEntries))
			{
				values.Add(token.Replace("${CMAKE_CURRENT_LIST_DIR}", directory));
			}

			if (last)
			{
				break;
			}
		}

		return values;
	}

	private static string ObjectDirectory => Path.Combine(BuildDirectory, "obj");

	/// <summary>Zig's compilation cache, pinned inside the build directory.</summary>
	/// <remarks>
	/// DELIBERATELY NOT THE DEFAULT (~/.cache/zig). Zig keys cached objects on content, so a
	/// rebuild reuses them across runs and across installs — the opposite of what the build
	/// step promises. Pinned here it lives under GeneratedDirectory, which step 0 deletes, so
	/// every build compiles every TU for real. It also keeps a few GB of build cache inside
	/// the app's own working directory rather than growing in the player's home.
	/// </remarks>
	private static string CompileCacheDirectory => Path.Combine(BuildDirectory, "zig-cache");

	private static string ObjectPath(int index) =>
		Path.Combine(ObjectDirectory, index.ToString("D4") + ".o");

	/// <summary>
	/// The flags every translation unit is compiled with.
	/// </summary>
	/// <remarks>
	/// MATCHED TO THE DEV BUILD. <c>-mcmodel=large</c> and <c>-msse4.1</c> are applied to the
	/// payload by refii_apply_generated_payload_compile_options in the top-level
	/// CMakeLists, and the install-time project set NEITHER — so a player was compiling the
	/// same sources under a different code model than anything we build or test. At ~300 MB
	/// the module overflows 32-bit PC-relative relocations without the large model.
	///
	/// Optimised even in a Debug install: this is the guest's hot code, the whole simulation
	/// runs inside it, and -O0 would hand the player a guest that is the entire frame budget.
	/// Measured per generated TU: -O0 9.6s, -O1 14.3s, -O2 17.7s. Debug info costs nothing
	/// measurable and keeps generated frames resolvable in a backtrace.
	/// </remarks>
	/// <summary>
	/// The triple to compile for, when the bundled zig is not built for this host.
	/// </summary>
	/// <remarks>
	/// WINDOWS ON ARM IS SHIPPED THE x86_64 ZIG, deliberately, so the target the toolchain would
	/// otherwise have inferred has to be named here instead.
	///
	/// zig 0.16.0's own aarch64-windows build crashes clang on -include-pch: 6 to 13 of every 16
	/// units, sequentially as much as in parallel, on a precompiled header of nothing but
	/// standard library headers, with STATUS_ACCESS_VIOLATION and STATUS_INSTRUCTION_MISALIGNMENT.
	/// Jumping to a misaligned instruction is bad codegen inside zig's binary, not anything this
	/// build does wrong -- and zig's aarch64-windows CI is down for want of hardware, so it is not
	/// a thing upstream is currently catching. The same units compiled without the PCH crash 0 of
	/// 16, which is what made the header look innocent at first.
	///
	/// The x86_64 build under emulation cross-compiles the identical objects with the PCH working,
	/// and measured 0.27s per unit against 0.64s for dropping the PCH on the native build: the
	/// header is worth more than the emulation costs, so this is 2.4x FASTER, not a compromise.
	///
	/// aarch64-windows-gnu is what the native toolchain already defaulted to, so no ABI changes --
	/// see Toolchain's note on the GNU/MinGW ABI. Linux and Windows x86_64 name nothing and keep
	/// native detection, which is what the glibc discovery described there depends on.
	/// </remarks>
	private static IReadOnlyList<string> TargetFlags() =>
		OperatingSystem.IsWindows()
				&& RuntimeInformation.OSArchitecture == Architecture.Arm64
			? new[] { "-target", "aarch64-windows-gnu" }
			: Array.Empty<string>();

	/// <summary>True for a C translation unit, which cannot take the C++ flag set.</summary>
	private static bool IsCSource(string path) =>
		Path.GetExtension(path).Equals(".c", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// The C++ flag set rewritten for a C translation unit.
	/// </summary>
	/// <remarks>
	/// Only the flags that are actually C++-specific are touched, so the defines, the undefines
	/// and every include path stay exactly as the C++ units saw them -- lua51 has to be
	/// configured by the same macros whichever language builds it.
	///
	/// gnu99 to match refii_lua51's own C_STANDARD, so the payload's copy is built the way this
	/// tree builds it. The dropped `-include` is the libc++ chrono shim, which is C++ only.
	/// </remarks>
	private static List<string> AsCFlags(IReadOnlyList<string> cxxFlags)
	{
		var flags = new List<string>(cxxFlags.Count);
		for (int index = 0; index < cxxFlags.Count; index++)
		{
			string flag = cxxFlags[index];
			if (flag.StartsWith("-std=", StringComparison.Ordinal))
			{
				flags.Add("-std=gnu99");
				continue;
			}

			if (flag == "-include")
			{
				index++;  // and the header it names
				continue;
			}

			flags.Add(flag);
		}

		return flags;
	}

	private static List<string> CompileFlags(IReadOnlyList<string> includeDirectories)
	{
		var flags = new List<string>
		{
			"-std=gnu++23",
			"-fPIC",
			"-O2",
			// NO -g. This is a release build of the player's own game code, and the debug info
			// was not free: refii_register.cpp is 61,000 lines of one function, where the DWARF
			// for it dominated the unit's compile time. Nothing ships a debugger for it.
		};

		// Ahead of everything else so the rest is read against the right target.
		flags.InsertRange(0, TargetFlags());

		if (RuntimeInformation.OSArchitecture == Architecture.X64)
		{
			flags.Add("-msse4.1");
		}

		if (!OperatingSystem.IsWindows())
		{
			flags.Add("-mcmodel=large");
		}

		// MATCHED TO THE PAYLOAD TARGETS IN OUR OWN BUILD. The hook sources now compile
		// here, so the definitions that make them payload-shaped have to be stated here too:
		// logging crosses as C rather than as spdlog, and Tracy is absent. Omitting either
		// produces a module that imports C++ symbols again and fails to link on Windows.
		flags.Add("-DREFII_PAYLOAD_LOG_BRIDGE=1");

		// HEADER-ONLY fmt. The log bridge still formats with ::fmt::format before handing the
		// finished string across the C boundary, so the payload pulls in fmt::vformat, which is
		// NOT inline. On Linux that resolves inside librexruntime because both sides are GNU; on
		// Windows the runtime is MSVC-built and mangles it differently, so it can never resolve
		// and the link ends on one undefined symbol. Inlining fmt removes the import entirely,
		// which is the same answer on both platforms rather than a Windows special case.
		flags.Add("-DFMT_HEADER_ONLY");

		// SPDLOG_COMPILED_LIB is not optional here. Without it spdlog's headers switch to
		// header-only and define logger::flush() inline, which collides with the payload's
		// own forwarder for it:
		//   "error: redefinition of 'flush'" against spdlog/logger-inl.h
		// SPDLOG_FMT_EXTERNAL goes with it, because that is how this build compiles spdlog
		// and the two must agree on which fmt the headers expect.
		flags.Add("-DSPDLOG_COMPILED_LIB");
		flags.Add("-DSPDLOG_FMT_EXTERNAL");
		foreach (string undefine in new[]
		{
			"TRACY_ENABLE", "TRACY_MANUAL_LIFETIME", "TRACY_DELAYED_INIT",
			"TRACY_ON_DEMAND", "TRACY_FIBERS", "TRACY_IMPORTS", "REXGLUE_ENABLE_PROFILING",
		})
		{
			flags.Add("-U" + undefine);
		}

		// FORCE-INCLUDED AHEAD OF EVERYTHING. The SDK's chrono headers specialize
		// std::chrono::clock_time_conversion, which libstdc++ declares and libc++ does not, and
		// an explicit specialization of an undeclared template is an error rather than something
		// the preprocessor can be talked out of. On Linux the build compiles against staged
		// libstdc++ headers so it never arises; on Windows refii_stage_stdcxx.cmake stages
		// nothing and zig supplies libc++, so it always does. The header self-guards on
		// _LIBCPP_VERSION and costs a libstdc++ build one <version> include.
		string chronoShim = Path.Combine(RecompileDirectory, "src", "libcxx_chrono_shim.h");
		if (File.Exists(chronoShim))
		{
			flags.Add("-include");
			flags.Add(chronoShim);
		}

		flags.Add("-I" + GeneratedDirectory);
		foreach (string directory in includeDirectories)
		{
			flags.Add("-I" + directory);
		}

		return flags;
	}

	private static string StdCxxDirectory => Path.Combine(RecompileDirectory, "stdcxx");

	/// <summary>
	/// Points the compiler at the staged libstdc++ headers instead of Zig's libc++.
	/// </summary>
	/// <remarks>
	/// ZIG BUNDLES libc++, AND THE PAYLOAD NEEDS libstdc++ — see cmake/refii_stage_stdcxx.cmake
	/// for why. Under libc++ our own rex/chrono/chrono.h does not even parse:
	///   "error: explicit specialization of undeclared template struct 'clock_time_conversion'"
	/// and the std::-typed symbols the payload imports from librefii_vfs would not resolve,
	/// because libc++ mangles them through an inline namespace.
	///
	/// ORDER IS THE CONTRACT. The C++ headers must come FIRST and Zig's libc headers after,
	/// because &lt;cmath&gt; reaches the C library through <c>#include_next &lt;math.h&gt;</c>,
	/// which resumes the search after the directory it was itself found in. Reversed, the
	/// build fails with "fatal error: 'math.h' file not found".
	///
	/// Returns empty when nothing is staged, which is the Windows case: that build links
	/// MSVC's runtime and this question does not arise.
	/// </remarks>
	private static List<string> StandardLibraryFlags()
	{
		var flags = new List<string>();

		string order = Path.Combine(StdCxxDirectory, "order.txt");
		if (!File.Exists(order))
		{
			return flags;
		}

		flags.Add("-nostdinc++");
		foreach (string slot in File.ReadAllLines(order))
		{
			if (slot.Trim().Length == 0)
			{
				continue;
			}

			flags.Add("-isystem");
			flags.Add(Path.Combine(StdCxxDirectory, slot.Trim()));
		}

		string libc = Path.Combine(Toolchain.BundleDirectory, "lib", "libc", "include");
		bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
		foreach (string variant in new[]
		{
			arm ? "aarch64-linux-gnu" : "x86_64-linux-gnu",
			"generic-glibc",
			arm ? "aarch64-linux-any" : "x86-linux-any",
			"any-linux-any",
		})
		{
			string directory = Path.Combine(libc, variant);
			if (Directory.Exists(directory))
			{
				flags.Add("-isystem");
				flags.Add(directory);
			}
		}

		return flags;
	}

	/// <summary>
	/// Compiles every translation unit, <see cref="JobCount"/> at a time.
	/// </summary>
	/// <remarks>
	/// The first failure stops the rest from starting. A broken generated source fails the
	/// same way in all 148 units, and letting them all run turns one compiler error into
	/// several minutes of waiting and megabytes of duplicate output.
	/// </remarks>
	private static async Task<Result> CompileAllAsync(
		Toolchain zig,
		IReadOnlyList<string> sources,
		IReadOnlyList<string> flags,
		string precompiledHeader,
		Action<Progress> progress,
		CancellationToken cancellation)
	{
		int total = sources.Count;
		int done = 0;
		string failure = string.Empty;
		object failureLock = new();

		// THE PRECOMPILED HEADER IS NOT ALWAYS USABLE. zig 0.16.0 on windows-arm64 crashes
		// clang on -include-pch: measured 6-13 of every 16 units, sequentially as well as in
		// parallel, with and without -fno-pch-timestamp, while the same units compiled without
		// it crash 0 of 16. It is only a speed optimisation and every unit includes the header
		// itself, so a build that hits this drops the PCH and carries on rather than failing.
		int pchBroken = 0;

		// ONE UNIT ALONE FIRST, then the rest in parallel.
		//
		// ZIG CRASHES ON A COLD SHARED CACHE. Step 0 deletes the build directory and
		// CompileCacheDirectory lives inside it, so every build starts with an empty
		// ZIG_GLOBAL_CACHE_DIR/ZIG_LOCAL_CACHE_DIR. Several zig processes populating that at
		// once intermittently die with an access violation, heap corruption or a stack buffer
		// overrun -- measured at 2-3 of every 16 units on windows-arm64, against 0-1 once a
		// single compile has populated it. Costs nothing: unit 0 is work the build had to do.
		var warmed = new TaskCompletionSource<bool>(
			TaskCreationOptions.RunContinuationsAsynchronously);

		using var gate = new SemaphoreSlim(JobCount());
		var running = new List<Task>(total);

		for (int index = 0; index < total; index++)
		{
			int slot = index;
			running.Add(Task.Run(async () =>
			{
				// OUTSIDE THE GATE, deliberately. Waiting on unit 0 while holding a permit lets the
				// other units take every permit and leaves unit 0 unable to acquire one.
				if (slot != 0)
				{
					await warmed.Task.WaitAsync(cancellation).ConfigureAwait(false);
				}

				await gate.WaitAsync(cancellation).ConfigureAwait(false);
				try
				{
					lock (failureLock)
					{
						if (failure.Length != 0)
						{
							return;
						}
					}

					// LUA51 IS C. Its 32 files compile into the payload beside the generated code,
					// and everything above this is a C++ flag set: clang rejects -std=gnu++23
					// outright on a C input, and a C++ precompiled header and the libc++ chrono
					// shim mean nothing there either.
					//
					// They must build AS C, not merely be made to compile. lua.h carries no
					// extern "C" guards and host_lua_vm.cpp wraps its includes in one, so the
					// C-linkage names are what that side references -- built as C++ every lua_*
					// entry would be mangled and none of them would resolve.
					bool isC = IsCSource(sources[slot]);

					List<string> BuildArguments(bool withPch)
					{
						var arguments = isC ? AsCFlags(flags) : new List<string>(flags);
						if (withPch && !isC)
						{
							arguments.Add("-include-pch");
							arguments.Add(precompiledHeader);
						}

						// PAYLOAD-INTERNAL BY CONSTRUCTION. rex_bridge_payload.cpp defines the
						// rex:: symbols librexruntime also defines; exported, the payload's copies
						// capture the host's own lazily-bound references through the RTLD_GLOBAL
						// scope and each forwarder recurses into itself until the stack ends.
						// Mirrors the source-file property in src/hooks/CMakeLists.txt.
						if (Path.GetFileName(sources[slot]) == "rex_bridge_payload.cpp")
						{
							arguments.Add("-fvisibility=hidden");
						}

						arguments.Add("-c");
						arguments.Add(sources[slot]);
						arguments.Add("-o");
						arguments.Add(ObjectPath(slot));
						return arguments;
					}

					// A C unit never uses it, so it can never be the unit that blames it either.
					bool usePch = precompiledHeader.Length != 0 && !isC
						&& Volatile.Read(ref pchBroken) == 0;

					(int code, string output) = await RunCaptureAsync(
						zig.Zig, Prefixed(isC ? "cc" : "c++", BuildArguments(usePch)), cancellation)
						.ConfigureAwait(false);

					// A CRASH WITH THE PCH IS THE PCH'S FAULT until shown otherwise, so this unit is
					// compiled again without it and every later unit skips it too. A real compile error
					// is exit 1 and is left alone -- dropping the header would not change it, and the
					// second attempt would only bury the diagnostic the player needs to see.
					if (code != 0 && usePch && IsCrashExit(code))
					{
						if (Interlocked.Exchange(ref pchBroken, 1) == 0)
						{
							LogWrite("the precompiled header crashes this toolchain; "
								+ "compiling the rest without it");
						}

						(code, output) = await RunCaptureAsync(
							zig.Zig, Prefixed(isC ? "cc" : "c++", BuildArguments(false)), cancellation)
							.ConfigureAwait(false);
					}

					if (code != 0)
					{
						lock (failureLock)
						{
							if (failure.Length == 0)
							{
								failure = Path.GetFileName(sources[slot]) + ":\n" + output;
							}
						}

						return;
					}

					int finished = Interlocked.Increment(ref done);
					progress(new Progress(
						"Compiling game code... " + finished + "/" + total,
						0.40f + 0.55f * Math.Clamp((float)finished / total, 0.0f, 1.0f)));
				}
				finally
				{
					gate.Release();

					// ALWAYS, even when unit 0 failed or threw: everything else is waiting on this
					// and never signalling would hang the build instead of reporting the failure.
					if (slot == 0)
					{
						warmed.TrySetResult(true);
					}
				}
			}, cancellation));
		}

		try
		{
			await Task.WhenAll(running).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return new Result(false, "cancelled");
		}

		return failure.Length == 0
			? new Result(true, string.Empty)
			: new Result(false, failure);
	}

	/// <summary>
	/// Links the objects, the hook archive and the shipped libraries into the module.
	/// </summary>
	/// <remarks>
	/// WHOLE-ARCHIVE FOR THE HOOKS. They are strong PPC overrides of functions the generator
	/// emits WEAK, and a weak definition creates no undefined reference — so a normal archive
	/// link pulls in no member at all and every hook silently disappears. The module builds,
	/// runs, and behaves as though none of refii's native overrides existed.
	/// </remarks>
	private static async Task<Result> LinkAsync(
		Toolchain zig, int objectCount, CancellationToken cancellation)
	{
		string libraryDirectory = Path.Combine(RecompileDirectory, "lib");

		var arguments = new List<string> { "-shared", "-o", ModulePath };
		arguments.InsertRange(0, TargetFlags());
		if (!OperatingSystem.IsWindows())
		{
			arguments.Add("-mcmodel=large");

			// RUNPATH, WITHOUT WHICH THE MODULE CANNOT BE LOADED AT ALL.
			//
			// The module is dlopen'd by name and needs librexruntime, librefii_vfs and
			// librefii_lua beside it. CMake used to emit this automatically from the linked
			// library paths, and driving the linker directly means stating it — a module
			// linked without it builds, verifies clean under `ldd -r` with LD_LIBRARY_PATH
			// set, and then fails at runtime with
			//   "librexruntimed.so: cannot open shared object file".
			//
			// Both entries, matching the dev build: an exported app has the whole native
			// closure in one directory, a dev tree splits bin/ and lib/.
			arguments.Add("-Wl,-rpath,$ORIGIN:$ORIGIN/lib");
		}

		for (int index = 0; index < objectCount; index++)
		{
			arguments.Add(ObjectPath(index));
		}

		// NO WHOLE-ARCHIVE, AND NO HOOK ARCHIVE AT ALL. The hooks are among the objects
		// compiled above, so their strong PPC overrides are already in the link as ordinary
		// members rather than weak-definition members an archive link would skip.

		// Already loaded in the process by the time the payload is; linking them here only
		// resolves the references.
		// NO refii_lua. Its sources are staged and compiled beside the generated code instead
		// (see REFII_LUA_HOOK_SOURCES): they call the recompiled guest, so they cannot ship as a
		// library the payload links -- and linking the MSVC-built static one is what dragged
		// /DEFAULTLIB:msvcrtd into a MinGW link. The refii_bridge_lua_* entry points the payload
		// needs are among those sources, so they are defined in the payload itself now.
		foreach (string name in new[] { "rexruntime", "refii_core", "refii_vfs" })
		{
			string found = FindLibrary(libraryDirectory, name);
			if (found.Length == 0)
			{
				return new Result(false,
					"the " + name + " library is missing from " + libraryDirectory);
			}

			arguments.Add(found);
		}

		// libstdc++, NOT libc++. The translation units were compiled against the staged
		// libstdc++ headers, so every std:: reference in the module is a GNU one; zig would
		// otherwise link its own libc++ beside them and put two C++ runtimes, with two
		// operator new, in one process.
		//
		// libstdc++.so.6 does not appear in the module's DT_NEEDED even so — --as-needed
		// drops it because librexruntime already carries it — which is the same shape the
		// GCC-built payload has. `ldd -r` resolves the module with 0 undefined symbols.
		if (StandardLibraryFlags().Count > 0)
		{
			arguments.Add("-nostdlib++");
			arguments.Add("-lstdc++");
		}

		(int code, string output) = await RunCaptureAsync(
			zig.Zig, Prefixed("c++", arguments), cancellation).ConfigureAwait(false);

		return code == 0
			? new Result(true, string.Empty)
			: new Result(false, output);
	}

	/// <summary>
	/// Finds a shipped library by base name, in either build configuration.
	/// </summary>
	/// <remarks>
	/// Both the plain and the "d"-suffixed name: a Debug-configured release stages
	/// librexruntimed, a Release one stages librexruntime, and the payload has to link
	/// whichever this install actually shipped.
	/// </remarks>
	private static string FindLibrary(string directory, string name)
	{
		var found = new List<string>();

		foreach (string suffix in new[] { string.Empty, "rd", "d" })
		{
			foreach (string candidate in new[]
			{
				"lib" + name + suffix + ".so",
				"lib" + name + suffix + ".a",
				name + suffix + ".lib",
				"lib" + name + suffix + ".dll.a",
				// LAST, so a real import library wins if one was staged. A Windows SHARED target
				// stages $<TARGET_FILE>, which is the DLL and not the .lib, and lld links against a
				// DLL directly -- verified against the staged runtime.
				name + suffix + ".dll",
				"lib" + name + suffix + ".dll",
			})
			{
				string path = Path.Combine(directory, candidate);
				if (File.Exists(path))
				{
					found.Add(path);
				}
			}
		}

		if (found.Count == 0)
		{
			return string.Empty;
		}

		// ONE CONFIGURATION SHOULD BE PRESENT, and the staging now empties lib/ so that it is.
		// When more than one is anyway -- an older tree, or a hand-copied file -- the newest is
		// the one this install just built, and taking it on name order instead is how the
		// payload ended up importing a four-hour-old rexruntimerd.dll while the host loaded
		// rexruntimed.dll. Two runtimes, two sets of globals, and a null deref in the guest.
		// Logged rather than silent: picking between configurations is never routine.
		if (found.Count > 1)
		{
			found.Sort((left, right) =>
				File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
			LogWrite("more than one " + name + " in " + directory + " ("
				+ string.Join(", ", found.ConvertAll(Path.GetFileName))
				+ "); taking the newest: " + Path.GetFileName(found[0]));
		}

		return found[0];
	}

	private static List<string> Prefixed(string first, IReadOnlyList<string> rest)
	{
		var arguments = new List<string>(rest.Count + 1) { first };
		arguments.AddRange(rest);
		return arguments;
	}

	/// <summary>
	/// Writes the record the launcher reads to decide whether the module is still current.
	/// </summary>
	/// <remarks>
	/// Beside the module, NOT beside the sources, which are deleted after a successful build.
	/// </remarks>
	private static void WriteBuildInfo(Toolchain zig)
	{
		try
		{
			var record = new Dictionary<string, string>
			{
				["game_hash"] = HashGameExecutable(),
				["compiler"] = "zig " + Toolchain.Version(zig),
				["platform"] = RuntimeInformation.RuntimeIdentifier,
				["built_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
			};

			File.WriteAllText(BuildInfoPath, JsonSerializer.Serialize(
				record, new JsonSerializerOptions { WriteIndented = true }));
		}
		catch (Exception)
		{
		}
	}

	/// <summary>
	/// Windows status codes that mean zig DIED rather than rejecting the source.
	/// </summary>
	/// <remarks>
	/// A CRASH IS NOT A DIAGNOSTIC. clang reports a bad translation unit as exit 1 with a
	/// message on stderr; these exit with an NTSTATUS and stderr EMPTY, so the install used to
	/// fail with a filename, a colon and nothing after it. Retrying only these codes leaves a
	/// genuine compile error failing on the spot, which is what it should do.
	///
	/// Seen on windows-arm64 while several zig processes populate one cold cache directory.
	/// The object file is usually already written when it happens, so the retry is cheap.
	/// </remarks>
	private static bool IsCrashExit(int code) =>
		code == unchecked((int)0xC0000005)   // STATUS_ACCESS_VIOLATION
			|| code == unchecked((int)0xC0000374)   // STATUS_HEAP_CORRUPTION
			|| code == unchecked((int)0xC0000409);  // STATUS_STACK_BUFFER_OVERRUN

	/// <summary>How many times a crashed (not failed) zig invocation is run again.</summary>
	private const int CrashRetries = 2;

	/// <summary>
	/// Runs zig, retrying if it crashes. Every caller is a deterministic, re-runnable step --
	/// the precompiled header, one translation unit, or the link -- so running one again is
	/// always safe.
	/// </summary>
	private static async Task<(int Code, string Output)> RunCaptureAsync(
		string executable, IReadOnlyList<string> arguments, CancellationToken cancellation)
	{
		for (int attempt = 0; ; attempt++)
		{
			(int Code, string Output) result = await RunCaptureOnceAsync(
				executable, arguments, cancellation).ConfigureAwait(false);

			if (!IsCrashExit(result.Code) || attempt >= CrashRetries
					|| cancellation.IsCancellationRequested)
			{
				return result;
			}

			LogWrite("retrying " + Path.GetFileName(executable) + " after crash exit 0x"
				+ result.Code.ToString("X8") + ", attempt " + (attempt + 2)
				+ " of " + (CrashRetries + 1));
		}
	}

	/// <summary>Runs a process to completion and returns its exit code and output.</summary>
	private static async Task<(int Code, string Output)> RunCaptureOnceAsync(
		string executable, IReadOnlyList<string> arguments, CancellationToken cancellation)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = executable,
			WorkingDirectory = RecompileDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,

			// NO CONSOLE WINDOW. Redirecting both streams is not enough on Windows: the
			// launcher is a GUI-subsystem process and zig and rexglue are console ones, so
			// each child gets a console allocated and flashed on screen even though nothing
			// is ever drawn in it. The compile phase runs one per batch across ~150k
			// functions, which is a window appearing and vanishing for the whole build.
			// Meaningless on Linux, where there is no such thing to suppress.
			CreateNoWindow = true,
		};

		// Both, because zig consults a global and a local cache and either would carry work
		// over from the last build. Pointed inside the build directory so the clean in step 0
		// removes them with everything else.
		startInfo.EnvironmentVariables["ZIG_GLOBAL_CACHE_DIR"] = CompileCacheDirectory;
		startInfo.EnvironmentVariables["ZIG_LOCAL_CACHE_DIR"] = CompileCacheDirectory;

		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		Log(executable, arguments);

		try
		{
			using var process = new Process { StartInfo = startInfo };
			if (!process.Start())
			{
				return (-1, LogResult(-1, "could not start " + executable));
			}

			Task<string> output = process.StandardOutput.ReadToEndAsync();
			Task<string> errors = process.StandardError.ReadToEndAsync();
			try
			{
				await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// KILL THE CHILD, as RunStepAsync does. WaitForExitAsync only stops WAITING; the
				// zig process carries on. On Windows the survivors keep handles open on the
				// object files and the cache, so step 0's delete of the build directory fails
				// with a sharing violation and the NEXT build cannot start either. On Linux the
				// unlink succeeds regardless, which is why this only ever bites on Windows.
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch (Exception)
				{
				}

				return (-1, LogResult(-1, "cancelled"));
			}

			return (process.ExitCode, LogResult(process.ExitCode,
				(await output.ConfigureAwait(false) + await errors.ConfigureAwait(false)).Trim()));
		}
		catch (OperationCanceledException)
		{
			return (-1, LogResult(-1, "cancelled"));
		}
		catch (Exception exception)
		{
			return (-1, LogResult(-1, exception.ToString()));
		}
	}

	/// <summary>
	/// The build's own log, because until now it had none.
	/// </summary>
	/// <remarks>
	/// THIS RUNS ON THE PLAYER'S MACHINE and it can fail there for reasons no developer will
	/// reproduce — a game image we have not seen, a full disk, an antivirus holding an object
	/// file open. Every phase's real output used to exist only as a truncated line on the
	/// progress bar and a string in a <see cref="Result"/>, so a failed install left NOTHING
	/// behind to send back. LauncherPaths.GetLauncherLogPath() had been written for this and
	/// was never called from anywhere.
	///
	/// BEST EFFORT, ALWAYS. Logging is a diagnostic, not a feature: if the path is
	/// unwritable the build carries on rather than failing for want of somewhere to complain.
	/// </remarks>
	private static void LogWrite(string text)
	{
		try
		{
			string path = LauncherPaths.GetLauncherLogPath();
			string? directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.AppendAllText(path,
				DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + text + Environment.NewLine);
		}
		catch (Exception)
		{
		}
	}

	/// <summary>Records a phase boundary, so a log shows how far a failed build reached.</summary>
	internal static void LogPhase(string phase) => LogWrite("=== " + phase);

	/// <summary>
	/// Records the command line. Arguments are logged one per line rather than joined: the
	/// compile invocations carry dozens of -isystem paths with spaces in them, and a joined
	/// line cannot be pasted back into a shell to reproduce the failure by hand.
	/// </summary>
	private static void Log(string executable, IReadOnlyList<string> arguments)
	{
		var text = new StringBuilder();
		text.Append("run: ").Append(executable);
		foreach (string argument in arguments)
		{
			text.Append(Environment.NewLine).Append("       ").Append(argument);
		}

		LogWrite(text.ToString());
	}

	/// <summary>
	/// Records an exit code, and the output only when it is not zero — a successful compile
	/// of 150 translation units has nothing worth keeping and would bury the one that failed.
	/// Returns its argument so call sites stay single-expression.
	/// </summary>
	private static string LogResult(int code, string output)
	{
		if (code == 0)
		{
			LogWrite("exit 0");
		}
		else
		{
			LogWrite("exit " + code + (output.Length > 0 ? Environment.NewLine + output : string.Empty));
		}

		return output;
	}

	private static async Task<Result> RunStepAsync(
		string executable,
		IReadOnlyList<string> arguments,
		string workingDirectory,
		Action<string> onLine,
		CancellationToken cancellation)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = executable,
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,

			// Same as RunCaptureAsync: a GUI parent starting a console child on Windows
			// gets a console window per invocation unless this says otherwise.
			CreateNoWindow = true,
		};

		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		// The generator links the ReXGlue runtime, and its RUNPATH is $ORIGIN — it looks
		// beside itself, while the staged libraries are one directory over in lib/. Without
		// this it cannot start at all, which reads as "the generator is missing" rather
		// than "it could not find its runtime".
		string libraryDirectory = Path.Combine(RecompileDirectory, "lib");
		string variable = OperatingSystem.IsWindows()
			? "PATH"
			: OperatingSystem.IsMacOS() ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH";
		string existing = Environment.GetEnvironmentVariable(variable) ?? string.Empty;
		startInfo.Environment[variable] = existing.Length == 0
			? libraryDirectory
			: libraryDirectory + Path.PathSeparator + existing;

		// The tail of the output, kept so a failure can say what actually went wrong
		// instead of only an exit code. Bounded: a failing compile can produce megabytes.
		var recent = new Queue<string>();

		// The WHOLE output, for the log. The 40-line tail above is what a person can be shown
		// in a dialog; it is not enough to diagnose a generator that printed the real cause
		// early and then kept going. Buffered and written once rather than appended per line,
		// because this can be thousands of lines and reopening the file for each is slower
		// than the generator itself.
		var transcript = new StringBuilder();

		void Record(string? line)
		{
			if (line is null)
			{
				return;
			}

			transcript.AppendLine(line);

			recent.Enqueue(line);
			while (recent.Count > 40)
			{
				recent.Dequeue();
			}

			onLine(line);
		}

		Log(executable, arguments);

		try
		{
			using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
			process.OutputDataReceived += (_, args) => Record(args.Data);
			process.ErrorDataReceived += (_, args) => Record(args.Data);

			if (!process.Start())
			{
				return new Result(false, "could not start " + executable);
			}

			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			try
			{
				await process.WaitForExitAsync(cancellation);
			}
			catch (OperationCanceledException)
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch (Exception)
				{
				}

				return new Result(false, "cancelled");
			}

			// The transcript goes to the log whether this succeeded or not: a generator that
			// exits 0 without writing sources.cmake is exactly the failure this pipeline has
			// hit, and the reason is in the output, not the exit code.
			LogWrite("output of " + Path.GetFileName(executable) + Environment.NewLine + transcript);

			if (process.ExitCode != 0)
			{
				LogWrite("exit " + process.ExitCode);
				return new Result(false,
					"exit code " + process.ExitCode + "\n" + string.Join('\n', recent));
			}

			LogWrite("exit 0");
			return new Result(true, string.Empty);
		}
		catch (Exception exception)
		{
			LogWrite("output of " + Path.GetFileName(executable) + Environment.NewLine + transcript);
			LogWrite("threw: " + exception);
			return new Result(false, exception.Message);
		}
	}
}
