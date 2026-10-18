#if TOOLS
using System;
using System.IO;
using Godot;
using Refii.Mods;

namespace Refii.EditorTools;

/// <summary>
/// Sharing on the script API: the active mod as one <c>.f2mod</c> a player installs from the
/// launcher (<see cref="ModInstaller"/>).
/// </summary>
public partial class RefiiModApi
{
	/// <summary>
	/// Writes the active mod to <c>&lt;destinationFolder&gt;/&lt;mod&gt;.f2mod</c> — a copy of its
	/// package with the space left by edits compacted out — checks it by the rules the launcher's
	/// installer applies, and opens the folder. A package the installer would refuse is removed
	/// and the reason returned. Empty when the package was written.
	/// </summary>
	public string SharePackage(string destinationFolder)
	{
		string package = PackagePath();
		if (package.Length == 0)
		{
			return NoActiveMod;
		}

		if (!File.Exists(package))
		{
			return $"'{GetActiveMod()}' has no package yet — write something into it first";
		}

		if (string.IsNullOrWhiteSpace(destinationFolder))
		{
			return "choose a folder to share the package into";
		}

		string folder = Path.GetFullPath(ProjectSettings.GlobalizePath(destinationFolder.Trim()));
		string modsRoot = Path.GetFullPath(GetModsRoot());
		if (string.Equals(folder.TrimEnd(Path.DirectorySeparatorChar), modsRoot.TrimEnd(Path.DirectorySeparatorChar),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
		{
			return "choose a folder outside the mods folder — the package there is the one being edited";
		}

		string target = Path.Combine(folder, GetActiveMod() + ModWorkspace.PackageExtension);
		try
		{
			Directory.CreateDirectory(folder);
			File.Copy(package, target, overwrite: true);
		}
		catch (Exception exception)
		{
			return $"could not write '{target}': {exception.Message}";
		}

		if (!F2ModPackage.Defragment(target, out string compactError))
		{
			TryDeleteShared(target);
			return $"could not compact '{target}': {compactError}";
		}

		ModInstallResult check = ModInstaller.Inspect(target, modsRoot);
		if (!check.Ok)
		{
			TryDeleteShared(target);
			return "the launcher would refuse this package: " + check.Error;
		}

		OS.ShellOpen(folder);
		return string.Empty;
	}

	private static void TryDeleteShared(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception)
		{
		}
	}
}
#endif
