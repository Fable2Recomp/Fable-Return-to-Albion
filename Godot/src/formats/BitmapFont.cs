using System;
using System.Buffers.Binary;
using System.IO;

namespace Refii.Launcher.Formats;

/// <summary>One glyph's atlas rectangle and horizontal metrics.</summary>
public readonly struct BitmapGlyph
{
	/// <summary>Atlas UVs. Note the engine stores these as pixel/(size-1), not pixel/size.</summary>
	public readonly float U0, V0, U1, V1;

	/// <summary>Pen offset applied before drawing.</summary>
	public readonly short LeftBearing;

	/// <summary>Stored as width-1; <see cref="DrawWidth"/> adds it back.</summary>
	public readonly short DrawWidthMinusOne;

	/// <summary>Pen advance applied after drawing.</summary>
	public readonly short PostAdvance;

	public int DrawWidth => DrawWidthMinusOne + 1;

	/// <summary>Total pen movement for this glyph. There is no kerning table.</summary>
	public int Advance => LeftBearing + PostAdvance;

	public BitmapGlyph(float u0, float v0, float u1, float v1, short bearing, short widthMinusOne, short post)
	{
		U0 = u0; V0 = v0; U1 = u1; V1 = v1;
		LeftBearing = bearing; DrawWidthMinusOne = widthMinusOne; PostAdvance = post;
	}
}

/// <summary>
/// Reader for Fable 2's <c>.bft</c> bitmap fonts
/// (<c>data/language/&lt;culture&gt;/fonts/gui_{small,normal,large}.bft</c>).
///
/// All scalars big-endian. Layout: a 25-byte header, a glyph table of 22-byte
/// records, a <see cref="CharMapSize"/>-entry u16 codepoint map, then an 88-byte
/// Fable <c>.tex</c> header and a DXT1 atlas payload.
///
/// Layout is entirely runtime — nothing is baked — so a renderer must reproduce
/// the pen arithmetic itself. Two off-by-one quirks in the original are worth
/// knowing, because matching them is what makes text line up:
/// the measure pass advances <c>LineHeight + 1</c> per line while the draw pass
/// advances <c>LineHeight</c>; and each glyph quad is drawn <c>LineHeight + 1</c>
/// tall regardless of the stored cell height.
/// </summary>
public sealed class BitmapFont
{
	/// <summary>The codepoint map is a fixed 8483 entries in every shipped font.</summary>
	public const int CharMapSize = 8483;

	/// <summary>Codepoints outside the map, or mapping past the glyph table, draw this glyph.</summary>
	public const int FallbackGlyph = 126;

	private const int HeaderSize = 25;
	private const int GlyphRecordSize = 22;
	private const int TexHeaderSize = 88;
	private const uint TexMagic = 0xFFFFFFFEu;

	private readonly BitmapGlyph[] _glyphs;
	private readonly ushort[] _charMap;

	/// <summary>Newline advance in pixels. The draw pass uses this; the measure pass uses this + 1.</summary>
	public readonly int LineHeight;

	public readonly int CellHeight;
	public readonly int AverageCharWidth;

	public readonly int AtlasWidth;
	public readonly int AtlasHeight;

	/// <summary>
	/// Raw block payload, <c>AtlasWidth * AtlasHeight / 2</c> bytes.
	///
	/// 4bpp single-channel BC4, Xbox 360 tiled and word-swapped — see
	/// <c>BitmapFontTexture.DecodeAtlasBlocks</c>, which is the only place that should
	/// interpret it. The size alone does not identify the format: BC1 is also 4bpp.
	/// </summary>
	public readonly ReadOnlyMemory<byte> AtlasBlocks;

	/// <summary>
	/// Family-constant header field — 400 in the GUI fonts, 100 in the debug fonts.
	/// No reader for it was found in the engine and it is not needed to render;
	/// most likely a GDI font weight left by the generator. Exposed for completeness.
	/// </summary>
	public readonly uint WeightHint;

	public int GlyphCount => _glyphs.Length;

	private BitmapFont(BitmapGlyph[] glyphs, ushort[] charMap, int lineHeight, int cellHeight,
		int averageCharWidth, uint weightHint, int atlasWidth, int atlasHeight, ReadOnlyMemory<byte> atlas)
	{
		_glyphs = glyphs;
		_charMap = charMap;
		LineHeight = lineHeight;
		CellHeight = cellHeight;
		AverageCharWidth = averageCharWidth;
		WeightHint = weightHint;
		AtlasWidth = atlasWidth;
		AtlasHeight = atlasHeight;
		AtlasBlocks = atlas;
	}

	public static BitmapFont Load(string path) => Parse(File.ReadAllBytes(path));

	public static BitmapFont Parse(byte[] d)
	{
		int p = 0;
		uint version = ReadU32(d, ref p);
		if (version != 1)
			throw new InvalidDataException($"bft: unsupported version {version}");

		int lineHeight = checked((int)ReadU32(d, ref p));
		uint weightHint = ReadU32(d, ref p);
		p += 1; // flag byte, always 0 in shipped fonts
		int cellHeight = checked((int)ReadU32(d, ref p));
		int averageCharWidth = checked((int)ReadU32(d, ref p));
		int glyphCount = checked((int)ReadU32(d, ref p));

		if (p != HeaderSize)
			throw new InvalidDataException($"bft: header consumed {p} bytes, expected {HeaderSize}");

		var glyphs = new BitmapGlyph[glyphCount];
		for (int i = 0; i < glyphCount; i++)
		{
			float u0 = ReadF32(d, ref p);
			float v0 = ReadF32(d, ref p);
			float u1 = ReadF32(d, ref p);
			float v1 = ReadF32(d, ref p);
			short bearing = ReadI16(d, ref p);
			short widthMinusOne = ReadI16(d, ref p);
			short post = ReadI16(d, ref p);
			glyphs[i] = new BitmapGlyph(u0, v0, u1, v1, bearing, widthMinusOne, post);
		}

		int charMapCount = checked((int)ReadU32(d, ref p));
		var charMap = new ushort[charMapCount];
		for (int i = 0; i < charMapCount; i++)
		{
			charMap[i] = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p, 2));
			p += 2;
		}

		uint texMagic = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p, 4));
		if (texMagic != TexMagic)
			throw new InvalidDataException($"bft: atlas magic {texMagic:X8}, expected {TexMagic:X8}");

		int atlasWidth = checked((int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 0x10, 4)));
		int atlasHeight = checked((int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 0x14, 4)));
		p += TexHeaderSize;

		// 4 bits per pixel.
		int expected = atlasWidth * atlasHeight / 2;
		int actual = d.Length - p;
		if (actual != expected)
			throw new InvalidDataException(
				$"bft: atlas payload {actual} bytes, expected {expected} for {atlasWidth}x{atlasHeight} at 4bpp");

		return new BitmapFont(glyphs, charMap, lineHeight, cellHeight, averageCharWidth,
			weightHint, atlasWidth, atlasHeight, new ReadOnlyMemory<byte>(d, p, actual));
	}

	/// <summary>
	/// Maps a codepoint to its glyph, falling back to <see cref="FallbackGlyph"/> exactly
	/// as the engine does — unmapped charmap slots hold filler that is out of range.
	/// </summary>
	public BitmapGlyph GetGlyph(int codepoint)
	{
		int index = FallbackGlyph;
		if (codepoint >= 0 && codepoint < _charMap.Length)
		{
			int mapped = _charMap[codepoint];
			if (mapped < _glyphs.Length)
				index = mapped;
		}
		return _glyphs[index];
	}

	public BitmapGlyph GetGlyphByIndex(int index) => _glyphs[index];

	/// <summary>True if the codepoint has a real glyph rather than the fallback.</summary>
	public bool HasGlyph(int codepoint) =>
		codepoint >= 0 && codepoint < _charMap.Length && _charMap[codepoint] < _glyphs.Length;

	/// <summary>
	/// Width of a single-line string in pixels, summing <see cref="BitmapGlyph.Advance"/>.
	/// Does not handle the inline markup tags or word wrap.
	/// </summary>
	public int MeasureWidth(ReadOnlySpan<char> text)
	{
		int w = 0;
		foreach (char c in text)
			w += GetGlyph(c).Advance;
		return w;
	}

	private static uint ReadU32(byte[] d, ref int p)
	{
		uint v = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p, 4));
		p += 4;
		return v;
	}

	private static float ReadF32(byte[] d, ref int p)
	{
		float v = BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(p, 4));
		p += 4;
		return v;
	}

	private static short ReadI16(byte[] d, ref int p)
	{
		short v = BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(p, 2));
		p += 2;
		return v;
	}
}
