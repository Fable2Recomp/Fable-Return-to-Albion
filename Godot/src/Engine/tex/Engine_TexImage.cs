using System;
using System.Collections.Generic;
using Godot;

using Refii.Core;

using Refii.Ui;

namespace Refii.Textures;

public static class Engine_TexImage
{
	public static Image.Format ToGodotFormat(TexSurfaceFormat format) => format switch
	{
		TexSurfaceFormat.Bc1 => Image.Format.Dxt1,
		TexSurfaceFormat.Bc3 => Image.Format.Dxt5,
		TexSurfaceFormat.Bc4 => Image.Format.RgtcR,
		TexSurfaceFormat.Bc5 => Image.Format.RgtcRg,
		TexSurfaceFormat.Rgba8 => Image.Format.Rgba8,
		_ => Image.Format.Dxt1,
	};

	public static int FullChainLength(int width, int height)
	{
		int levels = 1;
		int w = width;
		int h = height;
		while (w > 1 || h > 1)
		{
			w = Math.Max(1, w / 2);
			h = Math.Max(1, h / 2);
			++levels;
		}

		return levels;
	}

	public static Image? TryCreateImage(byte[] texBytes, out string error) =>
		TryCreateImage(texBytes, out _, out error);

	public static Image? TryCreateImage(byte[] texBytes, out bool hasAlpha, out string error)
	{
		hasAlpha = false;

		if (!Engine_TexData.TryDecode(texBytes, out TexSurface surface, out error))
		{
			return null;
		}

		if (surface.Format == TexSurfaceFormat.Unsupported)
		{
			error = "unsupported surface format";
			return null;
		}

		hasAlpha = surface.Format is TexSurfaceFormat.Bc3 or TexSurfaceFormat.Rgba8;

		if (surface.Format == TexSurfaceFormat.Bc1 &&
			Bc1HasPunchThrough(surface.Data))
		{
			Image? punched = TryCreateBc1PunchThroughImage(surface);
			if (punched is not null)
			{
				hasAlpha = true;
				return punched;
			}
		}

		Image.Format format = ToGodotFormat(surface.Format);

		bool suppressMips = OS.GetEnvironment("REFII_TEX_NO_MIPS") == "1";

		int fullChain = FullChainLength(surface.Width, surface.Height);

		if (fullChain > 1 && !suppressMips)
		{
			byte[] chain = surface.Data;

			if (surface.MipLevels < fullChain && !TryExtendChain(surface, format, ref chain))
			{
				chain = surface.Data;
			}

			if (chain.Length != surface.Data.Length || surface.MipLevels == fullChain)
			{
				Image mipped = Image.CreateFromData(
					surface.Width, surface.Height, true, format, chain);
				if (IsUsable(mipped, surface))
				{
					return mipped;
				}
			}
		}

		int topLevel = TopLevelBytes(surface);
		byte[] top = surface.Data;
		if (topLevel > 0 && top.Length > topLevel)
		{
			top = new byte[topLevel];
			Buffer.BlockCopy(surface.Data, 0, top, 0, topLevel);
		}

		Image image = Image.CreateFromData(
			surface.Width, surface.Height, false, format, top);

		if (!IsUsable(image, surface))
		{
			error = $"Image.CreateFromData rejected {surface.Width}x{surface.Height} " +
					$"{surface.Format} ({top.Length} bytes)";
			return null;
		}

		return image;
	}

	private static bool TryExtendChain(
		TexSurface surface, Image.Format format, ref byte[] chain)
	{
		if (surface.Format == TexSurfaceFormat.Rgba8)
		{
			return false;
		}

		if (OS.GetEnvironment("REFII_TEX_NO_MIP_TAIL") == "1")
		{
			return false;
		}

		int width = surface.Width;
		int height = surface.Height;
		int offset = 0;
		for (int level = 0; level < surface.MipLevels - 1; ++level)
		{
			offset += BlockBytesFor(width, height, surface.Format);
			width = Math.Max(1, width / 2);
			height = Math.Max(1, height / 2);
		}

		int smallestBytes = BlockBytesFor(width, height, surface.Format);
		if (offset + smallestBytes != surface.Data.Length)
		{
			return false;
		}

		var seedBlocks = new byte[smallestBytes];
		Buffer.BlockCopy(surface.Data, offset, seedBlocks, 0, smallestBytes);

		byte[]? colour = null;
		byte[]? gray0 = null;
		byte[]? gray1 = null;
		switch (surface.Format)
		{
			case TexSurfaceFormat.Bc1:
				colour = Engine_TexMipGen.DecodeBc1(seedBlocks, width, height);
				break;
			case TexSurfaceFormat.Bc4:
				gray0 = Engine_TexMipGen.DecodeBc4(seedBlocks, width, height);
				break;
			case TexSurfaceFormat.Bc5:
				gray0 = Engine_TexMipGen.DecodeBc4(HalfBlocks(seedBlocks, 0), width, height);
				gray1 = Engine_TexMipGen.DecodeBc4(HalfBlocks(seedBlocks, 8), width, height);
				break;
			case TexSurfaceFormat.Bc3:
				gray0 = Engine_TexMipGen.DecodeBc4(HalfBlocks(seedBlocks, 0), width, height);
				colour = Engine_TexMipGen.DecodeBc1(HalfBlocks(seedBlocks, 8), width, height);
				break;
			default:
				return false;
		}

		var parts = new List<byte[]> { surface.Data };
		int w = width;
		int h = height;
		while (w > 1 || h > 1)
		{
			int nw = Math.Max(1, w / 2);
			int nh = Math.Max(1, h / 2);
			if (colour is not null) colour = Engine_TexMipGen.Downscale(colour, w, h, 4);
			if (gray0 is not null) gray0 = Engine_TexMipGen.Downscale(gray0, w, h, 1);
			if (gray1 is not null) gray1 = Engine_TexMipGen.Downscale(gray1, w, h, 1);
			w = nw;
			h = nh;

			byte[] level = surface.Format switch
			{
				TexSurfaceFormat.Bc1 => Engine_TexMipGen.EncodeBc1(colour!, w, h),
				TexSurfaceFormat.Bc4 => Engine_TexMipGen.EncodeBc4(gray0!, w, h),
				TexSurfaceFormat.Bc5 => Interleave(
					Engine_TexMipGen.EncodeBc4(gray0!, w, h),
					Engine_TexMipGen.EncodeBc4(gray1!, w, h)),
				_ => Interleave(
					Engine_TexMipGen.EncodeBc4(gray0!, w, h),
					Engine_TexMipGen.EncodeBc1(colour!, w, h)),
			};

			if (level.Length != BlockBytesFor(w, h, surface.Format))
			{
				return false;
			}

			parts.Add(level);
		}

		int total = 0;
		foreach (byte[] part in parts)
		{
			total += part.Length;
		}

		var result = new byte[total];
		int cursor = 0;
		foreach (byte[] part in parts)
		{
			Buffer.BlockCopy(part, 0, result, cursor, part.Length);
			cursor += part.Length;
		}

		chain = result;
		return true;
	}

	private static byte[] HalfBlocks(byte[] blocks16, int at)
	{
		int count = blocks16.Length / 16;
		var half = new byte[count * 8];
		for (int i = 0; i < count; ++i)
		{
			Buffer.BlockCopy(blocks16, (i * 16) + at, half, i * 8, 8);
		}

		return half;
	}

	private static byte[] Interleave(byte[] first, byte[] second)
	{
		int count = Math.Min(first.Length, second.Length) / 8;
		var blocks = new byte[count * 16];
		for (int i = 0; i < count; ++i)
		{
			Buffer.BlockCopy(first, i * 8, blocks, i * 16, 8);
			Buffer.BlockCopy(second, i * 8, blocks, (i * 16) + 8, 8);
		}

		return blocks;
	}

	private static int BlockBytesFor(int width, int height, TexSurfaceFormat format)
	{
		int blocksW = Math.Max(1, (width + 3) / 4);
		int blocksH = Math.Max(1, (height + 3) / 4);
		int blockBytes = format is TexSurfaceFormat.Bc3 or TexSurfaceFormat.Bc5 ? 16 : 8;
		return blocksW * blocksH * blockBytes;
	}

	private static bool IsUsable(Image? image, TexSurface surface) =>
		image is not null &&
		image.GetWidth() == surface.Width &&
		image.GetHeight() == surface.Height;

	private static int TopLevelBytes(TexSurface surface)
	{
		if (surface.Format == TexSurfaceFormat.Rgba8)
		{
			return surface.Width * surface.Height * 4;
		}

		return BlockBytesFor(surface.Width, surface.Height, surface.Format);
	}

	/// <summary>
	/// Builds a Godot <see cref="Cubemap"/> from a cube <c>.tex</c>, or null.
	/// </summary>
	/// <remarks>
	/// FACE ORDER IS FILE ORDER. The guest samples these with <c>tfetchCube</c>, which takes
	/// D3D's order (+X, -X, +Y, -Y, +Z, -Z), and Godot's Cubemap takes the same six in the same
	/// order — so the faces are handed over as they were stored. Nothing in the file asks for a
	/// permutation, and reordering on a hunch is how a reflection ends up mirrored in a way
	/// nobody can point at afterwards.
	/// </remarks>
	public static Cubemap? TryCreateCubemap(byte[] texBytes, out string error) =>
		TryCreateCubemap(texBytes, out _, out error);

	/// <summary>As above, also handing back the face images so they can be cached.</summary>
	public static Cubemap? TryCreateCubemap(
		byte[] texBytes, out Image[] images, out string error)
	{
		images = Array.Empty<Image>();

		if (!TexHeader.TryParse(texBytes, out TexHeader header, out error))
		{
			return null;
		}

		if (!Engine_TexCube.TryDecode(texBytes, header, out TexSurface[] faces, out error))
		{
			return null;
		}

		Image.Format format = ToGodotFormat(faces[0].Format);
		var decoded = new Image[faces.Length];
		for (int face = 0; face < faces.Length; ++face)
		{
			decoded[face] = Image.CreateFromData(
				faces[face].Width, faces[face].Height, faces[face].HasMipmaps, format,
				faces[face].Data);
		}

		Cubemap? cubemap = CubemapFromImages(decoded, out error);
		if (cubemap is not null)
		{
			images = decoded;
		}

		return cubemap;
	}

	/// <summary>
	/// Wraps six face images as a <see cref="Cubemap"/>.
	/// </summary>
	/// <remarks>
	/// <c>CreateFromImages</c> validates that the faces are square, equal-sized and the same
	/// format, which is worth more than it looks: it is the check that catches a face table read
	/// in the wrong order or a chain that decoded short, and it does it before anything reaches a
	/// shader.
	/// </remarks>
	public static Cubemap? CubemapFromImages(IReadOnlyList<Image> faces, out string error)
	{
		error = string.Empty;

		if (faces.Count != Engine_TexCube.FaceCount)
		{
			error = $"a cube needs {Engine_TexCube.FaceCount} faces, got {faces.Count}";
			return null;
		}

		var images = new Godot.Collections.Array<Image>();
		foreach (Image face in faces)
		{
			images.Add(face);
		}

		var cubemap = new Cubemap();
		Error created = cubemap.CreateFromImages(images);
		if (created != Error.Ok)
		{
			error = $"Cubemap.CreateFromImages: {created}";
			return null;
		}

		return cubemap;
	}

	public static ImageTexture? TryCreateTexture(byte[] texBytes, out string error) =>
		TryCreateTexture(texBytes, out _, out error);

	public static ImageTexture? TryCreateTexture(
		byte[] texBytes, out bool hasAlpha, out string error)
	{
		Image? image = TryCreateImage(texBytes, out hasAlpha, out error);
		return image is null ? null : ImageTexture.CreateFromImage(image);
	}

	public static bool Bc1HasPunchThrough(byte[] blocks)
	{
		for (int b = 0; b + 8 <= blocks.Length; b += 8)
		{
			ushort c0 = (ushort)(blocks[b] | (blocks[b + 1] << 8));
			ushort c1 = (ushort)(blocks[b + 2] | (blocks[b + 3] << 8));
			if (c0 > c1)
			{
				continue;
			}

			uint bits = blocks[b + 4] | ((uint)blocks[b + 5] << 8) |
						((uint)blocks[b + 6] << 16) | ((uint)blocks[b + 7] << 24);
			for (int t = 0; t < 16; ++t)
			{
				if (((bits >> (t * 2)) & 0x3u) == 3u)
				{
					return true;
				}
			}
		}

		return false;
	}

	private static Image? TryCreateBc1PunchThroughImage(TexSurface surface)
	{
		int fullChain = FullChainLength(surface.Width, surface.Height);
		bool shipsFullChain = surface.MipLevels >= fullChain;
		bool suppressMips = OS.GetEnvironment("REFII_TEX_NO_MIPS") == "1";

		try
		{
			if (shipsFullChain && !suppressMips)
			{
				var chain = new List<byte[]>();
				int w = surface.Width;
				int h = surface.Height;
				int offset = 0;
				for (int level = 0; level < fullChain; ++level)
				{
					int blockBytes = BlockBytesFor(w, h, TexSurfaceFormat.Bc1);
					if (offset + blockBytes > surface.Data.Length)
					{
						return null;
					}

					chain.Add(DecodeBc1Rgba(surface.Data, offset, w, h));
					offset += blockBytes;
					w = Math.Max(1, w / 2);
					h = Math.Max(1, h / 2);
				}

				int total = 0;
				foreach (byte[] part in chain)
				{
					total += part.Length;
				}

				var data = new byte[total];
				int cursor = 0;
				foreach (byte[] part in chain)
				{
					Buffer.BlockCopy(part, 0, data, cursor, part.Length);
					cursor += part.Length;
				}

				Image full = Image.CreateFromData(
					surface.Width, surface.Height, true, Image.Format.Rgba8, data);
				if (IsUsable(full, surface))
				{
					return full;
				}
			}

			byte[] top = DecodeBc1Rgba(surface.Data, 0, surface.Width, surface.Height);
			Image image = Image.CreateFromData(
				surface.Width, surface.Height, false, Image.Format.Rgba8, top);
			if (!IsUsable(image, surface))
			{
				return null;
			}

			if (!suppressMips)
			{
				image.GenerateMipmaps();
			}

			return image;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static byte[] DecodeBc1Rgba(byte[] blocks, int offset, int width, int height)
	{
		var pixels = new byte[width * height * 4];
		int blocksW = Math.Max(1, (width + 3) / 4);
		int blocksH = Math.Max(1, (height + 3) / 4);

		Span<uint> texels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
		Span<uint> palette = stackalloc uint[4];

		for (int by = 0; by < blocksH; ++by)
		{
			for (int bx = 0; bx < blocksW; ++bx)
			{
				int b = offset + (((by * blocksW) + bx) * 8);
				ushort c0 = (ushort)(blocks[b] | (blocks[b + 1] << 8));
				ushort c1 = (ushort)(blocks[b + 2] | (blocks[b + 3] << 8));
				uint bits = blocks[b + 4] | ((uint)blocks[b + 5] << 8) |
							((uint)blocks[b + 6] << 16) | ((uint)blocks[b + 7] << 24);

				Expand565(c0, out int r0, out int g0, out int b0);
				Expand565(c1, out int r1, out int g1, out int b1);
				palette[0] = Pack(r0, g0, b0, 255);
				palette[1] = Pack(r1, g1, b1, 255);

				if (c0 > c1)
				{
					palette[2] = Pack(((2 * r0) + r1 + 1) / 3, ((2 * g0) + g1 + 1) / 3, ((2 * b0) + b1 + 1) / 3, 255);
					palette[3] = Pack((r0 + (2 * r1) + 1) / 3, (g0 + (2 * g1) + 1) / 3, (b0 + (2 * b1) + 1) / 3, 255);
				}
				else
				{
					palette[2] = Pack((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2, 255);
					palette[3] = 0;
				}

				int x0 = bx * 4;
				int y0 = by * 4;
				if (x0 + 4 <= width && y0 + 4 <= height)
				{
					for (int row = 0; row < 4; ++row)
					{
						int at = ((y0 + row) * width) + x0;
						uint rowBits = bits >> (row * 8);
						texels[at] = palette[(int)(rowBits & 3u)];
						texels[at + 1] = palette[(int)((rowBits >> 2) & 3u)];
						texels[at + 2] = palette[(int)((rowBits >> 4) & 3u)];
						texels[at + 3] = palette[(int)((rowBits >> 6) & 3u)];
					}

					continue;
				}

				for (int t = 0; t < 16; ++t)
				{
					int px = x0 + (t & 3);
					int py = y0 + (t >> 2);
					if (px >= width || py >= height)
					{
						continue;
					}

					texels[(py * width) + px] = palette[(int)((bits >> (t * 2)) & 3u)];
				}
			}
		}

		return pixels;
	}

	private static uint Pack(int r, int g, int b, int a) =>
		(uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

	private static void Expand565(ushort colour, out int r, out int g, out int b)
	{
		int r5 = (colour >> 11) & 0x1F;
		int g6 = (colour >> 5) & 0x3F;
		int b5 = colour & 0x1F;
		r = (r5 << 3) | (r5 >> 2);
		g = (g6 << 2) | (g6 >> 4);
		b = (b5 << 3) | (b5 >> 2);
	}
}
