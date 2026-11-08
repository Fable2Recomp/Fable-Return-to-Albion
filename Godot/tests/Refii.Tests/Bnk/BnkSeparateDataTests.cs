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
using Refii.Core;

/// <summary>
/// Format-4 banks (Fable 3): the header file is the table alone, records keyed by the resource
/// hash with the names after them, and the entries' bytes in a separate <c>.dat</c>. The
/// fixtures are laid out as the disc's are — no terminator chunk, the header pointer at the end
/// of the file, data offsets from the start of the <c>.dat</c>. No Godot type is touched.
/// </summary>
[TestSuite]
public class BnkSeparateDataTests
{
	private const int ChunkSize = 0x8000;

	private static string NewTempDir()
	{
		string dir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_bnk_f4_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static byte[] Pattern(int length, int seed)
	{
		var bytes = new byte[length];
		for (int i = 0; i < length; i++)
		{
			bytes[i] = (byte)((i * 13 + seed) % 251);
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
	public void HeaderBesideItsDatReadsEveryEntryByName()
	{
		byte[] script = Pattern(1905, 1);
		byte[] large = Pattern(300_000, 2);
		(byte[] header, byte[] data) = WriteBank(new[]
		{
			("scripts\\ai\\aibase.lua", script),
			("Art\\GUI\\Big.bin", large),
		}, compressed: false);

		string dir = NewTempDir();
		try
		{
			string path = Path.Combine(dir, "gamescripts.bnk");
			File.WriteAllBytes(path, header);
			File.WriteAllBytes(path + ".dat", data);

			AssertThat(BnkArchive.TryOpen(path, out BnkArchive? archive, out string error)).IsTrue();
			using (archive)
			{
				AssertThat(archive!.FormatVersion).IsEqual(4);
				AssertThat(archive.DataIsSeparate).IsTrue();
				AssertThat(archive.HasData).IsTrue();
				AssertThat(archive.IsMapped).IsTrue();
				AssertThat(archive.Entries.Count).IsEqual(2);

				// Names come back in the mount's spelling, case as authored.
				AssertThat(archive.Entries[0].RelativePath).IsEqual("scripts/ai/aibase.lua");
				AssertThat(archive.Entries[1].RelativePath).IsEqual("Art/GUI/Big.bin");

				AssertThat(archive.ReadEntryBytes(Find(archive, "scripts/ai/aibase.lua"))).IsEqual(script);
				AssertThat(archive.ReadEntryBytes(Find(archive, "art/gui/big.bin"))).IsEqual(large);
			}
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[TestCase]
	public void CompressedEntriesInflateOutOfTheDat()
	{
		byte[] payload = Pattern(ChunkSize * 2 + 4_321, 3);
		byte[] tiny = Pattern(168, 4);
		(byte[] header, byte[] data) = WriteBank(new[]
		{
			("art\\gui\\gameface\\abilitiesmanager.lua", tiny),
			("art\\gui\\gameface\\bedmenubox.bgf", payload),
		}, compressed: true);

		string dir = NewTempDir();
		try
		{
			string path = Path.Combine(dir, "guiscripts.bnk");
			File.WriteAllBytes(path, header);
			File.WriteAllBytes(path + ".dat", data);

			AssertThat(BnkArchive.TryOpen(path, out BnkArchive? archive, out string error)).IsTrue();
			using (archive)
			{
				AssertThat(archive!.FileDataCompressed).IsTrue();
				BnkEntry big = Find(archive, "art/gui/gameface/bedmenubox.bgf");
				AssertThat(big.IsCompressed).IsTrue();
				AssertThat(big.ChunkSizes.Count).IsEqual(3);
				AssertThat(archive.ReadEntryBytes(big)).IsEqual(payload);
				AssertThat(archive.ReadEntryBytes(Find(archive, "art/gui/gameface/abilitiesmanager.lua"))).IsEqual(tiny);
			}
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	/// <summary>
	/// A header nested in another bank (globals_textures.bnk inside levels.bnk) opens with no
	/// data: the table is readable, an entry read says which .dat is missing, and attaching
	/// the data makes every entry readable.
	/// </summary>
	[TestCase]
	public void HeaderWithoutItsDatReadsOnlyAfterTheDataIsAttached()
	{
		byte[] texture = Pattern(92_000, 5);
		(byte[] header, byte[] data) = WriteBank(new[]
		{
			("art\\fx\\pfx_textures\\fxt_flames_double_03.tex", texture),
		}, compressed: false);

		AssertThat(BnkArchive.TryOpenBytes("globals/globals_textures.bnk", header, out BnkArchive? archive, out _)).IsTrue();
		using (archive)
		{
			AssertThat(archive!.DataIsSeparate).IsTrue();
			AssertThat(archive.HasData).IsFalse();

			BnkEntry entry = Find(archive, "art/fx/pfx_textures/fxt_flames_double_03.tex");
			AssertThat(archive.TryMapEntry(entry, out _)).IsFalse();
			string message = string.Empty;
			try
			{
				archive.ReadEntryBytes(entry);
			}
			catch (InvalidDataException exception)
			{
				message = exception.Message;
			}

			AssertThat(message).Contains("globals_textures.bnk.dat");

			archive.AttachData(new MemoryStream(data, false), null, ownsRange: false);
			AssertThat(archive.HasData).IsTrue();
			AssertThat(archive.ReadEntryBytes(entry)).IsEqual(texture);
		}
	}

	/// <summary>A name that does not hash to its record means the table was misread.</summary>
	[TestCase]
	public void NameThatDoesNotMatchItsHashIsRejected()
	{
		(byte[] header, _) = WriteBank(new[]
		{
			("scripts\\a.lua", Pattern(10, 6)),
			("scripts\\b.lua", Pattern(20, 7)),
		}, compressed: false, swapNames: true);

		AssertThat(BnkArchive.TryOpenBytes("swapped.bnk", header, out _, out string error)).IsFalse();
		AssertThat(error).Contains("does not match its record's hash");
	}

	/// <summary>
	/// A format-4 header and its <c>.dat</c>. Compressed entries are 32 KB slots of one zlib
	/// stream each, the last cut to its stream, as format 3's are.
	/// </summary>
	private static (byte[] Header, byte[] Data) WriteBank(
		IReadOnlyList<(string Name, byte[] Bytes)> entries, bool compressed, bool swapNames = false)
	{
		var data = new MemoryStream();
		var records = new MemoryStream();
		WriteWord(records, 0);
		WriteWord(records, (uint)entries.Count);

		foreach ((string name, byte[] bytes) in entries)
		{
			uint offset = (uint)data.Length;
			WriteWord(records, Engine_GameHash.ResourcePath(name));
			WriteWord(records, offset);
			WriteWord(records, (uint)bytes.Length);

			if (!compressed)
			{
				data.Write(bytes);
				continue;
			}

			int chunkCount = Math.Max(1, (bytes.Length + ChunkSize - 1) / ChunkSize);
			var chunkSizes = new uint[chunkCount];
			var stored = new MemoryStream();
			for (int i = 0; i < chunkCount; i++)
			{
				int start = i * ChunkSize;
				int length = Math.Min(ChunkSize, bytes.Length - start);
				chunkSizes[i] = (uint)length;
				byte[] chunk = Deflate(bytes, start, length);
				stored.Write(chunk);
				if (i != chunkCount - 1)
				{
					stored.Write(new byte[ChunkSize - chunk.Length]);
				}
			}

			WriteWord(records, (uint)stored.Length);
			WriteWord(records, (uint)chunkCount);
			foreach (uint size in chunkSizes)
			{
				WriteWord(records, size);
			}

			stored.Position = 0;
			stored.CopyTo(data);
		}

		for (int i = 0; i < entries.Count; i++)
		{
			int source = swapNames ? entries.Count - 1 - i : i;
			byte[] name = Encoding.UTF8.GetBytes(entries[source].Name);
			WriteWord(records, (uint)name.Length + 1);
			records.Write(name);
			records.WriteByte(0);
			records.Write(new byte[28]);
		}

		byte[] rawTable = records.ToArray();
		byte[] compressedTable = Deflate(rawTable, 0, rawTable.Length);
		uint tablePointer = (uint)(9 + 8 + compressedTable.Length);

		var header = new MemoryStream();
		WriteWord(header, tablePointer);
		WriteWord(header, 4);
		header.WriteByte(compressed ? (byte)1 : (byte)0);
		WriteWord(header, (uint)compressedTable.Length);
		WriteWord(header, (uint)rawTable.Length);
		header.Write(compressedTable);
		return (header.ToArray(), data.ToArray());
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
