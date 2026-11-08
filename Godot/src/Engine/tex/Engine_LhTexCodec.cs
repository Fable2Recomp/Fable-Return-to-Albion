using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Refii.Core;

using Refii.Ui;

namespace Refii.Textures;

public static class Engine_LhTexCodec
{
	private static readonly int[,] OpTable =
	{
		{ 0, -1, 0 }, { 0, -2, 0 }, { 0, -3, 0 }, { 0, -4, 0 }, { 0, -5, 0 },
		{ 0, 0, -1 }, { 0, -1, -1 }, { 0, -2, -1 }, { 0, -3, -1 }, { 0, -4, -1 }, { 0, -5, -1 },
		{ 0, 0, -2 }, { 0, -1, -2 }, { 0, -2, -2 }, { 0, -3, -2 },
		{ 0, 0, -3 }, { 0, -1, -3 }, { 0, -2, -3 },
		{ 0, 0, -4 }, { 0, -1, -4 },
		{ 0, 0, -5 }, { 0, -1, -5 },
		{ 1, -1, 0 }, { 1, -2, 0 }, { 1, -3, 0 }, { 1, -4, 0 },
		{ 1, 0, -1 }, { 1, -1, -1 }, { 1, -2, -1 }, { 1, -3, -1 }, { 1, -4, -1 },
		{ 1, 0, -2 }, { 1, -1, -2 }, { 1, -2, -2 },
		{ 1, 0, -3 }, { 1, -1, -3 },
		{ 1, 0, -4 }, { 1, -1, -4 },
		{ 2, -1, 0 }, { 2, -2, 0 }, { 2, -3, 0 }, { 2, -4, 0 },
		{ 2, 0, -1 }, { 2, -1, -1 }, { 2, -2, -1 }, { 2, -3, -1 }, { 2, -4, -1 },
		{ 2, 0, -2 }, { 2, -1, -2 }, { 2, -2, -2 },
		{ 2, 0, -3 }, { 2, -1, -3 },
		{ 2, 0, -4 }, { 2, -1, -4 },
		{ 3, -1, 0 }, { 3, -2, 0 },
		{ 3, 0, -1 }, { 3, -1, -1 }, { 3, -2, -1 },
		{ 3, 0, -2 }, { 3, -1, -2 },
		{ 4, 0, 0 },
	};

	private static readonly int[,] DeltaTable =
	{
		{ -3, -5, -3 }, { -3, -5, -2 }, { -3, -5, -1 },
		{ -3, -3, -3 }, { -3, -3, -2 }, { -3, -3, -1 },
		{ -2, -5, -3 }, { -2, -5, -2 }, { -2, -5, -1 }, { -2, -5, 0 },
		{ -2, -3, -3 }, { -2, -3, -2 }, { -2, -3, -1 }, { -2, -3, 0 },
		{ -2, -1, -2 }, { -2, -1, -1 }, { -2, -1, 0 },
		{ -2, 0, -2 }, { -2, 0, -1 }, { -2, 0, 0 },
		{ -2, 1, -2 }, { -2, 1, -1 }, { -2, 1, 0 },
		{ -1, -5, -3 }, { -1, -5, -2 }, { -1, -5, -1 }, { -1, -5, 0 },
		{ -1, -3, -3 }, { -1, -3, -2 }, { -1, -3, -1 }, { -1, -3, 0 }, { -1, -3, 1 },
		{ -1, -1, -2 }, { -1, -1, -1 }, { -1, -1, 0 }, { -1, -1, 1 },
		{ -1, 0, -2 }, { -1, 0, -1 }, { -1, 0, 0 }, { -1, 0, 1 },
		{ -1, 1, -2 }, { -1, 1, -1 }, { -1, 1, 0 }, { -1, 1, 1 },
		{ -1, 3, -1 }, { -1, 3, 0 }, { -1, 3, 1 },
		{ 0, -5, -2 }, { 0, -5, -1 }, { 0, -5, 0 },
		{ 0, -3, -2 }, { 0, -3, -1 }, { 0, -3, 0 }, { 0, -3, 1 },
		{ 0, -1, -2 }, { 0, -1, -1 }, { 0, -1, 0 }, { 0, -1, 1 }, { 0, -1, 2 },
		{ 0, 0, -2 }, { 0, 0, -1 }, { 0, 0, 1 }, { 0, 0, 2 },
		{ 0, 1, -2 }, { 0, 1, -1 }, { 0, 1, 0 }, { 0, 1, 1 }, { 0, 1, 2 },
		{ 0, 3, -1 }, { 0, 3, 0 }, { 0, 3, 1 }, { 0, 3, 2 },
		{ 0, 5, 0 }, { 0, 5, 1 }, { 0, 5, 2 },
		{ 1, -3, -1 }, { 1, -3, 0 }, { 1, -3, 1 },
		{ 1, -1, -1 }, { 1, -1, 0 }, { 1, -1, 1 }, { 1, -1, 2 },
		{ 1, 0, -1 }, { 1, 0, 0 }, { 1, 0, 1 }, { 1, 0, 2 },
		{ 1, 1, -1 }, { 1, 1, 0 }, { 1, 1, 1 }, { 1, 1, 2 },
		{ 1, 3, -1 }, { 1, 3, 0 }, { 1, 3, 1 }, { 1, 3, 2 }, { 1, 3, 3 },
		{ 1, 5, 0 }, { 1, 5, 1 }, { 1, 5, 2 }, { 1, 5, 3 },
		{ 2, -1, 0 }, { 2, -1, 1 }, { 2, -1, 2 },
		{ 2, 0, 0 }, { 2, 0, 1 }, { 2, 0, 2 },
		{ 2, 1, 0 }, { 2, 1, 1 }, { 2, 1, 2 },
		{ 2, 3, 0 }, { 2, 3, 1 }, { 2, 3, 2 }, { 2, 3, 3 },
		{ 2, 5, 0 }, { 2, 5, 1 }, { 2, 5, 2 }, { 2, 5, 3 },
		{ 3, 3, 1 }, { 3, 3, 2 }, { 3, 3, 3 },
		{ 3, 5, 1 }, { 3, 5, 2 }, { 3, 5, 3 },
	};

	/// <summary>
	/// A body is big-endian 32-bit words, each read from its low bit up, with zeros past its end.
	/// Words are fetched into a 64-bit window as the reader reaches them, off the caller's bytes.
	/// </summary>
	private ref struct BitReader
	{
		private readonly ReadOnlySpan<byte> _data;
		private ulong _window;
		private int _available;
		private int _next;

		public BitReader(byte[] data, int offset, int length)
		{
			if (offset < 0 || (long)offset + length > data.Length)
			{
				throw new IndexOutOfRangeException();
			}

			_data = new ReadOnlySpan<byte>(data, offset, length);
			_window = 0;
			_available = 0;
			_next = 0;
		}

		public readonly long CurrentBit => ((long)_next * 8) - _available;

		public readonly long TotalBits => (long)_data.Length * 8;

		private static uint LastWord(ReadOnlySpan<byte> data, int at)
		{
			uint word = 0;
			for (int k = 0; at + k < data.Length; ++k)
			{
				word |= (uint)data[at + k] << (24 - (8 * k));
			}

			return word;
		}

		/// <summary>Up to 32 bits; the window holds at least that many after a refill.</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public uint Peek(int n)
		{
			if (_available < 32)
			{
				int at = _next;
				_next = at + 4;
				uint word = at + 4 <= _data.Length
					? BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(at, 4))
					: LastWord(_data, at);
				_window |= (ulong)word << _available;
				_available += 32;
			}

			return (uint)(_window & ((1UL << n) - 1UL));
		}

		/// <summary>Only after a <see cref="Peek"/> of at least <paramref name="n"/> bits.</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Skip(int n)
		{
			_window >>= n;
			_available -= n;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public uint Read(int n)
		{
			uint value = Peek(n);
			Skip(n);
			return value;
		}
	}

	private sealed class HuffTree
	{
		private const int TableBits = 11;

		private const ushort TableSlow = 0;

		private uint[] _freq = Array.Empty<uint>();
		private int[] _sym = Array.Empty<int>();
		private int[] _left = Array.Empty<int>();
		private int[] _right = Array.Empty<int>();
		private int[] _heap = Array.Empty<int>();
		private int _count;
		private int _heapSize;

		private readonly ushort[] _table = new ushort[1 << TableBits];

		public int Root { get; private set; } = -1;

		private void Fill(int node, uint code, int length)
		{
			if (node < 0)
			{
				return;
			}

			ushort[] table = _table;
			if (_sym[node] >= 0)
			{
				if (length <= TableBits)
				{
					// Symbol in the low 10 bits (Fable 3's alpha C table has 512 symbols),
					// code length in the next four, the flag on top.
					var entry = (ushort)(0x8000 | (length << 10) | (_sym[node] & 0x3FF));
					uint step = 1u << length;
					for (uint j = code; j < (1u << TableBits); j += step)
					{
						table[j] = entry;
					}
				}
				else
				{
					table[code & ((1u << TableBits) - 1u)] = TableSlow;
				}

				return;
			}

			if (length == TableBits)
			{
				table[code] = TableSlow;
				return;
			}

			Fill(_left[node], code, length + 1);
			Fill(_right[node], code | (1u << length), length + 1);
		}

		private int Alloc(uint freq, int sym, int left, int right)
		{
			int i = _count++;
			_freq[i] = freq;
			_sym[i] = sym;
			_left[i] = left;
			_right[i] = right;
			return i;
		}

		private void Push(int node)
		{
			int[] heap = _heap;
			uint[] freq = _freq;
			heap[_heapSize] = node;
			int i = _heapSize++;
			while (i > 0)
			{
				int p = (i - 1) >> 1;
				if (freq[heap[p]] < freq[heap[i]])
				{
					break;
				}

				(heap[p], heap[i]) = (heap[i], heap[p]);
				i = p;
			}
		}

		private int Pop()
		{
			int[] heap = _heap;
			uint[] freq = _freq;
			int result = heap[0];
			int heapSize = --_heapSize;
			int last = heap[heapSize];
			if (heapSize > 0)
			{
				heap[0] = last;
				int i = 0;
				while (true)
				{
					int l = 2 * i + 1;
					if (l >= heapSize)
					{
						break;
					}

					int rr = l + 1;
					int s = rr >= heapSize
						? l
						: (freq[heap[rr]] >= freq[heap[l]] ? l : rr);
					if (freq[heap[i]] < freq[heap[s]])
					{
						break;
					}

					(heap[i], heap[s]) = (heap[s], heap[i]);
					i = s;
				}
			}

			return result;
		}

		/// <summary>
		/// Rebuilds this tree in the arrays it already has.
		/// <paramref name="skipZero"/>: Fable 3 leaves zero-frequency symbols out of the heap.
		/// </summary>
		public void Build(ReadOnlySpan<uint> freqs, bool skipZero)
		{
			int count = freqs.Length;
			int capacity = count * 2 + 1;
			if (_freq.Length < capacity)
			{
				_freq = new uint[capacity];
				_sym = new int[capacity];
				_left = new int[capacity];
				_right = new int[capacity];
				_heap = new int[capacity];
			}

			_count = 0;
			_heapSize = 0;

			for (int s = 0; s < count; ++s)
			{
				if (skipZero && freqs[s] == 0)
				{
					continue;
				}

				Push(Alloc(freqs[s], s, -1, -1));
			}

			while (_heapSize > 1)
			{
				int a = Pop();
				int b = Pop();
				Push(Alloc(_freq[a] + _freq[b], -1, a, b));
			}

			Root = _heapSize > 0 ? Pop() : -1;
			Array.Clear(_table);
			if (Root >= 0)
			{
				Fill(Root, 0, 0);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int Decode(ref BitReader reader)
		{
			ushort entry = _table[reader.Peek(TableBits)];
			if (entry != TableSlow)
			{
				reader.Skip((entry >> 10) & 0xF);
				return entry & 0x3FF;
			}

			// By value, so that a block loop's reader can stay in registers.
			reader = DecodeSlow(reader, out int symbol);
			return symbol;
		}

		private BitReader DecodeSlow(BitReader reader, out int symbol)
		{
			int n = Root;
			while (n >= 0 && _sym[n] < 0)
			{
				n = reader.Read(1) != 0 ? _right[n] : _left[n];
			}

			symbol = n >= 0 ? _sym[n] : -1;
			return reader;
		}
	}

	/// <summary>One decoding thread's trees and frequency table, kept between bodies: a level needs three or four.</summary>
	private sealed class Scratch
	{
		public readonly HuffTree Tree0 = new();
		public readonly HuffTree Tree1 = new();
		public readonly HuffTree Tree2 = new();
		public readonly HuffTree Tree3 = new();
		public readonly uint[] Freq = new uint[512];

		[ThreadStatic]
		private static Scratch? _current;

		public static Scratch Current => _current ??= new Scratch();
	}

	private static byte[] RentBlocks(long bytes) =>
		bytes > Array.MaxLength ? new byte[bytes] : ArrayPool<byte>.Shared.Rent((int)bytes);

	private static void Return(byte[] blocks)
	{
		if (blocks.Length != 0)
		{
			ArrayPool<byte>.Shared.Return(blocks);
		}
	}

	/// <summary>An exact-size copy of a pooled result, for the callers that keep the array.</summary>
	private static byte[] Keep(byte[] pooled, int length)
	{
		if (pooled.Length == 0)
		{
			return pooled;
		}

		byte[] kept = pooled.AsSpan(0, length).ToArray();
		ArrayPool<byte>.Shared.Return(pooled);
		return kept;
	}

	private static readonly int[] OpFlat = Flatten(OpTable);
	private static readonly int[] DeltaFlat = Flatten(DeltaTable);

	private static int[] Flatten(int[,] table)
	{
		int rows = table.GetLength(0);
		var flat = new int[rows * 3];
		for (int r = 0; r < rows; ++r)
		{
			flat[r * 3] = table[r, 0];
			flat[(r * 3) + 1] = table[r, 1];
			flat[(r * 3) + 2] = table[r, 2];
		}

		return flat;
	}

	private static readonly byte[] Dq6To8 = BuildDq(64, 63);
	private static readonly byte[] Dq4To8 = BuildDq(16, 15);

	private static byte[] BuildDq(int count, int max)
	{
		var table = new byte[count];
		for (int v = 0; v < count; ++v)
		{
			table[v] = (byte)((v * 255 + (max >> 1)) / max);
		}

		return table;
	}

	private static int Clamp6(int v) => v < 0 ? 0 : v > 0x3F ? 0x3F : v;

	public static bool TryDecodeVariant(
		byte[] body,
		int offset,
		int length,
		int mode,
		int width,
		int height,
		out byte[] blocks,
		out string error) =>
		TryDecodeVariant(body, offset, length, mode, width, height, TexScheme.Auto, out blocks, out error);

	/// <summary>
	/// <paramref name="scheme"/> <see cref="TexScheme.Auto"/> tries Fable 2's layout and then
	/// Fable 3's; a caller that decoded the colour plane passes what it learnt there instead.
	/// </summary>
	public static bool TryDecodeVariant(
		byte[] body,
		int offset,
		int length,
		int mode,
		int width,
		int height,
		TexScheme scheme,
		out byte[] blocks,
		out string error)
	{
		bool ok = TryDecodeVariantPooled(body, offset, length, mode, width, height, scheme, out byte[] pooled, out error);
		blocks = Keep(pooled, (width / 4) * (height / 4) * 8);
		return ok;
	}

	/// <summary>The same, with <paramref name="blocks"/> from the shared pool for the caller to return; width / 4 * height / 4 * 8 bytes of it are the level.</summary>
	internal static bool TryDecodeVariantPooled(
		byte[] body,
		int offset,
		int length,
		int mode,
		int width,
		int height,
		TexScheme scheme,
		out byte[] blocks,
		out string error)
	{
		if (scheme != TexScheme.Auto)
		{
			return DecodeVariant(body, offset, length, mode, width, height, scheme, out blocks, out error, out _, out _);
		}

		bool fable2Ok = DecodeVariant(body, offset, length, mode, width, height, TexScheme.Fable2, out blocks, out error, out long consumed, out long total);
		if (fable2Ok && EndsAtBody(consumed, total))
		{
			return true;
		}

		string fable2Error = fable2Ok ? $"stream ended {total - consumed} bits short" : error;
		byte[] fable2Blocks = blocks;
		bool fable3Ok = DecodeVariant(body, offset, length, mode, width, height, TexScheme.Fable3, out blocks, out error, out consumed, out total);
		if (fable3Ok && EndsAtBody(consumed, total))
		{
			Return(fable2Blocks);
			return true;
		}

		if (fable2Ok)
		{
			Return(blocks);
			blocks = fable2Blocks;
			error = string.Empty;
			return true;
		}

		if (fable3Ok)
		{
			error = string.Empty;
			return true;
		}

		error = $"as Fable 2: {fable2Error}; as Fable 3: {error}";
		return false;
	}

	private static bool DecodeVariant(
		byte[] body,
		int offset,
		int length,
		int mode,
		int width,
		int height,
		TexScheme scheme,
		out byte[] blocks,
		out string error,
		out long bitsConsumed,
		out long bitsTotal)
	{
		blocks = Array.Empty<byte>();
		error = string.Empty;
		bitsConsumed = 0;
		bitsTotal = (long)length * 8;
		bool fable3 = scheme == TexScheme.Fable3;

		if (length < 32)
		{
			error = "variant: body too small";
			return false;
		}

		if (width <= 0 || height <= 0 || width % 4 != 0 || height % 4 != 0)
		{
			error = $"variant: invalid dimensions {width}x{height}";
			return false;
		}

		var reader = new BitReader(body, offset, length);
		reader.Read(16);
		reader.Read(16);
		int modeFlag = (int)reader.Read(4);
		reader.Read(8);

		Scratch scratch = Scratch.Current;
		HuffTree treeA = scratch.Tree0;
		HuffTree treeB = scratch.Tree1;
		HuffTree treeC = scratch.Tree2;
		HuffTree treeOp = scratch.Tree3;
		ReadTree(ref reader, treeA, scratch.Freq.AsSpan(0, 64), scheme);
		ReadTree(ref reader, treeB, scratch.Freq.AsSpan(0, 32), scheme);
		ReadTree(ref reader, treeC, scratch.Freq.AsSpan(0, fable3 ? 512 : 64), scheme);
		ReadTree(ref reader, treeOp, scratch.Freq.AsSpan(0, 32), scheme);

		// Fable 3 leaves an unused table empty (no leaves at all); decoding from it is the error.
		if (treeOp.Root < 0 || (!fable3 && (treeA.Root < 0 || treeB.Root < 0 || treeC.Root < 0)))
		{
			error = "variant: tree build failed";
			return false;
		}

		int bxCount = width / 4;
		int byCount = height / 4;
		blocks = RentBlocks((long)bxCount * byCount * 8);

		if (!DecodeVariantBlocks(ref reader, treeA, treeB, treeC, treeOp, mode, modeFlag, fable3, bxCount, byCount, blocks, out error))
		{
			Return(blocks);
			blocks = Array.Empty<byte>();
			return false;
		}

		bitsConsumed = reader.CurrentBit;
		if (reader.CurrentBit > reader.TotalBits + 32)
		{
			error = $"variant: ran past end of body: bit={reader.CurrentBit} of {reader.TotalBits}";
			Return(blocks);
			blocks = Array.Empty<byte>();
			return false;
		}

		return true;
	}

	private static bool DecodeVariantBlocks(
		ref BitReader shared,
		HuffTree treeA,
		HuffTree treeB,
		HuffTree treeC,
		HuffTree treeOp,
		int mode,
		int modeFlag,
		bool fable3,
		int bxCount,
		int byCount,
		byte[] blocks,
		out string error)
	{
		error = string.Empty;
		BitReader reader = shared;
		Span<ulong> output = MemoryMarshal.Cast<byte, ulong>(blocks.AsSpan(0, bxCount * byCount * 8));

		int runRemaining = 0;
		int lastOpType = -1;
		byte lastV20 = 0;
		// Fable 3's five C symbols per block. Here, not in the block loop: a stackalloc is freed only
		// when the method returns, so one per block grew the stack ~20 bytes a block until a large
		// terrain texture overflowed a thread pool worker's stack (1 MB on Android).
		Span<int> sym = stackalloc int[5];
		Span<int> ramp = stackalloc int[16];

		int at = 0;
		for (int by = 0; by < byCount; ++by)
		{
			for (int bx = 0; bx < bxCount; ++bx, ++at)
			{
				int opType;
				byte v20 = 0;

				if (runRemaining > 0)
				{
					--runRemaining;
					opType = lastOpType;
					v20 = lastV20;
				}
				else
				{
					int opSym = treeOp.Decode(ref reader);
					if (opSym < 0)
					{
						error = "variant: op tree decode failed";
						return false;
					}

					int count = opSym & 7;
					opType = opSym >> 3;
					int extra = count > 0 ? (int)reader.Read(count) : 0;
					runRemaining = (1 << count) + extra - 1;

					if (opType == 2)
					{
						int bits = modeFlag == 1 ? (int)reader.Read(4) : (int)reader.Read(8);
						if (mode == 1)
						{
							v20 = (byte)((bits & 0xF) | ((bits & 0xF) << 4));
						}
						else if (modeFlag == 1)
						{
							v20 = Dq4To8[bits & 0xF];
						}
						else
						{
							v20 = (byte)(bits & 0xFF);
						}
					}

					lastOpType = opType;
					lastV20 = v20;
				}

				switch (opType)
				{
					case 0:
						output[at] = 0;
						break;

					case 1:
						output[at] = ulong.MaxValue;
						break;

					case 2:
						output[at] = mode == 2
							? v20 * 0x0101UL
							: v20 * 0x0101010101010101UL;
						break;

					case 3:
					{
						int symA = treeA.Decode(ref reader);
						int symB = treeB.Decode(ref reader);
						if (symA < 0 || symB < 0)
						{
							error = "variant: A/B tree decode failed";
							return false;
						}

						int a0 = Dq6To8[Clamp6(symA + symB)];
						int a1 = Dq6To8[Clamp6(symA - symB)];

						// Sixteen 3-bit indices, first texel lowest.
						ulong packed = 0;
						if (fable3)
						{
							// FABLE 3 (sub_82D6DB58, 0x82d6e0a0..0x82d6e218): three raw bits start
							// column 0; the first C symbol's three 3-bit deltas walk it down; the next
							// four C symbols walk each row left to right; every step is mod 8. The
							// result is a ramp level 0 (lo) .. 7 (hi), not a BC3 index: the BC3 path
							// maps it through byte_820EB2B0. When lo == hi the indices stay 0, but the
							// symbols are consumed all the same.
							int raw = (int)reader.Read(3);
							for (int k = 0; k < 5; ++k)
							{
								sym[k] = treeC.Decode(ref reader);
								if (sym[k] < 0)
								{
									error = "variant: C tree decode failed";
									return false;
								}
							}

							static int Delta(int symbol, int i) => (symbol >> (3 * i)) & 7;
							int column = raw;
							for (int y = 0; y < 4; ++y)
							{
								if (y > 0)
								{
									column = (column + Delta(sym[0], y - 1)) & 7;
								}

								ramp[y * 4] = column;
								for (int x = 1; x < 4; ++x)
								{
									ramp[y * 4 + x] = (ramp[y * 4 + x - 1] + Delta(sym[y + 1], x - 1)) & 7;
								}
							}

							if (a0 != a1)
							{
								for (int i = 0; i < 16; ++i)
								{
									packed |= (ulong)RampToBc3Index[ramp[i]] << (i * 3);
								}
							}
						}
						else
						{
							for (int iter = 0; iter < 4; ++iter)
							{
								int c1 = treeC.Decode(ref reader);
								int c2 = treeC.Decode(ref reader);
								if (c1 < 0 || c2 < 0)
								{
									error = "variant: C tree decode failed";
									return false;
								}

								// Each symbol holds two indices, the first in its low bits.
								packed |= (ulong)(uint)((c1 & 0x3F) | ((c2 & 0x3F) << 6)) << (iter * 12);
							}
						}

						output[at] = mode == 2
							? (uint)a0 | ((ulong)(uint)a1 << 8) | (packed << 16)
							: (uint)a0 | ((ulong)(uint)a1 << 8);
						break;
					}

					default:
						error = $"variant: invalid op_type {opType}";
						return false;
				}
			}
		}

		shared = reader;
		return true;
	}

	private static uint DecodeFreqByte(byte b) => (uint)(b & 0x0F) << ((b >> 4) & 0x0F);

	/// <summary>
	/// Which game wrote the body. FABLE 3 changed two things in every codec here, read from its
	/// binary (frequency reader sub_82D6D338, tree build sub_82D6CAC0): a frequency is a
	/// variable-length field instead of a byte — <c>0</c> → 0; <c>1 0 mmmm</c> → m;
	/// <c>1 1 mmmm eeee</c> → m &lt;&lt; e — and zero-frequency symbols are not pushed into the
	/// Huffman heap, which changes the tree shape. The alpha plane also grew its C table from 64
	/// to 512 symbols and recoded op 3 (see <see cref="TryDecodeVariant"/>). The record chain
	/// around the bodies is unchanged, so a caller that does not know asks for
	/// <see cref="TexScheme.Auto"/> and the colour plane's decode tells it which one held.
	/// </summary>
	public enum TexScheme
	{
		Auto,
		Fable2,
		Fable3,
	}

	private static void ReadFrequencies(ref BitReader reader, Span<uint> freqs, TexScheme scheme)
	{
		for (int i = 0; i < freqs.Length; ++i)
		{
			if (scheme == TexScheme.Fable2)
			{
				freqs[i] = DecodeFreqByte((byte)reader.Read(8));
			}
			else if (reader.Read(1) == 0)
			{
				freqs[i] = 0;
			}
			else if (reader.Read(1) == 0)
			{
				freqs[i] = reader.Read(4);
			}
			else
			{
				uint m = reader.Read(4);
				int e = (int)reader.Read(4);
				freqs[i] = m << e;
			}
		}
	}

	/// <summary>A body's next frequency table and the tree it describes.</summary>
	private static void ReadTree(ref BitReader reader, HuffTree tree, Span<uint> freqs, TexScheme scheme)
	{
		ReadFrequencies(ref reader, freqs, scheme);
		tree.Build(freqs, scheme == TexScheme.Fable3);
	}

	/// <summary>Fable 3's BC3 index for a monotone ramp level 0 (lo) .. 7 (hi): byte_820EB2B0.</summary>
	private static readonly byte[] RampToBc3Index = { 1, 7, 6, 5, 4, 3, 2, 0 };

	private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

	private static ushort ApplyDelta(ushort c565, int dr, int dg, int db)
	{
		int r = Clamp(((c565 >> 11) & 0x1F) + dr, 0, 31);
		int g = Clamp(((c565 >> 5) & 0x3F) + dg, 0, 63);
		int b = Clamp((c565 & 0x1F) + db, 0, 31);
		return (ushort)((r << 11) | (g << 5) | b);
	}

	public static bool TryDecodeCompressedMip(
		byte[] body,
		int offset,
		int length,
		bool comp11Layout,
		out int width,
		out int height,
		out byte[] blocks,
		out string error)
	{
		return TryDecodeCompressedMip(body, offset, length, comp11Layout,
			out width, out height, out blocks, out _, out _, out error);
	}

	public static bool TryDecodeCompressedMip(
		byte[] body,
		int offset,
		int length,
		bool comp11Layout,
		out int width,
		out int height,
		out byte[] blocks,
		out long bitsConsumed,
		out long bitsTotal,
		out string error) =>
		TryDecodeCompressedMip(body, offset, length, comp11Layout, TexScheme.Auto, out width, out height, out blocks,
			out bitsConsumed, out bitsTotal, out _, out error);

	/// <summary>
	/// <paramref name="scheme"/> <see cref="TexScheme.Auto"/> tries Fable 2's layout and then
	/// Fable 3's; <paramref name="used"/> says which one decoded, for the alpha plane beside it.
	/// </summary>
	public static bool TryDecodeCompressedMip(
		byte[] body,
		int offset,
		int length,
		bool comp11Layout,
		TexScheme scheme,
		out int width,
		out int height,
		out byte[] blocks,
		out long bitsConsumed,
		out long bitsTotal,
		out TexScheme used,
		out string error)
	{
		bool ok = TryDecodeCompressedMipPooled(body, offset, length, comp11Layout, scheme, out width, out height,
			out byte[] pooled, out bitsConsumed, out bitsTotal, out used, out error);
		blocks = Keep(pooled, (width / 4) * (height / 4) * 8);
		return ok;
	}

	/// <summary>The same, with <paramref name="blocks"/> from the shared pool for the caller to return; width / 4 * height / 4 * 8 bytes of it are the level.</summary>
	internal static bool TryDecodeCompressedMipPooled(
		byte[] body,
		int offset,
		int length,
		bool comp11Layout,
		TexScheme scheme,
		out int width,
		out int height,
		out byte[] blocks,
		out long bitsConsumed,
		out long bitsTotal,
		out TexScheme used,
		out string error)
	{
		if (scheme != TexScheme.Auto)
		{
			used = scheme;
			return DecodeCompressedMip(body, offset, length, comp11Layout, scheme, out width, out height, out blocks,
				out bitsConsumed, out bitsTotal, out error);
		}

		// A real stream ends inside the body's last padding word; a wrong scheme that happens not
		// to overrun still stops far short of it, so the slack decides, not success alone.
		used = TexScheme.Fable2;
		bool fable2Ok = DecodeCompressedMip(body, offset, length, comp11Layout, TexScheme.Fable2, out width, out height,
			out blocks, out bitsConsumed, out bitsTotal, out error);
		if (fable2Ok && EndsAtBody(bitsConsumed, bitsTotal))
		{
			return true;
		}

		string fable2Error = fable2Ok ? $"stream ended {bitsTotal - bitsConsumed} bits short" : error;
		int fable2Width = width;
		int fable2Height = height;
		byte[] fable2Blocks = blocks;
		used = TexScheme.Fable3;
		bool fable3Ok = DecodeCompressedMip(body, offset, length, comp11Layout, TexScheme.Fable3, out width, out height,
			out blocks, out bitsConsumed, out bitsTotal, out error);
		if (fable3Ok && EndsAtBody(bitsConsumed, bitsTotal))
		{
			Return(fable2Blocks);
			return true;
		}

		if (fable2Ok)
		{
			// Neither scheme lands on the end; keep Fable 2's reading, as before this detection.
			Return(blocks);
			used = TexScheme.Fable2;
			width = fable2Width;
			height = fable2Height;
			blocks = fable2Blocks;
			error = string.Empty;
			return true;
		}

		if (fable3Ok)
		{
			error = string.Empty;
			return true;
		}

		error = $"as Fable 2: {fable2Error}; as Fable 3: {error}";
		return false;
	}

	/// <summary>Both games' streams finish within the last padding word; 64 bits of slack covers it.</summary>
	private static bool EndsAtBody(long consumed, long total) => consumed <= total + 32 && total - consumed < 64;

	private static bool DecodeCompressedMip(
		byte[] body,
		int offset,
		int length,
		bool comp11Layout,
		TexScheme scheme,
		out int width,
		out int height,
		out byte[] blocks,
		out long bitsConsumed,
		out long bitsTotal,
		out string error)
	{
		bool fable3 = scheme == TexScheme.Fable3;
		bitsConsumed = 0;
		bitsTotal = (long)length * 8;
		width = 0;
		height = 0;
		blocks = Array.Empty<byte>();
		error = string.Empty;
		// Fable 2's tables alone are 440 bytes; Fable 3's are variable-length and can be far shorter.
		if (length < (fable3 ? 8 : 444))
		{
			error = $"body too small: {length}";
			return false;
		}

		var reader = new BitReader(body, offset, length);
		int mw = (int)reader.Read(16);
		int mh = (int)reader.Read(16);

		if (mw <= 0 || mh <= 0 || mw > 8192 || mh > 8192)
		{
			error = $"implausible mip dimensions: {mw}x{mh}";
			return false;
		}

		if (mw % 4 != 0 || mh % 4 != 0)
		{
			error = $"mip dimensions not 4-aligned: {mw}x{mh}";
			return false;
		}

		width = mw;
		height = mh;

		Scratch scratch = Scratch.Current;
		HuffTree treeIdx = scratch.Tree0;
		HuffTree treeOp = scratch.Tree1;
		HuffTree treeDel = scratch.Tree2;
		ReadTree(ref reader, treeIdx, scratch.Freq.AsSpan(0, 256), scheme);
		ReadTree(ref reader, treeOp, scratch.Freq.AsSpan(0, 62), scheme);
		ReadTree(ref reader, treeDel, scratch.Freq.AsSpan(0, 122), scheme);

		// Fable 3 leaves an unused table empty (no leaves at all); decoding from it is the error.
		if (treeOp.Root < 0 || (!fable3 && (treeIdx.Root < 0 || treeDel.Root < 0)))
		{
			error = "Huffman tree build failed";
			return false;
		}

		int bw = mw / 4;
		int bh = mh / 4;
		blocks = RentBlocks((long)bw * bh * 8);

		if (!DecodeCompressedBlocks(ref reader, treeIdx, treeOp, treeDel, comp11Layout, bw, bh, blocks, out error))
		{
			Return(blocks);
			blocks = Array.Empty<byte>();
			return false;
		}

		bitsConsumed = reader.CurrentBit;
		if (reader.CurrentBit > reader.TotalBits + 32)
		{
			error = $"ran past end of body: bit={reader.CurrentBit} of {reader.TotalBits}";
			Return(blocks);
			blocks = Array.Empty<byte>();
			return false;
		}

		return true;
	}

	private static bool DecodeCompressedBlocks(
		ref BitReader shared,
		HuffTree treeIdx,
		HuffTree treeOp,
		HuffTree treeDel,
		bool comp11Layout,
		int bw,
		int bh,
		byte[] blocks,
		out string error)
	{
		error = string.Empty;
		BitReader reader = shared;
		Span<ulong> output = MemoryMarshal.Cast<byte, ulong>(blocks.AsSpan(0, bw * bh * 8));
		int[] opFlat = OpFlat;
		int[] deltaFlat = DeltaFlat;

		int at = 0;
		for (int by = 0; by < bh; ++by)
		{
			for (int bx = 0; bx < bw; ++bx, ++at)
			{
				int opIdx = treeOp.Decode(ref reader);
				if (opIdx < 0 || opIdx >= 62)
				{
					error = $"op_idx out of range at block ({bx},{by}): {opIdx}";
					return false;
				}

				int op = opFlat[opIdx * 3];
				int dx = opFlat[(opIdx * 3) + 1];
				int dy = opFlat[(opIdx * 3) + 2];

				ushort c0;
				ushort c1;
				if (op == 4)
				{
					c0 = (ushort)reader.Read(16);
					c1 = (ushort)reader.Read(16);
				}
				else
				{
					int rx = bx + dx;
					int ry = by + dy;
					if (rx < 0 || ry < 0 || rx >= bw || ry >= bh)
					{
						c0 = 0;
						c1 = 0;
					}
					else
					{
						ulong reference = output[(ry * bw) + rx];
						c0 = (ushort)reference;
						c1 = (ushort)(reference >> 16);
					}

					if (op == 1 || op == 3)
					{
						int d = treeDel.Decode(ref reader);
						if (d < 0 || d >= 122)
						{
							error = $"delta idx out of range (c0) at block ({bx},{by}): {d}";
							return false;
						}

						c0 = ApplyDelta(c0, deltaFlat[d * 3], deltaFlat[(d * 3) + 1], deltaFlat[(d * 3) + 2]);
					}

					if (op == 2 || op == 3)
					{
						int d = treeDel.Decode(ref reader);
						if (d < 0 || d >= 122)
						{
							error = $"delta idx out of range (c1) at block ({bx},{by}): {d}";
							return false;
						}

						c1 = ApplyDelta(c1, deltaFlat[d * 3], deltaFlat[(d * 3) + 1], deltaFlat[(d * 3) + 2]);
					}

					if (!comp11Layout && c0 < c1)
					{
						(c0, c1) = (c1, c0);
					}
				}

				ulong block = c0 | ((ulong)c1 << 16);

				if (c0 != c1 || comp11Layout)
				{
					int b0 = treeIdx.Decode(ref reader);
					int b1 = treeIdx.Decode(ref reader);
					int b2 = treeIdx.Decode(ref reader);
					int b3 = treeIdx.Decode(ref reader);
					if (b0 < 0 || b1 < 0 || b2 < 0 || b3 < 0)
					{
						error = $"idx decode failed at block ({bx},{by})";
						return false;
					}

					uint indices = (uint)(b0 & 0x0F) | ((uint)(b1 & 0x0F) << 4) |
						((uint)(b0 & 0xF0) << 4) | ((uint)(b1 & 0xF0) << 8) |
						((uint)(b2 & 0x0F) << 16) | ((uint)(b3 & 0x0F) << 20) |
						((uint)(b2 & 0xF0) << 20) | ((uint)(b3 & 0xF0) << 24);
					block |= (ulong)indices << 32;
				}

				output[at] = block;
			}
		}

		shared = reader;
		return true;
	}
}
