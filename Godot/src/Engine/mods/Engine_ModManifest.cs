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

	/// <summary><c>file</c> replaces a whole asset; <c>rows</c> merges into one (phase 2).</summary>
	public string Kind = KindFile;

	/// <summary>For <c>rows</c>: the file under the mod that produced it.</summary>
	public string Source = string.Empty;

	public const string KindFile = "file";
	public const string KindRows = "rows";
}

/// <summary>
/// A MOD'S LOCAL DIFF: every virtual path it contributes, with what it takes to tell whether the
/// folder still matches.
///
/// Enabling and disabling mods is then a set operation over cached manifests rather than a walk
/// of every mod folder. <see cref="Stamp"/> is what makes that safe — a folder that changed
/// under us fails the check and is rebuilt, so the manifest can never quietly describe an older
/// version of the mod.
/// </summary>
public sealed class ModManifest
{
	public string Name = string.Empty;
	public string Version = string.Empty;
	public string Generated = string.Empty;

	/// <summary>File count and newest write time of the asset root, as <c>count:ticks</c>.</summary>
	public string Stamp = string.Empty;

	public List<ModManifestEntry> Entries { get; } = new();

	/// <summary>
	/// The manifest for <paramref name="mod"/>, rebuilt and saved when the folder has moved on.
	///
	/// Hashing happens only on a rebuild: the staleness check needs the walk, which is cheap,
	/// and not the hashes, which are not.
	/// </summary>
	public static ModManifest EnsureCurrent(ModInstall mod)
	{
		string assetRoot = ModWorkspace.GetModAssetRoot(mod);
		string stamp = ComputeStamp(assetRoot);

		string path = ModWorkspace.GetModManifestPath(mod);
		ModManifest? cached = Load(path);
		if (cached is not null && cached.Stamp == stamp && stamp.Length != 0)
		{
			return cached;
		}

		ModManifest built = Build(mod, assetRoot, stamp);
		built.Save(path);
		return built;
	}

	public static ModManifest Build(ModInstall mod) =>
		Build(mod, ModWorkspace.GetModAssetRoot(mod), ComputeStamp(ModWorkspace.GetModAssetRoot(mod)));

	private static ModManifest Build(ModInstall mod, string assetRoot, string stamp)
	{
		var manifest = new ModManifest
		{
			Name = mod.Name,
			Version = mod.Metadata.Version,
			Generated = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
			Stamp = stamp,
		};

		foreach (string file in EnumerateAssets(assetRoot))
		{
			string relative;
			long size;
			try
			{
				relative = System.IO.Path.GetRelativePath(assetRoot, file);
				size = new FileInfo(file).Length;
			}
			catch (Exception)
			{
				continue;
			}

			string normalized = ModLoadOrder.Normalize(relative);
			if (normalized.Length == 0)
			{
				continue;
			}

			var entry = new ModManifestEntry
			{
				Path = normalized,
				Size = size,
				Sha1 = HashFile(file),
			};

			// A ROW OVERLAY NAMES ITS TABLE. `globals.gdb.csv` contributes to `globals.gdb`, not
			// to itself, so that is the path it claims — which is what makes two mods editing
			// different rows of one table a reported row conflict rather than a file conflict.
			// Phase 2 merges them; phase 1 records them.
			if (normalized.EndsWith(".gdb.csv", StringComparison.Ordinal) ||
				normalized.EndsWith(".babel.csv", StringComparison.Ordinal))
			{
				entry.Kind = ModManifestEntry.KindRows;
				entry.Source = normalized;
				entry.Path = normalized[..^4];
			}

			manifest.Entries.Add(entry);
		}

		manifest.Entries.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
		return manifest;
	}

	/// <summary>Everything under the asset root except the mod's own bookkeeping.</summary>
	public static IEnumerable<string> EnumerateAssets(string assetRoot)
	{
		if (string.IsNullOrEmpty(assetRoot) || !Directory.Exists(assetRoot))
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
			string name = System.IO.Path.GetFileName(file);
			if (name.Equals(ModWorkspace.ModMetadataFilename, StringComparison.OrdinalIgnoreCase) ||
				name.Equals(ModWorkspace.ModManifestFilename, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			yield return file;
		}
	}

	private static string ComputeStamp(string assetRoot)
	{
		long count = 0;
		long newest = 0;
		foreach (string file in EnumerateAssets(assetRoot))
		{
			try
			{
				long ticks = File.GetLastWriteTimeUtc(file).Ticks;
				if (ticks > newest)
				{
					newest = ticks;
				}

				count++;
			}
			catch (Exception)
			{
			}
		}

		return count == 0 ? string.Empty : $"{count}:{newest}";
	}

	private static string HashFile(string path)
	{
		try
		{
			using FileStream stream = File.OpenRead(path);
			return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	/// <summary>
	/// Written and read WITHOUT reflection — <see cref="Utf8JsonWriter"/> and
	/// <see cref="JsonDocument"/> — so an export that trims unused metadata cannot turn this
	/// into an empty manifest at runtime.
	/// </summary>
	public void Save(string path)
	{
		try
		{
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
		catch (Exception)
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
			using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
			JsonElement root = document.RootElement;
			var manifest = new ModManifest
			{
				Name = ReadString(root, "name"),
				Version = ReadString(root, "version"),
				Generated = ReadString(root, "generated"),
				Stamp = ReadString(root, "stamp"),
			};

			if (root.TryGetProperty("entries", out JsonElement entries) &&
				entries.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement element in entries.EnumerateArray())
				{
					string entryPath = ReadString(element, "path");
					if (entryPath.Length == 0)
					{
						continue;
					}

					manifest.Entries.Add(new ModManifestEntry
					{
						Path = entryPath,
						Size = element.TryGetProperty("size", out JsonElement size) &&
							   size.TryGetInt64(out long value) ? value : 0,
						Sha1 = ReadString(element, "sha1"),
						Kind = ReadString(element, "kind") is { Length: > 0 } kind
							? kind
							: ModManifestEntry.KindFile,
						Source = ReadString(element, "source"),
					});
				}
			}

			return manifest;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static string ReadString(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? string.Empty
			: string.Empty;
}
