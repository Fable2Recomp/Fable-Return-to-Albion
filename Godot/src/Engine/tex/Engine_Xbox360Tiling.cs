using System;

namespace Refii.Textures;

/// <summary>
/// Untiles Xbox 360 texture surfaces.
///
/// The 360's GPU reads textures in a swizzled layout rather than row-major, and a
/// <c>.tex</c> taking the <c>flags &amp; 0x02</c> raw path ships exactly what the GPU was
/// handed — tiled, and with each 16-bit word byte-swapped for the big-endian bus. A
/// desktop GPU needs both undone. Presented as-is the surface is pure noise, which is
/// what the GUI font atlases looked like.
///
/// This works in BLOCKS, not pixels: for BC1/BC3 a "texel" to the tiler is one
/// compressed block, so the caller passes block counts and the block size in bytes. An
/// uncompressed surface is the same call with a 1x1 block.
/// </summary>
public static class Engine_Xbox360Tiling
{
	/// <summary>
	/// Untiles into a fresh row-major buffer.
	///
	/// <paramref name="widthBlocks"/> and <paramref name="heightBlocks"/> are the
	/// LOGICAL extent — the output is exactly that many blocks. The tiled source may be
	/// larger, because tiling aligns the width to 32 blocks and the surface is padded to
	/// suit; that padding is read past, never copied out.
	/// </summary>
	public static byte[] Untile(ReadOnlySpan<byte> tiled, int widthBlocks, int heightBlocks,
		int blockBytes)
	{
		if (widthBlocks <= 0 || heightBlocks <= 0)
			return Array.Empty<byte>();
		if (blockBytes != 1 && blockBytes != 2 && blockBytes != 4 && blockBytes != 8 && blockBytes != 16)
			throw new ArgumentOutOfRangeException(nameof(blockBytes), blockBytes,
				"block size must be a power of two from 1 to 16");

		int log2BlockBytes = System.Numerics.BitOperations.TrailingZeroCount((uint)blockBytes);
		int alignedWidth = (widthBlocks + 31) & ~31;

		var output = new byte[widthBlocks * heightBlocks * blockBytes];

		for (int y = 0; y < heightBlocks; y++)
		{
			for (int x = 0; x < widthBlocks; x++)
			{
				int source = TiledBlockIndex(x, y, alignedWidth, log2BlockBytes) * blockBytes;
				int destination = (y * widthBlocks + x) * blockBytes;

				// A truncated or short surface must not take the whole texture down —
				// leaving the block zeroed shows a hole, which is diagnosable.
				if (source < 0 || source + blockBytes > tiled.Length)
					continue;

				tiled.Slice(source, blockBytes).CopyTo(output.AsSpan(destination, blockBytes));
			}
		}

		return output;
	}

	/// <summary>
	/// Swaps every 16-bit word in place.
	///
	/// Separate from <see cref="Untile"/> because the two are independent: a surface can
	/// need the swap without being tiled. DXT blocks are little-endian RGB565 endpoints
	/// plus an index word, so a 360 copy reads as garbage colours until this is undone.
	/// </summary>
	public static void SwapWords16(Span<byte> data)
	{
		for (int i = 0; i + 1 < data.Length; i += 2)
			(data[i], data[i + 1]) = (data[i + 1], data[i]);
	}

	/// <summary>
	/// Reverses every 32-bit word in place — the other endian convention a 360 surface
	/// can use.
	///
	/// Which one applies is per format, and the two are not interchangeable: a DXT1
	/// block read with the wrong one comes out with its two endpoints transposed, which
	/// also flips BC1's <c>c0 &lt;= c1</c> punch-through test. The symptom is a texture
	/// whose SHAPES are right but whose colours are wrong and whose transparent areas
	/// have gone opaque.
	/// </summary>
	public static void SwapWords32(Span<byte> data)
	{
		for (int i = 0; i + 3 < data.Length; i += 4)
		{
			(data[i], data[i + 3]) = (data[i + 3], data[i]);
			(data[i + 1], data[i + 2]) = (data[i + 2], data[i + 1]);
		}
	}

	/// <summary>
	/// Index of the tiled block holding logical block (<paramref name="x"/>,
	/// <paramref name="y"/>).
	///
	/// The address swizzle is the 360's documented 2D tiled layout: a 32x32-block macro
	/// tile selected by the high coordinate bits, then the low bits interleaved. Every
	/// term is load-bearing — the arithmetic is not a formula to simplify but a bit
	/// permutation, and dropping any one of the mixing steps still produces a
	/// plausible-looking image that is wrong.
	/// </summary>
	private static int TiledBlockIndex(int x, int y, int alignedWidthBlocks, int log2BlockBytes)
	{
		// High coordinate bits: which 32x32 macro tile, scaled by the tile's byte size.
		int macro = ((x >> 5) + (y >> 5) * (alignedWidthBlocks >> 5)) << (log2BlockBytes + 7);

		// Low coordinate bits, 6 bits' worth.
		int micro = ((x & 7) + ((y & 0xE) << 2)) << log2BlockBytes;

		int offset = macro + ((micro & ~0xF) << 1) + (micro & 0xF) + ((y & 1) << 4);

		int address =
			((offset & ~0x1FF) << 3)                          // everything above bit 9
			+ ((y & 16) << 7)                                 // one more y bit
			+ ((offset & 0x1C0) << 2)                         // offset bits 8..6
			+ ((((y & 8) >> 2) + (x >> 3)) & 3) * 64          // two mixed bits, at bit 6
			+ (offset & 0x3F);                                // offset bits 5..0

		// The address is in bytes; the caller wants a block index.
		return address >> log2BlockBytes;
	}
}
