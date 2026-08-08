using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Refii.Launcher.Formats;

/// <summary>
/// Lays a <see cref="UiText"/> out against its bitmap font and builds one mesh of glyph
/// quads for it.
///
/// One mesh per text element, not one per glyph: a screen carries up to a few hundred
/// strings and a node each would swamp the tree the editor has to show.
///
/// UNITS: one font pixel is one world unit. The authoring canvas is 960x720 and the
/// authored cameras frame close to 720 units of height at the content plane, so a point
/// size means the same thing in both spaces. Y is up, as everywhere else in the built
/// tree, so the pen walks DOWN in -Y.
/// </summary>
public static class UiTextMesh
{
	/// <summary>
	/// Whitespace the wrap pass may break on — <c>sub_82B90978</c> scans backwards for
	/// tab, CR, space or the ideographic space.
	/// </summary>
	private static bool IsBreakSpace(char c) => c is '\t' or '\r' or ' ' or '　';

	/// <summary>A laid-out line and the width it measured to.</summary>
	private readonly struct Line
	{
		public readonly string Text;
		public readonly float Width;

		public Line(string text, float width)
		{
			Text = text;
			Width = width;
		}
	}

	/// <summary>
	/// Builds the glyph mesh, or null when there is nothing to draw.
	/// </summary>
	/// <param name="scale">
	/// The residual scale from <see cref="UiFontLibrary.SelectFor"/> — a point size that
	/// does not sit on a bucket boundary draws its bucket's font scaled.
	/// </param>
	public static ArrayMesh? Build(UiText text, BitmapFont font, float scale, out int lineCount)
	{
		lineCount = 0;

		string content = StripMarkup(text.Display);
		if (content.Length == 0)
			return null;

		// boxwidth only ever enters layout as the WRAP width — the engine passes it as
		// the rect {0, 0, boxwidth, 10000} to the wrap pass. It is not the box the text
		// is aligned inside; see below.
		float wrapWidth = text.BoxWidth / Mathf.Max(scale, 0.0001f);
		List<Line> lines = Wrap(content, font, text.WrapMode == 1 ? wrapWidth : 0f);
		lineCount = lines.Count;
		if (lines.Count == 0)
			return null;

		// ALIGNMENT ANCHORS ON THE MEASURED EXTENT, NOT ON boxwidth/boxheight.
		// sub_828720D8 seeds an empty AABB, grows it by +/- half the text object's
		// MEASURED width and height, then shifts it by that same half extent: code 0
		// puts the near edge on the origin, 2 the far edge, anything else centres it.
		// boxwidth/boxheight are OUTPUTS the element publishes, not inputs — reading
		// them as the alignment box places short strings inside a wide box instead of
		// against the origin, which reads as text drifting off its anchor.
		//
		// The measure pass counts lineHeight + 1 per line while the draw pass advances
		// lineHeight, so the two differ by one pixel per line. Both are reproduced.
		float lineAdvance = font.LineHeight;
		float quadHeight = font.LineHeight + 1;

		float measuredWidth = 0f;
		foreach (Line line in lines)
			measuredWidth = Mathf.Max(measuredWidth, line.Width);
		float measuredHeight = lines.Count * (font.LineHeight + 1);

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
		var indices = new List<int>();

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

			foreach (char c in line.Text)
			{
				BitmapGlyph g = font.GetGlyph(c);
				float glyphLeft = penX + g.LeftBearing;
				penX = glyphLeft + g.PostAdvance;

				if (g.DrawWidth <= 0)
					continue;

				Append(vertices, uvs, normals, indices,
					glyphLeft, lineTop, g.DrawWidth, quadHeight, g);
			}
		}

		if (indices.Count == 0)
			return null;

		if (!Mathf.IsEqualApprox(scale, 1f))
			for (int i = 0; i < vertices.Count; i++)
				vertices[i] = vertices[i] * scale;

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
		arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
		arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
		arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}

	/// <summary>
	/// Removes the inline markup, turning <c>&lt;br&gt;</c> into a newline.
	///
	/// The delimiters are <c>&lt;</c> and <c>&gt;</c> and the characters between them
	/// emit no glyphs — the tag vocabulary is 46 entries of button-icon and size/style
	/// switches (<c>b</c>, <c>s1</c>..<c>s4</c>, <c>a_img</c>, <c>dpad.img</c>, …), and
	/// the engine substitutes <c>&lt;br&gt;</c> for a newline before laying out at all.
	///
	/// Icons are NOT drawn yet. Each occupies a square of lineHeight (double for one
	/// group of ids), so a string containing one lays out slightly narrow here. Emitting
	/// the tag text verbatim instead would be worse — it would put "&lt;a_img&gt;" on
	/// screen.
	/// </summary>
	public static string StripMarkup(string value)
	{
		if (value.IndexOf('<') < 0)
			return value;

		var output = new StringBuilder(value.Length);
		for (int i = 0; i < value.Length; i++)
		{
			if (value[i] != '<')
			{
				output.Append(value[i]);
				continue;
			}

			int close = value.IndexOf('>', i + 1);
			if (close < 0)
			{
				// Unterminated: the engine would swallow the rest of the string, but
				// showing it is more useful than silently losing it.
				output.Append(value, i, value.Length - i);
				break;
			}

			if (string.Equals(value.Substring(i + 1, close - i - 1), "br", StringComparison.OrdinalIgnoreCase))
				output.Append('\n');

			i = close;
		}

		return output.ToString();
	}

	/// <summary>
	/// Appends one glyph quad. Wound to match <c>UiSceneBuilder</c>'s shared quad —
	/// counter-clockwise seen from +Z, V increasing downward from the top-left.
	/// </summary>
	private static void Append(List<Vector3> vertices, List<Vector2> uvs, List<Vector3> normals,
		List<int> indices, float left, float top, float width, float height, in BitmapGlyph g)
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
			normals.Add(new Vector3(0f, 0f, 1f));

		indices.Add(b + 0); indices.Add(b + 1); indices.Add(b + 2);
		indices.Add(b + 0); indices.Add(b + 2); indices.Add(b + 3);
	}

	/// <summary>
	/// Splits into lines. Explicit newlines always break; wrapping happens only when
	/// <paramref name="maxWidth"/> is positive — mode 0 is a single line the engine
	/// truncates rather than folding.
	///
	/// Width accumulates by <see cref="BitmapGlyph.Advance"/>, never by the draw width.
	/// The break search runs backwards from the overflow point to the last breakable
	/// space and falls back to breaking mid-word when the line holds no space at all,
	/// so a long token degrades instead of looping.
	/// </summary>
	private static List<Line> Wrap(string content, BitmapFont font, float maxWidth)
	{
		var lines = new List<Line>();

		foreach (string paragraph in content.Replace("\r\n", "\n").Split('\n'))
		{
			if (maxWidth <= 0f)
			{
				lines.Add(new Line(paragraph, font.MeasureWidth(paragraph)));
				continue;
			}

			if (paragraph.Length == 0)
			{
				lines.Add(new Line(string.Empty, 0f));
				continue;
			}

			int start = 0;
			while (start < paragraph.Length)
			{
				int take = FitCount(font, paragraph, start, maxWidth);
				string line = paragraph.Substring(start, take).TrimEnd();
				lines.Add(new Line(line, font.MeasureWidth(line)));

				start += take;
				while (start < paragraph.Length && IsBreakSpace(paragraph[start]))
					start++;
			}
		}

		return lines;
	}

	/// <summary>How many characters from <paramref name="start"/> fit in <paramref name="maxWidth"/>.</summary>
	private static int FitCount(BitmapFont font, string s, int start, float maxWidth)
	{
		float width = 0f;
		int lastBreak = -1;

		for (int i = start; i < s.Length; i++)
		{
			width += font.GetGlyph(s[i]).Advance;

			if (IsBreakSpace(s[i]))
				lastBreak = i;

			if (width > maxWidth && i > start)
				return lastBreak > start ? lastBreak - start : i - start;
		}

		return s.Length - start;
	}
}
