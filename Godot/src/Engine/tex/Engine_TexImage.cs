using System;
using System.Collections.Generic;
using System.Linq;
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

	/// <summary>
	/// Block-compresses an image IN PLACE with our own encoder, mip chain and all.
	/// </summary>
	/// <remarks>
	/// GODOT'S BC COMPRESSOR IS EDITOR-ONLY. In an exported build `_image_compress_bc_func` is
	/// null and <see cref="Image.Compress"/> fails, so every ORM and every enhanced texture was
	/// kept, cached and uploaded as RGBA8. Measured on the 2026-08-19 cache: 1,485 of 3,576
	/// entries were RGBA8 and held 2.90 GB of the 3.13 GB total — 42% of the entries for 93% of
	/// the bytes, ~2 MB each against ~70 KB for the ones that did compress.
	///
	/// <see cref="Engine_BcEncoder"/> was already in the tree for the mip-tail synthesis below;
	/// this is the same encoder pointed at the whole chain instead of the missing tail.
	///
	/// FORMAT BY CONTENT, the way Godot's own does it: a normal map goes to BC5, which keeps two
	/// channels at full precision rather than putting DXT's 5:6:5 banding into the smooth
	/// gradients a normal map is made of. Anything with alpha goes to BC3 — including BINARY
	/// alpha, which DXT1 can nominally carry but this encoder does not emit, so honouring it
	/// costs the larger block and losing it would punch holes in the image.
	///
	/// Runs on a worker, never the frame thread — the encoder is per-block C# and a 2048² chain
	/// is hundreds of milliseconds. Stateless, so concurrent callers do not interact.
	/// </remarks>
	/// <returns>False if nothing was changed, so the caller can report it.</returns>
	/// <param name="force">
	/// The format to encode to, overriding the choice by content. A TEXTURE ARRAY NEEDS ONE
	/// FORMAT FOR EVERY SLICE — `Texture2DArray.CreateFromImages` rejects a mixed set — and
	/// deciding per slice would hand it BC1 for the opaque layers and BC3 for the rest. The
	/// caller scans its whole set once and states the answer here.
	/// </param>
	public static bool CompressBlocks(
		Image image, bool isNormalMap, TexSurfaceFormat? force = null)
	{
		if (image.GetWidth() < 1 || image.GetHeight() < 1)
		{
			return false;
		}

		TexSurfaceFormat target = force ?? (isNormalMap
			? TexSurfaceFormat.Bc5
			: image.DetectAlpha() == Image.AlphaMode.None
				? TexSurfaceFormat.Bc1
				: TexSurfaceFormat.Bc3);

		// The encoder reads four bytes a texel and nothing else.
		if (image.GetFormat() != Image.Format.Rgba8)
		{
			image.Convert(Image.Format.Rgba8);
			if (image.GetFormat() != Image.Format.Rgba8)
			{
				return false;
			}
		}

		int width = image.GetWidth();
		int height = image.GetHeight();
		bool mipmaps = image.HasMipmaps();

		// COUNTED FROM THE DIMENSIONS, not from GetMipmapCount(): GenerateMipmaps always writes
		// the full chain down to 1x1, and FullChainLength is the same walk the rest of this file
		// sizes chains with, so the two cannot disagree about how many levels there are.
		int levels = mipmaps ? FullChainLength(width, height) : 1;

		// One pooled output, each level encoded straight from a span of the source; SetData
		// copies the span. The image's own buffer, not a managed copy: it stays as it is until
		// released, even once SetData has replaced the image's data.
		using var sourceBytes = new Engine_ImageBytes(image);
		ReadOnlySpan<byte> source = sourceBytes.Span;
		int total = 0;
		for (int level = 0; level < levels; ++level)
		{
			total += Engine_BcEncoder.EncodedBytes(
				Math.Max(1, width >> level), Math.Max(1, height >> level), target);
		}

		byte[] packed = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(total, 1));
		try
		{
			int offset = 0;
			int cursor = 0;
			for (int level = 0; level < levels; ++level)
			{
				int levelWidth = Math.Max(1, width >> level);
				int levelHeight = Math.Max(1, height >> level);
				int levelBytes = levelWidth * levelHeight * 4;

				if (offset + levelBytes > source.Length)
				{
					return false;
				}

				int blockBytes = Engine_BcEncoder.EncodedBytes(levelWidth, levelHeight, target);
				if (blockBytes != BlockBytesFor(levelWidth, levelHeight, target) ||
					!Engine_BcEncoder.EncodeInto(
						source.Slice(offset, levelBytes), levelWidth, levelHeight, target, packed, cursor))
				{
					return false;
				}

				offset += levelBytes;
				cursor += blockBytes;
			}

			image.SetData(width, height, mipmaps, ToGodotFormat(target), new ReadOnlySpan<byte>(packed, 0, total));
		}
		finally
		{
			System.Buffers.ArrayPool<byte>.Shared.Return(packed);
		}

		return image.GetFormat() == ToGodotFormat(target);
	}

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

		if (!Engine_TexData.TryDecodePooled(texBytes, out TexSurface surface, out error))
		{
			return null;
		}

		try
		{
			return CreateImage(surface, out hasAlpha, ref error);
		}
		finally
		{
			Engine_TexData.Release(surface);
		}
	}

	private static Image? CreateImage(TexSurface surface, out bool hasAlpha, ref string error)
	{
		hasAlpha = false;

		if (surface.Format == TexSurfaceFormat.Unsupported)
		{
			error = "unsupported surface format";
			return null;
		}

		hasAlpha = surface.Format is TexSurfaceFormat.Bc3 or TexSurfaceFormat.Rgba8;

		if (surface.Format == TexSurfaceFormat.Bc1 &&
			Bc1HasPunchThrough(surface.Bytes))
		{
			Image? punched = TryCreateBc1PunchThroughImage(surface);
			if (punched is not null)
			{
				// RE-COMPRESS BEFORE IT LEAVES. This is the ONE decode path that returns RGBA8,
				// and the base decode never compresses — only the ORM and enhanced paths call
				// the encoder. So a punch-through source stayed four bytes a texel for the whole
				// session, eight times what its block form costs, and a source that is also over
				// the enhancement cap or under its floor is never enhanced either, so nothing
				// downstream ever compressed it. Measured on the built cache: 182 such entries
				// held 275 MB, four of them 2048x1024 at ~11 MB each.
				//
				// The alpha is BINARY here by construction — that is what punch-through means —
				// so BC3's block carries it exactly.
				CompressBlocks(punched, false);
				hasAlpha = true;
				return punched;
			}
		}

		Image.Format format = ToGodotFormat(surface.Format);

		bool suppressMips = OS.GetEnvironment("REFII_TEX_NO_MIPS") == "1";

		int fullChain = FullChainLength(surface.Width, surface.Height);

		if (fullChain > 1 && !suppressMips)
		{
			// The extended chain (source plus synthesized tail) is pooled and handed over as a
			// span; CreateFromData copies it.
			byte[]? extended = null;
			int extendedLength = 0;
			try
			{
				if (surface.MipLevels < fullChain)
				{
					TryExtendChain(surface, out extended, out extendedLength);
				}

				if (extended is not null || surface.MipLevels == fullChain)
				{
					ReadOnlySpan<byte> chain = extended is not null
						? new ReadOnlySpan<byte>(extended, 0, extendedLength)
						: surface.Bytes;
					Image mipped = Image.CreateFromData(
						surface.Width, surface.Height, true, format, chain);
					if (IsUsable(mipped, surface))
					{
						return mipped;
					}
				}
			}
			finally
			{
				if (extended is not null)
				{
					System.Buffers.ArrayPool<byte>.Shared.Return(extended);
				}
			}
		}

		int topLevel = TopLevelBytes(surface);
		int topLength = topLevel > 0 && surface.Bytes.Length > topLevel ? topLevel : surface.Bytes.Length;

		Image image = Image.CreateFromData(
			surface.Width, surface.Height, false, format, surface.Bytes[..topLength]);

		if (!IsUsable(image, surface))
		{
			error = $"Image.CreateFromData rejected {surface.Width}x{surface.Height} " +
					$"{surface.Format} ({topLength} bytes)";
			return null;
		}

		return image;
	}

	/// <summary>The stored chain plus a synthesized tail, in a POOLED buffer the caller returns.</summary>
	private static bool TryExtendChain(
		TexSurface surface, out byte[]? chain, out int chainLength)
	{
		chain = null;
		chainLength = 0;
		if (surface.Format == TexSurfaceFormat.Rgba8)
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
		if (offset + smallestBytes != surface.Bytes.Length)
		{
			return false;
		}

		int stride = surface.Format is TexSurfaceFormat.Bc3 or TexSurfaceFormat.Bc5 ? 16 : 8;
		bool hasColour = surface.Format is TexSurfaceFormat.Bc1 or TexSurfaceFormat.Bc3;
		bool hasGray0 = surface.Format is TexSurfaceFormat.Bc4 or TexSurfaceFormat.Bc3 or TexSurfaceFormat.Bc5;
		bool hasGray1 = surface.Format == TexSurfaceFormat.Bc5;
		if (!hasColour && !hasGray0)
		{
			return false;
		}

		int total = surface.Bytes.Length;
		for (int w = width, h = height; w > 1 || h > 1;)
		{
			w = Math.Max(1, w / 2);
			h = Math.Max(1, h / 2);
			total += BlockBytesFor(w, h, surface.Format);
		}

		// THE TAIL IS BUILT IN PLACE: each pixel plane shrinks where it is, each level's blocks go
		// straight into the chain, and this one buffer is all that outlives the seed's planes.
		byte[] result = System.Buffers.ArrayPool<byte>.Shared.Rent(total);
		int colourBytes = hasColour ? width * height * 4 : 0;
		int grayBytes = hasGray0 ? width * height : 0;
		byte[] planes = System.Buffers.ArrayPool<byte>.Shared.Rent(colourBytes + (grayBytes * 2));
		try
		{
			ReadOnlySpan<byte> seed = surface.Bytes.Slice(offset, smallestBytes);
			Span<byte> colour = planes.AsSpan(0, colourBytes);
			Span<byte> gray0 = planes.AsSpan(colourBytes, grayBytes);
			Span<byte> gray1 = planes.AsSpan(colourBytes + grayBytes, grayBytes);
			if (hasColour)
			{
				Engine_TexMipGen.DecodeBc1Into(seed, stride, stride - 8, width, height, colour);
			}

			if (hasGray0)
			{
				Engine_TexMipGen.DecodeBc4Into(seed, stride, 0, width, height, gray0);
			}

			if (hasGray1)
			{
				Engine_TexMipGen.DecodeBc4Into(seed, stride, 8, width, height, gray1);
			}

			surface.Bytes.CopyTo(result);
			int cursor = surface.Bytes.Length;
			int w = width;
			int h = height;
			while (w > 1 || h > 1)
			{
				int nw = Math.Max(1, w / 2);
				int nh = Math.Max(1, h / 2);
				if (hasColour) Engine_TexMipGen.Downscale(colour, w, h, 4, colour);
				if (hasGray0) Engine_TexMipGen.Downscale(gray0, w, h, 1, gray0);
				if (hasGray1) Engine_TexMipGen.Downscale(gray1, w, h, 1, gray1);
				w = nw;
				h = nh;

				Span<byte> level = result.AsSpan(cursor, BlockBytesFor(w, h, surface.Format));
				if (hasGray0) Engine_TexMipGen.EncodeBc4Into(gray0, w, h, level, stride, 0);
				if (hasGray1) Engine_TexMipGen.EncodeBc4Into(gray1, w, h, level, stride, 8);
				if (hasColour) Engine_TexMipGen.EncodeBc1Into(colour, w, h, level, stride, stride - 8);
				cursor += level.Length;
			}
		}
		finally
		{
			System.Buffers.ArrayPool<byte>.Shared.Return(planes);
		}

		chain = result;
		chainLength = total;
		return true;
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
		!image.IsEmpty() &&
		surface.Width > 0 &&
		surface.Height > 0 &&
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
		using Godot.Collections.Array imageStorage = (Godot.Collections.Array)images;
		foreach (Image face in faces)
		{
			images.Add(face);
		}

		var cubemap = new Cubemap();
		Error created = Refii.Textures.Engine_TextureUpload.CreateLayered(cubemap, images);
		if (created != Error.Ok)
		{
			error = $"Cubemap.CreateFromImages: {created}";
			return null;
		}

		return cubemap;
	}

	public static ImageTexture? TryCreateTexture(byte[] texBytes, out string error) =>
		TryCreateTexture(texBytes, out _, out error);

	/// <summary>Uploads a decoded texture and releases the exclusively owned CPU image.</summary>
	public static ImageTexture? TryCreateTexture(
		byte[] texBytes, out bool hasAlpha, out string error)
	{
		Image? image = TryCreateImage(texBytes, out hasAlpha, out error);
		return image is null || image.IsEmpty() ? null : Engine_TextureUpload.CreateTexture(image);
	}

	public static bool Bc1HasPunchThrough(byte[] blocks) => Bc1HasPunchThrough(new ReadOnlySpan<byte>(blocks));

	public static bool Bc1HasPunchThrough(ReadOnlySpan<byte> blocks)
	{
		ReadOnlySpan<ulong> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(blocks);
		foreach (ulong block in words)
		{
			var c0 = (ushort)block;
			var c1 = (ushort)(block >> 16);
			if (c0 > c1)
			{
				continue;
			}

			// Any of the sixteen 2-bit indices that is 3.
			var bits = (uint)(block >> 32);
			if ((bits & (bits >> 1) & 0x55555555u) != 0)
			{
				return true;
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
				int total = 0;
				for (int level = 0, w = surface.Width, h = surface.Height; level < fullChain; ++level)
				{
					total += w * h * 4;
					w = Math.Max(1, w / 2);
					h = Math.Max(1, h / 2);
				}

				byte[] data = System.Buffers.ArrayPool<byte>.Shared.Rent(total);
				try
				{
					int w = surface.Width;
					int h = surface.Height;
					int offset = 0;
					int cursor = 0;
					for (int level = 0; level < fullChain; ++level)
					{
						int blockBytes = BlockBytesFor(w, h, TexSurfaceFormat.Bc1);
						if (offset + blockBytes > surface.Bytes.Length)
						{
							return null;
						}

						DecodeBc1Rgba(surface.Bytes, offset, w, h, data.AsSpan(cursor, w * h * 4));
						offset += blockBytes;
						cursor += w * h * 4;
						w = Math.Max(1, w / 2);
						h = Math.Max(1, h / 2);
					}

					Image full = Image.CreateFromData(
						surface.Width, surface.Height, true, Image.Format.Rgba8, new ReadOnlySpan<byte>(data, 0, total));
					if (IsUsable(full, surface))
					{
						return full;
					}
				}
				finally
				{
					System.Buffers.ArrayPool<byte>.Shared.Return(data);
				}
			}

			int topBytes = surface.Width * surface.Height * 4;
			byte[] top = System.Buffers.ArrayPool<byte>.Shared.Rent(topBytes);
			Image image;
			try
			{
				DecodeBc1Rgba(surface.Bytes, 0, surface.Width, surface.Height, top.AsSpan(0, topBytes));
				image = Image.CreateFromData(
					surface.Width, surface.Height, false, Image.Format.Rgba8, new ReadOnlySpan<byte>(top, 0, topBytes));
			}
			finally
			{
				System.Buffers.ArrayPool<byte>.Shared.Return(top);
			}

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

	private static void DecodeBc1Rgba(ReadOnlySpan<byte> blocks, int offset, int width, int height, Span<byte> pixels)
	{
		int blocksW = Math.Max(1, (width + 3) / 4);
		int blocksH = Math.Max(1, (height + 3) / 4);

		Span<uint> texels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels);
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
