using System;
using System.Runtime.InteropServices;
using System.Text;
using Refii.Core;

namespace Refii.Lua;

/// <summary>How a decompiled text relates to the chunk it came from.</summary>
public enum LuaVerifyResult
{
	/// <summary>Recompiles to the same bytes.</summary>
	Exact = 0,
	/// <summary>Same code and constants; only debug info (lines, local names) differs.</summary>
	CodeExact = 1,
	/// <summary>The code differs.</summary>
	Mismatch = 2,
	/// <summary>The text does not compile.</summary>
	CompileError = 3,
	/// <summary>The original is not a readable chunk.</summary>
	Invalid = 4,
}

/// <summary>
/// The luadec decompiler and its byte-exact oracle (src/lua/src/tools/luadec), reached through
/// the refii_luadec host library. The editor decompiles a script-bank entry on demand and checks
/// an edited copy with the game's own compiler; nothing here touches disk.
/// </summary>
public static class Engine_LuaDecompiler
{
	private const string Library = "refii_luadec";
	public const int ExpectedAbiVersion = 1;
	private const int MessageCapacity = 2048;

	static Engine_LuaDecompiler() => Engine_NativeLibraries.Install();

	public static int? TryGetAbiVersion()
	{
		try
		{
			return RefiiLuadecAbiVersion();
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	public static bool Available => TryGetAbiVersion() == ExpectedAbiVersion;

	/// <summary>Whether the bytes are a compiled Lua 5.1 chunk rather than source text.</summary>
	public static bool IsChunk(byte[] bytes) =>
		bytes is { Length: >= 4 } && bytes[0] == 0x1b && bytes[1] == (byte)'L' &&
		bytes[2] == (byte)'u' && bytes[3] == (byte)'a';

	/// <summary>
	/// Decompiles <paramref name="chunk"/> and verifies the text against it. False when the
	/// chunk cannot be read or its structure cannot be recovered; <paramref name="message"/>
	/// then says why (and on success carries the oracle's detail when not exact).
	/// </summary>
	public static bool TryDecompile(
		byte[] chunk, out string text, out LuaVerifyResult verify, out string message)
	{
		text = string.Empty;
		verify = LuaVerifyResult.Invalid;
		message = string.Empty;
		if (!IsChunk(chunk))
		{
			message = "not a Lua chunk";
			return false;
		}

		var buffer = new byte[MessageCapacity];
		IntPtr native = IntPtr.Zero;
		try
		{
			int ok = RefiiLuadecDecompile(
				chunk, (uint)chunk.Length, out native, out uint size, out int code,
				buffer, (uint)buffer.Length);
			message = ReadMessage(buffer);
			if (ok == 0 || native == IntPtr.Zero)
			{
				return false;
			}

			text = Marshal.PtrToStringUTF8(native, (int)size) ?? string.Empty;
			verify = (LuaVerifyResult)code;
			return true;
		}
		catch (DllNotFoundException)
		{
			message = "the refii_luadec library is not available";
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			message = "the refii_luadec library is out of date";
			return false;
		}
		finally
		{
			if (native != IntPtr.Zero)
			{
				RefiiLuadecFree(native);
			}
		}
	}

	/// <summary>The instruction listing of a chunk, for one that will not decompile.</summary>
	public static bool TryDisassemble(byte[] chunk, out string text, out string message)
	{
		text = string.Empty;
		message = string.Empty;
		var buffer = new byte[MessageCapacity];
		IntPtr native = IntPtr.Zero;
		try
		{
			int ok = RefiiLuadecDisassemble(
				chunk, (uint)chunk.Length, out native, out uint size, buffer, (uint)buffer.Length);
			message = ReadMessage(buffer);
			if (ok == 0 || native == IntPtr.Zero)
			{
				return false;
			}

			text = Marshal.PtrToStringUTF8(native, (int)size) ?? string.Empty;
			return true;
		}
		catch (DllNotFoundException)
		{
			message = "the refii_luadec library is not available";
			return false;
		}
		finally
		{
			if (native != IntPtr.Zero)
			{
				RefiiLuadecFree(native);
			}
		}
	}

	/// <summary>Compiles <paramref name="text"/> with the game's compiler; the error names the line.</summary>
	public static bool TryCheckSource(string text, string chunkName, out string error)
	{
		error = string.Empty;
		byte[] utf8 = Encoding.UTF8.GetBytes(text ?? string.Empty);
		var buffer = new byte[MessageCapacity];
		try
		{
			int ok = RefiiLuadecCheckSource(
				utf8, (uint)utf8.Length, chunkName, buffer, (uint)buffer.Length);
			error = ReadMessage(buffer);
			return ok != 0;
		}
		catch (DllNotFoundException)
		{
			error = "the refii_luadec library is not available";
			return false;
		}
	}

	/// <summary>Verifies an edited text against the chunk it was decompiled from.</summary>
	public static LuaVerifyResult Verify(byte[] chunk, string text, out string message)
	{
		message = string.Empty;
		byte[] utf8 = Encoding.UTF8.GetBytes(text ?? string.Empty);
		var buffer = new byte[MessageCapacity];
		try
		{
			int code = RefiiLuadecVerify(
				chunk, (uint)chunk.Length, utf8, (uint)utf8.Length, buffer, (uint)buffer.Length);
			message = ReadMessage(buffer);
			return (LuaVerifyResult)code;
		}
		catch (DllNotFoundException)
		{
			message = "the refii_luadec library is not available";
			return LuaVerifyResult.Invalid;
		}
	}

	private static string ReadMessage(byte[] buffer)
	{
		int length = Array.IndexOf(buffer, (byte)0);
		return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
	}

	[DllImport(Library)]
	private static extern int RefiiLuadecAbiVersion();

	[DllImport(Library)]
	private static extern int RefiiLuadecDecompile(
		byte[] bytes, uint size, out IntPtr text, out uint textSize, out int verify,
		byte[] message, uint messageCapacity);

	[DllImport(Library)]
	private static extern int RefiiLuadecDisassemble(
		byte[] bytes, uint size, out IntPtr text, out uint textSize,
		byte[] message, uint messageCapacity);

	[DllImport(Library)]
	private static extern int RefiiLuadecCheckSource(
		byte[] text, uint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string chunkName,
		byte[] message, uint messageCapacity);

	[DllImport(Library)]
	private static extern int RefiiLuadecVerify(
		byte[] bytes, uint size, byte[] text, uint textSize, byte[] message, uint messageCapacity);

	[DllImport(Library)]
	private static extern void RefiiLuadecFree(IntPtr text);
}
