using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Refii.Fx;

/// <summary>
/// The low-memory tier's effect texture prefetch: each prepared (ASTC) image waits on disk instead
/// of in memory, and its first use reads it back.
/// </summary>
/// <remarks>
/// The prefetch held every bank effect texture, prepared for upload, until an effect first named
/// it: 18.3-18.4 MB on the S10e and OnePlus 7 Pro (the prefetch's own log line), most of it never
/// used in a level. The file is the transcoder's own output for the same source bytes, so the
/// texture is the same. A launch whose files are there skips the transcode; a missing or unreadable
/// file sends the name down the synchronous path, as a texture the prefetch had not reached.
/// </remarks>
public partial class FableFx
{
	private static readonly Lazy<bool> PrefetchToDisk = new(() =>
		Refii.Core.Engine_Platform.IsAndroid && Refii.Game.AndroidMemoryRelease.LowRamMode);

	private static readonly string PrefetchStamp =
		typeof(FableFx).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];

	/// <summary>Where each name's prepared image waits, by name; read on the first use only.</summary>
	private static readonly Dictionary<string, (string Path, int Width, int Height, bool Mipmaps)> PrefetchFiles =
		new(StringComparer.OrdinalIgnoreCase);

	private static string PrefetchDirectory =>
		Path.Combine(Refii.Core.Engine_Title.Active.CacheDirectory, "fx");

	/// <summary>Drops the files an earlier build wrote; on the prefetch thread, before its first write.</summary>
	private static void PurgeStalePrefetchFiles()
	{
		if (!PrefetchToDisk.Value) return;
		try
		{
			if (!Directory.Exists(PrefetchDirectory)) return;
			foreach (string file in Directory.EnumerateFiles(PrefetchDirectory))
				if (Engine_FxPrefetchFiles.IsStale(Path.GetFileName(file), PrefetchStamp)) File.Delete(file);
		}
		catch (Exception) { }
	}

	/// <summary>
	/// In the low-memory tier, an image the upload would send to ASTC goes to (or is already in) its
	/// file, and an empty stand-in is held in its place; anything else comes back as it was.
	/// </summary>
	/// <param name="name">The bank's texture name.</param>
	/// <param name="bytes">The texture file's bytes as the mount served them, which key the file.</param>
	/// <param name="image">The decoded, not yet prepared, image; disposed when a stand-in comes back.</param>
	/// <param name="prepared">Set when this call transcoded.</param>
	private static Image? KeepPrefetchOnDisk(string name, byte[] bytes, Image image, out bool prepared)
	{
		prepared = false;
		if (!PrefetchToDisk.Value) return null;
		Image.Format source = image.GetFormat();
		if (Refii.Textures.Engine_TextureUpload.Policy.Target(source) != Image.Format.Astc4X4) return null;

		int width = image.GetWidth(), height = image.GetHeight();
		bool mipmaps = image.HasMipmaps();
		string path = Path.Combine(PrefetchDirectory, Engine_FxPrefetchFiles.FileName(
			Refii.Core.Engine_Hash.Sha256(bytes), (int)source, width, height, mipmaps, PrefetchStamp));
		if (!File.Exists(path))
		{
			Image ready = Refii.Textures.Engine_TextureUpload.PrepareAhead(image);
			bool written = ready.GetFormat() == Image.Format.Astc4X4 && Refii.Textures.Engine_PreparedImageFile.TryWrite(
				path, width, height, (int)Image.Format.Astc4X4, mipmaps, ready.GetData());
			if (!ReferenceEquals(ready, image)) prepared = true;
			if (!written)
			{
				// Held in memory as before.
				if (!ReferenceEquals(ready, image)) image.Dispose();
				return ready;
			}

			if (!ReferenceEquals(ready, image)) ready.Dispose();
		}

		image.Dispose();
		lock (PrefetchFiles) PrefetchFiles[name] = (path, width, height, mipmaps);
		return new Image();
	}

	/// <summary>The image a prefetch handed in: itself, its file's contents for a stand-in, or null when the file failed.</summary>
	private static Image? TakePrefetched(string name, Image prefetched)
	{
		if (!prefetched.IsEmpty()) return prefetched;
		prefetched.Dispose();
		(string Path, int Width, int Height, bool Mipmaps) file;
		lock (PrefetchFiles)
		{
			if (!PrefetchFiles.Remove(name, out file)) return null;
		}

		byte[]? data = Refii.Textures.Engine_PreparedImageFile.TryRead(
			file.Path, file.Width, file.Height, (int)Image.Format.Astc4X4, file.Mipmaps);
		if (data is null) return null;
		var image = new Image();
		image.SetData(file.Width, file.Height, file.Mipmaps, Image.Format.Astc4X4, data);
		if (!image.IsEmpty()) return image;
		image.Dispose();
		return null;
	}
}

/// <summary>The low-memory tier's prefetch file names (FableFx.PrefetchFiles): no Godot type, so testable.</summary>
public static class Engine_FxPrefetchFiles
{
	/// <summary>
	/// <c>fx_&lt;hash&gt;_&lt;format&gt;_&lt;w&gt;x&lt;h&gt;[m]_&lt;build&gt;.bin</c>: the source file's
	/// SHA-256 (its first 16 bytes), its decoded format, size and mipmaps, and the build, which
	/// carries the transcoder.
	/// </summary>
	public static string FileName(ReadOnlySpan<byte> sha256, int format, int width, int height, bool mipmaps, string build) =>
		$"fx_{Convert.ToHexString(sha256[..16]).ToLowerInvariant()}_{format}_{width}x{height}{(mipmaps ? "m" : string.Empty)}_{build}.bin";

	/// <summary>A prefetch file (or a temporary one) another build wrote; anything else in the folder is left.</summary>
	public static bool IsStale(string fileName, string build) =>
		fileName.StartsWith("fx_", StringComparison.Ordinal) &&
		!fileName.EndsWith($"_{build}.bin", StringComparison.Ordinal) &&
		!fileName.Contains($"_{build}.bin.", StringComparison.Ordinal);
}
