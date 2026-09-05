using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Refii.Bnk;

/// <summary>One file going into a bank, under the path the guest will ask for.</summary>
public sealed class BnkWriterEntry
{
	public required string Path { get; init; }

	public required byte[] Bytes { get; init; }
}

/// <summary>
/// Writes a `.bnk`, as the exact inverse of <see cref="BnkArchive"/>'s parse.
///
/// THE ONLY CONSUMER OF A MOD BANK IS OUR OWN PARSER — the guest never opens one; it asks the
/// mount by name, and the mount expanded the bank at startup. So this targets the simplest
/// layout that parser reads:
/// <code>
/// @0            u32 tablePointer   u32 formatWord = 3   u8 fileDataCompressed = 0
/// @9            table chunks: { u32 compressedSize, u32 rawSize, zlib payload }, then a
///               chunk header whose size word is 0 — the terminator v3 requires
/// @tablePointer entry data; entry offsets are RELATIVE to here
/// table:        u32 count, then per entry { u32 nameLength, name, 0, u32 offset, u32 size }
/// </code>
///
/// V3 WITH UNCOMPRESSED DATA. The table is always zlib — the format has no uncompressed form for
/// it — while entry data is stored plainly: a mod bank is read from local disk, and the chunked
/// per-entry compression (0x8000-stride slots plus a chunk-size table) exists to stream from a
/// DVD. The reader handles both, so compression can be added later without a format change.
///
/// Names are written with backslashes, the separator the shipped banks are authored with.
/// </summary>
public static class BnkWriter
{
	private const int V3HeaderSize = 9;

	/// <summary>
	/// A chunk header is EIGHT bytes, and the reader's arithmetic depends on it: it reads the
	/// header, then takes the payload from <c>chunkOffset + ChunkHeaderSize</c>. A four-byte
	/// header put every payload four bytes early and the terminator out of reach, which reads
	/// back as "missing compressed file-table terminator".
	/// </summary>
	private const int ChunkHeaderSize = 8;

	private const uint FormatVersion3 = 3;

	/// <summary>
	/// Serialises a bank.
	///
	/// The whole bank is assembled in memory: entry offsets have to be known before the table
	/// can be written, and the table sits before the data. Mod banks are the intended input.
	/// </summary>
	public static byte[] Write(IReadOnlyList<BnkWriterEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);

		var data = new MemoryStream();
		var table = new MemoryStream();

		WriteWord(table, (uint)entries.Count);

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (BnkWriterEntry entry in entries)
		{
			string normalized = Normalize(entry.Path);
			if (normalized.Length == 0)
			{
				throw new InvalidDataException("a bank entry must have a path");
			}

			if (!seen.Add(normalized))
			{
				// ONE NAME, ONE ENTRY. The reader keys by path and would treat a second entry
				// as an alternate to be rejoined, which is not what a duplicate means here.
				throw new InvalidDataException($"duplicate bank entry: {normalized}");
			}

			if (entry.Bytes.LongLength > uint.MaxValue)
			{
				throw new InvalidDataException($"bank entry is too large: {normalized}");
			}

			uint offset = (uint)data.Length;
			data.Write(entry.Bytes, 0, entry.Bytes.Length);

			byte[] name = Encoding.UTF8.GetBytes(normalized.Replace('/', '\\'));
			WriteWord(table, (uint)name.Length);
			table.Write(name, 0, name.Length);
			table.WriteByte(0);
			WriteWord(table, offset);
			WriteWord(table, (uint)entry.Bytes.Length);
		}

		byte[] rawTable = table.ToArray();
		byte[] compressedTable = Deflate(rawTable);

		// The reader concatenates every chunk payload and inflates the result as ONE stream, so
		// a single chunk is a complete file table. The terminator is a whole chunk header whose
		// size word is zero, and it must fit inside the table region for the reader to see it.
		uint tablePointer =
			(uint)(V3HeaderSize + ChunkHeaderSize + compressedTable.Length + ChunkHeaderSize);

		var bank = new MemoryStream();
		WriteWord(bank, tablePointer);
		WriteWord(bank, FormatVersion3);
		bank.WriteByte(0);
		WriteWord(bank, (uint)compressedTable.Length);
		WriteWord(bank, (uint)rawTable.Length);
		bank.Write(compressedTable, 0, compressedTable.Length);
		WriteWord(bank, 0);
		WriteWord(bank, 0);
		data.Position = 0;
		data.CopyTo(bank);
		return bank.ToArray();
	}

	/// <summary>
	/// Writes a bank to disk and reads it back through <see cref="BnkArchive"/> before keeping it.
	///
	/// THE ROUND TRIP IS PART OF WRITING, not a test someone remembers to run: the reader is the
	/// only definition of the format that matters, so a bank it cannot parse is a failure here
	/// rather than a mod that mounts empty. The file is written to a temporary name and moved
	/// into place only once it reads back, so a failed write never replaces a good bank.
	/// </summary>
	public static bool WriteFile(
		string path, IReadOnlyList<BnkWriterEntry> entries, out string error)
	{
		error = string.Empty;
		string temporary = path + ".writing";
		try
		{
			byte[] bank = Write(entries);
			if (!Verify(bank, entries, out error))
			{
				return false;
			}

			string? directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllBytes(temporary, bank);
			File.Move(temporary, path, overwrite: true);
			return true;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			try
			{
				File.Delete(temporary);
			}
			catch (Exception)
			{
			}

			return false;
		}
	}

	/// <summary>
	/// Reads a written bank back and checks it describes the entries it was given.
	///
	/// Paths, sizes and bytes: the data is stored uncompressed, so reading it back costs one
	/// copy and closes the gap between "the table parses" and "the payload is where the table
	/// says it is".
	/// </summary>
	public static bool Verify(
		byte[] bank, IReadOnlyList<BnkWriterEntry> entries, out string error)
	{
		error = string.Empty;
		if (!BnkArchive.TryOpenBytes("<written>", bank, out BnkArchive? archive, out error) ||
			archive is null)
		{
			error = $"the bank did not read back: {error}";
			return false;
		}

		using (archive)
		{
			if (archive.Entries.Count != entries.Count)
			{
				error = $"wrote {entries.Count} entries, read back {archive.Entries.Count}";
				return false;
			}

			for (int i = 0; i < entries.Count; i++)
			{
				BnkEntry read = archive.Entries[i];
				string expected = Normalize(entries[i].Path);
				if (!string.Equals(Normalize(read.RelativePath), expected, StringComparison.OrdinalIgnoreCase))
				{
					error = $"entry {i} read back as '{read.RelativePath}', expected '{expected}'";
					return false;
				}

				byte[] bytes;
				try
				{
					bytes = archive.ReadEntryBytes(read);
				}
				catch (Exception exception)
				{
					error = $"entry '{expected}' did not read back: {exception.Message}";
					return false;
				}

				if (!bytes.AsSpan().SequenceEqual(entries[i].Bytes))
				{
					error = $"entry '{expected}' read back with different bytes";
					return false;
				}
			}
		}

		return true;
	}

	/// <summary>Big-endian, as every scalar in the format is.</summary>
	private static void WriteWord(Stream stream, uint value)
	{
		Span<byte> word = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(word, value);
		stream.Write(word);
	}

	private static byte[] Deflate(byte[] source)
	{
		using var output = new MemoryStream();
		using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, true))
		{
			compressor.Write(source, 0, source.Length);
		}

		return output.ToArray();
	}

	private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
