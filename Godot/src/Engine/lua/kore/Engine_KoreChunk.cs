using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Refii.Lua.Kore;

/// <summary>
/// A Kore (Fable 3 Lua) binary chunk.
///
/// <para>Header, as the loader checks it (TU3 sub_822A87C8, disc sub_822AA278): 12 bytes
/// <c>1B 4C 75 61 51 02 01 04 04 04 04 00</c>: signature, version 0x51, format byte (must be 2,
/// <c>v17 != 2</c> fails "Header mismatch when loading bytecode"), endianness (1 = little; the
/// reader swaps when it differs), int 4, size_t 4, Instruction 4, lua_Number 4 or 8 (the chunks
/// say 4: float), integral flag 0.</para>
///
/// <para>Function (TU3 sub_822A8458, disc sub_822A9F08): source string, then for format 2 a
/// function-name string (<c>if (*((_BYTE *)a1 + 60) == 2) sub_822A7EC0(a1 + 1, a2 + 14)</c>),
/// linedefined, lastlinedefined (int), nups, numparams, is_vararg, maxstacksize (byte), code
/// (count + words), constants (TU3 sub_822A7FF8: count, then tag byte 0 nil, 1 boolean (byte),
/// 3 number (4-byte float), 4 string; 2 or above 4 is "bad constant"), prototypes (count +
/// recursion), debug (TU3 sub_822A8200: lineinfo, locvars {name, startpc, endpc}, upvalue names).
/// Strings are a size_t length that counts the terminating NUL; 0 means none.</para>
///
/// <para>The name is the function statement's name as written (<c>Foo.Bar:Baz</c>), the local
/// function's name, <c>(main chunk)</c> for the main function, or none for an anonymous
/// function; stripped chunks carry neither source nor name.</para>
///
/// <para>Format 0 is the stock Lua 5.1 layout (no name, 6-bit opcodes). The Kore loader rejects
/// it; 122 such chunks ship as leftovers. They are read so they can still be shown.</para>
/// </summary>
public sealed class Engine_KoreChunk
{
	public const int HeaderSize = 12;

	/// <summary>Format byte: 2 for Kore, 0 for a stock Lua 5.1 chunk.</summary>
	public int Format { get; private set; }
	public bool LittleEndian { get; private set; } = true;
	public int NumberSize { get; private set; } = 4;
	public Proto Main { get; private set; } = new();

	/// <summary>Whether the Kore VM would load it (format 2).</summary>
	public bool IsKore => Format == 2;

	public sealed class Constant
	{
		public enum Kind { Nil, Boolean, Number, String }
		public Kind Type;
		public bool Boolean;
		/// <summary>The value; exactly a float when the chunk uses 4-byte numbers.</summary>
		public double Number;
		/// <summary>The string's bytes, one char per byte (Latin-1), so they round-trip exactly.</summary>
		public string String = string.Empty;
	}

	public struct LocVar
	{
		public string Name;
		public int StartPc;
		public int EndPc;
		/// <summary>Known register (inferred locals); -1 = positional, as in the debug info.</summary>
		public int Reg;
		/// <summary>Parameter or loop control seeded before inference: never retracted.</summary>
		public bool Seeded;

		public LocVar(string name, int startPc, int endPc, int reg = -1)
		{
			Name = name;
			StartPc = startPc;
			EndPc = endPc;
			Reg = reg;
			Seeded = false;
		}
	}

	public sealed class Proto
	{
		/// <summary>Source name (null when stripped).</summary>
		public string? Source;
		/// <summary>Function name (format 2 only; null when anonymous or stripped).</summary>
		public string? Name;
		/// <summary>Whether this chunk records function names (format 2 and not stripped).</summary>
		public bool NameRecorded;
		public int LineDefined;
		public int LastLineDefined;
		public int Nups;
		public int NumParams;
		public int IsVararg;
		public int MaxStackSize;
		public uint[] Code = Array.Empty<uint>();
		public List<Constant> Constants = new();
		public List<Proto> Protos = new();
		public int[] LineInfo = Array.Empty<int>();
		public List<LocVar> LocVars = new();
		public List<string> Upvalues = new();
		/// <summary>Decoded instructions, in Lua 5.1 terms (see <see cref="Engine_KoreOpcodes.Decode"/>).</summary>
		public Engine_KoreOpcodes.Instr[] Ins = Array.Empty<Engine_KoreOpcodes.Instr>();

		public bool HasLineInfo => LineInfo.Length > 0;
		public bool HasLocVars => LocVars.Count > 0;
	}

	/// <summary>Whether the bytes begin with the Lua signature.</summary>
	public static bool IsChunk(ReadOnlySpan<byte> bytes) =>
		bytes.Length >= 4 && bytes[0] == 0x1B && bytes[1] == (byte)'L' && bytes[2] == (byte)'u' && bytes[3] == (byte)'a';

	/// <summary>Parses a chunk; false with <paramref name="error"/> on malformed input or trailing bytes.</summary>
	public static bool TryRead(byte[] bytes, out Engine_KoreChunk chunk, out string error)
	{
		chunk = new Engine_KoreChunk();
		error = string.Empty;
		if (bytes == null || bytes.Length < HeaderSize || !IsChunk(bytes))
		{
			error = "not a Lua chunk";
			return false;
		}
		if (bytes[4] != 0x51)
		{
			error = $"unsupported Lua version 0x{bytes[4]:X2}";
			return false;
		}
		int format = bytes[5];
		if (format != 2 && format != 0)
		{
			error = $"unsupported chunk format {format}";
			return false;
		}
		if (bytes[7] != 4 || bytes[8] != 4 || bytes[9] != 4 || (bytes[10] != 4 && bytes[10] != 8) || bytes[11] != 0)
		{
			error = "header mismatch (int, size_t and Instruction must be 4 bytes, numbers not integral)";
			return false;
		}
		chunk.Format = format;
		chunk.LittleEndian = bytes[6] != 0;
		chunk.NumberSize = bytes[10];
		var reader = new Reader(bytes, HeaderSize, chunk.LittleEndian);
		try
		{
			chunk.Main = ReadFunction(ref reader, chunk, 0);
		}
		catch (FormatException e)
		{
			error = e.Message;
			return false;
		}
		if (reader.Position != bytes.Length)
		{
			error = $"{bytes.Length - reader.Position} trailing bytes after the main function";
			return false;
		}
		return true;
	}

	private static Proto ReadFunction(ref Reader r, Engine_KoreChunk chunk, int depth)
	{
		if (depth > 128)
		{
			throw new FormatException("code too deep");
		}
		var f = new Proto
		{
			Source = r.String(),
		};
		if (chunk.Format == 2)
		{
			f.Name = r.String();
			f.NameRecorded = f.Source != null;
		}
		f.LineDefined = r.Int();
		f.LastLineDefined = r.Int();
		f.Nups = r.Byte();
		f.NumParams = r.Byte();
		f.IsVararg = r.Byte();
		f.MaxStackSize = r.Byte();
		int ncode = r.Count(4);
		f.Code = new uint[ncode];
		for (int i = 0; i < ncode; i++)
		{
			f.Code[i] = (uint)r.Int();
		}
		int nk = r.Count(1);
		for (int i = 0; i < nk; i++)
		{
			int tag = r.Byte();
			var k = new Constant();
			switch (tag)
			{
				case 0: k.Type = Constant.Kind.Nil; break;
				case 1: k.Type = Constant.Kind.Boolean; k.Boolean = r.Byte() != 0; break;
				case 3: k.Type = Constant.Kind.Number; k.Number = r.Number(chunk.NumberSize); break;
				case 4: k.Type = Constant.Kind.String; k.String = r.String() ?? string.Empty; break;
				default: throw new FormatException($"bad constant tag {tag}");
			}
			f.Constants.Add(k);
		}
		int np = r.Count(1);
		for (int i = 0; i < np; i++)
		{
			f.Protos.Add(ReadFunction(ref r, chunk, depth + 1));
		}
		int nl = r.Count(4);
		f.LineInfo = new int[nl];
		for (int i = 0; i < nl; i++)
		{
			f.LineInfo[i] = r.Int();
		}
		int nloc = r.Count(9);
		for (int i = 0; i < nloc; i++)
		{
			string name = r.String() ?? string.Empty;
			int start = r.Int();
			int end = r.Int();
			f.LocVars.Add(new LocVar(name, start, end));
		}
		int nup = r.Count(4);
		for (int i = 0; i < nup; i++)
		{
			f.Upvalues.Add(r.String() ?? string.Empty);
		}
		f.Ins = new Engine_KoreOpcodes.Instr[ncode];
		for (int i = 0; i < ncode; i++)
		{
			int line = i < f.LineInfo.Length ? f.LineInfo[i] : 0;
			f.Ins[i] = chunk.Format == 2 ? Engine_KoreOpcodes.Decode(f.Code[i], line) : Engine_KoreOpcodes.DecodeStock(f.Code[i], line);
		}
		return f;
	}

	/// <summary>Writes a chunk in the layout the loader reads (used by tests and tools).</summary>
	public static byte[] Write(Proto main, int format = 2, bool strip = false)
	{
		var bytes = new List<byte> { 0x1B, (byte)'L', (byte)'u', (byte)'a', 0x51, (byte)format, 1, 4, 4, 4, 4, 0 };
		WriteFunction(bytes, main, format, strip);
		return bytes.ToArray();
	}

	private static void WriteFunction(List<byte> o, Proto f, int format, bool strip)
	{
		void Int(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); o.AddRange(b); }
		void Str(string? s)
		{
			if (s == null) { Int(0); return; }
			Int(s.Length + 1);
			foreach (char ch in s) o.Add((byte)ch);
			o.Add(0);
		}
		Str(strip ? null : f.Source);
		if (format == 2) Str(strip ? null : f.Name);
		Int(f.LineDefined);
		Int(f.LastLineDefined);
		o.Add((byte)f.Nups);
		o.Add((byte)f.NumParams);
		o.Add((byte)f.IsVararg);
		o.Add((byte)f.MaxStackSize);
		Int(f.Code.Length);
		foreach (uint w in f.Code) Int((int)w);
		Int(f.Constants.Count);
		foreach (Constant k in f.Constants)
		{
			switch (k.Type)
			{
				case Constant.Kind.Nil: o.Add(0); break;
				case Constant.Kind.Boolean: o.Add(1); o.Add(k.Boolean ? (byte)1 : (byte)0); break;
				case Constant.Kind.Number:
					o.Add(3);
					var b = new byte[4];
					BinaryPrimitives.WriteSingleLittleEndian(b, (float)k.Number);
					o.AddRange(b);
					break;
				case Constant.Kind.String: o.Add(4); Str(k.String); break;
			}
		}
		Int(f.Protos.Count);
		foreach (Proto p in f.Protos) WriteFunction(o, p, format, strip);
		if (strip)
		{
			Int(0); Int(0); Int(0);
			return;
		}
		Int(f.LineInfo.Length);
		foreach (int l in f.LineInfo) Int(l);
		Int(f.LocVars.Count);
		foreach (LocVar v in f.LocVars) { Str(v.Name); Int(v.StartPc); Int(v.EndPc); }
		Int(f.Upvalues.Count);
		foreach (string u in f.Upvalues) Str(u);
	}

	/// <summary>Bytes of a Lua string as the one-char-per-byte form constants use.</summary>
	public static string FromBytes(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

	private ref struct Reader
	{
		private readonly ReadOnlySpan<byte> _bytes;
		private readonly bool _little;
		public int Position;

		public Reader(byte[] bytes, int position, bool little)
		{
			_bytes = bytes;
			Position = position;
			_little = little;
		}

		private ReadOnlySpan<byte> Take(int n)
		{
			if (n < 0 || Position + n > _bytes.Length)
			{
				throw new FormatException($"truncated chunk at offset {Position}");
			}
			var s = _bytes.Slice(Position, n);
			Position += n;
			return s;
		}

		public int Byte() => Take(1)[0];

		public int Int()
		{
			var s = Take(4);
			return _little ? BinaryPrimitives.ReadInt32LittleEndian(s) : BinaryPrimitives.ReadInt32BigEndian(s);
		}

		/// <summary>An element count, bounded by the bytes left so a corrupt count fails cleanly.</summary>
		public int Count(int minElementSize)
		{
			int n = Int();
			if (n < 0 || (long)n * minElementSize > _bytes.Length - Position)
			{
				throw new FormatException($"bad count {n} at offset {Position - 4}");
			}
			return n;
		}

		public double Number(int size)
		{
			var s = Take(size);
			if (size == 4)
			{
				return _little ? BinaryPrimitives.ReadSingleLittleEndian(s) : BinaryPrimitives.ReadSingleBigEndian(s);
			}
			return _little ? BinaryPrimitives.ReadDoubleLittleEndian(s) : BinaryPrimitives.ReadDoubleBigEndian(s);
		}

		public string? String()
		{
			int n = Int();
			if (n == 0)
			{
				return null;
			}
			var s = Take(n);
			// The length counts the terminating NUL.
			return Encoding.Latin1.GetString(s[..^1]);
		}
	}
}
