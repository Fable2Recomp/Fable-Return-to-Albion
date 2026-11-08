using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading;
using Godot;
using Refii.Core;

namespace Refii.Textures;

/// <summary>The LZ4 block format, greedy on the way in and bounds-checked on the way out.</summary>
/// <remarks>Written here because a hundred lines cost less than a package: the upload form takes half
/// its size and decodes several times faster than it inflates.</remarks>
internal static unsafe class AndroidTexLz4
{
	internal const int TableEntries = 1 << 16;
	private const int MinMatch = 4, LastLiterals = 5, MatchSearchMargin = 12;

	/// <summary>The most an input of this length can encode to.</summary>
	public static int Bound(int length) => length + length / 255 + 16;

	/// <param name="source">The bytes to encode.</param>
	/// <param name="destination">At least <see cref="Bound"/> bytes.</param>
	/// <param name="table">A scratch table of <see cref="TableEntries"/> slots, overwritten.</param>
	/// <returns>The encoded length.</returns>
	public static int Encode(ReadOnlySpan<byte> source, Span<byte> destination, int[] table)
	{
		Array.Fill(table, -1, 0, TableEntries);
		fixed (byte* s = source)
		fixed (byte* d = destination)
		{
			int ip = 0, anchor = 0, op = 0, limit = source.Length - MatchSearchMargin;
			while (ip < limit)
			{
				uint value = Unsafe.ReadUnaligned<uint>(s + ip);
				uint hash = (value * 2654435761u) >> 16;
				int candidate = table[hash];
				table[hash] = ip;
				if (candidate < 0 || ip - candidate > ushort.MaxValue || Unsafe.ReadUnaligned<uint>(s + candidate) != value)
				{
					++ip;
					continue;
				}

				int length = MinMatch, most = source.Length - LastLiterals - ip;
				while (length < most && s[candidate + length] == s[ip + length])
				{
					++length;
				}

				int offset = ip - candidate;
				byte* token = d + op++;
				*token = (byte)(Math.Min(ip - anchor, 15) << 4);
				op = PutLength(d, op, ip - anchor);
				Buffer.MemoryCopy(s + anchor, d + op, ip - anchor, ip - anchor);
				op += ip - anchor;
				d[op++] = (byte)offset;
				d[op++] = (byte)(offset >> 8);
				*token |= (byte)Math.Min(length - MinMatch, 15);
				op = PutLength(d, op, length - MinMatch);
				ip += length;
				anchor = ip;
			}

			int last = source.Length - anchor;
			d[op++] = (byte)(Math.Min(last, 15) << 4);
			op = PutLength(d, op, last);
			Buffer.MemoryCopy(s + anchor, d + op, last, last);
			return op + last;
		}
	}

	private static int PutLength(byte* d, int op, int length)
	{
		if (length < 15)
		{
			return op;
		}

		int rest = length - 15;
		for (; rest >= 255; rest -= 255)
		{
			d[op++] = 255;
		}

		d[op++] = (byte)rest;
		return op;
	}

	/// <summary>Decodes into all of <paramref name="destination"/>, or false. No input reads or writes out of bounds.</summary>
	public static bool Decode(ReadOnlySpan<byte> source, Span<byte> destination)
	{
		fixed (byte* s = source)
		fixed (byte* d = destination)
		{
			byte* ip = s, inputEnd = s + source.Length, op = d, outputEnd = d + destination.Length;
			while (ip < inputEnd)
			{
				uint token = *ip++;
				if (!ReadLength(token >> 4, ref ip, inputEnd, out nint literals) ||
					literals > inputEnd - ip || literals > outputEnd - op)
				{
					return false;
				}

				if (literals <= 16 && inputEnd - ip >= 16 && outputEnd - op >= 16)
				{
					Unsafe.WriteUnaligned(op, Unsafe.ReadUnaligned<Vector128<byte>>(ip));
				}
				else
				{
					Buffer.MemoryCopy(ip, op, literals, literals);
				}

				ip += literals;
				op += literals;
				if (ip == inputEnd)
				{
					break;
				}

				if (inputEnd - ip < 2)
				{
					return false;
				}

				nint offset = ip[0] | ip[1] << 8;
				ip += 2;
				if (offset == 0 || offset > op - d || !ReadLength(token & 15, ref ip, inputEnd, out nint length))
				{
					return false;
				}

				length += MinMatch;
				if (length > outputEnd - op)
				{
					return false;
				}

				byte* match = op - offset;
				if (offset >= 16 && outputEnd - op >= length + 16)
				{
					byte* end = op + length;
					do
					{
						Unsafe.WriteUnaligned(op, Unsafe.ReadUnaligned<Vector128<byte>>(match));
						op += 16;
						match += 16;
					}
					while (op < end);

					op = end;
				}
				else
				{
					for (nint i = 0; i < length; ++i)
					{
						op[i] = match[i];
					}

					op += length;
				}
			}

			return op == outputEnd;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool ReadLength(uint nibble, ref byte* ip, byte* end, out nint length)
	{
		length = (nint)nibble;
		if (nibble != 15)
		{
			return true;
		}

		uint more;
		do
		{
			if (ip >= end)
			{
				return false;
			}

			more = *ip++;
			length += (nint)more;
		}
		while (more == 255);
		return true;
	}
}

/// <summary>One upload form's file header, and the check over what follows it.</summary>
/// <param name="Recipe">Everything that decides the form (<see cref="AndroidTexUploadCache.RecipeFor"/>).</param>
/// <param name="Width">The form's width, half the decoded one if the tier dropped a level.</param>
/// <param name="Height">Its height.</param>
/// <param name="Format">Its <see cref="Image.Format"/>.</param>
/// <param name="Mipmaps">Whether it has a mip chain.</param>
/// <param name="HasAlpha">The decoder's transparency classification, which the caller needs without a decode.</param>
/// <param name="Packed">LZ4 rather than stored.</param>
/// <param name="Length">The form's bytes.</param>
/// <param name="StoredLength">The bytes after this header.</param>
/// <param name="Checksum"><see cref="ChecksumOf"/> over those.</param>
internal readonly record struct AndroidTexUploadHeader(ulong Recipe, int Width, int Height, int Format, bool Mipmaps,
	bool HasAlpha, bool Packed, int Length, int StoredLength, ulong Checksum)
{
	public const int Size = 44;
	private const uint Magic = 0x50554652u; // 'RFUP' little endian: the sweep reads it as an entry of no known cost, so Decoded.

	/// <summary>The largest form a file may claim; a 4096x4096 RG8 chain is 45 MB.</summary>
	private const int MaximumLength = 64 * 1024 * 1024;

	public void Write(Span<byte> output)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(output, Magic);
		BinaryPrimitives.WriteUInt64LittleEndian(output[4..], Recipe);
		BinaryPrimitives.WriteInt32LittleEndian(output[12..], Width);
		BinaryPrimitives.WriteInt32LittleEndian(output[16..], Height);
		BinaryPrimitives.WriteInt32LittleEndian(output[20..], Format);
		output[24] = Mipmaps ? (byte)1 : (byte)0;
		output[25] = HasAlpha ? (byte)1 : (byte)0;
		output[26] = Packed ? (byte)1 : (byte)0;
		output[27] = 0;
		BinaryPrimitives.WriteInt32LittleEndian(output[28..], Length);
		BinaryPrimitives.WriteInt32LittleEndian(output[32..], StoredLength);
		BinaryPrimitives.WriteUInt64LittleEndian(output[36..], Checksum);
	}

	public static bool TryRead(ReadOnlySpan<byte> input, out AndroidTexUploadHeader header)
	{
		header = default;
		if (input.Length < Size || BinaryPrimitives.ReadUInt32LittleEndian(input) != Magic)
		{
			return false;
		}

		header = new AndroidTexUploadHeader(
			BinaryPrimitives.ReadUInt64LittleEndian(input[4..]),
			BinaryPrimitives.ReadInt32LittleEndian(input[12..]),
			BinaryPrimitives.ReadInt32LittleEndian(input[16..]),
			BinaryPrimitives.ReadInt32LittleEndian(input[20..]),
			input[24] != 0, input[25] != 0, input[26] != 0,
			BinaryPrimitives.ReadInt32LittleEndian(input[28..]),
			BinaryPrimitives.ReadInt32LittleEndian(input[32..]),
			BinaryPrimitives.ReadUInt64LittleEndian(input[36..]));
		return header.Width > 0 && header.Height > 0 &&
			header.Length > 0 && header.Length <= MaximumLength &&
			header.StoredLength > 0 && header.StoredLength <= AndroidTexLz4.Bound(header.Length);
	}

	/// <summary>Finds a torn or damaged file, which unlike a deflate stream would otherwise read as pixels.</summary>
	public static ulong ChecksumOf(ReadOnlySpan<byte> bytes)
	{
		const ulong prime = 0x9E3779B185EBCA87ul;
		ulong hash = (ulong)bytes.Length * prime;
		ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(bytes);
		foreach (ulong word in words)
		{
			hash = (BitOperations.RotateLeft(hash, 29) ^ word) * prime;
		}

		for (int i = words.Length * sizeof(ulong); i < bytes.Length; ++i)
		{
			hash = (BitOperations.RotateLeft(hash, 29) ^ bytes[i]) * prime;
		}

		return hash ^ (hash >> 32);
	}
}

/// <summary>
/// A world texture in the form the GPU takes it, so a known one skips the inflate, the BC image and
/// the transcode or expansion. Off unless <c>android_tex_upload_cache</c> is set.
/// </summary>
/// <remarks>
/// <see cref="Engine_TexLibrary.Get"/> asks here first, and offers what it decoded otherwise.
///
/// THE BC ENTRY STAYS, and this is a second copy of it: the UI loader, a mod remount, the cube maps
/// and any change of tier still decode from it. Everything here can be lost at no cost but time.
///
/// ONLY A TEXTURE <see cref="AndroidTexSourceKeys"/> KNOWS. A mod or a loose file has no identity, so
/// it never comes here, which is what lets a hit skip the read; the same conditions apply, and the
/// same ways of going stale.
///
/// THE RECIPE IS IN THE FILE, NOT THE NAME, so a texture has one file whatever the tier, the
/// transcoder or the policy. A file of another recipe is a miss, and the miss writes over it: as with
/// a container bump in <see cref="Engine_TexCache"/>, nothing is stranded.
///
/// BESIDE THE ENTRIES, UNDER THE SAME EPOCH AND EXTENSION, so a converter bump deletes these and the
/// budget sweep counts and evicts them. The magic differs, so the sweep takes them for Decoded, the
/// cheapest class, which they are.
///
/// WRITTEN BEHIND THE LOAD. A miss hands one thread the prepared image, sharing its buffer, and goes
/// on. That queue is capped; past the cap a texture is left for the next visit, so a load never waits
/// on the disk. The thread ends soon after the queue empties.
/// </remarks>
public static class AndroidTexUploadCache
{
	/// <summary>The setting. Off until tested on a phone.</summary>
	public const string SettingKey = "android_tex_upload_cache";

	/// <summary>Bump when the file's bytes mean something else, or Prepare's output changes for another reason.</summary>
	private const int FormVersion = 1;

	/// <summary>A form must save a tenth of its size for its file to be packed.</summary>
	private const int PackedAtMost = 90;

	/// <summary>How long the writer waits for more before ending: a load offers a texture every few milliseconds.</summary>
	private const int WriterIdleMilliseconds = 2000;

	private sealed record Job(string Path, ulong Recipe, Image Image, bool HasAlpha, long Bytes);

	private static readonly object Gate = new();
	private static readonly Queue<Job> Queue = new();
	private static Thread? _writer;
	private static bool _writing;
	private static long _queuedBytes;
	private static bool _writesFailed;
	private static bool? _enabled;
	private static (AndroidTextureUploadPolicy Policy, ulong Recipe)? _recipe;
	private static long _served;
	private static long _missed;
	private static long _written;
	private static long _skipped;
	private static long _bytesRead;
	private static long _bytesWritten;
	private static long _lastReported = -1;

	/// <summary>How much may wait for the writer, at any moment, beside the images the load holds.</summary>
	internal static long MaximumQueuedBytes { get; set; } = 32 * 1024 * 1024;

	/// <summary>Holds the writer back, so a test can see a full queue.</summary>
	internal static ManualResetEventSlim? WriterGateForTests { get; set; }

	/// <summary>Where a test keeps its files, in place of the epoch directory.</summary>
	internal static string? RootOverride { get; set; }

	internal static bool Enabled
	{
		get => _enabled ??= ReadSetting();
		set => _enabled = value;
	}

	/// <summary>Textures served from here since the process started.</summary>
	internal static long Served => Interlocked.Read(ref _served);

	/// <summary>Known textures with no usable file: none, or another recipe's, or damaged.</summary>
	internal static long Missed => Interlocked.Read(ref _missed);

	/// <summary>Files written.</summary>
	internal static long Written => Interlocked.Read(ref _written);

	/// <summary>Forms left for a later visit because the queue was full.</summary>
	internal static long Skipped => Interlocked.Read(ref _skipped);

	/// <summary>
	/// The form of a texture already seen with these banks, tier and transcoder, or null. Whatever
	/// happens next should end in <see cref="Remember"/> with this ticket.
	/// </summary>
	public static Image? TryServe(string path, Func<string, byte[]?> read, out AndroidTexSourceKeys.Ticket ticket, out bool hasAlpha)
	{
		ticket = default;
		hasAlpha = false;
		if (!Enabled || AndroidTextureUpload.Policy is not { Android: true } policy ||
			!AndroidTexSourceKeys.TryKnownKey(path, read, out ticket, out string? key))
		{
			return null;
		}

		Image? image = null;
		try
		{
			image = Load(PathFor(key!), RecipeOf(policy), out hasAlpha);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
		}

		if (image is null)
		{
			hasAlpha = false;
			Interlocked.Increment(ref _missed);
			return null;
		}

		Interlocked.Increment(ref _served);
		return image;
	}

	/// <summary>
	/// Prepares a decoded image ahead of its upload and queues the result for the disk. The upload
	/// then finds nothing left to do, and uploads the same bytes it would have made itself.
	/// </summary>
	/// <param name="ticket">From <see cref="TryServe"/>, taken before the decode. Nothing is kept if the mount changed since.</param>
	/// <param name="image">Whatever the caller is about to upload.</param>
	/// <param name="hasAlpha">The decoder's classification of it.</param>
	/// <returns>The image to upload: a new one the caller owns, or the one it passed.</returns>
	public static Image? Remember(in AndroidTexSourceKeys.Ticket ticket, Image? image, bool hasAlpha)
	{
		if (image is null || !Enabled || !ticket.Issued || image.IsEmpty() ||
			AndroidTextureUpload.Policy is not { Android: true } policy)
		{
			return image;
		}

		Image.Format source = image.GetFormat();
		Image.Format target = policy.Target(source);
		if (source is not (Image.Format.Dxt1 or Image.Format.Dxt5 or Image.Format.RgtcRg) || source == target)
		{
			return image;
		}

		Image prepared = AndroidTextureUpload.PrepareAhead(image, world: true);
		if (prepared.IsEmpty())
		{
			// The upload makes it again, and says so with the image's name on it.
			prepared.Dispose();
			return image;
		}

		if (prepared.GetFormat() == target && AndroidTexSourceKeys.TryKeyOf(ticket, out string? key))
		{
			Enqueue(PathFor(key!), RecipeOf(policy), prepared, hasAlpha);
		}

		return prepared;
	}

	/// <summary>One log line, when the counts have moved.</summary>
	public static void Report()
	{
		long moved = Served + Missed + Written + Skipped;
		if (!Enabled || moved == Interlocked.Exchange(ref _lastReported, moved))
		{
			return;
		}

		GD.Print($"[tex] upload cache: {Served:N0} served ({Interlocked.Read(ref _bytesRead) / 1048576.0:F1} MB read), " +
			$"{Missed:N0} missed, {Written:N0} written ({Interlocked.Read(ref _bytesWritten) / 1048576.0:F1} MB), {Skipped:N0} left for later");
	}

	/// <summary>Everything that decides a form's bytes, as one number.</summary>
	/// <remarks>The policy's text carries the tier and every format's support, so a field added to it
	/// joins by itself. The Godot version is there for BC5, which Godot's own decoder expands.</remarks>
	internal static ulong RecipeFor(AndroidTextureUploadPolicy policy, int transcoder = Engine_AstcTranscode.Version)
	{
		string text = $"{FormVersion}|{transcoder}|{Godot.Engine.GetVersionInfo()["string"]}|{policy}|" +
			$"{AndroidTextureUploadPolicy.ReducedNormalMapMinimumEdge}|{AndroidTextureUploadPolicy.ReducedColorMapMinimumEdge}|world";
		return BinaryPrimitives.ReadUInt64LittleEndian(Engine_Hash.Sha256(Encoding.UTF8.GetBytes(text)));
	}

	/// <summary>Waits for the writer to finish what it was given.</summary>
	internal static void FlushForTests()
	{
		lock (Gate)
		{
			while (Queue.Count > 0 || _writing)
			{
				Monitor.Wait(Gate, 100);
			}
		}
	}

	internal static void ResetForTests()
	{
		FlushForTests();
		lock (Gate)
		{
			_writesFailed = false;
			_recipe = null;
		}

		_enabled = null;
		Interlocked.Exchange(ref _served, 0);
		Interlocked.Exchange(ref _missed, 0);
		Interlocked.Exchange(ref _written, 0);
		Interlocked.Exchange(ref _skipped, 0);
		Interlocked.Exchange(ref _bytesRead, 0);
		Interlocked.Exchange(ref _bytesWritten, 0);
		Interlocked.Exchange(ref _lastReported, -1);
	}

	internal static string PathFor(string key) =>
		Path.Combine(RootOverride ?? Path.Combine(Engine_TexCache.CacheRoot, $"v{Engine_TexCache.ConverterVersion}", "upload"), key[..2], key + ".rftx");

	private static bool ReadSetting()
	{
		if (!Engine_Platform.IsAndroid)
		{
			return false;
		}

		try
		{
			return Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), SettingKey, false);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static ulong RecipeOf(AndroidTextureUploadPolicy policy)
	{
		lock (Gate)
		{
			if (_recipe is { } known && ReferenceEquals(known.Policy, policy))
			{
				return known.Recipe;
			}

			ulong recipe = RecipeFor(policy);
			_recipe = (policy, recipe);
			return recipe;
		}
	}

	private static Image? Load(string path, ulong recipe, out bool hasAlpha)
	{
		hasAlpha = false;
		if (!File.Exists(path))
		{
			return null;
		}

		Span<byte> head = stackalloc byte[AndroidTexUploadHeader.Size];
		using Microsoft.Win32.SafeHandles.SafeFileHandle file = File.OpenHandle(path);
		if (RandomAccess.Read(file, head, 0) != head.Length ||
			!AndroidTexUploadHeader.TryRead(head, out AndroidTexUploadHeader header) ||
			header.Recipe != recipe ||
			RandomAccess.GetLength(file) != AndroidTexUploadHeader.Size + (long)header.StoredLength)
		{
			return null;
		}

		byte[] stored = ArrayPool<byte>.Shared.Rent(header.StoredLength);
		byte[]? unpacked = header.Packed ? ArrayPool<byte>.Shared.Rent(header.Length) : null;
		try
		{
			Span<byte> bytes = stored.AsSpan(0, header.StoredLength);
			if (RandomAccess.Read(file, bytes, AndroidTexUploadHeader.Size) != bytes.Length ||
				AndroidTexUploadHeader.ChecksumOf(bytes) != header.Checksum)
			{
				return null;
			}

			ReadOnlySpan<byte> form = bytes;
			if (unpacked is not null)
			{
				form = unpacked.AsSpan(0, header.Length);
				if (!AndroidTexLz4.Decode(bytes, unpacked.AsSpan(0, header.Length)))
				{
					return null;
				}
			}
			else if (header.StoredLength != header.Length)
			{
				return null;
			}

			var image = new Image();
			image.SetData(header.Width, header.Height, header.Mipmaps, (Image.Format)header.Format, form);
			if (image.IsEmpty())
			{
				image.Dispose();
				return null;
			}

			Interlocked.Add(ref _bytesRead, header.StoredLength);
			hasAlpha = header.HasAlpha;
			return image;
		}
		finally
		{
			if (unpacked is not null)
			{
				ArrayPool<byte>.Shared.Return(unpacked);
			}

			ArrayPool<byte>.Shared.Return(stored);
		}
	}

	private static void Enqueue(string path, ulong recipe, Image prepared, bool hasAlpha)
	{
		long bytes = prepared.GetDataSize();
		lock (Gate)
		{
			if (_writesFailed)
			{
				return;
			}

			if (_queuedBytes > 0 && _queuedBytes + bytes > MaximumQueuedBytes)
			{
				Interlocked.Increment(ref _skipped);
				return;
			}

			// Its own reference: the caller disposes its image once uploaded. A copy-on-write share
			// of the buffer, which nothing writes to, so no bytes are copied.
			Queue.Enqueue(new Job(path, recipe, (Image)prepared.Duplicate(), hasAlpha, bytes));
			_queuedBytes += bytes;
			if (_writer is null)
			{
				_writer = new Thread(WriteAll)
				{
					IsBackground = true,
					Name = "refii-tex-upload-writer",
					Priority = ThreadPriority.Lowest,
				};
				_writer.Start();
			}
			else
			{
				Monitor.PulseAll(Gate);
			}
		}
	}

	private static void WriteAll()
	{
		var table = new int[AndroidTexLz4.TableEntries];
		while (true)
		{
			Job? job;
			lock (Gate)
			{
				_writing = false;
				Monitor.PulseAll(Gate);
				while (!Queue.TryDequeue(out job))
				{
					if (!Monitor.Wait(Gate, WriterIdleMilliseconds) && Queue.Count == 0)
					{
						_writer = null;
						return;
					}
				}

				_writing = true;
			}

			WriterGateForTests?.Wait();
			try
			{
				Write(job, table);
			}
			catch (Exception e)
			{
				// A full disk, most likely. The next launch's sweep may free some; this one stops trying.
				// Every exception: one escaping this thread ends the process.
				lock (Gate)
				{
					if (!_writesFailed)
					{
						GD.PushWarning($"[tex] upload cache stops writing: {e.GetType().Name}: {e.Message}");
					}

					_writesFailed = true;
				}
			}
			finally
			{
				job.Image.Dispose();
				lock (Gate)
				{
					_queuedBytes -= job.Bytes;
				}
			}
		}
	}

	private static void Write(Job job, int[] table)
	{
		using var image = new Engine_ImageBytes(job.Image);
		ReadOnlySpan<byte> raw = image.Span;
		byte[] packed = ArrayPool<byte>.Shared.Rent(AndroidTexLz4.Bound(raw.Length));
		string temp = $"{job.Path}.{System.Environment.CurrentManagedThreadId}.tmp";
		try
		{
			int encoded = AndroidTexLz4.Encode(raw, packed, table);
			bool usePacked = encoded * 100L <= raw.Length * (long)PackedAtMost;
			ReadOnlySpan<byte> stored = usePacked ? packed.AsSpan(0, encoded) : raw;

			Span<byte> head = stackalloc byte[AndroidTexUploadHeader.Size];
			new AndroidTexUploadHeader(job.Recipe, job.Image.GetWidth(), job.Image.GetHeight(), (int)job.Image.GetFormat(),
				job.Image.HasMipmaps(), job.HasAlpha, usePacked, raw.Length, stored.Length,
				AndroidTexUploadHeader.ChecksumOf(stored)).Write(head);

			Directory.CreateDirectory(Path.GetDirectoryName(job.Path)!);
			using (var output = new FileStream(temp, FileMode.Create, System.IO.FileAccess.Write, FileShare.None, bufferSize: 0))
			{
				output.Write(head);
				output.Write(stored);
			}

			// Aside and renamed, as Engine_TexCache.Store does, and the sweep clears a .tmp a kill leaves.
			File.Move(temp, job.Path, overwrite: true);
			Interlocked.Increment(ref _written);
			Interlocked.Add(ref _bytesWritten, AndroidTexUploadHeader.Size + (long)stored.Length);
		}
		catch
		{
			try
			{
				File.Delete(temp);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
			}

			throw;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(packed);
		}
	}
}
