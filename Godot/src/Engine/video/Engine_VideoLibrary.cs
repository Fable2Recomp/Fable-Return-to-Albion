using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Refii.Video;

/// <summary>
/// The game's movie folder, indexed on its own.
///
/// Videos are their own area, like the fonts and the terrain: Fable 2 ships every .bik
/// LOOSE under <c>art/videos</c> — the bank index contains none of them — so the bank
/// filesystem is the wrong place to look, and widening it to index the entire data folder
/// to catch one format would make every mount pay for it.
///
/// Small and cheap: one directory listing, no file reads until something asks for a movie.
/// </summary>
public sealed class Engine_VideoLibrary
{
	private readonly Dictionary<string, string> _byName =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Movie names without extension, in listing order.</summary>
	public IReadOnlyList<string> Names { get; private set; } = Array.Empty<string>();

	/// <summary>The folder that was indexed, or empty when none was found.</summary>
	public string Directory { get; private set; } = string.Empty;

	public int Count => _byName.Count;

	/// <summary>
	/// Indexes <c>art/videos</c> beneath a game data root. Installs differ in whether the
	/// root already includes the <c>data</c> level, so both are tried.
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
			string videos = Path.Combine(root, "art", "videos");
			if (!System.IO.Directory.Exists(videos))
			{
				continue;
			}

			library.Directory = videos;
			try
			{
				foreach (string path in System.IO.Directory
					.EnumerateFiles(videos, "*.bik", SearchOption.AllDirectories)
					.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
				{
					library._byName[Path.GetFileNameWithoutExtension(path)] = path;
				}
			}
			catch (Exception)
			{
				// An unreadable folder leaves an empty library rather than throwing at
				// whatever happened to trigger the first lookup.
			}

			break;
		}

		library.Names = library._byName.Keys.ToArray();
		return library;
	}

	/// <summary>
	/// Reads a movie. Accepts a bare name, a file name, or a guest-style path such as
	/// <c>art\videos\lionhead_logo.bik</c> — the lookup is by name, so the separators and
	/// the leading folders do not have to match.
	/// </summary>
	public byte[]? Read(string nameOrPath)
	{
		string key = Path.GetFileNameWithoutExtension(
			nameOrPath.Replace('\\', '/').TrimEnd('/'));

		if (key.Length == 0 || !_byName.TryGetValue(key, out string? path))
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

	public bool Contains(string nameOrPath) =>
		_byName.ContainsKey(Path.GetFileNameWithoutExtension(
			nameOrPath.Replace('\\', '/').TrimEnd('/')));
}
