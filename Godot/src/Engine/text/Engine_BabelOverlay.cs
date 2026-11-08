using System;
using System.Collections.Generic;
using System.Globalization;
using Refii.Core;

namespace Refii.Text;

/// <summary>One string a mod sets.</summary>
public sealed class BabelOverlayRow
{
	public string Key = string.Empty;

	public string Value = string.Empty;

	public int Line;

	public string Mod = string.Empty;
}

/// <summary>What applying a set of text rows did.</summary>
public sealed class BabelOverlayResult
{
	public byte[] Bytes = Array.Empty<byte>();

	public int Applied;

	public List<string> Errors = new();

	public bool Changed => Applied != 0;
}

/// <summary>
/// Localised text as a row overlay: <c>key,value</c>, one string per line.
///
/// Two mods retranslating different keys both apply, for the same reason two mods editing
/// different database rows do — the unit of change is the thing a mod actually means to change,
/// not the 5 MB file it lives in.
///
/// A key may be a tag (<c>TEXT_SWORD_NAME</c>) or a <c>0x…</c> hash. Unlike a game-database row,
/// a key the file does not have is ADDED rather than refused: the record table is a sorted key
/// list with no schema, so new text is a legitimate thing for a mod to ship.
/// </summary>
public static class BabelOverlay
{
	public const string CsvSuffix = ".babel.csv";

	/// <summary>
	/// Reads a text overlay.
	///
	/// <c>key,value</c>, or <c>key,kind,value</c> so a row copied out of a full dump works — only
	/// <c>text</c> rows are applied, and any other kind is reported rather than guessed at.
	/// </summary>
	public static List<BabelOverlayRow> ParseCsv(string text, List<string> errors)
	{
		var rows = new List<BabelOverlayRow>();
		if (string.IsNullOrEmpty(text))
		{
			return rows;
		}

		bool firstDataLine = true;
		string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		for (int i = 0; i < lines.Length; i++)
		{
			string line = lines[i].Trim();
			if (line.Length == 0 || line[0] == '#')
			{
				continue;
			}

			List<string> cells = Engine_Csv.SplitLine(line);
			if (cells.Count < 2)
			{
				errors.Add($"line {i + 1}: expected key,value — got {cells.Count} column(s)");
				continue;
			}

			string key = cells[0].Trim();
			string kind = cells.Count >= 3 ? cells[1].Trim() : "text";
			string value = cells.Count >= 3 ? cells[2] : cells[1];

			bool wasFirst = firstDataLine;
			firstDataLine = false;
			if (wasFirst && key.Equals("key", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (key.Length == 0)
			{
				errors.Add($"line {i + 1}: the key cannot be empty");
				continue;
			}

			if (!kind.Equals("text", StringComparison.OrdinalIgnoreCase))
			{
				errors.Add(
					$"line {i + 1}: '{kind}' rows are not applied — an overlay changes text, and " +
					"speech links, groups and speaker names are carried across unchanged.");
				continue;
			}

			rows.Add(new BabelOverlayRow
			{
				Key = key,
				Value = Engine_Csv.Unescape(value),
				Line = i + 1,
			});
		}

		return rows;
	}

	/// <summary>
	/// The book one of a mod's text tables applies to while the game runs in
	/// <paramref name="culture"/>. Mods author text in <c>language/en-uk/text/book.babel.csv</c>;
	/// in any other culture the mod's own <c>language/&lt;culture&gt;/…</c> table wins when the mod
	/// ships one (this en-uk table is then dropped: null), else its en-uk rows go to the culture's
	/// book — a disc that ships one language and no en-uk still shows the mod's text. Every other
	/// table, and every table while the culture is en-uk, applies where it is authored.
	/// <paramref name="table"/> and <paramref name="modTables"/> are normalised mod-relative paths
	/// without <c>.csv</c>.
	/// </summary>
	public static string? TableForCulture(string table, IReadOnlyCollection<string> modTables, string culture)
	{
		string? target = Engine_GameCulture.Normalize(culture);
		if (target is null || string.Equals(target, Engine_GameCulture.Fallback, StringComparison.Ordinal))
		{
			return table;
		}

		foreach (string prefix in new[] { "language/", "data/language/" })
		{
			string authored = prefix + Engine_GameCulture.Fallback + "/";
			if (!table.StartsWith(authored, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			string own = prefix + target + "/" + table[authored.Length..];
			foreach (string other in modTables)
			{
				if (string.Equals(other, own, StringComparison.OrdinalIgnoreCase))
				{
					return null;
				}
			}

			return own;
		}

		return table;
	}

	/// <summary>
	/// The culture folder a normalised table path is under (<c>language/&lt;culture&gt;/…</c>, with or
	/// without <c>data/</c>), lower case, or null for a table outside the language root or one
	/// without a culture folder.
	/// </summary>
	public static string? CultureOf(string table)
	{
		foreach (string prefix in new[] { "language/", "data/language/" })
		{
			if (!table.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			int end = table.IndexOf('/', prefix.Length);
			return end > prefix.Length ? Engine_GameCulture.Normalize(table[prefix.Length..end]) : null;
		}

		return null;
	}

	/// <summary>
	/// True for a mod's translation into a culture other than <paramref name="culture"/>: a book an
	/// install that does not ship that language has no copy of, which is a language the player
	/// cannot be running in rather than a fault in the mod. The book of the culture in force, an
	/// en-uk table and any table outside the language root are not.
	/// </summary>
	public static bool IsOtherCultureTable(string table, string culture)
	{
		string? own = CultureOf(table);
		return own is not null &&
			!string.Equals(own, Engine_GameCulture.Normalize(culture), StringComparison.Ordinal) &&
			!string.Equals(own, Engine_GameCulture.Fallback, StringComparison.Ordinal);
	}

	/// <summary>
	/// Applies rows to <paramref name="original"/>, in the order given — later rows win.
	/// </summary>
	public static BabelOverlayResult Apply(byte[] original, IReadOnlyList<BabelOverlayRow> rows)
	{
		var result = new BabelOverlayResult { Bytes = original };
		if (rows.Count == 0)
		{
			return result;
		}

		Engine_BabelDatabase database;
		try
		{
			database = Engine_BabelDatabase.Parse(original);
		}
		catch (Exception exception)
		{
			result.Errors.Add($"the text database could not be read: {exception.Message}");
			return result;
		}

		var overrides = new Dictionary<uint, string>();
		foreach (BabelOverlayRow row in rows)
		{
			overrides[ResolveKey(row.Key)] = row.Value;
		}

		try
		{
			result.Bytes = Engine_BabelWriter.Write(database, overrides, result.Errors);
			result.Applied = overrides.Count;
		}
		catch (Exception exception)
		{
			result.Errors.Add($"the text database could not be rebuilt: {exception.Message}");
			result.Bytes = original;
			result.Applied = 0;
		}

		return result;
	}

	private static uint ResolveKey(string key) =>
		key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
		uint.TryParse(key.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint literal)
			? literal
			: Engine_GameHash.Fnv1(key);
}
