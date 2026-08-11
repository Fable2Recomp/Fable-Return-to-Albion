using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Refii.Bnk;

public sealed class BnkArchive : IDisposable
{
	private const int ChunkHeaderSize = 8;
	private const int V3HeaderSize = 9;
	private const int CompressedChunkSize = 0x8000;

	private readonly Stream _stream;
	private readonly uint _dataBaseOffset;
	private readonly bool _offsetsAreAbsolute;

	private BnkArchive(
		string archivePath,
		Stream stream,
		int formatVersion,
		bool fileDataCompressed,
		uint dataBaseOffset,
		bool offsetsAreAbsolute,
		IReadOnlyList<BnkEntry> entries)
	{
		ArchivePath = archivePath;
		_stream = stream;
		FormatVersion = formatVersion;
		FileDataCompressed = fileDataCompressed;
		_dataBaseOffset = dataBaseOffset;
		_offsetsAreAbsolute = offsetsAreAbsolute;
		Entries = entries;
	}

	public string ArchivePath { get; }

	public string ArchiveName => Path.GetFileNameWithoutExtension(ArchivePath);

	public int FormatVersion { get; }

	public bool FileDataCompressed { get; }

	public IReadOnlyList<BnkEntry> Entries { get; }

	public static bool TryOpen(string path, out BnkArchive? archive, out string error)
	{
		archive = null;
		error = string.Empty;

		FileStream? stream = null;
		try
		{
			stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			archive = Parse(path, stream);
			return true;
		}
		catch (Exception exception)
		{
			stream?.Dispose();
			error = exception.Message;
			return false;
		}
	}

	public static bool TryOpenBytes(
		string virtualName, byte[] bytes, out BnkArchive? archive, out string error)
	{
		archive = null;
		error = string.Empty;
		try
		{
			archive = Parse(virtualName, new MemoryStream(bytes, false));
			return true;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}

	private static BnkArchive Parse(string path, Stream stream)
	{
		long fileSize = stream.Length;
		if (fileSize < V3HeaderSize + ChunkHeaderSize)
		{
			throw new InvalidDataException($"{path} is too small to contain a valid BNK header");
		}

		Span<byte> header = stackalloc byte[16];
		stream.Position = 0;
		ReadExact(stream, header);

		uint headerPointer = BinaryPrimitives.ReadUInt32BigEndian(header);
		uint formatWord = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
		bool fileDataCompressed = header[8] != 0;

		if (formatWord != 2 && formatWord != 3)
		{
			throw new InvalidDataException(
				$"unsupported BNK format word 0x{formatWord:x8} in {path}");
		}

		if (headerPointer > fileSize)
		{
			throw new InvalidDataException($"BNK table offset exceeds bank size in {path}");
		}

		byte[] tableBytes;
		uint dataBaseOffset;
		bool offsetsAreAbsolute;

		if (formatWord == 2)
		{
			tableBytes = ReadCompressedTableChunks(stream, headerPointer, (ulong)fileSize, false);
			dataBaseOffset = 0;
			offsetsAreAbsolute = true;
		}
		else
		{
			tableBytes = ReadCompressedTableChunks(stream, V3HeaderSize, headerPointer, true);
			dataBaseOffset = headerPointer;
			offsetsAreAbsolute = false;
		}

		List<BnkEntry> entries = ParseEntryTable(tableBytes, fileDataCompressed, path);
		return new BnkArchive(
			path,
			stream,
			(int)formatWord,
			fileDataCompressed,
			dataBaseOffset,
			offsetsAreAbsolute,
			entries);
	}

	private static byte[] ReadCompressedTableChunks(
		Stream stream, ulong chunkOffset, ulong chunkLimit, bool hasZeroTerminator)
	{
		var compressed = new List<byte[]>();
		bool sawZeroTerminator = false;
		Span<byte> chunkHeader = stackalloc byte[ChunkHeaderSize];

		while (chunkOffset + ChunkHeaderSize <= chunkLimit)
		{
			stream.Position = (long)chunkOffset;
			ReadExact(stream, chunkHeader);

			uint compressedSize = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader);
			if (compressedSize == 0)
			{
				sawZeroTerminator = true;
				break;
			}

			ulong dataOffset = chunkOffset + ChunkHeaderSize;
			ulong nextChunkOffset = dataOffset + compressedSize;
			if (nextChunkOffset > chunkLimit)
			{
				throw new InvalidDataException("compressed file-table chunk exceeds bank bounds");
			}

			var payload = new byte[compressedSize];
			ReadExact(stream, payload);
			compressed.Add(payload);
			chunkOffset = nextChunkOffset;
		}

		if (hasZeroTerminator && !sawZeroTerminator)
		{
			throw new InvalidDataException("missing compressed file-table terminator");
		}

		if (compressed.Count == 0)
		{
			return new byte[4];
		}

		int total = 0;
		foreach (byte[] chunk in compressed)
		{
			total += chunk.Length;
		}

		var joined = new byte[total];
		int written = 0;
		foreach (byte[] chunk in compressed)
		{
			Buffer.BlockCopy(chunk, 0, joined, written, chunk.Length);
			written += chunk.Length;
		}

		return Inflate(joined, 0, joined.Length, 0)
			   ?? throw new InvalidDataException("failed to decompress file-table stream");
	}

	private static List<BnkEntry> ParseEntryTable(
		byte[] table, bool fileDataCompressed, string path)
	{
		if (table.Length < 4)
		{
			throw new InvalidDataException($"decompressed file table is too small for {path}");
		}

		uint fileCount = BinaryPrimitives.ReadUInt32BigEndian(table);
		int position = 4;
		var entries = new List<BnkEntry>((int)Math.Min(fileCount, 1u << 20));

		for (uint index = 0; index < fileCount; ++index)
		{
			if (position + 4 > table.Length)
			{
				throw new InvalidDataException(
					$"unexpected end of file table while reading name length in {path}");
			}

			uint storedNameLength = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(position));
			position += 4;
			if (storedNameLength == 0)
			{
				throw new InvalidDataException($"encountered zero-length entry name in {path}");
			}

			int maxNameEnd = (int)Math.Min(
				(long)table.Length, (long)position + storedNameLength + 1);
			int nullPosition = Array.IndexOf(table, (byte)0, position, maxNameEnd - position);
			if (nullPosition < 0)
			{
				throw new InvalidDataException(
					$"failed to locate a null terminator for a bank entry in {path}");
			}

			string relativePath = System.Text.Encoding.UTF8.GetString(
				table, position, nullPosition - position);
			position = nullPosition + 1;

			uint offset;
			uint size;
			uint storedSize;
			bool isCompressed;
			uint[] chunkSizes = Array.Empty<uint>();

			if (!fileDataCompressed)
			{
				if (position + 8 > table.Length)
				{
					throw new InvalidDataException(
						$"unexpected end of file table while reading entry metadata in {path}");
				}

				offset = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(position));
				position += 4;
				size = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(position));
				position += 4;
				storedSize = size;
				isCompressed = false;
			}
			else
			{
				if (position + 16 > table.Length)
				{
					throw new InvalidDataException(
						$"unexpected end of compressed file table in {path}");
				}

				offset = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(position));
				position += 4;
				size = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(position));
				position += 4;
				storedSize = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(position));
				position += 4;
				uint chunkCount = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(position));
				position += 4;

				if ((long)position + (long)chunkCount * 4 > table.Length)
				{
					throw new InvalidDataException(
						$"compressed entry chunk table exceeds file table in {path}");
				}

				chunkSizes = new uint[chunkCount];
				for (uint chunkIndex = 0; chunkIndex < chunkCount; ++chunkIndex)
				{
					chunkSizes[chunkIndex] =
						BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(position));
					position += 4;
				}

				isCompressed = true;
			}

			entries.Add(new BnkEntry
			{
				RelativePath = relativePath.Replace('\\', '/'),
				Offset = offset,
				Size = size,
				StoredSize = storedSize,
				IsCompressed = isCompressed,
				ChunkSizes = chunkSizes,
			});
		}

		return entries;
	}

	public long GetEntryDataOffset(BnkEntry entry) =>
		_offsetsAreAbsolute ? entry.Offset : (long)_dataBaseOffset + entry.Offset;

	/// <summary>
	/// A SEEK AND A READ ARE ONE OPERATION, and the stream's position is shared state.
	///
	/// Every subsystem reads banks through this one archive object, so once anything reads off
	/// the main thread — the region build does — two readers interleaving `Position = x` and
	/// `ReadExact` hand each other the wrong bytes. It would not throw: it would decompress
	/// whatever happened to be at the other reader's offset and surface as a corrupt mesh or a
	/// texture from a different file, which is a bug that looks like a format problem.
	///
	/// Held for the I/O only. Inflation is pure CPU work on a private buffer and is the
	/// expensive half, so it stays outside and two threads can decompress at once.
	/// </summary>
	private readonly object _streamLock = new();

	public byte[] ReadEntryBytes(BnkEntry entry)
	{
		long dataOffset = GetEntryDataOffset(entry);

		if (!entry.IsCompressed)
		{
			var raw = new byte[entry.Size];
			lock (_streamLock)
			{
				_stream.Position = dataOffset;
				ReadExact(_stream, raw);
			}

			return raw;
		}

		var stored = new byte[entry.StoredSize];
		lock (_streamLock)
		{
			_stream.Position = dataOffset;
			ReadExact(_stream, stored);
		}

		var output = new byte[entry.Size];
		int produced = 0;

		for (int i = 0; i < entry.ChunkSizes.Count; ++i)
		{
			int outLength = (int)entry.ChunkSizes[i];
			int compressedOffset = i * CompressedChunkSize;
			if (compressedOffset >= stored.Length)
			{
				throw new InvalidDataException(
					$"invalid chunk offset in {entry.RelativePath}");
			}

			int available = stored.Length - compressedOffset;
			int compressedSize = Math.Min(CompressedChunkSize, available);

			byte[]? chunk = Inflate(stored, compressedOffset, compressedSize, outLength);
			if (chunk is null)
			{
				throw new InvalidDataException(
					$"failed to inflate chunk {i} of {entry.RelativePath}");
			}

			int copy = Math.Min(chunk.Length, output.Length - produced);
			Buffer.BlockCopy(chunk, 0, output, produced, copy);
			produced += copy;
		}

		return output;
	}

	private static byte[]? Inflate(byte[] source, int offset, int count, int expectedLength)
	{
		foreach (int wrapper in new[] { 0, 1, 2 })
		{
			try
			{
				using var input = new MemoryStream(source, offset, count, false);
				Stream decompressor = wrapper switch
				{
					0 => new ZLibStream(input, CompressionMode.Decompress, true),
					1 => new DeflateStream(input, CompressionMode.Decompress, true),
					_ => new GZipStream(input, CompressionMode.Decompress, true),
				};

				using (decompressor)
				{
					using var output = expectedLength > 0
						? new MemoryStream(expectedLength)
						: new MemoryStream();
					decompressor.CopyTo(output);
					byte[] result = output.ToArray();
					if (expectedLength > 0 && result.Length != expectedLength)
					{
						Array.Resize(ref result, expectedLength);
					}

					return result;
				}
			}
			catch (InvalidDataException)
			{
			}
		}

		return null;
	}

	private static void ReadExact(Stream stream, Span<byte> buffer)
	{
		int read = 0;
		while (read < buffer.Length)
		{
			int got = stream.Read(buffer[read..]);
			if (got <= 0)
			{
				throw new EndOfStreamException("unexpected end of BNK stream");
			}

			read += got;
		}
	}

	public void Dispose() => _stream.Dispose();
}
