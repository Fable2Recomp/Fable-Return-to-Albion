using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.Text;

/// <summary>
/// The culture the game's text and fonts are read in — the guest's own, as it states it.
///
/// The guest switches language at the title (`GUI_FrontendRoot_MenuActionDispatch` case 302 ->
/// `sub_82382C68`), reloads its own book and fonts, and writes the choice to the profile, so the
/// next boot starts in it. Everything the host draws text with follows the code the guest names
/// (<c>HostBridge.GetCurrentCulture</c>). Until it names one, <see cref="Current"/> is the culture
/// the guest's own rule picks from this install (<see cref="Choose"/>), so a disc that ships one
/// language and no <see cref="Fallback"/> folder still reads its own book before the guest runs.
///
/// No Godot types: the text loaders and the tests share it.
/// </summary>
public static class Engine_GameCulture
{
	/// <summary>
	/// The culture the guest remaps every language file to after its own: `sub_823728E0` adds
	/// `language\` -> `language\en-uk` (0x82372AC4) right after the component's culture, then one
	/// remap per row of the language table. Also every <c>cultures.txt</c> row's last fallback.
	/// </summary>
	public const string Fallback = "en-uk";

	/// <summary>
	/// The console's language as the host's kernel reports it (`user_language` 1, English) put
	/// through XLip's language table: `sub_82BF0610` maps `XGetLanguage` through
	/// `dword_832D5E90[1]` = "en-us" (disc build), whose <c>cultures.txt</c> fallback is en-uk.
	/// </summary>
	public const string ConsoleCulture = "en-us";

	/// <summary>The culture in force: the one the guest stated, else this install's own.</summary>
	public static string Current { get; private set; } = Fallback;

	/// <summary>
	/// The install's culture folders the guest would enumerate — those whose name is a
	/// <c>cultures.txt</c> code — in <see cref="Choose"/>'s order. Empty until
	/// <see cref="UseInstall"/> reads one.
	/// </summary>
	public static IReadOnlyList<string> Installed { get; private set; } = Array.Empty<string>();

	/// <summary>Raised after <see cref="Current"/> changes, on the thread that called <see cref="Set"/>.</summary>
	public static event Action<string>? Changed;

	private static readonly object Gate = new();

	private static bool _stated;

	private static HashSet<string> _codes = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Takes the culture the guest states. Returns true when it differs from the one in force
	/// (and raises <see cref="Changed"/>); a null or malformed code changes nothing — an unread
	/// table is not a statement that the language went back to English.
	/// </summary>
	public static bool Set(string? culture)
	{
		if (Normalize(culture) is not { } normalized)
		{
			return false;
		}

		lock (Gate)
		{
			_stated = true;
			if (string.Equals(normalized, Current, StringComparison.Ordinal))
			{
				return false;
			}

			Current = normalized;
		}

		Changed?.Invoke(normalized);
		return true;
	}

	/// <summary>
	/// Reads the install's <c>language/cultures.txt</c> and culture folders under
	/// <paramref name="gameDirectory"/> (the game folder or its <c>data</c>), and — while the guest
	/// has stated nothing — takes the culture its rule picks there. An install without a
	/// <c>cultures.txt</c> (Fable 3 ships <c>cultures.loc</c>) leaves <see cref="Fallback"/> and no
	/// installed list, as before.
	/// </summary>
	public static void UseInstall(string? gameDirectory)
	{
		string? languageRoot = LanguageRoot(gameDirectory);
		Dictionary<string, string?> cultures = new(StringComparer.OrdinalIgnoreCase);
		var folders = new List<string>();
		var withText = new List<string>();
		if (languageRoot is not null)
		{
			try
			{
				cultures = ParseCultures(DecodeCulturesText(File.ReadAllBytes(Path.Combine(languageRoot, "cultures.txt"))));
				foreach (string directory in Directory.EnumerateDirectories(languageRoot))
				{
					string name = Path.GetFileName(directory);
					folders.Add(name);
					if (Directory.Exists(Path.Combine(directory, "text")))
					{
						withText.Add(name);
					}
				}
			}
			catch (Exception)
			{
				cultures.Clear();
			}
		}

		IReadOnlyList<string> installed = InstalledCultures(folders, cultures);
		string predicted = Choose(cultures, installed, InstalledCultures(withText, cultures), ConsoleCulture);
		lock (Gate)
		{
			_codes = new HashSet<string>(cultures.Keys, StringComparer.OrdinalIgnoreCase);
			Installed = installed;
			if (!_stated)
			{
				Current = predicted;
			}
		}
	}

	/// <summary>Back to <see cref="Fallback"/> and no install, without raising <see cref="Changed"/>. Tests only.</summary>
	public static void ResetForTests()
	{
		lock (Gate)
		{
			Current = Fallback;
			Installed = Array.Empty<string>();
			_codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			_stated = false;
		}
	}

	/// <summary>True when <paramref name="folder"/> is a culture of the install's <c>cultures.txt</c>.</summary>
	public static bool IsCultureCode(string folder)
	{
		lock (Gate)
		{
			return _codes.Contains(folder);
		}
	}

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
	/// The cultures to look under for a language file, in the guest's remap order (`sub_823728E0`):
	/// the one in force, then <see cref="Fallback"/> (0x82372AC4), then every installed culture
	/// (the per-row remaps from 0x82372C3C). A culture without a folder for a root (hu-hu has no
	/// fonts) reads en-uk's; a disc without en-uk reads the culture it ships.
	/// </summary>
	public static IReadOnlyList<string> SearchOrder(string? culture)
	{
		var order = new List<string> { Normalize(culture) ?? Fallback };
		if (!order.Contains(Fallback))
		{
			order.Add(Fallback);
		}

		foreach (string installed in Installed)
		{
			if (!order.Contains(installed))
			{
				order.Add(installed);
			}
		}

		return order;
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

	/// <summary>
	/// <c>cultures.txt</c> as the guest decodes it: `sub_82BF2128` opens it with a 0xFEFF mark
	/// required (`sub_82BF0CE0`, the BOM check at 0x82BF0E04), so it is UTF-16; the shipped file is
	/// big-endian. A file without a mark is read as UTF-8 so a hand-edited copy still parses.
	/// </summary>
	public static string DecodeCulturesText(byte[] bytes)
	{
		if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
		{
			return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
		}

		if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
		{
			return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
		}

		return Encoding.UTF8.GetString(bytes);
	}

	/// <summary>
	/// Each row's code and fallback, as `sub_82BF1E58` reads a line: blank and ';' lines skipped,
	/// fields split on tab or comma, the code and the display name required, the fallback optional.
	/// A fallback of "null" (`off_832D5EC4`) or none ends the chain: the value is then null.
	/// </summary>
	public static Dictionary<string, string?> ParseCultures(string text)
	{
		var cultures = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
		foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
		{
			string line = raw.Trim();
			if (line.Length == 0 || line[0] == ';')
			{
				continue;
			}

			string[] fields = line.Split(new[] { '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
			if (fields.Length < 2 || Normalize(fields[0].Trim()) is not { } code)
			{
				continue;
			}

			string? fallback = fields.Length >= 3 ? Normalize(fields[2].Trim()) : null;
			if (string.Equals(fallback, "null", StringComparison.Ordinal))
			{
				fallback = null;
			}

			cultures.TryAdd(code, fallback);
		}

		return cultures;
	}

	/// <summary>
	/// The folders XLip takes as languages: `sub_82BEF170` keeps a folder only when
	/// `sub_82BF1268` finds its name in <c>cultures.txt</c>. In name order, as the disc's directory
	/// lists them.
	/// </summary>
	public static IReadOnlyList<string> InstalledCultures(
		IEnumerable<string> folders, IReadOnlyDictionary<string, string?> cultures)
	{
		var installed = new List<string>();
		foreach (string folder in folders)
		{
			if (Normalize(folder) is { } code && cultures.ContainsKey(code) && !installed.Contains(code))
			{
				installed.Add(code);
			}
		}

		installed.Sort(StringComparer.Ordinal);
		return installed;
	}

	/// <summary>
	/// The culture the guest runs in on a first boot (no language saved in the profile).
	///
	/// `sub_82BF0610` lists the console's culture and its <c>cultures.txt</c> fallbacks
	/// (`sub_82BF04F8` follows the third column until "null"); `sub_82BF07C0` keeps those that are
	/// installed, in that order, then appends every installed language. The text component then
	/// resolves to the first of them that ships text (`sub_82BED548`), and `sub_823737D0` makes
	/// that row current — row 0 when none matched (`sub_82373D60(0)` at 0x82373D40).
	///
	/// So English on the USA/Europe disc is en-us -> en-uk = en-uk, and on a disc that ships only
	/// es-es it is es-es. <see cref="Fallback"/> only when nothing is installed at all.
	/// </summary>
	public static string Choose(
		IReadOnlyDictionary<string, string?> cultures,
		IReadOnlyList<string> installed,
		IReadOnlyList<string> withText,
		string consoleCulture)
	{
		var preference = new List<string>();
		string? code = Normalize(consoleCulture);
		while (code is not null && !preference.Contains(code) && cultures.TryGetValue(code, out string? next))
		{
			preference.Add(code);
			code = next;
		}

		foreach (string culture in preference)
		{
			if (withText.Contains(culture))
			{
				return culture;
			}
		}

		foreach (string culture in installed)
		{
			if (withText.Contains(culture))
			{
				return culture;
			}
		}

		return installed.Count != 0 ? installed[0] : Fallback;
	}

	private static string? LanguageRoot(string? gameDirectory)
	{
		if (string.IsNullOrEmpty(gameDirectory))
		{
			return null;
		}

		foreach (string root in new[] { gameDirectory, Path.Combine(gameDirectory, "data") })
		{
			string language = Path.Combine(root, "language");
			if (File.Exists(Path.Combine(language, "cultures.txt")))
			{
				return language;
			}
		}

		return null;
	}
}
