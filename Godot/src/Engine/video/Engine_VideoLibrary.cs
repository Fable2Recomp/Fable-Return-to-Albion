using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Refii.Video;

/// <summary>
/// The game's movie folders, indexed on their own.
///
/// Videos are their own area, like the fonts and the terrain: Fable 2 ships every .bik
/// LOOSE — the bank index contains none of them — so the bank filesystem is the wrong place
/// to look, and widening it to index the entire data folder to catch one format would make
/// every mount pay for it.
///
/// There are TWO folders. <c>art/videos</c> holds the logos, the intro, the ability previews
/// and the wordless cutscenes. <c>language/&lt;culture&gt;/videos</c> holds the narrated ones —
/// childbirth, fall_m/fall_f (the Spire window after Lucien), herospire, historyalbion,
/// marriage, newbeginnings — which the guest opens as
/// <c>game:\data\language\en-uk\videos\Fall_m.bik</c> through `PlayLocalisedMovieInternal`.
/// Indexing only the first folder is how every one of those played as a black screen.
///
/// Small and cheap: a directory listing per folder, no file reads until something asks.
/// </summary>
public sealed class Engine_VideoLibrary
{
	private readonly Dictionary<string, string> _byName =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Keyed by the path relative to the data root, forward slashes, lower case.</summary>
	private readonly Dictionary<string, string> _byRelativePath =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Movie names without extension, in listing order.</summary>
	public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

	/// <summary>The data root the folders were found under, or empty when none was found.</summary>
	public string Directory { get; private set; } = string.Empty;

	public int Count => _byName.Count;

	/// <summary>
	/// Indexes <c>art/videos</c> and every <c>language/*/videos</c> beneath a game data root.
	/// Installs differ in whether the root already includes the <c>data</c> level, so both
	/// are tried.
	/// </summary>
	public static Engine_VideoLibrary Load(string gameDataRoot)
	{
		var library = new Engine_VideoLibrary();
		if (string.IsNullOrEmpty(gameDataRoot))
		{
			return library;
		}

		foreach (string root in new[] { gameDataRoot, Path.Combine(gameDataRoot, "data") })
		{
			string art = Path.Combine(root, "art", "videos");
			string language = Path.Combine(root, "language");
			if (!System.IO.Directory.Exists(art) && !System.IO.Directory.Exists(language))
			{
				continue;
			}

			library.Directory = root;
			library.IndexFolder(root, art);

			try
			{
				if (System.IO.Directory.Exists(language))
				{
					foreach (string culture in System.IO.Directory
						.EnumerateDirectories(language)
						.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
					{
						library.IndexFolder(root, Path.Combine(culture, "videos"));
					}
				}
			}
			catch (Exception)
			{
				// An unreadable folder leaves what was indexed so far rather than throwing at
				// whatever happened to trigger the first lookup.
			}

			break;
		}

		library.Names = library._byName.Keys.ToArray();
		return library;
	}

	/// <summary>
	/// Adds every .bik under one folder. The first folder to claim a bare name keeps it —
	/// <c>art/videos</c> is indexed first, then the cultures in name order — while the
	/// relative path stays exact, so a stated culture always resolves to its own file.
	/// </summary>
	private void IndexFolder(string root, string folder)
	{
		if (!System.IO.Directory.Exists(folder))
		{
			return;
		}

		try
		{
			foreach (string path in System.IO.Directory
				.EnumerateFiles(folder, "*.bik", SearchOption.AllDirectories)
				.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
			{
				_byName.TryAdd(Path.GetFileNameWithoutExtension(path), path);
				_byRelativePath[Path.GetRelativePath(root, path).Replace('\\', '/')] = path;
			}
		}
		catch (Exception)
		{
			// Same as above: an unreadable folder is an empty one.
		}
	}

	/// <summary>
	/// Reads a movie. Accepts a bare name, a file name, or a guest-style path such as
	/// <c>game:\data\language\en-uk\videos\Fall_m.bik</c>. The path relative to the data
	/// root is tried first, so a stated culture wins; a bare name, or a culture whose folder
	/// this install does not ship, falls back to the name — which is what cultures.txt does
	/// too, every language falling back to en-uk.
	/// </summary>
	public byte[]? Read(string nameOrPath)
	{
		string? path = Resolve(nameOrPath);
		if (path is null)
		{
			return null;
		}

		try
		{
			return File.ReadAllBytes(path);
		}
		catch (Exception)
		{
			return null;
		}
	}

	public bool Contains(string nameOrPath) => Resolve(nameOrPath) is not null;

	private string? Resolve(string nameOrPath)
	{
		string relative = RelativeKey(nameOrPath);
		if (relative.Length != 0 && _byRelativePath.TryGetValue(relative, out string? exact))
		{
			return exact;
		}

		string key = Path.GetFileNameWithoutExtension(relative);
		return key.Length != 0 && _byName.TryGetValue(key, out string? byName) ? byName : null;
	}

	/// <summary>
	/// <c>game:\data\language\en-uk\videos\Fall_m.bik</c> to <c>language/en-uk/videos/Fall_m.bik</c>:
	/// the device prefix (<c>game:</c>, <c>epi:</c>, anything before a colon) and a leading
	/// <c>data/</c> are the guest's, not the folder's.
	/// </summary>
	private static string RelativeKey(string nameOrPath)
	{
		string s = nameOrPath.Replace('\\', '/').TrimEnd('/');
		int colon = s.IndexOf(':');
		if (colon >= 0)
		{
			s = s[(colon + 1)..];
		}

		s = s.TrimStart('/');
		if (s.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
		{
			s = s["data/".Length..];
		}

		return s;
	}
}
