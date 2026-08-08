using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Refii.Launcher.Formats;

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
	public readonly BitmapFont Font;
	public readonly Texture2D Atlas;

	public UiFont(BitmapFont font, Texture2D atlas)
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
public sealed class UiFontLibrary
{
	/// <summary>The culture every other one falls back to, per <c>cultures.txt</c>.</summary>
	public const string DefaultCulture = "en-uk";

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
	public static UiFontLibrary Load(string gameDataRoot, string culture = DefaultCulture)
	{
		var library = new UiFontLibrary();

		string directory = FontDirectory(gameDataRoot, culture)
			?? FontDirectory(gameDataRoot, DefaultCulture)
			?? string.Empty;

		if (directory.Length == 0)
		{
			library.Failures.Add($"no fonts directory under {gameDataRoot}/language");
			return library;
		}

		library.Directory = directory;

		foreach ((UiFontBucket bucket, string name) in FileNames)
		{
			string path = Path.Combine(directory, name);
			try
			{
				BitmapFont font = BitmapFont.Load(path);
				library._fonts[bucket] = new UiFont(font,
					BitmapFontTexture.CreateAtlasTextureDecompressed(font));
			}
			catch (Exception exception)
			{
				library.Failures.Add($"{name}: {exception.Message}");
			}
		}

		return library;
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
	/// The bucket a point size lands in, and the residual scale to draw it at.
	///
	/// From <c>sub_828715E0</c>, which compares the integer size against 18 and 24:
	/// below 18 takes GUI_Small scaled by <c>size/18</c>; exactly 18 takes GUI_Normal at
	/// 1.0; 19..24 takes GUI_Large at 1.0; above 24 takes GUI_Large scaled by
	/// <c>size/24</c>. There is no per-element font name — the point size is the whole
	/// input.
	///
	/// The SCALE is the part that is easy to miss, because three of the four cases and
	/// every bucket boundary are right without it. It only shows up as text at the wrong
	/// size for the sizes that do not sit exactly on a bucket.
	/// </summary>
	public static (UiFontBucket Bucket, float Scale) SelectFor(int pointSize) => pointSize switch
	{
		< 18 => (UiFontBucket.Small, pointSize / 18f),
		18 => (UiFontBucket.Normal, 1f),
		<= 24 => (UiFontBucket.Large, 1f),
		_ => (UiFontBucket.Large, pointSize / 24f),
	};

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
		(UiFontBucket wanted, float scale) = SelectFor(pointSize);

		if (_fonts.TryGetValue(wanted, out UiFont? exact))
			return (exact, scale);

		foreach (UiFontBucket fallback in new[] { UiFontBucket.Normal, UiFontBucket.Small, UiFontBucket.Large })
			if (_fonts.TryGetValue(fallback, out UiFont? any))
				return (any, scale);

		return null;
	}
}
