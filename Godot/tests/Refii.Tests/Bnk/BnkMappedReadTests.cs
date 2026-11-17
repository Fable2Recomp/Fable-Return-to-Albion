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
		string dir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_bnk_map_" + Guid.NewGuid().ToString("N"));
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

	[TestCase]
	public void ReadIntoAndReadHeadMatchTheWholeRead()
	{
		byte[] payload = Pattern(ChunkSize * 3 + 12_345, 8);
		byte[] tiny = Pattern(100, 9);
		byte[] compressedBank = WriteCompressedBank(new[] { ("big.bin", payload), ("tiny.bin", tiny) });
		byte[] storedBank = BnkWriter.Write(new[]
		{
			new BnkWriterEntry { Path = "big.bin", Bytes = payload },
			new BnkWriterEntry { Path = "tiny.bin", Bytes = tiny },
		});

		string dir = NewTempDir();
		try
		{
			foreach ((string name, byte[] bank) in new[] { ("compressed.bnk", compressedBank), ("stored.bnk", storedBank) })
			{
				string path = Path.Combine(dir, name);
				File.WriteAllBytes(path, bank);
				AssertThat(BnkArchive.TryOpen(path, out BnkArchive? mapped, out _)).IsTrue();
				AssertThat(BnkArchive.TryOpenBytes("copy", bank, out BnkArchive? copy, out _)).IsTrue();
				using (mapped)
				using (copy)
				{
					foreach (BnkArchive archive in new[] { mapped!, copy! })
					{
						BnkEntry big = Find(archive, "big.bin");
						var into = new byte[payload.Length + 7];
						archive.ReadEntryInto(big, into, 7);
						AssertThat(into.AsSpan(7).SequenceEqual(payload)).IsTrue();
						AssertThat(into.AsSpan(0, 7).IndexOfAnyExcept((byte)0)).IsEqual(-1);

						foreach (int count in new[] { 1, 8, 79, ChunkSize - 1, ChunkSize, ChunkSize + 1, ChunkSize * 2 + 5 })
						{
							AssertThat(archive.ReadEntryHead(big, count)).IsEqual(payload[..count]);
						}

						AssertThat(archive.ReadEntryHead(big, payload.Length + 100)).IsEqual(payload);
						AssertThat(archive.ReadEntryHead(Find(archive, "tiny.bin"), 500)).IsEqual(tiny);
					}
				}
			}
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	/// <summary>
	/// The guest's read: an entry written straight into native memory that starts full of junk is
	/// the whole read's bytes, whole or as a front, stored or compressed, mapped or copied. 70
	/// chunks reaches the parallel inflate on a thread outside the pool. Nothing past the range is
	/// touched.
	/// </summary>
	[TestCase]
	public void ReadEntryRangeIntoJunkNativeMemoryMatchesTheWholeRead()
	{
		byte[] payload = Pattern(ChunkSize * 70 + 4321, 12);
		byte[] compressedBank = WriteCompressedBank(new[] { ("big.bin", payload) });
		byte[] storedBank = BnkWriter.Write(new[] { new BnkWriterEntry { Path = "big.bin", Bytes = payload } });

		const int Guard = 16;
		IntPtr native = System.Runtime.InteropServices.Marshal.AllocHGlobal(payload.Length + Guard);
		string dir = NewTempDir();
		try
		{
			foreach ((string name, byte[] bank) in new[] { ("compressed.bnk", compressedBank), ("stored.bnk", storedBank) })
			{
				string path = Path.Combine(dir, name);
				File.WriteAllBytes(path, bank);
				AssertThat(BnkArchive.TryOpen(path, out BnkArchive? mapped, out _)).IsTrue();
				AssertThat(BnkArchive.TryOpenBytes("copy", bank, out BnkArchive? copy, out _)).IsTrue();
				using (mapped)
				using (copy)
				{
					foreach (BnkArchive archive in new[] { mapped!, copy! })
					{
						BnkEntry big = Find(archive, "big.bin");
						foreach (int count in new[] { 0, 1, 8, ChunkSize - 1, ChunkSize + 1, ChunkSize * 3, payload.Length })
						{
							var junk = new byte[payload.Length + Guard];
							Array.Fill(junk, (byte)0xCD);
							System.Runtime.InteropServices.Marshal.Copy(junk, 0, native, junk.Length);

							var memory = new Refii.Core.Engine_NativeMemory(native, count);
							archive.ReadEntryRange(big, memory.Memory);

							var back = new byte[payload.Length + Guard];
							System.Runtime.InteropServices.Marshal.Copy(native, back, 0, back.Length);
							AssertThat(back.AsSpan(0, count).SequenceEqual(payload.AsSpan(0, count))).IsTrue();
							AssertThat(back.AsSpan(count).IndexOfAnyExcept((byte)0xCD)).IsEqual(-1);
						}

						AssertThrown(() => archive.ReadEntryRange(big, new byte[payload.Length + 1]))
							.IsInstanceOf<ArgumentOutOfRangeException>();
					}
				}
			}
		}
		finally
		{
			System.Runtime.InteropServices.Marshal.FreeHGlobal(native);
			Directory.Delete(dir, true);
		}
	}

	[TestCase]
	public void MountedJoinsMatchTheirParts()
	{
		var modelHeader = new byte[104];
		Pattern(104, 10).CopyTo(modelHeader, 0);
		Encoding.ASCII.GetBytes("MeshFile").CopyTo(modelHeader, 0);
		byte[] modelBody = Pattern(ChunkSize * 2 + 77, 11);

		byte[] mip0 = Pattern(4096, 12);
		byte[] rest = Pattern(1024, 13);
		byte[] wholeHeader = TexHeaderBytes(mip0.Length + rest.Length, mip0.Length);
		byte[] splitHeader = TexHeaderBytes(mip0.Length + rest.Length, mip0.Length);
		byte[] whole = Pattern(mip0.Length + rest.Length, 14);
		byte[] loose = Pattern(300, 15);

		string dir = NewTempDir();
		try
		{
			File.WriteAllBytes(Path.Combine(dir, "a_headers.bnk"), BnkWriter.Write(new[]
			{
				new BnkWriterEntry { Path = "m/model.mdl", Bytes = modelHeader },
				new BnkWriterEntry { Path = "m/nobody.mdl", Bytes = modelHeader },
				new BnkWriterEntry { Path = "m/loose.mdl", Bytes = modelHeader },
				new BnkWriterEntry { Path = "t/whole.tex", Bytes = wholeHeader },
				new BnkWriterEntry { Path = "t/split.tex", Bytes = splitHeader },
			}));
			File.WriteAllBytes(Path.Combine(dir, "b_models.bnk"), WriteCompressedBank(new[]
			{
				("m/model.mdl", modelBody),
				("m/loose.mdl", modelBody),
			}));
			File.WriteAllBytes(Path.Combine(dir, "c_textures.bnk"), BnkWriter.Write(new[]
			{
				new BnkWriterEntry { Path = "t/whole.tex", Bytes = whole },
				new BnkWriterEntry { Path = "t/split.tex", Bytes = mip0 },
			}));
			File.WriteAllBytes(Path.Combine(dir, "d_textures.bnk"), BnkWriter.Write(new[]
			{
				new BnkWriterEntry { Path = "t/split.tex", Bytes = rest },
			}));
			Directory.CreateDirectory(Path.Combine(dir, "m"));
			File.WriteAllBytes(Path.Combine(dir, "m", "loose.mdl"), loose);

			using BnkFileSystem mount = BnkFileSystem.Mount(dir);

			byte[] joinedModel = [.. modelHeader, .. modelBody];
			AssertThat(mount.TryGetFile("m/model.mdl", out BnkFile model)).IsTrue();
			AssertThat(model.ReadAllBytes()).IsEqual(joinedModel);
			AssertThat(model.CachedResolvedSize).IsEqual((long)joinedModel.Length);
			AssertThat(model.ReadHead(79)[..79]).IsEqual(modelHeader[..79]);
			AssertThat(model.ReadHead(500)[..500]).IsEqual(joinedModel[..500]);

			AssertThat(mount.TryGetFile("m/nobody.mdl", out BnkFile stub)).IsTrue();
			AssertThat(stub.ReadAllBytes()).IsEqual(modelHeader);
			AssertThat(stub.ReadHead(500)).IsEqual(modelHeader);

			// A loose file replaces the bank's two parts, and so does its head.
			AssertThat(mount.TryGetFile("m/loose.mdl", out BnkFile override_)).IsTrue();
			AssertThat(override_.ReadAllBytes()).IsEqual(loose);
			AssertThat(override_.ReadHead(79)[..79]).IsEqual(loose[..79]);

			AssertThat(mount.TryGetFile("t/whole.tex", out BnkFile wholeFile)).IsTrue();
			byte[] joinedWhole = [.. wholeHeader, .. whole];
			AssertThat(wholeFile.ReadAllBytes()).IsEqual(joinedWhole);
			AssertThat(wholeFile.ReadHead(84)[..84]).IsEqual(joinedWhole[..84]);
			AssertThat(wholeFile.ReadHead(92)[..92]).IsEqual(joinedWhole[..92]);
			AssertThat(mount.TryGetFile("t/split.tex", out BnkFile splitFile)).IsTrue();
			byte[] joinedSplit = [.. splitHeader, .. mip0, .. rest];
			AssertThat(splitFile.ReadAllBytes()).IsEqual(joinedSplit);
			AssertThat(splitFile.ReadHead(92)[..92]).IsEqual(joinedSplit[..92]);

			// The guest's in-place read gives the same bytes as the whole read: joined, overridden,
			// stub, whole or a front, and a buffer longer than the file takes only the file.
			foreach ((BnkFile file, byte[] expected) in new[]
			{
				(model, joinedModel), (stub, modelHeader), (override_, loose),
				(wholeFile, joinedWhole), (splitFile, joinedSplit),
			})
			{
				foreach (int count in new[] { 8, 92, expected.Length, expected.Length + 40 })
				{
					var into = new byte[count];
					Array.Fill(into, (byte)0xCD);
					int written = file.ReadInto(into);
					int expectedCount = Math.Min(count, expected.Length);
					AssertThat(written).IsEqual(expectedCount);
					AssertThat(into.AsSpan(0, written).SequenceEqual(expected.AsSpan(0, written))).IsTrue();
				}
			}
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[TestCase]
	public void GunzipAnswersAsTheStreamDoes()
	{
		byte[] plain = Pattern(200_000, 16);
		byte[] one = Gzip(plain);
		byte[] two = [.. one, .. Gzip(Pattern(5000, 17))];
		byte[] lyingTrailer = (byte[])one.Clone();
		lyingTrailer[^4] ^= 0x55;
		byte[] truncated = one[..(one.Length / 2)];

		foreach (byte[] source in new[] { one, two, lyingTrailer, truncated, Gzip(Array.Empty<byte>()) })
		{
			byte[] bytes = source;
			bool inflated = Refii.Core.Engine_GameGzip.MaybeGunzip(ref bytes);

			byte[]? expected = null;
			try
			{
				using var input = new MemoryStream(source);
				using var gzip = new GZipStream(input, CompressionMode.Decompress);
				using var output = new MemoryStream();
				gzip.CopyTo(output);
				expected = output.ToArray();
			}
			catch (InvalidDataException)
			{
			}
			catch (IOException)
			{
			}

			AssertThat(inflated).IsEqual(expected is not null);
			AssertThat(bytes).IsEqual(expected ?? source);
		}
	}

	private static byte[] Gzip(byte[] plain)
	{
		var output = new MemoryStream();
		using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
		{
			gzip.Write(plain);
		}

		// a GZipStream given no bytes writes nothing on .NET 9+; an empty member is header, empty stored block, zero trailer.
		return output.Length == 0 ? new byte[] { 0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0xFF, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0 } : output.ToArray();
	}

	private static byte[] TexHeaderBytes(int payload, int mip0)
	{
		var header = new byte[84];
		BinaryPrimitives.WriteInt32BigEndian(header, -1);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x04), payload);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x10), 64);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x14), 64);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x18), 35);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x1C), 2);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x20), 84);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0x24), 84 + mip0);
		return header;
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
