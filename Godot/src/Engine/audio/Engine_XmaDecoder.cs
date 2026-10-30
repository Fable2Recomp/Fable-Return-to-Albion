using System;
using System.Runtime.InteropServices;
using System.Text;
using Refii.Core;
using Refii.Text;
using Refii.Ui;

namespace Refii.Audio;

public static class Engine_XmaDecoder
{
	// FFmpeg's ff_xma1_decoder/ff_xma2_decoder, wrapped by src/xmadecode — the same shape
	// as refii_binkdecode. This used to be refii_assetbrowser, whose Fable2AssetBrowser
	// submodule reimplemented the decoder FFmpeg already ships (and does not build with
	// clang on any architecture). The exported ABI below is unchanged.
	private const string Library = "refii_xmadecode";

	public const int ExpectedAbiVersion = 2;

	private const int ErrorCapacity = 512;

	static Engine_XmaDecoder() => Engine_NativeLibraries.Install();

	public static int? TryGetAbiVersion()
	{
		try
		{
			return RefiiAudioAbiVersion();
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

	public static bool TryDecode(
		byte[] source, out short[] pcm, out int sampleRate, out int channels, out string error) =>
		TryDecode(source, Engine_Title.Active, out pcm, out sampleRate, out channels, out error);

	public static bool TryDecode(
		byte[] source, Engine_Title title,
		out short[] pcm, out int sampleRate, out int channels, out string error)
	{
		pcm = Array.Empty<short>();
		sampleRate = 0;
		channels = 0;
		error = string.Empty;

		if (source is null || source.Length == 0)
		{
			error = "empty file";
			return false;
		}

		byte[] stated = ToDecoderByteOrder(source, title);
		var message = new byte[ErrorCapacity];
		IntPtr buffer = IntPtr.Zero;

		try
		{
			int ok = RefiiXmaDecode(
				stated, (nuint)stated.Length,
				out buffer, out nuint sampleCount,
				out sampleRate, out channels,
				message, ErrorCapacity);

			if (ok == 0 || buffer == IntPtr.Zero)
			{
				error = Decode(message);
				if (error.Length == 0)
				{
					error = "decode failed";
				}

				return false;
			}

			if (sampleCount == 0 || sampleCount > int.MaxValue)
			{
				error = $"implausible sample count {sampleCount}";
				return false;
			}

			pcm = new short[(int)sampleCount];
			Marshal.Copy(buffer, pcm, 0, pcm.Length);
			return true;
		}
		catch (DllNotFoundException)
		{
			error = "refii_xmadecode not found — build src/xmadecode";
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			error = "refii_xmadecode is missing the audio ABI — rebuild it";
			return false;
		}
		finally
		{
			if (buffer != IntPtr.Zero)
			{
				RefiiXmaFree(buffer);
			}
		}
	}

	public static bool IsNotXma(string error) =>
		error.Contains("not XMA", StringComparison.OrdinalIgnoreCase) ||
		error.Contains("no RIFF", StringComparison.OrdinalIgnoreCase);

	/// <summary>Size of an XMA2WAVEFORMATEX: WAVEFORMATEX (18) plus the XMA2 extension (34).</summary>
	public const int Xma2WaveFormatExSize = 52;

	/// <summary>
	/// Every multi-byte field of an XMA2WAVEFORMATEX as (offset, width); LoopCount and
	/// EncoderVersion at +48/+49 are single bytes.
	/// </summary>
	private static readonly (int Offset, int Width)[] Xma2WaveFormatExFields =
	{
		(0, 2), (2, 2), (4, 4), (8, 4), (12, 2), (14, 2), (16, 2),
		(18, 2), (20, 4), (24, 4), (28, 4), (32, 4), (36, 4), (40, 4), (44, 4), (50, 2),
	};

	/// <summary>
	/// The source as the native decoder reads it: its WAVEFORMATEX fields little-endian.
	/// </summary>
	/// <remarks>
	/// FABLE 3's fmt CHUNK IS A BIG-ENDIAN XMA2WAVEFORMATEX that the guest reads in place: the XMA
	/// reader refuses an fmt under 0x34 bytes (sub_82E21458 `cmplwi r3, 0x34`) and loads its
	/// fields with plain PPC loads — nChannels `lhz r11, 2(r3)`, NumStreams `lhz r3, 0x12(r11)`,
	/// BytesPerBlock `lwz r3, 0x1C(r11)`, BlockCount `lhz r3, 0x32(r11)`. Chunk sizes stay
	/// little-endian under 'RIFF' (sub_82E20C28 byte-swaps them only for that magic). So a Fable 3
	/// RIFF/WAVE's fmt fields are swapped to little-endian on a copy; Fable 2's files, and Fable 3's
	/// xWMA speech (whose fmt is little-endian — sub_82E202E0 byte-swaps every field on read), pass
	/// through unchanged. The file states no version, so the title says which order it is in.
	/// </remarks>
	public static byte[] ToDecoderByteOrder(byte[] source, Engine_Title title)
	{
		if (title != Engine_Title.Fable3 ||
		    !TryFindWaveFmt(source, out int fmt, out int fmtSize) ||
		    fmtSize < Xma2WaveFormatExSize)
		{
			return source;
		}

		var copy = (byte[])source.Clone();
		foreach ((int offset, int width) in Xma2WaveFormatExFields)
		{
			Array.Reverse(copy, fmt + offset, width);
		}

		return copy;
	}

	/// <summary>
	/// The 'fmt ' payload of a RIFF/WAVE at offset 0, walking little-endian, word-aligned chunk
	/// sizes. Fable 3's sound-bank and music .wav carry no tag before 'RIFF' (sub_82DC1D78 skips 4
	/// bytes only for its xwma and wav kinds).
	/// </summary>
	private static bool TryFindWaveFmt(byte[] bytes, out int payload, out int size)
	{
		payload = -1;
		size = 0;

		if (bytes.Length < 12 ||
		    bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F' ||
		    bytes[8] != (byte)'W' || bytes[9] != (byte)'A' || bytes[10] != (byte)'V' || bytes[11] != (byte)'E')
		{
			return false;
		}

		long position = 12;
		while (position + 8 <= bytes.Length)
		{
			int at = (int)position;
			uint chunkSize = (uint)(bytes[at + 4] | (bytes[at + 5] << 8) | (bytes[at + 6] << 16) | (bytes[at + 7] << 24));
			long body = position + 8;

			if (bytes[at] == (byte)'f' && bytes[at + 1] == (byte)'m' && bytes[at + 2] == (byte)'t' && bytes[at + 3] == (byte)' ')
			{
				if (body + chunkSize > bytes.Length)
				{
					return false;
				}

				payload = (int)body;
				size = (int)chunkSize;
				return true;
			}

			position = body + chunkSize + (chunkSize & 1u);
		}

		return false;
	}

	private static string Decode(byte[] buffer)
	{
		int end = Array.IndexOf(buffer, (byte)0);
		return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
	}

	[DllImport(Library)]
	private static extern int RefiiAudioAbiVersion();

	[DllImport(Library)]
	private static extern int RefiiXmaDecode(
		byte[] data,
		nuint length,
		out IntPtr outPcm,
		out nuint outSampleCount,
		out int outSampleRate,
		out int outChannels,
		byte[] error,
		nuint errorCapacity);

	[DllImport(Library)]
	private static extern void RefiiXmaFree(IntPtr pcm);
}
