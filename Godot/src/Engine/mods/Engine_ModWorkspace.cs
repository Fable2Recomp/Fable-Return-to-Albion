using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Refii.Mods;

/// <summary>What a mod says about itself, from its embedded <c>mod.toml</c>. All fields optional.</summary>
public sealed class ModMetadata
{
	public string Title = string.Empty;
	public string Version = string.Empty;
	public string Author = string.Empty;
	public string Description = string.Empty;
}

/// <summary>One installed mod package (.f2mod).</summary>
public sealed class ModInstall
{
	public string Name = string.Empty;
	public string PackagePath = string.Empty;
	public ModMetadata Metadata = new();

	public bool HasFilesFolder;

	/// <summary>Points to the package path or parent directory.</summary>
	public string Root
	{
		get => !string.IsNullOrEmpty(_root) ? _root : (!string.IsNullOrEmpty(PackagePath) ? PackagePath : string.Empty);
		set => _root = value;
	}
	private string _root = string.Empty;
}

/// <summary>
/// The mod workspace on disk: where .f2mod packages live, which ones are installed, and what each says.
/// </summary>
public static class ModWorkspace
{
	public const string DefaultModsFolderName = "mods";
	public const string ModFilesFolderName = "files";
	public const string ModMetadataFilename = "mod.toml";
	public const string ModManifestFilename = "manifest.json";
	public const string PackageExtension = ".f2mod";

	public static string GetDefaultModsRoot(string assetsRoot) =>
		string.IsNullOrEmpty(assetsRoot)
			? string.Empty
			: Path.GetFullPath(Path.Combine(assetsRoot, DefaultModsFolderName));

	/// <summary>
	/// Whether a name may be joined to the mods root.
	/// </summary>
	public static bool IsSafeModFolderName(string name)
	{
		if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name))
		{
			return false;
		}

		foreach (string component in name.Split('/', '\\'))
		{
			if (component is ".." or "." || string.IsNullOrWhiteSpace(component))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Discovers all .f2mod packages directly under <paramref name="modsRoot"/>, name-ordered.
	/// Subdirectories, non-.f2mod files, and cache folders (.cache) are strictly ignored.
	/// </summary>
	public static List<ModInstall> DiscoverModInstalls(string modsRoot)
	{
		var mods = new List<ModInstall>();
		if (string.IsNullOrEmpty(modsRoot) || !Directory.Exists(modsRoot))
		{
			return mods;
		}

		string[] files;
		try
		{
			files = Directory.GetFiles(modsRoot, "*.f2mod");
		}
		catch (Exception)
		{
			return mods;
		}

		foreach (string file in files)
		{
			string name = Path.GetFileNameWithoutExtension(file);
			if (!IsSafeModFolderName(name))
			{
				continue;
			}

			if (F2ModPackage.TryReadMetadata(file, out ModMetadata meta, out _))
			{
				if (string.IsNullOrWhiteSpace(meta.Title))
				{
					meta.Title = name;
				}

				mods.Add(new ModInstall
				{
					Name = name,
					PackagePath = file,
					Metadata = meta,
				});
			}
		}

		mods.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
		return mods;
	}

	/// <summary>
	/// Creates a brand-new .f2mod package in modsRoot with default metadata.
	/// </summary>
	public static bool CreateModPackage(
		string modsRoot, string modName, out string packagePath, out string error)
	{
		packagePath = string.Empty;
		error = string.Empty;

		if (string.IsNullOrWhiteSpace(modName) || !IsSafeModFolderName(modName))
		{
			error = $"Invalid mod name: '{modName}'.";
			return false;
		}

		packagePath = Path.Combine(modsRoot, modName + PackageExtension);
		if (File.Exists(packagePath))
		{
			error = $"Mod package '{modName}{PackageExtension}' already exists.";
			return false;
		}

		var meta = new ModMetadata
		{
			Title = modName,
			Version = "1.0.0",
			Author = "Modder",
			Description = "Custom mod for Fable 2",
		};

		return F2ModPackage.Create(packagePath, meta, Array.Empty<(string, byte[])>(), out error);
	}

	public static string GetModAssetRoot(ModInstall mod) => mod.PackagePath;

	public static ModMetadata ParseModToml(string tomlText)
	{
		var metadata = new ModMetadata();
		if (string.IsNullOrWhiteSpace(tomlText))
		{
			return metadata;
		}

		using var reader = new StringReader(tomlText);
		string? line;
		while ((line = reader.ReadLine()) is not null)
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

	public static string FormatModToml(ModMetadata metadata)
	{
		var sb = new StringBuilder();
		sb.AppendLine($"title = \"{metadata.Title}\"");
		sb.AppendLine($"version = \"{metadata.Version}\"");
		sb.AppendLine($"author = \"{metadata.Author}\"");
		sb.AppendLine($"description = \"{metadata.Description}\"");
		return sb.ToString();
	}

	internal static string Unquote(string value) =>
		value.Length >= 2 &&
		((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
			? value[1..^1]
			: value;
}
