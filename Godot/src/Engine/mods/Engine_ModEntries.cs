using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Mods;

/// <summary>
/// One entry path read from EVERY enabled mod, lowest priority first.
/// </summary>
/// <remarks>
/// For content that COMPOSES across mods — UI templates, HUD pieces, mod abilities — rather than
/// being overridden whole. The mount serves one winner per path, so composing content is read
/// from each package instead, in an order that lets the highest-priority mod apply last.
/// </remarks>
public static class ModEntries
{
	public static List<(string Mod, string Text)> ReadEnabledText(string entryPath) =>
		ReadEnabledText(
			Refii.Assets.Engine_GameAssets.ModsRoot,
			Refii.Core.Engine_Title.Active.ModExtension,
			entryPath);

	/// <summary>
	/// Reads <paramref name="entryPath"/> from every enabled mod in <paramref name="modsRoot"/> matching
	/// <paramref name="packageExtension"/>, lowest priority first.
	/// </summary>
	public static List<(string Mod, string Text)> ReadEnabledText(
		string modsRoot, string packageExtension, string entryPath)
	{
		var result = new List<(string Mod, string Text)>();
		bool useSession = ModSession.IsActive &&
			string.Equals(modsRoot, Refii.Assets.Engine_GameAssets.ModsRoot, StringComparison.OrdinalIgnoreCase) &&
			string.Equals(packageExtension, Refii.Core.Engine_Title.Active.ModExtension, StringComparison.OrdinalIgnoreCase);
		if (string.IsNullOrEmpty(modsRoot) || (!System.IO.Directory.Exists(modsRoot) && !useSession))
		{
			return result;
		}

		(ModLoadOrder order, List<ModInstall> found) = useSession
			? ModSession.Resolve(modsRoot)
			: (ModLoadOrder.Read(modsRoot), ModWorkspace.DiscoverModInstalls(modsRoot, packageExtension));
		var installs = new Dictionary<string, ModInstall>(StringComparer.OrdinalIgnoreCase);
		foreach (ModInstall install in found)
		{
			installs[install.Name] = install;
		}

		for (int i = order.Enabled.Count - 1; i >= 0; i--)
		{
			if (!installs.TryGetValue(order.Enabled[i], out ModInstall? mod) ||
				!F2ModPackage.TryOpen(mod.PackagePath, out F2ModPackage? package, out _) ||
				package is null)
			{
				continue;
			}

			using (package)
			{
				if (package.TryGetEntry(entryPath, out F2ModEntry? entry) && entry is not null)
				{
					result.Add((mod.Name, Encoding.UTF8.GetString(package.ReadEntryBytes(entry))));
				}
			}
		}

		return result;
	}

	/// <summary>
	/// One entry's bytes from the highest-priority enabled mod that ships it (a marker's PNG under
	/// <c>art/</c>), or null when none does.
	/// </summary>
	public static byte[]? ReadEnabledBytes(string entryPath)
	{
		string modsRoot = Refii.Assets.Engine_GameAssets.ModsRoot;
		if (string.IsNullOrEmpty(modsRoot) || (!System.IO.Directory.Exists(modsRoot) && !ModSession.IsActive))
		{
			return null;
		}

		(ModLoadOrder order, List<ModInstall> found) = ModSession.Resolve(modsRoot);
		var installs = new Dictionary<string, ModInstall>(StringComparer.OrdinalIgnoreCase);
		foreach (ModInstall install in found)
		{
			installs[install.Name] = install;
		}

		for (int i = 0; i < order.Enabled.Count; i++)
		{
			if (!installs.TryGetValue(order.Enabled[i], out ModInstall? mod) ||
				!F2ModPackage.TryOpen(mod.PackagePath, out F2ModPackage? package, out _) ||
				package is null)
			{
				continue;
			}

			using (package)
			{
				if (package.TryGetEntry(entryPath, out F2ModEntry? entry) && entry is not null)
				{
					return package.ReadEntryBytes(entry);
				}
			}
		}

		return null;
	}

	/// <summary>Every entry path under <paramref name="prefix"/> (a folder, `/`-separated) in any enabled mod, each once.</summary>
	public static List<string> EnabledEntryPaths(string prefix)
	{
		var result = new List<string>();
		string modsRoot = Refii.Assets.Engine_GameAssets.ModsRoot;
		if (string.IsNullOrEmpty(modsRoot) || (!System.IO.Directory.Exists(modsRoot) && !ModSession.IsActive))
		{
			return result;
		}

		(ModLoadOrder order, List<ModInstall> found) = ModSession.Resolve(modsRoot);
		var installs = new Dictionary<string, ModInstall>(StringComparer.OrdinalIgnoreCase);
		foreach (ModInstall install in found)
		{
			installs[install.Name] = install;
		}

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string name in order.Enabled)
		{
			if (!installs.TryGetValue(name, out ModInstall? mod) ||
				!F2ModPackage.TryOpen(mod.PackagePath, out F2ModPackage? package, out _) || package is null)
			{
				continue;
			}

			using (package)
			{
				foreach (F2ModEntry entry in package.Entries)
				{
					string path = entry.Path.Replace('\\', '/');
					if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(path))
					{
						result.Add(path);
					}
				}
			}
		}

		return result;
	}
}
