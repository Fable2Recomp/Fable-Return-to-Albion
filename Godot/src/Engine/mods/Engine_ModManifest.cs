using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Refii.Mods;

/// <summary>One thing a mod contributes.</summary>
public sealed class ModManifestEntry
{
	/// <summary>The virtual path this lands on in the mount — normalised, forward slashes.</summary>
	public string Path = string.Empty;

	public long Size;

	public string Sha1 = string.Empty;

	/// <summary><c>file</c> replaces a whole asset; <c>rows</c> merges into one; <c>tu</c> merges records.</summary>
	public string Kind = KindFile;

	/// <summary>For <c>rows</c> or <c>tu</c>: the relative container path.</summary>
	public string Source = string.Empty;

	public const string KindFile = "file";
	public const string KindRows = "rows";
	public const string KindTu = "tu";
}

/// <summary>
/// A MOD'S LOCAL DIFF: every virtual path it contributes, derived directly from its .f2mod package.
/// </summary>
public sealed class ModManifest
{
	public string Name = string.Empty;
	public string Version = string.Empty;
	public string Generated = string.Empty;

	/// <summary>Package length and last write ticks, as <c>length:ticks</c>.</summary>
	public string Stamp = string.Empty;

	public List<ModManifestEntry> Entries { get; } = new();

	/// <summary>
	/// Returns the up-to-date manifest for <paramref name="mod"/>, read directly from its .f2mod package.
	/// </summary>
	public static ModManifest EnsureCurrent(ModInstall mod)
	{
		string packagePath = mod.PackagePath;
		string stamp = ComputeStamp(packagePath);

		string cacheDir = Path.Combine(Path.GetDirectoryName(packagePath) ?? string.Empty, ".cache", "manifests");
		string cachePath = Path.Combine(cacheDir, mod.Name + ".manifest.json");

		ModManifest? cached = Load(cachePath);
		if (cached is not null && cached.Stamp == stamp && stamp.Length != 0)
		{
			return cached;
		}

		ModManifest built = Build(mod, stamp);
		try
		{
			Directory.CreateDirectory(cacheDir);
			built.Save(cachePath);
		}
		catch
		{
		}

		return built;
	}

	public static ModManifest Build(ModInstall mod) =>
		Build(mod, ComputeStamp(mod.PackagePath));

	private static ModManifest Build(ModInstall mod, string stamp)
	{
		var manifest = new ModManifest
		{
			Name = mod.Name,
			Version = mod.Metadata.Version,
			Generated = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
			Stamp = stamp,
		};

		if (File.Exists(mod.PackagePath) && Refii.Core.Engine_Title.IsModPackagePath(mod.PackagePath))
		{
			if (F2ModPackage.TryOpen(mod.PackagePath, out F2ModPackage? package, out _) && package is not null)
			{
				using (package)
				{
					foreach (F2ModEntry f2Entry in package.Entries)
					{
						string normalized = ModLoadOrder.Normalize(f2Entry.Path);
						// Native sources are owned per function (ModNative), never per file: two
						// mods may each ship native/combat.cpp and both compile. Mod Lua is
						// composed (ModLua) and mod GDScript is run by the host (ModGodot):
						// neither is an asset overlay.
						if (normalized.Length == 0 || ModNative.IsNativePath(normalized) ||
							ModLua.IsModLuaPath(normalized) || ModGodot.IsGodotScriptPath(normalized))
						{
							continue;
						}

						var entry = new ModManifestEntry
						{
							Path = normalized,
							Size = (long)f2Entry.Size,
							Sha1 = string.Empty,
						};

						if (normalized.EndsWith(".babel.csv", StringComparison.OrdinalIgnoreCase))
						{
							entry.Kind = ModManifestEntry.KindRows;
							entry.Source = normalized;
							entry.Path = normalized[..^4];
						}
						else if (normalized.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase))
						{
							entry.Kind = ModManifestEntry.KindTu;
							entry.Source = normalized;
						}
						else if (normalized.EndsWith(Refii.Gdb.GdbCsvOverlay.CsvSuffix, StringComparison.OrdinalIgnoreCase))
						{
							// Rows compile to the same patch a .gdb.tu holds and merge with it.
							entry.Kind = ModManifestEntry.KindTu;
							entry.Source = normalized;
							entry.Path = normalized[..^4] + ".tu";
						}

						manifest.Entries.Add(entry);
					}
				}
			}
		}
		else
		{
			string dir = !string.IsNullOrEmpty(mod.Root) && Directory.Exists(mod.Root) ? mod.Root : string.Empty;
			if (!string.IsNullOrEmpty(dir))
			{
				foreach (string file in EnumerateAssets(dir))
				{
					string rel = Path.GetRelativePath(dir, file);
					string normalized = ModLoadOrder.Normalize(rel);
					if (normalized.Length == 0) continue;

					var entry = new ModManifestEntry
					{
						Path = normalized,
						Size = new FileInfo(file).Length,
						Sha1 = HashFile(file),
					};

					if (normalized.EndsWith(".babel.csv", StringComparison.OrdinalIgnoreCase))
					{
						entry.Kind = ModManifestEntry.KindRows;
						entry.Source = normalized;
						entry.Path = normalized[..^4];
					}
					else if (normalized.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase))
					{
						entry.Kind = ModManifestEntry.KindTu;
						entry.Source = normalized;
					}
					else if (normalized.EndsWith(Refii.Gdb.GdbCsvOverlay.CsvSuffix, StringComparison.OrdinalIgnoreCase))
					{
						entry.Kind = ModManifestEntry.KindTu;
						entry.Source = normalized;
						entry.Path = normalized[..^4] + ".tu";
					}

					manifest.Entries.Add(entry);
				}
			}
		}

		manifest.Entries.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
		return manifest;
	}

	/// <summary>
	/// Enumerates relative asset paths under an .f2mod package or directory, strictly excluding sidecars.
	/// </summary>
	public static IEnumerable<string> EnumerateAssets(string assetRoot)
	{
		if (string.IsNullOrEmpty(assetRoot))
		{
			yield break;
		}

		if (File.Exists(assetRoot) && Refii.Core.Engine_Title.IsModPackagePath(assetRoot))
		{
			if (F2ModPackage.TryOpen(assetRoot, out F2ModPackage? package, out _) && package is not null)
			{
				using (package)
				{
					foreach (F2ModEntry entry in package.Entries)
					{
						yield return entry.Path;
					}
				}
			}
			yield break;
		}

		if (!Directory.Exists(assetRoot))
		{
			yield break;
		}

		List<string> files;
		try
		{
			files = new List<string>(
				Directory.EnumerateFiles(assetRoot, "*", SearchOption.AllDirectories));
		}
		catch (Exception)
		{
			yield break;
		}

		files.Sort(StringComparer.OrdinalIgnoreCase);
		foreach (string file in files)
		{
			string name = Path.GetFileName(file);
			if (name.Equals(ModWorkspace.ModMetadataFilename, StringComparison.OrdinalIgnoreCase) ||
				name.Equals(ModWorkspace.ModManifestFilename, StringComparison.OrdinalIgnoreCase) ||
				name.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
				name.EndsWith(".uid", StringComparison.OrdinalIgnoreCase) ||
				Refii.Core.Engine_Title.IsModPackagePath(name) ||
				name.EndsWith(".f2zip", StringComparison.OrdinalIgnoreCase) ||
				name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			yield return file;
		}
	}

	private static string HashFile(string path)
	{
		try
		{
			using FileStream stream = File.OpenRead(path);
			return Convert.ToHexString(Refii.Core.Engine_Hash.Sha1(stream)).ToLowerInvariant();
		}
		catch
		{
			return string.Empty;
		}
	}

	public static string ComputeStamp(string assetPath)
	{
		if (string.IsNullOrEmpty(assetPath))
		{
			return string.Empty;
		}

		try
		{
			if (File.Exists(assetPath))
			{
				var fi = new FileInfo(assetPath);
				return $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
			}
		}
		catch
		{
		}

		return string.Empty;
	}

	public void Save(string path)
	{
		try
		{
			string? dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}

			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
			{
				writer.WriteStartObject();
				writer.WriteString("name", Name);
				writer.WriteString("version", Version);
				writer.WriteString("generated", Generated);
				writer.WriteString("stamp", Stamp);

				writer.WriteStartArray("entries");
				foreach (ModManifestEntry entry in Entries)
				{
					writer.WriteStartObject();
					writer.WriteString("path", entry.Path);
					writer.WriteNumber("size", entry.Size);
					writer.WriteString("sha1", entry.Sha1);
					writer.WriteString("kind", entry.Kind);
					if (entry.Source.Length != 0)
					{
						writer.WriteString("source", entry.Source);
					}

					writer.WriteEndObject();
				}

				writer.WriteEndArray();
				writer.WriteEndObject();
			}

			File.WriteAllBytes(path, stream.ToArray());
		}
		catch
		{
		}
	}

	public static ModManifest? Load(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			using var document = JsonDocument.Parse(File.ReadAllBytes(path));
			JsonElement root = document.RootElement;

			var manifest = new ModManifest
			{
				Name = root.GetProperty("name").GetString() ?? string.Empty,
				Version = root.GetProperty("version").GetString() ?? string.Empty,
				Generated = root.GetProperty("generated").GetString() ?? string.Empty,
				Stamp = root.GetProperty("stamp").GetString() ?? string.Empty,
			};

			if (root.TryGetProperty("entries", out JsonElement entries))
			{
				foreach (JsonElement element in entries.EnumerateArray())
				{
					var entry = new ModManifestEntry
					{
						Path = element.GetProperty("path").GetString() ?? string.Empty,
						Size = element.GetProperty("size").GetInt64(),
						Sha1 = element.GetProperty("sha1").GetString() ?? string.Empty,
						Kind = element.GetProperty("kind").GetString() ?? ModManifestEntry.KindFile,
					};

					if (element.TryGetProperty("source", out JsonElement source))
					{
						entry.Source = source.GetString() ?? string.Empty;
					}

					manifest.Entries.Add(entry);
				}
			}

			return manifest;
		}
		catch
		{
			return null;
		}
	}
}
