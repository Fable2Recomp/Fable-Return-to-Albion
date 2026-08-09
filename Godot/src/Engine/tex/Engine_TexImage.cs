using System;
using System.Collections.Generic;
using Godot;

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

        var seedData = new byte[smallestBytes];
        Buffer.BlockCopy(surface.Data, offset, seedData, 0, smallestBytes);

        Image? seed;
        try
        {
            seed = Image.CreateFromData(width, height, false, format, seedData);
        }
        catch (Exception)
        {
            return false;
        }

        if (seed is null || seed.GetWidth() != width || seed.Decompress() != Error.Ok)
        {
            return false;
        }

        if (seed.GetFormat() != Image.Format.Rgba8)
        {
            seed.Convert(Image.Format.Rgba8);
        }

        var parts = new List<byte[]> { surface.Data };

        int w = Math.Max(1, width / 2);
        int h = Math.Max(1, height / 2);

        Image current = seed;

        while (true)
        {
            var tail = (Image)current.Duplicate();
            tail.Resize(w, h, Image.Interpolation.Lanczos);
            if (tail.GetFormat() != Image.Format.Rgba8)
            {
                tail.Convert(Image.Format.Rgba8);
            }

            current = tail;

            byte[]? blocks = Engine_BcEncoder.Encode(
                tail.GetData(), w, h, surface.Format);

            if (blocks is null || blocks.Length != BlockBytesFor(w, h, surface.Format))
            {
                return false;
            }

            parts.Add(blocks);
            if (w == 1 && h == 1)
            {
                break;
            }

            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
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

        Span<byte> palette = stackalloc byte[16];

        for (int by = 0; by < blocksH; ++by)
        {
            for (int bx = 0; bx < blocksW; ++bx)
            {
                int b = offset + (((by * blocksW) + bx) * 8);
                ushort c0 = (ushort)(blocks[b] | (blocks[b + 1] << 8));
                ushort c1 = (ushort)(blocks[b + 2] | (blocks[b + 3] << 8));
                uint bits = blocks[b + 4] | ((uint)blocks[b + 5] << 8) |
                            ((uint)blocks[b + 6] << 16) | ((uint)blocks[b + 7] << 24);

                Expand565(c0, palette, 0);
                Expand565(c1, palette, 4);

                if (c0 > c1)
                {
                    for (int k = 0; k < 3; ++k)
                    {
                        palette[8 + k] = (byte)(((2 * palette[k]) + palette[4 + k] + 1) / 3);
                        palette[12 + k] = (byte)((palette[k] + (2 * palette[4 + k]) + 1) / 3);
                    }

                    palette[11] = 255;
                    palette[15] = 255;
                }
                else
                {
                    for (int k = 0; k < 3; ++k)
                    {
                        palette[8 + k] = (byte)((palette[k] + palette[4 + k]) / 2);
                        palette[12 + k] = 0;
                    }

                    palette[11] = 255;
                    palette[15] = 0;
                }

                for (int t = 0; t < 16; ++t)
                {
                    int px = (bx * 4) + (t & 3);
                    int py = (by * 4) + (t >> 2);
                    if (px >= width || py >= height)
                    {
                        continue;
                    }

                    int index = (int)((bits >> (t * 2)) & 0x3u) * 4;
                    int at = ((py * width) + px) * 4;
                    pixels[at] = palette[index];
                    pixels[at + 1] = palette[index + 1];
                    pixels[at + 2] = palette[index + 2];
                    pixels[at + 3] = palette[index + 3];
                }
            }
        }

        return pixels;
    }

    private static void Expand565(ushort colour, Span<byte> palette, int at)
    {
        int r = (colour >> 11) & 0x1F;
        int g = (colour >> 5) & 0x3F;
        int b = colour & 0x1F;
        palette[at] = (byte)((r << 3) | (r >> 2));
        palette[at + 1] = (byte)((g << 2) | (g >> 4));
        palette[at + 2] = (byte)((b << 3) | (b >> 2));
        palette[at + 3] = 255;
    }
}
