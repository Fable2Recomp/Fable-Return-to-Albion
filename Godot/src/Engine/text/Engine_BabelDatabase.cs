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
	/// <summary>
	/// The last inflated block, as ONE reference: the mods' text lookup reads the book on the guest's
	/// thread too (Engine_ModText), and an index and its bytes held apart could be read torn.
	/// </summary>
	private sealed record CachedBlock(int Index, byte[] Bytes);

	private volatile CachedBlock? _cached;

	/// <summary>Speaker display names, keyed by the same hash as the record table.</summary>
	public readonly Dictionary<uint, string> SpeakerNames = new();

	/// <summary>
	/// The text GROUPS: a group's key (the text-key hash of its name) to its member keys, in order —
	/// what `sub_8237AC80` finds and the credits screen walks (`sub_826B79D8`).
	/// </summary>
	public readonly Dictionary<uint, uint[]> Groups = new();

	/// <summary>A group's member keys by its name, or empty when the bank has no such group.</summary>
	public uint[] GroupMembers(string name) =>
		Groups.TryGetValue(Engine_GameHash.Fnv1(name), out uint[]? members) ? members : System.Array.Empty<uint>();

	public int Count => _keys.Length;

	/// <summary>Where the post-block sections start — see the note at the assignment.</summary>
	public int TailOffset { get; private set; }

	/// <summary>The post-block sections, exactly as they were read.</summary>
	public ReadOnlySpan<byte> Tail => _data.AsSpan(TailOffset);

	/// <summary>
	/// The largest block this file already uses, uncompressed.
	///
	/// The writer packs to this rather than to a constant: the guest inflates into one fixed
	/// scratch buffer, and a block no larger than one the file already carries cannot overflow
	/// a buffer that already reads this file.
	/// </summary>
	public int LargestBlock
	{
		get
		{
			int largest = 0;
			foreach (int size in _blockUncompressedSize)
			{
				if (size > largest)
				{
					largest = size;
				}
			}

			return largest;
		}
	}

	/// <summary>
	/// Every record, block by block, so the whole file is inflated once rather than once per
	/// string. Ascending by key within a block; the writer re-sorts anyway.
	/// </summary>
	public IEnumerable<(uint Key, string Value)> Entries()
	{
		var byBlock = new Dictionary<int, List<int>>();
		for (int i = 0; i < _keys.Length; i++)
		{
			if (_blockIndex[i] < 0)
			{
				continue;
			}

			if (!byBlock.TryGetValue(_blockIndex[i], out List<int>? records))
			{
				records = new List<int>();
				byBlock[_blockIndex[i]] = records;
			}

			records.Add(i);
		}

		foreach ((int block, List<int> records) in byBlock)
		{
			byte[] bytes = GetBlock(block);
			foreach (int i in records)
			{
				int p = checked((int)_offsets[i]);
				if (p + 4 > bytes.Length)
				{
					continue;
				}

				int chars = checked((int)ReadU32(bytes, ref p));
				if (chars <= 0 || p + (chars * 2) > bytes.Length)
				{
					continue;
				}

				yield return (_keys[i], ReadUtf16Be(bytes, ref p, chars));
			}
		}
	}

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

		// EVERYTHING FROM HERE IS COPIED VERBATIM BY THE WRITER. Speech links, text groups and
		// speaker names are read below but never rebuilt: an overlay changes strings, and a
		// section it does not touch is safest carried across byte for byte.
		db.TailOffset = p;

		// A SPEECH LINK IS 10 BYTES IN FABLE 2 AND 11 IN FABLE 3, under the same magic, so the
		// stride is the one whose tail ends exactly at the end of the file. Measured on Fable 3's
		// en-uk book.babel: 57,914 links at 11 bytes, then 3,859 groups and 613 speaker names land
		// on its last byte; at 10 the groups are read out of the links.
		Exception? last = null;
		foreach (int linkStride in SpeechLinkStrides)
		{
			db.Groups.Clear();
			db.SpeakerNames.Clear();
			try
			{
				ReadTail(data, p, linkStride, db);
				return db;
			}
			catch (Exception exception) when (exception is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
			{
				last = exception;
			}
		}

		throw last is InvalidDataException invalid ? invalid : new InvalidDataException($"babel: tail unreadable: {last?.Message}");
	}

	/// <summary>The speech-link record sizes the titles use, Fable 2's first.</summary>
	private static readonly int[] SpeechLinkStrides = { 10, 11 };

	private static void ReadTail(byte[] data, int p, int linkStride, Engine_BabelDatabase db)
	{
		p += 4; // max-block-size hint; the engine stores it but never sizes anything from it

		// Speech links are skipped; text groups are kept for the screens that walk one (the
		// credits).
		int speechLinks = checked((int)ReadU32(data, ref p));
		p = checked(p + speechLinks * linkStride);

		int textGroups = checked((int)ReadU32(data, ref p));
		for (int i = 0; i < textGroups; i++)
		{
			uint group = ReadU32(data, ref p);
			int members = checked((int)ReadU32(data, ref p));
			if (members < 0 || members > (data.Length - p) / 4)
				throw new InvalidDataException($"babel: text group of {members} members at {p}");
			var memberKeys = new uint[members];
			for (int m = 0; m < members; m++)
			{
				memberKeys[m] = ReadU32(data, ref p);
			}

			db.Groups[group] = memberKeys;
		}

		int speakerNames = checked((int)ReadU32(data, ref p));
		for (int i = 0; i < speakerNames; i++)
		{
			uint key = ReadU32(data, ref p);
			int chars = checked((int)ReadU32(data, ref p));
			if (chars < 0 || chars > (data.Length - p) / 2)
				throw new InvalidDataException($"babel: speaker name of {chars} chars at {p}");
			db.SpeakerNames[key] = ReadUtf16Be(data, ref p, chars);
		}

		if (p != data.Length)
			throw new InvalidDataException($"babel: {data.Length - p} trailing bytes, expected 0");
	}

	/// <summary>
	/// The hero's text suffix, <c>_HM</c>, <c>_HF</c> or empty — what <c>sub_8230FC40</c> reads
	/// off the hero's component 12 (<c>+12</c>: 2 is <c>_HM</c>, 1 is <c>_HF</c>). Set by the game
	/// from the guest; empty in the editor.
	/// </summary>
	public static string HeroTextSuffix { get; set; } = string.Empty;

	/// <summary>Look up by tag, e.g. <c>GUI_BACK</c> (or hex <c>0x1234ABCD</c>). Case sensitive.</summary>
	/// <remarks>
	/// A tag is tried with the hero's suffix first, as every guest lookup is: `sub_8230E3A0`
	/// takes `tag + suffix` when that key exists and the bare tag otherwise. Some strings exist
	/// ONLY suffixed — `INV_ITEM_GIFT_TOY_DOLL_HERO_NAME` is absent, `_NAME_HM`/`_NAME_HF` are not.
	/// </remarks>
	public bool TryGetString(string tag, out string value)
	{
		if (tag.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
			uint.TryParse(tag.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint hexKey))
		{
			return TryGetString(hexKey, out value);
		}

		string suffix = HeroTextSuffix;
		if (suffix.Length != 0 && TryGetString(Engine_GameHash.Fnv1(tag + suffix), out value))
		{
			return true;
		}

		return TryGetString(Engine_GameHash.Fnv1(tag), out value);
	}

	/// <summary>
	/// Strings already resolved, by key. The table never changes after load, and the HUD asks
	/// for the same handful every frame from different blocks, which would defeat the one-block
	/// cache below and inflate a zlib block per ask. Bounded by the distinct tags the game shows.
	/// </summary>
	private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> _resolved = new();

	public bool TryGetString(uint key, out string value)
	{
		if (_resolved.TryGetValue(key, out string? known))
		{
			value = known;
			return true;
		}

		if (!TryResolve(key, out value))
		{
			return false;
		}

		_resolved.TryAdd(key, value);
		return true;
	}

	private bool TryResolve(uint key, out string value)
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
		if (_cached is { } cached && cached.Index == index)
			return cached.Bytes;

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

		_cached = new CachedBlock(index, outBytes);
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
