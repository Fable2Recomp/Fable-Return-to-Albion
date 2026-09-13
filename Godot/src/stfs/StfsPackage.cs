using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Refii.Launcher.Stfs;

/// <summary>
/// Reads one file out of an STFS container — the LIVE / PIRS / CON package a title update
/// ships in. Enough of the format to walk the file table and follow a file's blocks; no
/// hash verification, no writing.
/// </summary>
/// <remarks>
/// Layout, all offsets from the package start:
/// <list type="bullet">
/// <item>0x340 header size, 0x344 content type (0xB0000 = title update), 0x354 media id,
/// 0x360 title id; big-endian.</item>
/// <item>0x379 volume descriptor: +3 file-table block count (LITTLE-endian 16), +5 file-table
/// first block (LITTLE-endian 24).</item>
/// <item>Data blocks are 0x1000 bytes and start at 0xC000. Every 0xAA data blocks are led by a
/// level-0 hash table block, every 0x70E4 by a level-1 table, so a data block's file offset
/// is its number plus the tables in front of it. A header that rounds to 0xB000 has single
/// tables; any other size doubles them (<c>_tableSizeShift</c>).</item>
/// <item>File-table entries are 0x40 bytes: name (0x28), flags (bit 7 directory, bit 6
/// contiguous, low 6 bits name length), block count (LE 24), start block (LE 24) at 0x2F,
/// parent index (BE 16) at 0x32, size (BE 32) at 0x34. A zero name length ends the table.</item>
/// <item>A non-contiguous file's next block is in its level-0 hash entry: 0x18 bytes per
/// entry, next block as BE 24 at +0x15.</item>
/// </list>
/// </remarks>
public static class StfsPackage
{
	private const int BlockSize = 0x1000;
	private const int BlocksPerLevel0Table = 0xAA;
	private const int BlocksPerLevel1Table = 0x70E4;
	private const long FirstDataBlockOffset = 0xC000;
	private const int HeaderSizeOffset = 0x340;
	private const int ContentTypeOffset = 0x344;
	private const int MediaIdOffset = 0x354;
	private const int TitleIdOffset = 0x360;
	private const int VolumeDescriptorOffset = 0x379;
	private const int DescriptorTypeOffset = 0x3A9;
	private const int HeaderReadLength = 0x400;
	private const int EntrySize = 0x40;
	private const int EntriesPerBlock = BlockSize / EntrySize;
	private const int HashEntrySize = 0x18;
	private const int MaxNameLength = 0x28;
	private const int MaxFileTableBlocks = 256;

	public const uint ContentTypeTitleUpdate = 0xB0000;

	/// <summary>True for a LIVE, PIRS or CON signature.</summary>
	public static bool IsPackage(ReadOnlySpan<byte> head)
	{
		if (head.Length < 4)
		{
			return false;
		}

		return (head[0] == (byte)'L' && head[1] == (byte)'I' && head[2] == (byte)'V' && head[3] == (byte)'E') ||
			   (head[0] == (byte)'P' && head[1] == (byte)'I' && head[2] == (byte)'R' && head[3] == (byte)'S') ||
			   (head[0] == (byte)'C' && head[1] == (byte)'O' && head[2] == (byte)'N' && head[3] == (byte)' ');
	}

	public sealed class Entry
	{
		public string Name = string.Empty;
		public bool IsDirectory;
		public bool IsContiguous;
		public int BlockCount;
		public int StartBlock;
		public int ParentIndex;
		public uint Size;
	}

	public sealed class Header
	{
		public uint HeaderSize;
		public uint ContentType;
		public uint MediaId;
		public uint TitleId;
		public uint DescriptorType;
	}

	/// <summary>Reads the package header fields the launcher reports on.</summary>
	public static bool TryReadHeader(string path, out Header header, out string error)
	{
		header = new Header();
		error = string.Empty;

		byte[] head;
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			head = new byte[HeaderReadLength];
			if (!ReadExact(stream, 0, head, head.Length))
			{
				error = "The package is too short to be an STFS container.";
				return false;
			}
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}

		if (!IsPackage(head))
		{
			error = "The file does not start with an STFS signature.";
			return false;
		}

		header.HeaderSize = ReadUInt32BE(head, HeaderSizeOffset);
		header.ContentType = ReadUInt32BE(head, ContentTypeOffset);
		header.MediaId = ReadUInt32BE(head, MediaIdOffset);
		header.TitleId = ReadUInt32BE(head, TitleIdOffset);
		header.DescriptorType = ReadUInt32BE(head, DescriptorTypeOffset);
		return true;
	}

	/// <summary>Lists every file-table entry in the package.</summary>
	public static bool TryListEntries(string path, out List<Entry> entries, out string error)
	{
		entries = new List<Entry>();
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			var reader = new Reader(stream);
			if (!reader.Open(out error))
			{
				return false;
			}

			return reader.ListEntries(entries, out error);
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}

	/// <summary>
	/// Reads the first file the picker accepts, whole, into memory. Meant for the few-MB files
	/// a title update carries, not for a DLC's banks.
	/// </summary>
	public static bool TryReadFile(
		string path, Func<Entry, bool> pick, out Entry? picked, out byte[] data, out string error)
	{
		picked = null;
		data = Array.Empty<byte>();
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			var reader = new Reader(stream);
			if (!reader.Open(out error))
			{
				return false;
			}

			var entries = new List<Entry>();
			if (!reader.ListEntries(entries, out error))
			{
				return false;
			}

			foreach (Entry entry in entries)
			{
				if (entry.IsDirectory || !pick(entry))
				{
					continue;
				}

				picked = entry;
				return reader.ReadFile(entry, out data, out error);
			}

			error = "The package holds no file of that kind.";
			return false;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}

	private sealed class Reader
	{
		private readonly FileStream _stream;
		private int _tableSizeShift;
		private int _fileTableBlockCount;
		private int _fileTableStartBlock;

		public Reader(FileStream stream)
		{
			_stream = stream;
		}

		public bool Open(out string error)
		{
			error = string.Empty;
			var head = new byte[HeaderReadLength];
			if (!ReadExact(_stream, 0, head, head.Length))
			{
				error = "The package is too short to be an STFS container.";
				return false;
			}

			if (!IsPackage(head))
			{
				error = "The file does not start with an STFS signature.";
				return false;
			}

			uint descriptorType = ReadUInt32BE(head, DescriptorTypeOffset);
			if (descriptorType != 0)
			{
				error = "The package is an SVOD container, not STFS.";
				return false;
			}

			uint headerSize = ReadUInt32BE(head, HeaderSizeOffset);
			_tableSizeShift = ((headerSize + 0xFFF) & 0xF000) == 0xB000 ? 0 : 1;

			int descriptor = VolumeDescriptorOffset;
			_fileTableBlockCount = head[descriptor + 3] | (head[descriptor + 4] << 8);
			_fileTableStartBlock = ReadInt24LE(head, descriptor + 5);

			if (_fileTableBlockCount <= 0 || _fileTableBlockCount > MaxFileTableBlocks)
			{
				error = "The package's file table has an implausible block count.";
				return false;
			}

			return true;
		}

		public bool ListEntries(List<Entry> entries, out string error)
		{
			error = string.Empty;
			var block = new byte[BlockSize];
			int blockNumber = _fileTableStartBlock;

			for (int i = 0; i < _fileTableBlockCount; i++)
			{
				if (!ReadExact(_stream, BlockOffset(blockNumber), block, block.Length))
				{
					error = "The package's file table runs past the end of the file.";
					return false;
				}

				for (int e = 0; e < EntriesPerBlock; e++)
				{
					int at = e * EntrySize;
					byte flags = block[at + MaxNameLength];
					int nameLength = flags & 0x3F;
					if (nameLength == 0)
					{
						return true;
					}

					entries.Add(new Entry
					{
						Name = Encoding.Latin1.GetString(block, at, Math.Min(nameLength, MaxNameLength)),
						IsDirectory = (flags & 0x80) != 0,
						IsContiguous = (flags & 0x40) != 0,
						BlockCount = ReadInt24LE(block, at + 0x29),
						StartBlock = ReadInt24LE(block, at + 0x2F),
						ParentIndex = (block[at + 0x32] << 8) | block[at + 0x33],
						Size = ReadUInt32BE(block, at + 0x34),
					});
				}

				if (i + 1 < _fileTableBlockCount && !NextBlock(blockNumber, out blockNumber, out error))
				{
					return false;
				}
			}

			return true;
		}

		public bool ReadFile(Entry entry, out byte[] data, out string error)
		{
			error = string.Empty;
			data = new byte[entry.Size];
			long remaining = entry.Size;
			int written = 0;
			int blockNumber = entry.StartBlock;

			for (int i = 0; i < entry.BlockCount && remaining > 0; i++)
			{
				int chunk = (int)Math.Min(remaining, BlockSize);
				if (!ReadExact(_stream, BlockOffset(blockNumber), data, written, chunk))
				{
					error = entry.Name + " runs past the end of the package.";
					return false;
				}

				written += chunk;
				remaining -= chunk;

				if (remaining <= 0)
				{
					break;
				}

				if (entry.IsContiguous)
				{
					blockNumber++;
				}
				else if (!NextBlock(blockNumber, out blockNumber, out error))
				{
					return false;
				}
			}

			if (remaining > 0)
			{
				error = entry.Name + " is shorter in the package than its recorded size.";
				return false;
			}

			return true;
		}

		/// <summary>
		/// The next block in a chain, from the block's level-0 hash entry. With doubled tables
		/// the copy whose entry carries the higher status byte is the live one.
		/// </summary>
		private bool NextBlock(int blockNumber, out int next, out string error)
		{
			error = string.Empty;
			next = 0;
			int group = blockNumber / BlocksPerLevel0Table;
			long tableBase = BlockOffset(group * BlocksPerLevel0Table) - ((long)BlockSize << _tableSizeShift);
			int entryOffset = (blockNumber % BlocksPerLevel0Table) * HashEntrySize;
			var entry = new byte[HashEntrySize];

			int bestStatus = -1;
			for (int copy = 0; copy < (1 << _tableSizeShift); copy++)
			{
				long at = tableBase + (long)copy * BlockSize + entryOffset;
				if (!ReadExact(_stream, at, entry, entry.Length))
				{
					continue;
				}

				int status = entry[0x14];
				if (status > bestStatus)
				{
					bestStatus = status;
					next = (entry[0x15] << 16) | (entry[0x16] << 8) | entry[0x17];
				}
			}

			if (bestStatus < 0)
			{
				error = "The package's hash table runs past the end of the file.";
				return false;
			}

			if (next == 0xFFFFFF)
			{
				error = "A block chain in the package ends early.";
				return false;
			}

			return true;
		}

		private long BlockOffset(int blockNumber)
		{
			long adjusted = blockNumber;
			if (blockNumber >= BlocksPerLevel0Table)
			{
				adjusted += (long)(blockNumber / BlocksPerLevel0Table + 1) << _tableSizeShift;
			}

			if (blockNumber >= BlocksPerLevel1Table)
			{
				adjusted += (long)(blockNumber / BlocksPerLevel1Table + 1) << _tableSizeShift;
			}

			return FirstDataBlockOffset + adjusted * BlockSize;
		}
	}

	private static bool ReadExact(FileStream stream, long offset, byte[] buffer, int count) =>
		ReadExact(stream, offset, buffer, 0, count);

	private static bool ReadExact(FileStream stream, long offset, byte[] buffer, int start, int count)
	{
		if (offset < 0 || offset + count > stream.Length)
		{
			return false;
		}

		stream.Seek(offset, SeekOrigin.Begin);
		int total = 0;
		while (total < count)
		{
			int read = stream.Read(buffer, start + total, count - total);
			if (read <= 0)
			{
				return false;
			}

			total += read;
		}

		return true;
	}

	private static uint ReadUInt32BE(byte[] data, int at) =>
		((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];

	private static int ReadInt24LE(byte[] data, int at) =>
		data[at] | (data[at + 1] << 8) | (data[at + 2] << 16);
}
