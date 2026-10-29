using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Refii.Mods;

/// <summary>
/// Mods that ship WITH the app, as source, packed into the mods folder by the launcher.
/// </summary>
/// <remarks>
/// <para>The release carries each bundled mod as its source tree under
/// <see cref="Refii.Core.Engine_AppRoot.BundledModsDirectory"/> (the repo's <c>Fable2/mods-src/</c> in
/// a dev tree): <c>mod.toml</c> is the package metadata, every other file is an entry at its own
/// path, README files excepted. Packed here with <see cref="F2ModPackage.Create"/> — the one
/// writer — rather than by the build, which has no way to write the format.</para>
/// <para>INSTALLED, NEVER ENABLED. A bundled mod appears in the Mods panel switched off; a
/// gameplay overhaul is the player's choice, and a hard mod asks for consent when enabled.</para>
/// <para>ONLY THE LISTED FILES. A release writes <c>manifest.txt</c> beside the mods naming every
/// file it shipped; an update unpacked over an install leaves files the release deleted lying
/// there, and without the list they would be packed back into the mod. A dev tree has no list
/// and packs what is on disk.</para>
/// <para>NEVER OVER SOMEONE'S EDITS. <c>.bundled/&lt;name&gt;.sha256</c> records the source the
/// package was packed from and the package's own hash. An update rewrites the package only while
/// it is still byte-for-byte what was packed; one edited in the Mod Studio, or a package of that
/// name the player installed themselves, is left alone and named.</para>
/// </remarks>
public static class ModBundled
{
	private const string MarkerFolder = ".bundled";

	/// <summary>Packs every bundled mod under <paramref name="sourceRoot"/> into <paramref name="modsRoot"/>.</summary>
	public static List<string> Install(string sourceRoot, string modsRoot)
	{
		var report = new List<string>();
		if (string.IsNullOrEmpty(sourceRoot) || !Directory.Exists(sourceRoot) || string.IsNullOrEmpty(modsRoot))
		{
			return report;
		}

		HashSet<string>? listed = ReadManifest(sourceRoot);
		foreach (string tree in Directory.GetDirectories(sourceRoot))
		{
			string name = Path.GetFileName(tree);
			if (!ModWorkspace.IsSafeModFolderName(name) || !File.Exists(Path.Combine(tree, ModWorkspace.ModMetadataFilename)))
			{
				continue;
			}

			try
			{
				report.Add(InstallOne(tree, name, modsRoot, listed));
			}
			catch (Exception exception)
			{
				report.Add($"{name}: not installed — {exception.Message}");
			}
		}

		return report;
	}

	/// <summary>The release's list of bundled files (<c>mod/relative/path</c>), or null in a dev tree.</summary>
	private static HashSet<string>? ReadManifest(string sourceRoot)
	{
		string manifest = Path.Combine(sourceRoot, "manifest.txt");
		if (!File.Exists(manifest))
		{
			return null;
		}

		var listed = new HashSet<string>(StringComparer.Ordinal);
		foreach (string line in File.ReadAllLines(manifest))
		{
			string entry = line.Trim().Replace('\\', '/');
			if (entry.Length != 0)
			{
				listed.Add(entry);
			}
		}

		return listed;
	}

	private static string InstallOne(string tree, string name, string modsRoot, HashSet<string>? listed)
	{
		string metadataText = File.ReadAllText(Path.Combine(tree, ModWorkspace.ModMetadataFilename));
		List<(string Path, byte[] Bytes)> files = Collect(tree, name, listed);
		string sourceHash = HashSource(metadataText, files);

		string package = Path.Combine(modsRoot, name + ModWorkspace.PackageExtension);
		string marker = Path.Combine(modsRoot, MarkerFolder, name + ".sha256");
		(string packedFrom, string packedAs) = ReadMarker(marker);

		if (File.Exists(package))
		{
			if (packedAs.Length == 0)
			{
				return $"{name}: a package of that name is already installed and was not put there by " +
					"the app — left alone";
			}

			if (!string.Equals(HashFile(package), packedAs, StringComparison.OrdinalIgnoreCase))
			{
				return $"{name}: edited since it was installed — left alone";
			}

			if (string.Equals(packedFrom, sourceHash, StringComparison.OrdinalIgnoreCase))
			{
				return $"{name}: current";
			}
		}

		Directory.CreateDirectory(modsRoot);
		ModMetadata metadata = ModWorkspace.ParseModToml(metadataText);
		if (metadata.Title.Length == 0)
		{
			metadata.Title = name;
		}

		if (!F2ModPackage.Create(package, metadata, files, out string error))
		{
			return $"{name}: could not be packed — {error}";
		}

		Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
		File.WriteAllText(marker, sourceHash + "\n" + HashFile(package) + "\n");
		return $"{name}: installed ({files.Count} file(s)) — enable it in the Mods panel";
	}

	/// <summary>A mod source folder's entries, as a package holds them: every file at its own path but
	/// <c>mod.toml</c> (the metadata) and the readme.</summary>
	public static List<(string Path, byte[] Bytes)> Collect(string tree) => Collect(tree, string.Empty, null);

	private static List<(string Path, byte[] Bytes)> Collect(string tree, string name, HashSet<string>? listed)
	{
		var files = new List<(string Path, byte[] Bytes)>();
		foreach (string file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
		{
			string onDisk = Path.GetRelativePath(tree, file).Replace('\\', '/');
			if (listed is not null && !listed.Contains(name + "/" + onDisk))
			{
				continue;
			}

			string relative = ModLoadOrder.Normalize(onDisk);
			string leaf = Path.GetFileName(relative);
			if (relative == ModWorkspace.ModMetadataFilename ||
				leaf.Equals("readme.md", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			files.Add((relative, File.ReadAllBytes(file)));
		}

		files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
		return files;
	}

	private static string HashSource(string metadataText, List<(string Path, byte[] Bytes)> files)
	{
		using var sha = Refii.Core.Engine_Hash.CreateSha256();
		byte[] metadata = Encoding.UTF8.GetBytes(metadataText + "\n");
		sha.TransformBlock(metadata, 0, metadata.Length, null, 0);
		foreach ((string path, byte[] bytes) in files)
		{
			byte[] name = Encoding.UTF8.GetBytes(path + "\n");
			sha.TransformBlock(name, 0, name.Length, null, 0);
			sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
		}

		sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
		return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
	}

	private static string HashFile(string path)
	{
		using FileStream stream = File.OpenRead(path);
		return Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(stream));
	}

	private static (string From, string As) ReadMarker(string marker)
	{
		if (!File.Exists(marker))
		{
			return (string.Empty, string.Empty);
		}

		string[] lines = File.ReadAllLines(marker);
		return (lines.Length > 0 ? lines[0].Trim() : string.Empty, lines.Length > 1 ? lines[1].Trim() : string.Empty);
	}
}
