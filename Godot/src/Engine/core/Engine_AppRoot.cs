using System;
using System.IO;
using System.Runtime.InteropServices;
using Godot;

namespace Refii.Core;

/// <summary>
/// The one directory every other path in the app is measured from.
/// </summary>
/// <remarks>
/// THERE USED TO BE THREE, and they only agreed in an exported build:
///
///   <c>GlobalizePath("res://")</c>   the project, or the executable's directory once exported
///   <c>AppContext.BaseDirectory</c>  the managed assemblies — under the editor that is
///                                    <c>.godot/mono/temp/bin/Debug</c>
///   <c>OS.GetExecutablePath()</c>    the host binary — under the editor that is wherever
///                                    the user keeps their Godot install
///
/// Each subsystem picked one, so the launcher wrote its config beside the assemblies while
/// the guest read it beside the Godot editor binary and the natives were staged beside the
/// project. Nothing was wrong in isolation and nothing lined up.
///
/// The rule now: the app root is where the SHIPPED layout lives, and the layout under it is
/// identical in a dev tree and an export, so a path that resolves in one resolves in the
/// other.
///
///   &lt;root&gt;/bin              native libraries, and bin/recompile
///   &lt;root&gt;/assets           game, generated, unpacked, save
///   &lt;root&gt;/refii.toml       runtime config
///   &lt;root&gt;/logs             guest logs
///
/// Exported, that is the executable's own directory. In the editor the executable is
/// Godot's own binary, which is not our tree, so it is the per-architecture staging root
/// the native build already writes — the same shape, one level in.
/// Android uses OS.GetUserDataDir because its installed package is immutable; the same
/// layout beneath that private root keeps config, assets, saves, logs, and caches writable.
/// </remarks>
public static class Engine_AppRoot
{
	/// <summary>Overrides the resolved root outright. For tests and odd deployments.</summary>
	public const string EnvironmentVariable = "REFII_APP_ROOT";

	private static readonly object Gate = new();
	private static string? _resolved;

	/// <summary>Architecture folder for THIS process, matching REFII_GODOT_ARCH in CMake.</summary>
	public static string ArchFolder => RuntimeInformation.ProcessArchitecture switch
	{
		Architecture.Arm64 => "arm64",
		Architecture.X64 => "x86_64",
		var other => other.ToString().ToLowerInvariant(),
	};

	public static bool IsTestHost =>
		AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
		System.Environment.CommandLine.Contains("testhost", StringComparison.OrdinalIgnoreCase);

	/// <summary>True when running from an exported template rather than the editor.</summary>
	public static bool IsExported
	{
		get
		{
			if (IsTestHost)
				return false;
			try { return OS.HasFeature("template"); } catch { return false; }
		}
	}

	public static string Path
	{
		get
		{
			lock (Gate)
			{
				return _resolved ??= Resolve();
			}
		}
	}

	/// <summary>Forces re-resolution. Only useful after an override changes.</summary>
	public static void Invalidate()
	{
		lock (Gate)
		{
			_resolved = null;
		}
	}

	public static string SafeGetProjectDir()
	{
		if (IsTestHost)
		{
			string dir = Directory.GetCurrentDirectory();
			while (!string.IsNullOrEmpty(dir))
			{
				if (File.Exists(System.IO.Path.Combine(dir, "project.godot")))
					return dir;
				if (Directory.Exists(System.IO.Path.Combine(dir, "Godot")) && File.Exists(System.IO.Path.Combine(dir, "Godot", "project.godot")))
					return System.IO.Path.Combine(dir, "Godot");
				string? parent = Directory.GetParent(dir)?.FullName;
				if (parent == null || parent == dir) break;
				dir = parent;
			}
			return Directory.GetCurrentDirectory();
		}

		try
		{
			return ProjectSettings.GlobalizePath("res://");
		}
		catch
		{
			return Directory.GetCurrentDirectory();
		}
	}

	private static string Resolve()
	{
		// System.Environment: Godot.Environment is the 3D world environment resource.
		string? explicitRoot = System.Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (!string.IsNullOrEmpty(explicitRoot))
		{
			return Normalize(explicitRoot!);
		}

		// Android maps res:// and its native libraries from an installed package. Neither
		// is a writable desktop install tree. Keep user data in the engine-provided private
		// directory; shipped libraries continue to use the Android linker's package search.
		if (!IsTestHost && OS.HasFeature("android"))
		{
			string privateRoot = AndroidPrivateRoot(OS.GetUserDataDir());
			GD.Print($"[android-path] app_root={privateRoot}; config={System.IO.Path.Combine(privateRoot, "refii.toml")}");
			return privateRoot;
		}

		if (IsExported)
		{
			// The executable's directory. Deliberately NOT res://: with an embedded PCK
			// there is no on-disk res://, and this is also exactly what the guest runtime
			// resolves its own paths from, so the two cannot drift.
			string? executable = null;
			try { executable = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()); } catch { }
			if (!string.IsNullOrEmpty(executable))
			{
				return Normalize(executable!);
			}
		}

		// Editor: the staging root the native build writes, which already has the shipped
		// shape. Per-architecture, because an x86_64 and an ARM64 build produce identically
		// named libraries and a shared directory can only ever hold one of them.
		string project = SafeGetProjectDir();
		return Normalize(System.IO.Path.Combine(project, "build", ArchFolder));
	}

	/// <summary>Validates the Android engine's app-private writable directory.</summary>
	/// <param name="userDataDirectory">The physical pathname supplied by OS.GetUserDataDir.</param>
	/// <returns>The normalized root used by assets, saves, caches, logs, and launcher config.</returns>
	/// <exception cref="ArgumentException">The engine returned no private directory.</exception>
	internal static string AndroidPrivateRoot(string userDataDirectory)
	{
		if (string.IsNullOrWhiteSpace(userDataDirectory))
			throw new ArgumentException("Android private data directory is unavailable.", nameof(userDataDirectory));
		return Normalize(userDataDirectory);
	}

	private static string Normalize(string path) =>
		System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);

	/// <summary>Native libraries, and the install-time build inputs under <c>recompile</c>.</summary>
	public static string BinDirectory => System.IO.Path.Combine(Path, "bin");

	/// <summary>Everything derived from the player's own game.</summary>
	public static string AssetsDirectory => System.IO.Path.Combine(Path, "assets");

	/// <summary>The runtime config, written by the launcher and read by the guest.</summary>
	public static string ConfigPath => System.IO.Path.Combine(Path, "refii.toml");

	public static string LogsDirectory => System.IO.Path.Combine(Path, "logs");

	/// <summary>
	/// Mods that ship with the app, as source: <c>bundled-mods</c> beside the binary once exported,
	/// the repo's own <c>Fable2/mods-src</c> in a dev tree. See <see cref="Refii.Mods.ModBundled"/>.
	/// </summary>
	public static string BundledModsDirectory => BundledModsDirectoryFor(Engine_Title.Active);

	/// <summary>
	/// One title's shipped mods: <c>bundled-mods/&lt;Folder&gt;</c> exported, the repo's
	/// <c>&lt;Folder&gt;/mods-src</c> in a dev tree.
	/// </summary>
	public static string BundledModsDirectoryFor(Engine_Title title) => IsExported
		? System.IO.Path.Combine(Path, "bundled-mods", title.Folder)
		: System.IO.Path.GetFullPath(System.IO.Path.Combine(SafeGetProjectDir(), "..", title.Folder, "mods-src"));

	/// <summary>
	/// Player-owned state — the profile and savegames — for the active title:
	/// <c>assets/&lt;Folder&gt;/save</c> (<see cref="Engine_Title.SaveDirectory"/>).
	/// </summary>
	public static string SaveDirectory => Engine_Title.Active.SaveDirectory;

	/// <summary>
	/// Derived data that can be thrown away and regenerated: <c>&lt;app&gt;/cache</c>.
	/// </summary>
	/// <remarks>
	/// ITS OWN CONTAINER AT THE INSTALL ROOT, and deliberately NOT under <see cref="SaveDirectory"/>.
	/// Cache is regenerable and save data is not, so the one directory a player may delete to
	/// reclaim disk must not contain the one directory they can never get back — and a cache
	/// sweep walking assets/save is a sweep pointed at their profile. It is also what
	/// refii_app.h already names as the native default (<c>appRoot / "cache"</c>); the guest only
	/// ends up under assets/save because the SDK derives cache_root from user_data_root before
	/// that default is consulted.
	///
	/// Everything regenerable lives here — converted and upscaled textures alike — so clearing
	/// the app's derived data is one folder, inside the install, and saves are untouched.
	/// </remarks>
	public static string CacheDirectory => System.IO.Path.Combine(Path, "cache");

	/// <summary>
	/// Moves a pre-title install into its title folder (<see cref="Engine_TitleLayout"/>), then
	/// creates the directories the app expects to write into.
	/// </summary>
	public static void EnsureLayout()
	{
		// FIRST: creating the active title's folders before the move would leave it nowhere to go.
		foreach (string line in Engine_TitleLayout.MigrateLegacyLayout())
		{
			GD.Print("[layout] " + line);
		}

		foreach (string directory in new[]
		         {
			         BinDirectory, AssetsDirectory, LogsDirectory, SaveDirectory, CacheDirectory,
			         Engine_Title.Active.AssetsDirectory, Engine_Title.Active.BinDirectory,
		         })
		{
			try
			{
				Directory.CreateDirectory(directory);
			}
			catch (Exception)
			{
			}
		}
	}
}
