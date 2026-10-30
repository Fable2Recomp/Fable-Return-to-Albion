using System;

namespace Refii.Core;

/// <summary>
/// A virtual path that names its title: <c>@Fable3/art/gui/…</c>. The editor mounts every installed
/// title at once, each its own tree, and a bare path keeps meaning the default one (the active
/// title), so existing paths and scenes resolve as they always did. A running game serves its own
/// title's prefix as a bare path and no other title's.
/// </summary>
public static class Engine_TitlePath
{
	private const char Marker = '@';

	/// <summary><c>@&lt;Folder&gt;/&lt;path&gt;</c>.</summary>
	public static string Make(Engine_Title title, string path) =>
		path.Length == 0 ? Marker + title.Folder : Marker + title.Folder + "/" + path;

	/// <summary>
	/// Splits a title-prefixed path into its title and the path within it. False for a bare path,
	/// and for a prefix that names no title.
	/// </summary>
	public static bool TrySplit(string path, out Engine_Title title, out string rest)
	{
		title = default!;
		rest = path;
		if (path.Length < 2 || path[0] != Marker)
		{
			return false;
		}

		int slash = path.IndexOf('/');
		string folder = slash < 0 ? path[1..] : path[1..slash];
		foreach (Engine_Title candidate in Engine_Title.All)
		{
			if (string.Equals(candidate.Folder, folder, StringComparison.OrdinalIgnoreCase))
			{
				title = candidate;
				rest = slash < 0 ? string.Empty : path[(slash + 1)..];
				return true;
			}
		}

		return false;
	}
}
