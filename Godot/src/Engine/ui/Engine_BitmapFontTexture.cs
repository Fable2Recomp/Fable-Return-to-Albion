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

		return image.IsEmpty() ? null! : ImageTexture.CreateFromImage(image);
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

	public static ImageTexture CreateAtlasTextureDecompressed(Engine_BitmapFont font) =>
		CreateAtlasTexture(font, CreateAtlasImage(font, out int factor), factor);

	/// <summary>
	/// The atlas as an image, before it becomes a texture: CPU work only, so a worker may build
	/// one font's while another builds the next (<see cref="Engine_UiFontLibrary"/>).
	/// </summary>
	public static Image CreateAtlasImage(Engine_BitmapFont font, out int factor)
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
		factor = Mathf.Max(width, height) * 4 <= 4096 ? 4 : 2;
		mask.Resize(width * factor, height * factor, Image.Interpolation.Cubic);
		mask.GenerateMipmaps();
		return mask;
	}

	/// <summary>The texture for an atlas <see cref="CreateAtlasImage"/> built; null when it came out empty.</summary>
	/// <remarks>The distance field reads the atlas's full-size level only, so its mips being built already changes nothing.</remarks>
	public static ImageTexture CreateAtlasTexture(Engine_BitmapFont font, Image mask, int factor)
	{
		if (mask.IsEmpty())
		{
			return null!;
		}

		ImageTexture? field = DistanceField(font, mask, factor);
		ImageTexture texture = ImageTexture.CreateFromImage(mask);
		texture.SetMeta(SupersampleMeta, factor);
		if (field is not null)
		{
			texture.SetMeta(DistanceFieldMeta, field);
		}

		return texture;
	}

	/// <summary>
	/// Meta on a coverage atlas: its signed distance field (<see cref="Engine_SdfField"/>), the
	/// texture `ui_text.gdshader` draws from. The coverage stays the atlas for plain 2D drawing.
	/// </summary>
	public static readonly StringName DistanceFieldMeta = "distance_field";

	/// <summary>The distance field paired with a coverage atlas, or null.</summary>
	public static Texture2D? DistanceFieldOf(Texture2D? atlas) =>
		atlas is not null && atlas.HasMeta(DistanceFieldMeta)
			? atlas.GetMeta(DistanceFieldMeta).As<Texture2D>()
			: null;

	/// <summary>Bump when the field's encoding changes, so cached fields are rebuilt.</summary>
	private const int DistanceFieldVersion = 1;

	/// <summary>
	/// The supersampled coverage's distance field, L8 with mips. Built on the player's machine
	/// from their own font and cached in the title's cache folder (derived game data never ships),
	/// keyed by the atlas payload; a second start reads the cache. Spread is two shipped texels
	/// either side of the edge, which is what an outline or a soft shadow can reach.
	/// </summary>
	private static ImageTexture? DistanceField(Engine_BitmapFont font, Image mask, int factor)
	{
		int width = mask.GetWidth();
		int height = mask.GetHeight();
		string path = DistanceFieldCachePath(font, factor);
		Image? image = null;
		try
		{
			if (System.IO.File.Exists(path))
			{
				Image cached = Image.LoadFromFile(path);
				if (cached.GetWidth() == width && cached.GetHeight() == height)
				{
					cached.Convert(Image.Format.L8);
					image = cached;
				}
			}
		}
		catch (Exception)
		{
			image = null;
		}

		if (image is null)
		{
			byte[] rgba = mask.GetData();
			var coverage = new float[width * height];
			for (int i = 0; i < coverage.Length; ++i)
			{
				coverage[i] = rgba[(i * 4) + 3] / 255f;
			}

			byte[] field = Engine_SdfField.FromCoverage(coverage, width, height,
				spread: 2f * factor, edgeWidth: factor);
			image = Image.CreateFromData(width, height, false, Image.Format.L8, field);
			try
			{
				System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
				image.SavePng(path);
			}
			catch (Exception exception)
			{
				GD.PushWarning($"[ui-font] distance field not cached at {path}: {exception.Message}");
			}
		}

		if (image.IsEmpty())
		{
			return null;
		}

		image.GenerateMipmaps();
		return ImageTexture.CreateFromImage(image);
	}

	private static string DistanceFieldCachePath(Engine_BitmapFont font, int factor)
	{
		byte[] digest = Engine_Hash.Sha1(font.AtlasBlocks.Span);
		string key = Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
		return System.IO.Path.Combine(Engine_Title.Active.CacheDirectory, "fonts",
			$"sdf-v{DistanceFieldVersion}-{key}-x{factor}.png");
	}

	/// <summary>
	/// The atlas rectangle for a glyph, in shipped atlas texels: one texel per pixel of the
	/// quad the engine draws, <see cref="BitmapGlyph.DrawWidth"/> wide and
	/// <see cref="Engine_BitmapFont.LineHeight"/> + 1 tall.
	/// </summary>
	/// <remarks>
	/// The stored UVs are <c>pixel / (size - 1)</c>, not <c>pixel / size</c>, so converting
	/// back to pixels multiplies by <c>size - 1</c>, and U1/V1 name the cell's LAST texel, not
	/// the edge past it. In every shipped GUI font the cell's first column and row carry ink
	/// and its last column and row are blank. Sampling the raw UVs instead starts each glyph
	/// up to a whole texel late, so the letters on the right of the atlas lose their left
	/// stroke (n, h, b, u, p, E), and squeezes the rest into the quad. A Fable 3 font
	/// (<see cref="Engine_BitmapFont.TexelUvs"/> false) keeps its raw UVs.
	/// </remarks>
	public static Rect2 GetGlyphRect(Engine_BitmapFont font, in BitmapGlyph glyph)
	{
		if (!font.TexelUvs)
		{
			return new Rect2(glyph.U0 * font.AtlasWidth, glyph.V0 * font.AtlasHeight,
				(glyph.U1 - glyph.U0) * font.AtlasWidth, (glyph.V1 - glyph.V0) * font.AtlasHeight);
		}

		return new Rect2(Mathf.Round(glyph.U0 * (font.AtlasWidth - 1)), Mathf.Round(glyph.V0 * (font.AtlasHeight - 1)),
			glyph.DrawWidth, font.LineHeight + 1);
	}

	/// <summary><see cref="GetGlyphRect"/> as texture coordinates.</summary>
	public static Rect2 GetGlyphUvRect(Engine_BitmapFont font, in BitmapGlyph glyph)
	{
		Rect2 texels = GetGlyphRect(font, glyph);
		var size = new Vector2(font.AtlasWidth, font.AtlasHeight);
		return new Rect2(texels.Position / size, texels.Size / size);
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
