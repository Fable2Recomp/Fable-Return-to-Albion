using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Refii.Launcher.Core;
using Refii.Mods;

namespace Refii.Launcher.Recompile;

/// <summary>
/// Turns the player's own game executable into the native module the runtime loads.
/// </summary>
/// <remarks>
/// AN INSTALL STEP, and the one that cannot be skipped: the recompiled guest is derived
/// from the player's game image, so it is not redistributable and no release can contain
/// it. What we ship is the generator (refii_recompiler, a library in bin/), the runtime,
/// our own libraries and a bundled Zig under <c>recompile/</c>; this drives them.
///
/// Five phases, in order:
///   1. generate  — the generator reads assets/game/default.xex and writes C++ to
///                  assets/generated, in this process (see <see cref="Recompiler"/>)
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

	/// <summary>The title update beside the installed executable, when the player installed one.</summary>
	public static string GameTitleUpdatePath =>
		Path.Combine(Path.GetDirectoryName(GameExecutablePath) ?? string.Empty, "default.xexp");

	/// <summary>
	/// Which <c>versions/</c> folder describes the installed game: the patched executable or
	/// the disc one.
	/// </summary>
	/// <remarks>
	/// THE TWO ARE DIFFERENT PROGRAMS. The title update is a full relink, so no guest address
	/// is shared between them; each folder carries its own manifest and config, and the hooks
	/// bind through the function names the two configs have in common. The generator applies
	/// the patch whenever it sits beside the executable, so its presence IS the version.
	/// </remarks>
	public static string GameVersionName =>
		File.Exists(GameTitleUpdatePath) ? "Fable2TU1" : "Fable2";

	/// <summary>The ReXGlue manifest and config for the installed game's version.</summary>
	public static string VersionDirectory =>
		Path.Combine(RecompileDirectory, "versions", GameVersionName);

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
	/// Whether to compile with every thread rather than half of them.
	/// </summary>
	/// <remarks>
	/// DEFAULTS OFF. Compiling ~150,000 functions saturates every thread for the length of
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
	/// Compile jobs to run at once. At least one, however few threads are reported.
	/// </summary>
	public static int JobCount()
	{
		int threads = Environment.ProcessorCount;
		if (UseAllCores)
		{
			return Math.Max(1, threads);
		}

		return Math.Max(1, threads / 2);
	}

	public readonly record struct Progress(string Status, float Fraction);

	/// <summary>The outcome. <c>Stopped</c> is the player's doing, not a failure.</summary>
	public readonly record struct Result(bool Ok, string Error, bool Stopped = false);

	private static Result StoppedResult => new(false, "stopped", Stopped: true);

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

		// DEV ONLY, and deliberately AFTER the module-exists test so it can never conjure a
		// module that is not there. Written by REFII_DEV_SKIP_GAMECODE_REBUILD=ON; packaging
		// excludes it and the export refuses to zip a tree containing it, so it cannot reach a
		// player. Announced every launch, because a silently-skipped rebuild is exactly the
		// failure the input fingerprint below exists to prevent.
		if (File.Exists(Path.Combine(RecompileDirectory, "dev-skip-rebuild")))
		{
			Godot.GD.Print(
				"[gamecode] dev-skip-rebuild is present: using the module already in bin/ " +
				"without checking it against the installed game or the shipped sources. " +
				"Turn REFII_DEV_SKIP_GAMECODE_REBUILD off to restore the real check.");
			reason = string.Empty;
			return false;
		}

		if (!File.Exists(BuildInfoPath))
		{
			reason = "The recompiled game module has no build record beside it.";
			return true;
		}

		string recordedHash;
		string recordedInputs;
		string recordedMods;
		try
		{
			using FileStream stream = File.OpenRead(BuildInfoPath);
			using JsonDocument document = JsonDocument.Parse(stream);
			recordedHash = document.RootElement.TryGetProperty("game_hash", out JsonElement value)
				? value.GetString() ?? string.Empty
				: string.Empty;
			recordedInputs =
				document.RootElement.TryGetProperty("build_inputs", out JsonElement inputs)
					? inputs.GetString() ?? string.Empty
					: string.Empty;
			// ABSENT MEANS NONE: every record written before hard mods existed compiled no mod code,
			// which is exactly what an empty hash states.
			recordedMods = document.RootElement.TryGetProperty("mods_hash", out JsonElement mods)
				? mods.GetString() ?? string.Empty
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

		// OUR OWN CODE, which the game hash cannot see. A record with no `build_inputs` at all
		// was written by a build from before this check existed, and there is no way to know
		// what it compiled — so it rebuilds rather than being assumed current. That costs one
		// rebuild, once, on the update that introduces this.
		if (recordedInputs.Length == 0)
		{
			reason = "The build record does not say what sources the module was built from.";
			return true;
		}

		string currentInputs = HashBuildInputs();
		if (currentInputs.Length == 0)
		{
			reason = "The build inputs beside the module could not be read.";
			return true;
		}

		if (!string.Equals(recordedInputs, currentInputs, StringComparison.OrdinalIgnoreCase))
		{
			reason = "The shipped game code has changed since the module was built — " +
				"unpacking an update over an install is exactly this, and running the old " +
				"module would silently keep every fix in that update out of the game.";
			return true;
		}

		// HARD MODS ARE COMPILED IN, so the module is only current for the native code it was
		// built with. Enabling, disabling, reordering or updating one changes this hash.
		string currentMods = HashNativeMods();
		if (!string.Equals(recordedMods, currentMods, StringComparison.OrdinalIgnoreCase))
		{
			reason = currentMods.Length == 0
				? "The mods with native code that the game was built with are no longer enabled."
				: "The enabled mods with native code have changed since the game code was built.";
			return true;
		}

		reason = string.Empty;
		return false;
	}

	/// <summary>
	/// The hard mods a build compiles: enabled, consented to, highest priority first.
	/// </summary>
	private static List<ModNativeSources> NativeModsForBuild(out List<string> withheld) =>
		NativeModConsent.ForBuild(Refii.Assets.Engine_GameAssets.ModsRoot, out withheld);

	/// <summary>
	/// The fingerprint of the native code mods add to this build; empty when they add none.
	/// </summary>
	/// <remarks>
	/// An unreadable mods folder hashes to a value no build records, so it reads as stale and
	/// the build that follows reports the real error, rather than passing as "no mods".
	/// </remarks>
	public static string HashNativeMods()
	{
		try
		{
			return ModNative.HashSet(NativeModsForBuild(out _));
		}
		catch (Exception exception)
		{
			return "unreadable: " + exception.Message;
		}
	}

	/// <summary>
	/// Extracts every enabled, consented hard mod's native sources beside the generated code and
	/// appends the compiled ones to <paramref name="sources"/>.
	/// </summary>
	/// <remarks>
	/// Four refusals, all before a single unit compiles, each naming the mod:
	/// <list type="bullet">
	/// <item>a native mod requires a mod that is missing, disabled or too old;</item>
	/// <item>a hook replaces a function's body while another owner hooks it beneath (two owners
	/// that both wrap are chained instead, see <see cref="ModNative"/>);</item>
	/// <item>a mod overrides a name the generator never declared — otherwise its hook defines a
	/// symbol nothing calls and the mod silently does nothing. The usual cause is a
	/// <c>sub_XXXXXXXX</c> the config has named, so the config's name is offered;</item>
	/// <item>an entry path that would leave the mod's folder.</item>
	/// </list>
	/// Runs after generation, so the declared set is this install's own.
	/// </remarks>
	private static Result AddNativeModSources(
		List<string> sources, Dictionary<string, List<string>> arguments, IReadOnlyList<string> hookSources,
		out string modsHash)
	{
		modsHash = string.Empty;
		List<ModNativeSources> mods;
		List<string> withheld;
		try
		{
			mods = NativeModsForBuild(out withheld);
		}
		catch (Exception exception)
		{
			return new Result(false, "The mods folder could not be read: " + exception.Message);
		}

		foreach (string name in withheld)
		{
			LogWrite($"mod '{name}': its native code is NOT compiled - its current sources were " +
				"never enabled in the launcher's Mods panel");
		}

		if (mods.Count == 0)
		{
			return new Result(true, string.Empty);
		}

		string modsRoot = Refii.Assets.Engine_GameAssets.ModsRoot;
		List<ModDependencyProblem> unmet = ModDependencies.Check(
			ModLoadOrder.Read(modsRoot), ModWorkspace.DiscoverModInstalls(modsRoot),
			mods.Select(mod => mod.ModName));
		if (unmet.Count > 0)
		{
			return new Result(false,
				"A mod with native code needs another mod that is not there:\n" +
				string.Join("\n", unmet.Select(problem => $"  {problem.Mod}: {problem.Message}")) +
				"\nEnable or install what it needs in the launcher's Mods panel.");
		}

		Dictionary<string, string> refiiOverrides = NativeModConsent.RefiiOverrides();
		List<ModNativeConflict> conflicts = ModNative.FindConflicts(mods, refiiOverrides);
		if (conflicts.Count > 0)
		{
			return new Result(false,
				"A mod replaces the body of a game function that another owner also hooks, so that " +
				"hook would never run:\n" +
				string.Join("\n", conflicts.Select(conflict =>
					$"  {conflict.Function}: {conflict.First} replaces it over {conflict.Second}")) +
				"\nDisable one of the mods named in the launcher's Mods panel.");
		}

		HashSet<string> declared = ReadDeclaredFunctions();
		if (declared.Count == 0)
		{
			return new Result(false,
				"The generated code declares no functions, so the mods' hooks cannot be checked.");
		}

		Dictionary<uint, string> configNames = ReadConfigFunctionNames();
		var unknown = new List<string>();
		foreach (ModNativeSources mod in mods)
		{
			foreach (string function in mod.Overrides)
			{
				if (declared.Contains(function))
				{
					continue;
				}

				string hint = function.StartsWith("sub_", StringComparison.Ordinal) &&
					uint.TryParse(function.AsSpan(4), System.Globalization.NumberStyles.HexNumber,
						null, out uint address) &&
					configNames.TryGetValue(address, out string? named)
						? $" (this build calls it {named})"
						: " (no such function in this build of the game)";
				unknown.Add($"  {mod.ModName}: {function}{hint}");
			}
		}

		if (unknown.Count > 0)
		{
			return new Result(false,
				"A mod replaces a game function that does not exist under that name, so its hook " +
				"would never run:\n" + string.Join("\n", unknown));
		}

		string extracted = Path.Combine(GeneratedDirectory, "mods");
		var compiledBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (ModNativeSources mod in mods)
		{
			string root = Path.Combine(extracted, mod.ModName);
			List<string> compiled = compiledBy[mod.ModName] = new List<string>();
			foreach (ModNativeFile file in mod.Files)
			{
				string target = Path.GetFullPath(Path.Combine(root, file.Path));
				if (!target.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
					StringComparison.Ordinal))
				{
					return new Result(false, $"mod '{mod.ModName}': '{file.Path}' leaves the mod's folder.");
				}

				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(target)!);
					File.WriteAllBytes(target, file.Bytes);
				}
				catch (Exception exception)
				{
					return new Result(false,
						$"mod '{mod.ModName}': could not write '{file.Path}': {exception.Message}");
				}

				if (ModNative.IsCompiled(file.Path))
				{
					sources.Add(target);
					compiled.Add(target);
					// A hard mod's own headers, for its own sources only: two mods may each ship a
					// "combat.h" and neither may see the other's. REFII_MOD_NAME is what the event
					// bus logs the mod's handlers under (core/refii_mod_events.h).
					AddArguments(arguments, target, new[] { "-I" + root, $"-DREFII_MOD_NAME=\"{mod.ModName}\"" });
				}
			}

			LogWrite($"mod '{mod.ModName}': compiling {mod.CompiledFiles.Count()} native source(s), " +
				$"hooking {string.Join(", ", mod.Overrides)}");
		}

		// A REQUIRED MOD'S PUBLIC HEADERS. What a mod puts under native/include/ is its API to the
		// mods that require it; nothing else of it is visible to them.
		Dictionary<string, ModInstall> installs = ModWorkspace.DiscoverModInstalls(modsRoot)
			.ToDictionary(mod => mod.Name, StringComparer.OrdinalIgnoreCase);
		foreach (ModNativeSources mod in mods)
		{
			if (!installs.TryGetValue(mod.ModName, out ModInstall? install))
			{
				continue;
			}

			foreach (ModRequirement requirement in install.Metadata.Requires)
			{
				ModNativeSources? required = mods.FirstOrDefault(other =>
					string.Equals(other.ModName, requirement.Name, StringComparison.OrdinalIgnoreCase));
				if (required == null)
				{
					continue;
				}

				string include = Path.Combine(extracted, required.ModName, "include");
				foreach (string source in compiledBy[mod.ModName])
				{
					AddArguments(arguments, source, new[] { "-I" + include });
				}
			}
		}

		// CHAINS. A function hooked by more than one owner gets each owner's hook under its own
		// symbol and one generated entry; see ModNative.
		List<ModNativeChain> chains = ModNative.PlanChains(mods, refiiOverrides);
		if (chains.Count > 0)
		{
			foreach (ModNativeSources mod in mods)
			{
				List<string> defines = ModNative.ChainDefines(chains, mod.ModName);
				foreach (string source in compiledBy[mod.ModName])
				{
					AddArguments(arguments, source, defines);
				}
			}

			foreach (string hookSource in hookSources)
			{
				List<string> defined;
				try
				{
					defined = ModNative.DeriveOverrides(File.ReadAllText(hookSource)).ToList();
				}
				catch (Exception)
				{
					continue;
				}

				List<string> defines = ModNative.ChainDefines(
					chains.Where(chain => defined.Contains(chain.Function)).ToList(), ModNative.RefiiOwner);
				AddArguments(arguments, hookSource, defines);
			}

			string chainFile = Path.Combine(extracted, "refii_mod_chains.cpp");
			try
			{
				Directory.CreateDirectory(extracted);
				File.WriteAllText(chainFile, ModNative.ChainSource(chains));
			}
			catch (Exception exception)
			{
				return new Result(false, $"could not write '{chainFile}': {exception.Message}");
			}

			sources.Add(chainFile);
			foreach (ModNativeChain chain in chains)
			{
				LogWrite($"chain {chain.Function}: {string.Join(" -> ", chain.Owners)} -> game");
			}
		}

		modsHash = ModNative.HashSet(mods);
		return new Result(true, string.Empty);
	}

	private static void AddArguments(
		Dictionary<string, List<string>> arguments, string source, IEnumerable<string> extra)
	{
		if (!arguments.TryGetValue(source, out List<string>? list))
		{
			arguments[source] = list = new List<string>();
		}

		foreach (string argument in extra)
		{
			if (!list.Contains(argument, StringComparer.Ordinal))
			{
				list.Add(argument);
			}
		}
	}

	/// <summary>Every function the generator emitted, from its <c>DECLARE_REX_FUNC(name)</c> lines.</summary>
	private static HashSet<string> ReadDeclaredFunctions()
	{
		var declared = new HashSet<string>(StringComparer.Ordinal);
		const string marker = "DECLARE_REX_FUNC(";
		foreach (string header in Directory.EnumerateFiles(GeneratedDirectory, "*.h"))
		{
			foreach (string line in File.ReadLines(header))
			{
				int at = line.IndexOf(marker, StringComparison.Ordinal);
				if (at < 0)
				{
					continue;
				}

				int start = at + marker.Length;
				int end = line.IndexOf(')', start);
				if (end > start)
				{
					declared.Add(line.Substring(start, end - start).Trim());
				}
			}
		}

		return declared;
	}

	/// <summary>The config's function names by address (<c>0x8272A270 = { name = "rex_…" }</c>).</summary>
	private static Dictionary<uint, string> ReadConfigFunctionNames()
	{
		var names = new Dictionary<uint, string>();
		string config = Path.Combine(VersionDirectory, "refii_config.toml");
		if (!File.Exists(config))
		{
			return names;
		}

		foreach (string line in File.ReadLines(config))
		{
			string trimmed = line.TrimStart();
			int nameAt = trimmed.IndexOf("name = \"", StringComparison.Ordinal);
			if (!trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || nameAt < 0)
			{
				continue;
			}

			int equals = trimmed.IndexOf('=');
			if (!uint.TryParse(trimmed.AsSpan(2, equals - 2).Trim(),
				System.Globalization.NumberStyles.HexNumber, null, out uint address))
			{
				continue;
			}

			int start = nameAt + "name = \"".Length;
			int end = trimmed.IndexOf('"', start);
			if (end > start)
			{
				names[address] = trimmed.Substring(start, end - start);
			}
		}

		return names;
	}

	/// <summary>
	/// A fingerprint of everything the module is COMPILED FROM, computed off the disk.
	/// </summary>
	/// <remarks>
	/// THE GAME HASH ALONE CANNOT SEE OUR OWN UPDATES. It catches a different disc or a title
	/// update, and nothing else — so a player who unpacked a new build over an old install kept
	/// running the module the OLD sources produced, silently, with no version anywhere that
	/// disagreed. Every fix we ship that touches a hook or a header was simply absent for them.
	///
	/// MEASURED, NOT STAMPED. A version written at package time would only describe what
	/// packaging believed it wrote; this hashes what is actually present, so it is right however
	/// the install got there — a zip over the top, a partial copy, a hand-edited header in a dev
	/// tree. Content only, never timestamps: unpacking a zip rewrites every mtime and would
	/// otherwise force a rebuild on every update whether or not anything changed.
	///
	/// Measured at ~0.8 s over 2,825 files. Paid once per launch, and only when there is already
	/// a module to judge.
	/// </remarks>
	public static string HashBuildInputs()
	{
		try
		{
			var files = new List<string>();

			foreach (string relative in new[] { "hooks", "include", "stdcxx" })
			{
				string directory = Path.Combine(RecompileDirectory, relative);
				if (Directory.Exists(directory))
				{
					files.AddRange(Directory.EnumerateFiles(
						directory, "*", SearchOption.AllDirectories));
				}
			}

			foreach (string name in new[] { "hook_sources.cmake", "include_dirs.cmake" })
			{
				string file = Path.Combine(RecompileDirectory, name);
				if (File.Exists(file))
				{
					files.Add(file);
				}
			}

			// The manifest and config the generator reads for THIS install's version. They
			// decide which functions exist and what they are called, so a config change is a
			// code change even when no hook source moved.
			if (Directory.Exists(VersionDirectory))
			{
				files.AddRange(Directory.EnumerateFiles(
					VersionDirectory, "*", SearchOption.AllDirectories));
			}

			if (files.Count == 0)
			{
				return string.Empty;
			}

			// Sorted by the path RELATIVE to the recompile directory, and ordinally, so the
			// fingerprint does not move when the install does or when a different filesystem
			// hands the enumeration back in another order.
			files.Sort((a, b) => string.CompareOrdinal(
				Path.GetRelativePath(RecompileDirectory, a).Replace('\\', '/'),
				Path.GetRelativePath(RecompileDirectory, b).Replace('\\', '/')));

			using var sha = Refii.Core.Engine_Hash.CreateSha256();
			var buffer = new byte[64 * 1024];

			foreach (string file in files)
			{
				// The PATH is hashed as well as the bytes: renaming a header, or the include
				// renumbering that moved src/vfs/include from slot 15 to 16, changes what the
				// compiler resolves without changing any file's contents.
				byte[] name = Encoding.UTF8.GetBytes(
					Path.GetRelativePath(RecompileDirectory, file).Replace('\\', '/') + "\n");
				sha.TransformBlock(name, 0, name.Length, null, 0);

				using FileStream stream = File.OpenRead(file);
				int read;
				while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
				{
					sha.TransformBlock(buffer, 0, read, null, 0);
				}
			}

			// The compile flags are an input too: a module built for instructions this CPU
			// lacks would otherwise stay until some source happened to change. Paths are left
			// out, as above, so moving the install does not move the fingerprint.
			List<string> compileFlags = CompileFlags(Array.Empty<string>());
			var options = new StringBuilder();
			for (int i = 0; i < compileFlags.Count; i++)
			{
				if (compileFlags[i] == "-include")
				{
					i++;
					continue;
				}
				if (compileFlags[i].StartsWith("-I", StringComparison.Ordinal))
				{
					continue;
				}
				options.Append(compileFlags[i]).Append('\n');
			}
			// -march=native ties the module to this CPU, so its feature bits are an input as
			// well: an install copied to a machine without, say, AVX2 rebuilds instead of
			// loading instructions it cannot run. Leaf 1 EBX is left out; it carries the APIC
			// id of whichever core this thread is on.
			if (X86Base.IsSupported)
			{
				(_, _, int ecx1, int edx1) = X86Base.CpuId(1, 0);
				(_, int ebx7, int ecx7, int edx7) = X86Base.CpuId(7, 0);
				(_, _, int ecxExt, int edxExt) = X86Base.CpuId(unchecked((int)0x80000001), 0);
				options.Append($"cpu {ecx1:X8} {edx1:X8} {ebx7:X8} {ecx7:X8} {edx7:X8} {ecxExt:X8} {edxExt:X8}\n");
			}
			byte[] optionBytes = Encoding.UTF8.GetBytes(options.ToString());
			sha.TransformBlock(optionBytes, 0, optionBytes.Length, null, 0);

			sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
			return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
		}
		catch (Exception)
		{
			// Deliberately NOT a silent pass: an unreadable input tree returns empty, and the
			// caller treats empty as "cannot prove it is current", which rebuilds.
			return string.Empty;
		}
	}

	public static string HashGameExecutable()
	{
		try
		{
			// The executable AND its title update: the generator recompiles the patched image
			// when the patch is present, so adding or removing it changes the code produced
			// without changing a byte of default.xex.
			using var sha = Refii.Core.Engine_Hash.CreateSha256();
			byte[] executable = File.ReadAllBytes(GameExecutablePath);
			sha.TransformBlock(executable, 0, executable.Length, null, 0);
			byte[] update = File.Exists(GameTitleUpdatePath)
				? File.ReadAllBytes(GameTitleUpdatePath)
				: Array.Empty<byte>();
			sha.TransformFinalBlock(update, 0, update.Length);
			return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	private static readonly SemaphoreSlim WindowsInstallInputsGate = new(1, 1);

	private readonly record struct WindowsBootstrapTools(
		string CMake, string Ninja, string CCompiler, string CxxCompiler,
		IReadOnlyList<string> SearchDirectories);

	private static bool WindowsInstallInputsMissing()
	{
		string recompile = RecompileDirectory;

		return !File.Exists(Path.Combine(BinDirectory,
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_hostbridge"))) ||
			!File.Exists(Path.Combine(BinDirectory,
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_core"))) ||
			!File.Exists(Path.Combine(BinDirectory,
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_vfs"))) ||
			!File.Exists(Path.Combine(BinDirectory,
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_binkdecode"))) ||
			!File.Exists(Path.Combine(BinDirectory,
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_xmadecode"))) ||
			!AnyFileExists(BinDirectory,
				"rexruntime.dll", "rexruntimed.dll", "rexruntimerd.dll",
				"librexruntime.dll", "librexruntimed.dll", "librexruntimerd.dll") ||
			!File.Exists(Path.Combine(BinDirectory,
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_recompiler"))) ||
			FindShippedLibrary("rexruntime").Length == 0 ||
			FindShippedLibrary("refii_core").Length == 0 ||
			FindShippedLibrary("refii_vfs").Length == 0 ||
			!DirectoryHasFiles(Path.Combine(recompile, "hooks")) ||
			!DirectoryHasFiles(Path.Combine(recompile, "include")) ||
			!File.Exists(Path.Combine(recompile, "include_dirs.cmake")) ||
			!File.Exists(Path.Combine(recompile, "hook_sources.cmake")) ||
			!File.Exists(Path.Combine(VersionDirectory, "refii_manifest.toml")) ||
			!File.Exists(Path.Combine(VersionDirectory, "refii_config.toml")) ||
			!File.Exists(Path.Combine(recompile, "src", "payload_export.cpp")) ||
			!File.Exists(Path.Combine(recompile, "zig", "zig.exe")) ||
			!Directory.Exists(Path.Combine(recompile, "zig", "lib"));
	}

	private static bool WindowsInstallInputsModeEnabled(string buildDirectory)
	{
		try
		{
			string cache = Path.Combine(buildDirectory, "CMakeCache.txt");
			foreach (string line in File.ReadLines(cache))
			{
				if (!line.StartsWith("REFII_WINDOWS_INSTALL_INPUTS_ONLY:",
						StringComparison.Ordinal))
				{
					continue;
				}

				int separator = line.IndexOf('=');
				return separator >= 0 && string.Equals(
					line[(separator + 1)..].Trim(), "ON", StringComparison.OrdinalIgnoreCase);
			}
		}
		catch (Exception)
		{
		}

		return false;
	}

	private static bool AnyFileExists(string directory, params string[] names)
	{
		foreach (string name in names)
		{
			if (File.Exists(Path.Combine(directory, name)))
			{
				return true;
			}
		}
		return false;
	}

	private static bool DirectoryHasFiles(string directory)
	{
		try
		{
			return Directory.Exists(directory) && Directory.EnumerateFiles(
				directory, "*", SearchOption.AllDirectories).Any();
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static async Task<Result> EnsureWindowsInstallInputsAsync(
		Action<Progress> progress, CancellationToken cancellation)
	{
		bool acquired = false;
		try
		{
			await WindowsInstallInputsGate.WaitAsync(cancellation).ConfigureAwait(false);
			acquired = true;
			if (!WindowsInstallInputsMissing())
			{
				return new Result(true, string.Empty);
			}

			if (!TryResolveWindowsSourceRoot(out string sourceRoot, out string sourceError))
			{
				return new Result(false, sourceError);
			}

			if (!TryResolveWindowsBootstrapTools(out WindowsBootstrapTools tools,
					out string toolsError))
			{
				return new Result(false, toolsError);
			}

			string arch = Refii.Core.Engine_AppRoot.ArchFolder;
			string buildDirectory = Path.Combine(
				sourceRoot, "out", "build", "windows-godot-install-inputs-" + arch);

			var configureArguments = new List<string>
			{
				"-S", sourceRoot,
				"-B", buildDirectory,
				"-G", "Ninja Multi-Config",
				"-DCMAKE_MAKE_PROGRAM=" + tools.Ninja,
				"-DCMAKE_C_COMPILER=" + tools.CCompiler,
				"-DCMAKE_CXX_COMPILER=" + tools.CxxCompiler,
				"-DCMAKE_CONFIGURATION_TYPES=RelWithDebInfo",
				"-DREFII_WINDOWS_INSTALL_INPUTS_ONLY=ON",
				"-DREFII_PACKAGE=OFF",
			};
			if (string.Equals(arch, "x86_64", StringComparison.OrdinalIgnoreCase))
			{
				// The same target as the shipped Windows libraries (windows-base in
				// CMakePresets.json): x86-64-v2, so a CPU without AVX2 or FMA3 runs them.
				configureArguments.Add("-DCMAKE_C_FLAGS=-march=x86-64-v2");
				configureArguments.Add("-DCMAKE_CXX_FLAGS=-march=x86-64-v2");
			}

			LogPhase("prepare-windows-install-inputs");
			progress(new Progress("Preparing the Windows game-code components...", 0.005f));
			Result configured = default;
			for (int attempt = 0; attempt < 2; attempt++)
			{
				configured = await RunStepAsync(
					tools.CMake,
					configureArguments,
					sourceRoot,
					line => ReportBootstrapLine(
						progress, "Configuring Windows components", line, 0.008f),
					cancellation,
					tools.SearchDirectories).ConfigureAwait(false);
				if (!configured.Ok)
				{
					return new Result(false,
						"Could not configure the Windows game-code components automatically.\n\n" +
						configured.Error);
				}

				if (WindowsInstallInputsModeEnabled(buildDirectory))
				{
					break;
				}

				progress(new Progress(
					"Finalizing the Windows game-code configuration...", 0.009f));
			}
			if (!WindowsInstallInputsModeEnabled(buildDirectory))
			{
				return new Result(false,
					"CMake did not retain the Windows install-input build mode in " +
					Path.Combine(buildDirectory, "CMakeCache.txt") + ".");
			}

			var buildArguments = new List<string>
			{
				"--build", buildDirectory,
				"--config", "RelWithDebInfo",
				"--target", "refii_windows_install_inputs",
				"--parallel", JobCount().ToString(),
			};
			progress(new Progress("Building the Windows game-code components...", 0.01f));
			Result built = await RunStepAsync(
				tools.CMake,
				buildArguments,
				sourceRoot,
				line => ReportBootstrapLine(progress, "Building Windows components", line, 0.015f),
				cancellation,
				tools.SearchDirectories).ConfigureAwait(false);
			if (!built.Ok)
			{
				return new Result(false,
					"Could not build the Windows game-code components automatically.\n\n" +
					built.Error);
			}

			if (WindowsInstallInputsMissing())
			{
				return new Result(false,
					"The Windows component build finished, but the install-time game-code " +
					"inputs were not completely staged at " + RecompileDirectory + ".");
			}

			return new Result(true, string.Empty);
		}
		catch (OperationCanceledException)
		{
			return new Result(false, "cancelled");
		}
		finally
		{
			if (acquired)
			{
				WindowsInstallInputsGate.Release();
			}
		}
	}

	private static void ReportBootstrapLine(
		Action<Progress> progress, string phase, string line, float fraction)
	{
		string status = line.Trim();
		if (status.Length == 0)
		{
			return;
		}
		if (status.Length > 180)
		{
			status = status[..177] + "...";
		}

		progress(new Progress(phase + "...\n" + status, fraction));
	}

	private static bool TryResolveWindowsSourceRoot(out string sourceRoot, out string error)
	{
		sourceRoot = string.Empty;
		error = string.Empty;

		try
		{
			string projectDirectory = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(Godot.ProjectSettings.GlobalizePath("res://")));
			string? parent = Directory.GetParent(projectDirectory)?.FullName;
			if (string.IsNullOrEmpty(parent))
			{
				error = "Could not resolve the source checkout above the Godot project.";
				return false;
			}

			string expectedBin = Path.Combine(
				projectDirectory, "build", Refii.Core.Engine_AppRoot.ArchFolder, "bin");
			if (!string.Equals(Path.GetFullPath(BinDirectory), Path.GetFullPath(expectedBin),
					StringComparison.OrdinalIgnoreCase))
			{
				error = "The Windows install inputs are missing, but this editor run is not " +
					"using the source checkout's Godot/build staging directory.";
				return false;
			}

			foreach (string required in new[]
			         {
				         "CMakeLists.txt", "CMakePresets.json",
				         Path.Combine("cmake", "refii_finalize_windows_install_inputs.cmake"),
				         Path.Combine("rexglue-net", "rexglue-sdk", "CMakeLists.txt"),
				         Path.Combine("src", "extractor", "thirdparty", "zlib", "CMakeLists.txt"),
			         })
			{
				if (!File.Exists(Path.Combine(parent!, required)))
				{
					error = "This clean Windows run needs the complete refii source checkout; " +
						"missing " + Path.Combine(parent!, required) + ".";
					return false;
				}
			}

			sourceRoot = parent!;
			return true;
		}
		catch (Exception exception)
		{
			error = "Could not resolve the Windows source checkout: " + exception.Message;
			return false;
		}
	}

	private static bool TryResolveWindowsBootstrapTools(
		out WindowsBootstrapTools tools, out string error)
	{
		tools = default;
		error = string.Empty;

		var visualStudioRoots = FindVisualStudioRoots();
		string cmake = FindOnPath("cmake.exe");
		if (cmake.Length == 0)
		{
			foreach (string root in visualStudioRoots)
			{
				string candidate = Path.Combine(root, "Common7", "IDE", "CommonExtensions",
					"Microsoft", "CMake", "CMake", "bin", "cmake.exe");
				if (File.Exists(candidate))
				{
					cmake = candidate;
					break;
				}
			}
		}

		string ninja = FindOnPath("ninja.exe");
		if (ninja.Length == 0 && cmake.Length != 0)
		{
			string? cmakeDirectory = Path.GetDirectoryName(cmake);
			if (!string.IsNullOrEmpty(cmakeDirectory))
			{
				string candidate = Path.GetFullPath(
					Path.Combine(cmakeDirectory!, "..", "..", "Ninja", "ninja.exe"));
				if (File.Exists(candidate))
				{
					ninja = candidate;
				}
			}
		}
		if (ninja.Length == 0)
		{
			foreach (string root in visualStudioRoots)
			{
				string candidate = Path.Combine(root, "Common7", "IDE", "CommonExtensions",
					"Microsoft", "CMake", "Ninja", "ninja.exe");
				if (File.Exists(candidate))
				{
					ninja = candidate;
					break;
				}
			}
		}

		var compilerDirectories = new List<string>();
		foreach (string root in visualStudioRoots)
		{
			compilerDirectories.Add(Path.Combine(root, "VC", "Tools", "Llvm", "x64", "bin"));
			compilerDirectories.Add(Path.Combine(root, "VC", "Tools", "Llvm", "bin"));
		}
		string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		if (programFiles.Length != 0)
		{
			compilerDirectories.Add(Path.Combine(programFiles, "LLVM", "bin"));
		}

		string clang = string.Empty;
		string clangxx = string.Empty;
		foreach (string directory in compilerDirectories)
		{
			string candidateC = Path.Combine(directory, "clang.exe");
			string candidateCxx = Path.Combine(directory, "clang++.exe");
			if (File.Exists(candidateC) && File.Exists(candidateCxx))
			{
				clang = candidateC;
				clangxx = candidateCxx;
				break;
			}
		}
		if (clang.Length == 0)
		{
			string pathClang = FindOnPath("clang.exe");
			string pathClangxx = FindOnPath("clang++.exe");
			if (!pathClang.Contains("mingw", StringComparison.OrdinalIgnoreCase) &&
					!pathClangxx.Contains("mingw", StringComparison.OrdinalIgnoreCase))
			{
				clang = pathClang;
				clangxx = pathClangxx;
			}
		}

		var missing = new List<string>();
		if (cmake.Length == 0) missing.Add("CMake");
		if (ninja.Length == 0) missing.Add("Ninja");
		if (clang.Length == 0 || clangxx.Length == 0) missing.Add("MSVC-compatible Clang");
		if (missing.Count != 0)
		{
			error = "The clean Windows checkout needs " + string.Join(", ", missing) +
				" to prepare its native game-code components, and they could not be found. " +
				"Install the Visual Studio C++ CMake tools and LLVM, then run the build again.";
			return false;
		}

		var searchDirectories = new List<string>();
		foreach (string executable in new[] { cmake, ninja, clang, clangxx })
		{
			string? directory = Path.GetDirectoryName(executable);
			if (!string.IsNullOrEmpty(directory) &&
					!searchDirectories.Exists(item => string.Equals(
						item, directory, StringComparison.OrdinalIgnoreCase)))
			{
				searchDirectories.Add(directory!);
			}
		}

		tools = new WindowsBootstrapTools(cmake, ninja, clang, clangxx, searchDirectories);
		return true;
	}

	private static List<string> FindVisualStudioRoots()
	{
		var result = new List<string>();
		var programRoots = new List<string>
		{
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
		};

		foreach (string programRoot in programRoots)
		{
			if (programRoot.Length == 0)
			{
				continue;
			}
			string visualStudio = Path.Combine(programRoot, "Microsoft Visual Studio");
			foreach (string version in SafeEnumerateDirectories(visualStudio))
			{
				foreach (string edition in SafeEnumerateDirectories(version))
				{
					if (!result.Exists(item => string.Equals(
							item, edition, StringComparison.OrdinalIgnoreCase)))
					{
						result.Add(edition);
					}
				}
			}
		}

		return result;
	}

	private static IReadOnlyList<string> SafeEnumerateDirectories(string directory)
	{
		try
		{
			return Directory.Exists(directory)
				? Directory.GetDirectories(directory)
				: Array.Empty<string>();
		}
		catch (Exception)
		{
			return Array.Empty<string>();
		}
	}

	private static string FindOnPath(string executable)
	{
		string searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		foreach (string rawDirectory in searchPath.Split(Path.PathSeparator))
		{
			string directory = rawDirectory.Trim().Trim('"');
			if (directory.Length == 0)
			{
				continue;
			}
			try
			{
				string candidate = Path.Combine(directory, executable);
				if (File.Exists(candidate))
				{
					return Path.GetFullPath(candidate);
				}
			}
			catch (Exception)
			{
			}
		}

		return string.Empty;
	}

	/// <summary>
	/// Runs the whole pipeline. Reports progress as a status line and a 0..1 fraction.
	/// </summary>
	/// <remarks>
	/// <paramref name="control"/> is where a pause holds the build and a stop abandons it; see
	/// <see cref="BuildControl"/> for exactly where each takes effect. A stop comes back as a
	/// <see cref="Result"/> with <c>Stopped</c> set, never as an error: the partial output is
	/// deleted here, the module was removed before generation started, and the next build is a
	/// full build regardless.
	/// </remarks>
	public static async Task<Result> RunAsync(Action<Progress> progress, BuildControl control)
	{
		CancellationToken cancellation = control.StopToken;
		Result result;
		try
		{
			result = await RunPipelineAsync(progress, control, cancellation).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			result = StoppedResult;
		}

		// A step that fails because its process was killed reports the kill as its error; the
		// token says which it was.
		if (result.Ok || (!result.Stopped && !cancellation.IsCancellationRequested))
		{
			return result;
		}

		LogPhase("stopped");
		progress(new Progress("Game code build stopped.", 0.0f));

		// The sources and objects of a build that will never finish. Best effort: on Windows a
		// killed compiler can still hold a handle for a moment, and the next build's clean step
		// removes whatever this could not, loudly if it cannot either.
		try
		{
			if (Directory.Exists(GeneratedDirectory))
			{
				Directory.Delete(GeneratedDirectory, recursive: true);
			}
		}
		catch (Exception exception)
		{
			LogWrite("could not remove the stopped build's output: " + exception.Message);
		}

		InvalidateStatus();
		return StoppedResult;
	}

	private static async Task<Result> RunPipelineAsync(
		Action<Progress> progress, BuildControl control, CancellationToken cancellation)
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

		if (OperatingSystem.IsWindows() && !Refii.Core.Engine_AppRoot.IsExported)
		{
			Result prepared = await EnsureWindowsInstallInputsAsync(
				progress, cancellation).ConfigureAwait(false);
			if (!prepared.Ok)
			{
				return prepared;
			}
		}

		// EVERYTHING missing, in one message. Reported one at a time this takes a build
		// attempt per fault to discover, and each attempt looks like "it just doesn't work".
		string generator = Path.Combine(BinDirectory,
			Refii.Core.Engine_NativeLibraries.FileNameFor("refii_recompiler"));

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

			if (FindShippedLibrary("rexruntime").Length == 0)
			{
				missing.Add("the link libraries (" + BinDirectory + ")");
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
				". These ship beside the app under bin/ — if you copied the " +
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

		// 1. Generate, in this process. The fraction advances only per phase because the
		//    generator does not report a count we could scale. Its log lines go to the
		//    launcher log, as the executable's output did; its phases drive the status line.
		LogPhase("generate");
		Result generated = await Task.Run(
			() => Recompiler.Generate(
				manifest,
				control,
				line => progress(new Progress("Generating game code... " + line, 0.05f)),
				LogWrite),
			cancellation).ConfigureAwait(false);
		if (!generated.Ok)
		{
			LogWrite("generate failed: " + generated.Error);
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

		// EVERY LISTED SOURCE MUST BE ON DISK. The manifest and the sources are extracted
		// separately, and a zip unpacked part-way (one file skipped, an extraction stopped early)
		// leaves a name in the list with nothing behind it. zig reports that as
		// "<file>:1:1: error: CacheCheckFailed" — its cache check could not open the input — which
		// reads as a compiler fault in the hook. A fresh Windows install hit exactly this on one
		// hook source of 85. Named here instead, with the fix.
		var absentSources = new List<string>();
		foreach (string source in hookSources)
		{
			if (!File.Exists(source))
			{
				absentSources.Add(Path.GetRelativePath(RecompileDirectory, source).Replace('\\', '/'));
			}
		}

		if (absentSources.Count > 0)
		{
			return new Result(false,
				"This install is missing " + absentSources.Count + " of the game code's source files: " +
				string.Join(", ", absentSources) + ". The download was not fully extracted — extract " +
				"the whole zip again over this folder, or reinstall, then build again.");
		}

		sources.AddRange(hookSources);

		// HARD MODS: their native sources compile in this same step, after refii's hooks, with the
		// same compiler and flags. The link takes every object by index, so it needs nothing more.
		var modArguments = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		Result modded = AddNativeModSources(sources, modArguments, hookSources, out string compiledModsHash);
		if (!modded.Ok)
		{
			return modded;
		}

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
		await control.WaitWhilePausedAsync(cancellation).ConfigureAwait(false);
		LogPhase("precompile");
		progress(new Progress("Preparing the game code build...", 0.36f));

		// ReXGlue 0.10 splits the surface: every recomp TU includes refii_pch.h (config, SDK
		// headers, the function macros) plus a per-file declaration header, and refii_init.h
		// is the full surface only the init/register TUs and our hooks take. The pch header
		// is what precompiles; refii_init.h stays as the fallback for an older generator.
		string header = Path.Combine(GeneratedDirectory, "refii_pch.h");
		if (!File.Exists(header))
		{
			header = Path.Combine(GeneratedDirectory, "refii_init.h");
		}

		string pch = Path.Combine(BuildDirectory, Path.GetFileNameWithoutExtension(header) + ".pch");
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

			(int pchCode, string pchOutput) = await RunCaptureAsync(
				resolved.Zig, Prefixed("c++", pchArguments), control, cancellation);

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
			resolved, sources, flags, modArguments, pchUsed, progress, control, cancellation);
		if (!compiled.Ok)
		{
			return new Result(false, "Compiling the game code failed: " + compiled.Error);
		}

		// 4. Link.
		await control.WaitWhilePausedAsync(cancellation).ConfigureAwait(false);
		LogPhase("link");
		progress(new Progress("Linking the game module...", 0.95f));
		Result linked = await LinkAsync(resolved, sources.Count, control, cancellation);
		if (!linked.Ok)
		{
			return new Result(false, "Linking the game module failed: " + linked.Error);
		}

		if (!File.Exists(ModulePath))
		{
			return new Result(false,
				"The build reported success but produced no module at " + ModulePath + ".");
		}

		WriteBuildInfo(resolved, compiledModsHash);

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
		string source = Path.Combine(VersionDirectory, "refii_manifest.toml");
		if (!File.Exists(source))
		{
			error = "The code generation manifest for " + GameVersionName +
					" is missing from this install (" + source + ").";
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
			// By file NAME, beside the manifest that names it: an include sits in its
			// version's folder, and the repo path it is written as does not exist on a
			// player's machine.
			absolute.Add("\"" +
				Escape(Path.Combine(VersionDirectory, Path.GetFileName(name))) + "\"");
		}

		rewritten.AppendLine("includes = [" + string.Join(", ", absolute) + "]");
	}

	private static string Escape(string path) => path.Replace("\\", "\\\\");

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
			// -march=native: the module is built on the machine that runs it, so it targets this
			// CPU. Stated outright because zig only defaults to the host CPU when no -m feature
			// flag is given; -msse4.1 alone drops it to baseline x86-64, and std::fma then calls
			// compiler-rt's software fma (66% of the XAudio render thread, measured). Forcing
			// -mfma instead would not start on a CPU without FMA3.
			flags.Add("-march=native");
			flags.Add("-msse4.1");
		}

		if (!OperatingSystem.IsWindows())
		{
			flags.Add("-mcmodel=large");
		}
		else
		{
			// Guest SEH scopes emit __try/__except, which traps hardware faults only under
			// asynchronous EH; the SDK's own recomp targets set the same flag (REXGLUE_RECOMP_OPTIONS
			// in generated/rexglue.cmake). zig c++ is clang, so it is the GNU spelling.
			flags.Add("-fasync-exceptions");
		}

		// MATCHED TO THE PAYLOAD TARGETS IN OUR OWN BUILD. The hook sources now compile
		// here, so the definitions that make them payload-shaped have to be stated here too:
		// logging crosses as C rather than as spdlog, and Tracy is absent. Omitting either
		// produces a module that imports C++ symbols again and fails to link on Windows.
		flags.Add("-DREFII_PAYLOAD_LOG_BRIDGE=1");

		// Which executable this payload is recompiled from. guest_build_stamp.cpp states it to
		// the runtime at load, which is how the hooks and the host bridge pick per-build values.
		if (GameVersionName == "Fable2")
		{
			flags.Add("-DREFII_GUEST_BUILD_UNPATCHED=1");
		}

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
	/// SO THE C++ HEADERS ARE -I, NOT -isystem. On a host with no glibc headers of its own
	/// (SteamOS ships none in /usr/include) zig falls back to its bundled libc and emits
	/// -isystem for those directories AHEAD of anything given here; clang then drops our
	/// later copies as duplicates, which leaves every libc directory before the C++ ones
	/// and nothing for the #include_next to find. -I is searched before all -isystem, so
	/// the order holds whichever libc zig picks. libstdc++'s headers mark themselves
	/// system headers, so nothing is lost in diagnostics.
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

			flags.Add("-I");
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
		IReadOnlyDictionary<string, List<string>> sourceArguments,
		string precompiledHeader,
		Action<Progress> progress,
		BuildControl control,
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

				// BEFORE THE PERMIT. The compilers already running are frozen by the pause
				// itself (RunCaptureOnceAsync attaches each to the control); this keeps new ones
				// from starting, and waiting with a permit held would keep that permit from a
				// resumed unit for no reason.
				await control.WaitWhilePausedAsync(cancellation).ConfigureAwait(false);

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

					List<string> BuildArguments(bool withPch)
					{
						var arguments = new List<string>(flags);
						if (withPch)
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

						// Per-source: a hard mod's own and required headers, and the chain defines
						// that rename a hook shared with another owner (AddNativeModSources).
						if (sourceArguments.TryGetValue(sources[slot], out List<string>? extra))
						{
							arguments.AddRange(extra);
						}

						arguments.Add("-c");
						arguments.Add(sources[slot]);
						arguments.Add("-o");
						arguments.Add(ObjectPath(slot));
						return arguments;
					}

					bool usePch = precompiledHeader.Length != 0
						&& Volatile.Read(ref pchBroken) == 0;

					(int code, string output) = await RunCaptureAsync(
						zig.Zig, Prefixed("c++", BuildArguments(usePch)), control, cancellation)
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
							zig.Zig, Prefixed("c++", BuildArguments(false)), control, cancellation)
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
		Toolchain zig, int objectCount, BuildControl control, CancellationToken cancellation)
	{
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
			string found = FindShippedLibrary(name);
			if (found.Length == 0)
			{
				return new Result(false,
					"the " + name + " library is missing from " + BinDirectory);
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
			zig.Zig, Prefixed("c++", LinkArguments(arguments)), control, cancellation)
			.ConfigureAwait(false);

		return code == 0
			? new Result(true, string.Empty)
			: new Result(false, output);
	}

	/// <summary>
	/// The link arguments, moved into a response file on Windows.
	/// </summary>
	/// <remarks>
	/// CreateProcess TAKES AT MOST 32767 CHARACTERS for the whole command line, and this link
	/// names every object by absolute path -- about 150 characters apiece in a real install, so
	/// a few hundred translation units clear the limit between them. .NET then raises Win32
	/// error 206, "The filename or extension is too long", from Process.Start before zig is
	/// reached at all, which reads as the build crashing with nothing compiled wrong.
	///
	/// Linux has no comparable ceiling -- ARG_MAX is megabytes -- so the same link has always
	/// been fine there and is handed through untouched.
	///
	/// EVERY ENTRY IS QUOTED: the install path routinely contains spaces. Backslashes inside
	/// the quotes are literal to zig's parser rather than escapes, so Windows paths need
	/// nothing further, and a double quote cannot occur in a Windows filename.
	///
	/// The file goes in BuildDirectory, which phase 0 clears and a successful build deletes, so
	/// it outlives the build only when the link failed. Its contents are logged too, because
	/// the command line no longer carries them.
	/// </remarks>
	private static IReadOnlyList<string> LinkArguments(IReadOnlyList<string> arguments)
	{
		if (!OperatingSystem.IsWindows())
		{
			return arguments;
		}

		var body = new StringBuilder();
		foreach (string argument in arguments)
		{
			body.Append('"').Append(argument).Append('"').Append('\n');
		}

		string path = Path.Combine(BuildDirectory, "link.rsp");
		Directory.CreateDirectory(BuildDirectory);
		File.WriteAllText(path, body.ToString());
		LogWrite("link response file " + path + ":");
		LogWrite(body.ToString());
		return new[] { "@" + path };
	}

	/// <summary>
	/// Finds a shipped library by base name in bin/, or "".
	/// </summary>
	/// <remarks>
	/// bin/ is the ONE directory for natives: the host loads the runtime and the refii
	/// libraries from it, the generator library imports the runtime from it, and the
	/// payload links the same files. There used to be a second copy of each under
	/// recompile/lib for the link, and the two drifting apart is how the payload once
	/// imported a four-hour-old runtime while the host loaded the current one.
	/// </remarks>
	private static string FindShippedLibrary(string name) => FindLibrary(BinDirectory, name);

	/// <summary>
	/// Finds a library by base name in one directory, in either build configuration.
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

		// ONE CONFIGURATION SHOULD BE PRESENT, and the staging prunes bin/ so that it is.
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
	private static void WriteBuildInfo(Toolchain zig, string modsHash)
	{
		try
		{
			var record = new Dictionary<string, string>
			{
				["game_hash"] = HashGameExecutable(),
				["build_inputs"] = HashBuildInputs(),
				["mods_hash"] = modsHash,
				["compiler"] = "zig " + Toolchain.Version(zig),
				["generator"] = "rexglue " + Recompiler.SdkVersion(),
				["platform"] = RuntimeInformation.RuntimeIdentifier,
				["built_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
			};

			File.WriteAllText(BuildInfoPath, JsonSerializer.Serialize(
				record, new JsonSerializerOptions
				{
					WriteIndented = true,
					TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
				}));
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
		string executable, IReadOnlyList<string> arguments, BuildControl control,
		CancellationToken cancellation)
	{
		for (int attempt = 0; ; attempt++)
		{
			(int Code, string Output) result = await RunCaptureOnceAsync(
				executable, arguments, control, cancellation).ConfigureAwait(false);

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

	/// <summary>
	/// Runs a process to completion and returns its exit code and output. Attached to
	/// <paramref name="control"/> while it runs, so a pause freezes it and a resume thaws it.
	/// </summary>
	private static async Task<(int Code, string Output)> RunCaptureOnceAsync(
		string executable, IReadOnlyList<string> arguments, BuildControl control,
		CancellationToken cancellation)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = executable,
			WorkingDirectory = RecompileDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,

			// NO CONSOLE WINDOW. Redirecting both streams is not enough on Windows: the
			// launcher is a GUI-subsystem process and zig is a console one, so
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

			control.Attach(process);
			Task<string> output = process.StandardOutput.ReadToEndAsync();
			Task<string> errors = process.StandardError.ReadToEndAsync();
			try
			{
				await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				control.Detach(process);
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

			control.Detach(process);
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
		CancellationToken cancellation,
		IReadOnlyList<string>? additionalPathDirectories = null)
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

		// Only the Windows bootstrap uses this, and its tools may live outside PATH (the
		// copies Visual Studio bundles): cmake finds ninja and the compiler by name, so their
		// directories go ahead of the inherited PATH.
		if (additionalPathDirectories is not null && additionalPathDirectories.Count > 0)
		{
			string existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
			var searchDirectories = new List<string>(additionalPathDirectories);
			if (existing.Length != 0)
			{
				searchDirectories.Add(existing);
			}
			startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, searchDirectories);
		}

		// The tail of the output, kept so a failure can say what actually went wrong
		// instead of only an exit code. Bounded: a failing compile can produce megabytes.
		var recent = new Queue<string>();

		// The WHOLE output, for the log. The 40-line tail above is what a person can be shown
		// in a dialog; it is not enough to diagnose a generator that printed the real cause
		// early and then kept going. Buffered and written once rather than appended per line,
		// because this can be thousands of lines and reopening the file for each is slower
		// than the generator itself.
		var transcript = new StringBuilder();

		// LOCKED, because two threads call this. `BeginOutputReadLine` and `BeginErrorReadLine`
		// each raise their events on their own pool thread, and both land here — so `recent` and
		// `transcript` were being mutated concurrently. A `Queue<T>` growing under that races on
		// its backing array and throws `Destination array was not long enough` out of
		// `SetCapacity`, on a pool thread, where nothing catches it: the whole app went down
		// mid-codegen with the build half done.
		object gate = new();

		void Record(string? line)
		{
			if (line is null)
			{
				return;
			}

			lock (gate)
			{
				transcript.AppendLine(line);

				recent.Enqueue(line);
				while (recent.Count > 40)
				{
					recent.Dequeue();
				}
			}

			// Outside the lock: the callback draws progress, and holding a lock across it would
			// let a slow paint stall the process's own output pipe.
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

			// The transcript goes to the log whether this succeeded or not: a tool that exits 0
			// without producing what it was run for is a failure this pipeline has hit, and the
			// reason is in the output, not the exit code.
			//
			// Read under the same lock the writers take — the reader threads are drained by
			// WaitForExit() by now, but taking it costs nothing and keeps one rule for the pair.
			string full;
			string tail;
			lock (gate)
			{
				full = transcript.ToString();
				tail = string.Join('\n', recent);
			}

			LogWrite("output of " + Path.GetFileName(executable) + Environment.NewLine + full);

			if (process.ExitCode != 0)
			{
				LogWrite("exit " + process.ExitCode);
				return new Result(false, "exit code " + process.ExitCode + "\n" + tail);
			}

			LogWrite("exit 0");
			return new Result(true, string.Empty);
		}
		catch (Exception exception)
		{
			string failed;
			lock (gate)
			{
				failed = transcript.ToString();
			}

			LogWrite("output of " + Path.GetFileName(executable) + Environment.NewLine + failed);
			LogWrite("threw: " + exception);
			return new Result(false, exception.Message);
		}
	}
}
