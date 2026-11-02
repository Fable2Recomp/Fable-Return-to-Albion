using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Refii.Ui;

/// <summary>
/// A GLYPH PACK: a folder of PNGs named by button, drawn in place of the built-in glyphs — the
/// player's own under <c>user://glyphs/&lt;pack&gt;/</c>, or a mod's <c>glyphs/&lt;pack&gt;/</c>
/// shared through the mod mount. The pack is content the core reads; no mod code runs. The
/// player's folder wins over a mod's of the same name, file by file; a missing file falls back to
/// the selected style.
/// </summary>
/// <remarks>
/// <para>Pad files name the PHYSICAL input by its Xbox position, lowercase: <c>a</c> (south),
/// <c>b</c> (east), <c>x</c> (west), <c>y</c> (north), <c>lb</c> <c>rb</c> <c>lt</c> <c>rt</c>
/// <c>start</c> <c>back</c> <c>ls</c> <c>rs</c> (<c>l3</c>/<c>r3</c> also read), <c>dpad</c>,
/// <c>dpad_up</c> <c>dpad_down</c> <c>dpad_left</c> <c>dpad_right</c>. Keyboard files are
/// <c>key_</c> + the keycap's label lowercased (<c>key_e</c>, <c>key_space</c>, <c>key_lmb</c>,
/// <c>key_esc</c>). Any size; square art fills a prompt best. Selected by <c>glyph_pack</c>.</para>
/// </remarks>
public static class Engine_UiGlyphPack
{
	/// <summary>The player's packs, and the folder a mod ships them under.</summary>
	public const string Folder = "glyphs";

	private static readonly Dictionary<string, Image?> _images = new(StringComparer.Ordinal);

	private static string _loadedPack = string.Empty;

	/// <summary>Bumped when the pack in use changes, so a redraw follows.</summary>
	public static int Generation { get; private set; }

	/// <summary>The player's packs folder on disk.</summary>
	public static string UserRoot => ProjectSettings.GlobalizePath("user://" + Folder);

	/// <summary>Every pack on offer: the player's folders and the enabled mods', by name, sorted.</summary>
	public static List<string> Available()
	{
		var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			if (Directory.Exists(UserRoot))
			{
				foreach (string dir in Directory.GetDirectories(UserRoot))
				{
					names.Add(Path.GetFileName(dir));
				}
			}
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			GD.PushWarning("[glyphs] " + error.Message);
		}

		foreach (string entry in Refii.Mods.ModEntries.EnabledEntryPaths(Folder + "/"))
		{
			string[] parts = entry.Split('/');
			if (parts.Length >= 3 && parts[1].Length != 0)
			{
				names.Add(parts[1]);
			}
		}

		return new List<string>(names);
	}

	/// <summary>
	/// The image a pack draws for <paramref name="name"/> (a file name without extension), or null
	/// when the pack has none — or no pack is selected.
	/// </summary>
	public static Image? Find(string pack, string name)
	{
		if (pack.Length == 0 || name.Length == 0 || !Refii.Mods.ModWorkspace.IsSafeModFolderName(pack))
		{
			return null;
		}

		if (!string.Equals(pack, _loadedPack, StringComparison.Ordinal))
		{
			_loadedPack = pack;
			_images.Clear();
			++Generation;
		}

		if (_images.TryGetValue(name, out Image? cached))
		{
			return cached;
		}

		Image? image = Load(pack, name);
		_images[name] = image;
		return image;
	}

	/// <summary>Drops every cached image: the pack's files may have changed on disk.</summary>
	public static void Forget()
	{
		_images.Clear();
		++Generation;
	}

	private static Image? Load(string pack, string name)
	{
		byte[]? bytes = null;
		string file = Path.Combine(UserRoot, pack, name + ".png");
		try
		{
			if (File.Exists(file))
			{
				bytes = File.ReadAllBytes(file);
			}
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			GD.PushWarning($"[glyphs] {file}: {error.Message}");
		}

		bytes ??= Refii.Mods.ModEntries.ReadEnabledBytes($"{Folder}/{pack}/{name}.png");
		if (bytes is null || bytes.Length == 0)
		{
			return null;
		}

		var image = new Image();
		if (image.LoadPngFromBuffer(bytes) != Error.Ok || image.IsEmpty())
		{
			GD.PushWarning($"[glyphs] {pack}/{name}.png is not a readable PNG; the built-in glyph stands");
			return null;
		}

		image.Convert(Image.Format.Rgba8);
		return image;
	}
}
