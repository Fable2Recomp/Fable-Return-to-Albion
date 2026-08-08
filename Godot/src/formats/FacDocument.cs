using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Refii.Launcher.Formats;

/// <summary>
/// A textured quad. <see cref="StableId"/> matches a <see cref="BgfElement.ObjectId"/>.
/// </summary>
public sealed class FacUvRecord
{
	public int StableId;

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

	/// <summary>Point size. Selects the font bucket: &lt;18 small, ==18 normal, &gt;18 large.</summary>
	public int Size;

	/// <summary>Authored 0..1; multiply by 960 for the virtual UI space.</summary>
	public float BoxWidth;

	/// <summary>Authored 0..1; multiply by 720.</summary>
	public float BoxHeight;

	/// <summary>0 = left, 1 = centre, 2 = right.</summary>
	public int HorzAlign;

	/// <summary>0 / 1 / 2, with 1 = middle.</summary>
	public int VertAlign;

	/// <summary>Clamped to &lt;4, defaulting to 3. No reader for it was found in the engine.</summary>
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
/// </summary>
public sealed class FacDocument
{
	private const int UvFieldCount = 15;
	private const int TextFieldCount = 11;
	private const int LightFieldCount = 5;

	public readonly List<FacUvRecord> Uv = new();
	public readonly List<FacTextRecord> Text = new();
	public readonly List<FacLightRecord> Lights = new();

	public static FacDocument Load(string path) => Parse(File.ReadAllBytes(path));

	public static FacDocument Parse(byte[] data) =>
		Parse(Encoding.ASCII.GetString(data));

	public static FacDocument Parse(string text)
	{
		var doc = new FacDocument();

		var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
		var record = new List<string>();

		void Flush()
		{
			if (record.Count > 0)
			{
				doc.AddRecord(record);
				record.Clear();
			}
		}

		foreach (var raw in lines)
		{
			if (raw.Length == 0)
				Flush();
			else
				record.Add(raw);
		}
		Flush();

		return doc;
	}

	private void AddRecord(List<string> f)
	{
		switch (f[0])
		{
			case "UV" when f.Count >= UvFieldCount:
				Uv.Add(new FacUvRecord
				{
					StableId = Int(f[1]),
					// fields 2..4 are written by the exporter and skipped by the engine
					TextureHash = unchecked((uint)Int64(f[5])),
					Du = Float(f[6]),
					Dv = Float(f[7]),
					U0 = Float(f[8]),
					V0 = Float(f[9]),
					DetailMapMode = Int(f[10]),
					SecondaryTexture = Int(f[11]),
					VertexLane3 = Float(f[13]),
					R = Rgb(f[14], 0),
					G = Rgb(f[14], 1),
					B = Rgb(f[14], 2),
				});
				break;

			case "TEXT" when f.Count >= TextFieldCount:
				Text.Add(new FacTextRecord
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
				});
				break;

			case "LIGHT" when f.Count >= LightFieldCount:
				Lights.Add(new FacLightRecord
				{
					StableId = Int(f[1]),
					R = Float(f[2]),
					G = Float(f[3]),
					B = Float(f[4]),
				});
				break;
		}
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
