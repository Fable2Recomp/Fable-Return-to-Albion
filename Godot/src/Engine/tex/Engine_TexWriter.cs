using System;
using System.Buffers.Binary;
using System.IO;

namespace Refii.Textures;

/// <summary>
/// Writes a `.tex` a mod can ship, in the shape a shipped one has.
///
/// THE RAW FORM, TILED, because that is what the reader expects: `TryDecodeRawSurface` untiles
/// and word-swaps unconditionally, so a linear texture written "the obvious way" decodes with
/// every block somewhere else. Writing it the console's way also means the file is not special —
/// it is the same kind of file the game shipped, which is the only form we know the guest reads.
///
/// <code>
/// [0, 84)    header
/// [84, 88)   u32 payload byte count
/// [88, …)    block data: tiled, then 16-bit word-swapped
/// </code>
///
/// ONE LEVEL. The raw path decodes a single surface and ignores the level table, so a mip chain
/// here would be bytes nothing reads. Godot generates its own mips from the decoded surface.
/// </summary>
public static class Engine_TexWriter
{
	/// <summary>
	/// What the shipped 2D textures declare. Read off `screen_title.tex` and `fe_f2logo.tex`,
	/// which both report kind 0 — not 1, which is what this wrote before checking. Our reader
	/// only tests for 2 (a cube), so the wrong value worked; the guest has not been asked.
	/// </summary>
	private const int Texture2D = 0;

	/// <summary>Bit 1 of the flags word: the payload is surface data, not a plane chain.</summary>
	private const int RawFlag = 0x02;

	/// <summary>
	/// Encodes RGBA8 pixels into <paramref name="format"/> and writes the file.
	///
	/// <paramref name="rgba"/> is tightly packed, four bytes per pixel, top row first — what
	/// Godot's <c>Image.GetData()</c> hands back for <c>Format.Rgba8</c>.
	/// </summary>
	public static bool TryWrite(
		byte[] rgba,
		int width,
		int height,
		TexSurfaceFormat format,
		out byte[] bytes,
		out string error)
	{
		bytes = Array.Empty<byte>();
		error = string.Empty;

		if (width <= 0 || height <= 0)
		{
			error = $"a texture cannot be {width}x{height}";
			return false;
		}

		if ((long)width * height * 4 > rgba.LongLength)
		{
			error =
				$"{width}x{height} needs {(long)width * height * 4} bytes of RGBA, got {rgba.LongLength}";
			return false;
		}

		int pixelFormat = PixelFormatFor(format);
		if (pixelFormat == 0)
		{
			error = $"{format} is not a format this writes";
			return false;
		}

		byte[] linear;
		int unitsWide;
		int unitsHigh;
		int blockBytes;

		if (format == TexSurfaceFormat.Rgba8)
		{
			// The file stores A,R,G,B per texel; the reader swizzles to RGBA after untiling.
			linear = new byte[width * height * 4];
			for (int i = 0; i < width * height; i++)
			{
				linear[(i * 4) + 0] = rgba[(i * 4) + 3];
				linear[(i * 4) + 1] = rgba[(i * 4) + 0];
				linear[(i * 4) + 2] = rgba[(i * 4) + 1];
				linear[(i * 4) + 3] = rgba[(i * 4) + 2];
			}

			unitsWide = width;
			unitsHigh = height;
			blockBytes = 4;
		}
		else
		{
			byte[]? encoded = Engine_BcEncoder.Encode(rgba, width, height, format);
			if (encoded is null)
			{
				error = $"the {format} encoder refused a {width}x{height} image";
				return false;
			}

			linear = encoded;
			unitsWide = Math.Max(1, (width + 3) / 4);
			unitsHigh = Math.Max(1, (height + 3) / 4);
			blockBytes = format is TexSurfaceFormat.Bc3 or TexSurfaceFormat.Bc5 ? 16 : 8;
		}

		byte[] payload = Engine_Xbox360Tiling.Tile(linear, unitsWide, unitsHigh, blockBytes);

		// The reader swaps BEFORE it untiles, so writing swaps AFTER tiling — the same two steps
		// in the opposite order. Uncompressed texels are not swapped at all.
		if (blockBytes >= 8)
		{
			Engine_Xbox360Tiling.SwapWords16(payload);
		}

		var file = new MemoryStream(TexHeader.Size + 4 + payload.Length);
		var header = new byte[TexHeader.Size];

		void Field(int at, int value) =>
			BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(at, 4), value);

		// The version is stored negated, which is also what marks the file: the reader takes a
		// negative first word as "this is a header".
		Field(0x00, -1);
		Field(0x04, 4 + payload.Length);
		Field(0x08, Texture2D);
		Field(0x0C, RawFlag);
		Field(0x10, width);
		Field(0x14, height);
		Field(0x18, pixelFormat);
		Field(0x1C, 1);
		Field(0x20, TexHeader.Size);
		Field(0x50, 0);

		file.Write(header, 0, header.Length);

		Span<byte> count = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian(count, payload.Length);
		file.Write(count);
		file.Write(payload, 0, payload.Length);

		bytes = file.ToArray();
		return true;
	}

	private static int PixelFormatFor(TexSurfaceFormat format) => format switch
	{
		TexSurfaceFormat.Bc1 => (int)TexPixelFormat.Dxt1,
		TexSurfaceFormat.Bc3 => (int)TexPixelFormat.Dxt5,
		TexSurfaceFormat.Bc4 => (int)TexPixelFormat.Bc4,
		TexSurfaceFormat.Bc5 => (int)TexPixelFormat.Dxn,
		TexSurfaceFormat.Rgba8 => (int)TexPixelFormat.A8R8G8B8,
		_ => 0,
	};

	/// <summary>
	/// The format to write an image as: BC1 when it is fully opaque, BC3 when it is not.
	///
	/// Alpha is the only thing that decides it — BC1 carries one bit of it at best, and a mod's
	/// icon with soft edges written as BC1 gets hard, speckled ones.
	/// </summary>
	public static TexSurfaceFormat ChooseFormat(byte[] rgba, int width, int height)
	{
		int pixels = Math.Min(width * height, rgba.Length / 4);
		for (int i = 0; i < pixels; i++)
		{
			if (rgba[(i * 4) + 3] != 0xFF)
			{
				return TexSurfaceFormat.Bc3;
			}
		}

		return TexSurfaceFormat.Bc1;
	}
}
