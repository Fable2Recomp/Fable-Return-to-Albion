using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Refii.Launcher.Xiso;

public static class XisoExtractor
{
	private const ulong GlobalLseekOffset = 0x0FD90000UL;
	private const ulong Xgd3LseekOffset = 0x02080000UL;
	private const ulong Xgd1LseekOffset = 0x18300000UL;

	private const ulong HeaderOffset = 0x10000UL;
	private const ulong OptimizedTagOffset = 31337UL;
	private const int HeaderDataLength = 20;
	private const int SectorSize = 2048;
	private const int EntryHeaderSize = 14;
	private const int CopyBufferSize = 2 * 1024 * 1024;
	private const ushort PadShort = 0xffff;
	private const byte AttributeDirectory = 0x10;

	private static readonly byte[] HeaderData = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");
	private static readonly byte[] OptimizedTagPrefix = Encoding.ASCII.GetBytes("in!xiso");

	public sealed class ExtractProgress
	{
		public long TotalBytes;
		public long CopiedBytes;
		public int TotalFiles;
		public int CompletedFiles;
		public string CurrentFile = string.Empty;
	}

	public static bool ExtractImage(string isoPath, string destinationDirectory, out string error) =>
		ExtractImage(isoPath, destinationDirectory, out error, null);

	public static bool ExtractImage(
		string isoPath,
		string destinationDirectory,
		out string error,
		Action<ExtractProgress>? onProgress)
	{
		var extractor = new Extractor { OnProgress = onProgress };
		return extractor.Extract(isoPath, destinationDirectory, out error);
	}

	private sealed class DirectoryEntry
	{
		public ushort LeftOffset;
		public ushort RightOffset;
		public uint StartSector;
		public uint FileSize;
		public bool IsDirectory;
		public string Filename = string.Empty;
	}

	private sealed class ParsedEntry
	{
		public DirectoryEntry Entry = new();
		public int EntryOffset;
		public int NextOffset;
	}

	private sealed class Extractor
	{
		private string _imagePath = string.Empty;
		private FileStream? _image;
		private ulong _discOffset;
		private byte[] _copyBuffer = Array.Empty<byte>();
		private bool _countOnly;
		private readonly ExtractProgress _progress = new();

		public Action<ExtractProgress>? OnProgress;

		private void Report()
		{
			OnProgress?.Invoke(_progress);
		}

		public bool Extract(string isoPath, string destinationDirectory, out string error)
		{
			error = string.Empty;
			_imagePath = isoPath;

			try
			{
				_image = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			}
			catch (Exception)
			{
				return Fail("Failed to open " + isoPath + ".", out error);
			}

			using (_image)
			{
				bool optimized = DetectOptimizedTag();

				if (!DetectDiscLayout(out uint rootSector, out uint rootSize, out error))
				{
					return false;
				}

				if (rootSector == 0 && rootSize == 0)
				{
					return Fail("The Xbox ISO contains no files.", out error);
				}

				_copyBuffer = new byte[CopyBufferSize];

				_countOnly = true;
				if (!TraverseDirectory(
						(ulong)rootSector * SectorSize + _discOffset,
						rootSize,
						destinationDirectory,
						!optimized,
						out error))
				{
					return false;
				}

				_countOnly = false;
				_progress.TotalFiles = _progress.CompletedFiles;
				_progress.TotalBytes = _progress.CopiedBytes;
				_progress.CompletedFiles = 0;
				_progress.CopiedBytes = 0;
				Report();

				return TraverseDirectory(
					(ulong)rootSector * SectorSize + _discOffset,
					rootSize,
					destinationDirectory,
					!optimized,
					out error);
			}
		}

		private bool Seek(ulong offset)
		{
			try
			{
				_image!.Seek((long)offset, SeekOrigin.Begin);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private bool ReadExact(byte[] buffer, int size)
		{
			if (size == 0)
			{
				return true;
			}

			int total = 0;
			while (total < size)
			{
				int read = _image!.Read(buffer, total, size - total);
				if (read <= 0)
				{
					return false;
				}

				total += read;
			}

			return true;
		}

		private bool ReadAt(ulong offset, byte[] buffer, int size) =>
			Seek(offset) && ReadExact(buffer, size);

		private bool DetectOptimizedTag()
		{
			var buffer = new byte[OptimizedTagPrefix.Length];
			return ReadAt(OptimizedTagOffset, buffer, buffer.Length) &&
				   Matches(buffer, OptimizedTagPrefix, OptimizedTagPrefix.Length);
		}

		private bool ReadFilesystemHeader(ulong candidateOffset, out uint rootSector, out uint rootSize)
		{
			rootSector = 0;
			rootSize = 0;

			var header = new byte[HeaderDataLength];
			if (!ReadAt(candidateOffset + HeaderOffset, header, header.Length) ||
				!Matches(header, HeaderData, HeaderDataLength))
			{
				return false;
			}

			var rootData = new byte[8];
			if (!ReadAt(candidateOffset + HeaderOffset + HeaderDataLength, rootData, rootData.Length))
			{
				return false;
			}

			var footer = new byte[HeaderDataLength];
			ulong footerOffset = candidateOffset + HeaderOffset + HeaderDataLength + 4 + 4 + 8 + 0x7c8;
			if (!ReadAt(footerOffset, footer, footer.Length) ||
				!Matches(footer, HeaderData, HeaderDataLength))
			{
				return false;
			}

			rootSector = BitConverter.ToUInt32(rootData, 0);
			rootSize = BitConverter.ToUInt32(rootData, 4);
			return true;
		}

		private bool DetectDiscLayout(out uint rootSector, out uint rootSize, out string error)
		{
			error = string.Empty;
			ulong[] offsets = { 0UL, GlobalLseekOffset, Xgd3LseekOffset, Xgd1LseekOffset };

			foreach (ulong offset in offsets)
			{
				if (ReadFilesystemHeader(offset, out rootSector, out rootSize))
				{
					_discOffset = offset;
					return true;
				}
			}

			rootSector = 0;
			rootSize = 0;
			return Fail(_imagePath + " does not appear to be a valid Xbox ISO image.", out error);
		}

		private bool LoadDirectoryTable(
			ulong absoluteOffset, uint directorySize, out byte[] table, out string error)
		{
			error = string.Empty;
			if (directorySize == 0)
			{
				table = Array.Empty<byte>();
				return true;
			}

			int bytesToRead = Math.Max(AlignToSector((int)directorySize), SectorSize);
			table = new byte[bytesToRead];
			if (!ReadAt(absoluteOffset, table, table.Length))
			{
				return Fail(
					"Failed to read an XDVDFS directory table from " + _imagePath + ".", out error);
			}

			return true;
		}

		private bool ParseEntry(
			byte[] table, int requestedOffset, ParsedEntry parsed, out bool emptyDirectory, out string error)
		{
			emptyDirectory = false;
			error = string.Empty;

			if (requestedOffset >= table.Length)
			{
				return Fail("Encountered an invalid directory entry offset.", out error);
			}

			int offset = requestedOffset;
			while (true)
			{
				if (offset + sizeof(ushort) > table.Length)
				{
					return Fail("Encountered a truncated directory table.", out error);
				}

				ushort marker = BitConverter.ToUInt16(table, offset);
				if (marker != PadShort)
				{
					break;
				}

				if (offset == 0)
				{
					emptyDirectory = true;
					return true;
				}

				offset = AlignToSector(offset + 1);
				if (offset >= table.Length)
				{
					return Fail("Encountered a truncated directory table.", out error);
				}
			}

			if (offset + EntryHeaderSize > table.Length)
			{
				return Fail("Encountered a truncated directory entry.", out error);
			}

			int filenameLength = table[offset + 13];
			if (filenameLength == 0 || offset + EntryHeaderSize + filenameLength > table.Length)
			{
				return Fail("Encountered a truncated XDVDFS filename.", out error);
			}

			parsed.EntryOffset = offset;
			parsed.Entry.LeftOffset = BitConverter.ToUInt16(table, offset);
			parsed.Entry.RightOffset = BitConverter.ToUInt16(table, offset + 2);
			parsed.Entry.StartSector = BitConverter.ToUInt32(table, offset + 4);
			parsed.Entry.FileSize = BitConverter.ToUInt32(table, offset + 8);
			parsed.Entry.IsDirectory = (table[offset + 12] & AttributeDirectory) != 0;
			parsed.Entry.Filename = Encoding.ASCII.GetString(
				table, offset + EntryHeaderSize, filenameLength);
			parsed.NextOffset = offset + EntryHeaderSize + filenameLength;

			if (HasInvalidFilename(parsed.Entry.Filename))
			{
				return Fail(
					"Encountered an invalid filename while extracting " + _imagePath + ".", out error);
			}

			return true;
		}

		private bool TraverseTree(
			byte[] table,
			int entryOffset,
			string outputPath,
			bool linkedListCompat,
			HashSet<int> visitedEntries,
			out string error)
		{
			var parsed = new ParsedEntry();
			if (!ParseEntry(table, entryOffset, parsed, out bool emptyDirectory, out error))
			{
				return false;
			}

			if (emptyDirectory)
			{
				return true;
			}

			if (!visitedEntries.Add(parsed.EntryOffset))
			{
				return Fail("Encountered a cyclic directory tree inside the Xbox ISO.", out error);
			}

			DirectoryEntry entry = parsed.Entry;
			if (entry.LeftOffset != 0)
			{
				linkedListCompat = false;
			}

			if (entry.LeftOffset != 0 &&
				!TraverseTree(table, entry.LeftOffset * 4, outputPath, linkedListCompat, visitedEntries, out error))
			{
				return false;
			}

			string entryPath = Path.Combine(outputPath, entry.Filename);
			if (entry.IsDirectory)
			{
				if (!_countOnly)
				{
					try
					{
						Directory.CreateDirectory(entryPath);
					}
					catch (Exception exception)
					{
						return Fail(exception.Message, out error);
					}
				}

				if (entry.FileSize > 0 &&
					!TraverseDirectory(
						(ulong)entry.StartSector * SectorSize + _discOffset,
						entry.FileSize,
						entryPath,
						linkedListCompat,
						out error))
				{
					return false;
				}
			}
			else if (_countOnly)
			{
				_progress.CompletedFiles++;
				_progress.CopiedBytes += entry.FileSize;
			}
			else
			{
				if (!ExtractFile(entry, entryPath, out error))
				{
					return false;
				}

				_progress.CompletedFiles++;
				Report();
			}

			if (entry.RightOffset != 0)
			{
				ushort rightOffset = entry.RightOffset;
				int currentSector = parsed.NextOffset / SectorSize;
				if (linkedListCompat && rightOffset * 4 / SectorSize > currentSector)
				{
					rightOffset = (ushort)((currentSector + 1) * (SectorSize / 4));
				}

				if (!TraverseTree(
						table, rightOffset * 4, outputPath, linkedListCompat, visitedEntries, out error))
				{
					return false;
				}
			}

			return true;
		}

		private bool TraverseDirectory(
			ulong absoluteOffset,
			uint directorySize,
			string outputPath,
			bool linkedListCompat,
			out string error)
		{
			if (!LoadDirectoryTable(absoluteOffset, directorySize, out byte[] table, out error))
			{
				return false;
			}

			if (table.Length == 0)
			{
				return true;
			}

			return TraverseTree(table, 0, outputPath, linkedListCompat, new HashSet<int>(), out error);
		}

		private bool ExtractFile(DirectoryEntry entry, string outputPath, out string error)
		{
			error = string.Empty;

			try
			{
				string? parent = Path.GetDirectoryName(outputPath);
				if (!string.IsNullOrEmpty(parent))
				{
					Directory.CreateDirectory(parent!);
				}
			}
			catch (Exception exception)
			{
				return Fail(exception.Message, out error);
			}

			FileStream output;
			try
			{
				output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
			}
			catch (Exception)
			{
				return Fail("Failed to create " + outputPath + ".", out error);
			}

			using (output)
			{
				if (entry.FileSize == 0)
				{
					return true;
				}

				ulong fileOffset = (ulong)entry.StartSector * SectorSize + _discOffset;
				if (!Seek(fileOffset))
				{
					return Fail("Failed to seek inside " + _imagePath + ".", out error);
				}

				_progress.CurrentFile = entry.Filename;
				uint remaining = entry.FileSize;
				while (remaining > 0)
				{
					int chunkSize = (int)Math.Min(remaining, (uint)_copyBuffer.Length);
					if (!ReadExact(_copyBuffer, chunkSize))
					{
						return Fail(
							"Unexpected end of file while extracting " + entry.Filename + ".", out error);
					}

					try
					{
						output.Write(_copyBuffer, 0, chunkSize);
						_progress.CopiedBytes += chunkSize;
						Report();
					}
					catch (Exception)
					{
						return Fail("Failed to write " + outputPath + ".", out error);
					}

					remaining -= (uint)chunkSize;
				}
			}

			return true;
		}

		private static bool Fail(string message, out string error)
		{
			error = message;
			return false;
		}
	}

	private static bool Matches(byte[] buffer, byte[] expected, int length)
	{
		for (int i = 0; i < length; i++)
		{
			if (buffer[i] != expected[i])
			{
				return false;
			}
		}

		return true;
	}

	private static int AlignToSector(int value)
	{
		if (value == 0)
		{
			return 0;
		}

		int remainder = value % SectorSize;
		return remainder == 0 ? value : value + (SectorSize - remainder);
	}

	private static bool HasInvalidFilename(string filename)
	{
		if (filename.Length == 0 || filename == "." || filename == ".." || filename.Contains('\0'))
		{
			return true;
		}

		char last = filename[^1];
		if (last == ' ' || last == '.')
		{
			return true;
		}

		foreach (char ch in filename)
		{
			if (ch <= 0x1F || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
			{
				return true;
			}
		}

		string lowered = filename.ToLowerInvariant();
		int dot = lowered.IndexOf('.');
		string baseName = dot >= 0 ? lowered[..dot] : lowered;

		if (lowered.Length == 0 || baseName.Length == 0)
		{
			return true;
		}

		if (baseName is "con" or "prn" or "aux" or "nul" or "clock$")
		{
			return true;
		}

		return baseName.Length == 4 &&
			   ((baseName[0] == 'c' && baseName[1] == 'o' && baseName[2] == 'm' && char.IsDigit(baseName[3])) ||
				(baseName[0] == 'l' && baseName[1] == 'p' && baseName[2] == 't' && char.IsDigit(baseName[3])));
	}
}
