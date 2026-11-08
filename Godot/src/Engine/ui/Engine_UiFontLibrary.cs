using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;

using Refii.Core;

using Refii.Text;

using Refii.Textures;

namespace Refii.Ui;

/// <summary>The three GUI font buckets a text element can land in.</summary>
public enum UiFontBucket
{
	Small,
	Normal,
	Large,
}

/// <summary>One loaded font: the parsed <c>.bft</c> plus its atlas as a Godot texture.</summary>
public sealed class UiFont
{
	public readonly Engine_BitmapFont Font;
	public readonly Texture2D Atlas;

	public UiFont(Engine_BitmapFont font, Texture2D atlas)
	{
		Font = font;
		Atlas = atlas;
	}
}

/// <summary>
/// Finds and loads the GUI bitmap fonts for a culture.
///
/// Fonts are LOOSE FILES, not bank entries — <c>language/&lt;culture&gt;/fonts/gui_{small,
/// normal,large}.bft</c> — so this reads the filesystem rather than going through
/// <see cref="BankFileSystem"/>. Only a couple of cultures ship their own set; the rest
/// fall back to the default, which is what <c>language/cultures.txt</c> describes.
///
/// The atlas is decompressed to RGBA8 rather than handed over as DXT1. A glyph atlas is
/// sampled at close to 1:1 and BC1's 5:6:5 endpoints visibly fringe antialiased text at
/// that size; the whole set is under a megabyte decompressed.
/// </summary>
public sealed class Engine_UiFontLibrary
{
	/// <summary>The culture every other one falls back to, per <c>cultures.txt</c>.</summary>
	public const string DefaultCulture = Refii.Text.Engine_GameCulture.Fallback;

	private readonly Dictionary<UiFontBucket, UiFont> _fonts = new();

	/// <summary>Where the fonts were loaded from, for diagnostics.</summary>
	public string Directory { get; private set; } = string.Empty;

	/// <summary>Why a bucket is missing, keyed by file name. A silent gap diagnoses nothing.</summary>
	public readonly List<string> Failures = new();

	public bool IsEmpty => _fonts.Count == 0;

	/// <summary>
	/// Loads the three buckets for a culture, falling back to
	/// <see cref="DefaultCulture"/> when that culture ships no fonts of its own.
	/// Never throws — a screen without text still renders.
	/// </summary>
	public static Engine_UiFontLibrary Load(string gameDataRoot, string culture = DefaultCulture)
	{
		var library = new Engine_UiFontLibrary();

		string directory = DirectoryFor(gameDataRoot, culture);
		if (directory.Length == 0)
		{
			library.Failures.Add($"no fonts directory under {gameDataRoot}/language");
			return library;
		}

		library.LoadFrom(directory);
		return library;
	}

	/// <summary>
	/// Reads the buckets again for another culture, IN PLACE, so everything holding this library
	/// draws the new glyphs. True when the folder changed and was read; false when the culture
	/// resolves to the folder already loaded (hu-hu, cs-cz, pl-pl and sk-sk ship no fonts and use
	/// en-uk's, as the guest does) or to none, which keeps what is loaded.
	/// </summary>
	public bool Reload(string gameDataRoot, string culture)
	{
		string directory = DirectoryFor(gameDataRoot, culture);
		if (directory.Length == 0 || string.Equals(directory, Directory, StringComparison.Ordinal))
		{
			return false;
		}

		_fonts.Clear();
		Failures.Clear();
		LoadFrom(directory);
		return true;
	}

	/// <summary>
	/// The first fonts folder of <see cref="Refii.Text.Engine_GameCulture.SearchOrder"/> — the
	/// culture's own, then <see cref="DefaultCulture"/>'s, then any installed culture's, the guest's
	/// remap order; empty when none exists.
	/// </summary>
	private static string DirectoryFor(string gameDataRoot, string culture)
	{
		foreach (string folder in Refii.Text.Engine_GameCulture.SearchOrder(culture))
		{
			if (FontDirectory(gameDataRoot, folder) is { } directory)
			{
				return directory;
			}
		}

		return string.Empty;
	}

	private void LoadFrom(string directory)
	{
		Directory = directory;

		// By name, ignoring case: zh-cht ships `GUI_Large.bft`, the others `gui_large.bft`, and
		// the 360's file system never told them apart.
		string[] present;
		try
		{
			present = System.IO.Directory.GetFiles(directory);
		}
		catch (Exception exception)
		{
			Failures.Add($"{directory}: {exception.Message}");
			return;
		}

		// THE THREE ATLASES ARE BUILT TOGETHER. Each is a cubic upsample with mips on one CPU thread,
		// and one after another they took about 0.75 s of the main thread on a phone while the UI
		// first stands up. Only the textures are made here, in the buckets' order.
		var builds = new List<(UiFontBucket Bucket, string Name, Task<(Engine_BitmapFont Font, Image Atlas, int Factor)> Work)>();
		foreach ((UiFontBucket bucket, string name) in FileNames)
		{
			string path = Array.Find(present, file =>
				string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
				?? Path.Combine(directory, name);
			builds.Add((bucket, name, Task.Run(() =>
			{
				Engine_BitmapFont font = Engine_BitmapFont.Load(path);
				Image atlas = Engine_BitmapFontTexture.CreateAtlasImage(font, out int factor);
				return (font, atlas, factor);
			})));
		}

		foreach (var build in builds)
		{
			try
			{
				(Engine_BitmapFont font, Image atlas, int factor) = build.Work.GetAwaiter().GetResult();
				_fonts[build.Bucket] = new UiFont(font, Engine_BitmapFontTexture.CreateAtlasTexture(font, atlas, factor));
			}
			catch (Exception exception)
			{
				Failures.Add($"{build.Name}: {exception.Message}");
			}
		}
	}

	private static readonly (UiFontBucket, string)[] FileNames =
	{
		(UiFontBucket.Small, "gui_small.bft"),
		(UiFontBucket.Normal, "gui_normal.bft"),
		(UiFontBucket.Large, "gui_large.bft"),
	};

	/// <summary>
	/// The <c>language/</c> tree, relative to whichever root the caller mounted.
	///
	/// Two roots are in play and they differ by one level: the engine's bank filesystem
	/// mounts <c>assets/game</c>, while <c>language/</c> sits under <c>assets/game/data</c>
	/// beside the banks. Accepting either is what stops fonts silently vanishing when the
	/// mount root changes — the symptom is text laying out at the right size and position
	/// with no glyphs at all.
	/// </summary>
	public static string? LanguageSubdirectory(string root, string culture, string leaf)
	{
		foreach (string prefix in new[] { root, Path.Combine(root, "data") })
		{
			string candidate = Path.Combine(prefix, "language", culture, leaf);
			if (System.IO.Directory.Exists(candidate))
				return candidate;
		}

		return null;
	}

	private static string? FontDirectory(string gameDataRoot, string culture) =>
		LanguageSubdirectory(gameDataRoot, culture, "fonts");

	/// <summary>
	/// The bucket a point size lands in, and the residual scale to draw it at, under the active
	/// title's <see cref="Engine_UiTextContract"/>. There is no per-element font name in either
	/// title — Fable 3's `.bgf` carries the artist's face name ("fertigo pro", "maiandra gd") but
	/// the element reads only its `.fac` record (`CTextAsset_vf1` 0x82B45E30) and the attributes
	/// TextID, size, truncate and textcolor.r/g/b (`CTextAsset_vf3` 0x82B46190); the point size is
	/// the whole input.
	/// </summary>
	public static (UiFontBucket Bucket, float Scale) SelectFor(int pointSize) =>
		Engine_UiTextContract.Active.SelectFor(pointSize);

	/// <summary>
	/// The text contract this library's fonts are laid out under — the title whose data they were
	/// read from. Fable 2's buckets sit at 18/24 and scale off them; Fable 3's at 16 and do not.
	/// </summary>
	public Engine_UiTextContract Contract { get; set; } = Engine_UiTextContract.Active;

	/// <summary>The bucket's own font at its authored size, or null when that file did not load.</summary>
	public UiFont? Get(UiFontBucket bucket) => _fonts.TryGetValue(bucket, out UiFont? font) ? font : null;

	/// <summary>The bucket alone, for naming and diagnostics.</summary>
	public static UiFontBucket BucketFor(int pointSize) => SelectFor(pointSize).Bucket;

	/// <summary>
	/// The font for a point size and the scale to draw it at, or null when nothing
	/// loaded at all. A missing bucket falls back to another rather than dropping the
	/// string — but the scale is left alone, since it is about the point size and not
	/// about which file answered.
	/// </summary>
	public (UiFont Font, float Scale)? For(int pointSize)
	{
		(UiFontBucket wanted, float scale) = Contract.SelectFor(pointSize);

		if (_fonts.TryGetValue(wanted, out UiFont? exact))
			return (exact, scale);

		foreach (UiFontBucket fallback in new[] { UiFontBucket.Normal, UiFontBucket.Small, UiFontBucket.Large })
			if (_fonts.TryGetValue(fallback, out UiFont? any))
				return (any, scale);

		return null;
	}
}
