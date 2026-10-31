using Refii.Mods;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Refii.AndroidRuntime;

/// <summary>The mods that ship with the APK: <c>Fable2/mods-src</c> zipped by the export, unpacked to
/// <c>runtime/bundled-mods</c> and packed into the mods folder switched off, as the desktop launcher does.</summary>
/// <remarks>The desktop release carries the trees as files beside the binary; an APK's resources
/// are not files, so tools/android/export-apk.py zips them with the release's <c>manifest.txt</c>.
/// The unpacked copy is replaced whenever the archive changes, and <see cref="ModBundled.Install"/>
/// keeps its own rules: installed disabled, rewritten on an update only while the player has not
/// edited the package.</remarks>
public static class AndroidBundledMods
{
    /// <summary>The archive the export stages into the project.</summary>
    public const string Resource = "res://bundled-mods.zip";

    /// <summary>Beside the unpacked trees: the SHA-256 of the archive they came from.</summary>
    public const string Marker = ".archive-sha256";

    /// <summary>Unpacks the shipped archive, if there is one, and packs its mods into <paramref name="modsRoot"/>.</summary>
    /// <param name="target">Where the trees are unpacked (<c>Engine_AppRoot.BundledModsDirectory</c>).</param>
    /// <param name="modsRoot">The mods folder.</param>
    /// <returns>One line per mod, as <see cref="ModBundled.Install"/> reports it; empty without an archive.</returns>
    public static List<string> Install(string target, string modsRoot)
    {
        if (!Godot.FileAccess.FileExists(Resource)) return new List<string>();
        Unpack(Godot.FileAccess.GetFileAsBytes(Resource), target);
        return ModBundled.Install(target, modsRoot, Refii.Core.Engine_Title.Active.ModExtension);
    }

    /// <summary>Replaces <paramref name="target"/> with the archive's trees unless it already holds this archive.</summary>
    /// <param name="archive">The zip's bytes.</param>
    /// <param name="target">The unpacked folder; its parent must be writable.</param>
    /// <returns>Whether it unpacked (false: the folder already held this archive).</returns>
    public static bool Unpack(byte[] archive, string target)
    {
        string identity = Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(archive)).ToLowerInvariant();
        string marker = Path.Combine(target, Marker);
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == identity) return false;
        target = Path.GetFullPath(target);
        string staging = target + ".unpacking";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        using (var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string path = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    entry.FullName.Contains('\\') || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                    throw new InvalidDataException("Unsafe bundled mod entry: " + entry.FullName);
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path);
            }
        }
        File.WriteAllText(Path.Combine(staging, Marker), identity);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(staging, target);
        return true;
    }
}
