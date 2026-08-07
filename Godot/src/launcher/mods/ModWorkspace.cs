using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Refii.Launcher.Modding;

public sealed class ModMetadata
{
	public string Title = string.Empty;
	public string Version = string.Empty;
	public string Author = string.Empty;
	public string Description = string.Empty;
}

public sealed class ModAssetSummary
{
	public int FileCount;
	public bool HasBankManifests;
	public List<string> NormalizedPaths = new();
}

public sealed class ModInstall
{
	public string Name = string.Empty;
	public string Root = string.Empty;
	public bool HasFilesFolder;
	public ModMetadata Metadata = new();
}

public static class ModWorkspace
{
	public const string DefaultModsFolderName = "mods";
	public const string EnabledModsFilename = "enabled.txt";
	public const string ModFilesFolderName = "files";
	public const string ModMetadataFilename = "mod.toml";
	private const string BankManifestFilename = "_manifest.tsv";

	public static string GetDefaultModsRoot(string assetsRoot) =>
		string.IsNullOrEmpty(assetsRoot)
			? string.Empty
			: Path.GetFullPath(Path.Combine(assetsRoot, DefaultModsFolderName));

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

	public static List<string> ReadEnabledModNames(string modsRoot)
	{
		var names = new List<string>();
		if (string.IsNullOrEmpty(modsRoot))
		{
			return names;
		}

		string path = Path.Combine(modsRoot, EnabledModsFilename);
		if (!File.Exists(path))
		{
			return names;
		}

		foreach (string line in File.ReadLines(path))
		{
			AddEnabledModName(names, line);
		}

		return names;
	}

	private static void AddEnabledModName(List<string> names, string value)
	{
		string trimmed = value.Trim();
		if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';')
		{
			return;
		}

		int inlineComment = trimmed.IndexOf('#');
		if (inlineComment >= 0)
		{
			trimmed = trimmed[..inlineComment].Trim();
		}

		if (trimmed.Length == 0 || !IsSafeModFolderName(trimmed))
		{
			return;
		}

		if (!names.Contains(trimmed))
		{
			names.Add(trimmed);
		}
	}

	public static bool WriteEnabledModNames(
		string modsRoot, IReadOnlyList<string> names, out string error)
	{
		error = string.Empty;
		try
		{
			Directory.CreateDirectory(modsRoot);

			var builder = new StringBuilder();
			foreach (string name in names)
			{
				if (IsSafeModFolderName(name))
				{
					builder.Append(name).Append('\n');
				}
			}

			File.WriteAllText(
				Path.Combine(modsRoot, EnabledModsFilename),
				builder.ToString(),
				new UTF8Encoding(false));
			return true;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}

	public static List<ModInstall> DiscoverModInstalls(string modsRoot)
	{
		var mods = new List<ModInstall>();
		if (string.IsNullOrEmpty(modsRoot) || !Directory.Exists(modsRoot))
		{
			return mods;
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
			if (!IsSafeModFolderName(name))
			{
				continue;
			}

			mods.Add(new ModInstall
			{
				Name = name,
				Root = Path.GetFullPath(directory),
				HasFilesFolder = Directory.Exists(Path.Combine(directory, ModFilesFolderName)),
				Metadata = ReadModMetadata(directory),
			});
		}

		mods.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
		return mods;
	}

	public static string GetModAssetRoot(ModInstall mod) =>
		mod.HasFilesFolder ? Path.Combine(mod.Root, ModFilesFolderName) : mod.Root;

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

		foreach (string line in File.ReadLines(path))
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
			string value = trimmed[(equals + 1)..].Trim();

			if (value.Length >= 2 &&
				((value[0] == '"' && value[^1] == '"') ||
				 (value[0] == '\'' && value[^1] == '\'')))
			{
				value = value[1..^1];
			}

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

	public static ModAssetSummary SummarizeModAssets(string assetRoot, int maxFiles = 200000)
	{
		var summary = new ModAssetSummary();
		if (string.IsNullOrEmpty(assetRoot) || !Directory.Exists(assetRoot))
		{
			return summary;
		}

		IEnumerable<string> files;
		try
		{
			files = Directory.EnumerateFiles(assetRoot, "*", SearchOption.AllDirectories);
		}
		catch (Exception)
		{
			return summary;
		}

		foreach (string file in files)
		{
			string key;
			try
			{
				key = Path.GetRelativePath(assetRoot, file).Replace('\\', '/').ToLowerInvariant();
			}
			catch (Exception)
			{
				continue;
			}

			if (key == ModMetadataFilename)
			{
				continue;
			}

			if (key.EndsWith(BankManifestFilename, StringComparison.Ordinal))
			{
				summary.HasBankManifests = true;
			}

			summary.FileCount++;
			if (summary.NormalizedPaths.Count < maxFiles)
			{
				summary.NormalizedPaths.Add(key);
			}
		}

		return summary;
	}
}
