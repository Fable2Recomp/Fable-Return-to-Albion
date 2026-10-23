using System;
using System.Collections.Generic;

namespace Refii.Text;

/// <summary>
/// The culture the game's text and fonts are read in — the guest's own, as it states it.
///
/// The guest switches language at the title (`GUI_FrontendRoot_MenuActionDispatch` case 302 ->
/// `sub_82382C68`), reloads its own book and fonts, and writes the choice to the profile, so the
/// next boot starts in it. Everything the host draws text with follows the code the guest names
/// (<c>HostBridge.GetCurrentCulture</c>); until it names one, and for any culture folder that
/// does not ship a file, <see cref="Fallback"/> answers — the culture every row of
/// <c>language/cultures.txt</c> falls back to.
///
/// No Godot types: the text loaders and the tests share it.
/// </summary>
public static class Engine_GameCulture
{
	/// <summary>The title's own fallback culture.</summary>
	public const string Fallback = "en-uk";

	/// <summary>The culture in force. <see cref="Fallback"/> until the guest states one.</summary>
	public static string Current { get; private set; } = Fallback;

	/// <summary>Raised after <see cref="Current"/> changes, on the thread that called <see cref="Set"/>.</summary>
	public static event Action<string>? Changed;

	/// <summary>
	/// Takes the culture the guest states. Returns true when it differs from the one in force
	/// (and raises <see cref="Changed"/>); a null or malformed code changes nothing — an unread
	/// table is not a statement that the language went back to English.
	/// </summary>
	public static bool Set(string? culture)
	{
		if (Normalize(culture) is not { } normalized ||
			string.Equals(normalized, Current, StringComparison.Ordinal))
		{
			return false;
		}

		Current = normalized;
		Changed?.Invoke(normalized);
		return true;
	}

	/// <summary>Back to <see cref="Fallback"/> without raising <see cref="Changed"/>. Tests only.</summary>
	public static void ResetForTests() => Current = Fallback;

	/// <summary>
	/// A culture code as a folder name — lower case, letters, digits and '-' only, at most 16
	/// characters — or null when it is not one. The row buffer is 85 bytes, but every culture in
	/// <c>cultures.txt</c> is a short tag, and anything else is a torn read.
	/// </summary>
	public static string? Normalize(string? culture)
	{
		if (string.IsNullOrEmpty(culture) || culture.Length > 16)
		{
			return null;
		}

		foreach (char c in culture)
		{
			bool plain = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-';
			if (!plain)
			{
				return null;
			}
		}

		return culture.ToLowerInvariant();
	}

	/// <summary>
	/// The cultures to look under for a language file, in order: the one in force, then
	/// <see cref="Fallback"/>. The guest's own alias for a language file lists the active
	/// culture's folder first and en-uk after it (`sub_823817E8`), and only en-uk ships every
	/// root, so a culture without a folder for a root (hu-hu has no fonts) reads en-uk's.
	/// </summary>
	public static IReadOnlyList<string> SearchOrder(string? culture)
	{
		string first = Normalize(culture) ?? Fallback;
		return string.Equals(first, Fallback, StringComparison.Ordinal)
			? new[] { Fallback }
			: new[] { first, Fallback };
	}

	/// <summary>
	/// Mount spellings of a file under a language root (<paramref name="relative"/> is e.g.
	/// <c>text/book.babel</c>): each culture of <see cref="SearchOrder"/> with and without the
	/// <c>data/</c> prefix, then the locale-less spelling the mount aliases to its own culture.
	/// </summary>
	public static IReadOnlyList<string> MountCandidates(string? culture, string relative)
	{
		var candidates = new List<string>();
		foreach (string folder in SearchOrder(culture))
		{
			candidates.Add($"language/{folder}/{relative}");
			candidates.Add($"data/language/{folder}/{relative}");
		}

		candidates.Add($"language/{relative}");
		candidates.Add($"data/language/{relative}");
		return candidates;
	}
}
