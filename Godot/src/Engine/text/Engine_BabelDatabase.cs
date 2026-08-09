using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

using Refii.Core;

namespace Refii.Text;

/// <summary>
/// Reader for Fable 2's "Babel" localised string database
/// (<c>data/language/&lt;culture&gt;/text/book.babel</c>).
///
/// Every scalar is big-endian. Strings live in zlib-compressed blocks as UTF-16BE
/// with a leading character count that INCLUDES the terminating NUL. Records are
/// sorted by key hash, so lookup is a binary search.
///
/// Keys are <see cref="Engine_GameHash.Fnv1"/> of the tag — case sensitive, NUL excluded.
/// </summary>
public sealed class Engine_BabelDatabase
{
	private const uint Magic = 0x5B010000u;

	private readonly byte[] _data;

	// Parallel arrays rather than a struct array: the record table is ~56k entries
	// and this keeps the binary search touching one cache-friendly span.
	private readonly uint[] _keys;
	private readonly int[] _blockIndex;
	private readonly uint[] _offsets;

	private readonly int[] _blockDataOffset;
	private readonly int[] _blockCompressedSize;
	private readonly int[] _blockUncompressedSize;

	// Blocks are large (up to ~17 KB) and strings cluster, so caching the most
	// recently inflated block turns a screenful of lookups into one inflate.
	private int _cachedBlock = -1;
	private byte[]? _cachedBytes;

	/// <summary>Speaker display names, keyed by the same hash as the record table.</summary>
	public readonly Dictionary<uint, string> SpeakerNames = new();

	public int Count => _keys.Length;

	private Engine_BabelDatabase(byte[] data, uint[] keys, int[] blockIndex, uint[] offsets,
		int[] blockDataOffset, int[] blockCompressedSize, int[] blockUncompressedSize)
	{
		_data = data;
		_keys = keys;
		_blockIndex = blockIndex;
		_offsets = offsets;
		_blockDataOffset = blockDataOffset;
		_blockCompressedSize = blockCompressedSize;
		_blockUncompressedSize = blockUncompressedSize;
	}

	public static Engine_BabelDatabase Load(string path) => Parse(File.ReadAllBytes(path));

	public static Engine_BabelDatabase Parse(byte[] data)
	{
		int p = 0;
		uint magic = ReadU32(data, ref p);
		if (magic != Magic)
			throw new InvalidDataException($"not a babel file: magic {magic:X8}, expected {Magic:X8}");

		int recordCount = checked((int)ReadU32(data, ref p));
		var keys = new uint[recordCount];
		var blockIds = new uint[recordCount];
		var offsets = new uint[recordCount];
		for (int i = 0; i < recordCount; i++)
		{
			keys[i] = ReadU32(data, ref p);
			blockIds[i] = ReadU32(data, ref p);
			offsets[i] = ReadU32(data, ref p);
		}

		int blockCount = checked((int)ReadU32(data, ref p));
		var blockIdToIndex = new Dictionary<uint, int>(blockCount);
		var dataOffset = new int[blockCount];
		var compressed = new int[blockCount];
		var uncompressed = new int[blockCount];
		for (int i = 0; i < blockCount; i++)
		{
			uint id = ReadU32(data, ref p);
			compressed[i] = checked((int)ReadU32(data, ref p));
			uncompressed[i] = checked((int)ReadU32(data, ref p));
			dataOffset[i] = p;
			p += compressed[i];
			blockIdToIndex[id] = i;
		}

		// Resolve each record's block id to a dense index once, so lookup never
		// touches the dictionary.
		var blockIndex = new int[recordCount];
		for (int i = 0; i < recordCount; i++)
			blockIndex[i] = blockIdToIndex.TryGetValue(blockIds[i], out int idx) ? idx : -1;

		var db = new Engine_BabelDatabase(data, keys, blockIndex, offsets,
			dataOffset, compressed, uncompressed);

		p += 4; // max-block-size hint; the engine stores it but never sizes anything from it

		// Speech links (10 bytes each) and text groups are skipped: neither is needed
		// to resolve a tag to text, and both are documented in the format notes.
		int speechLinks = checked((int)ReadU32(data, ref p));
		p += speechLinks * 10;

		int textGroups = checked((int)ReadU32(data, ref p));
		for (int i = 0; i < textGroups; i++)
		{
			p += 4;
			int members = checked((int)ReadU32(data, ref p));
			p += members * 4;
		}

		int speakerNames = checked((int)ReadU32(data, ref p));
		for (int i = 0; i < speakerNames; i++)
		{
			uint key = ReadU32(data, ref p);
			int chars = checked((int)ReadU32(data, ref p));
			db.SpeakerNames[key] = ReadUtf16Be(data, ref p, chars);
		}

		if (p != data.Length)
			throw new InvalidDataException($"babel: {data.Length - p} trailing bytes, expected 0");

		return db;
	}

	/// <summary>Look up by tag, e.g. <c>GUI_BACK</c>. Case sensitive.</summary>
	public bool TryGetString(string tag, out string value) =>
		TryGetString(Engine_GameHash.Fnv1(tag), out value);

	public bool TryGetString(uint key, out string value)
	{
		value = string.Empty;
		int i = Array.BinarySearch(_keys, key);
		if (i < 0)
			return false;

		int block = _blockIndex[i];
		if (block < 0)
			return false;

		byte[] bytes = GetBlock(block);
		int p = checked((int)_offsets[i]);
		if (p + 4 > bytes.Length)
			return false;

		int chars = checked((int)ReadU32(bytes, ref p));
		if (chars <= 0 || p + chars * 2 > bytes.Length)
			return false;

		value = ReadUtf16Be(bytes, ref p, chars);
		return true;
	}

	public bool ContainsKey(string tag) => Array.BinarySearch(_keys, Engine_GameHash.Fnv1(tag)) >= 0;

	/// <summary>All record keys, ascending. Useful for browsing or diffing a language.</summary>
	public ReadOnlySpan<uint> Keys => _keys;

	private byte[] GetBlock(int index)
	{
		if (_cachedBlock == index && _cachedBytes is not null)
			return _cachedBytes;

		var outBytes = new byte[_blockUncompressedSize[index]];
		using (var src = new MemoryStream(_data, _blockDataOffset[index], _blockCompressedSize[index], writable: false))
		using (var zlib = new ZLibStream(src, CompressionMode.Decompress))
		{
			int read = 0;
			while (read < outBytes.Length)
			{
				int n = zlib.Read(outBytes, read, outBytes.Length - read);
				if (n <= 0)
					throw new InvalidDataException($"babel: block {index} inflated short ({read}/{outBytes.Length})");
				read += n;
			}
		}

		_cachedBlock = index;
		_cachedBytes = outBytes;
		return outBytes;
	}

	private static uint ReadU32(byte[] d, ref int p)
	{
		uint v = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p, 4));
		p += 4;
		return v;
	}

	/// <summary>Reads <paramref name="chars"/> UTF-16BE units and drops the trailing NUL.</summary>
	private static string ReadUtf16Be(byte[] d, ref int p, int chars)
	{
		var sb = new StringBuilder(Math.Max(0, chars - 1));
		for (int i = 0; i < chars; i++)
		{
			char c = (char)BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p, 2));
			p += 2;
			if (i < chars - 1)
				sb.Append(c);
		}
		return sb.ToString();
	}
}
