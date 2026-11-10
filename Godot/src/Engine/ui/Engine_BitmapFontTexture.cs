using System;
using System.Collections.Generic;
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
	private static readonly object CacheLock = new();
	private static readonly Dictionary<string, (ImageTexture Texture, long Bytes)> CachedAtlases = new(StringComparer.Ordinal);

	/// <summary>Number of deduplicated font atlas + SDF texture pairs currently resident.</summary>
	public static int CachedFontCount
	{
		get
		{
			lock (CacheLock)
			{
				return CachedAtlases.Count;
			}
		}
	}

	/// <summary>Estimated bytes held by cached font atlas + SDF textures.</summary>
	public static long CachedFontBytes
	{
		get
		{
			lock (CacheLock)
			{
				long total = 0;
				foreach ((_, long bytes) in CachedAtlases.Values)
				{
					total += bytes;
				}
				return total;
			}
		}
	}
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

		return image.IsEmpty() ? null! : Engine_TextureUpload.CreateTexture(image);
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
	private static StringName? _supersampleMeta;

	/// <summary>Meta on the atlas texture: texture pixels per shipped atlas texel.</summary>
	public static StringName SupersampleMeta => _supersampleMeta ??= "supersample";

	/// <summary>Texture pixels per shipped atlas texel for an atlas built here; 1 otherwise.</summary>
	public static float SupersampleOf(Texture2D? atlas) =>
		atlas is not null && atlas.HasMeta(SupersampleMeta)
			? (float)atlas.GetMeta(SupersampleMeta).AsDouble()
			: 1f;

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, Image> PreparedFields = new();

	public static ImageTexture CreateAtlasTextureDecompressed(Engine_BitmapFont font) =>
		CreateAtlasTexture(font, CreateAtlasImage(font, out int factor), factor);

	/// <summary>
	/// The atlas as an image, before it becomes a texture: CPU work only, so a worker may build
	/// one font's while another builds the next (<see cref="Engine_UiFontLibrary"/>).
	/// </summary>
	public static Image CreateAtlasImage(Engine_BitmapFont font, out int factor)
	{
		int width = font.AtlasWidth;
		int height = font.AtlasHeight;
		factor = Mathf.Max(width, height) * 2 <= 2048 ? 2 : 1;

		using Image image = Image.CreateFromData(
			width, height,
			useMipmaps: false,
			Image.Format.RgtcR,
			DecodeAtlasBlocks(font));
		image.Decompress();
		if (image.GetFormat() != Image.Format.R8)
		{
			image.Convert(Image.Format.R8);
		}

		byte[] red = image.GetData();
		int pixelCount = width * height;
		var maskBytes = new byte[pixelCount * 4];
		for (int i = 0; i < pixelCount; i++)
		{
			int dst = i * 4;
			maskBytes[dst] = 255;
			maskBytes[dst + 1] = 255;
			maskBytes[dst + 2] = 255;
			maskBytes[dst + 3] = red[i];
		}

		Image mask = Image.CreateFromData(width, height, false, Image.Format.Rgba8, maskBytes);

		// SUPERSAMPLED, WITH MIPS. The shipped atlas is a 720p font; the UI's own art is
		// enhanced x4 and the text was the one thing still magnified raw — 2x at 1440p, 3x at
		// 2160p. A cubic upsample gives the coverage smooth contours for `ui_text.gdshader`
		// to re-sharpen at the display's own pixel size; the mips are what its
		// `filter_linear_mipmap` samples when a label draws smaller than authored.
		if (factor > 1)
		{
			mask.Resize(width * factor, height * factor, Image.Interpolation.Cubic);
		}

		// Precompute the signed distance field image here on the CPU worker before generating
		// mipmaps on `mask` so `CreateAtlasTexture` on the main thread only uploads GPU textures.
		if (BuildDistanceFieldImage(font, mask, factor) is { } sdfImage)
		{
			PreparedFields.AddOrUpdate(mask, sdfImage);
		}

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

		string cacheKey = FontDigestKey(font, factor);
		lock (CacheLock)
		{
			if (CachedAtlases.TryGetValue(cacheKey, out (ImageTexture Texture, long Bytes) existing) &&
				GodotObject.IsInstanceValid(existing.Texture))
			{
				return existing.Texture;
			}
		}

		Image? sdfImage = PreparedFields.TryGetValue(mask, out Image? prepared)
			? prepared
			: BuildDistanceFieldImage(font, mask, factor);
		ImageTexture? field = sdfImage is not null ? Engine_TextureUpload.CreateTexture(sdfImage) : null;
		ImageTexture texture = Engine_TextureUpload.CreateTexture(mask);
		texture.SetMeta(SupersampleMeta, factor);
		if (field is not null)
		{
			texture.SetMeta(DistanceFieldMeta, field);
		}

		long atlasBytes = mask.GetDataSize() + (sdfImage?.GetDataSize() ?? 0);
		lock (CacheLock)
		{
			CachedAtlases[cacheKey] = (texture, atlasBytes);
		}

		return texture;
	}

	private static StringName? _distanceFieldMeta;

	/// <summary>
	/// Meta on a coverage atlas: its signed distance field (<see cref="Engine_SdfField"/>), the
	/// texture `ui_text.gdshader` draws from. The coverage stays the atlas for plain 2D drawing.
	/// </summary>
	public static StringName DistanceFieldMeta => _distanceFieldMeta ??= "distance_field";

	/// <summary>The distance field paired with a coverage atlas, or null.</summary>
	public static Texture2D? DistanceFieldOf(Texture2D? atlas) =>
		atlas is not null && atlas.HasMeta(DistanceFieldMeta)
			? atlas.GetMeta(DistanceFieldMeta).As<Texture2D>()
			: null;

	/// <summary>Bump when the field's encoding changes, so cached fields are rebuilt.</summary>
	private const int DistanceFieldVersion = 1;

	/// <summary>
	/// The supersampled coverage's distance field as an RGBA8 image (signed distance in all channels)
	/// with mipmaps. Built on the player's machine from their own font and cached in the title's
	/// cache folder (derived game data never ships), keyed by the atlas payload.
	/// </summary>
	private static Image? BuildDistanceFieldImage(Engine_BitmapFont font, Image mask, int factor)
	{
		int width = mask.GetWidth();
		int height = mask.GetHeight();
		string path = DistanceFieldCachePath(font, factor);
		byte[]? field = null;
		try
		{
			if (System.IO.File.Exists(path))
			{
				using Image cached = Image.LoadFromFile(path);
				if (cached.GetWidth() == width && cached.GetHeight() == height)
				{
					if (cached.GetFormat() != Image.Format.L8 && cached.GetFormat() != Image.Format.R8)
					{
						cached.Convert(Image.Format.R8);
					}
					field = cached.GetData();
				}
			}
		}
		catch (Exception)
		{
			field = null;
		}

		if (field is null || field.Length != width * height)
		{
			byte[] rgba = mask.GetData();
			var coverage = new float[width * height];
			for (int i = 0; i < coverage.Length && (i * 4) + 3 < rgba.Length; ++i)
			{
				coverage[i] = rgba[(i * 4) + 3] / 255f;
			}

			field = Engine_SdfField.FromCoverage(coverage, width, height,
				spread: 2f * factor, edgeWidth: factor);
			try
			{
				using Image toSave = Image.CreateFromData(width, height, false, Image.Format.L8, field);
				System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
				toSave.SavePng(path);
			}
			catch (Exception exception)
			{
				GD.PushWarning($"[ui-font] distance field not cached at {path}: {exception.Message}");
			}
		}

		int count = width * height;
		var rgbaBytes = new byte[count * 4];
		for (int i = 0; i < count; ++i)
		{
			byte d = field[i];
			int dst = i * 4;
			rgbaBytes[dst] = d;
			rgbaBytes[dst + 1] = d;
			rgbaBytes[dst + 2] = d;
			rgbaBytes[dst + 3] = d;
		}

		Image sdf = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgbaBytes);
		sdf.GenerateMipmaps();
		return sdf;
	}

	private static string FontDigestKey(Engine_BitmapFont font, int factor)
	{
		byte[] digest = Engine_Hash.Sha1(font.AtlasBlocks.Span);
		string key = Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
		return $"sdf-v{DistanceFieldVersion}-{key}-x{factor}";
	}

	private static string DistanceFieldCachePath(Engine_BitmapFont font, int factor)
	{
		return System.IO.Path.Combine(Engine_Title.Active.CacheDirectory, "fonts",
			$"{FontDigestKey(font, factor)}.png");
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
