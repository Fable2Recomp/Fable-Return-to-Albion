using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Refii.Video;

/// <summary>
/// FFmpeg-backed Bink decoding — a format reader, alongside the .mdl and .tex ones.
///
/// Lives under Engine/ with the other format code because that is what it is: bytes in,
/// frames out, with no knowledge of the guest, the session or the window. What plays and
/// when is decided elsewhere.
///
/// rexglue ships FFmpeg but builds it audio-only for XMA/WMA Pro, so the Bink video
/// decoder was compiled out; the build now enables it and this binds the thin C wrapper
/// around it. Decoding here rather than scraping the guest's decoded planes removes every
/// dependency on the guest reaching its render path, on its double-buffer timing, and on
/// a render thread that is currently parked.
/// </summary>
public sealed class Engine_BinkDecoder : IDisposable
{
	// One resolver for the whole assembly, contributed centrally — without this the
	// decoder only loads where something ELSE happened to install it first, which is why
	// the game window found the library and a standalone tool scene did not.
	static Engine_BinkDecoder() => Refii.Core.Engine_NativeLibraries.Install();

	private IntPtr _handle;

	public int Width { get; private set; }
	public int Height { get; private set; }
	public int FrameCount { get; private set; }
	public double FrameSeconds { get; private set; }

	private Engine_BinkDecoder(IntPtr handle)
	{
		_handle = handle;

		RefiiBinkInfo(handle, out int w, out int h, out int frames, out int num, out int den);
		Width = w;
		Height = h;
		FrameCount = frames;

		// fps arrives as a rational; a zero denominator would be a divide by zero on a
		// malformed file rather than a slow video.
		FrameSeconds = num > 0 && den > 0 ? (double)den / num : 1.0 / 30.0;
	}

	/// <summary>Opens a .bik from memory, or returns null with the reason.</summary>
	public static Engine_BinkDecoder? Open(byte[] data, out string error)
	{
		var buffer = new byte[256];
		IntPtr handle = RefiiBinkOpen(data, data.Length, buffer, buffer.Length);
		if (handle == IntPtr.Zero)
		{
			int length = Array.IndexOf(buffer, (byte)0);
			error = Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
			return null;
		}

		error = string.Empty;
		Refii.Core.Engine_TracedMemory.Alloc(TracedPool, (ulong)handle, (ulong)data.Length);
		return new Engine_BinkDecoder(handle);
	}

	/// <summary>The in-game Tracy capture's pool for open movies (the decoder holds the whole .bik).</summary>
	private const string TracedPool = "movies";

	/// <summary>
	/// Decodes the next frame into three tightly packed planes. False at the end of the
	/// movie or on a decode error.
	/// </summary>
	public bool NextFrame(byte[] y, byte[] u, byte[] v, out int chromaWidth, out int chromaHeight)
	{
		var desc = new int[4];
		int ok = RefiiBinkNextFrame(
			_handle, y, y.Length, u, u.Length, v, v.Length, desc);
		chromaWidth = desc[2];
		chromaHeight = desc[3];
		return ok != 0;
	}

	public void Rewind()
	{
		if (_handle != IntPtr.Zero)
		{
			RefiiBinkRewind(_handle);
		}
	}

	public void Dispose()
	{
		if (_handle != IntPtr.Zero)
		{
			Refii.Core.Engine_TracedMemory.Free(TracedPool, (ulong)_handle);
			RefiiBinkClose(_handle);
			_handle = IntPtr.Zero;
		}
	}

	/// <summary>How many audio tracks the file carries.</summary>
	public int AudioTrackCount => RefiiBinkAudioTrackCount(_handle);

	/// <summary>Sample rate of the selected tracks; 0 when none is selected.</summary>
	public int AudioRate => RefiiBinkAudioRate(_handle);

	/// <summary>The Bink id of the track at a file index, or -1 out of range.</summary>
	public int AudioTrackId(int index) =>
		RefiiBinkAudioTrackInfo(_handle, index, out int id, out _, out _) != 0 ? id : -1;

	/// <summary>
	/// Selects the tracks to decode by Bink track id, replacing any earlier selection. An id
	/// the file does not carry is dropped, as Bink drops it. Returns how many matched.
	/// </summary>
	public int SelectAudio(int[] ids, int count) => RefiiBinkSelectAudio(_handle, ids, count);

	/// <summary>Linear gain for one track, by id.</summary>
	public void SetAudioVolume(int id, float volume) =>
		RefiiBinkSetAudioVolume(_handle, id, volume);

	/// <summary>
	/// Takes the audio <see cref="NextFrame"/> has decoded: the selected tracks mixed, as
	/// interleaved stereo. Returns stereo frames written; what did not fit stays queued.
	/// </summary>
	public int TakeAudio(float[] interleaved, int maxFrames) =>
		RefiiBinkTakeAudio(_handle, interleaved, Math.Min(maxFrames, interleaved.Length / 2));

	private const string Library = "refii_binkdecode";

	[DllImport(Library)]
	private static extern IntPtr RefiiBinkOpen(
		byte[] data, int size, byte[] error, int errorCapacity);

	[DllImport(Library)]
	private static extern void RefiiBinkInfo(
		IntPtr handle, out int width, out int height, out int frames,
		out int fpsNum, out int fpsDen);

	[DllImport(Library)]
	private static extern int RefiiBinkNextFrame(
		IntPtr handle, byte[] y, int yCapacity, byte[] u, int uCapacity,
		byte[] v, int vCapacity, int[] desc);

	[DllImport(Library)]
	private static extern void RefiiBinkRewind(IntPtr handle);

	[DllImport(Library)]
	private static extern void RefiiBinkClose(IntPtr handle);

	[DllImport(Library)]
	private static extern int RefiiBinkAudioTrackCount(IntPtr handle);

	[DllImport(Library)]
	private static extern int RefiiBinkAudioTrackInfo(
		IntPtr handle, int index, out int id, out int rate, out int channels);

	[DllImport(Library)]
	private static extern int RefiiBinkSelectAudio(IntPtr handle, int[] ids, int count);

	[DllImport(Library)]
	private static extern int RefiiBinkAudioRate(IntPtr handle);

	[DllImport(Library)]
	private static extern void RefiiBinkSetAudioVolume(IntPtr handle, int id, float volume);

	[DllImport(Library)]
	private static extern int RefiiBinkTakeAudio(
		IntPtr handle, [Out] float[] interleaved, int capacityFrames);
}
