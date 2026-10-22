using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Refii.Save;

/// <summary>
/// One element of the guest's tokenised save XML: a name, attributes in stream order, optional
/// text, and children. <see cref="TextAt"/> keeps where the text sat among the children so a
/// rewrite reproduces the guest's own byte order.
/// </summary>
public sealed class SaveNode
{
	public string Name = string.Empty;
	public List<(string Key, object Value)> Attributes = new();
	public object? Text;
	public int TextAt = -1;
	public List<SaveNode> Children = new();

	public SaveNode() { }

	public SaveNode(string name) => Name = name;

	public SaveNode? Child(string name) => Children.Find(c => c.Name == name);

	public IEnumerable<SaveNode> All(string name) => Children.FindAll(c => c.Name == name);

	public bool TryGetAttribute(string key, out object? value)
	{
		foreach ((string k, object v) in Attributes)
		{
			if (k == key)
			{
				value = v;
				return true;
			}
		}

		value = null;
		return false;
	}

	/// <summary>A child `name` carrying `value` as its text, the way the guest writes a field.</summary>
	public SaveNode Field(string name, object value)
	{
		var child = new SaveNode(name) { Text = value, TextAt = 0 };
		Children.Add(child);
		return child;
	}

	/// <summary>A deep copy.</summary>
	public SaveNode Clone()
	{
		var copy = new SaveNode(Name) { Text = Text, TextAt = TextAt };
		copy.Attributes.AddRange(Attributes);
		foreach (SaveNode child in Children)
		{
			copy.Children.Add(child.Clone());
		}

		return copy;
	}
}

/// <summary>
/// The guest's `mainsave.bin`, read and written whole: the tokenised XML documents (`Header`,
/// `EntityManager`) as a tree, and every byte after them (<see cref="Tail"/>) kept verbatim —
/// the stream carries non-tokenised sections (the scripts' Pluto blob among them) after the
/// documents, and nothing here needs to understand them.
/// </summary>
/// <remarks>
/// THE FORMAT, verified against real saves (see <see cref="Engine_SaveHero"/>'s remarks): the file
/// is `[u32 BE compressed][u32 BE raw][zlib bytes]` chunks forming ONE zlib stream, each chunk
/// ending on a sync flush, raw size 8192 (the last shorter), never finished. Inside: `00 name`
/// opens an element, `03 name value` an attribute, `02 value` text, `01` closes, `05` ends a
/// document; names are u16 indices into a per-document string table, a new string introduced by
/// `index == count` then `u32 len` (NUL included) and bytes; values are a type byte (1 string as
/// a name, 2 u32, 3 i32, 4 f32, 5 u8 bool) and payload. Big-endian throughout.
///
/// A rewrite reproduces the guest's bytes exactly for the documents (string introduction on
/// first use, attributes before text and children, text at its original place among the
/// children) and copies the tail unchanged, so the only change in the file is the edit asked for.
///
/// THE GUEST'S READER, read in TU1 so the writer matches what it depends on. The load path
/// (`sub_82446B10`) wraps the file in a buffered read stream (`sub_82B4E3F0`, 0x10000) and the
/// LHVFS decompressing stream (`sub_82B4D990`, mode 0, vtable `off_820F9BA0`), whose Read
/// (`sub_82B4DC90`) copies out of one inflated buffer and refills it with `sub_82B4E000`. A refill
/// reads two big-endian u32 (`sub_82C8AAE0` at 0x82B4E1D8 / 0x82B4E1F8: the compressed and the raw
/// size), reallocates the output buffer to the raw size if it differs (0x82B4E214..0x82B4E230 —
/// so the raw size is per chunk and unconstrained), reads exactly the compressed bytes, and
/// calls the compressor's Decompress (`sub_82B4F0B8`, vtable+8): `0x82B4F134 li r4,2;
/// 0x82B4F140 bl inflate` — Z_SYNC_FLUSH on ONE inflate context (`0x82B4F0E0 cmpwi r11,2; beq`
/// skips the init once it is up), the return code never read (`0x82B4F14C mr r3,r30`), consumed
/// and produced computed from the stream's counters. The refill then requires consumed ==
/// compressed and produced == raw (`0x82B4E294 cmplw r10,r30; 0x82B4E2A4 cmplw r11,r29`), else
/// fails. So the reader DEPENDS on: each chunk's header stating its own exact compressed size
/// and the exact bytes it inflates to, every chunk ending on a sync point of one continuous zlib
/// stream (<see cref="Deflate"/> flushes a sync point per chunk). It does NOT depend on the raw
/// size (8192 is kept only because it is the guest's own), on a final block or Z_STREAM_END (the
/// return value is not read), or on any total-size field: there is none, the end is where the
/// next header cannot be read, and the consumer stops at the document's `05` first.
/// </remarks>
public sealed class SaveStream
{
	/// <summary>The raw bytes a chunk inflates to — the guest's own chunking.</summary>
	public const int ChunkRaw = 8192;

	public List<SaveNode> Documents { get; } = new();

	/// <summary>Everything after the last parsed document, verbatim.</summary>
	public byte[] Tail { get; set; } = Array.Empty<byte>();

	public SaveNode? Document(string name) => Documents.Find(d => d.Name == name);

	// ---------------------------------------------------------------- files

	public static SaveStream ReadFile(string path) => Parse(Inflate(File.ReadAllBytes(path)));

	public void WriteFile(string path)
	{
		string temporary = path + ".tmp";
		File.WriteAllBytes(temporary, Deflate(Serialize()));
		File.Move(temporary, path, overwrite: true);
	}

	// ---------------------------------------------------------------- the zlib chunks

	/// <summary>The one zlib stream the chunks form, inflated; what the guest never finished is kept.</summary>
	public static byte[] Inflate(byte[] file)
	{
		if (file.Length > 64 * 1024 * 1024)
			throw new InvalidDataException("save exceeds the supported size limit");
		using var joined = new MemoryStream();
		int pos = 0;
		while (pos + 8 <= file.Length)
		{
			int compressed = (file[pos] << 24) | (file[pos + 1] << 16) | (file[pos + 2] << 8) | file[pos + 3];
			pos += 8;
			if (compressed <= 0 || compressed > file.Length - pos)
				throw new InvalidDataException("save compression chunk is truncated");
			joined.Write(file, pos, compressed);
			pos += compressed;
		}

		joined.Position = 0;
		using var inflater = new ZLibStream(joined, CompressionMode.Decompress);
		using var output = new MemoryStream();
		var buffer = new byte[1 << 16];
		try
		{
			int read;
			while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
			{
				output.Write(buffer, 0, read);
				if (output.Length > 128 * 1024 * 1024)
					throw new IOException("save exceeds the supported decompressed size limit");
			}
		}
		catch (InvalidDataException) when (output.Length > 0)
		{
			// The guest's last chunk ends on a sync flush, never a final block.
		}

		return output.ToArray();
	}

	/// <summary>
	/// <paramref name="raw"/> as the guest's chunked stream: one zlib stream, a sync flush every
	/// <paramref name="chunkRaw"/> bytes, each flushed run prefixed by its compressed and raw
	/// sizes, no final block.
	/// </summary>
	public static byte[] Deflate(byte[] raw, int chunkRaw = ChunkRaw)
	{
		if (chunkRaw <= 0)
			throw new ArgumentOutOfRangeException(nameof(chunkRaw));
		var compressed = new MemoryStream();
		var file = new MemoryStream();
		// Never disposed: disposing writes the final block and the checksum, which the guest's
		// own writer never emits. The stream holds no native handle on .NET 8+.
		// SmallestSize is zlib level 9: the header reads 78 DA, the guest's own. inflate takes any
		// level (FLEVEL is informational), so this is sameness, not a dependency.
		var deflater = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true);
		long taken = 0;
		Span<byte> header = stackalloc byte[8];
		for (int pos = 0; pos < raw.Length || (pos == 0 && raw.Length == 0); pos += chunkRaw)
		{
			int size = Math.Min(chunkRaw, raw.Length - pos);
			deflater.Write(raw, pos, size);
			deflater.Flush();  // a Z_SYNC_FLUSH: the chunk's bytes inflate on their own
			long produced = compressed.Length - taken;
			WriteU32(header, (uint)produced);
			WriteU32(header[4..], (uint)size);
			file.Write(header);
			file.Write(compressed.GetBuffer(), (int)taken, (int)produced);
			taken = compressed.Length;
			if (raw.Length == 0)
			{
				break;
			}
		}

		return file.ToArray();
	}

	private static void WriteU32(Span<byte> at, uint value)
	{
		at[0] = (byte)(value >> 24);
		at[1] = (byte)(value >> 16);
		at[2] = (byte)(value >> 8);
		at[3] = (byte)value;
	}

	// ---------------------------------------------------------------- the token stream

	/// <summary>
	/// Every document the inflated bytes open with, then the tail. A document is parsed whole or
	/// not at all: the first bytes that do not parse as one begin the tail.
	/// </summary>
	public static SaveStream Parse(byte[] inflated)
	{
		var stream = new SaveStream();
		var reader = new Reader(inflated);
		int consumed = 0;
		while (!reader.AtEnd && reader.LooksLikeDocument())
		{
			SaveNode? document = reader.TryDocument(out int after);
			if (document is null)
			{
				break;
			}

			stream.Documents.Add(document);
			consumed = after;
		}

		stream.Tail = new byte[inflated.Length - consumed];
		Buffer.BlockCopy(inflated, consumed, stream.Tail, 0, stream.Tail.Length);
		return stream;
	}

	/// <summary>The documents re-tokenised, then the tail.</summary>
	public byte[] Serialize()
	{
		var writer = new Writer();
		foreach (SaveNode document in Documents)
		{
			writer.Document(document);
		}

		writer.Append(Tail);
		return writer.ToArray();
	}

	private sealed class Reader
	{
		private readonly byte[] _b;
		private int _p;
		private readonly List<string> _strings = new();

		public Reader(byte[] bytes) => _b = bytes;
		public bool AtEnd => _p >= _b.Length;

		private byte U8() => _b[_p++];
		private ushort U16() { ushort v = (ushort)((_b[_p] << 8) | _b[_p + 1]); _p += 2; return v; }
		private uint U32() { uint v = (uint)((_b[_p] << 24) | (_b[_p + 1] << 16) | (_b[_p + 2] << 8) | _b[_p + 3]); _p += 4; return v; }

		/// <summary>An open token introducing the document's first string, with a sane length and name.</summary>
		public bool LooksLikeDocument()
		{
			if (_p + 7 > _b.Length || _b[_p] != 0 || _b[_p + 1] != 0 || _b[_p + 2] != 0)
			{
				return false;
			}

			uint length = (uint)((_b[_p + 3] << 24) | (_b[_p + 4] << 16) | (_b[_p + 5] << 8) | _b[_p + 6]);
			if (length < 2 || length > 256 || _p + 7 + length > _b.Length || _b[_p + 7 + (int)length - 1] != 0)
			{
				return false;
			}

			for (int i = 0; i < (int)length - 1; ++i)
			{
				byte c = _b[_p + 7 + i];
				if (c < 0x20 || c > 0x7E)
				{
					return false;
				}
			}

			return true;
		}

		private string Name()
		{
			int index = U16();
			if (index == _strings.Count)
			{
				int length = (int)U32();
				if (length < 1 || length > 1024 * 1024 || length > _b.Length - _p)
					throw new InvalidDataException("invalid save string length");
				string text = Encoding.Latin1.GetString(_b, _p, length).TrimEnd('\0');
				_p += length;
				_strings.Add(text);
			}
			else if (index > _strings.Count)
			{
				throw new InvalidDataException($"save string index {index} beyond the table ({_strings.Count})");
			}

			return _strings[index];
		}

		private object Value()
		{
			byte type = U8();
			return type switch
			{
				1 => Name(),
				2 => U32(),
				3 => unchecked((int)U32()),
				4 => BitConverter.Int32BitsToSingle(unchecked((int)U32())),
				5 => U8() != 0,
				_ => throw new InvalidDataException($"value type {type} at {_p - 1}"),
			};
		}

		/// <summary>One document, or null (position restored) when it does not parse.</summary>
		public SaveNode? TryDocument(out int after)
		{
			int start = _p;
			try
			{
				SaveNode document = Document();
				after = _p;
				return document;
			}
			catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
			{
				_p = start;
				after = start;
				return null;
			}
		}

		/// <summary>One document: a fresh string table, elements to depth zero, then `05`.</summary>
		public SaveNode Document()
		{
			_strings.Clear();
			var stack = new Stack<SaveNode>();
			SaveNode? root = null;
			while (true)
			{
				byte token = U8();
				switch (token)
				{
					case 0:
					{
						if (stack.Count >= 128) throw new InvalidDataException("save nesting limit exceeded");
						var node = new SaveNode { Name = Name() };
						if (stack.Count == 0) root = node; else stack.Peek().Children.Add(node);
						stack.Push(node);
						break;
					}
					case 3:
					{
						string key = Name();
						stack.Peek().Attributes.Add((key, Value()));
						break;
					}
					case 2:
					{
						SaveNode node = stack.Peek();
						node.Text = Value();
						node.TextAt = node.Children.Count;
						break;
					}
					case 1:
						stack.Pop();
						if (stack.Count == 0)
						{
							if (U8() != 5) throw new InvalidDataException("document did not end with 05");
							return root!;
						}
						break;
					default:
						throw new InvalidDataException($"token {token} at {_p - 1}");
				}
			}
		}
	}

	private sealed class Writer
	{
		private readonly MemoryStream _out = new();
		private readonly Dictionary<string, int> _strings = new(StringComparer.Ordinal);

		public byte[] ToArray() => _out.ToArray();

		public void Append(byte[] bytes) => _out.Write(bytes, 0, bytes.Length);

		private void U8(byte v) => _out.WriteByte(v);
		private void U16(ushort v) { _out.WriteByte((byte)(v >> 8)); _out.WriteByte((byte)v); }
		private void U32(uint v) { _out.WriteByte((byte)(v >> 24)); _out.WriteByte((byte)(v >> 16)); _out.WriteByte((byte)(v >> 8)); _out.WriteByte((byte)v); }

		private void Name(string text)
		{
			if (_strings.TryGetValue(text, out int index))
			{
				U16((ushort)index);
				return;
			}

			index = _strings.Count;
			if (index > ushort.MaxValue) throw new InvalidDataException("save string table is full");
			_strings[text] = index;
			U16((ushort)index);
			byte[] bytes = Encoding.Latin1.GetBytes(text);
			U32((uint)(bytes.Length + 1));
			_out.Write(bytes, 0, bytes.Length);
			U8(0);
		}

		private void Value(object value)
		{
			switch (value)
			{
				case string s: U8(1); Name(s); break;
				case uint u: U8(2); U32(u); break;
				case int i: U8(3); U32(unchecked((uint)i)); break;
				case float f: U8(4); U32(unchecked((uint)BitConverter.SingleToInt32Bits(f))); break;
				case bool b: U8(5); U8(b ? (byte)1 : (byte)0); break;
				default: throw new InvalidDataException($"save value of type {value.GetType().Name} cannot be written");
			}
		}

		public void Document(SaveNode root)
		{
			_strings.Clear();
			Element(root, 0);
			U8(5);
		}

		private void Element(SaveNode node, int depth)
		{
			if (depth >= 128) throw new InvalidDataException("save nesting limit exceeded");
			U8(0);
			Name(node.Name);
			foreach ((string key, object value) in node.Attributes)
			{
				U8(3);
				Name(key);
				Value(value);
			}

			int textAt = node.Text is null ? -1 : Math.Clamp(node.TextAt, 0, node.Children.Count);
			for (int i = 0; i <= node.Children.Count; ++i)
			{
				if (i == textAt)
				{
					U8(2);
					Value(node.Text!);
				}

				if (i < node.Children.Count)
				{
					Element(node.Children[i], depth + 1);
				}
			}

			U8(1);
		}
	}
}
