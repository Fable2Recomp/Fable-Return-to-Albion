using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Refii.Bnk;

/// <summary>
/// A byte range of a bank file that is memory-mapped rather than copied onto the managed heap.
/// </summary>
/// <remarks>
/// A whole outer bank (levels.bnk, streaming.bnk, the texture banks) is mapped once; a nested
/// container stored uncompressed inside it is a sub-range of that map, and a nested archive
/// parsed from the sub-range reads its entries straight out of the OS page cache. Nothing in
/// the chain is a managed array, so the bytes never reach the large-object heap and residency
/// is the kernel's to decide. Every read makes its own stream over the shared buffer, so there
/// is no shared position and no lock.
///
/// The map is owned by the outermost range; sub-ranges borrow it and must not outlive it, which
/// holds because the file system keeps every outer archive open for as long as it is mounted.
/// </remarks>
public sealed class BnkMappedRange : IDisposable
{
	private readonly MemoryMappedFile? _file;
	private readonly MemoryMappedViewAccessor? _view;
	private readonly SafeBuffer _buffer;

	private BnkMappedRange(
		MemoryMappedFile? file, MemoryMappedViewAccessor? view, SafeBuffer buffer, long offset, long length)
	{
		_file = file;
		_view = view;
		_buffer = buffer;
		Offset = offset;
		Length = length;
	}

	/// <summary>Where this range starts in the mapped buffer.</summary>
	public long Offset { get; }

	/// <summary>Bytes in this range.</summary>
	public long Length { get; }

	/// <summary>
	/// Maps a whole file read-only. False when the file cannot be mapped (empty, or the platform
	/// refuses); the caller then reads through its stream as before.
	/// </summary>
	public static bool TryMapFile(FileStream stream, out BnkMappedRange? range)
	{
		range = null;
		long length = stream.Length;
		if (length <= 0)
		{
			return false;
		}

		MemoryMappedFile? file = null;
		MemoryMappedViewAccessor? view = null;
		try
		{
			file = MemoryMappedFile.CreateFromFile(
				stream, null, length, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
			view = file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
			range = new BnkMappedRange(file, view, view.SafeMemoryMappedViewHandle, 0, length);
			return true;
		}
		catch (Exception)
		{
			view?.Dispose();
			file?.Dispose();
			return false;
		}
	}

	/// <summary>A sub-range; throws when it falls outside this one.</summary>
	public BnkMappedRange Slice(long offset, long length)
	{
		Check(offset, length);
		return new BnkMappedRange(null, null, _buffer, Offset + offset, length);
	}

	/// <summary>A fresh read-only stream over part of the range, positioned at its start.</summary>
	public UnmanagedMemoryStream OpenStream(long offset, long length)
	{
		Check(offset, length);
		return new UnmanagedMemoryStream(_buffer, Offset + offset, length, FileAccess.Read);
	}

	/// <summary>A fresh read-only stream over the whole range.</summary>
	public UnmanagedMemoryStream OpenStream() => OpenStream(0, Length);

	/// <summary>Copies part of the range out.</summary>
	public void CopyTo(long offset, Span<byte> destination)
	{
		Check(offset, destination.Length);
		_buffer.ReadSpan((ulong)(Offset + offset), destination);
	}

	private void Check(long offset, long length)
	{
		if (offset < 0 || length < 0 || offset > Length - length)
		{
			throw new ArgumentOutOfRangeException(
				nameof(offset), $"range [{offset}, {offset + length}) exceeds the mapped {Length} bytes");
		}
	}

	/// <summary>Releases the map. A sub-range owns nothing and this is a no-op for it.</summary>
	public void Dispose()
	{
		_view?.Dispose();
		_file?.Dispose();
	}
}
