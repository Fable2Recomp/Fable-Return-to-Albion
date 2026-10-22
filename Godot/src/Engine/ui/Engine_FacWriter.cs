using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.Ui;

/// <summary>
/// Writes a <c>.fac</c> sidecar — the exact inverse of <see cref="Engine_FacDocument.Parse"/>.
///
/// The file as emitted: ASCII, one value per line, each record's lines then one blank line,
/// CRLF line endings (every shipped file's), the file ending with the last record's blank
/// line. A record read from a file is written from its lines as read, so a parsed file comes
/// back byte-identical; a record made in memory is formatted from its fields in the exporter's
/// style, observed across the shipped set:
/// <code>
/// UV      id, three extra ids, texture hash (unsigned), du, dv, u0, v0, detail map mode,
///         secondary texture, field 12 ("0.0"), vertex lane 3, "r g b 1"
/// TEXT    id, text (percent-escaped, "-" when empty), wrap mode, size, box width, box height,
///         horizontal align, vertical align, style, "r g b 1"
/// LIGHT   id, r, g, b (each with a decimal point)
/// </code>
/// Floats print shortest-round-trip ("0.25", "0.502", "0"); the colour line's fourth value is
/// the 1 the engine discards. <see cref="TryWrite"/> parses what it wrote and refuses text the
/// shipping reader does not read as the same document.
/// </summary>
public static class Engine_FacWriter
{
	public static byte[] Write(Engine_FacDocument doc)
	{
		if (!TryWrite(doc, out byte[] bytes, out string error))
		{
			throw new InvalidDataException("fac write: " + error);
		}

		return bytes;
	}

	public static bool TryWrite(Engine_FacDocument doc, out byte[] bytes, out string error)
	{
		string text = Emit(doc);
		bytes = Encoding.ASCII.GetBytes(text);
		if (Encoding.ASCII.GetString(bytes) != text)
		{
			error = "a record carries a non-ASCII character the file cannot hold";
			bytes = Array.Empty<byte>();
			return false;
		}

		Engine_FacDocument back = Engine_FacDocument.Parse(text);
		if (!Same(doc, back, out string difference))
		{
			error = "the written text parses to a different document: " + difference;
			bytes = Array.Empty<byte>();
			return false;
		}

		error = string.Empty;
		return true;
	}

	public static string Emit(Engine_FacDocument doc)
	{
		var sb = new StringBuilder();
		string le = doc.LineEnding;
		for (int i = 0; i < doc.Records.Count; i++)
		{
			FacRecord record = doc.Records[i];
			string[] lines = record.Lines ?? Format(record);
			bool last = i == doc.Records.Count - 1;
			for (int k = 0; k < lines.Length; k++)
			{
				sb.Append(lines[k]);
				if (!last || k < lines.Length - 1 || record.Closed || doc.EndsWithNewline)
				{
					sb.Append(le);
				}
			}

			if (record.Closed)
			{
				sb.Append(le);
			}
		}

		return sb.ToString();
	}

	/// <summary>A record made in memory, in the exporter's style.</summary>
	public static string[] Format(FacRecord record)
	{
		if (record.Uv is { } uv)
		{
			return new[]
			{
				"UV",
				uv.StableId.ToString(CultureInfo.InvariantCulture),
				uv.Extra.Length > 0 ? uv.Extra[0] : "0",
				uv.Extra.Length > 1 ? uv.Extra[1] : "0",
				uv.Extra.Length > 2 ? uv.Extra[2] : "0",
				uv.TextureHash.ToString(CultureInfo.InvariantCulture),
				F(uv.Du), F(uv.Dv), F(uv.U0), F(uv.V0),
				uv.DetailMapMode.ToString(CultureInfo.InvariantCulture),
				uv.SecondaryTexture.ToString(CultureInfo.InvariantCulture),
				uv.Field12,
				uv.VertexLane3 == 0f ? "0.0" : F(uv.VertexLane3),
				$"{F(uv.R)} {F(uv.G)} {F(uv.B)} 1",
			};
		}

		if (record.Text is { } text)
		{
			return new[]
			{
				"TEXT",
				text.StableId.ToString(CultureInfo.InvariantCulture),
				text.Text.Length == 0 ? "-" : PercentEncode(text.Text),
				text.WrapMode.ToString(CultureInfo.InvariantCulture),
				text.Size.ToString(CultureInfo.InvariantCulture),
				F(text.BoxWidth), F(text.BoxHeight),
				text.HorzAlign.ToString(CultureInfo.InvariantCulture),
				text.VertAlign.ToString(CultureInfo.InvariantCulture),
				text.Style.ToString(CultureInfo.InvariantCulture),
				$"{F(text.R)} {F(text.G)} {F(text.B)} 1",
			};
		}

		if (record.Light is { } light)
		{
			return new[] { "LIGHT", light.StableId.ToString(CultureInfo.InvariantCulture), D(light.R), D(light.G), D(light.B) };
		}

		return Array.Empty<string>();
	}

	/// <summary>Shortest round-trip float, invariant: "0.25", "0.502", "0", "-1".</summary>
	private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

	/// <summary>A light channel: always with a decimal point, as shipped ("1.0", "0.878431").</summary>
	private static string D(float v)
	{
		string s = F(v);
		return s.Contains('.') || s.Contains('E') ? s : s + ".0";
	}

	/// <summary>
	/// The exporter's escapes: percent, the quotes, and every byte outside printable ASCII as the
	/// UTF-8 bytes, percent-encoded — the inverse of <see cref="Engine_FacDocument.PercentDecode"/>.
	/// </summary>
	public static string PercentEncode(string s)
	{
		var sb = new StringBuilder(s.Length);
		foreach (byte b in Encoding.UTF8.GetBytes(s))
		{
			if (b < 0x20 || b > 0x7E || b == (byte)'%' || b == (byte)'\'' || b == (byte)'"')
			{
				sb.Append('%').Append(b.ToString("X2"));
			}
			else
			{
				sb.Append((char)b);
			}
		}

		return sb.ToString();
	}

	/// <summary>Record-for-record equality as the parser reads them, floats by their bits; the first difference named.</summary>
	public static bool Same(Engine_FacDocument a, Engine_FacDocument b, out string difference)
	{
		difference = string.Empty;
		if (a.Records.Count != b.Records.Count) return Differs(out difference, $"record count {a.Records.Count} vs {b.Records.Count}");
		// The line ending is a property of the text only once there is one; the final line
		// ending matters only for a last record the file ends inside.
		if (a.Records.Count > 0 && a.LineEnding != b.LineEnding) return Differs(out difference, "line ending");
		if (a.Records.Count > 0 && !a.Records[^1].Closed && a.EndsWithNewline != b.EndsWithNewline) return Differs(out difference, "final line ending");
		for (int i = 0; i < a.Records.Count; i++)
		{
			FacRecord x = a.Records[i], y = b.Records[i];
			if (x.Closed != y.Closed) return Differs(out difference, $"record {i} closing");
			if (x.Lines is not null && y.Lines is not null && !x.Lines.SequenceEqual(y.Lines, StringComparer.Ordinal))
				return Differs(out difference, $"record {i} lines");
			if ((x.Uv is null) != (y.Uv is null) || (x.Text is null) != (y.Text is null) || (x.Light is null) != (y.Light is null))
				return Differs(out difference, $"record {i} kind");
			if (x.Uv is { } p && y.Uv is { } q && !SameUv(p, q)) return Differs(out difference, $"record {i} UV {p.StableId}");
			if (x.Text is { } s && y.Text is { } t && !SameText(s, t)) return Differs(out difference, $"record {i} TEXT {s.StableId}");
			if (x.Light is { } l && y.Light is { } m &&
				(l.StableId != m.StableId || Bits(l.R) != Bits(m.R) || Bits(l.G) != Bits(m.G) || Bits(l.B) != Bits(m.B)))
				return Differs(out difference, $"record {i} LIGHT {l.StableId}");
		}

		return true;
	}

	private static bool SameUv(FacUvRecord p, FacUvRecord q) =>
		p.StableId == q.StableId && p.Extra.SequenceEqual(q.Extra, StringComparer.Ordinal) && p.TextureHash == q.TextureHash &&
		Bits(p.Du) == Bits(q.Du) && Bits(p.Dv) == Bits(q.Dv) && Bits(p.U0) == Bits(q.U0) && Bits(p.V0) == Bits(q.V0) &&
		p.DetailMapMode == q.DetailMapMode && p.SecondaryTexture == q.SecondaryTexture &&
		string.Equals(p.Field12, q.Field12, StringComparison.Ordinal) && Bits(p.VertexLane3) == Bits(q.VertexLane3) &&
		Bits(p.R) == Bits(q.R) && Bits(p.G) == Bits(q.G) && Bits(p.B) == Bits(q.B);

	private static bool SameText(FacTextRecord s, FacTextRecord t) =>
		s.StableId == t.StableId && string.Equals(s.Text, t.Text, StringComparison.Ordinal) && s.WrapMode == t.WrapMode &&
		s.Size == t.Size && Bits(s.BoxWidth) == Bits(t.BoxWidth) && Bits(s.BoxHeight) == Bits(t.BoxHeight) &&
		s.HorzAlign == t.HorzAlign && s.VertAlign == t.VertAlign && s.Style == t.Style &&
		Bits(s.R) == Bits(t.R) && Bits(s.G) == Bits(t.G) && Bits(s.B) == Bits(t.B);

	private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

	private static bool Differs(out string difference, string what)
	{
		difference = what;
		return false;
	}
}
