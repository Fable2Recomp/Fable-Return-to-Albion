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

    /// <summary>Levels a complete chain holds, counting the base and ending at 1x1.</summary>
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

        // Forces the unmipped path for the WHOLE texture, shipped levels included. Paired
        // with REFII_TEX_NO_MIP_TAIL this separates "mipmapping at all" from "the levels we
        // synthesised", which no single switch can distinguish.
        bool suppressMips = OS.GetEnvironment("REFII_TEX_NO_MIPS") == "1";

        int fullChain = FullChainLength(surface.Width, surface.Height);

        // Note the gate is on the FULL chain length, not on whether the file shipped more
        // than one level. Fable 2 ships exactly one for 1,161 textures — including the
        // large stone and rock surfaces that dominate a scene — and gating on
        // surface.HasMipmaps left precisely those uploading unfiltered, which is the
        // opposite of what mipmapping is for. A single-level file simply has its whole
        // chain synthesised from level 0.
        if (fullChain > 1 && !suppressMips)
        {
            byte[] chain = surface.Data;

            // Fable 2 stops its chains well above 1x1 — 8,974 of 9,419 textures — while
            // Godot requires every level down to it. Extending here is what lets those
            // upload mipped at all; without it the game samples unfiltered at distance,
            // which reads as sparkle on anything far away.
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

    /// <summary>
    /// Extends a short mip chain down to 1x1 by downsampling the smallest level the file
    /// ships.
    ///
    /// Seeding from the SMALLEST shipped level rather than the base is the whole point.
    /// Measured over 400 real textures, this costs 1.34 ms each against a decode that
    /// already costs 9 ms — and 27x more if done the obvious way, by decompressing the
    /// full-size base to synthesise an 8x8. Here the largest image ever touched is the
    /// file's own last level, and the tail comes to about 99 bytes, 1.76% of the texture.
    ///
    /// Any inconsistency returns false and leaves the caller on the unmipped path, because
    /// a chain of the wrong length is worse than no chain: Godot rejects the surface
    /// outright rather than ignoring the surplus.
    /// </summary>
    private static bool TryExtendChain(
        TexSurface surface, Image.Format format, ref byte[] chain)
    {
        // Uncompressed surfaces do not go through the block-compressor path and have no
        // short-chain problem worth the special case.
        if (surface.Format == TexSurfaceFormat.Rgba8)
        {
            return false;
        }

        // Kill switch, so "is this artefact the synthesised tail?" stays a one-run A/B
        // instead of a rebuild. Filtering faults and decode faults look alike at distance.
        if (OS.GetEnvironment("REFII_TEX_NO_MIP_TAIL") == "1")
        {
            return false;
        }

        // Walk down to the smallest shipped level, tracking where it begins: its size is
        // the base halved once per level, and its offset is everything above it.
        int width = surface.Width;
        int height = surface.Height;
        int offset = 0;
        for (int level = 0; level < surface.MipLevels - 1; ++level)
        {
            offset += BlockBytesFor(width, height, surface.Format);
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        // The smallest level must END exactly at the end of the buffer. Anything else means
        // this walk does not describe how the levels were actually packed, and the seed
        // would be read from the wrong place — which yields garbage blocks that only get
        // sampled at distance, so it shows up as coloured fringing on small features rather
        // than as an obvious failure.
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

        // Convert once, so the encoder always sees the same channel layout regardless of
        // what Decompress happened to produce for this format.
        if (seed.GetFormat() != Image.Format.Rgba8)
        {
            seed.Convert(Image.Format.Rgba8);
        }

        var parts = new List<byte[]> { surface.Data };

        int w = Math.Max(1, width / 2);
        int h = Math.Max(1, height / 2);

        // Emit-then-test, because 1x1 is a LEVEL and not a terminator. Testing first stops
        // one level early and leaves the chain exactly one block short — 10,928 bytes where
        // Godot wants 10,936 — which it rejects wholesale, so the texture silently falls
        // back to unmipped and the entire exercise is wasted.
        // SUCCESSIVE HALVING: each level is resized from the level above it, not from the
        // seed. Resizing from the seed every time re-reads the full source for all ten
        // levels, which barely registers when the seed is 16x16 but costs 53 ms on a
        // single-level 1024x1024 file — the same mistake, in the same place, as
        // decompressing the base to synthesise an 8x8. Halving makes the total work about
        // 4/3 of one resize instead of ten of them, and is how a mip chain is built anyway.
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
        // Uncompressed surfaces are per-pixel, not per-block.
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
