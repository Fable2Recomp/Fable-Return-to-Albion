using System.Collections.Generic;
using System.Text;

namespace Refii.Core;

/// <summary>
/// The one CSV line reader. Mod overlays are authored by hand in a spreadsheet or a text editor,
/// so both dialects — game database rows and localised text — read the same way: commas, with
/// double quotes around a cell that contains one and <c>""</c> for a literal quote.
/// </summary>
public static class Engine_Csv
{
	public static List<string> SplitLine(string line)
	{
		var cells = new List<string>();
		var cell = new StringBuilder();
		bool quoted = false;

		for (int i = 0; i < line.Length; i++)
		{
			char c = line[i];
			if (quoted)
			{
				if (c == '"')
				{
					if (i + 1 < line.Length && line[i + 1] == '"')
					{
						cell.Append('"');
						i++;
						continue;
					}

					quoted = false;
					continue;
				}

				cell.Append(c);
				continue;
			}

			switch (c)
			{
				case '"':
					quoted = true;
					break;
				case ',':
					cells.Add(cell.ToString());
					cell.Clear();
					break;
				default:
					cell.Append(c);
					break;
			}
		}

		cells.Add(cell.ToString());
		return cells;
	}

	/// <summary>
	/// Turns <c>\n</c>, <c>\t</c>, <c>\r</c> and <c>\\</c> into the characters they name.
	///
	/// Localised text is full of line breaks and a CSV row is one line, so a mod has to be able
	/// to write one. Anything else after a backslash is left as it was typed rather than
	/// swallowed — a stray backslash in prose is not an error.
	/// </summary>
	public static string Unescape(string value)
	{
		if (!value.Contains('\\'))
		{
			return value;
		}

		var text = new StringBuilder(value.Length);
		for (int i = 0; i < value.Length; i++)
		{
			if (value[i] != '\\' || i + 1 >= value.Length)
			{
				text.Append(value[i]);
				continue;
			}

			char next = value[++i];
			text.Append(next switch
			{
				'n' => '\n',
				'r' => '\r',
				't' => '\t',
				'\\' => '\\',
				_ => next,
			});

			if (next is not ('n' or 'r' or 't' or '\\'))
			{
				text.Insert(text.Length - 1, '\\');
			}
		}

		return text.ToString();
	}
}
