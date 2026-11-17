using System;
using System.Buffers;

namespace Refii.Core;

/// <summary>
/// Native memory the caller owns, as <see cref="Memory{T}"/>, so code written for managed buffers
/// fills it in place. It is valid only as long as the native buffer is: the guest's read buffer
/// lives for one call.
/// </summary>
public sealed unsafe class Engine_NativeMemory(IntPtr pointer, int length) : MemoryManager<byte>
{
	public override Span<byte> GetSpan() => new((void*)pointer, length);

	public override MemoryHandle Pin(int elementIndex = 0)
	{
		if ((uint)elementIndex > (uint)length)
		{
			throw new ArgumentOutOfRangeException(nameof(elementIndex));
		}

		return new MemoryHandle((byte*)pointer + elementIndex);
	}

	public override void Unpin()
	{
	}

	protected override void Dispose(bool disposing)
	{
	}
}
