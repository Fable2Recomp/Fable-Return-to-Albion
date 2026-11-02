using System;

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

	private sealed class BitReader
	{
		private readonly uint[] _words;

		public BitReader(byte[] data, int offset, int length)
		{
			int wordCount = (length + 3) / 4;
			_words = new uint[wordCount + 2];

			int i = 0;
			int b = offset;
			int end = offset + length;
			for (; b + 4 <= end; b += 4, ++i)
			{
				_words[i] = ((uint)data[b] << 24) | ((uint)data[b + 1] << 16) |
							((uint)data[b + 2] << 8) | data[b + 3];
			}

			if (b < end)
			{
				uint partial = 0;
				for (int k = 0; b + k < end; ++k)
				{
					partial |= (uint)data[b + k] << (24 - 8 * k);
				}

				_words[i] = partial;
			}

			TotalBits = (long)length * 8;
		}

		public long CurrentBit { get; private set; }

		public long TotalBits { get; }

		[System.Runtime.CompilerServices.MethodImpl(
			System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		public uint Peek(int n)
		{
			int bitOff = (int)(CurrentBit & 31);
			long w = CurrentBit >> 5;
			uint[] words = _words;
			ulong lo = w < words.Length ? words[w] : 0u;
			ulong hi = w + 1 < words.Length ? words[w + 1] : 0u;
			uint value = (uint)(((hi << 32) | lo) >> bitOff);
			return n == 32 ? value : value & ((1u << n) - 1u);
		}

		[System.Runtime.CompilerServices.MethodImpl(
			System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		public void Skip(int n) => CurrentBit += n;

		[System.Runtime.CompilerServices.MethodImpl(
			System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		public uint Read(int n)
		{
			uint value = Peek(n);
			CurrentBit += n;
			return value;
		}
	}

	private sealed class HuffTree
	{
		private const int TableBits = 11;

		private const ushort TableSlow = 0;

		private readonly uint[] _freq;
		private readonly int[] _sym;
		private readonly int[] _left;
		private readonly int[] _right;
		private int _count;

		private ushort[]? _table;

		public HuffTree(int capacity)
		{
			_freq = new uint[capacity];
			_sym = new int[capacity];
			_left = new int[capacity];
			_right = new int[capacity];
		}

		public int Root { get; private set; } = -1;

		private void BuildTable()
		{
			_table = new ushort[1 << TableBits];
			if (Root >= 0)
			{
				Fill(Root, 0, 0);
			}
		}

		private void Fill(int node, uint code, int length)
		{
			if (node < 0)
			{
				return;
			}

			ushort[] table = _table!;
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

		public static HuffTree Build(uint[] freqs) => Build(freqs, false);

		/// <summary><paramref name="skipZero"/>: Fable 3 leaves zero-frequency symbols out of the heap.</summary>
		public static HuffTree Build(uint[] freqs, bool skipZero)
		{
			int count = freqs.Length;
			var tree = new HuffTree(count * 2 + 1);
			var heap = new int[count * 2 + 1];
			int heapSize = 0;

			void Push(int node)
			{
				heap[heapSize] = node;
				int i = heapSize++;
				while (i > 0)
				{
					int p = (i - 1) >> 1;
					if (tree._freq[heap[p]] < tree._freq[heap[i]])
					{
						break;
					}

					(heap[p], heap[i]) = (heap[i], heap[p]);
					i = p;
				}
			}

			int Pop()
			{
				int result = heap[0];
				int last = heap[--heapSize];
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
							: (tree._freq[heap[rr]] >= tree._freq[heap[l]] ? l : rr);
						if (tree._freq[heap[i]] < tree._freq[heap[s]])
						{
							break;
						}

						(heap[i], heap[s]) = (heap[s], heap[i]);
						i = s;
					}
				}

				return result;
			}

			for (int s = 0; s < count; ++s)
			{
				if (skipZero && freqs[s] == 0)
				{
					continue;
				}

				Push(tree.Alloc(freqs[s], s, -1, -1));
			}

			while (heapSize > 1)
			{
				int a = Pop();
				int b = Pop();
				Push(tree.Alloc(tree._freq[a] + tree._freq[b], -1, a, b));
			}

			tree.Root = heapSize > 0 ? Pop() : -1;
			tree.BuildTable();
			return tree;
		}

		[System.Runtime.CompilerServices.MethodImpl(
			System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
		public int Decode(BitReader reader)
		{
			ushort entry = _table![reader.Peek(TableBits)];
			if (entry != TableSlow)
			{
				reader.Skip((entry >> 10) & 0xF);
				return entry & 0x3FF;
			}

			int n = Root;
			while (n >= 0 && _sym[n] < 0)
			{
				n = reader.Read(1) != 0 ? _right[n] : _left[n];
			}

			return n >= 0 ? _sym[n] : -1;
		}
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
			return true;
		}

		if (fable2Ok)
		{
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

		var freqA = new uint[64];
		var freqB = new uint[32];
		var freqC = new uint[fable3 ? 512 : 64];
		var freqOp = new uint[32];
		ReadFrequencies(reader, freqA, scheme);
		ReadFrequencies(reader, freqB, scheme);
		ReadFrequencies(reader, freqC, scheme);
		ReadFrequencies(reader, freqOp, scheme);

		HuffTree treeA = HuffTree.Build(freqA, fable3);
		HuffTree treeB = HuffTree.Build(freqB, fable3);
		HuffTree treeC = HuffTree.Build(freqC, fable3);
		HuffTree treeOp = HuffTree.Build(freqOp, fable3);

		// Fable 3 leaves an unused table empty (no leaves at all); decoding from it is the error.
		if (treeOp.Root < 0 || (!fable3 && (treeA.Root < 0 || treeB.Root < 0 || treeC.Root < 0)))
		{
			error = "variant: tree build failed";
			return false;
		}

		Span<int> ramp = stackalloc int[16];

		int bxCount = width / 4;
		int byCount = height / 4;
		blocks = new byte[(long)bxCount * byCount * 8];

		int runRemaining = 0;
		int lastOpType = -1;
		byte lastV20 = 0;
		Span<byte> indices = stackalloc byte[16];
		// Fable 3's five C symbols per block. Here, not in the block loop: a stackalloc is freed only
		// when the method returns, so one per block grew the stack ~20 bytes a block until a large
		// terrain texture overflowed a thread pool worker's stack (1 MB on Android).
		Span<int> sym = stackalloc int[5];

		for (int by = 0; by < byCount; ++by)
		{
			for (int bx = 0; bx < bxCount; ++bx)
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
					int opSym = treeOp.Decode(reader);
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

				int o = (by * bxCount + bx) * 8;

				switch (opType)
				{
					case 0:
						for (int i = 0; i < 8; ++i) blocks[o + i] = 0;
						break;

					case 1:
						for (int i = 0; i < 8; ++i) blocks[o + i] = 0xFF;
						break;

					case 2:
						if (mode == 2)
						{
							blocks[o] = v20;
							blocks[o + 1] = v20;
							for (int i = 2; i < 8; ++i) blocks[o + i] = 0;
						}
						else
						{
							for (int i = 0; i < 8; ++i) blocks[o + i] = v20;
						}

						break;

					case 3:
					{
						int symA = treeA.Decode(reader);
						int symB = treeB.Decode(reader);
						if (symA < 0 || symB < 0)
						{
							error = "variant: A/B tree decode failed";
							return false;
						}

						int a0 = Dq6To8[Clamp6(symA + symB)];
						int a1 = Dq6To8[Clamp6(symA - symB)];

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
								sym[k] = treeC.Decode(reader);
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

							for (int i = 0; i < 16; ++i)
							{
								indices[i] = a0 == a1 ? (byte)0 : RampToBc3Index[ramp[i]];
							}
						}
						else
						{
							for (int iter = 0; iter < 4; ++iter)
							{
								int c1 = treeC.Decode(reader);
								int c2 = treeC.Decode(reader);
								if (c1 < 0 || c2 < 0)
								{
									error = "variant: C tree decode failed";
									return false;
								}

								indices[iter * 4] = (byte)(c1 & 7);
								indices[iter * 4 + 1] = (byte)((c1 >> 3) & 7);
								indices[iter * 4 + 2] = (byte)(c2 & 7);
								indices[iter * 4 + 3] = (byte)((c2 >> 3) & 7);
							}
						}

						if (mode == 2)
						{
							blocks[o] = (byte)a0;
							blocks[o + 1] = (byte)a1;
							ulong packed = 0;
							for (int i = 0; i < 16; ++i)
							{
								packed |= (ulong)(indices[i] & 7) << (i * 3);
							}

							blocks[o + 2] = (byte)(packed & 0xFF);
							blocks[o + 3] = (byte)((packed >> 8) & 0xFF);
							blocks[o + 4] = (byte)((packed >> 16) & 0xFF);
							blocks[o + 5] = (byte)((packed >> 24) & 0xFF);
							blocks[o + 6] = (byte)((packed >> 32) & 0xFF);
							blocks[o + 7] = (byte)((packed >> 40) & 0xFF);
						}
						else
						{
							blocks[o] = (byte)a0;
							blocks[o + 1] = (byte)a1;
							for (int i = 2; i < 8; ++i) blocks[o + i] = 0;
						}

						break;
					}

					default:
						error = $"variant: invalid op_type {opType}";
						return false;
				}
			}
		}

		bitsConsumed = reader.CurrentBit;
		if (reader.CurrentBit > reader.TotalBits + 32)
		{
			error = $"variant: ran past end of body: bit={reader.CurrentBit} of {reader.TotalBits}";
			return false;
		}

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

	private static void ReadFrequencies(BitReader reader, uint[] freqs, TexScheme scheme)
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
			return true;
		}

		if (fable2Ok)
		{
			// Neither scheme lands on the end; keep Fable 2's reading, as before this detection.
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

		var freqIdx = new uint[256];
		var freqOp = new uint[62];
		var freqDel = new uint[122];
		ReadFrequencies(reader, freqIdx, scheme);
		ReadFrequencies(reader, freqOp, scheme);
		ReadFrequencies(reader, freqDel, scheme);

		HuffTree treeIdx = HuffTree.Build(freqIdx, fable3);
		HuffTree treeOp = HuffTree.Build(freqOp, fable3);
		HuffTree treeDel = HuffTree.Build(freqDel, fable3);

		// Fable 3 leaves an unused table empty (no leaves at all); decoding from it is the error.
		if (treeOp.Root < 0 || (!fable3 && (treeIdx.Root < 0 || treeDel.Root < 0)))
		{
			error = "Huffman tree build failed";
			return false;
		}

		int bw = mw / 4;
		int bh = mh / 4;
		blocks = new byte[(long)bw * bh * 8];

		for (int by = 0; by < bh; ++by)
		{
			for (int bx = 0; bx < bw; ++bx)
			{
				int opIdx = treeOp.Decode(reader);
				if (opIdx < 0 || opIdx >= 62)
				{
					error = $"op_idx out of range at block ({bx},{by}): {opIdx}";
					return false;
				}

				int op = OpFlat[opIdx * 3];
				int dx = OpFlat[(opIdx * 3) + 1];
				int dy = OpFlat[(opIdx * 3) + 2];

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
						int p = (ry * bw + rx) * 8;
						c0 = (ushort)(blocks[p] | (blocks[p + 1] << 8));
						c1 = (ushort)(blocks[p + 2] | (blocks[p + 3] << 8));
					}

					if (op == 1 || op == 3)
					{
						int d = treeDel.Decode(reader);
						if (d < 0 || d >= 122)
						{
							error = $"delta idx out of range (c0) at block ({bx},{by}): {d}";
							return false;
						}

						c0 = ApplyDelta(c0, DeltaFlat[d * 3], DeltaFlat[(d * 3) + 1], DeltaFlat[(d * 3) + 2]);
					}

					if (op == 2 || op == 3)
					{
						int d = treeDel.Decode(reader);
						if (d < 0 || d >= 122)
						{
							error = $"delta idx out of range (c1) at block ({bx},{by}): {d}";
							return false;
						}

						c1 = ApplyDelta(c1, DeltaFlat[d * 3], DeltaFlat[(d * 3) + 1], DeltaFlat[(d * 3) + 2]);
					}

					if (!comp11Layout && c0 < c1)
					{
						(c0, c1) = (c1, c0);
					}
				}

				int o = (by * bw + bx) * 8;
				blocks[o] = (byte)(c0 & 0xFF);
				blocks[o + 1] = (byte)(c0 >> 8);
				blocks[o + 2] = (byte)(c1 & 0xFF);
				blocks[o + 3] = (byte)(c1 >> 8);

				if (c0 == c1 && !comp11Layout)
				{
					blocks[o + 4] = 0;
					blocks[o + 5] = 0;
					blocks[o + 6] = 0;
					blocks[o + 7] = 0;
				}
				else
				{
					int b0 = treeIdx.Decode(reader);
					int b1 = treeIdx.Decode(reader);
					int b2 = treeIdx.Decode(reader);
					int b3 = treeIdx.Decode(reader);
					if (b0 < 0 || b1 < 0 || b2 < 0 || b3 < 0)
					{
						error = $"idx decode failed at block ({bx},{by})";
						return false;
					}

					blocks[o + 4] = (byte)((b0 & 0x0F) | ((b1 & 0x0F) << 4));
					blocks[o + 5] = (byte)(((b0 >> 4) & 0x0F) | (b1 & 0xF0));
					blocks[o + 6] = (byte)((b2 & 0x0F) | ((b3 & 0x0F) << 4));
					blocks[o + 7] = (byte)(((b2 >> 4) & 0x0F) | (b3 & 0xF0));
				}
			}
		}

		bitsConsumed = reader.CurrentBit;
		if (reader.CurrentBit > reader.TotalBits + 32)
		{
			error = $"ran past end of body: bit={reader.CurrentBit} of {reader.TotalBits}";
			return false;
		}

		return true;
	}
}
