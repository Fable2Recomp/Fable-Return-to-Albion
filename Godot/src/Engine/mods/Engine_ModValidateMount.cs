using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Refii.Mods;

/// <summary>
/// The schema check over whole mods: every entry of a package (or a loose asset tree) whose path is
/// a checked kind, and its <c>mod.toml</c>. Separate from the per-file checks so those stay free of
/// the package reader.
/// </summary>
public static partial class ModValidate
{
	/// <summary>
	/// Every issue of every enabled mod, in load order, for the mount to log once:
	/// <c>[mods] &lt;mod&gt;/&lt;issue&gt;</c>.
	/// </summary>
	public static List<(string Mod, ModIssue Issue)> ValidateOverlays(IReadOnlyList<ModOverlay> overlays)
	{
		var found = new List<(string Mod, ModIssue Issue)>();
		foreach (ModOverlay overlay in overlays)
		{
			string source = !string.IsNullOrEmpty(overlay.PackagePath) ? overlay.PackagePath : overlay.AssetRoot;
			foreach (ModIssue issue in ValidateSource(source))
			{
				found.Add((overlay.Name, issue));
			}
		}

		return found;
	}

	/// <summary>
	/// Every issue of one mod: a <c>.f2mod</c> package (its metadata checked as the <c>mod.toml</c> it
	/// was packed from, re-stated) or a loose folder (its own <c>mod.toml</c> and every file).
	/// </summary>
	public static List<ModIssue> ValidateSource(string source)
	{
		var issues = new List<ModIssue>();
		if (File.Exists(source) && source.EndsWith(ModWorkspace.PackageExtension, StringComparison.OrdinalIgnoreCase))
		{
			if (!F2ModPackage.TryOpen(source, out F2ModPackage? package, out string error) || package is null)
			{
				issues.Add(new ModIssue(Path.GetFileName(source), string.Empty, true, $"the package cannot be read: {error}"));
				return issues;
			}

			using (package)
			{
				issues.AddRange(Validate(ModWorkspace.ModMetadataFilename, ModWorkspace.FormatModToml(package.Metadata)));
				foreach (F2ModEntry entry in package.Entries)
				{
					if (KindOf(entry.Path).Length == 0 || KindOf(entry.Path) == ModWorkspace.ModMetadataFilename)
					{
						continue;
					}

					string text;
					try
					{
						text = Encoding.UTF8.GetString(package.ReadEntryBytes(entry));
					}
					catch (Exception exception)
					{
						issues.Add(new ModIssue(Normalize(entry.Path), string.Empty, true, $"cannot be read: {exception.Message}"));
						continue;
					}

					issues.AddRange(Validate(entry.Path, text));
				}
			}

			return issues;
		}

		if (!Directory.Exists(source))
		{
			return issues;
		}

		string toml = Path.Combine(source, ModWorkspace.ModMetadataFilename);
		if (File.Exists(toml))
		{
			issues.AddRange(Validate(ModWorkspace.ModMetadataFilename, ReadText(toml)));
		}

		foreach (string file in ModManifest.EnumerateAssets(source))
		{
			string relative = Path.GetRelativePath(source, file);
			if (KindOf(relative).Length != 0)
			{
				issues.AddRange(Validate(relative, ReadText(file)));
			}
		}

		return issues;
	}

	/// <summary>A file's text with its byte-order mark kept, so the check sees what the loader reads.</summary>
	private static string ReadText(string file) => Encoding.UTF8.GetString(File.ReadAllBytes(file));
}
