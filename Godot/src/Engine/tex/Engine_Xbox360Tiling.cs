using System;

using Refii.Core;

using Refii.Ui;

namespace Refii.Textures;

public static class Engine_Xbox360Tiling
{
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

				if (source < 0 || source + blockBytes > tiled.Length)
					continue;

				tiled.Slice(source, blockBytes).CopyTo(output.AsSpan(destination, blockBytes));
			}
		}

		return output;
	}

	/// <summary>
	/// The exact inverse of <see cref="Untile"/>: linear blocks in, the console's tiled layout out.
	///
	/// A texture a mod authors has to be written the way a shipped one is, because the reader
	/// untiles unconditionally — hand it linear data and every block lands somewhere else. The
	/// two share <see cref="TiledBlockIndex"/> rather than deriving the address twice, so the
	/// round trip is the same arithmetic run in both directions.
	///
	/// The result can be LARGER than the linear input: tiling addresses within a 32-block aligned
	/// width, so the tail of the last macro-tile is padding no reader looks at. It is zero here,
	/// where the console left whatever the allocator had.
	/// </summary>
	public static byte[] Tile(ReadOnlySpan<byte> linear, int widthBlocks, int heightBlocks,
		int blockBytes)
	{
		if (widthBlocks <= 0 || heightBlocks <= 0)
			return Array.Empty<byte>();
		if (blockBytes != 1 && blockBytes != 2 && blockBytes != 4 && blockBytes != 8 && blockBytes != 16)
			throw new ArgumentOutOfRangeException(nameof(blockBytes), blockBytes,
				"block size must be a power of two from 1 to 16");

		int log2BlockBytes = System.Numerics.BitOperations.TrailingZeroCount((uint)blockBytes);
		int alignedWidth = (widthBlocks + 31) & ~31;

		int size = 0;
		for (int y = 0; y < heightBlocks; y++)
		{
			for (int x = 0; x < widthBlocks; x++)
			{
				int end = (TiledBlockIndex(x, y, alignedWidth, log2BlockBytes) + 1) * blockBytes;
				if (end > size)
					size = end;
			}
		}

		var output = new byte[size];

		for (int y = 0; y < heightBlocks; y++)
		{
			for (int x = 0; x < widthBlocks; x++)
			{
				int destination = TiledBlockIndex(x, y, alignedWidth, log2BlockBytes) * blockBytes;
				int source = (y * widthBlocks + x) * blockBytes;

				if (source + blockBytes > linear.Length)
					continue;

				linear.Slice(source, blockBytes).CopyTo(output.AsSpan(destination, blockBytes));
			}
		}

		return output;
	}

	public static void SwapWords16(Span<byte> data)
	{
		for (int i = 0; i + 1 < data.Length; i += 2)
			(data[i], data[i + 1]) = (data[i + 1], data[i]);
	}

	public static void SwapWords32(Span<byte> data)
	{
		for (int i = 0; i + 3 < data.Length; i += 4)
		{
			(data[i], data[i + 3]) = (data[i + 3], data[i]);
			(data[i + 1], data[i + 2]) = (data[i + 2], data[i + 1]);
		}
	}

	private static int TiledBlockIndex(int x, int y, int alignedWidthBlocks, int log2BlockBytes)
	{
		int macro = ((x >> 5) + (y >> 5) * (alignedWidthBlocks >> 5)) << (log2BlockBytes + 7);

		int micro = ((x & 7) + ((y & 0xE) << 2)) << log2BlockBytes;

		int offset = macro + ((micro & ~0xF) << 1) + (micro & 0xF) + ((y & 1) << 4);

		int address =
			((offset & ~0x1FF) << 3)
			+ ((y & 16) << 7)
			+ ((offset & 0x1C0) << 2)
			+ ((((y & 8) >> 2) + (x >> 3)) & 3) * 64
			+ (offset & 0x3F);

		return address >> log2BlockBytes;
	}
}
