using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Refii.Bnk;

namespace Refii.Mods;

/// <summary>
/// Packs a mod's loose files into one `.bnk`, ready to hand to someone.
///
/// A MOD BANK IS THE PACKED FORM OF A MOD FOLDER, nothing more: the mount indexes
/// <c>&lt;mod&gt;/x.bnk</c> and <c>&lt;mod&gt;/files/…</c> through the same code, so a mod may be
/// developed loose and shipped packed with no change to what the game sees.
///
/// ONLY WHAT THE MOD OWNS. The entries come from the mod's own folder, never from the mount —
/// a rejoined texture read back through the mount carries the game's payload, and packing that
/// would put shipped assets into a file meant to be redistributed.
/// </summary>
public static class ModExport
{
	public const string PackedFolderSuffix = "-packed";

	/// <summary>
	/// Writes <c>&lt;modsRoot&gt;/&lt;name&gt;-packed/&lt;name&gt;.bnk</c> plus the mod's metadata
	/// and anything a bank must not hold, and reports the folder it made.
	///
	/// The result is itself an installable mod folder: enable it in place of the loose one and
	/// the mount must resolve identically, which is how the packed form is checked.
	/// </summary>
	public static bool ToBank(
		ModInstall mod, string modsRoot, out string packedRoot, out string error)
	{
		packedRoot = string.Empty;
		error = string.Empty;

		string assetRoot = ModWorkspace.GetModAssetRoot(mod);
		if (!Directory.Exists(assetRoot))
		{
			error = $"'{mod.Name}' has no files to pack.";
			return false;
		}

		var entries = new List<BnkWriterEntry>();
		var beside = new List<string>();
		foreach (string file in ModManifest.EnumerateAssets(assetRoot))
		{
			string relative;
			try
			{
				relative = Path.GetRelativePath(assetRoot, file).Replace('\\', '/');
			}
			catch (Exception exception)
			{
				error = exception.Message;
				return false;
			}

			// A ROW OVERLAY IS SOURCE, NOT AN ASSET, and a bank inside a bank is just a bank —
			// both travel beside the pack rather than inside it.
			// An authored model is converted at mount from the file it replaces, which a bank
			// entry cannot carry with it — so it travels beside the pack, like a row overlay.
			bool authoredMesh = false;
			foreach (string suffix in ModMeshOverlays.Suffixes)
			{
				if (relative.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				{
					authoredMesh = true;
					break;
				}
			}

			if (IsRowOverlay(relative) || authoredMesh ||
				relative.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
			{
				beside.Add(file);
				continue;
			}

			try
			{
				byte[] bytes = File.ReadAllBytes(file);
				string path = relative;

				// AN AUTHORED IMAGE IS PACKED AS THE TEXTURE IT BECOMES. The mount converts
				// `foo.tex.png` at load; packing the PNG instead would ship a bank whose entry
				// the game cannot read, and packing it under both names would ship it twice.
				if (relative.EndsWith(ModImageOverlays.Suffix, StringComparison.OrdinalIgnoreCase))
				{
					var conversion = new ImageConversion { Target = relative, Mod = mod.Name };
					if (!ModImageOverlays.TryConvert(bytes, out byte[] tex, conversion))
					{
						error = $"{relative}: {conversion.Error}";
						return false;
					}

					bytes = tex;
					path = relative[..^4];
				}

				entries.Add(new BnkWriterEntry { Path = path, Bytes = bytes });
			}
			catch (Exception exception)
			{
				error = $"{relative}: {exception.Message}";
				return false;
			}
		}

		if (entries.Count == 0 && beside.Count == 0)
		{
			error = $"'{mod.Name}' has no files to pack.";
			return false;
		}

		string destination = Path.Combine(modsRoot, mod.Name + PackedFolderSuffix);
		try
		{
			Directory.CreateDirectory(destination);

			if (entries.Count != 0 &&
				!BnkWriter.WriteFile(
					Path.Combine(destination, mod.Name + ".bnk"), entries, out error))
			{
				return false;
			}

			foreach (string file in beside)
			{
				string relative = Path.GetRelativePath(assetRoot, file);
				string target = Path.Combine(destination, relative);
				string? directory = Path.GetDirectoryName(target);
				if (!string.IsNullOrEmpty(directory))
				{
					Directory.CreateDirectory(directory);
				}

				File.Copy(file, target, overwrite: true);
			}

			string metadata = Path.Combine(mod.Root, ModWorkspace.ModMetadataFilename);
			if (File.Exists(metadata))
			{
				File.Copy(
					metadata,
					Path.Combine(destination, ModWorkspace.ModMetadataFilename),
					overwrite: true);
			}
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}

		packedRoot = destination;
		return true;
	}

	public static bool IsRowOverlay(string path) =>
		path.EndsWith(".babel.csv", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Packages the mod into a standard .f2zip archive ready for distribution.
	/// Only packages metadata (mod.toml) and runtime files under files/.
	/// Authoring sources under src/ are strictly excluded.
	/// </summary>
	public static bool ToF2Zip(ModInstall mod, string destinationZip, out string error)
	{
		error = string.Empty;
		string assetRoot = ModWorkspace.GetModAssetRoot(mod);
		bool hasFiles = Directory.Exists(assetRoot);
		string metadataPath = Path.Combine(mod.Root, ModWorkspace.ModMetadataFilename);
		bool hasMeta = File.Exists(metadataPath);

		if (!hasFiles && !hasMeta)
		{
			error = $"'{mod.Name}' has no files or metadata to package.";
			return false;
		}

		try
		{
			string? dir = Path.GetDirectoryName(destinationZip);
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}

			if (File.Exists(destinationZip))
			{
				File.Delete(destinationZip);
			}

			using var zip = ZipFile.Open(destinationZip, ZipArchiveMode.Create);

			if (hasMeta)
			{
				zip.CreateEntryFromFile(metadataPath, ModWorkspace.ModMetadataFilename, CompressionLevel.Optimal);
			}

			if (hasFiles)
			{
				foreach (string file in Directory.EnumerateFiles(assetRoot, "*", SearchOption.AllDirectories))
				{
					string rel = Path.GetRelativePath(assetRoot, file).Replace('\\', '/');
					if (rel.StartsWith(".") || rel.Contains("/."))
					{
						continue;
					}

					string entryName = Path.Combine(ModWorkspace.ModFilesFolderName, rel).Replace('\\', '/');
					zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
				}
			}

			return true;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}
}
