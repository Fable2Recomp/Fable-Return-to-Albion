using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Refii.Text;

/// <summary>
/// Rebuilds a Babel database with some of its strings changed.
///
/// TEXT CANNOT BE PATCHED IN PLACE the way a game-database value can. A `.gdb` value is a u32 at
/// a known offset, so a row overlay writes four bytes and leaves the container alone; a string
/// lives inside a zlib block, and changing its length moves every string after it. So this
/// genuinely re-emits the record table and the blocks.
///
/// EVERYTHING IT DOES NOT UNDERSTAND IS CARRIED VERBATIM. Speech links, text groups and speaker
/// names are copied byte for byte from the original — an overlay changes strings, and a section
/// nothing asked to change is safest untouched.
///
/// NEW KEYS ARE ALLOWED. The record table is only a sorted key list, so a mod may add text the
/// game never shipped; a `.gdb` overlay pointing a string property at the new key is what makes
/// it appear. Keys are re-sorted here because the guest binary-searches them.
/// </summary>
public static class Engine_BabelWriter
{
	/// <summary>The guest inflates a block into one fixed scratch buffer of this size.</summary>
	public const int ScratchLimit = 18022;

	/// <summary>
	/// Writes the database with <paramref name="overrides"/> applied.
	///
	/// Blocks are packed no larger than the largest the original already used: the guest reads
	/// that file today, so a block within the same bound cannot overflow a buffer that already
	/// holds it.
	/// </summary>
	public static byte[] Write(
		Engine_BabelDatabase original,
		IReadOnlyDictionary<uint, string> overrides,
		List<string> errors)
	{
		var strings = new SortedDictionary<uint, string>();
		foreach ((uint key, string value) in original.Entries())
		{
			strings[key] = value;
		}

		foreach ((uint key, string value) in overrides)
		{
			strings[key] = value;
		}

		int cap = Math.Clamp(original.LargestBlock, 1024, ScratchLimit);

		var records = new List<(uint Key, uint Block, uint Offset)>(strings.Count);
		var blocks = new List<byte[]>();
		var current = new MemoryStream();

		void Flush()
		{
			if (current.Length == 0)
			{
				return;
			}

			blocks.Add(current.ToArray());
			current.SetLength(0);
		}

		foreach ((uint key, string value) in strings)
		{
			byte[] encoded = Encode(value);
			if (encoded.Length > cap)
			{
				errors.Add(
					$"the text for 0x{key:X8} is {encoded.Length} bytes, more than the {cap} a " +
					"block may hold — shorten it or split it across keys.");
				continue;
			}

			if (current.Length + encoded.Length > cap)
			{
				Flush();
			}

			records.Add((key, (uint)blocks.Count, (uint)current.Length));
			current.Write(encoded, 0, encoded.Length);
		}

		Flush();

		var file = new MemoryStream();
		WriteU32(file, 0x5B010000u);
		WriteU32(file, (uint)records.Count);
		foreach ((uint key, uint block, uint offset) in records)
		{
			WriteU32(file, key);
			WriteU32(file, block);
			WriteU32(file, offset);
		}

		WriteU32(file, (uint)blocks.Count);
		for (int i = 0; i < blocks.Count; i++)
		{
			byte[] compressed = Deflate(blocks[i]);
			WriteU32(file, (uint)i);
			WriteU32(file, (uint)compressed.Length);
			WriteU32(file, (uint)blocks[i].Length);
			file.Write(compressed, 0, compressed.Length);
		}

		ReadOnlySpan<byte> tail = original.Tail;
		file.Write(tail);
		return file.ToArray();
	}

	/// <summary>
	/// One string as a block stores it: a character count that INCLUDES the terminating NUL,
	/// then UTF-16BE.
	/// </summary>
	private static byte[] Encode(string value)
	{
		var bytes = new byte[4 + ((value.Length + 1) * 2)];
		BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)(value.Length + 1));
		for (int i = 0; i < value.Length; i++)
		{
			BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4 + (i * 2)), value[i]);
		}

		return bytes;
	}

	private static byte[] Deflate(byte[] source)
	{
		using var output = new MemoryStream();
		using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, true))
		{
			compressor.Write(source, 0, source.Length);
		}

		return output.ToArray();
	}

	private static void WriteU32(Stream stream, uint value)
	{
		Span<byte> word = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(word, value);
		stream.Write(word);
	}
}
