using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Refii.Core;

namespace Refii.Gdb;

/// <summary>One value a mod sets, and where it was written.</summary>
public sealed class GdbOverlayRow
{
	public string Entry = string.Empty;

	public string Property = string.Empty;

	public string Value = string.Empty;

	/// <summary>Line in the mod's `.csv`, so a bad row can be pointed at.</summary>
	public int Line;

	/// <summary>The mod that wrote it. Set by the fold, not by the parser.</summary>
	public string Mod = string.Empty;

	public string Key => Entry + "." + Property;
}

/// <summary>What applying a set of rows did.</summary>
public sealed class GdbOverlayResult
{
	public byte[] Bytes = Array.Empty<byte>();

	public int Applied;

	public List<string> Errors = new();

	public bool Changed => Applied != 0;
}

/// <summary>
/// A MOD SETS VALUES; IT DOES NOT REBUILD THE DATABASE.
///
/// A `.gdb` record is a schema pointer followed by one u32 per property, so setting
/// <c>Items/Sword.Price</c> is writing four bytes at a known offset. That is the whole operation,
/// and everything that makes the container hard — schemas, sorted hash tables, interpolation
/// deltas, aliases, the name-table tail — is never touched, so a patched database is malformed in
/// exactly the ways the original was: none.
///
/// It also decides what a row overlay CAN do. Values of properties that already exist, yes; new
/// entries, new properties or a changed type, no — those need the container rebuilt, and a writer
/// that rebuilds it is a second implementation of a format we only read. A row that asks for one
/// is reported, not half-applied.
///
/// TWO MODS EDITING DIFFERENT ROWS BOTH APPLY, which is the point: file replacement would make
/// them exclusive. Two mods editing the SAME row is a conflict the load order resolves, and the
/// fold records both so the panel can say so.
/// </summary>
public static class GdbOverlay
{
	public const string CsvSuffix = ".gdb.csv";

	/// <summary>
	/// Reads a row overlay.
	///
	/// <c>entry,property,value</c>, one per line. Blank lines and <c>#</c> comments are skipped,
	/// a leading header row naming the columns is tolerated, and any further columns are ignored
	/// so a row copied out of a full database dump works unchanged.
	/// </summary>
	public static List<GdbOverlayRow> ParseCsv(string text, List<string> errors)
	{
		var rows = new List<GdbOverlayRow>();
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
			if (cells.Count < 3)
			{
				errors.Add($"line {i + 1}: expected entry,property,value — got {cells.Count} column(s)");
				continue;
			}

			string entry = cells[0].Trim();
			string property = cells[1].Trim();
			string value = cells[2].Trim();

			// A header row is the FIRST row that carries data, not the first line: a file that
			// opens with a comment pushes the header down, and matching on line 0 then read
			// "entry,property,value" as an edit to an entry called `entry`.
			bool wasFirst = firstDataLine;
			firstDataLine = false;
			if (wasFirst &&
				entry.Equals("entry", StringComparison.OrdinalIgnoreCase) &&
				property.Equals("property", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (entry.Length == 0 || property.Length == 0)
			{
				errors.Add($"line {i + 1}: entry and property cannot be empty");
				continue;
			}

			rows.Add(new GdbOverlayRow
			{
				Entry = entry,
				Property = property,
				Value = value,
				Line = i + 1,
			});
		}

		return rows;
	}

	/// <summary>
	/// Applies rows to a copy of <paramref name="original"/>, in the order given.
	///
	/// The caller composes the order — later rows win — so two mods setting the same value leave
	/// the higher-priority one in place, exactly as two mods replacing the same file do.
	/// </summary>
	public static GdbOverlayResult Apply(byte[] original, IReadOnlyList<GdbOverlayRow> rows)
	{
		var result = new GdbOverlayResult { Bytes = original };
		if (rows.Count == 0)
		{
			return result;
		}

		if (!GdbView.TryParse(original, out GdbView view, out string parseError))
		{
			result.Errors.Add($"the database could not be read: {parseError}");
			return result;
		}

		var patched = (byte[])original.Clone();
		foreach (GdbOverlayRow row in rows)
		{
			string where = row.Mod.Length != 0 ? $"{row.Mod} line {row.Line}" : $"line {row.Line}";

			if (!TryResolveEntry(view, row.Entry, out int index))
			{
				result.Errors.Add($"{where}: no entry '{row.Entry}' in this database");
				continue;
			}

			uint property = ResolveHash(row.Property);
			if (!view.TryFindValueSlot(index, property, -1, out int slot, out GdbFieldType type))
			{
				result.Errors.Add(
					$"{where}: '{row.Entry}' has no property '{row.Property}' of its own. " +
					"An inherited property belongs to the parent entry — set it there.");
				continue;
			}

			if (!TryEncode(row.Value, type, out uint raw, out string encodeError))
			{
				result.Errors.Add($"{where}: {row.Property} is {type} — {encodeError}");
				continue;
			}

			WriteU32(patched, slot, raw);
			result.Applied++;
		}

		if (result.Applied != 0)
		{
			result.Bytes = patched;
		}

		return result;
	}

	/// <summary>
	/// The entry an overlay names: a <c>0x…</c> id, the name it hashes to, or an alias.
	/// </summary>
	private static bool TryResolveEntry(GdbView view, string entry, out int index)
	{
		if (TryParseLiteral(entry, out uint literal) && view.TryLookup(literal, out index))
		{
			return true;
		}

		if (view.TryLookup(Engine_GameHash.Fnv1(entry), out index))
		{
			return true;
		}

		// Entry ids are stripped from the name table, so a database that knows a name at all
		// knows it through its alias pairs.
		foreach ((uint hash, string name) in view.RecordNames)
		{
			if (string.Equals(name, entry, StringComparison.OrdinalIgnoreCase) &&
				view.TryLookup(hash, out index))
			{
				return true;
			}
		}

		index = -1;
		return false;
	}

	private static uint ResolveHash(string name) =>
		TryParseLiteral(name, out uint literal) ? literal : Engine_GameHash.Fnv1(name);

	private static bool TryParseLiteral(string text, out uint value)
	{
		value = 0;
		return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
			uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
	}

	/// <summary>
	/// The u32 a value becomes, decided by the property's EXISTING type.
	///
	/// The overlay never states a type: the database already has one, and letting a row disagree
	/// with it is how a float lands in an int slot and reads back as a number in the millions.
	/// </summary>
	private static bool TryEncode(string value, GdbFieldType type, out uint raw, out string error)
	{
		raw = 0;
		error = string.Empty;

		if (TryParseLiteral(value, out uint literal))
		{
			raw = literal;
			return true;
		}

		switch (type)
		{
			case GdbFieldType.Bool:
				if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1")
				{
					raw = 1;
					return true;
				}

				if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0")
				{
					raw = 0;
					return true;
				}

				error = $"'{value}' is not true or false";
				return false;

			case GdbFieldType.Int:
			case GdbFieldType.Enum:
				if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
				{
					raw = unchecked((uint)number);
					return true;
				}

				error = $"'{value}' is not a whole number";
				return false;

			case GdbFieldType.Float:
				if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float real))
				{
					raw = unchecked((uint)BitConverter.SingleToInt32Bits(real));
					return true;
				}

				error = $"'{value}' is not a number";
				return false;

			case GdbFieldType.String:
			case GdbFieldType.Reference:
			case GdbFieldType.ResourceReference:
				// A string value is the hash of a TEXT_ key and a reference is the hash of an
				// entry, so both are the name written as authored. A name that resolves to
				// nothing is not detectable here — it is a valid hash of a key this database
				// does not carry — and shows up as missing text rather than a broken file.
				raw = Engine_GameHash.Fnv1(value);
				return true;

			default:
				error = $"values of type {type} cannot be set from an overlay";
				return false;
		}
	}

	/// <summary>Big-endian, as every scalar in the container is.</summary>
	private static void WriteU32(byte[] data, int at, uint value)
	{
		data[at] = (byte)(value >> 24);
		data[at + 1] = (byte)(value >> 16);
		data[at + 2] = (byte)(value >> 8);
		data[at + 3] = (byte)value;
	}
}
