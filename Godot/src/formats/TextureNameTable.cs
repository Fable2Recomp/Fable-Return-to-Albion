using System;
using System.Collections.Generic;
using System.IO;

namespace Refii.Launcher.Formats;

/// <summary>
/// Resolves the texture hashes carried by <see cref="FacUvRecord.TextureHash"/> back
/// into asset paths.
///
/// The engine identifies a texture by <see cref="GameHash.ResourcePath"/> of its
/// lowercased path — <c>art\gui\frames\frames_04.tex</c> — and nothing in the
/// presentation data stores the path alongside the hash. The reliable way to invert
/// it is to hash the assets you actually have: extract <c>art/gui/gui_texture_headers.bnk</c>
/// and point <see cref="FromDirectory"/> at the result.
///
/// Doing that resolves every UV record in the shipped screens — 146 of 146 distinct
/// hashes, 15,790 of 15,790 records. Recovering paths from the presentations' own
/// authoring-path attributes instead only reaches about 83%, because not every
/// element carries one.
/// </summary>
public sealed class TextureNameTable
{
	private readonly Dictionary<uint, string> _byHash = new();

	public int Count => _byHash.Count;

	/// <summary>
	/// Hashes every file under <paramref name="root"/>, keyed by its path relative to
	/// that root. Point this at an extraction root so relative paths come out as
	/// <c>art\gui\...</c>, which is what the engine hashes.
	/// </summary>
	/// <param name="extensions">
	/// Extensions to include, without the dot. Null takes everything, which is fine —
	/// non-texture files simply never get looked up.
	/// </param>
	public static TextureNameTable FromDirectory(string root, IEnumerable<string>? extensions = null)
	{
		var table = new TextureNameTable();
		if (!Directory.Exists(root))
			return table;

		HashSet<string>? filter = null;
		if (extensions is not null)
		{
			filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var e in extensions)
				filter.Add(e.TrimStart('.'));
		}

		foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
		{
			if (filter is not null)
			{
				string ext = Path.GetExtension(file).TrimStart('.');
				if (!filter.Contains(ext))
					continue;
			}
			table.Add(Path.GetRelativePath(root, file));
		}
		return table;
	}

	/// <summary>
	/// Hashes an arbitrary set of asset paths. This is the form that works against a
	/// bank streamed in place, where there is no directory to walk — pass the mounted
	/// filesystem's own path enumeration.
	/// </summary>
	public static TextureNameTable FromPaths(IEnumerable<string> paths, IEnumerable<string>? extensions = null)
	{
		var table = new TextureNameTable();

		HashSet<string>? filter = null;
		if (extensions is not null)
		{
			filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var e in extensions)
				filter.Add(e.TrimStart('.'));
		}

		foreach (string path in paths)
		{
			if (filter is not null)
			{
				string ext = Path.GetExtension(path).TrimStart('.');
				if (!filter.Contains(ext))
					continue;
			}
			table.Add(path);
		}
		return table;
	}

	/// <summary>Registers one asset path. Separators and case are normalised the way the engine does.</summary>
	public void Add(string relativePath)
	{
		string normalised = relativePath.Replace('/', '\\').ToLowerInvariant();
		_byHash[GameHash.ResourcePath(normalised)] = normalised;
	}

	public bool TryResolve(uint hash, out string path) => _byHash.TryGetValue(hash, out path!);

	/// <summary>Resolves a quad's texture, or an empty string if the asset is not present.</summary>
	public string Resolve(FacUvRecord uv) =>
		_byHash.TryGetValue(uv.TextureHash, out var p) ? p : string.Empty;

	public IReadOnlyDictionary<uint, string> Entries => _byHash;
}
