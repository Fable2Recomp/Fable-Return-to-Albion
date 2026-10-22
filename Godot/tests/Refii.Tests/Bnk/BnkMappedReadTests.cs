namespace Refii.Tests.Bnk;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;

/// <summary>
/// Bank reads through the memory map: an entry read from a mapped file is byte-identical to the
/// reader's answer over a managed copy, a nested container stored uncompressed in a mapped bank
/// is parsed and read from a sub-range of that map, and a compressed entry inflates the
/// same from the map (no stored copy) as from the pooled fallback. Every bank is built here;
/// no Godot type is touched.
/// </summary>
[TestSuite]
public class BnkMappedReadTests
{
	private const int ChunkSize = 0x8000;

	private static string NewTempDir()
	{
		string dir = Path.Combine(Path.GetTempPath(), "refii_bnk_map_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static byte[] Pattern(int length, int seed)
	{
		var bytes = new byte[length];
		for (int i = 0; i < length; i++)
		{
			bytes[i] = (byte)((i * 7 + seed) % 251);
		}

		return bytes;
	}

	private static BnkEntry Find(BnkArchive archive, string path)
	{
		foreach (BnkEntry entry in archive.Entries)
		{
			if (string.Equals(entry.RelativePath, path, StringComparison.OrdinalIgnoreCase))
			{
				return entry;
			}
		}

		throw new InvalidOperationException($"no entry {path}");
	}

	[TestCase]
	public void MappedFileReadsEqualCopyReads()
	{
		byte[] small = Pattern(1000, 1);
		byte[] large = Pattern(300_000, 2);
		byte[] bank = BnkWriter.Write(new[]
		{
			new BnkWriterEntry { Path = "a/small.bin", Bytes = small },
			new BnkWriterEntry { Path = "a/large.bin", Bytes = large },
		});

		string dir = NewTempDir();
		try
		{
			string path = Path.Combine(dir, "test.bnk");
			File.WriteAllBytes(path, bank);

			AssertThat(BnkArchive.TryOpen(path, out BnkArchive? mapped, out string error)).IsTrue();
			AssertThat(BnkArchive.TryOpenBytes("copy", bank, out BnkArchive? copy, out _)).IsTrue();
			using (mapped)
			using (copy)
			{
				AssertThat(mapped!.IsMapped).IsTrue();
				AssertThat(copy!.IsMapped).IsFalse();

				AssertThat(mapped.ReadEntryBytes(Find(mapped, "a/small.bin"))).IsEqual(small);
				AssertThat(mapped.ReadEntryBytes(Find(mapped, "a/large.bin"))).IsEqual(large);
				AssertThat(copy.ReadEntryBytes(Find(copy, "a/large.bin"))).IsEqual(large);

				// The entry as a range: same bytes, read through a fresh stream.
				AssertThat(mapped.TryMapEntry(Find(mapped, "a/large.bin"), out BnkMappedRange? range)).IsTrue();
				AssertThat((int)range!.Length).IsEqual(large.Length);
				using UnmanagedMemoryStream stream = range.OpenStream();
				var viaStream = new byte[large.Length];
				int read = 0;
				while (read < viaStream.Length)
				{
					int got = stream.Read(viaStream, read, viaStream.Length - read);
					AssertThat(got > 0).IsTrue();
					read += got;
				}

				AssertThat(viaStream).IsEqual(large);

				// An unmapped bank maps nothing.
				AssertThat(copy.TryMapEntry(Find(copy, "a/large.bin"), out _)).IsFalse();
			}
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	/// <summary>
	/// The nested path as the file system walks it: the container entry becomes a range of the
	/// outer map, an archive parses from that range, and its entries read out of the sub-range.
	/// <c>BnkFileSystem.Mount</c> itself is not called here: it indexes the pub games through
	/// <c>Godot.Image</c>, which is a native crash in the test host.
	/// </summary>
	[TestCase]
	public void NestedContainerReadsFromTheOuterMapWithNoManagedCopy()
	{
		byte[] innerA = Pattern(50_000, 3);
		byte[] innerB = Pattern(200_000, 4);
		byte[] inner = BnkWriter.Write(new[]
		{
			new BnkWriterEntry { Path = "textures/a.tex", Bytes = innerA },
			new BnkWriterEntry { Path = "models/b.mdl", Bytes = innerB },
		});
		byte[] loose = Pattern(777, 5);
		byte[] outer = BnkWriter.Write(new[]
		{
			new BnkWriterEntry { Path = "loose.bin", Bytes = loose },
			new BnkWriterEntry { Path = "regions/inner.bnk", Bytes = inner },
		});

		string dir = NewTempDir();
		try
		{
			string path = Path.Combine(dir, "outer.bnk");
			File.WriteAllBytes(path, outer);

			AssertThat(BnkArchive.TryOpen(path, out BnkArchive? outerArchive, out string error)).IsTrue();
			using (outerArchive)
			{
				BnkEntry container = Find(outerArchive!, "regions/inner.bnk");
				AssertThat(outerArchive!.TryMapEntry(container, out BnkMappedRange? range)).IsTrue();
				AssertThat((int)range!.Length).IsEqual(inner.Length);

				AssertThat(BnkArchive.TryOpenMapped("regions/inner.bnk", range, out BnkArchive? nested, out string nestedError)).IsTrue();
				using (nested)
				{
					AssertThat(nested!.IsMapped).IsTrue();
					AssertThat(nested.Entries.Count).IsEqual(2);
					AssertThat(nested.ReadEntryBytes(Find(nested, "textures/a.tex"))).IsEqual(innerA);
					AssertThat(nested.ReadEntryBytes(Find(nested, "models/b.mdl"))).IsEqual(innerB);

					// A nested entry is itself a sub-range of the outer map.
					AssertThat(nested.TryMapEntry(Find(nested, "models/b.mdl"), out BnkMappedRange? sub)).IsTrue();
					var copy = new byte[innerB.Length];
					sub!.CopyTo(0, copy);
					AssertThat(copy).IsEqual(innerB);
				}

				// Disposing the nested archive leaves the outer map usable: it borrowed the map.
				AssertThat(outerArchive.ReadEntryBytes(Find(outerArchive, "loose.bin"))).IsEqual(loose);
				AssertThat(outerArchive.ReadEntryBytes(container)).IsEqual(inner);
			}
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[TestCase]
	public void CompressedEntryInflatesTheSameFromTheMapAndFromThePool()
	{
		byte[] payload = Pattern(ChunkSize * 3 + 12_345, 6);
		byte[] tiny = Pattern(100, 7);
		byte[] bank = WriteCompressedBank(new[]
		{
			("big/payload.bin", payload),
			("tiny.bin", tiny),
		});

		string dir = NewTempDir();
		try
		{
			string path = Path.Combine(dir, "compressed.bnk");
			File.WriteAllBytes(path, bank);

			AssertThat(BnkArchive.TryOpen(path, out BnkArchive? mapped, out string error)).IsTrue();
			AssertThat(BnkArchive.TryOpenBytes("copy", bank, out BnkArchive? copy, out _)).IsTrue();
			using (mapped)
			using (copy)
			{
				AssertThat(mapped!.FileDataCompressed).IsTrue();
				BnkEntry entry = Find(mapped, "big/payload.bin");
				AssertThat(entry.IsCompressed).IsTrue();
				AssertThat(entry.ChunkSizes.Count).IsEqual(4);

				AssertThat(mapped.ReadEntryBytes(entry)).IsEqual(payload);
				AssertThat(copy!.ReadEntryBytes(Find(copy, "big/payload.bin"))).IsEqual(payload);
				AssertThat(mapped.ReadEntryBytes(Find(mapped, "tiny.bin"))).IsEqual(tiny);
				AssertThat(copy.ReadEntryBytes(Find(copy, "tiny.bin"))).IsEqual(tiny);

				// A compressed entry is not a range: it has to be inflated to be read.
				AssertThat(mapped.TryMapEntry(entry, out _)).IsFalse();
			}
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	/// <summary>
	/// A format-3 bank with compressed file data, in the layout the reader expects: each entry's
	/// stored bytes are 32 KB slots, one zlib stream per slot, the last slot cut to its stream;
	/// the table lists each chunk's inflated size.
	/// </summary>
	private static byte[] WriteCompressedBank(IReadOnlyList<(string Path, byte[] Bytes)> entries)
	{
		var data = new MemoryStream();
		var table = new MemoryStream();
		WriteWord(table, (uint)entries.Count);

		foreach ((string path, byte[] bytes) in entries)
		{
			int chunkCount = Math.Max(1, (bytes.Length + ChunkSize - 1) / ChunkSize);
			var chunkSizes = new uint[chunkCount];
			var stored = new MemoryStream();
			for (int i = 0; i < chunkCount; i++)
			{
				int start = i * ChunkSize;
				int length = Math.Min(ChunkSize, bytes.Length - start);
				chunkSizes[i] = (uint)length;
				byte[] compressed = Deflate(bytes, start, length);
				if (compressed.Length > ChunkSize)
				{
					throw new InvalidOperationException("fixture chunk does not fit its slot");
				}

				stored.Write(compressed, 0, compressed.Length);
				if (i != chunkCount - 1)
				{
					stored.Write(new byte[ChunkSize - compressed.Length], 0, ChunkSize - compressed.Length);
				}
			}

			uint offset = (uint)data.Length;
			stored.Position = 0;
			stored.CopyTo(data);

			byte[] name = Encoding.UTF8.GetBytes(path.Replace('/', '\\'));
			WriteWord(table, (uint)name.Length);
			table.Write(name, 0, name.Length);
			table.WriteByte(0);
			WriteWord(table, offset);
			WriteWord(table, (uint)bytes.Length);
			WriteWord(table, (uint)stored.Length);
			WriteWord(table, (uint)chunkCount);
			foreach (uint size in chunkSizes)
			{
				WriteWord(table, size);
			}
		}

		byte[] rawTable = table.ToArray();
		byte[] compressedTable = Deflate(rawTable, 0, rawTable.Length);
		uint tablePointer = (uint)(9 + 8 + compressedTable.Length + 8);

		var bank = new MemoryStream();
		WriteWord(bank, tablePointer);
		WriteWord(bank, 3);
		bank.WriteByte(1);
		WriteWord(bank, (uint)compressedTable.Length);
		WriteWord(bank, (uint)rawTable.Length);
		bank.Write(compressedTable, 0, compressedTable.Length);
		WriteWord(bank, 0);
		WriteWord(bank, 0);
		data.Position = 0;
		data.CopyTo(bank);
		return bank.ToArray();
	}

	private static byte[] Deflate(byte[] source, int offset, int count)
	{
		var output = new MemoryStream();
		using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, true))
		{
			compressor.Write(source, offset, count);
		}

		return output.ToArray();
	}

	private static void WriteWord(Stream stream, uint value)
	{
		Span<byte> word = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(word, value);
		stream.Write(word);
	}
}
