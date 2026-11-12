namespace Refii.Core;

using System;
using System.IO;
using Godot;

/// <summary>
/// The application and game window icon: the shared non-copyright icon (fable.ico / icon.png)
/// used across the launcher and both titles (Fable 2 and Fable 3).
/// </summary>
public static class Engine_TitleIcon
{
	/// <summary>The XDBF image id of a title's own image (historical reference).</summary>
	public const uint XdbfTitleImageId = 0x8000u;

	/// <summary>Raw decoded icon payload, independent of Godot unmanaged runtime.</summary>
	public readonly record struct DecodedIco(int Width, int Height, byte[]? Rgba, byte[]? PngBytes);

	private static Image? _cachedIcon;

	/// <summary>
	/// Pure C# parser that decodes an ICO byte buffer into raw RGBA or PNG bytes.
	/// Safe to execute in tests without the Godot engine process.
	/// </summary>
	public static DecodedIco? ParseIco(byte[]? data)
	{
		if (data is null || data.Length < 6)
		{
			return null;
		}

		ushort type = BitConverter.ToUInt16(data, 2);
		ushort count = BitConverter.ToUInt16(data, 4);
		if (type != 1 || count == 0)
		{
			return null;
		}

		int bestIndex = -1;
		int bestScore = -1;
		for (int i = 0; i < count; i++)
		{
			int entryOffset = 6 + (i * 16);
			if (entryOffset + 16 > data.Length)
			{
				break;
			}

			int w = data[entryOffset] == 0 ? 256 : data[entryOffset];
			int h = data[entryOffset + 1] == 0 ? 256 : data[entryOffset + 1];
			ushort bpp = BitConverter.ToUInt16(data, entryOffset + 6);
			int score = (w * h) + bpp;
			if (score > bestScore)
			{
				bestScore = score;
				bestIndex = i;
			}
		}

		if (bestIndex < 0)
		{
			return null;
		}

		int selectedEntry = 6 + (bestIndex * 16);
		uint size = BitConverter.ToUInt32(data, selectedEntry + 8);
		uint offset = BitConverter.ToUInt32(data, selectedEntry + 12);
		if (offset + size > (uint)data.Length)
		{
			return null;
		}

		// 1. PNG-in-ICO
		if (size >= 8 &&
			data[offset] == 0x89 &&
			data[offset + 1] == 0x50 && // P
			data[offset + 2] == 0x4E && // N
			data[offset + 3] == 0x47)   // G
		{
			byte[] pngBytes = new byte[size];
			Buffer.BlockCopy(data, (int)offset, pngBytes, 0, (int)size);
			int w = data[selectedEntry] == 0 ? 256 : data[selectedEntry];
			int h = data[selectedEntry + 1] == 0 ? 256 : data[selectedEntry + 1];
			return new DecodedIco(w, h, null, pngBytes);
		}

		// 2. DIB/BMP-in-ICO
		if (size >= 40)
		{
			uint biSize = BitConverter.ToUInt32(data, (int)offset);
			int biWidth = BitConverter.ToInt32(data, (int)offset + 4);
			int biHeight = BitConverter.ToInt32(data, (int)offset + 8);
			ushort biBitCount = BitConverter.ToUInt16(data, (int)offset + 14);
			uint biCompression = BitConverter.ToUInt32(data, (int)offset + 16);

			int imgWidth = biWidth;
			int imgHeight = biHeight / 2; // In ICO DIB, biHeight includes XOR and AND mask heights

			if (biBitCount == 32 && biCompression == 0 && imgWidth > 0 && imgHeight > 0)
			{
				int pixelDataOffset = (int)offset + (int)biSize;
				int rowStride = imgWidth * 4;
				if (pixelDataOffset + (rowStride * imgHeight) <= data.Length)
				{
					byte[] rgba = new byte[imgWidth * imgHeight * 4];
					for (int y = 0; y < imgHeight; y++)
					{
						int srcRow = pixelDataOffset + ((imgHeight - 1 - y) * rowStride);
						int dstRow = y * rowStride;
						for (int x = 0; x < imgWidth; x++)
						{
							int srcPx = srcRow + (x * 4);
							int dstPx = dstRow + (x * 4);
							rgba[dstPx + 0] = data[srcPx + 2]; // R
							rgba[dstPx + 1] = data[srcPx + 1]; // G
							rgba[dstPx + 2] = data[srcPx + 0]; // B
							rgba[dstPx + 3] = data[srcPx + 3]; // A
						}
					}

					return new DecodedIco(imgWidth, imgHeight, rgba, null);
				}
			}
		}

		return null;
	}

	/// <summary>
	/// Decodes an ICO byte buffer into a Godot <see cref="Image"/>.
	/// Supports both PNG-compressed and 32-bit DIB/BMP icon payloads.
	/// </summary>
	public static Image? DecodeIco(byte[]? data)
	{
		if (ParseIco(data) is not { } parsed)
		{
			return null;
		}

		if (parsed.PngBytes is not null)
		{
			var img = new Image();
			if (img.LoadPngFromBuffer(parsed.PngBytes) == Error.Ok && !img.IsEmpty())
			{
				return img;
			}
			return null;
		}

		if (parsed.Rgba is not null)
		{
			return Image.CreateFromData(parsed.Width, parsed.Height, false, Image.Format.Rgba8, parsed.Rgba);
		}

		return null;
	}

	/// <summary>
	/// Loads the shared application/game icon, trying external runtime overrides,
	/// then res:// assets (fable.ico, fable.png, icon.png).
	/// </summary>
	public static Image? LoadIcon()
	{
		if (_cachedIcon is not null && !_cachedIcon.IsEmpty())
		{
			return _cachedIcon;
		}

		// 1. External overrides beside the launcher
		try
		{
			string externalIco = Refii.Launcher.Core.LauncherPaths.GetLauncherAssetPath("fable.ico");
			if (File.Exists(externalIco))
			{
				byte[] icoBytes = File.ReadAllBytes(externalIco);
				if (DecodeIco(icoBytes) is { } ico && !ico.IsEmpty())
				{
					return _cachedIcon = ico;
				}
			}

			string externalPng = Refii.Launcher.Core.LauncherPaths.GetLauncherAssetPath("icon.png");
			if (File.Exists(externalPng))
			{
				Image ext = Image.LoadFromFile(externalPng);
				if (ext is not null && !ext.IsEmpty())
				{
					return _cachedIcon = ext;
				}
			}
		}
		catch (Exception ex)
		{
			GD.PushWarning($"[title-icon] failed reading external icon: {ex.Message}");
		}

		// 2. Bundled res://assets/launcher/fable.ico
		if (Godot.FileAccess.FileExists("res://assets/launcher/fable.ico"))
		{
			byte[] bundledIcoBytes = Godot.FileAccess.GetFileAsBytes("res://assets/launcher/fable.ico");
			if (DecodeIco(bundledIcoBytes) is { } ico && !ico.IsEmpty())
			{
				return _cachedIcon = ico;
			}
		}

		// 3. Bundled res://assets/launcher/icon.png or fable.png
		foreach (string resPath in new[] { "res://assets/launcher/fable.png", "res://assets/launcher/icon.png" })
		{
			if (ResourceLoader.Exists(resPath))
			{
				var tex = ResourceLoader.Load<Texture2D>(resPath);
				Image? img = tex?.GetImage();
				if (img is not null && !img.IsEmpty())
				{
					return _cachedIcon = img;
				}
			}
		}

		return null;
	}

	/// <summary>Applies the shared non-copyright icon to the main window.</summary>
	public static void Apply()
	{
		Image? icon = LoadIcon();
		if (icon is not null && !icon.IsEmpty())
		{
			DisplayServer.SetIcon(icon);
			GD.Print($"[title-icon] applied window icon ({icon.GetWidth()}x{icon.GetHeight()})");
		}
	}

	/// <summary>
	/// Maintained for achievements callback: keeps the shared non-copyright icon
	/// for both Fable 2 and Fable 3 instead of replacing it with copyrighted dashboard images.
	/// </summary>
	public static void Adopt(Refii.Achievements.Engine_XdbfDatabase _)
	{
		Apply();
	}
}
