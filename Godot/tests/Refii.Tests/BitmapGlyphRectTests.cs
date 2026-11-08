using System;
using System.Buffers.Binary;
using Godot;
using GdUnit4;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Where a Fable 2 glyph is read from the atlas: one texel per pixel of its quad.</summary>
[TestSuite]
public sealed class BitmapGlyphRectTests
{
	/// <summary>
	/// gui_normal's 'g': a 10-texel cell from x 345 on row 28 of a 512x256 atlas, stored as
	/// pixel / (size - 1) with U1 on its last texel. The raw UVs times the size start 0.68 texels in.
	/// </summary>
	[TestCase]
	public void GlyphRectStartsOnTheCellsFirstTexel()
	{
		Engine_BitmapFont font = Fable2Font(512, 256, lineHeight: 27,
			u0: 345f / 511, v0: 28f / 255, u1: 354f / 511, v1: 55f / 255, drawWidth: 10);
		BitmapGlyph g = font.GetGlyphByIndex(0);

		AssertThat(font.TexelUvs).IsTrue();
		AssertThat(Engine_BitmapFontTexture.GetGlyphRect(font, g)).IsEqual(new Rect2(345, 28, 10, 28));
		Rect2 uv = Engine_BitmapFontTexture.GetGlyphUvRect(font, g);
		AssertThat(uv.Position).IsEqualApprox(new Vector2(345f / 512, 28f / 256), new Vector2(1e-6f, 1e-6f));
		AssertThat(uv.End).IsEqualApprox(new Vector2(355f / 512, 56f / 256), new Vector2(1e-6f, 1e-6f));
	}

	/// <summary>A minimal .bft: header, one glyph, a one-entry char map, a Fable 2 .tex header, blank texels.</summary>
	private static Engine_BitmapFont Fable2Font(int width, int height, int lineHeight,
		float u0, float v0, float u1, float v1, int drawWidth)
	{
		var d = new byte[25 + 22 + 4 + 2 + 88 + width * height / 2];
		int p = 0;
		void U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(p, 4), v); p += 4; }
		void F32(float v) { BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(p, 4), v); p += 4; }
		void I16(short v) { BinaryPrimitives.WriteInt16BigEndian(d.AsSpan(p, 2), v); p += 2; }

		U32(1); U32((uint)lineHeight); U32(400); p += 1; U32((uint)lineHeight - 1); U32(10); U32(1);
		F32(u0); F32(v0); F32(u1); F32(v1); I16(1); I16((short)(drawWidth - 1)); I16((short)drawWidth);
		U32(1); I16(0);
		BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(p, 4), 0xFFFFFFFEu);
		BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(p + 0x10, 4), (uint)width);
		BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(p + 0x14, 4), (uint)height);
		return Engine_BitmapFont.Parse(d);
	}
}
