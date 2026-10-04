using System;
using Godot;
using Refii.Textures;

using Refii.Core;

using Refii.Text;

namespace Refii.Ui;

/// <summary>
/// Godot-side bridge for <see cref="Engine_BitmapFont"/>. The reader itself stays engine-free
/// so it can be unit tested; this is the only piece that needs <c>Godot</c>.
/// </summary>
public static class Engine_BitmapFontTexture
{
	/// <summary>
	/// The atlas as BC4 blocks a desktop GPU can read: byte-swapped, then untiled.
	///
	/// Three things have to be right together, and each one alone still produces
	/// something that renders:
	///
	/// <list type="bullet">
	/// <item><b>Format is BC4</b>, one channel of coverage — not BC1. Both are 4bpp with
	///   8-byte blocks, so the payload size cannot tell them apart, and BC4 read as BC1
	///   gives correct glyph SHAPES with nonsense colour. The embedded header's pixel
	///   format field is 100, which is none of the values we have identified elsewhere;
	///   BC4 was established by decoding the atlas both ways and looking at it.</item>
	/// <item><b>Each 16-bit word is byte-swapped</b> for the 360's big-endian bus. The
	///   32-bit convention transposes a block's two endpoints instead.</item>
	/// <item><b>The surface is TILED</b> — the header carries <c>flags = 2</c>, the raw
	///   path, so the payload is exactly what the GPU was handed. Untiled it is noise,
	///   with none of the structure that would suggest the layout is the problem.</item>
	/// </list>
	/// </summary>
	public static byte[] DecodeAtlasBlocks(Engine_BitmapFont font)
	{
		byte[] payload = font.AtlasBlocks.ToArray();
		Engine_Xbox360Tiling.SwapWords16(payload);

		return Engine_Xbox360Tiling.Untile(payload,
			widthBlocks: (font.AtlasWidth + 3) / 4,
			heightBlocks: (font.AtlasHeight + 3) / 4,
			blockBytes: 8);
	}

	/// <summary>Wraps a font's atlas as a compressed single-channel Godot texture.</summary>
	public static ImageTexture CreateAtlasTexture(Engine_BitmapFont font)
	{
		Image image = Image.CreateFromData(
			font.AtlasWidth, font.AtlasHeight,
			useMipmaps: false,
			Image.Format.RgtcR,
			DecodeAtlasBlocks(font));

		return ImageTexture.CreateFromImage(image);
	}

	/// <summary>
	/// The atlas as an RGBA coverage mask: white everywhere, with the decoded channel as
	/// ALPHA.
	///
	/// This is the form a renderer wants. The atlas carries coverage in one channel and
	/// nothing else, so a material has to turn that into opacity and take its colour
	/// from the text element — sampling it as red would draw every string red. Doing the
	/// conversion once here rather than in the shader keeps <c>ui_text.gdshader</c> to a
	/// single coverage read, and keeps the atlas usable by anything that samples it plainly.
	/// </summary>
	/// <summary>Meta on the atlas texture: texture pixels per shipped atlas texel.</summary>
	public static readonly StringName SupersampleMeta = "supersample";

	/// <summary>Texture pixels per shipped atlas texel for an atlas built here; 1 otherwise.</summary>
	public static float SupersampleOf(Texture2D? atlas) =>
		atlas is not null && atlas.HasMeta(SupersampleMeta)
			? (float)atlas.GetMeta(SupersampleMeta).AsDouble()
			: 1f;

	public static ImageTexture CreateAtlasTextureDecompressed(Engine_BitmapFont font)
	{
		Image image = Image.CreateFromData(
			font.AtlasWidth, font.AtlasHeight,
			useMipmaps: false,
			Image.Format.RgtcR,
			DecodeAtlasBlocks(font));
		image.Decompress();

		int width = image.GetWidth();
		int height = image.GetHeight();
		var mask = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);

		for (int y = 0; y < height; y++)
			for (int x = 0; x < width; x++)
				mask.SetPixel(x, y, new Color(1f, 1f, 1f, image.GetPixel(x, y).R));

		// SUPERSAMPLED, WITH MIPS. The shipped atlas is a 720p font; the UI's own art is
		// enhanced x4 and the text was the one thing still magnified raw — 2x at 1440p, 3x at
		// 2160p. A cubic upsample gives the coverage smooth contours for `ui_text.gdshader`
		// to re-sharpen at the display's own pixel size; the mips are what its
		// `filter_linear_mipmap` samples when a label draws smaller than authored.
		int factor = Mathf.Max(width, height) * 4 <= 4096 ? 4 : 2;
		mask.Resize(width * factor, height * factor, Image.Interpolation.Cubic);
		mask.GenerateMipmaps();

		ImageTexture texture = ImageTexture.CreateFromImage(mask);
		texture.SetMeta(SupersampleMeta, factor);
		return texture;
	}

	/// <summary>
	/// The atlas rectangle for a glyph, in pixels.
	/// </summary>
	/// <remarks>
	/// The stored UVs are <c>pixel / (size - 1)</c>, not <c>pixel / size</c>, so converting
	/// back to pixels multiplies by <c>size - 1</c>. Getting this wrong shifts every glyph
	/// by a fraction of a texel, which reads as soft or clipped edges rather than an
	/// obvious break.
	/// </remarks>
	public static Rect2 GetGlyphRect(Engine_BitmapFont font, in BitmapGlyph glyph)
	{
		float w = font.AtlasWidth - 1;
		float h = font.AtlasHeight - 1;
		float x0 = glyph.U0 * w;
		float y0 = glyph.V0 * h;
		return new Rect2(x0, y0, glyph.U1 * w - x0, glyph.V1 * h - y0);
	}

	/// <summary>
	/// Advances the pen across a single line, invoking <paramref name="drawGlyph"/> with each
	/// glyph's destination rectangle. Mirrors the engine's arithmetic, including the quirk
	/// that a quad is drawn <see cref="Engine_BitmapFont.LineHeight"/> + 1 tall regardless of the
	/// glyph's stored cell height.
	/// </summary>
	public static void LayoutLine(Engine_BitmapFont font, ReadOnlySpan<char> text, Vector2 origin,
		float scale, Action<BitmapGlyph, Rect2> drawGlyph)
	{
		float penX = origin.X;
		float quadHeight = (font.LineHeight + 1) * scale;

		foreach (char c in text)
		{
			BitmapGlyph g = font.GetGlyph(c);
			float left = penX + g.LeftBearing * scale;
			var dest = new Rect2(left, origin.Y, g.DrawWidth * scale, quadHeight);
			drawGlyph(g, dest);
			penX = left + g.PostAdvance * scale;
		}
	}

	private static void SwapUInt16InPlace(byte[] data)
	{
		for (int i = 0; i + 1 < data.Length; i += 2)
			(data[i], data[i + 1]) = (data[i + 1], data[i]);
	}
}
