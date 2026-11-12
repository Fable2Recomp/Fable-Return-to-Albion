using Refii.Mods;
using System;
using System.IO;
using System.Text;

namespace Refii.AndroidRuntime;

/// <summary>Brings a .f2mod picked in Android's document picker into the mods folder's downloads, ready for the Modding panel's install.</summary>
/// <remarks>The picker hands back a content URI, not a path, and the installer names a mod after its
/// file. A URI from the file manager usually ends in the file's name (<c>…%2FDownload%2Fhotbar.f2mod</c>);
/// one from the Downloads list may be a bare id (<c>msf:1234</c>), so the package's own title
/// names it then. The copy lands where a link's download does, so the panel's confirm-then-install
/// and its discard of the copy work the same for both.</remarks>
public static class AndroidModPicker
{
    /// <summary>The mod name a picked document's URI carries: its last path segment, without <paramref name="packageExtension"/>; null when it names no package.</summary>
    /// <param name="uri">The picker's content URI (or a plain path).</param>
    /// <param name="packageExtension">Optional mod package extension (defaults to <see cref="ModWorkspace.PackageExtension"/>).</param>
    public static string? NameFromUri(string uri, string? packageExtension = null)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        string ext = string.IsNullOrEmpty(packageExtension) ? ModWorkspace.PackageExtension : packageExtension;
        string decoded;
        try { decoded = Uri.UnescapeDataString(uri); }
        catch (UriFormatException) { decoded = uri; }
        string leaf = decoded[(decoded.LastIndexOfAny(new[] { '/', ':' }) + 1)..];
        if (!leaf.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return null;
        string name = leaf[..^ext.Length].Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>A mod name made from a package's title: letters and digits kept, lower case, runs of anything else one '_'.</summary>
    /// <param name="title">The package's metadata title; empty gives <c>mod</c>.</param>
    public static string NameFromTitle(string title)
    {
        var name = new StringBuilder();
        foreach (char c in title.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c)) name.Append(char.ToLowerInvariant(c));
            else if (name.Length > 0 && name[^1] != '_') name.Append('_');
        }
        string result = name.ToString().Trim('_');
        if (result.Length > 48) result = result[..48].TrimEnd('_');
        return result.Length == 0 ? "mod" : result;
    }

    /// <summary>Copies the picked document into <see cref="ModInstaller.DownloadsDirectory"/> under its mod name and inspects it.</summary>
    /// <param name="uri">The picker's content URI.</param>
    /// <param name="modsRoot">The mods folder.</param>
    /// <param name="copyTo">Writes the document's bytes to the given new file (Godot's FileAccess on the phone).</param>
    /// <param name="packageExtension">Optional mod package extension (defaults to <see cref="ModWorkspace.PackageExtension"/>).</param>
    /// <returns>The installer's inspection of the copy; a failed one names why. The copy stays for the
    /// panel to install or discard, and is removed here when it is not a package.</returns>
    public static ModInstallResult Fetch(string uri, string modsRoot, Action<string> copyTo, string? packageExtension = null)
    {
        string ext = string.IsNullOrEmpty(packageExtension) ? ModWorkspace.PackageExtension : packageExtension;
        string downloads = ModInstaller.DownloadsDirectory(modsRoot);
        Directory.CreateDirectory(downloads);
        string partial = Path.Combine(downloads, "picked-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            copyTo(partial);
            string? name = NameFromUri(uri, ext);
            if (name == null)
            {
                if (!F2ModPackage.TryReadMetadata(partial, out ModMetadata meta, out string error))
                    return ModInstallResult.Failed("The picked file is not a mod package: " + error);
                name = NameFromTitle(ModText.ForMod(partial, meta.Title));
            }
            string final = Path.Combine(downloads, name + ext);
            File.Move(partial, final, overwrite: true);
            ModInstallResult result = ModInstaller.Inspect(final, modsRoot, ext);
            if (!result.Ok) ModInstaller.DiscardDownload(final, modsRoot);
            return result;
        }
        catch (Exception error)
        {
            return ModInstallResult.Failed("Could not read the picked file: " + error.Message);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    /// <summary>Streams a picked document to <paramref name="target"/> through Godot, which reads content URIs.</summary>
    /// <param name="uri">The picker's content URI.</param>
    /// <param name="target">A new file to write.</param>
    public static void CopyDocument(string uri, string target)
    {
        using var source = Godot.FileAccess.Open(uri, Godot.FileAccess.ModeFlags.Read)
            ?? throw new IOException("Cannot read the picked file (" + Godot.FileAccess.GetOpenError() + ")");
        ulong length = source.GetLength();
        if (length == 0 || length > (ulong)ModInstaller.MaxPackageBytes)
            throw new InvalidDataException("The picked file has an unsupported size");
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        while (source.GetPosition() < length)
        {
            byte[] data = source.GetBuffer((long)Math.Min(1024UL * 1024, length - source.GetPosition()));
            if (data.Length == 0) throw new EndOfStreamException("The picked file ended early");
            output.Write(data);
        }
    }
}
