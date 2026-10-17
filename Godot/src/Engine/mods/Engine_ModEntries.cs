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
	public static List<(string Mod, string Text)> ReadEnabledText(string entryPath)
	{
		var result = new List<(string Mod, string Text)>();
		string modsRoot = Refii.Assets.Engine_GameAssets.ModsRoot;
		if (string.IsNullOrEmpty(modsRoot) || !System.IO.Directory.Exists(modsRoot))
		{
			return result;
		}

		ModLoadOrder order = ModLoadOrder.Read(modsRoot);
		var installs = new Dictionary<string, ModInstall>(StringComparer.OrdinalIgnoreCase);
		foreach (ModInstall install in ModWorkspace.DiscoverModInstalls(modsRoot))
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
}
