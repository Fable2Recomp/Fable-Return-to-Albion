using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Refii.Core;
using Refii.Text;
using Refii.Ui;

namespace Refii.Host;

/// <summary>
/// P/Invoke over <c>refii_hostbridge</c> — the C ABI that lets Godot host the recompiled
/// guest IN ITS OWN PROCESS rather than spawning <c>refii</c> as a child.
///
/// The launcher already spawns the guest; this is the other arrangement, where Godot
/// renders and the guest becomes a simulation server behind a null D3D9 device. That is
/// a large change, and everything in it is downstream of one question — whether the
/// guest's 4.5 GB address space can be reserved inside a process Godot already owns —
/// so that is what this exposes first.
/// </summary>
public static class HostBridge
{
	private const string Library = "refii_hostbridge";

	/// <summary>ABI this binding expects. A mismatch means a stale library on the search path.</summary>
	public const int ExpectedAbiVersion = 13;

	private const int MessageCapacity = 512;

	static HostBridge() => Engine_NativeLibraries.Install();

	/// <summary>
	/// The loaded library's ABI version, or null when it could not be loaded at all.
	/// Distinguishes "not built yet" from "built but out of date", which otherwise look
	/// the same at the call site.
	/// </summary>
	public static int? TryGetAbiVersion()
	{
		try
		{
			return RefiiHostBridgeAbiVersion();
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

	/// <summary>Outcome of <see cref="ProbeGuestMemory"/>.</summary>
	public readonly struct ProbeResult
	{
		public readonly bool Success;
		public readonly string Message;

		public ProbeResult(bool success, string message)
		{
			Success = success;
			Message = message;
		}
	}

	/// <summary>
	/// Reserves the guest address space inside this process and reports where it landed.
	///
	/// Releases it again before returning — the question is whether the space CAN be
	/// taken, and holding 4.5 GB of the editor's address space to prove it would be its
	/// own problem.
	/// </summary>
	public static ProbeResult ProbeGuestMemory()
	{
		var message = new byte[MessageCapacity];

		try
		{
			int ok = RefiiHostProbeGuestMemory(message, (nuint)message.Length);
			return new ProbeResult(ok == 1, Decode(message));
		}
		catch (DllNotFoundException)
		{
			return new ProbeResult(false,
				$"{Engine_NativeLibraries.FileNameFor(Library)} not found. Build it with " +
				"REFII_BUILD_HOST_BRIDGE=ON, or set REFII_NATIVE_DIR to its output directory.");
		}
		catch (EntryPointNotFoundException exception)
		{
			return new ProbeResult(false, $"stale host bridge: {exception.Message}");
		}
	}

	/// <summary>
	/// Boots the guest on its own thread inside THIS process and returns immediately.
	///
	/// A true return means the thread started, NOT that the guest is up — booting loads
	/// the XEX and runs the guest's init, which takes seconds and can fail long after
	/// this returns. Poll <see cref="IsRunning"/> and <see cref="Status"/>.
	///
	/// One boot per process: the runtime's global state cannot be set up twice.
	/// </summary>
	/// <param name="arguments">
	/// Passed to the guest. Joined with newlines rather than spaces on the way across,
	/// because an argument can be a path containing spaces.
	/// </param>
	public static ProbeResult Start(string gameDirectory, params string[] arguments)
	{
		var message = new byte[MessageCapacity];
		string packed = arguments is { Length: > 0 } ? string.Join('\n', arguments) : string.Empty;

		try
		{
			int ok = RefiiHostStart(gameDirectory, packed, message, (nuint)message.Length);
			return new ProbeResult(ok == 1, Decode(message));
		}
		catch (DllNotFoundException)
		{
			return new ProbeResult(false,
				$"{Engine_NativeLibraries.FileNameFor(Library)} not found. Build it with " +
				"REFII_BUILD_HOST_BRIDGE=ON, or set REFII_NATIVE_DIR to its output directory.");
		}
		catch (EntryPointNotFoundException exception)
		{
			return new ProbeResult(false, $"stale host bridge: {exception.Message}");
		}
	}

	/// <summary>True while the guest thread is alive.</summary>
	public static bool IsRunning => RefiiHostIsRunning() == 1;

	/// <summary>The guest's exit code, or -1 while running or never started.</summary>
	public static int ExitCode => RefiiHostExitCode();

	/// <summary>A human-readable status line from the bridge.</summary>
	public static string Status
	{
		get
		{
			var message = new byte[MessageCapacity];
			RefiiHostStatus(message, (nuint)message.Length);
			return Decode(message);
		}
	}

	/// <summary>Joins the guest thread once it has finished. False while still running.</summary>
	public static ProbeResult Join()
	{
		var message = new byte[MessageCapacity];
		int ok = RefiiHostJoin(message, (nuint)message.Length);
		return new ProbeResult(ok == 1, Decode(message));
	}

	/// <summary>
	/// Initialises the guest ON THIS THREAD and returns once it is up.
	///
	/// This is the "owned by Godot" path: Godot keeps its own thread and drives the
	/// guest with <see cref="Step"/>, rather than handing a thread to the guest's own
	/// blocking loop. SYNCHRONOUS and takes seconds — call it from the main thread and
	/// accept the stall, because SDL wants its main thread and running the guest on a
	/// worker left that unresolved.
	/// </summary>
	public static ProbeResult Initialize(string gameDirectory, params string[] arguments)
	{
		var message = new byte[MessageCapacity];
		string packed = arguments is { Length: > 0 } ? string.Join('\n', arguments) : string.Empty;

		try
		{
			int ok = RefiiHostInitialize(gameDirectory, packed, message, (nuint)message.Length);
			return new ProbeResult(ok == 1, Decode(message));
		}
		catch (DllNotFoundException)
		{
			return new ProbeResult(false,
				$"{Engine_NativeLibraries.FileNameFor(Library)} not found. Build it with " +
				"REFII_BUILD_HOST_BRIDGE=ON, or set REFII_NATIVE_DIR to its output directory.");
		}
		catch (EntryPointNotFoundException exception)
		{
			return new ProbeResult(false, $"stale host bridge: {exception.Message}");
		}
	}

	/// <summary>
	/// Advances the guest by one frame. False once it has stopped.
	/// Call from <c>_Process</c>.
	/// </summary>
	public static bool Step() => RefiiHostStep() == 1;

	/// <summary>Status of the hosted guest.</summary>
	public static string HostedStatus
	{
		get
		{
			var message = new byte[MessageCapacity];
			RefiiHostHostedStatus(message, (nuint)message.Length);
			return Decode(message);
		}
	}

	/// <summary>
	/// Tears the guest down. Safe to call more than once, and worth calling from
	/// <c>_ExitTree</c> — leaving the guest's threads live at process exit is what
	/// produced the abort on teardown.
	/// </summary>
	public static ProbeResult Shutdown()
	{
		var message = new byte[MessageCapacity];
		try
		{
			int ok = RefiiHostShutdown(message, (nuint)message.Length);
			return new ProbeResult(ok == 1, Decode(message));
		}
		catch (DllNotFoundException)
		{
			return new ProbeResult(false, "host bridge not loaded");
		}
	}

	private static string Decode(byte[] buffer)
	{
		int end = Array.IndexOf(buffer, (byte)0);
		return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
	}

	/// <summary>
	/// Publishes one controller's state for the guest to read.
	///
	/// Values are in the GUEST's units, not Godot's: buttons are X_INPUT_GAMEPAD_* bits,
	/// triggers 0..255, sticks -32768..32767 with +Y UP. Callers convert; see GameInput.
	/// </summary>
	public static void SetControllerState(
		int userIndex, bool connected, uint buttons, uint leftTrigger, uint rightTrigger,
		int thumbLx, int thumbLy, int thumbRx, int thumbRy) =>
		RefiiHostSetControllerState(
			(uint)userIndex, connected ? 1 : 0, buttons, leftTrigger, rightTrigger,
			thumbLx, thumbLy, thumbRx, thumbRy);

	/// <summary>
	/// Copies the current Bink frame as RGBA8. Pass null to probe the size without
	/// copying — width/height are reported either way, so a caller can size its buffer
	/// from a failed call.
	/// </summary>
	public static bool TryGetVideoFrame(byte[]? rgba, out int width, out int height)
	{
		uint w = 0;
		uint h = 0;
		int ok = RefiiHostGetVideoFrame(rgba, (uint)(rgba?.Length ?? 0), out w, out h);
		width = (int)w;
		height = (int)h;
		return ok != 0;
	}

	/// <summary>
	/// Copies the .bik the guest just handed to BinkOpen. The startup logos arrive as a
	/// BUFFER the guest has already loaded, so this is the bytes themselves. Movies after
	/// the logos are opened by name instead — see <see cref="GetRequestedVideoName"/> —
	/// and return null here for the same generation.
	/// </summary>
	public static byte[]? GetRequestedVideo(out uint generation)
	{
		uint size = 0;
		uint gen = 0;
		if (RefiiHostGetRequestedVideo(null, 0, out size, out gen) == 0 || size == 0)
		{
			generation = gen;
			return null;
		}

		var buffer = new byte[size];
		int ok = RefiiHostGetRequestedVideo(buffer, size, out size, out gen);
		generation = gen;
		return ok != 0 ? buffer : null;
	}

	/// <summary>
	/// The PATH the guest handed to BinkOpen, when it opened a movie by name rather than
	/// from a loaded buffer. Empty when the request carried bytes instead.
	///
	/// The generation is shared with <see cref="GetRequestedVideo"/>: one request is
	/// either a buffer or a name, so the caller takes whichever has content.
	/// </summary>
	public static string GetRequestedVideoName(out uint generation)
	{
		var buffer = new byte[512];
		uint length = RefiiHostGetRequestedVideoName(buffer, (uint)buffer.Length, out generation);
		return length == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(buffer, 0, (int)length);
	}

	/// <summary>
	/// Tells the guest the movie the host was showing has finished, so its startup
	/// sequence advances with what the player actually saw rather than on its own decode
	/// clock. Returns the player address retired, or 0 when no movie was live.
	/// </summary>
	public static uint FinishMovie() => RefiiHostFinishMovie();

	/// <summary>
	/// The guest's front-end (title/main-menu) state object, or 0 while it is still
	/// booting. The guest builds it only once every startup logo has played and its bank
	/// mounter has finished, so a non-zero value is the guest saying it has reached the
	/// front end — rather than the host inferring it from a movie ending.
	/// </summary>
	public static uint FrontEndState() => RefiiHostGetFrontEndState();

	/// <summary>Which of the guest's front-end screens is up, and its state.</summary>
	public enum FrontEndScreen { None = 0, Title = 1, MainMenu = 2 }

	/// <summary>
	/// The guest's live front-end state.
	///
	/// <paramref name="state"/> is the screen's own state int. On the title screen the
	/// proven values are 9 legal sequence, 0 idle, 8 attract video, 10 start sequence,
	/// 11 handing over to the menu, 12 dialog. <paramref name="canPressA"/> is the flag
	/// the title's "CAN_PRESS_A" event sets — A is ignored until it is true.
	/// </summary>
	public static bool TryGetFrontEnd(
		out FrontEndScreen screen, out uint state, out bool canPressA, out uint cursor,
		out int sceneSlide, out int slideCount)
	{
		uint[] words = new uint[8];
		int ok = RefiiHostGetFrontEnd(words);

		screen = (FrontEndScreen)words[2];
		state = words[3];
		canPressA = words[4] != 0;
		cursor = words[5];
		sceneSlide = (int)words[6];
		slideCount = (int)words[7];
		return ok != 0;
	}


	/// <summary>One element of the guest's live UI.</summary>
	public readonly record struct UiElement(uint NameHash, int Slide, string? Text);

	/// <summary>
	/// Every element of the guest's active screen: its slide and the text it is actually
	/// displaying.
	///
	/// The text is what makes this worth reading rather than inferring — the shipped
	/// presentation carries placeholders the game overwrites at runtime, and some tags
	/// resolve through a table that is not the Babel book.
	/// </summary>
	public static List<UiElement>? GetUiElements()
	{
		uint count = RefiiHostGetUiElements(null, 0, null, 0);
		if (count == 0)
		{
			return null;
		}

		// Sized from the probe, with headroom: the guest is mutating the tree, so the
		// second walk can legitimately find more than the first.
		var words = new uint[count * 3 + 64];
		var chars = new ushort[count * 24 + 1024];
		uint written = RefiiHostGetUiElements(words, (uint)words.Length, chars, (uint)chars.Length);

		int usable = (int)Math.Min(written, (uint)(words.Length / 3));
		var elements = new List<UiElement>(usable);
		for (int i = 0; i < usable; ++i)
		{
			uint offset = words[i * 3 + 2];
			string? text = null;
			if (offset != 0xFFFFFFFFu && offset < chars.Length)
			{
				int end = (int)offset;
				while (end < chars.Length && chars[end] != 0)
				{
					++end;
				}

				var span = new char[end - (int)offset];
				for (int c = 0; c < span.Length; ++c)
				{
					span[c] = (char)chars[offset + c];
				}

				text = new string(span);
			}

			elements.Add(new UiElement(words[i * 3], (int)words[i * 3 + 1], text));
		}

		return elements;
	}

	/// <summary>The four-argument form, for callers that only want the screen state.</summary>
	public static bool TryGetFrontEnd(
		out FrontEndScreen screen, out uint state, out bool canPressA, out uint cursor) =>
		TryGetFrontEnd(out screen, out state, out canPressA, out cursor, out _, out _);

	/// <summary>Describes the current video frame's plane geometry.</summary>
	public readonly record struct VideoPlanes(
		int LumaWidth, int LumaHeight, int ChromaWidth, int ChromaHeight,
		int LumaBytes, int ChromaBytes);

	/// <summary>
	/// Copies the current frame's three planes, tightly packed, for GPU conversion. Pass
	/// nulls to probe the geometry without copying.
	/// </summary>
	public static bool TryGetVideoPlanes(
		byte[]? y, byte[]? cr, byte[]? cb, out VideoPlanes planes)
	{
		uint[] desc = new uint[6];
		int ok = RefiiHostGetVideoPlanes(
			y, (uint)(y?.Length ?? 0),
			cr, (uint)(cr?.Length ?? 0),
			cb, (uint)(cb?.Length ?? 0),
			desc);

		planes = new VideoPlanes(
			(int)desc[0], (int)desc[1], (int)desc[2], (int)desc[3],
			(int)desc[4], (int)desc[5]);
		return ok != 0;
	}

	/// <summary>The rumble the guest last asked for, 0..65535 per motor.</summary>
	public static bool TryGetVibration(int userIndex, out uint left, out uint right)
	{
		left = 0;
		right = 0;
		return RefiiHostGetVibration((uint)userIndex, out left, out right) != 0;
	}

	[DllImport(Library)]
	private static extern int RefiiHostGetRequestedVideo(
		byte[]? outBuffer, uint capacity, out uint outSize, out uint outGeneration);

	[DllImport(Library)]
	private static extern uint RefiiHostFinishMovie();

	[DllImport(Library)]
	private static extern uint RefiiHostGetFrontEndState();

	[DllImport(Library)]
	private static extern int RefiiHostGetFrontEnd(uint[] outWords);

	[DllImport(Library)]
	private static extern uint RefiiHostGetUiElements(
		uint[]? outWords, uint wordCapacity, ushort[]? outText, uint textCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetRequestedVideoName(
		byte[] outBuffer, uint capacity, out uint outGeneration);

	[DllImport(Library)]
	private static extern int RefiiHostGetVideoPlanes(
		byte[]? outY, uint yCapacity, byte[]? outCr, uint crCapacity,
		byte[]? outCb, uint cbCapacity, uint[] outDesc);

	[DllImport(Library)]
	private static extern int RefiiHostGetVideoFrame(
		byte[]? outRgba, uint capacity, out uint outWidth, out uint outHeight);

	[DllImport(Library)]
	private static extern void RefiiHostSetControllerState(
		uint userIndex, int connected, uint buttons, uint leftTrigger, uint rightTrigger,
		int thumbLx, int thumbLy, int thumbRx, int thumbRy);

	[DllImport(Library)]
	private static extern int RefiiHostGetVibration(
		uint userIndex, out uint outLeft, out uint outRight);

	[DllImport(Library)]
	private static extern int RefiiHostBridgeAbiVersion();

	[DllImport(Library)]
	private static extern int RefiiHostProbeGuestMemory(byte[] message, nuint capacity);

	[DllImport(Library, CharSet = CharSet.Ansi)]
	private static extern int RefiiHostStart(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string gameDirectory,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string arguments,
		byte[] message, nuint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostIsRunning();

	[DllImport(Library)]
	private static extern int RefiiHostExitCode();

	[DllImport(Library)]
	private static extern int RefiiHostStatus(byte[] message, nuint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostJoin(byte[] message, nuint capacity);

	[DllImport(Library, CharSet = CharSet.Ansi)]
	private static extern int RefiiHostInitialize(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string gameDirectory,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string arguments,
		byte[] message, nuint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostStep();

	[DllImport(Library)]
	private static extern int RefiiHostHostedStatus(byte[] message, nuint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostShutdown(byte[] message, nuint capacity);
}
