using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.Bnk;
using Refii.Textures;

namespace Refii.Mods;

/// <summary>One image a mod authored, and what became of it.</summary>
public sealed class ImageConversion
{
	public string Target = string.Empty;

	public string Mod = string.Empty;

	public int Width;

	public int Height;

	public TexSurfaceFormat Format;

	public string Error = string.Empty;

	public bool Ok => Error.Length == 0;
}

/// <summary>
/// Lets a mod ship a texture as `foo.tex.png` and have the game read `foo.tex`.
///
/// A MODDER HAS A PNG, NOT A TILED BC BLOB. The shipped form is Xbox 360 tiled, word-swapped and
/// block-compressed; asking someone to produce that by hand is asking them not to bother. This
/// converts at mount, the same way a `.gdb.csv` becomes a database — authored in a format people
/// have tools for, served in the format the game reads.
///
/// CONVERTED IN MEMORY, NEVER TO DISK. The result is a virtual file in the mount, so no derived
/// copy of anything is written and the mod folder keeps only what its author put there.
///
/// The format is chosen by the image: BC1 when it is fully opaque, BC3 when it is not. A mod that
/// needs a specific one can still ship a real `.tex`, which overrides as any other file does.
/// </summary>
public static class ModImageOverlays
{
	public const string Suffix = ".tex.png";

	public static List<ImageConversion> Apply(
		BnkFileSystem mounted, IReadOnlyList<ModOverlay> overlays)
	{
		var conversions = new List<ImageConversion>();

		foreach (ModOverlay overlay in overlays)
		{
			string sourcePath = !string.IsNullOrEmpty(overlay.PackagePath) ? overlay.PackagePath : overlay.AssetRoot;
			if (File.Exists(sourcePath) && sourcePath.EndsWith(".f2mod", StringComparison.OrdinalIgnoreCase))
			{
				if (F2ModPackage.TryOpen(sourcePath, out F2ModPackage? pkg, out _) && pkg is not null)
				{
					using (pkg)
					{
						foreach (F2ModEntry entry in pkg.Entries)
						{
							if (!entry.Path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
							{
								continue;
							}

							byte[] png;
							try
							{
								png = pkg.ReadEntryBytes(entry);
							}
							catch (Exception exception)
							{
								conversions.Add(new ImageConversion
								{
									Target = entry.Path,
									Mod = overlay.Name,
									Error = exception.Message,
								});
								continue;
							}

							string relative = entry.Path.Replace('\\', '/').TrimStart('/');
							if (relative.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
							{
								relative = relative["files/".Length..];
							}
							string target = ModLoadOrder.Normalize(relative);
							target = target[..^4];

							var conversion = new ImageConversion { Target = target, Mod = overlay.Name };
							if (!TryConvert(png, out byte[] tex, conversion))
							{
								conversions.Add(conversion);
								continue;
							}

							conversions.Add(conversion);
							mounted.AddVirtualFile(
								target, $"<image:{target}>", tex.LongLength, () => tex, overlay.Name);
						}
					}
				}
				continue;
			}

			if (!Directory.Exists(overlay.AssetRoot))
			{
				continue;
			}

			foreach (string file in ModManifest.EnumerateAssets(overlay.AssetRoot))
			{
				if (!file.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string relative;
				byte[] png;
				try
				{
					relative = Path.GetRelativePath(overlay.AssetRoot, file);
					png = File.ReadAllBytes(file);
				}
				catch (Exception exception)
				{
					conversions.Add(new ImageConversion
					{
						Target = file,
						Mod = overlay.Name,
						Error = exception.Message,
					});
					continue;
				}

				// `foo.tex.png` becomes `foo.tex`.
				string normRel = relative.Replace('\\', '/').TrimStart('/');
				if (normRel.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
				{
					normRel = normRel["files/".Length..];
				}
				string target = ModLoadOrder.Normalize(normRel);
				target = target[..^4];

				var conversion = new ImageConversion { Target = target, Mod = overlay.Name };
				if (!TryConvert(png, out byte[] tex, conversion))
				{
					conversions.Add(conversion);
					continue;
				}

				conversions.Add(conversion);

				mounted.AddVirtualFile(
					target, $"<image:{target}>", tex.LongLength, () => tex, overlay.Name);
			}
		}

		return conversions;
	}

	/// <summary>
	/// One PNG to one `.tex`.
	///
	/// Shared with the packer so a mod exported as a bank carries the same bytes the mount would
	/// have served — a packed mod that converted differently from the loose one it was made from
	/// would be a different mod.
	/// </summary>
	public static bool TryConvert(byte[] png, out byte[] tex, ImageConversion conversion)
	{
		tex = Array.Empty<byte>();

		var image = new Image();
		Error loaded = image.LoadPngFromBuffer(png);
		if (loaded != Error.Ok)
		{
			conversion.Error = $"the PNG could not be read ({loaded}).";
			return false;
		}

		if (image.GetFormat() != Image.Format.Rgba8)
		{
			image.Convert(Image.Format.Rgba8);
		}

		byte[] rgba = image.GetData();
		int width = image.GetWidth();
		int height = image.GetHeight();
		TexSurfaceFormat format = Engine_TexWriter.ChooseFormat(rgba, width, height);

		if (!Engine_TexWriter.TryWrite(rgba, width, height, format, out tex, out string error))
		{
			conversion.Error = error;
			return false;
		}

		conversion.Width = width;
		conversion.Height = height;
		conversion.Format = format;
		return true;
	}

	public static string Describe(ImageConversion conversion) =>
		conversion.Ok
			? $"{conversion.Target}: {conversion.Width}x{conversion.Height} {conversion.Format} " +
			  $"from {conversion.Mod}"
			: $"{conversion.Target} ({conversion.Mod}): {conversion.Error}";
}
