using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;

namespace Refii.Core;

/// <summary>
/// SHA-256 and SHA-1 for content identities, on every runtime the game ships on.
/// </summary>
/// <remarks>
/// <para>
/// .NET's hash classes call the platform's crypto library. The Mono Android runtime uses
/// Android's own; the NativeAOT Android export (linux-bionic) uses OpenSSL, which no Android
/// device provides to apps — and its shim does not throw, it ABORTS the process the first time
/// anything touches System.Security.Cryptography ("libssl.so.10" not found, from the static
/// constructor of Interop.Crypto). The first such call was the launcher's install check.
/// </para>
/// <para>
/// So under linux-bionic these hashes run in managed code (<see cref="ManagedSha256"/>,
/// <see cref="ManagedSha1"/>; FIPS 180-4, checked against the platform's on the desktop by
/// Engine_HashTests). Everywhere else they ARE the platform's. The digests are the same either
/// way, so recorded identities stay valid across runtimes.
/// </para>
/// </remarks>
public static class Engine_Hash
{
	/// <summary>True where the platform's hash classes cannot be used (NativeAOT on Android).</summary>
	public static bool UsesManaged => Engine_Platform.IsLinuxBionic;

	/// <summary>SHA-256 of the bytes.</summary>
	public static byte[] Sha256(ReadOnlySpan<byte> data)
	{
		if (!UsesManaged)
			return SHA256.HashData(data);
		var sha = new ManagedSha256();
		sha.Append(data);
		return sha.Finish();
	}

	/// <summary>SHA-256 of the rest of the stream.</summary>
	public static byte[] Sha256(Stream stream) =>
		UsesManaged ? Drain(new ManagedSha256(), stream) : SHA256.HashData(stream);

	/// <summary>SHA-1 of the bytes.</summary>
	public static byte[] Sha1(ReadOnlySpan<byte> data)
	{
		if (!UsesManaged)
			return SHA1.HashData(data);
		var sha = new ManagedSha1();
		sha.Append(data);
		return sha.Finish();
	}

	/// <summary>SHA-1 of the rest of the stream.</summary>
	public static byte[] Sha1(Stream stream) =>
		UsesManaged ? Drain(new ManagedSha1(), stream) : SHA1.HashData(stream);

	/// <summary>A SHA-256 <see cref="HashAlgorithm"/> (TransformBlock / TransformFinalBlock / Hash).</summary>
	public static HashAlgorithm CreateSha256() => UsesManaged ? new ManagedSha256() : SHA256.Create();

	/// <summary>An incremental SHA-256, as <see cref="IncrementalHash"/> is used.</summary>
	public static Incremental CreateIncrementalSha256() => new(UsesManaged);

	/// <summary>Appends spans and returns the digest; the platform's IncrementalHash or the managed one.</summary>
	public sealed class Incremental : IDisposable
	{
		private readonly IncrementalHash? _platform;
		private readonly ManagedSha256? _managed;

		internal Incremental(bool managed)
		{
			if (managed)
				_managed = new ManagedSha256();
			else
				_platform = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		}

		public void AppendData(ReadOnlySpan<byte> data)
		{
			if (_managed != null)
				_managed.Append(data);
			else
				_platform!.AppendData(data);
		}

		public byte[] GetHashAndReset() => _managed != null ? _managed.Finish() : _platform!.GetHashAndReset();

		public void Dispose()
		{
			_platform?.Dispose();
			_managed?.Dispose();
		}
	}

	private static byte[] Drain(BlockHash hash, Stream stream)
	{
		byte[] buffer = new byte[1 << 16];
		int read;
		while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
			hash.Append(buffer.AsSpan(0, read));
		return hash.Finish();
	}

	/// <summary>The Merkle–Damgård framing both hashes share: 64-byte blocks, big-endian bit length.</summary>
	public abstract class BlockHash : HashAlgorithm
	{
		private readonly byte[] _block = new byte[64];
		private int _filled;
		private ulong _length;

		/// <summary>Adds bytes to the message.</summary>
		public void Append(ReadOnlySpan<byte> data)
		{
			_length += (ulong)data.Length;
			if (_filled > 0)
			{
				int take = Math.Min(64 - _filled, data.Length);
				data[..take].CopyTo(_block.AsSpan(_filled));
				_filled += take;
				data = data[take..];
				if (_filled < 64)
					return;
				Compress(_block);
				_filled = 0;
			}

			while (data.Length >= 64)
			{
				Compress(data[..64]);
				data = data[64..];
			}

			data.CopyTo(_block);
			_filled = data.Length;
		}

		/// <summary>The digest of everything appended; the hash then starts over.</summary>
		public byte[] Finish()
		{
			ulong bits = _length * 8;
			_block[_filled++] = 0x80;
			if (_filled > 56)
			{
				_block.AsSpan(_filled).Clear();
				Compress(_block);
				_filled = 0;
			}

			_block.AsSpan(_filled, 56 - _filled).Clear();
			BinaryPrimitives.WriteUInt64BigEndian(_block.AsSpan(56), bits);
			Compress(_block);
			byte[] digest = Digest();
			Initialize();
			return digest;
		}

		public sealed override void Initialize()
		{
			_filled = 0;
			_length = 0;
			Array.Clear(_block);
			Reset();
		}

		protected sealed override void HashCore(byte[] array, int ibStart, int cbSize) => Append(array.AsSpan(ibStart, cbSize));

		protected sealed override void HashCore(ReadOnlySpan<byte> source) => Append(source);

		protected sealed override byte[] HashFinal() => Finish();

		protected abstract void Reset();

		protected abstract void Compress(ReadOnlySpan<byte> block);

		protected abstract byte[] Digest();
	}

	/// <summary>SHA-256 in managed code (FIPS 180-4 §6.2).</summary>
	public sealed class ManagedSha256 : BlockHash
	{
		private static readonly uint[] K =
		{
			0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
			0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
			0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
			0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
			0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
			0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
			0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
			0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
		};

		private readonly uint[] _state = new uint[8];
		private readonly uint[] _w = new uint[64];

		public ManagedSha256()
		{
			HashSizeValue = 256;
			Reset();
		}

		protected override void Reset()
		{
			_state[0] = 0x6a09e667; _state[1] = 0xbb67ae85; _state[2] = 0x3c6ef372; _state[3] = 0xa54ff53a;
			_state[4] = 0x510e527f; _state[5] = 0x9b05688c; _state[6] = 0x1f83d9ab; _state[7] = 0x5be0cd19;
		}

		/// <summary>The same compression with the CPU's SHA-2 instructions, which every phone the game
		/// runs on has: the texture cache hashes each texture's whole source on every load.</summary>
		private void CompressArm(ReadOnlySpan<byte> block)
		{
			Vector128<uint> abcd = Vector128.Create<uint>(_state), efgh = Vector128.Create<uint>(_state.AsSpan(4));
			Vector128<uint> start0 = abcd, start1 = efgh;
			Span<Vector128<uint>> m = stackalloc Vector128<uint>[4];
			for (int i = 0; i < 4; i++)
				m[i] = AdvSimd.ReverseElement8(Vector128.Create<byte>(block.Slice(i * 16, 16)).AsUInt32());

			for (int i = 0; i < 16; i++)
			{
				Vector128<uint> wk = m[i & 3] + Vector128.Create<uint>(K.AsSpan(i * 4));
				Vector128<uint> previous = abcd;
				abcd = System.Runtime.Intrinsics.Arm.Sha256.HashUpdate1(abcd, efgh, wk);
				efgh = System.Runtime.Intrinsics.Arm.Sha256.HashUpdate2(efgh, previous, wk);
				if (i < 12)
					m[i & 3] = System.Runtime.Intrinsics.Arm.Sha256.ScheduleUpdate1(
						System.Runtime.Intrinsics.Arm.Sha256.ScheduleUpdate0(m[i & 3], m[(i + 1) & 3]),
						m[(i + 2) & 3], m[(i + 3) & 3]);
			}

			(abcd + start0).CopyTo(_state);
			(efgh + start1).CopyTo(_state, 4);
		}

		protected override void Compress(ReadOnlySpan<byte> block)
		{
			if (System.Runtime.Intrinsics.Arm.Sha256.IsSupported)
			{
				CompressArm(block);
				return;
			}

			uint[] w = _w;
			for (int t = 0; t < 16; t++)
				w[t] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(t * 4, 4));
			for (int t = 16; t < 64; t++)
			{
				uint s0 = BitOperations.RotateRight(w[t - 15], 7) ^ BitOperations.RotateRight(w[t - 15], 18) ^ (w[t - 15] >> 3);
				uint s1 = BitOperations.RotateRight(w[t - 2], 17) ^ BitOperations.RotateRight(w[t - 2], 19) ^ (w[t - 2] >> 10);
				w[t] = w[t - 16] + s0 + w[t - 7] + s1;
			}

			uint a = _state[0], b = _state[1], c = _state[2], d = _state[3];
			uint e = _state[4], f = _state[5], g = _state[6], h = _state[7];
			for (int t = 0; t < 64; t++)
			{
				uint s1 = BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25);
				uint ch = (e & f) ^ (~e & g);
				uint t1 = h + s1 + ch + K[t] + w[t];
				uint s0 = BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22);
				uint maj = (a & b) ^ (a & c) ^ (b & c);
				uint t2 = s0 + maj;
				h = g; g = f; f = e; e = d + t1;
				d = c; c = b; b = a; a = t1 + t2;
			}

			_state[0] += a; _state[1] += b; _state[2] += c; _state[3] += d;
			_state[4] += e; _state[5] += f; _state[6] += g; _state[7] += h;
		}

		protected override byte[] Digest()
		{
			byte[] digest = new byte[32];
			for (int i = 0; i < 8; i++)
				BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4), _state[i]);
			return digest;
		}
	}

	/// <summary>SHA-1 in managed code (FIPS 180-4 §6.1), for the identities that already use it.</summary>
	public sealed class ManagedSha1 : BlockHash
	{
		private readonly uint[] _state = new uint[5];
		private readonly uint[] _w = new uint[80];

		public ManagedSha1()
		{
			HashSizeValue = 160;
			Reset();
		}

		protected override void Reset()
		{
			_state[0] = 0x67452301; _state[1] = 0xefcdab89; _state[2] = 0x98badcfe; _state[3] = 0x10325476;
			_state[4] = 0xc3d2e1f0;
		}

		protected override void Compress(ReadOnlySpan<byte> block)
		{
			uint[] w = _w;
			for (int t = 0; t < 16; t++)
				w[t] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(t * 4, 4));
			for (int t = 16; t < 80; t++)
				w[t] = BitOperations.RotateLeft(w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16], 1);

			uint a = _state[0], b = _state[1], c = _state[2], d = _state[3], e = _state[4];
			for (int t = 0; t < 80; t++)
			{
				uint f, k;
				if (t < 20) { f = (b & c) | (~b & d); k = 0x5a827999; }
				else if (t < 40) { f = b ^ c ^ d; k = 0x6ed9eba1; }
				else if (t < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8f1bbcdc; }
				else { f = b ^ c ^ d; k = 0xca62c1d6; }
				uint temp = BitOperations.RotateLeft(a, 5) + f + e + k + w[t];
				e = d; d = c; c = BitOperations.RotateLeft(b, 30); b = a; a = temp;
			}

			_state[0] += a; _state[1] += b; _state[2] += c; _state[3] += d; _state[4] += e;
		}

		protected override byte[] Digest()
		{
			byte[] digest = new byte[20];
			for (int i = 0; i < 5; i++)
				BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4), _state[i]);
			return digest;
		}
	}
}
