using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace Refii.Bnk;

public sealed class BnkArchive : IDisposable
{
	private const int ChunkHeaderSize = 8;
	private const int V3HeaderSize = 9;
	private const int CompressedChunkSize = 0x8000;

	/// <summary>
	/// Fable 3's bank. The header file is the table alone and the entries' bytes are a second
	/// file, <c>&lt;bank&gt;.dat</c>, at offsets from its start. The table carries no terminator
	/// chunk, keys each record by <see cref="Refii.Core.Engine_GameHash.ResourcePath"/>, and
	/// follows the records with the names in record order.
	/// </summary>
	private const uint FormatSeparateData = 4;

	/// <summary>
	/// After each format-4 name: a 16-byte digest, two u32 sizes and a u32 kind. Nothing here
	/// reads them.
	/// </summary>
	private const int SeparateDataNameTrailer = 28;

	private Stream _stream;
	private readonly uint _dataBaseOffset;
	private readonly bool _offsetsAreAbsolute;

	/// <summary>
	/// The bank's bytes as a memory map when it was opened from a file or from an uncompressed
	/// entry of a mapped bank; null when it was opened from a managed copy. Reads through the map
	/// take no lock and copy nothing but the entry the caller asked for.
	/// </summary>
	private BnkMappedRange? _range;
	private bool _ownsRange;
	private bool _hasData;

	private BnkArchive(
		string archivePath,
		Stream stream,
		int formatVersion,
		bool fileDataCompressed,
		uint dataBaseOffset,
		bool offsetsAreAbsolute,
		IReadOnlyList<BnkEntry> entries,
		BnkMappedRange? range,
		bool ownsRange)
	{
		ArchivePath = archivePath;
		_stream = stream;
		_range = range;
		_ownsRange = ownsRange;
		FormatVersion = formatVersion;
		FileDataCompressed = fileDataCompressed;
		_dataBaseOffset = dataBaseOffset;
		_offsetsAreAbsolute = offsetsAreAbsolute;
		Entries = entries;
		DataIsSeparate = formatVersion == FormatSeparateData;
		_hasData = !DataIsSeparate;
	}

	public string ArchivePath { get; }

	public string ArchiveName => Path.GetFileNameWithoutExtension(ArchivePath);

	public int FormatVersion { get; }

	public bool FileDataCompressed { get; }

	public IReadOnlyList<BnkEntry> Entries { get; }

	/// <summary>True when entries are read out of a memory map rather than a stream or a copy.</summary>
	public bool IsMapped => _range is not null;

	/// <summary>
	/// The entries' bytes are not in this file but in <c>&lt;bank&gt;.dat</c> (format 4). Until
	/// <see cref="AttachData"/> supplies them, only the table can be read.
	/// </summary>
	public bool DataIsSeparate { get; }

	/// <summary>False only for a separate-data bank whose <c>.dat</c> has not been attached.</summary>
	public bool HasData => _hasData;

	/// <summary>
	/// Opens a bank file. A separate-data bank takes its <c>.dat</c> from beside it when that
	/// file exists; otherwise the caller attaches it.
	/// </summary>
	public static bool TryOpen(string path, out BnkArchive? archive, out string error)
	{
		archive = null;
		error = string.Empty;

		FileStream? stream = null;
		BnkMappedRange? range = null;
		try
		{
			stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			BnkMappedRange.TryMapFile(stream, out range);
			archive = Parse(path, stream, range, ownsRange: true);
			stream = null;
			range = null;

			string dataPath = path + ".dat";
			if (archive.DataIsSeparate && File.Exists(dataPath))
			{
				stream = new FileStream(
					dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				BnkMappedRange.TryMapFile(stream, out range);
				archive.AttachData(stream, range, ownsRange: true);
			}

			return true;
		}
		catch (Exception exception)
		{
			range?.Dispose();
			stream?.Dispose();
			archive?.Dispose();
			archive = null;
			error = exception.Message;
			return false;
		}
	}

	/// <summary>
	/// Gives a separate-data bank its <c>.dat</c>: <paramref name="stream"/> over the data file's
	/// bytes, and <paramref name="range"/> mapping the same bytes when they are mapped. The
	/// archive disposes the stream, and the range when <paramref name="ownsRange"/>.
	/// </summary>
	public void AttachData(Stream stream, BnkMappedRange? range, bool ownsRange)
	{
		if (!DataIsSeparate)
		{
			throw new InvalidOperationException($"{ArchivePath} keeps its data in the bank itself");
		}

		if (_hasData)
		{
			throw new InvalidOperationException($"{ArchivePath} already has its data attached");
		}

		_stream = stream;
		_range = range;
		_ownsRange = ownsRange;
		_hasData = true;
	}

	/// <summary>
	/// Opens a bank that lives inside a mapped range — a nested container stored uncompressed in
	/// a mapped outer bank. The range is borrowed: the outer archive owns the map.
	/// </summary>
	public static bool TryOpenMapped(
		string virtualName, BnkMappedRange range, out BnkArchive? archive, out string error)
	{
		archive = null;
		error = string.Empty;
		try
		{
			archive = Parse(virtualName, range.OpenStream(), range, ownsRange: false);
			return true;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}

	/// <summary>
	/// The mapped bytes of an entry stored uncompressed in a mapped bank, as a range the caller
	/// may open another archive over. False for a compressed entry or an unmapped bank.
	/// </summary>
	public bool TryMapEntry(BnkEntry entry, out BnkMappedRange? range)
	{
		range = null;
		if (_range is null || entry.IsCompressed)
		{
			return false;
		}

		long offset = GetEntryDataOffset(entry);
		if (offset < 0 || entry.Size > _range.Length - offset)
		{
			return false;
		}

		range = _range.Slice(offset, entry.Size);
		return true;
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

	private static BnkArchive Parse(
		string path, Stream stream, BnkMappedRange? range = null, bool ownsRange = false)
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

		if (formatWord != 2 && formatWord != 3 && formatWord != FormatSeparateData)
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
		else if (formatWord == FormatSeparateData)
		{
			tableBytes = ReadCompressedTableChunks(stream, V3HeaderSize, headerPointer, false);
			dataBaseOffset = 0;
			offsetsAreAbsolute = true;
		}
		else
		{
			tableBytes = ReadCompressedTableChunks(stream, V3HeaderSize, headerPointer, true);
			dataBaseOffset = headerPointer;
			offsetsAreAbsolute = false;
		}

		List<BnkEntry> entries = formatWord == FormatSeparateData
			? ParseSeparateDataEntryTable(tableBytes, fileDataCompressed, path)
			: ParseEntryTable(tableBytes, fileDataCompressed, path);

		if (formatWord == FormatSeparateData)
		{
			// The file was the table and nothing else; the data comes from AttachData.
			stream.Dispose();
			if (ownsRange)
			{
				range?.Dispose();
			}

			stream = Stream.Null;
			range = null;
			ownsRange = false;
		}

		return new BnkArchive(
			path,
			stream,
			(int)formatWord,
			fileDataCompressed,
			dataBaseOffset,
			offsetsAreAbsolute,
			entries,
			range,
			ownsRange);
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

		return InflateAll(joined)
			   ?? throw new InvalidDataException("failed to decompress file-table stream");
	}

	private static byte[]? InflateAll(byte[] source)
	{
		foreach (int wrapper in new[] { 0, 1, 2 })
		{
			try
			{
				using var input = new MemoryStream(source, 0, source.Length, false);
				Stream decompressor = wrapper switch
				{
					0 => new ZLibStream(input, CompressionMode.Decompress, true),
					1 => new DeflateStream(input, CompressionMode.Decompress, true),
					_ => new GZipStream(input, CompressionMode.Decompress, true),
				};

				using (decompressor)
				{
					using var output = new MemoryStream();
					decompressor.CopyTo(output);
					return output.ToArray();
				}
			}
			catch (InvalidDataException)
			{
			}
		}

		return null;
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

	/// <summary>
	/// The format-4 table:
	/// <code>
	/// u32 0, u32 count
	/// count records: u32 hash, u32 offset, u32 size
	///                (compressed: + u32 storedSize, u32 chunkCount, chunkCount × u32)
	/// count names:   u32 length (with the NUL), name, NUL, 28-byte trailer
	/// </code>
	/// Name i belongs to record i, and each name is checked against its record's hash, so a
	/// misaligned table is an error here, not a wrong file later.
	/// </summary>
	private static List<BnkEntry> ParseSeparateDataEntryTable(
		byte[] table, bool fileDataCompressed, string path)
	{
		if (table.Length < 8)
		{
			throw new InvalidDataException($"decompressed file table is too small for {path}");
		}

		uint fileCount = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(4));
		int position = 8;
		var records = new List<(uint Hash, BnkEntry Entry)>((int)Math.Min(fileCount, 1u << 20));

		for (uint index = 0; index < fileCount; ++index)
		{
			int recordSize = fileDataCompressed ? 20 : 12;
			if (position + recordSize > table.Length)
			{
				throw new InvalidDataException($"unexpected end of file table in {path}");
			}

			ReadOnlySpan<byte> record = table.AsSpan(position);
			uint hash = BinaryPrimitives.ReadUInt32BigEndian(record);
			uint offset = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
			uint size = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
			uint storedSize = size;
			uint[] chunkSizes = Array.Empty<uint>();
			position += recordSize;

			if (fileDataCompressed)
			{
				storedSize = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
				uint chunkCount = BinaryPrimitives.ReadUInt32BigEndian(record[16..]);
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
			}

			records.Add((hash, new BnkEntry
			{
				Offset = offset,
				Size = size,
				StoredSize = storedSize,
				IsCompressed = fileDataCompressed,
				ChunkSizes = chunkSizes,
			}));
		}

		var entries = new List<BnkEntry>(records.Count);
		foreach ((uint hash, BnkEntry record) in records)
		{
			if (position + 4 > table.Length)
			{
				throw new InvalidDataException($"file table ends before its names in {path}");
			}

			int storedLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(position)));
			position += 4;
			if (storedLength == 0 || position + storedLength + SeparateDataNameTrailer > table.Length)
			{
				throw new InvalidDataException($"bank entry name exceeds file table in {path}");
			}

			int nameLength = Array.IndexOf(table, (byte)0, position, storedLength) is int nul and >= 0
				? nul - position
				: storedLength;
			string name = System.Text.Encoding.UTF8.GetString(table, position, nameLength);
			position += storedLength + SeparateDataNameTrailer;

			if (Refii.Core.Engine_GameHash.ResourcePath(name) != hash)
			{
				throw new InvalidDataException(
					$"bank entry '{name}' does not match its record's hash 0x{hash:x8} in {path}");
			}

			entries.Add(new BnkEntry
			{
				RelativePath = name.Replace('\\', '/'),
				Offset = record.Offset,
				Size = record.Size,
				StoredSize = record.StoredSize,
				IsCompressed = record.IsCompressed,
				ChunkSizes = record.ChunkSizes,
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
	///
	/// A mapped bank never takes it: every read opens its own view of the map, so the region
	/// build's workers read the same bank at once.
	/// </summary>
	private readonly object _streamLock = new();

	public byte[] ReadEntryBytes(BnkEntry entry)
	{
		if (!_hasData)
		{
			throw new InvalidDataException(
				$"{entry.RelativePath}: {ArchivePath} keeps its data in {ArchiveName}.bnk.dat, which is not mounted");
		}

		long dataOffset = GetEntryDataOffset(entry);

		if (!entry.IsCompressed)
		{
			var raw = new byte[entry.Size];
			if (_range is not null)
			{
				_range.CopyTo(dataOffset, raw);
				return raw;
			}

			lock (_streamLock)
			{
				_stream.Position = dataOffset;
				ReadExact(_stream, raw);
			}

			return raw;
		}

		var output = new byte[entry.Size];
		int storedLength = checked((int)entry.StoredSize);

		if (_range is not null)
		{
			// Each chunk inflates straight out of the map: no copy of the stored bytes, no lock.
			BnkMappedRange range = _range;
			InflateChunks(
				entry, output, storedLength,
				(offset, count) => range.OpenStream(dataOffset + offset, count));
			return output;
		}

		// THE STORED BYTES ARE SCRATCH. They are read once and inflated once, so they come from
		// the pool rather than the large-object heap, where every multi-megabyte entry read was
		// leaving a dead array for the next gen2 to find.
		byte[] stored = ArrayPool<byte>.Shared.Rent(storedLength);
		try
		{
			lock (_streamLock)
			{
				_stream.Position = dataOffset;
				ReadExact(_stream, stored.AsSpan(0, storedLength));
			}

			InflateChunks(
				entry, output, storedLength,
				(offset, count) => new MemoryStream(stored, offset, count, false));
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(stored);
		}

		return output;
	}

	/// <summary>
	/// Inflates every chunk of a compressed entry into <paramref name="output"/>. Chunk i's
	/// compressed bytes sit at <c>i * 0x8000</c> of the stored region; <paramref name="open"/>
	/// hands back a stream over <c>(offset, count)</c> of that region.
	/// </summary>
	private static void InflateChunks(
		BnkEntry entry, byte[] output, int storedLength, Func<int, int, Stream> open)
	{
		int chunkCount = entry.ChunkSizes.Count;

		var starts = new int[chunkCount + 1];
		for (int i = 0; i < chunkCount; ++i)
		{
			starts[i + 1] = (int)Math.Min((long)starts[i] + entry.ChunkSizes[i], output.Length);
		}

		void InflateChunk(int i)
		{
			int outOffset = starts[i];
			int outLength = starts[i + 1] - starts[i];
			if (outLength <= 0)
			{
				return;
			}

			int compressedOffset = i * CompressedChunkSize;
			if (compressedOffset >= storedLength)
			{
				throw new InvalidDataException(
					$"invalid chunk offset in {entry.RelativePath}");
			}

			int compressedSize = Math.Min(CompressedChunkSize, storedLength - compressedOffset);
			if (!InflateInto(() => open(compressedOffset, compressedSize), output, outOffset, outLength))
			{
				throw new InvalidDataException(
					$"failed to inflate chunk {i} of {entry.RelativePath}");
			}
		}

		// Nested parallel dispatch inside an existing thread-pool worker can saturate
		// the thread pool and cause work starvation; only parallelize on dedicated threads.
		if (chunkCount >= ParallelInflateChunks && !Thread.CurrentThread.IsThreadPoolThread)
		{
			try
			{
				System.Threading.Tasks.Parallel.For(0, chunkCount, InflateChunk);
			}
			catch (AggregateException aggregate) when (aggregate.InnerException is not null)
			{
				throw aggregate.InnerException;
			}
		}
		else
		{
			for (int i = 0; i < chunkCount; ++i)
			{
				InflateChunk(i);
			}
		}
	}

	private const int ParallelInflateChunks = 64;

	private static bool InflateInto(
		Func<Stream> open, byte[] output, int outOffset, int outLength)
	{
		foreach (int wrapper in new[] { 0, 1, 2 })
		{
			try
			{
				using Stream input = open();
				Stream decompressor = wrapper switch
				{
					0 => new ZLibStream(input, CompressionMode.Decompress, true),
					1 => new DeflateStream(input, CompressionMode.Decompress, true),
					_ => new GZipStream(input, CompressionMode.Decompress, true),
				};

				using (decompressor)
				{
					int read = 0;
					while (read < outLength)
					{
						int got = decompressor.Read(output, outOffset + read, outLength - read);
						if (got <= 0)
						{
							break;
						}

						read += got;
					}

					if (read == 0 && outLength > 0)
					{
						continue;
					}

					return true;
				}
			}
			catch (InvalidDataException)
			{
			}
		}

		return false;
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

	public void Dispose()
	{
		_stream.Dispose();
		if (_ownsRange)
		{
			_range?.Dispose();
		}
	}
}
