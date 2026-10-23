using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Refii.Core;

/// <summary>
/// The assembly's one and only DllImport resolver.
///
/// .NET permits a SINGLE resolver per assembly — a second
/// <see cref="NativeLibrary.SetDllImportResolver"/> throws "A resolver is already set
/// for the assembly". Every binding here used to install its own from a static
/// constructor, so whichever type initialised second died with a
/// <see cref="TypeInitializationException"/>: mounting banks first made
/// <see cref="MdlConverter"/> permanently unusable, which is exactly the order the
/// model bridge runs in.
///
/// So resolution lives here, once, and the bindings contribute directories instead.
/// This class stays engine-free — Godot-side code publishes the project-relative
/// directories through <see cref="AddSearchPath"/>.
/// </summary>
public static class Engine_NativeLibraries
{
	/// <summary>
	/// Directories to search, in preference order.
	///
	/// A LIST, not one directory: the libraries are produced by different targets into
	/// different build outputs — <c>librefii_bnk_extractor</c> into a config's
	/// <c>lib/</c>, <c>librefii_assetbrowser</c> into its own build dir — so a single
	/// configured directory can only ever find one of them.
	/// </summary>
	private static readonly List<string> Paths = new();

	private static readonly object Gate = new();
	private static bool _installed;
	private static bool _published;

	/// <summary>
	/// The staging folder name for the architecture THIS PROCESS is running as — matching
	/// REFII_GODOT_ARCH in the root CMakeLists, which stages each architecture's native
	/// closure into Godot/build/&lt;arch&gt;/bin.
	/// </summary>
	public static string ArchFolder => RuntimeInformation.ProcessArchitecture switch
	{
		Architecture.Arm64 => "arm64",
		Architecture.X64 => "x86_64",
		var other => other.ToString().ToLowerInvariant(),
	};

	/// <summary>
	/// Where the native build stages this architecture's DLLs, and where anything shipped
	/// beside them (the upscaler weights, for instance) lives.
	/// </summary>
	/// <param name="projectDir">Globalized "res://".</param>
	public static string StagedBinDirectory(string projectDir) =>
		System.IO.Path.Combine(projectDir, "build", ArchFolder, "bin");

	/// <summary>Installs the resolver. Idempotent and safe to call from anywhere.</summary>
	public static void Install()
	{
		lock (Gate)
		{
			if (_installed)
				return;

			_installed = true;
			NativeLibrary.SetDllImportResolver(typeof(Engine_NativeLibraries).Assembly, Resolve);

			if (OperatingSystem.IsWindows())
			{
				try
				{
					string bin = Engine_AppRoot.BinDirectory;
					if (Directory.Exists(bin))
						SetDllDirectory(bin);
				}
				catch
				{
				}
			}
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetDllDirectory(string? lpPathName);

	/// <summary>
	/// Adds a directory to search. Deduplicating, so republishing the project's paths
	/// neither grows the list nor discards a directory another subsystem contributed.
	/// </summary>
	public static void AddSearchPath(string? directory)
	{
		if (string.IsNullOrEmpty(directory))
			return;

		lock (Gate)
		{
			if (!Paths.Contains(directory, StringComparer.Ordinal))
				Paths.Add(directory);
		}
	}

	/// <summary>Snapshot of the search path, for diagnostics.</summary>
	public static IReadOnlyList<string> SearchPaths
	{
		get { lock (Gate) return Paths.ToArray(); }
	}

	/// <summary>Platform file name for one of our libraries.</summary>
	public static string FileNameFor(string library) =>
		OperatingSystem.IsWindows() ? $"{library}.dll" : $"lib{library}.so";

	private static IEnumerable<string> FileNamesFor(string library)
	{
		if (OperatingSystem.IsWindows())
		{
			yield return $"{library}.dll";
			yield return $"lib{library}.dll";
			yield break;
		}

		yield return $"lib{library}.so";
		yield return $"{library}.so";
	}

	private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
	{
		// Ours only. Anything else falls through to the runtime's default probing.
		if (!name.StartsWith("refii_", StringComparison.Ordinal))
			return IntPtr.Zero;

		// The project's build directories are discovered from Godot, and the first
		// P/Invoke is not always preceded by a mount — BankAssetSource opens a bank
		// directly, and used to fail with "unable to load shared library" purely because
		// nothing had published the paths yet. Publish here, where every resolve passes.
		EnsurePublished();

		// Several build outputs can each hold a copy, and directory enumeration order is
		// the filesystem's, not sorted — so "first found" is nondeterministic and can pick
		// a stale library whose entry points predate the current ABI. Take the most
		// recently built one.
		var found = new List<string>();
		foreach (string directory in CandidateDirectories())
		{
			foreach (string fileName in FileNamesFor(name))
			{
				string candidate = Path.Combine(directory, fileName);
				if (File.Exists(candidate))
					found.Add(candidate);
			}
		}

		foreach (string candidate in found.OrderByDescending(File.GetLastWriteTimeUtc))
			if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
				return handle;

		return IntPtr.Zero;
	}

	/// <summary>
	/// Adds the project's build directories, once. Kept lazy so resolution does not
	/// depend on the caller having mounted anything first.
	/// </summary>
	private static void EnsurePublished()
	{
		lock (Gate)
		{
			if (_published)
				return;
			_published = true;
		}

		// Every build output under out/build. No config is hardcoded — they vary by
		// platform and by debug/netplay — and the libraries land in different
		// subdirectories per target, so all three are tried.
		//
		// This used to live on the bank reader, which the engine's pure-C# one
		// superseded and removed. Losing it is not obvious: the remaining P/Invokes
		// (the launcher ops DLL, the host bridge) simply report "library not found"
		// while sitting in a build directory nobody is looking in.
		try
		{
			string? projectDir = ProjectDirectory();
			if (projectDir is null)
				return;

			// The STAGED copy first — Godot/build/<arch>/bin, where the native build puts
			// the launcher ops, the host bridge and the guest runtime. This is the only
			// location that survives an export; everything below it is a dev-tree
			// convenience.
			//
			// Staging is PER-ARCHITECTURE: an x86_64 and an ARM64 build produce DLLs with
			// identical names, so a single flat directory can only ever hold one of them
			// and exporting for two architectures is impossible. Only the root matching
			// the architecture this process is actually running as is searched — picking
			// up the wrong one would fail at load with a confusing "not a valid Win32
			// application" rather than "library not found".
			// The app root's bin/ first — the one location that is the same in a dev tree
			// and an export, and the only one an installed player has.
			AddSearchPath(Engine_AppRoot.BinDirectory);
			if (OperatingSystem.IsWindows())
			{
				try
				{
					string bin = Engine_AppRoot.BinDirectory;
					if (Directory.Exists(bin))
						SetDllDirectory(bin);
				}
				catch
				{
				}
			}

			AddSearchPath(StagedBinDirectory(projectDir));
			AddSearchPath(System.IO.Path.Combine(projectDir, "build", ArchFolder));

			// The pre-split flat layout, still honoured so an existing tree (or an export
			// that flattened the closure next to the executable) keeps working.
			AddSearchPath(System.IO.Path.Combine(projectDir, "build", "bin"));
			AddSearchPath(System.IO.Path.Combine(projectDir, "build"));

			string repoRoot = System.IO.Path.GetFullPath(
				System.IO.Path.Combine(projectDir, ".."));
			string builds = System.IO.Path.Combine(repoRoot, "out", "build");
			if (!Directory.Exists(builds))
				return;

			foreach (string config in Directory.EnumerateDirectories(builds))
			{
				foreach (string sub in new[] { "lib", "bin", "" })
				{
					string directory = sub.Length == 0 ? config : System.IO.Path.Combine(config, sub);
					if (Directory.Exists(directory))
						AddSearchPath(directory);
				}
			}
		}
		catch (Exception)
		{
			// Godot not up yet, or no project — the environment override and the
			// assembly's own directory still apply.
		}
	}

	/// <summary>
	/// The Godot project directory. From the engine when one is running; otherwise — the test
	/// host, a tool — by walking up from the assembly to the folder holding project.godot. A
	/// Godot API call with no engine behind it is a native crash, not an exception, so the engine
	/// is asked only when this process IS the engine (its main module, never dotnet's test host).
	/// </summary>
	private static string? ProjectDirectory()
	{
		string module = string.Empty;
		try
		{
			module = System.Diagnostics.Process.GetCurrentProcess().MainModule?.ModuleName ?? string.Empty;
		}
		catch (Exception)
		{
		}

		bool hostedByDotnet = module.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
			module.StartsWith("testhost", StringComparison.OrdinalIgnoreCase);
		if (!hostedByDotnet)
		{
			try
			{
				return Godot.ProjectSettings.GlobalizePath("res://");
			}
			catch (Exception)
			{
			}
		}

		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at is not null; i++)
		{
			if (File.Exists(Path.Combine(at, "project.godot")))
				return at;
			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	private static IEnumerable<string> CandidateDirectories()
	{
		// The explicit override wins — the build output is on no default search path and
		// Godot runs the assembly from the project directory, nowhere near it.
		string? explicitDir = Environment.GetEnvironmentVariable("REFII_NATIVE_DIR");
		if (!string.IsNullOrEmpty(explicitDir))
			yield return explicitDir!;

		foreach (string dir in SearchPaths)
			yield return dir;

		yield return AppContext.BaseDirectory;

		string? refiiRoot = Environment.GetEnvironmentVariable("REFII_ROOT");
		if (!string.IsNullOrEmpty(refiiRoot))
		{
			string builds = Path.Combine(refiiRoot!, "out", "build");
			if (Directory.Exists(builds))
				foreach (string config in Directory.EnumerateDirectories(builds))
					foreach (string sub in new[] { "lib", "bin", "" })
						yield return sub.Length == 0 ? config : Path.Combine(config, sub);
		}
	}
}
