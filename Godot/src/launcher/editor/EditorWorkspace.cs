using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Godot;
using Refii.Core;

namespace Refii.Launcher.Editing;

/// <summary>What the modding editor needs, and whether it is here yet.</summary>
public enum EditorReadiness
{
	/// <summary>No game installed — the editor mounts the install and has nothing to open.</summary>
	NoGame,

	/// <summary>The project the editor opens is missing; the package did not stage it.</summary>
	NoSdk,

	/// <summary>The Godot editor has not been downloaded.</summary>
	NoEditor,

	/// <summary>No .NET SDK, which a C# project cannot open without.</summary>
	NoDotnet,

	Ready,
}

/// <summary>What version of what to fetch. Written by the package, read by the launcher.</summary>
public sealed class EditorPins
{
	/// <summary>The lowest Godot the project opens in — <c>project.godot</c>'s own feature tag.</summary>
	public string GodotFloor = "4.7";

	/// <summary>Which Godot build: the .NET one, because the project is C#.</summary>
	public string Flavour = "mono";

	/// <summary>The .NET major the assembly targets.</summary>
	public string DotnetMajor = "8";

	/// <summary>Release asset that lists the checksums for a Godot release.</summary>
	public string ChecksumAsset = "SHA512-SUMS.txt";
}

/// <summary>
/// THE MODDING EDITOR IS THIS PROJECT.
///
/// The plugins are C# inside the launcher's own assembly and lean on `Refii.*` throughout, so
/// there is nothing to extract into a separate add-on — and no reason to: the project carries no
/// game assets, so it ships as it is. What the package adds is a copy of it (`sdk/`) and the pins
/// naming the editor to fetch.
///
/// EVERYTHING RESOLVES FROM ONE PLACE. The editor is launched with `REFII_APP_ROOT` pointing at
/// the install, which <see cref="Engine_AppRoot"/> already honours, so `bin/`, `assets/game`,
/// `refii.toml` and `mods/` are the install's. Nothing is copied and the editor session and the
/// game share one install — a mod exported in the editor is the mod the launcher lists.
/// </summary>
public static class EditorWorkspace
{
	public const string PinsFilename = "pins.json";

	/// <summary>
	/// The project the editor opens: the package's `sdk/`, or this repository's `Godot/` when
	/// running from a development tree.
	///
	/// Resolved by looking for `project.godot` rather than by configuration — the same rule
	/// <see cref="Refii.Core.Engine_GameData"/> follows for game data.
	/// </summary>
	public static string SdkRoot
	{
		get
		{
			foreach (string candidate in SdkCandidates())
			{
				if (File.Exists(Path.Combine(candidate, "project.godot")))
				{
					return candidate;
				}
			}

			return Path.Combine(Engine_AppRoot.Path, "sdk");
		}
	}

	private static IEnumerable<string> SdkCandidates()
	{
		yield return Path.Combine(Engine_AppRoot.Path, "sdk");

		// A development tree runs the launcher from inside the project itself.
		string project = ProjectSettings.GlobalizePath("res://");
		if (project.Length != 0)
		{
			yield return project;
		}
	}

	public static string ToolsRoot => Path.Combine(Engine_AppRoot.Path, "tools");

	public static string GodotRoot => Path.Combine(ToolsRoot, "godot");

	public static string DotnetRoot => Path.Combine(ToolsRoot, "dotnet");

	public static bool SdkPresent => File.Exists(Path.Combine(SdkRoot, "project.godot"));

	/// <summary>
	/// The Godot editor this install downloaded, or null.
	///
	/// Matched by name — the archive unpacks into a versioned folder, and the version is not
	/// known ahead of time because the latest stable is what gets fetched.
	/// </summary>
	public static string? FindEditor()
	{
		if (!Directory.Exists(GodotRoot))
		{
			return null;
		}

		string[] candidates;
		try
		{
			candidates = Directory.GetFiles(GodotRoot, "Godot_v*", SearchOption.AllDirectories);
		}
		catch (Exception)
		{
			return null;
		}

		string? best = null;
		foreach (string candidate in candidates)
		{
			string name = Path.GetFileName(candidate);
			if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
				name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
				!name.Contains("mono", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (OperatingSystem.IsWindows() != name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (best is null || string.CompareOrdinal(name, Path.GetFileName(best)) > 0)
			{
				best = candidate;
			}
		}

		return best;
	}

	/// <summary>The bundled .NET SDK if one was fetched, else one on PATH, else null.</summary>
	public static string? FindDotnet()
	{
		string bundled = Path.Combine(DotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
		if (File.Exists(bundled))
		{
			return bundled;
		}

		string? path = System.Environment.GetEnvironmentVariable("PATH");
		if (path is null)
		{
			return null;
		}

		foreach (string directory in path.Split(Path.PathSeparator))
		{
			if (directory.Length == 0)
			{
				continue;
			}

			string candidate = Path.Combine(
				directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

	/// <summary>
	/// Whether the editor can be opened, and what is missing when it cannot.
	///
	/// The game is checked FIRST: the editor mounts the player's install, so offering it before
	/// there is one gives them an editor with nothing in it.
	/// </summary>
	public static EditorReadiness Readiness(out string missing)
	{
		missing = string.Empty;

		if (Refii.Core.Engine_GameData.Resolve().Length == 0)
		{
			missing = "Install the game first — the editor opens the assets it installed.";
			return EditorReadiness.NoGame;
		}

		if (!SdkPresent)
		{
			missing =
				$"The editor project is missing from {SdkRoot.Replace('\\', '/')}. " +
				"This package was built without it.";
			return EditorReadiness.NoSdk;
		}

		if (FindEditor() is null)
		{
			missing = "Godot has not been downloaded yet.";
			return EditorReadiness.NoEditor;
		}

		if (FindDotnet() is null)
		{
			missing =
				$"No .NET {ReadPins().DotnetMajor} SDK found. Godot cannot open a C# project " +
				"without one — install it from dotnet.microsoft.com, then press Open Editor again.";
			return EditorReadiness.NoDotnet;
		}

		return EditorReadiness.Ready;
	}

	/// <summary>
	/// The archive name for this host, for a given Godot version.
	///
	/// The names come from the same table the build's own fetch uses, so the launcher and the
	/// build ask for identically-built editors.
	/// </summary>
	public static string? ArchiveNameFor(string version, EditorPins pins)
	{
		string? platform = (OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture) switch
		{
			(true, Architecture.X64) => "win64",
			(true, Architecture.Arm64) => "windows_arm64",
			(false, Architecture.X64) => "linux_x86_64",
			(false, Architecture.Arm64) => "linux_arm64",
			_ => null,
		};

		return platform is null ? null : $"Godot_v{version}_{pins.Flavour}_{platform}.zip";
	}

	/// <summary>
	/// The pins the package wrote, or the defaults.
	///
	/// ONLY A FLOOR IS PINNED, not a version: the plugins are plugins, and a C# project has a
	/// lowest Godot it opens in rather than a highest. The build keeps its own exact pin for
	/// reproducible exports; the editor a player opens is the latest stable at or above this.
	/// </summary>
	public static EditorPins ReadPins()
	{
		var pins = new EditorPins();
		string path = Path.Combine(SdkRoot, PinsFilename);
		if (!File.Exists(path))
		{
			return pins;
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
			JsonElement root = document.RootElement;
			pins.GodotFloor = ReadString(root, "godotFloor", pins.GodotFloor);
			pins.Flavour = ReadString(root, "flavour", pins.Flavour);
			pins.DotnetMajor = ReadString(root, "dotnetMajor", pins.DotnetMajor);
			pins.ChecksumAsset = ReadString(root, "checksumAsset", pins.ChecksumAsset);
		}
		catch (Exception)
		{
		}

		return pins;
	}

	private static string ReadString(JsonElement element, string name, string fallback) =>
		element.TryGetProperty(name, out JsonElement value) &&
		value.ValueKind == JsonValueKind.String &&
		value.GetString() is { Length: > 0 } text
			? text
			: fallback;

	/// <summary>
	/// Whether <paramref name="version"/> is at or above the floor. Compares the numeric parts,
	/// so <c>4.10</c> is correctly newer than <c>4.9</c>.
	/// </summary>
	public static bool MeetsFloor(string version, string floor)
	{
		int[] left = Parts(version);
		int[] right = Parts(floor);
		for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
		{
			int a = i < left.Length ? left[i] : 0;
			int b = i < right.Length ? right[i] : 0;
			if (a != b)
			{
				return a > b;
			}
		}

		return true;
	}

	private static int[] Parts(string version)
	{
		string trimmed = version.Split('-')[0];
		string[] pieces = trimmed.Split('.');
		var parts = new int[pieces.Length];
		for (int i = 0; i < pieces.Length; i++)
		{
			parts[i] = int.TryParse(pieces[i], out int value) ? value : 0;
		}

		return parts;
	}

	/// <summary>
	/// Opens the editor on the install.
	///
	/// <c>REFII_APP_ROOT</c> is what makes the editor session and the game share one install, and
	/// <c>DOTNET_ROOT</c> points the editor at the bundled SDK when there is one.
	/// </summary>
	public static bool Launch(out string error)
	{
		error = string.Empty;
		if (Readiness(out string missing) != EditorReadiness.Ready)
		{
			error = missing;
			return false;
		}

		string editor = FindEditor()!;
		try
		{
			EnsurePluginsBuilt();

			var start = new System.Diagnostics.ProcessStartInfo
			{
				FileName = editor,
				WorkingDirectory = SdkRoot,
				UseShellExecute = false,
			};
			start.ArgumentList.Add("-e");
			start.ArgumentList.Add("--path");
			start.ArgumentList.Add(SdkRoot);
			// The editor writes no log of its own (file logging is off in editor mode), and its
			// stdout is not ours to read: one crash left nothing but a core dump. The SDK project
			// is this app's project, so the editor's user:// is this user dir.
			start.ArgumentList.Add("--log-file");
			start.ArgumentList.Add(Path.Combine(OS.GetUserDataDir(), "logs", "editor.log"));
			start.Environment["REFII_APP_ROOT"] = Engine_AppRoot.Path;

			string? dotnet = FindDotnet();
			if (dotnet is not null && dotnet.StartsWith(DotnetRoot, StringComparison.Ordinal))
			{
				start.Environment["DOTNET_ROOT"] = DotnetRoot;
			}

			System.Diagnostics.Process.Start(start);
			return true;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}

	/// <summary>
	/// Ensures the C# solution is compiled, and current, before Godot opens.
	///
	/// Godot C# plugins are loaded from the compiled assembly in <c>.godot/mono/temp/bin/Debug/</c>.
	/// If the assembly is missing when Godot opens, Godot fails to instantiate the plugins
	/// and disables them in <c>project.godot</c>. If it is older than the sources, the editor
	/// runs the OLD plugin code — Godot does not rebuild on open — which is how a fixed plugin
	/// kept crashing the editor through two package rebuilds.
	/// </summary>
	public static void EnsurePluginsBuilt()
	{
		string assemblyPath = Path.Combine(SdkRoot, ".godot", "mono", "temp", "bin", "Debug", "refii-launcher.dll");
		if (File.Exists(assemblyPath) && !SourcesNewerThan(File.GetLastWriteTimeUtc(assemblyPath)))
		{
			return;
		}

		string? dotnet = FindDotnet();
		if (dotnet is null)
		{
			return;
		}

		try
		{
			var start = new System.Diagnostics.ProcessStartInfo
			{
				FileName = dotnet,
				WorkingDirectory = SdkRoot,
				UseShellExecute = false,
			};
			start.ArgumentList.Add("build");
			if (dotnet.StartsWith(DotnetRoot, StringComparison.Ordinal))
			{
				start.Environment["DOTNET_ROOT"] = DotnetRoot;
			}

			using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(start);
			process?.WaitForExit();
		}
		catch (Exception)
		{
			// Best effort; if dotnet fails, launching Godot will report errors directly.
		}
	}

	/// <summary>Whether a C# source or the project file under the SDK was written after <paramref name="built"/>.</summary>
	private static bool SourcesNewerThan(DateTime built)
	{
		string project = Path.Combine(SdkRoot, "refii-launcher.csproj");
		if (File.Exists(project) && File.GetLastWriteTimeUtc(project) > built)
		{
			return true;
		}

		foreach (string folder in new[] { "addons", "src" })
		{
			string root = Path.Combine(SdkRoot, folder);
			if (!Directory.Exists(root))
			{
				continue;
			}

			foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
			{
				if (File.GetLastWriteTimeUtc(file) > built)
				{
					return true;
				}
			}
		}

		return false;
	}
}
