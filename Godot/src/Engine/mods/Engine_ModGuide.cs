using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.Mods;

/// <summary>What one block of a guide file is.</summary>
public enum ModGuideBlockKind
{
	Heading,
	Paragraph,
	ListItem,
	Code,
	Table,
	Rule,
}

/// <summary>
/// One block of a guide file. <see cref="Text"/> is the inline markdown of a heading, paragraph or list
/// item, or the verbatim text of a code block; <see cref="Rows"/> a table's cells (the header row first).
/// <see cref="Level"/> is a heading's level (1..6) or a list item's nesting depth (0 outermost).
/// </summary>
public sealed record ModGuideBlock(
	ModGuideBlockKind Kind,
	string Text,
	int Level = 0,
	string Anchor = "",
	IReadOnlyList<IReadOnlyList<string>>? Rows = null,
	string Language = "",
	string Marker = "");

/// <summary>A heading of a guide file, with the anchor a link names it by.</summary>
public sealed record ModGuideHeading(int Level, string Text, string Anchor);

/// <summary>
/// A link in a guide file. <see cref="External"/> is an http(s) target; otherwise <see cref="File"/> is the
/// guide file it names (the linking file itself for a bare <c>#anchor</c>) and <see cref="Anchor"/> the
/// heading in it, empty for the top.
/// </summary>
public sealed record ModGuideLink(string Text, string Target, string File, string Anchor, bool External);

/// <summary>One parsed guide file.</summary>
public sealed class ModGuideDocument
{
	public ModGuideDocument(string fileName, IReadOnlyList<ModGuideBlock> blocks)
	{
		FileName = fileName;
		Blocks = blocks;
		Headings = blocks.Where(b => b.Kind == ModGuideBlockKind.Heading)
			.Select(b => new ModGuideHeading(b.Level, b.Text, b.Anchor)).ToList();
		Title = Headings.FirstOrDefault(h => h.Level == 1)?.Text ?? Path.GetFileNameWithoutExtension(fileName);
	}

	/// <summary>The file's name inside the guide folder (<c>events.md</c>).</summary>
	public string FileName { get; }

	/// <summary>The first level-1 heading's text, else the file name.</summary>
	public string Title { get; }

	public IReadOnlyList<ModGuideBlock> Blocks { get; }

	public IReadOnlyList<ModGuideHeading> Headings { get; }

	/// <summary>Every link in the file's prose, headings and tables (never inside code).</summary>
	public IEnumerable<ModGuideLink> Links()
	{
		foreach (ModGuideBlock block in Blocks)
		{
			IEnumerable<string> texts = block.Kind switch
			{
				ModGuideBlockKind.Code or ModGuideBlockKind.Rule => Array.Empty<string>(),
				ModGuideBlockKind.Table => block.Rows!.SelectMany(r => r),
				_ => new[] { block.Text },
			};
			foreach (string text in texts)
			{
				foreach ((string label, string target) in ModGuide.InlineLinks(text))
				{
					yield return ModGuide.ResolveLink(FileName, label, target);
				}
			}
		}
	}
}

/// <summary>A guide file as BBCode for a RichTextLabel, with the paragraph each heading's anchor starts at.</summary>
public sealed record ModGuideRendered(string BbCode, IReadOnlyDictionary<string, int> AnchorParagraphs);

/// <summary>
/// THE MODDING GUIDE: the markdown files under <c>docs/modding/</c> of the Godot project — the files the
/// SDK ships as <c>sdk/docs/modding/</c> — read, parsed and drawn as BBCode for the Mod Studio's
/// "Mod API Guide" page. The page reads the shipped files themselves; there is no second copy of the
/// text. Pure .NET: the tests hold the files, their links and anchors to this model.
///
/// The markdown is the subset the guide is written in: ATX headings, paragraphs, <c>-</c> / <c>*</c> /
/// numbered list items with indented continuation lines, fenced code blocks (indented fences too),
/// pipe tables with a separator row, <c>---</c> rules, and inline code, links, <c>**bold**</c> and
/// <c>*italic*</c>. A <c>\|</c> is a literal pipe in a table cell.
/// </summary>
public static class ModGuide
{
	/// <summary>The guide's folder relative to the Godot project.</summary>
	public const string ProjectFolder = "docs/modding";

	/// <summary>The guide's folder as a resource path.</summary>
	public const string ResFolder = "res://docs/modding";

	/// <summary>The index file every other file is reached from.</summary>
	public const string IndexFile = "README.md";

	/// <summary>The scheme a guide link's RichTextLabel meta carries: <c>guide:file.md#anchor</c>.</summary>
	public const string MetaScheme = "guide:";

	private const string CodeColour = "#e3c27a";
	private const string CodeBlockBackground = "#161c27";
	private const string HeadingColour = "#8fd0ff";
	private const string LinkColour = "#7fc4ff";

	// ---------------------------------------------------------------------------------------
	// The files
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// The guide's files in reading order: the index first, then every file in the order the index
	/// first links it, then any other <c>.md</c> in the folder by name. Empty when the folder is absent.
	/// </summary>
	public static IReadOnlyList<string> Files(string folder)
	{
		if (!Directory.Exists(folder))
		{
			return Array.Empty<string>();
		}

		var present = Directory.GetFiles(folder, "*.md")
			.Select(Path.GetFileName)
			.Where(n => n is not null)
			.Select(n => n!)
			.ToHashSet(StringComparer.Ordinal);
		var order = new List<string>();
		if (present.Contains(IndexFile))
		{
			order.Add(IndexFile);
			ModGuideDocument index = Parse(IndexFile, File.ReadAllText(Path.Combine(folder, IndexFile)));
			// The index's own tables of chapters and references set the order; a file linked only from
			// elsewhere in the index follows them.
			IEnumerable<ModGuideLink> listed = LinksUnder(index, IndexSections);
			foreach (ModGuideLink link in listed.Concat(index.Links()))
			{
				if (!link.External && present.Contains(link.File) && !order.Contains(link.File))
				{
					order.Add(link.File);
				}
			}
		}

		order.AddRange(present.Where(n => !order.Contains(n)).OrderBy(n => n, StringComparer.Ordinal));
		return order;
	}

	/// <summary>The index's level-2 sections that list the guide's files in reading order.</summary>
	private static readonly string[] IndexSections = { "Concepts", "References" };

	/// <summary>The links in the blocks under the level-2 headings named <paramref name="sections"/>, in file order.</summary>
	private static IEnumerable<ModGuideLink> LinksUnder(ModGuideDocument doc, IReadOnlyCollection<string> sections)
	{
		bool inside = false;
		foreach (ModGuideBlock block in doc.Blocks)
		{
			if (block.Kind == ModGuideBlockKind.Heading && block.Level <= 2)
			{
				inside = block.Level == 2 && sections.Contains(block.Text);
				continue;
			}

			if (!inside)
			{
				continue;
			}

			IEnumerable<string> texts = block.Kind switch
			{
				ModGuideBlockKind.Table => block.Rows!.SelectMany(r => r),
				ModGuideBlockKind.Code or ModGuideBlockKind.Rule => Array.Empty<string>(),
				_ => new[] { block.Text },
			};
			foreach (string text in texts)
			{
				foreach ((string label, string target) in InlineLinks(text))
				{
					yield return ResolveLink(doc.FileName, label, target);
				}
			}
		}
	}

	/// <summary>Reads and parses one guide file.</summary>
	public static ModGuideDocument Load(string folder, string fileName) =>
		Parse(fileName, File.ReadAllText(Path.Combine(folder, fileName)));

	/// <summary>
	/// What is wrong with the guide in <paramref name="folder"/>: no index, an empty file, a file with no
	/// title, a link to a file that is not there, or to an anchor its file does not have. Empty when sound.
	/// </summary>
	public static IReadOnlyList<string> Problems(string folder)
	{
		var problems = new List<string>();
		IReadOnlyList<string> files = Files(folder);
		if (!files.Contains(IndexFile))
		{
			problems.Add($"{folder}: no {IndexFile}");
			return problems;
		}

		var docs = files.ToDictionary(f => f, f => Load(folder, f), StringComparer.Ordinal);
		foreach (ModGuideDocument doc in docs.Values)
		{
			if (doc.Blocks.Count == 0)
			{
				problems.Add($"{doc.FileName}: empty");
				continue;
			}

			if (!doc.Headings.Any(h => h.Level == 1))
			{
				problems.Add($"{doc.FileName}: no title (a '# ' heading)");
			}

			foreach (ModGuideLink link in doc.Links())
			{
				if (link.External)
				{
					continue;
				}

				if (!docs.TryGetValue(link.File, out ModGuideDocument? target))
				{
					problems.Add($"{doc.FileName}: [{link.Text}]({link.Target}) names {link.File}, which is not in the guide");
				}
				else if (link.Anchor.Length != 0 && target.Headings.All(h => h.Anchor != link.Anchor))
				{
					problems.Add($"{doc.FileName}: [{link.Text}]({link.Target}) names #{link.Anchor}, which {link.File} has no heading for");
				}
			}
		}

		foreach (string file in files.Where(f => f != IndexFile))
		{
			bool reached = docs.Values.Any(d => d.FileName != file && d.Links().Any(l => !l.External && l.File == file));
			if (!reached)
			{
				problems.Add($"{file}: no other guide file links to it");
			}
		}

		return problems;
	}

	// ---------------------------------------------------------------------------------------
	// Parsing
	// ---------------------------------------------------------------------------------------

	/// <summary>Parses one file's markdown into blocks; headings get GitHub-style anchors, unique in the file.</summary>
	public static ModGuideDocument Parse(string fileName, string markdown)
	{
		string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		var blocks = new List<ModGuideBlock>();
		var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
		var paragraph = new List<string>();

		void FlushParagraph()
		{
			if (paragraph.Count != 0)
			{
				blocks.Add(new ModGuideBlock(ModGuideBlockKind.Paragraph, string.Join(" ", paragraph)));
				paragraph.Clear();
			}
		}

		int i = 0;
		while (i < lines.Length)
		{
			string line = lines[i];
			string trimmed = line.Trim();

			if (trimmed.Length == 0)
			{
				FlushParagraph();
				i++;
				continue;
			}

			// A fenced code block, at any indent; its lines lose that indent.
			if (trimmed.StartsWith("```", StringComparison.Ordinal))
			{
				FlushParagraph();
				int indent = line.Length - line.TrimStart().Length;
				string language = trimmed.Substring(3).Trim();
				var code = new List<string>();
				i++;
				while (i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
				{
					string codeLine = lines[i];
					int strip = Math.Min(indent, codeLine.Length - codeLine.TrimStart(' ').Length);
					code.Add(codeLine.Substring(strip));
					i++;
				}

				i++; // the closing fence
				blocks.Add(new ModGuideBlock(ModGuideBlockKind.Code, string.Join("\n", code), Language: language));
				continue;
			}

			if (HeadingLevel(trimmed) is int level && line.StartsWith('#'))
			{
				FlushParagraph();
				string text = trimmed.Substring(level).Trim().TrimEnd('#').Trim();
				string anchor = Anchor(text);
				if (anchors.TryGetValue(anchor, out int seen))
				{
					anchors[anchor] = seen + 1;
					anchor = anchor + "-" + (seen + 1).ToString(CultureInfo.InvariantCulture);
				}
				else
				{
					anchors[anchor] = 0;
				}

				blocks.Add(new ModGuideBlock(ModGuideBlockKind.Heading, text, level, anchor));
				i++;
				continue;
			}

			if (trimmed.StartsWith('|') && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
			{
				FlushParagraph();
				var rows = new List<IReadOnlyList<string>> { SplitRow(trimmed) };
				i += 2;
				while (i < lines.Length && lines[i].Trim().StartsWith('|'))
				{
					rows.Add(SplitRow(lines[i].Trim()));
					i++;
				}

				int columns = rows[0].Count;
				blocks.Add(new ModGuideBlock(ModGuideBlockKind.Table, string.Empty,
					Rows: rows.Select(r => (IReadOnlyList<string>)Pad(r, columns)).ToList()));
				continue;
			}

			if (trimmed is "---" or "***" or "___")
			{
				FlushParagraph();
				blocks.Add(new ModGuideBlock(ModGuideBlockKind.Rule, string.Empty));
				i++;
				continue;
			}

			if (ListMarker(line) is (int depth, string marker, int contentStart))
			{
				FlushParagraph();
				var item = new StringBuilder(line.Substring(contentStart).Trim());
				int itemIndent = line.Length - line.TrimStart().Length;
				i++;
				// Continuation lines: indented deeper than the marker, not themselves a list item or a fence.
				while (i < lines.Length)
				{
					string next = lines[i];
					string nextTrimmed = next.Trim();
					int nextIndent = next.Length - next.TrimStart().Length;
					if (nextTrimmed.Length == 0 || nextIndent <= itemIndent || ListMarker(next) is not null ||
					    nextTrimmed.StartsWith("```", StringComparison.Ordinal) || nextTrimmed.StartsWith('|'))
					{
						break;
					}

					item.Append(' ').Append(nextTrimmed);
					i++;
				}

				blocks.Add(new ModGuideBlock(ModGuideBlockKind.ListItem, item.ToString(), depth, Marker: marker));
				continue;
			}

			paragraph.Add(trimmed);
			i++;
		}

		FlushParagraph();
		return new ModGuideDocument(fileName, blocks);
	}

	/// <summary>
	/// The anchor GitHub gives a heading: lower case; letters, digits, spaces, '-' and '_' kept, everything
	/// else dropped; each space a '-'.
	/// </summary>
	public static string Anchor(string heading)
	{
		var sb = new StringBuilder(heading.Length);
		foreach (char c in heading.Trim().ToLowerInvariant())
		{
			if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
			{
				sb.Append(c);
			}
			else if (c == ' ')
			{
				sb.Append('-');
			}
		}

		return sb.ToString();
	}

	private static int? HeadingLevel(string trimmed)
	{
		int level = 0;
		while (level < trimmed.Length && trimmed[level] == '#')
		{
			level++;
		}

		return level is >= 1 and <= 6 && level < trimmed.Length && trimmed[level] == ' ' ? level : null;
	}

	private static (int Depth, string Marker, int ContentStart)? ListMarker(string line)
	{
		int indent = line.Length - line.TrimStart().Length;
		string rest = line.Substring(indent);
		if (rest.Length >= 2 && (rest[0] == '-' || rest[0] == '*') && rest[1] == ' ')
		{
			return (indent / 2, "•", indent + 2);
		}

		int digits = 0;
		while (digits < rest.Length && char.IsDigit(rest[digits]))
		{
			digits++;
		}

		if (digits is > 0 and < 4 && digits + 1 < rest.Length && rest[digits] == '.' && rest[digits + 1] == ' ')
		{
			return (indent / 3, rest.Substring(0, digits + 1), indent + digits + 2);
		}

		return null;
	}

	private static bool IsTableSeparator(string line)
	{
		string t = line.Trim();
		return t.StartsWith('|') && t.Contains('-') && t.All(c => c is '|' or '-' or ':' or ' ');
	}

	/// <summary>A table row's cells: split on '|' outside inline code; <c>\|</c> is a literal pipe.</summary>
	internal static List<string> SplitRow(string row)
	{
		string t = row.Trim();
		if (t.StartsWith('|'))
		{
			t = t.Substring(1);
		}

		if (t.EndsWith('|') && !t.EndsWith("\\|", StringComparison.Ordinal))
		{
			t = t.Substring(0, t.Length - 1);
		}

		var cells = new List<string>();
		var cell = new StringBuilder();
		bool inCode = false;
		for (int i = 0; i < t.Length; i++)
		{
			char c = t[i];
			if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|')
			{
				cell.Append('|');
				i++;
			}
			else if (c == '`')
			{
				inCode = !inCode;
				cell.Append(c);
			}
			else if (c == '|' && !inCode)
			{
				cells.Add(cell.ToString().Trim());
				cell.Clear();
			}
			else
			{
				cell.Append(c);
			}
		}

		cells.Add(cell.ToString().Trim());
		return cells;
	}

	private static List<string> Pad(IReadOnlyList<string> row, int columns)
	{
		var padded = row.Take(columns).ToList();
		while (padded.Count < columns)
		{
			padded.Add(string.Empty);
		}

		return padded;
	}

	// ---------------------------------------------------------------------------------------
	// Links
	// ---------------------------------------------------------------------------------------

	/// <summary>Every <c>[label](target)</c> in a run of inline markdown, skipping inline code.</summary>
	public static IEnumerable<(string Label, string Target)> InlineLinks(string text)
	{
		int i = 0;
		while (i < text.Length)
		{
			if (text[i] == '`')
			{
				int close = text.IndexOf('`', i + 1);
				i = close < 0 ? text.Length : close + 1;
				continue;
			}

			if (text[i] == '[' && TryLink(text, i, out string label, out string target, out int end))
			{
				yield return (label, target);
				i = end;
				continue;
			}

			i++;
		}
	}

	/// <summary>A link target as the guide reads it, relative to <paramref name="fromFile"/>.</summary>
	public static ModGuideLink ResolveLink(string fromFile, string label, string target)
	{
		if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
		    target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			return new ModGuideLink(label, target, string.Empty, string.Empty, true);
		}

		int hash = target.IndexOf('#');
		string file = hash < 0 ? target : target.Substring(0, hash);
		string anchor = hash < 0 ? string.Empty : target.Substring(hash + 1);
		if (file.StartsWith("./", StringComparison.Ordinal))
		{
			file = file.Substring(2);
		}

		return new ModGuideLink(label, target, file.Length == 0 ? fromFile : file, anchor, false);
	}

	/// <summary>The RichTextLabel meta a guide link carries.</summary>
	public static string LinkMeta(string file, string anchor) => MetaScheme + file + (anchor.Length == 0 ? string.Empty : "#" + anchor);

	/// <summary>Reads a meta <see cref="LinkMeta"/> wrote.</summary>
	public static bool TryParseLinkMeta(string meta, out string file, out string anchor)
	{
		file = anchor = string.Empty;
		if (!meta.StartsWith(MetaScheme, StringComparison.Ordinal))
		{
			return false;
		}

		string rest = meta.Substring(MetaScheme.Length);
		int hash = rest.IndexOf('#');
		file = hash < 0 ? rest : rest.Substring(0, hash);
		anchor = hash < 0 ? string.Empty : rest.Substring(hash + 1);
		return file.Length != 0;
	}

	private static bool TryLink(string text, int open, out string label, out string target, out int end)
	{
		label = target = string.Empty;
		end = open;
		int depth = 0;
		int close = -1;
		for (int i = open; i < text.Length; i++)
		{
			if (text[i] == '[')
			{
				depth++;
			}
			else if (text[i] == ']' && --depth == 0)
			{
				close = i;
				break;
			}
		}

		if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
		{
			return false;
		}

		int paren = text.IndexOf(')', close + 2);
		if (paren < 0)
		{
			return false;
		}

		string t = text.Substring(close + 2, paren - close - 2).Trim();
		if (t.Length == 0 || t.Contains(' '))
		{
			return false;
		}

		label = text.Substring(open + 1, close - open - 1);
		target = t;
		end = paren + 1;
		return true;
	}

	// ---------------------------------------------------------------------------------------
	// BBCode
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// The file as BBCode: headings sized and coloured, list items indented with their marker, code blocks
	/// monospace on a dark ground, tables as a grid with a bold header row, guide links as
	/// <c>[url=guide:file#anchor]</c>. Every heading's anchor maps to the paragraph it starts at (the number
	/// of line breaks before it; table cells never hold one), for <c>RichTextLabel.ScrollToParagraph</c>.
	/// </summary>
	public static ModGuideRendered ToBbCode(ModGuideDocument doc)
	{
		var sb = new StringBuilder();
		var anchors = new Dictionary<string, int>(StringComparer.Ordinal);
		int newlines = 0;

		void Line(string text)
		{
			sb.Append(text).Append('\n');
			newlines += 1 + text.Count(c => c == '\n');
		}

		ModGuideBlockKind? previous = null;
		foreach (ModGuideBlock block in doc.Blocks)
		{
			// A blank line between blocks, except between consecutive list items.
			if (previous is not null && !(previous == ModGuideBlockKind.ListItem && block.Kind == ModGuideBlockKind.ListItem))
			{
				Line(string.Empty);
			}

			switch (block.Kind)
			{
				case ModGuideBlockKind.Heading:
					anchors[block.Anchor] = newlines;
					int size = block.Level switch { 1 => 24, 2 => 20, 3 => 17, _ => 15 };
					Line($"[font_size={size}][color={HeadingColour}][b]{Inline(block.Text, doc.FileName)}[/b][/color][/font_size]");
					break;
				case ModGuideBlockKind.Paragraph:
					Line(Inline(block.Text, doc.FileName));
					break;
				case ModGuideBlockKind.ListItem:
				{
					string open = string.Concat(Enumerable.Repeat("[indent]", block.Level + 1));
					string close = string.Concat(Enumerable.Repeat("[/indent]", block.Level + 1));
					Line($"{open}{block.Marker} {Inline(block.Text, doc.FileName)}{close}");
					break;
				}
				case ModGuideBlockKind.Code:
					Line($"[indent][bgcolor={CodeBlockBackground}][code]{Escape(block.Text)}[/code][/bgcolor][/indent]");
					break;
				case ModGuideBlockKind.Table:
					Line(Table(block.Rows!, doc.FileName));
					break;
				case ModGuideBlockKind.Rule:
					Line("[color=#3a4a60]────────────────────────────────[/color]");
					break;
			}

			previous = block.Kind;
		}

		return new ModGuideRendered(sb.ToString(), anchors);
	}

	private static string Table(IReadOnlyList<IReadOnlyList<string>> rows, string fileName)
	{
		int columns = rows[0].Count;
		var sb = new StringBuilder();
		sb.Append("[table=").Append(columns.ToString(CultureInfo.InvariantCulture)).Append(']');
		for (int c = 0; c < columns; c++)
		{
			// Wider text, wider column: the ratio follows each column's mean visible length.
			double mean = rows.Average(r => (double)VisibleLength(r[c]));
			int ratio = Math.Clamp((int)Math.Round(mean / 12.0), 1, 8);
			sb.Append("[cell expand=").Append(ratio.ToString(CultureInfo.InvariantCulture))
				.Append(" bg=#22304a border=#2c3a52 padding=6,3,6,3][b]")
				.Append(OneLine(Inline(rows[0][c], fileName))).Append("[/b][/cell]");
		}

		for (int r = 1; r < rows.Count; r++)
		{
			string bg = r % 2 == 1 ? "#121822" : "#171f2c";
			for (int c = 0; c < columns; c++)
			{
				sb.Append("[cell bg=").Append(bg).Append(" border=#2c3a52 padding=6,3,6,3]")
					.Append(OneLine(Inline(rows[r][c], fileName))).Append("[/cell]");
			}
		}

		sb.Append("[/table]");
		return sb.ToString();
	}

	private static string OneLine(string text) => text.Replace('\n', ' ');

	private static int VisibleLength(string markdown) =>
		markdown.Count(c => c != '`' && c != '*');

	/// <summary>Inline markdown as BBCode: code spans, links, bold and italic; every other '[' escaped.</summary>
	public static string Inline(string text, string fileName)
	{
		var sb = new StringBuilder(text.Length + 16);
		int i = 0;
		while (i < text.Length)
		{
			char c = text[i];
			if (c == '\\' && i + 1 < text.Length && "\\`*_[]()#|".IndexOf(text[i + 1]) >= 0)
			{
				sb.Append(Escape(text[i + 1].ToString()));
				i += 2;
				continue;
			}

			if (c == '`')
			{
				int close = text.IndexOf('`', i + 1);
				if (close > i)
				{
					sb.Append("[color=").Append(CodeColour).Append("][code]")
						.Append(Escape(text.Substring(i + 1, close - i - 1)))
						.Append("[/code][/color]");
					i = close + 1;
					continue;
				}
			}

			if (c == '[' && TryLink(text, i, out string label, out string target, out int end))
			{
				ModGuideLink link = ResolveLink(fileName, label, target);
				string meta = link.External ? link.Target : LinkMeta(link.File, link.Anchor);
				sb.Append("[url=").Append(meta.Replace("]", "%5D")).Append("][color=").Append(LinkColour).Append(']')
					.Append(Inline(label, fileName)).Append("[/color][/url]");
				i = end;
				continue;
			}

			if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
			{
				int close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
				if (close > i + 2)
				{
					sb.Append("[b]").Append(Inline(text.Substring(i + 2, close - i - 2), fileName)).Append("[/b]");
					i = close + 2;
					continue;
				}
			}

			if (c == '*' && i + 1 < text.Length && text[i + 1] != ' ')
			{
				int close = text.IndexOf('*', i + 1);
				if (close > i + 1 && text[close - 1] != ' ')
				{
					sb.Append("[i]").Append(Inline(text.Substring(i + 1, close - i - 1), fileName)).Append("[/i]");
					i = close + 1;
					continue;
				}
			}

			sb.Append(c == '[' ? "[lb]" : c.ToString());
			i++;
		}

		return sb.ToString();
	}

	/// <summary>Text shown as written: every '[' becomes <c>[lb]</c>, so nothing in it is read as a tag.</summary>
	public static string Escape(string text) => text.Replace("[", "[lb]");
}
