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

	public const int ExpectedAbiVersion = 1;

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
		byte[] source, out short[] pcm, out int sampleRate, out int channels, out string error)
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

		var message = new byte[ErrorCapacity];
		IntPtr buffer = IntPtr.Zero;

		try
		{
			int ok = RefiiXmaDecode(
				source, (nuint)source.Length,
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
