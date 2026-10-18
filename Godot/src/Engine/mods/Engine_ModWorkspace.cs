using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.Mods;

/// <summary>What a mod says about itself, from its embedded <c>mod.toml</c>. All fields optional.</summary>
public sealed class ModMetadata
{
	public string Title = string.Empty;
	public string Version = string.Empty;
	public string Author = string.Empty;
	public string Description = string.Empty;

	/// <summary>
	/// <c>requires = ["souls_combat >= 0.1", "hd-ui"]</c>: mods this one needs installed and enabled.
	/// A required mod is placed beneath (lower priority: mounted, composed and hooked inside) the
	/// mod that requires it, and its <c>native/include/</c> is visible to that mod's native code.
	/// </summary>
	public List<ModRequirement> Requires = new();

	/// <summary><c>after = ["hd-ui"]</c>: mods this one is placed above when both are installed, without needing them.</summary>
	public List<string> After = new();
}

/// <summary>One entry of <see cref="ModMetadata.Requires"/>: a mod folder name and an optional version floor.</summary>
public sealed class ModRequirement
{
	public string Name = string.Empty;

	/// <summary>Dotted version the installed mod must be at or above; empty for any version.</summary>
	public string MinVersion = string.Empty;

	/// <summary>Parses <c>name</c> or <c>name >= 1.2</c>; null when there is no name.</summary>
	public static ModRequirement? Parse(string text)
	{
		string trimmed = text.Trim();
		if (trimmed.Length == 0)
		{
			return null;
		}

		int at = trimmed.IndexOf(">=", StringComparison.Ordinal);
		if (at < 0)
		{
			return new ModRequirement { Name = trimmed };
		}

		string name = trimmed[..at].Trim();
		return name.Length == 0
			? null
			: new ModRequirement { Name = name, MinVersion = trimmed[(at + 2)..].Trim() };
	}

	/// <summary>Whether an installed <paramref name="version"/> meets the floor.</summary>
	public bool Satisfied(string version) =>
		MinVersion.Length == 0 || CompareVersions(version, MinVersion) >= 0;

	/// <summary>Dotted segments compared numerically where both are numbers, ordinally otherwise; a missing segment is 0.</summary>
	public static int CompareVersions(string left, string right)
	{
		string[] a = left.Trim().Split('.');
		string[] b = right.Trim().Split('.');
		for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
		{
			string x = i < a.Length ? a[i].Trim() : "0";
			string y = i < b.Length ? b[i].Trim() : "0";
			int order = int.TryParse(x, out int xi) && int.TryParse(y, out int yi)
				? xi.CompareTo(yi)
				: string.CompareOrdinal(x, y);
			if (order != 0)
			{
				return order;
			}
		}

		return 0;
	}

	public override string ToString() => MinVersion.Length == 0 ? Name : Name + " >= " + MinVersion;
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
				case "requires":
					metadata.Requires = ParseArray(trimmed[(equals + 1)..])
						.Select(ModRequirement.Parse)
						.Where(requirement => requirement is not null)
						.Select(requirement => requirement!)
						.ToList();
					break;
				case "after":
					metadata.After = ParseArray(trimmed[(equals + 1)..]);
					break;
			}
		}

		return metadata;
	}

	/// <summary>A one-line TOML array of strings, or a single string, as its items.</summary>
	internal static List<string> ParseArray(string raw)
	{
		string text = raw.Trim();
		if (text.StartsWith('[') && text.EndsWith(']'))
		{
			text = text[1..^1];
		}

		return text.Split(',')
			.Select(item => Unquote(item.Trim()))
			.Where(item => item.Length != 0)
			.ToList();
	}

	private static string FormatArray(IEnumerable<string> items) =>
		"[" + string.Join(", ", items.Select(item => "\"" + item + "\"")) + "]";

	public static string FormatModToml(ModMetadata metadata)
	{
		var sb = new StringBuilder();
		sb.AppendLine($"title = \"{metadata.Title}\"");
		sb.AppendLine($"version = \"{metadata.Version}\"");
		sb.AppendLine($"author = \"{metadata.Author}\"");
		sb.AppendLine($"description = \"{metadata.Description}\"");
		if (metadata.Requires.Count != 0)
		{
			sb.AppendLine("requires = " + FormatArray(metadata.Requires.Select(r => r.ToString())));
		}

		if (metadata.After.Count != 0)
		{
			sb.AppendLine("after = " + FormatArray(metadata.After));
		}

		return sb.ToString();
	}

	internal static string Unquote(string value) =>
		value.Length >= 2 &&
		((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
			? value[1..^1]
			: value;
}
