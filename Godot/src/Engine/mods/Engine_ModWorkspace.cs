using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Refii.Mods;

/// <summary>What a mod says about itself, from its <c>mod.toml</c>. All fields optional.</summary>
public sealed class ModMetadata
{
	public string Title = string.Empty;
	public string Version = string.Empty;
	public string Author = string.Empty;
	public string Description = string.Empty;
}

/// <summary>One installed mod folder. <see cref="Root"/> is the folder; assets may be under it
/// directly or under <c>files/</c> — see <see cref="ModWorkspace.GetModAssetRoot"/>.</summary>
public sealed class ModInstall
{
	public string Name = string.Empty;
	public string Root = string.Empty;
	public bool HasFilesFolder;
	public ModMetadata Metadata = new();
}

/// <summary>
/// The mod folder on disk: where mods live, which ones are installed, and what each says.
///
/// IN THE ENGINE, NOT THE LAUNCHER, because both read it — the launcher panel lists mods and the
/// mount overlays them. Two copies of "which mods are installed" is two answers to the question
/// the resolved index exists to settle.
/// </summary>
public static class ModWorkspace
{
	public const string DefaultModsFolderName = "mods";
	public const string ModFilesFolderName = "files";
	public const string ModMetadataFilename = "mod.toml";
	public const string ModManifestFilename = "manifest.json";

	public static string GetDefaultModsRoot(string assetsRoot) =>
		string.IsNullOrEmpty(assetsRoot)
			? string.Empty
			: Path.GetFullPath(Path.Combine(assetsRoot, DefaultModsFolderName));

	/// <summary>
	/// Whether a name may be joined to the mods root.
	///
	/// The name comes from a file the player can edit, and it is used to build a path — so
	/// rooted paths and <c>..</c> are refused rather than normalised.
	/// </summary>
	public static bool IsSafeModFolderName(string name)
	{
		if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name))
		{
			return false;
		}

		foreach (string component in name.Split('/', '\\'))
		{
			if (component.Length == 0 || component == "." || component == "..")
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>Every mod folder under <paramref name="modsRoot"/>, name-ordered.</summary>
	public static List<ModInstall> DiscoverModInstalls(string modsRoot)
	{
		var mods = new List<ModInstall>();
		if (string.IsNullOrEmpty(modsRoot) || !Directory.Exists(modsRoot))
		{
			return mods;
		}

		// Auto-unpack any .f2zip packages dropped directly into modsRoot
		try
		{
			foreach (string archive in Directory.GetFiles(modsRoot, "*.f2zip"))
			{
				string modName = Path.GetFileNameWithoutExtension(archive);
				if (!IsSafeModFolderName(modName))
				{
					continue;
				}

				string targetDir = Path.Combine(modsRoot, modName);
				if (!Directory.Exists(targetDir) || File.GetLastWriteTimeUtc(archive) > Directory.GetLastWriteTimeUtc(targetDir))
				{
					Directory.CreateDirectory(targetDir);
					ZipFile.ExtractToDirectory(archive, targetDir, overwriteFiles: true);
				}
			}
		}
		catch
		{
		}

		string[] directories;
		try
		{
			directories = Directory.GetDirectories(modsRoot);
		}
		catch (Exception)
		{
			return mods;
		}

		foreach (string directory in directories)
		{
			string name = Path.GetFileName(directory);
			if (name.StartsWith('.') || !IsSafeModFolderName(name))
			{
				continue;
			}

			// If mod folder contains an .f2zip but files/ is missing, unpack it into the mod folder
			string zipInMod = Path.Combine(directory, name + ".f2zip");
			string filesDir = Path.Combine(directory, ModFilesFolderName);
			if (File.Exists(zipInMod) && !Directory.Exists(filesDir))
			{
				try
				{
					ZipFile.ExtractToDirectory(zipInMod, directory, overwriteFiles: true);
				}
				catch
				{
				}
			}

			mods.Add(new ModInstall
			{
				Name = name,
				Root = directory,
				HasFilesFolder = Directory.Exists(Path.Combine(directory, ModFilesFolderName)),
				Metadata = ReadModMetadata(directory),
			});
		}

		mods.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
		return mods;
	}

	public static string GetModAssetRoot(ModInstall mod) =>
		mod.HasFilesFolder ? Path.Combine(mod.Root, ModFilesFolderName) : mod.Root;

	public static string GetModManifestPath(ModInstall mod) =>
		Path.Combine(mod.Root, ModManifestFilename);

	/// <summary>
	/// The mod's own description of itself.
	///
	/// Deliberately a hand-rolled key=value reader, not a TOML parser: the four keys below are
	/// the whole vocabulary, and an unreadable or absent file is an empty description rather
	/// than an error — a mod that ships no metadata still works.
	/// </summary>
	public static ModMetadata ReadModMetadata(string modRoot)
	{
		var metadata = new ModMetadata();
		if (string.IsNullOrEmpty(modRoot))
		{
			return metadata;
		}

		string path = Path.Combine(modRoot, ModMetadataFilename);
		if (!File.Exists(path))
		{
			return metadata;
		}

		IEnumerable<string> lines;
		try
		{
			lines = File.ReadLines(path);
		}
		catch (Exception)
		{
			return metadata;
		}

		foreach (string line in lines)
		{
			string trimmed = line.Trim();
			if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == '[')
			{
				continue;
			}

			int equals = trimmed.IndexOf('=');
			if (equals < 0)
			{
				continue;
			}

			string key = trimmed[..equals].Trim().ToLowerInvariant();
			string value = Unquote(trimmed[(equals + 1)..].Trim());

			switch (key)
			{
				case "name":
				case "title":
					metadata.Title = value;
					break;
				case "version":
					metadata.Version = value;
					break;
				case "author":
					metadata.Author = value;
					break;
				case "description":
					metadata.Description = value;
					break;
			}
		}

		return metadata;
	}

	internal static string Unquote(string value) =>
		value.Length >= 2 &&
		((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
			? value[1..^1]
			: value;
}
