using System;
using System.Collections.Generic;
using Godot;

using Refii.Core;

using Refii.Text;

using Refii.Textures;

namespace Refii.Ui;

/// <summary>
/// Lays a <see cref="UiText"/> out against its bitmap font and builds one mesh of glyph
/// quads for it, plus the placements of any inline ICONS the string asked for.
///
/// One mesh per text element, not one per glyph.
///
/// Icons are QUADS, not glyphs, and come back as placements rather than in the mesh: the font
/// atlas is single-channel coverage (<see cref="Engine_BitmapFont.AtlasBlocks"/> is BC4), so the
/// text shader reads only `texel.a` and takes its colour from a uniform, while an icon is
/// full-colour art in its own `.tex`. The guest splits them the same way, into two draw-node
/// types (`off_820015A0` text, `off_820F4520` image).
///
/// Units: one font pixel is one world unit — the authoring canvas is 720 high in both titles
/// (960 wide in Fable 2, 1280 in Fable 3) and the authored cameras frame close to 720 units of
/// height at the content plane. Y is up, so the pen walks down in -Y. What differs between the
/// titles is stated by <see cref="Engine_UiTextContract"/>.
/// </summary>
public static class Engine_UiTextMesh
{
	/// <summary>
	/// Whitespace the wrap pass may break on — <c>sub_82B90978</c> scans backwards for
	/// tab, CR, space or the ideographic space.
	/// </summary>
	private static bool IsBreakSpace(char c) => c is '\t' or '\r' or ' ' or '　';

	/// <summary>Where one inline icon lands, in the mesh's own space and already scaled.</summary>
	/// <param name="Tag">The tag, which names the art and whether it is double width.</param>
	/// <param name="TopLeft">Top-left corner. Y is up, so this is the largest Y of the quad.</param>
	/// <param name="Size">Drawn size — see <see cref="Engine_UiTextMarkup.DrawSize"/>.</param>
	public readonly record struct IconPlacement(
		Engine_UiTextMarkup.Tag Tag, Vector2 TopLeft, Vector2 Size);

	/// <summary>What one layout produced.</summary>
	public readonly record struct Built(
		ArrayMesh? Mesh, int LineCount, IReadOnlyList<IconPlacement> Icons);

	/// <summary>
	/// One laid-out atom: a glyph or an icon. Both advance the pen; only glyphs reach the mesh.
	/// One advance, not two — the guest bills an icon at the bare line height in its wrap pass
	/// (`sub_82802F80`) and one more than that when drawing, so its wrap disagrees with its
	/// placement. Here the wrap uses what is drawn.
	/// </summary>
	private readonly struct Cell
	{
		public readonly char Char;
		public readonly Engine_UiTextMarkup.Tag? Icon;
		public readonly Color? Colour;
		public readonly float Advance;

		public Cell(char c, Engine_UiTextMarkup.Tag? icon, Color? colour, float advance)
		{
			Char = c;
			Icon = icon;
			Colour = colour;
			Advance = advance;
		}

		public bool IsIcon => Icon is not null;
	}

	/// <summary>
	/// A laid-out line and the width it covers, icons INCLUDED. The guest's `sub_82870318`, which
	/// computes the line-start X for centre and right alignment, sums only text and colour spans
	/// and skips icons (`0x828703DC`); layout is ours, so a centred prompt is centred here.
	/// </summary>
	private readonly struct Line
	{
		public readonly List<Cell> Cells;
		public readonly float Width;

		public Line(List<Cell> cells, float width)
		{
			Cells = cells;
			Width = width;
		}
	}

	/// <summary>
	/// Builds the glyph mesh and the icon placements, or an empty result when there is
	/// nothing to draw.
	/// </summary>
	/// <param name="scale">
	/// The residual scale from <see cref="Engine_UiFontLibrary.SelectFor"/> — a point size that
	/// does not sit on a bucket boundary draws its bucket's font scaled.
	/// </param>
	/// <param name="contract">
	/// The title's text contract — the font library's <see cref="Engine_UiFontLibrary.Contract"/>;
	/// null takes the active title's.
	/// </param>
	public static Built Build(UiText text, Engine_BitmapFont font, float scale,
		Engine_UiTextContract? contract = null)
	{
		contract ??= Engine_UiTextContract.Active;

		List<List<Cell>> paragraphs = Cells(text.Display, font, contract);
		if (paragraphs.Count == 0)
		{
			return new Built(null, 0, Array.Empty<IconPlacement>());
		}

		// boxwidth only enters layout as the WRAP width — the engine passes the rect
		// {0, 0, boxwidth, 10000} to the wrap pass. It is not the alignment box.
		float wrapWidth = text.BoxWidth / Mathf.Max(scale, 0.0001f);
		List<Line> lines = Wrap(paragraphs, text.WrapMode == 1 ? wrapWidth : 0f);
		if (lines.Count == 0)
		{
			return new Built(null, 0, Array.Empty<IconPlacement>());
		}

		// Alignment anchors on the MEASURED extent, not on boxwidth/boxheight: sub_828720D8
		// seeds an empty AABB, grows it by ± half the measured width and height, then shifts it
		// by that same half extent — code 0 puts the near edge on the origin, 2 the far edge,
		// anything else centres. boxwidth/boxheight are outputs the element publishes.
		//
		// The measure counts lineHeight + 1 per line in both titles: Fable 2's `sub_82B714A0` does
		// `lwz r10, 0x18(r25); addi r6, r10, 1` at `0x82B714EC` and on every U+000A
		// (`0x82B71538`), Fable 3's `sub_82D40300` the same at 0x82D40354/0x82D403A4. The DRAW's
		// newline differs (Engine_UiTextContract.LineAdvanceExtra): Fable 2's `<br>` case at
		// `0x8226F1B4` (`sub_82802DB8` having rewritten `\n` to `<br>`) drops
		// `(lineHeight + 1) * scale`, Fable 3's newline token drops `lineHeight` alone.
		float lineAdvance = font.LineHeight + contract.LineAdvanceExtra;
		float quadHeight = font.LineHeight + 1;

		float measuredWidth = 0f;
		foreach (Line line in lines)
			measuredWidth = Mathf.Max(measuredWidth, line.Width);
		float measuredHeight = lines.Count * quadHeight;

		float left = text.HorzAlign switch
		{
			0 => 0f,
			2 => -measuredWidth,
			_ => -measuredWidth * 0.5f,
		};
		float top = text.VertAlign switch
		{
			0 => 0f,
			2 => measuredHeight,
			_ => measuredHeight * 0.5f,
		};

		var vertices = new List<Vector3>();
		var uvs = new List<Vector2>();
		var normals = new List<Vector3>();
		var colours = new List<Color>();
		var indices = new List<int>();
		var icons = new List<IconPlacement>();

		for (int i = 0; i < lines.Count; i++)
		{
			Line line = lines[i];

			// Per-line alignment inside the measured block, so a ragged wrapped
			// paragraph centres line by line rather than as one rectangle.
			float penX = text.HorzAlign switch
			{
				0 => left,
				2 => left + (measuredWidth - line.Width),
				_ => left + (measuredWidth - line.Width) * 0.5f,
			};
			float lineTop = top - i * lineAdvance;

			foreach (Cell cell in line.Cells)
			{
				if (cell.Icon is { } tag)
				{
					(float width, float height) = contract.IconSize(
						tag is { Kind: Engine_UiTextMarkup.Kind.Icon, DoubleWidth: true }, font.LineHeight);
					icons.Add(new IconPlacement(
						tag,
						new Vector2(penX - contract.IconInset, lineTop + contract.IconInset) * scale,
						new Vector2(width, height) * scale));
					penX += cell.Advance;
					continue;
				}

				BitmapGlyph g = font.GetGlyph(cell.Char);
				float glyphLeft = penX + g.LeftBearing;
				penX = glyphLeft + g.PostAdvance;

				if (g.DrawWidth <= 0)
					continue;

				Append(vertices, uvs, normals, colours, indices,
					glyphLeft, lineTop, g.DrawWidth, quadHeight, g,
					// White multiplies to the element's own colour, which is what an
					// unmarked run gets and what keeps Engine_UiTheme.Retint working.
					cell.Colour ?? Colors.White);
			}
		}

		if (indices.Count == 0)
		{
			return new Built(null, lines.Count, icons);
		}

		if (!Mathf.IsEqualApprox(scale, 1f))
			for (int i = 0; i < vertices.Count; i++)
				vertices[i] = vertices[i] * scale;

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
		arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
		arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
		arrays[(int)Mesh.ArrayType.Color] = colours.ToArray();
		arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

		var mesh = new ArrayMesh();
		Refii.Core.Engine_ArrayMeshes.AddSurface(mesh, Mesh.PrimitiveType.Triangles, arrays);
		return new Built(mesh, lines.Count, icons);
	}

	/// <summary>
	/// Turns a string into one cell list per paragraph, running the markup tokeniser first. An
	/// icon advances by the title's <see cref="Engine_UiTextContract.IconPenAdvance"/> — Fable 2's
	/// `lineHeight + 1` box (doubled in width for the five stars), Fable 3's fixed 26.
	/// </summary>
	private static List<List<Cell>> Cells(string display, Engine_BitmapFont font,
		Engine_UiTextContract contract)
	{
		var paragraphs = new List<List<Cell>>();
		var current = new List<Cell>();

		foreach (Engine_UiTextMarkup.Run run in Engine_UiTextMarkup.Split(display))
		{
			if (run.Break)
			{
				paragraphs.Add(current);
				current = new List<Cell>();
				continue;
			}

			if (run.Tag is { } icon)
			{
				current.Add(new Cell(
					'\0', icon, run.Colour,
					contract.IconPenAdvance(
						icon is { Kind: Engine_UiTextMarkup.Kind.Icon, DoubleWidth: true }, font.LineHeight)));
				continue;
			}

			foreach (char c in run.Text)
			{
				current.Add(new Cell(c, null, run.Colour, font.GetGlyph(c).Advance));
			}
		}

		paragraphs.Add(current);

		// An entirely empty string has one empty paragraph and nothing to draw.
		if (paragraphs.Count == 1 && paragraphs[0].Count == 0)
		{
			paragraphs.Clear();
		}

		return paragraphs;
	}

	/// <summary>
	/// Appends one glyph quad. Wound to match <c>Engine_UiSceneBuilder</c>'s shared quad —
	/// counter-clockwise seen from +Z, V increasing downward from the top-left.
	/// </summary>
	private static void Append(List<Vector3> vertices, List<Vector2> uvs, List<Vector3> normals,
		List<Color> colours, List<int> indices,
		float left, float top, float width, float height, in BitmapGlyph g, Color colour)
	{
		int b = vertices.Count;
		float right = left + width;
		float bottom = top - height;

		vertices.Add(new Vector3(left, top, 0f));
		vertices.Add(new Vector3(left, bottom, 0f));
		vertices.Add(new Vector3(right, bottom, 0f));
		vertices.Add(new Vector3(right, top, 0f));

		uvs.Add(new Vector2(g.U0, g.V0));
		uvs.Add(new Vector2(g.U0, g.V1));
		uvs.Add(new Vector2(g.U1, g.V1));
		uvs.Add(new Vector2(g.U1, g.V0));

		for (int i = 0; i < 4; i++)
		{
			normals.Add(new Vector3(0f, 0f, 1f));
			colours.Add(colour);
		}

		indices.Add(b + 0); indices.Add(b + 1); indices.Add(b + 2);
		indices.Add(b + 0); indices.Add(b + 2); indices.Add(b + 3);
	}

	/// <summary>
	/// Splits into lines. Paragraphs are already split on <c>&lt;br&gt;</c>; wrapping happens
	/// only when <paramref name="maxWidth"/> is positive — mode 0 is a single line the engine
	/// truncates rather than folding.
	///
	/// Width accumulates by <see cref="Cell.Advance"/>. The break search runs backwards from the
	/// overflow point to the last breakable space and falls back to breaking mid-word when the
	/// line holds none, so a long token degrades instead of looping. An icon is never a break
	/// point.
	/// </summary>
	private static List<Line> Wrap(List<List<Cell>> paragraphs, float maxWidth)
	{
		var lines = new List<Line>();

		foreach (List<Cell> paragraph in paragraphs)
		{
			if (maxWidth <= 0f || paragraph.Count == 0)
			{
				lines.Add(new Line(paragraph, Measure(paragraph, 0, paragraph.Count)));
				continue;
			}

			int start = 0;
			while (start < paragraph.Count)
			{
				int take = FitCount(paragraph, start, maxWidth);

				// TrimEnd, over cells: trailing breakable space does not count toward the
				// line's measured width.
				int end = start + take;
				while (end > start && !paragraph[end - 1].IsIcon &&
					IsBreakSpace(paragraph[end - 1].Char))
				{
					end--;
				}

				lines.Add(new Line(
					paragraph.GetRange(start, end - start), Measure(paragraph, start, end)));

				start += take;
				while (start < paragraph.Count && !paragraph[start].IsIcon &&
					IsBreakSpace(paragraph[start].Char))
				{
					start++;
				}
			}
		}

		return lines;
	}

	/// <summary>A run of cells' width.</summary>
	private static float Measure(List<Cell> cells, int from, int to)
	{
		float width = 0f;
		for (int i = from; i < to; i++)
		{
			width += cells[i].Advance;
		}

		return width;
	}

	/// <summary>How many cells from <paramref name="start"/> fit in <paramref name="maxWidth"/>.</summary>
	private static int FitCount(List<Cell> cells, int start, float maxWidth)
	{
		float width = 0f;
		int lastBreak = -1;

		for (int i = start; i < cells.Count; i++)
		{
			width += cells[i].Advance;

			if (!cells[i].IsIcon && IsBreakSpace(cells[i].Char))
				lastBreak = i;

			if (width > maxWidth && i > start)
				return lastBreak > start ? lastBreak - start : i - start;
		}

		return cells.Count - start;
	}
}
