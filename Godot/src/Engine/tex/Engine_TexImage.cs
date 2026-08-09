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

	public static Image? TryCreateImage(byte[] texBytes, out string error)
	{
		if (!Engine_TexData.TryDecode(texBytes, out TexSurface surface, out error))
		{
			return null;
		}

		if (surface.Format == TexSurfaceFormat.Unsupported)
		{
			error = "unsupported surface format";
			return null;
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

    public static ImageTexture? TryCreateTexture(byte[] texBytes, out string error)
    {
        Image? image = TryCreateImage(texBytes, out error);
        return image is null ? null : ImageTexture.CreateFromImage(image);
    }
}
