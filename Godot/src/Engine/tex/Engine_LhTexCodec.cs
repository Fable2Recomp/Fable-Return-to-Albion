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
		private readonly byte[] _data;
		private readonly int _bytes;

		public BitReader(byte[] data, int offset, int length)
		{
			_data = data;
			Offset = offset;
			_bytes = length;
		}

		public int Offset { get; }

		public long CurrentBit { get; private set; }

		public long TotalBits => (long)_bytes * 8;

		private uint Word(long index)
		{
			long off = index * 4;
			if (off + 4 > _bytes)
			{
				uint partial = 0;
				for (int i = 0; i < 4 && off + i < _bytes; ++i)
				{
					partial |= (uint)_data[Offset + off + i] << (24 - 8 * i);
				}

				return partial;
			}

			int b = Offset + (int)off;
			return ((uint)_data[b] << 24) | ((uint)_data[b + 1] << 16) |
				   ((uint)_data[b + 2] << 8) | _data[b + 3];
		}

		public uint Read(int n)
		{
			uint mask = n == 32 ? 0xFFFFFFFFu : (1u << n) - 1u;
			int bitOff = (int)(CurrentBit & 31);
			long wordIdx = CurrentBit >> 5;
			uint value;
			if (bitOff + n > 32)
			{
				uint lo = Word(wordIdx);
				uint hi = Word(wordIdx + 1);
				value = ((hi << (32 - bitOff)) | (lo >> bitOff)) & mask;
			}
			else
			{
				value = (Word(wordIdx) >> bitOff) & mask;
			}

			CurrentBit += n;
			return value;
		}
	}

	private sealed class HuffTree
	{
		private readonly uint[] _freq;
		private readonly int[] _sym;
		private readonly int[] _left;
		private readonly int[] _right;
		private int _count;

		public HuffTree(int capacity)
		{
			_freq = new uint[capacity];
			_sym = new int[capacity];
			_left = new int[capacity];
			_right = new int[capacity];
		}

		public int Root { get; private set; } = -1;

		private int Alloc(uint freq, int sym, int left, int right)
		{
			int i = _count++;
			_freq[i] = freq;
			_sym[i] = sym;
			_left[i] = left;
			_right[i] = right;
			return i;
		}

		public static HuffTree Build(uint[] freqs)
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
				Push(tree.Alloc(freqs[s], s, -1, -1));
			}

			while (heapSize > 1)
			{
				int a = Pop();
				int b = Pop();
				Push(tree.Alloc(tree._freq[a] + tree._freq[b], -1, a, b));
			}

			tree.Root = heapSize > 0 ? Pop() : -1;
			return tree;
		}

		public int Decode(BitReader reader)
		{
			int n = Root;
			while (n >= 0 && _sym[n] < 0)
			{
				n = reader.Read(1) != 0 ? _right[n] : _left[n];
			}

			return n >= 0 ? _sym[n] : -1;
		}
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
		out string error)
	{
		blocks = Array.Empty<byte>();
		error = string.Empty;

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
		var freqC = new uint[64];
		var freqOp = new uint[32];
		for (int i = 0; i < 64; ++i) freqA[i] = DecodeFreqByte((byte)reader.Read(8));
		for (int i = 0; i < 32; ++i) freqB[i] = DecodeFreqByte((byte)reader.Read(8));
		for (int i = 0; i < 64; ++i) freqC[i] = DecodeFreqByte((byte)reader.Read(8));
		for (int i = 0; i < 32; ++i) freqOp[i] = DecodeFreqByte((byte)reader.Read(8));

		HuffTree treeA = HuffTree.Build(freqA);
		HuffTree treeB = HuffTree.Build(freqB);
		HuffTree treeC = HuffTree.Build(freqC);
		HuffTree treeOp = HuffTree.Build(freqOp);

		if (treeA.Root < 0 || treeB.Root < 0 || treeC.Root < 0 || treeOp.Root < 0)
		{
			error = "variant: tree build failed";
			return false;
		}

		int bxCount = width / 4;
		int byCount = height / 4;
		blocks = new byte[(long)bxCount * byCount * 8];

		int runRemaining = 0;
		int lastOpType = -1;
		byte lastV20 = 0;

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

						var indices = new byte[16];
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

		return true;
	}

	private static uint DecodeFreqByte(byte b) => (uint)(b & 0x0F) << ((b >> 4) & 0x0F);

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
		out string error)
	{
		bitsConsumed = 0;
		bitsTotal = (long)length * 8;
		width = 0;
		height = 0;
		blocks = Array.Empty<byte>();
		error = string.Empty;

		if (length < 444)
		{
			error = $"body too small (<444 bytes): {length}";
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
		for (int i = 0; i < 256; ++i) freqIdx[i] = DecodeFreqByte((byte)reader.Read(8));
		for (int i = 0; i < 62; ++i) freqOp[i] = DecodeFreqByte((byte)reader.Read(8));
		for (int i = 0; i < 122; ++i) freqDel[i] = DecodeFreqByte((byte)reader.Read(8));

		HuffTree treeIdx = HuffTree.Build(freqIdx);
		HuffTree treeOp = HuffTree.Build(freqOp);
		HuffTree treeDel = HuffTree.Build(freqDel);

		if (treeIdx.Root < 0 || treeOp.Root < 0 || treeDel.Root < 0)
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

				int op = OpTable[opIdx, 0];
				int dx = OpTable[opIdx, 1];
				int dy = OpTable[opIdx, 2];

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

						c0 = ApplyDelta(c0, DeltaTable[d, 0], DeltaTable[d, 1], DeltaTable[d, 2]);
					}

					if (op == 2 || op == 3)
					{
						int d = treeDel.Decode(reader);
						if (d < 0 || d >= 122)
						{
							error = $"delta idx out of range (c1) at block ({bx},{by}): {d}";
							return false;
						}

						c1 = ApplyDelta(c1, DeltaTable[d, 0], DeltaTable[d, 1], DeltaTable[d, 2]);
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
