using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Refii.Core;

using Refii.Text;

using Refii.Textures;

namespace Refii.Ui;

/// <summary>
/// A textured quad. <see cref="StableId"/> matches a <see cref="BgfElement.ObjectId"/>.
/// </summary>
public sealed class FacUvRecord
{
	public int StableId;

	/// <summary>
	/// Fields 2..4 as written — the exporter's ids of the quad's geometry, material and texture
	/// children, which the engine skips. Carried for the writer; "0" on a record made here.
	/// </summary>
	public string[] Extra = { "0", "0", "0" };

	/// <summary>Field 12 as written, skipped by the engine; "0.0" in every shipped record.</summary>
	public string Field12 = "0.0";

	/// <summary>FNV-1 of the lowercased resource path, e.g. <c>art\gui\frames\frames_04.tex</c>.</summary>
	public uint TextureHash;

	public float Du, Dv, U0, V0;

	/// <summary>Only the value 4 acts, selecting the button detail map.</summary>
	public int DetailMapMode;

	/// <summary>Selects a secondary texture slot; 3 picks the tiled frame background.</summary>
	public int SecondaryTexture;

	/// <summary>
	/// Fourth vertex-colour lane. Mechanically it is written to the vertex, but it is
	/// 0 in ~85% of records including visibly-rendered dialogs, so it is NOT plain
	/// opacity. Do not multiply the tint by it without checking against the original.
	/// </summary>
	public float VertexLane3;

	/// <summary>Vertex tint, 0..1. The authored fourth component is read and discarded by the engine.</summary>
	public float R, G, B;

	/// <summary>Left edge of the atlas rectangle.</summary>
	public float UMin => U0;
	public float UMax => U0 + Du;

	/// <summary>V is flipped relative to the authored origin — the engine emits <c>1 - v</c>.</summary>
	public float VMin => 1.0f - V0 - Dv;
	public float VMax => 1.0f - V0;
}

/// <summary>A text placement. <see cref="StableId"/> matches a <see cref="BgfElement.ObjectId"/>.</summary>
public sealed class FacTextRecord
{
	public int StableId;

	/// <summary>
	/// A Babel tag or a literal. The engine tries the tag first and falls back to
	/// displaying this verbatim. <c>"-"</c> is the empty sentinel.
	/// </summary>
	public string Text = string.Empty;

	public bool IsEmpty => Text == "-";

	/// <summary>1 = wrapped box mode (per-line align, auto-shrink); 0 = single line with truncation.</summary>
	public int WrapMode;

	/// <summary>Point size. Selects the font bucket (<see cref="Engine_UiTextContract.SelectFor"/>).</summary>
	public int Size;

	/// <summary>Authored 0..1 of the title's text canvas width (<see cref="Engine_UiTextContract.BoxWidthUnits"/>).</summary>
	public float BoxWidth;

	/// <summary>Authored 0..1; multiply by 720.</summary>
	public float BoxHeight;

	/// <summary>0 = left, 1 = centre, 2 = right.</summary>
	public int HorzAlign;

	/// <summary>0 / 1 / 2, with 1 = middle.</summary>
	public int VertAlign;

	/// <summary>The draw style, as authored. Fable 3's element keeps it below 4, else 0
	/// (<see cref="Engine_UiTextContract.ElementStyle"/>), and outlines style 1
	/// (<see cref="Engine_UiTextContract.Outlines"/>).</summary>
	public int Style;

	/// <summary>Text tint. The authored alpha is overwritten with 1.0 by the engine — text is always opaque.</summary>
	public float R, G, B;
}

/// <summary>A light colour override. <see cref="StableId"/> matches a <see cref="BgfElement.ObjectId"/>.</summary>
public sealed class FacLightRecord
{
	public int StableId;
	public float R, G, B;
}

/// <summary>
/// One record of the file in file order: its lines as read (null for a record made in memory,
/// which <see cref="Engine_FacWriter"/> then formats from its fields) and the typed record it
/// decoded to — at most one of the three; none for a record the reader does not decode (an
/// unknown keyword, a short record, the empty record an extra blank line makes).
/// </summary>
public sealed class FacRecord
{
	public string[]? Lines;
	public FacUvRecord? Uv;
	public FacTextRecord? Text;
	public FacLightRecord? Light;

	/// <summary>The record was closed by a blank line; false only for a last record the file ends inside.</summary>
	public bool Closed = true;
}

/// <summary>
/// Reader for the <c>.fac</c> sidecar that ships beside every <c>.bgf</c> presentation.
/// It carries the baked render parameters the presentation itself does not: which
/// texture a quad samples, its atlas rectangle and tint, and each text element's
/// string, size, box and alignment.
///
/// Format is plain ASCII: one value per line, records separated by blank lines,
/// each record opening with its type keyword. String fields are percent-escaped
/// (<c>%27</c>, <c>%22</c>, and non-ASCII as UTF-8 bytes); the runtime does not
/// unescape, so decoding belongs at build time — which is what this reader does.
///
/// Two things a consumer must handle, both observed across the 52 shipped files:
/// <list type="bullet">
/// <item><b>LIGHT records do not key on the element table.</b> All 56 shipped light
///   records carry a <c>StableId</c> with no matching <see cref="BgfElement.ObjectId"/>
///   — they address the <c>.bsg</c> scene graph instead. Resolve them there, or skip them.</item>
/// <item><b>A few UV records are stale.</b> <c>holdbutton.fac</c> carries two quads whose
///   elements no longer exist in its presentation. Skip records that fail to resolve
///   rather than treating a miss as a parse error.</item>
/// </list>
/// UV and TEXT otherwise resolve exactly: 15,813 of 15,815 quads and 1,417 of 1,417
/// text records link to a live element.
///
/// Every record keeps its lines as read (<see cref="Records"/>), so <see cref="Engine_FacWriter"/>
/// writes a parsed file back byte-identical and formats only the records made in memory.
/// </summary>
public sealed class Engine_FacDocument
{
	private const int UvFieldCount = 15;
	private const int TextFieldCount = 11;
	private const int LightFieldCount = 5;

	public readonly List<FacUvRecord> Uv = new();
	public readonly List<FacTextRecord> Text = new();
	public readonly List<FacLightRecord> Lights = new();

	/// <summary>Every record in file order, the typed lists above being views of it. The writer emits this.</summary>
	public readonly List<FacRecord> Records = new();

	/// <summary>The file's line ending: CRLF in every shipped file.</summary>
	public string LineEnding = "\r\n";

	/// <summary>Whether the file's last line ends with a line ending (every shipped file does).</summary>
	public bool EndsWithNewline = true;

	public static Engine_FacDocument Load(string path) => Parse(File.ReadAllBytes(path));

	public static Engine_FacDocument Parse(byte[] data) =>
		Parse(Encoding.ASCII.GetString(data));

	public static Engine_FacDocument Parse(string text)
	{
		var doc = new Engine_FacDocument
		{
			LineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\r') ? "\r" : "\n",
		};

		string normalised = text.Replace("\r\n", "\n").Replace('\r', '\n');
		var lines = new List<string>(normalised.Split('\n'));
		// Split leaves one empty element after a terminating line ending; it is not a line.
		doc.EndsWithNewline = normalised.EndsWith('\n');
		if (doc.EndsWithNewline)
		{
			lines.RemoveAt(lines.Count - 1);
		}

		var record = new List<string>();
		foreach (var raw in lines)
		{
			if (raw.Length == 0)
			{
				doc.AddRecord(record, closed: true);
				record = new List<string>();
			}
			else
			{
				record.Add(raw);
			}
		}

		if (record.Count > 0)
		{
			doc.AddRecord(record, closed: false);
		}

		return doc;
	}

	/// <summary>Adds a record from its lines, decoding it where the keyword and field count say to; the record.</summary>
	public FacRecord AddRecord(List<string> f, bool closed = true)
	{
		var record = new FacRecord { Lines = f.ToArray(), Closed = closed };
		Records.Add(record);
		if (f.Count == 0)
		{
			return record;
		}

		switch (f[0])
		{
			case "UV" when f.Count >= UvFieldCount:
				record.Uv = new FacUvRecord
				{
					StableId = Int(f[1]),
					// fields 2..4 are written by the exporter and skipped by the engine
					Extra = new[] { f[2], f[3], f[4] },
					TextureHash = unchecked((uint)Int64(f[5])),
					Du = Float(f[6]),
					Dv = Float(f[7]),
					U0 = Float(f[8]),
					V0 = Float(f[9]),
					DetailMapMode = Int(f[10]),
					SecondaryTexture = Int(f[11]),
					Field12 = f[12],
					VertexLane3 = Float(f[13]),
					R = Rgb(f[14], 0),
					G = Rgb(f[14], 1),
					B = Rgb(f[14], 2),
				};
				Uv.Add(record.Uv);
				break;

			case "TEXT" when f.Count >= TextFieldCount:
				record.Text = new FacTextRecord
				{
					StableId = Int(f[1]),
					Text = PercentDecode(f[2]),
					WrapMode = Int(f[3]),
					Size = Int(f[4]),
					BoxWidth = Float(f[5]),
					BoxHeight = Float(f[6]),
					HorzAlign = Int(f[7]),
					VertAlign = Int(f[8]),
					Style = Int(f[9]),
					R = Rgb(f[10], 0),
					G = Rgb(f[10], 1),
					B = Rgb(f[10], 2),
				};
				Text.Add(record.Text);
				break;

			case "LIGHT" when f.Count >= LightFieldCount:
				record.Light = new FacLightRecord
				{
					StableId = Int(f[1]),
					R = Float(f[2]),
					G = Float(f[3]),
					B = Float(f[4]),
				};
				Lights.Add(record.Light);
				break;
		}

		return record;
	}

	/// <summary>Adds a record made in memory (no lines; the writer formats it) and files it in its typed list.</summary>
	public FacRecord Add(FacUvRecord uv)
	{
		var record = new FacRecord { Uv = uv };
		Records.Add(record);
		Uv.Add(uv);
		return record;
	}

	public FacRecord Add(FacTextRecord text)
	{
		var record = new FacRecord { Text = text };
		Records.Add(record);
		Text.Add(text);
		return record;
	}

	public FacRecord Add(FacLightRecord light)
	{
		var record = new FacRecord { Light = light };
		Records.Add(record);
		Lights.Add(light);
		return record;
	}

	/// <summary>Decodes the exporter's percent escapes, then interprets the bytes as UTF-8.</summary>
	public static string PercentDecode(string s)
	{
		if (s.IndexOf('%') < 0)
			return s;

		var bytes = new List<byte>(s.Length);
		for (int i = 0; i < s.Length; i++)
		{
			if (s[i] == '%' && i + 2 < s.Length
				&& Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
			{
				bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
				i += 2;
			}
			else
			{
				bytes.Add((byte)s[i]);
			}
		}
		return Encoding.UTF8.GetString(bytes.ToArray());
	}

	private static int Int(string s) =>
		int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

	private static long Int64(string s) =>
		long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

	private static float Float(string s) =>
		float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

	/// <summary>Colour fields are a single space-separated line; the fourth component is unused.</summary>
	private static float Rgb(string s, int index)
	{
		var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return index < parts.Length ? Float(parts[index]) : 0f;
	}
}
