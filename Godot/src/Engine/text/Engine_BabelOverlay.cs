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
