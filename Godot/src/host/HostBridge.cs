using Godot;
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

	/// <summary>
	/// ABI this binding expects. A mismatch means a stale library on the search path.
	///
	/// BUMP THIS IN THE SAME BREATH AS `RefiiHostBridgeAbiVersion`. The check is exact and
	/// `GameSession` treats a mismatch as fatal — it fails before the guest is initialised, so
	/// the game does not limp, it stops, and it stops before anything has written a guest log
	/// segment to look in. Adding export 33 without touching this line is what that looks like.
	/// </summary>
	public const int ExpectedAbiVersion = 50;

	private const int MessageCapacity = 512;

	static HostBridge() => Engine_NativeLibraries.Install();

	/// <summary>
	/// The bridge's version if it is USABLE, and null if it is not — either because the library
	/// would not load or because it is a version this binding cannot speak.
	///
	/// A MISMATCH USED TO PASS EVERY GUARD IN THE GAME. Callers write
	/// <c>if (TryGetAbiVersion() is null) return;</c> and mean "the bridge is not usable", but
	/// this returned the raw number — so a stale library answered with a version, every guard
	/// accepted it, and the whole app ran against a bridge whose guest `GameSession` had
	/// refused to initialise. That is not a limp, it is a segfault in UI startup with an ABI
	/// line thirty lines earlier as the only clue.
	///
	/// Refusing here fixes every call site at once, which is the point: the alternative is
	/// trusting a few dozen guards to each remember to compare against
	/// <see cref="ExpectedAbiVersion"/>. Use <see cref="TryGetRawAbiVersion"/> when you want to
	/// REPORT the version, which is the one case that genuinely wants the stale number.
	/// </summary>
	public static int? TryGetAbiVersion() =>
		TryGetRawAbiVersion() is { } version && version == ExpectedAbiVersion ? version : null;

	/// <summary>
	/// The loaded library's ABI version whatever it is, or null when it could not be loaded at
	/// all. Distinguishes "not built yet" from "built but out of date" — which is exactly the
	/// distinction a diagnostic needs and a guard must not make.
	/// </summary>
	public static int? TryGetRawAbiVersion()
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
	/// One of the guest's XAudio source voices, as the voice hooks last saw it.
	///
	/// Field order and widths mirror <c>refii::vfs::VoiceRecord</c>; the native side
	/// static_asserts the sizes match, because a silent mismatch marshals garbage rather
	/// than failing to build.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct GuestVoice
	{
		/// <summary>The guest's voice pointer. Unique only together with Generation.</summary>
		public uint Handle;

		/// <summary>
		/// Bumped every time a voice is created. The guest's allocator reuses handles, so
		/// this is what stops a new sound inheriting the previous one's stream.
		/// </summary>
		public uint Generation;

		public uint SampleRate;
		public uint Channels;

		/// <summary>Non-zero between the guest's Start and Stop.</summary>
		public uint Started;

		/// <summary>Set once the guest has destroyed the voice.</summary>
		public uint Retired;

		/// <summary>Linear amplitude, 1.0 unity. The guest never speaks in decibels.</summary>
		public float Volume;

		/// <summary>
		/// Frequency ratio. The guest states OCTAVES — CSourceStream::SetPitch stores
		/// <c>powf(2.0f, arg)</c> — and the native side converts once so no consumer has
		/// to remember the unit. This is directly Godot's PitchScale.
		/// </summary>
		public float PitchRatio;

		/// <summary>PCM bytes waiting to be drained for this voice.</summary>
		public uint QueuedBytes;

		/// <summary>Bumped per submit, so a consumer can spot buffers it missed.</summary>
		public uint SubmittedSeq;

		/// <summary>
		/// The guest's own spatialisation, folded to a stereo balance — not something the
		/// host invented. These come from the X3DAudio matrix the game computed for this
		/// voice and handed to XAudioSetChannelVolumes, so following them keeps a per-voice
		/// mix positioned the way the game intended.
		/// </summary>
		public float PanLeft;

		/// <inheritdoc cref="PanLeft"/>
		public float PanRight;
	}

	[DllImport(Library)]
	private static extern uint RefiiHostPullAudio(float[] outFrames, uint maxFrames);

	[DllImport(Library)]
	private static extern uint RefiiHostAudioSampleRate();

	[DllImport(Library)]
	private static extern uint RefiiHostAudioChannels();

	[DllImport(Library)]
	private static extern uint RefiiHostAudioSamplesPerFrame();

	[DllImport(Library)]
	private static extern uint RefiiHostAudioFloatsPerFrame();

	[DllImport(Library)]
	private static extern void RefiiHostAudioStats(
		out uint outQueuedFrames, out ulong outPushed, out ulong outPopped,
		out ulong outDropped, out int outDriverInstalled);

	// [Out] IS LOad-BEARING, not decoration.
	//
	// Without it a run measured this exactly: the native side reported "11 live voices,
	// 99328 bytes held, 6954 snapshots" — it was writing 11 valid records on every call —
	// while C# received an array of all zeros and therefore never drained a single byte.
	// .NET only pins an array and lets the callee write through it when the element type
	// is blittable; otherwise it marshals a COPY, and the default direction for an array
	// parameter is in-only, so everything the native side wrote was discarded. [Out] forces
	// the copy back, and costs nothing when the array was pinned anyway.
	[DllImport(Library)]
	private static extern uint RefiiHostGetVoices([Out] GuestVoice[]? outVoices, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostTakeVoicePcm(
		uint handle, uint generation, [Out] byte[] outPcm, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostVoiceCaptureActive();

	[DllImport(Library)]
	private static extern void RefiiHostSetVoicePlaybackOwned(int owned);

	/// <summary>
	/// Takes up to <paramref name="maxFrames"/> mixed frames as interleaved stereo float.
	///
	/// THIS CALL IS THE GUEST'S AUDIO CLOCK. Each frame taken credits the guest for one
	/// more, which is what lets its mixer produce the next. Only call it with somewhere to
	/// put the audio: draining faster than playback lets the guest run ahead of what is
	/// audible, which is the drift this whole path exists to remove.
	/// </summary>
	public static uint PullAudio(float[] outFrames, uint maxFrames) =>
		RefiiHostPullAudio(outFrames, maxFrames);

	/// <summary>The guest's output rate — 48000, stated by its mastering voice.</summary>
	public static uint AudioSampleRate() => RefiiHostAudioSampleRate();

	/// <summary>Channels the host receives, after the 5.1 fold.</summary>
	public static uint AudioChannels() => RefiiHostAudioChannels();

	/// <summary>256 — one frame is 5.333 ms, so 187.5 frames a second.</summary>
	public static uint AudioSamplesPerFrame() => RefiiHostAudioSamplesPerFrame();

	/// <summary>Floats in one frame, i.e. samples times channels.</summary>
	public static uint AudioFloatsPerFrame() => RefiiHostAudioFloatsPerFrame();

	/// <summary>
	/// Audio health. <paramref name="pushed"/> should settle at 187.5/s once the guest is
	/// producing — a rate that is not that means the clock is wrong however it sounds.
	/// <paramref name="dropped"/> climbing means nothing is draining.
	/// </summary>
	public static void AudioStats(
		out uint queuedFrames, out ulong pushed, out ulong popped, out ulong dropped,
		out bool driverInstalled)
	{
		RefiiHostAudioStats(out queuedFrames, out pushed, out popped, out dropped,
			out int installed);
		driverInstalled = installed != 0;
	}

	/// <summary>
	/// Snapshots the guest's live voices. Returns the number that exist, which may exceed
	/// the buffer — grow and ask again rather than lose voices.
	/// </summary>
	public static uint GetVoices(GuestVoice[]? outVoices)
	{
		uint total = RefiiHostGetVoices(outVoices, outVoices is null ? 0u : (uint)outVoices.Length);

		// A non-zero count with a zeroed first record can only mean the payload did not
		// survive marshalling — the native side never reports a voice with a null handle.
		// Said once, because the symptom otherwise reads as "the guest has no audio", which
		// is the opposite of the truth and cost two runs to tell apart.
		if (!_reportedVoiceMarshalFault && total > 0 && outVoices is not null &&
			outVoices.Length > 0 && outVoices[0].Handle == 0)
		{
			_reportedVoiceMarshalFault = true;
			GD.PushError(
				$"[audio] the bridge reported {total} voices but the first record came back " +
				"zeroed — the array is not reaching native code writable. Check [Out] on " +
				"RefiiHostGetVoices.");
		}

		return total;
	}

	private static bool _reportedVoiceMarshalFault;

	/// <summary>
	/// Drains one voice's PCM as interleaved 16-bit samples. The generation guards against
	/// the guest recycling a handle: a mismatch returns 0 rather than the wrong sound.
	/// </summary>
	public static uint TakeVoicePcm(uint handle, uint generation, byte[] outPcm) =>
		RefiiHostTakeVoicePcm(handle, generation, outPcm, (uint)outPcm.Length);

	/// <summary>
	/// True once the voice hooks have seen any voice — distinguishes "built but the guest
	/// has not reached audio yet" from "not running".
	/// </summary>
	public static bool VoiceCaptureActive() => RefiiHostVoiceCaptureActive() != 0;

	/// <summary>
	/// Hands per-voice playback to Godot. While set, the guest's mixed frame is discarded
	/// rather than queued, because playing both would double every sound. The guest's
	/// mixer keeps running and keeps being credited — that is the clock, and it does not
	/// stop being the clock just because nobody is listening to it.
	/// </summary>
	public static void SetVoicePlaybackOwned(bool owned) =>
		RefiiHostSetVoicePlaybackOwned(owned ? 1 : 0);

	/// <summary>
	/// The live value of one attribute on one element, as the running game holds it.
	/// </summary>
	public readonly record struct LiveAttribute(uint RawValue, bool Present)
	{
		/// <summary>The value read as a float, which is what layout attributes are.</summary>
		public float AsFloat => BitConverter.UInt32BitsToSingle(RawValue);
	}

	/// <summary>
	/// Every element's live attribute values, in the same walk order as
	/// <see cref="GetUiElements"/>, one row per element and one column per requested id.
	///
	/// The shipped .bgf holds what an element was AUTHORED with; the screen's Lua then
	/// moves and hides things at runtime with setAttribute. Reading only the file draws
	/// hidden widgets where the file put them.
	///
	/// Ids are sdbm hashes of the attribute names — use <c>Engine_GameHash.Sdbm</c>, which
	/// mirrors the engine's own sub_82189140.
	/// </summary>
	/// <param name="presentation">
	/// A presentation from <see cref="GetPresentations"/>, or 0 for the front end's active
	/// screen. The HUD needs this: the game hides it by writing <c>opacity</c> 0 on one group.
	/// </param>
	public static LiveAttribute[,]? GetUiAttributes(uint[] ids, uint presentation = 0)
	{
		if (ids.Length == 0)
		{
			return null;
		}

		uint elements = RefiiHostGetUiAttributes(presentation, ids, (uint)ids.Length, null, 0);
		if (elements == 0)
		{
			return null;
		}

		var words = new uint[elements * ids.Length * 2];
		uint written = RefiiHostGetUiAttributes(
			presentation, ids, (uint)ids.Length, words, (uint)words.Length);

		int rows = (int)Math.Min(written, elements);
		var table = new LiveAttribute[rows, ids.Length];
		for (int row = 0; row < rows; ++row)
		{
			for (int column = 0; column < ids.Length; ++column)
			{
				int slot = (row * ids.Length + column) * 2;
				table[row, column] = new LiveAttribute(words[slot], words[slot + 1] != 0);
			}
		}

		return table;
	}

	/// <summary>One live guest entity, in guest world space.</summary>
	public readonly record struct GuestEntity(
		Vector3 Position, Quaternion Rotation, uint ModelHash,
		uint Address, ulong UniqueId, uint RecordHash);

	/// <summary>How many entities <see cref="GetEntities"/> will read in one call.</summary>
	private const int MaxEntities = 4096;

	private const int EntityStride = 12;

	/// <summary>How many DISTINCT unrecognised placement vtables to bring back.</summary>
	private const uint MaxUnknownVtables = 32;

	private static uint[]? _entityWords;

	/// <summary>
	/// A placement vtable the bridge does not know, and how many live entities carry it.
	///
	/// The tally is what makes the list a work list. One address standing for three hundred
	/// entities is the thing to decompile next; thirty standing for a dozen each says the
	/// class hierarchy is wider than the twelve EC-2 vtables assumed.
	/// </summary>
	public readonly record struct UnknownPlacement(uint Vtable, uint Entities);

	/// <summary>
	/// Every live entity's transform and model, straight out of guest memory.
	///
	/// The plan's phase 1. It costs one pass over the entity list and calls no guest code, so
	/// it is safe to run each frame while the guest simulates — a torn read is one bad frame
	/// for one entity, never a fault.
	///
	/// <paramref name="unknown"/> counts entities whose placement component had a vtable the
	/// bridge does not recognise. Those are SKIPPED rather than guessed: the set of physics
	/// delegate classes could not be proven closed, and placing an entity from an unverified
	/// offset would put it somewhere plausible and wrong.
	///
	/// <paramref name="unknownVtables"/> names them, with how many entities each accounts for.
	/// The bridge returns the length explicitly rather than terminating the list, because a
	/// vtable word that reads zero is a real observation and a zero terminator hid every one
	/// of 388 unplaceable entities behind it.
	/// </summary>
	public static System.Collections.Generic.List<GuestEntity> GetEntities(
		out uint unknown, out UnknownPlacement[] unknownVtables)
	{
		unknown = 0;
		var pairs = new uint[MaxUnknownVtables * 2];
		unknownVtables = System.Array.Empty<UnknownPlacement>();
		var entities = new System.Collections.Generic.List<GuestEntity>();
		if (TryGetAbiVersion() is null)
		{
			return entities;
		}

		_entityWords ??= new uint[MaxEntities * EntityStride];
		uint count = RefiiHostGetEntities(
			_entityWords, MaxEntities, out unknown, pairs, MaxUnknownVtables,
			out uint distinct);

		distinct = System.Math.Min(distinct, MaxUnknownVtables);
		unknownVtables = new UnknownPlacement[distinct];
		for (uint i = 0; i < distinct; ++i)
		{
			unknownVtables[i] = new UnknownPlacement(pairs[i * 2], pairs[(i * 2) + 1]);
		}

		for (uint i = 0; i < count; ++i)
		{
			int at = (int)(i * EntityStride);
			entities.Add(new GuestEntity(
				new Vector3(
					BitConverter.Int32BitsToSingle((int)_entityWords[at]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 1]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 2])),
				new Quaternion(
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 3]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 4]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 5]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 6])),
				_entityWords[at + 7],
				_entityWords[at + 8],
				((ulong)_entityWords[at + 9] << 32) | _entityWords[at + 10],
				_entityWords[at + 11]));
		}

		return entities;
	}

	/// <summary>The most bones a character rig is read at. Fable's rigs are well under this.</summary>
	public const int MaxBones = 256;

	/// <summary>
	/// A live character's skeleton pose, or null when the entity has no animated mesh.
	///
	/// <see cref="BonePose.Space"/> is the guest's own statement of which space the bones are
	/// in — 2 model, 1 local — read from the component rather than assumed. The animation
	/// update writes the array LOCAL and converts it in place, so a pose sampled mid-update is
	/// genuinely local and using it as model-space would splay the rig apart.
	/// </summary>
	public readonly record struct BonePose(
		int BoneCount, uint Space, bool Torn, float[] Transforms, int[] Parents)
	{
		/// <summary>True when the bones are already object-space — no parent walk needed.</summary>
		public bool IsModelSpace => Space == 2;

		/// <summary>Bone <paramref name="index"/>'s rotation.</summary>
		public Quaternion Rotation(int index) => new(
			Transforms[(index * 12) + 0], Transforms[(index * 12) + 1],
			Transforms[(index * 12) + 2], Transforms[(index * 12) + 3]);

		/// <summary>Bone <paramref name="index"/>'s translation.</summary>
		public Vector3 Translation(int index) => new(
			Transforms[(index * 12) + 4], Transforms[(index * 12) + 5],
			Transforms[(index * 12) + 6]);

		/// <summary>Bone <paramref name="index"/>'s scale.</summary>
		public Vector3 Scale(int index) => new(
			Transforms[(index * 12) + 8], Transforms[(index * 12) + 9],
			Transforms[(index * 12) + 10]);
	}

	private static float[]? _poseTransforms;

	private static int[]? _poseParents;

	/// <summary>
	/// The pose of one entity, or null when it carries no animated mesh — which most entities
	/// do not, so null is the ordinary answer and not a failure.
	///
	/// The buffers are reused across calls, so the returned arrays are only valid until the
	/// next call. Callers copy what they keep.
	/// </summary>
	public static BonePose? GetEntityPose(uint entity)
	{
		if (TryGetAbiVersion() is null || !IsRunning)
		{
			return null;
		}

		_poseTransforms ??= new float[MaxBones * 12];
		_poseParents ??= new int[MaxBones];

		uint bones = RefiiHostGetEntityPose(
			entity, _poseTransforms, _poseParents, MaxBones, out uint space, out uint torn);

		return bones == 0
			? null
			: new BonePose((int)bones, space, torn != 0, _poseTransforms, _poseParents);
	}

	/// <summary>
	/// A character's REST pose — the bind pose, read once per model rather than per frame.
	///
	/// Unlike the live pose this is LOCAL and the caller has to compose it through the parent
	/// chain. What comes back is a copy, not the shared buffer: it is kept for the life of the
	/// skeleton while the live pose is consumed immediately.
	///
	/// ASKING IS CHEAP AND THE ANSWER IS USUALLY NO, so nothing is allocated until the entity
	/// turns out to have a rig. Most entities are not characters and the bridge says so from a
	/// single bit at `entity+0x24`, which makes this a reasonable thing to poll — but only if
	/// the caller does not pay for a rig it is not going to get. Sizing the buffers up front
	/// cost 13 KB per ask, so retrying across a thousand entities produced more garbage than
	/// the rigs were worth, and the retry was not written.
	/// </summary>
	public static BonePose? GetEntityRestPose(uint entity)
	{
		if (TryGetAbiVersion() is null || !IsRunning)
		{
			return null;
		}

		_poseTransforms ??= new float[MaxBones * 12];
		_poseParents ??= new int[MaxBones];

		uint bones = RefiiHostGetEntityRestPose(entity, _poseTransforms, _poseParents, MaxBones);
		if (bones == 0)
		{
			return null;
		}

		var transforms = new float[bones * 12];
		var parents = new int[bones];
		System.Array.Copy(_poseTransforms, transforms, transforms.Length);
		System.Array.Copy(_poseParents, parents, parents.Length);

		// Space 1 — LOCAL, and said explicitly so a caller cannot mistake this for the live
		// pose, which arrives already composed. Never torn: this is the SKELETON's array, not
		// the component's, and it is written once at load rather than every frame.
		return new BonePose((int)bones, 1, false, transforms, parents);
	}

	/// <summary>
	/// The guest's own name for each bone of a character's rig, or null when it has none.
	///
	/// THE ONLY KEY THE TWO SKELETONS SHARE. A `.mdl` numbers its bones one way and the guest
	/// numbers its runtime rig another — 143 against 161 for the child hero, and 39 against 39
	/// with 28 of the parent links disagreeing for a chicken — so an index means different
	/// things on the two sides and nothing but the name relates them.
	///
	/// An empty entry is a real answer: the guest allows an unnamed bone and degrades it to its
	/// shared empty string. Names are interned, so duplicates are expected and a caller that
	/// wants a map must decide what a repeat means rather than assume there are none.
	/// </summary>
	public static string[]? GetEntityBoneNames(uint entity)
	{
		if (TryGetAbiVersion() is null || !IsRunning)
		{
			return null;
		}

		_boneNames ??= new byte[MaxBones * BoneNameStride];
		uint bones = RefiiHostGetEntityBoneNames(entity, _boneNames, BoneNameStride, MaxBones);
		if (bones == 0)
		{
			return null;
		}

		var names = new string[bones];
		for (int i = 0; i < bones; ++i)
		{
			int start = i * (int)BoneNameStride;
			int length = 0;
			while (length < BoneNameStride && _boneNames[start + length] != 0)
			{
				++length;
			}

			names[i] = System.Text.Encoding.ASCII.GetString(_boneNames, start, length);
		}

		return names;
	}

	/// <summary>
	/// Bytes per name in the bone-name buffer. Passed to the bridge rather than agreed with it,
	/// so there is one owner and nothing to drift out of step.
	///
	/// 64 clears the longest name in the shipped rigs with room to spare —
	/// `Carry_SheathRangedWeaponBack_DummyObject` is 40.
	/// </summary>
	private const uint BoneNameStride = 64;

	private static byte[]? _boneNames;

	/// <summary>
	/// The component ids one entity carries, in the order its own vector holds them.
	///
	/// A MEASUREMENT, not a feature. A character with a live skeleton and a model hash of zero
	/// is drawn from the parts list on <c>CECGraphicAppearanceMorph</c> (id 17) instead — see
	/// <see cref="GetEntityMorphParts"/> — and whether an entity carries id 17 at all is a
	/// live-memory question no decompile settles. This answers it, before any offset inside
	/// that component is trusted.
	///
	/// The pointer half is deliberately dropped: knowing a component EXISTS is the whole
	/// question, and returning an address invites reading through it on the strength of a
	/// layout nobody has verified yet.
	/// </summary>
	public static byte[] GetEntityComponents(uint entity)
	{
		if (TryGetAbiVersion() is null)
		{
			return System.Array.Empty<byte>();
		}

		_componentPairs ??= new uint[MaxComponents * 2];
		uint pairs = RefiiHostGetEntityComponents(entity, _componentPairs, MaxComponents);
		if (pairs == 0)
		{
			return System.Array.Empty<byte>();
		}

		var ids = new byte[pairs];
		for (int i = 0; i < pairs; ++i)
		{
			ids[i] = (byte)_componentPairs[i * 2];
		}

		return ids;
	}

	/// <summary>
	/// Component pairs read per entity. The class registry in `sub_826399A8` holds 264 classes,
	/// so no entity can carry more than that and 128 is far past anything real.
	/// </summary>
	private const uint MaxComponents = 128;

	private static uint[]? _componentPairs;

	/// <summary>
	/// The model hashes a COMPOSITE character is assembled from, or empty for anything else.
	///
	/// A character whose <c>CECGraphicAppearanceAnimatedMesh</c> states a model hash of zero
	/// cannot be drawn from that component — the guest's own mesh block branches straight past
	/// it. Its geometry is a LIST on <c>CECGraphicAppearanceMorph</c>, component 17, and this
	/// is that list.
	///
	/// <paramref name="builtParts"/> is the length of the component's OTHER, built list, which
	/// the guest fills in its draw virtual. It is reported rather than used: our guest never
	/// draws, so that list is expected to be empty here, and a non-zero value would mean the
	/// draw path runs after all and the richer 24-byte records are available too.
	/// </summary>
	public static uint[] GetEntityMorphParts(uint entity, out int builtParts)
	{
		builtParts = 0;
		if (TryGetAbiVersion() is null)
		{
			return System.Array.Empty<uint>();
		}

		_morphParts ??= new uint[MaxMorphParts];
		uint written = RefiiHostGetEntityMorphParts(
			entity, _morphParts, MaxMorphParts, out uint built);
		builtParts = (int)built;

		if (written == 0)
		{
			return System.Array.Empty<uint>();
		}

		var hashes = new uint[written];
		System.Array.Copy(_morphParts, hashes, written);
		return hashes;
	}

	/// <summary>
	/// Parts read per composite character. A layered appearance is a head, a torso, legs, eyes
	/// and accessories — 64 is far past anything the character creator can produce.
	/// </summary>
	private const uint MaxMorphParts = 64;

	private static uint[]? _morphParts;

	/// <summary>
	/// The front end as a MODEL — which entries exist and which screen is up — for a menu
	/// Godot draws itself.
	///
	/// This replaces reading the guest's widgets. The main menu has no C++ item list and no
	/// C++ highlight: items, order, nesting and the highlight all live in the shipped Lua
	/// `g_Menu`, and the C++ side only spawns into it. So there is nothing to read that says
	/// "item 3 is selected" — a native menu owns its own highlight, and what it needs from the
	/// guest is which entries the guest's own spawn conditions will honour.
	/// </summary>
	public readonly record struct FrontEndModel(
		bool SignedIn, uint UserIndex, uint LanguageCount, uint LanguageIndex,
		bool HasNewDownloadableContent, bool MenuIsUp, uint MenuState, uint TitleState,
		bool CanPressA, uint LoadCardCursor, uint EnumeratedSaves)
	{
		/// <summary>
		/// Saves the guest has ENUMERATED — not "saves that exist".
		///
		/// Read from the vector its own "Load Game" branch tests, so a non-zero value is the
		/// guest saying it has cards to offer. Zero means it has not enumerated any yet, which
		/// is not the same as there being none: that answer needs a live
		/// XamContentCreateEnumerator, which is a call rather than a read.
		/// </summary>
		public bool HasSaves => EnumeratedSaves > 0;

		/// <summary>
		/// Whether the "Language" entry is spawned — the guest shows it only when more than one
		/// language is installed (`lwz r11, dword_83319934` / `cmplwi r11, 1` / `ble` past the
		/// spawn). A host list that always shows it would offer an entry the guest ignores.
		/// </summary>
		public bool HasLanguageEntry => LanguageCount > 1;
	}

	private static uint[]? _frontEndModelWords;

	/// <summary>
	/// The front-end model, or null when the front-end GUI is not up.
	///
	/// Null once the game starts, and that is correct rather than a failure: the guest
	/// republishes its GUI global with a base-only instance that has no screens at all.
	/// </summary>
	public static FrontEndModel? GetFrontEndModel()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		_frontEndModelWords ??= new uint[11];
		if (RefiiHostGetFrontEndModel(_frontEndModelWords) == 0)
		{
			return null;
		}

		uint[] w = _frontEndModelWords;
		return new FrontEndModel(
			w[0] != 0, w[1], w[2], w[3], w[4] != 0, w[5] != 0, w[6], w[7], w[8] != 0, w[9],
			w[10]);
	}

	/// <summary>The guest's camera, in guest world space.</summary>
	public readonly record struct GuestCamera(
		Vector3 Position, Vector3 Up, Vector3 Forward, float HorizontalFov, bool Flag)
	{
		/// <summary>
		/// True when this is the engine's "no camera" default rather than a live one.
		///
		/// There is no validity bit; the only tell is the exact default signature the
		/// guest writes when there is no camera to report — position zero, up +Z,
		/// forward +Y, and 1.2217305 rad (70°).
		/// </summary>
		public bool IsDefault =>
			Position == Vector3.Zero && Up == new Vector3(0f, 0f, 1f) &&
			Forward == new Vector3(0f, 1f, 0f) && Mathf.IsEqualApprox(HorizontalFov, 1.2217305f);

		/// <summary>
		/// The VERTICAL field of view in degrees, for Godot.
		///
		/// The guest stores a HORIZONTAL fov. Both of its aspect branches collapse to the
		/// same vertical angle — 4:3 gives 2·atan(tan(h/2)·0.75) and 16:9 gives
		/// 2·atan(tan(h/2)·(4/3)·(9/16)), which is the same number — so widescreen widens
		/// horizontally only and this needs no aspect input.
		/// </summary>
		public float VerticalFovDegrees =>
			Mathf.RadToDeg(2f * Mathf.Atan(0.75f * Mathf.Tan(HorizontalFov * 0.5f)));
	}

	/// <summary>
	/// The guest's camera, or null when it could not be read coherently.
	///
	/// The snapshot is five unsynchronised stores with no sequence counter, so the bridge
	/// reads it twice and requires agreement; a disagreement returns null rather than a
	/// torn camera.
	/// </summary>
	public static GuestCamera? GetCamera()
	{
		var values = new float[11];
		if (RefiiHostGetCamera(values) == 0)
		{
			return null;
		}

		return new GuestCamera(
			new Vector3(values[0], values[1], values[2]),
			new Vector3(values[3], values[4], values[5]),
			new Vector3(values[6], values[7], values[8]),
			values[9],
			values[10] != 0f);
	}

	/// <summary>Whether the guest still wants the movie the host is showing.</summary>
	public enum MoviePlayback { None = 0, Wanted = 1, EndedByGuest = 2 }

	/// <summary>
	/// The guest's view of the movie in progress.
	///
	/// The host and the guest can each end a movie. The host ends one by running out of
	/// frames; the guest ends one by finishing its own decode or by SKIPPING — which is
	/// what a button press at the title does. Watching for the second is what stops a
	/// skipped attract clip playing on over whatever the guest moved on to.
	/// </summary>
	public static MoviePlayback MovieState() => (MoviePlayback)RefiiHostMovieState();

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
	/// The raw eight words behind <see cref="TryGetFrontEnd"/>: the GUI object, the ACTIVE
	/// screen, which one it is, state, press-A-armed, cursor, scene slide, slide count.
	///
	/// Worth having because <see cref="FrontEndScreen.None"/> is not "no screen" — it is a
	/// screen that is neither the title nor the menu, which is exactly what the front end
	/// hands over to. Without the pointer a run can only say the guest went somewhere.
	/// </summary>
	public static uint[]? GetFrontEndRaw()
	{
		uint[] words = new uint[10];
		return RefiiHostGetFrontEnd(words) != 0 ? words : null;
	}

	/// <summary>
	/// The front-end GUI's vtable — the class whose slots include the GUI pump at
	/// 0x82379170. This is an identity STATED by the binary, not a guess: the base ctor
	/// writes off_82001610 at 0x8237111C and the derived front-end ctor overwrites it, so
	/// the word at the object's address says which of the two constructions built it.
	/// </summary>
	private const uint FrontEndGuiVtable = 0x820A924Cu;

	/// <summary>
	/// Whether the object at dword_83496B08 is the FRONT-END GUI at all.
	///
	/// False once the game starts: Game_InitDatabases republishes that global with a
	/// base-only instance (vtable off_82001610 = 0x82001610) that has no screens, and
	/// reading its +180/+184/+188 as title/menu/active reports "no front end screen" about
	/// an object that never had one. That misreads the game STARTING as the front end
	/// going nowhere.
	/// </summary>
	/// <summary>
	/// Whether the guest is in game, as the guest STATES it — <c>byte_83496A5E</c>, set by
	/// Game_InitDatabases (0x82311648) and cleared by the game mode's shutdown (0x82311860).
	/// Use this rather than inferring it from the front end disappearing.
	/// </summary>
	public static bool IsInGame => RefiiHostIsInGame() != 0;

	/// <summary>
	/// The guest's simulation tick — <c>dword_83319650</c>, incremented by the world update.
	///
	/// This is the guest's OWN clock, and it is the only honest answer to "has the simulation
	/// moved since we last looked". Godot's frame delta and the audio rate both measure our
	/// side of the boundary; the guest advances on neither. Compare successive values for
	/// INEQUALITY only — do not subtract and assume the difference is a count of ticks.
	/// </summary>
	public static uint SimTick => RefiiHostGetSimTick();

	/// <summary>The guest's live time of day, or null while the world clock does not exist.</summary>
	/// <remarks>
	/// NULL IS NOT AN ERROR and must not be substituted with a default. `GameTimeManager` is built
	/// at game-database init and destroyed with the world, so before a level exists there is no
	/// clock — and a host that answers "midday" in the meantime lights the front end and the first
	/// frames of a load from a time the game is not at, then snaps when the real value arrives.
	/// Callers hold their last good value instead.
	///
	/// The fraction is in [0, 1) and is the SAME value the guest's own day-set selector blends on,
	/// so a theme evaluated against it reproduces the guest's selection rather than approximating
	/// it — see the native side for the chain and the proof.
	/// </remarks>
	public static GuestClock? TimeOfDay()
	{
		try
		{
			if (RefiiHostIsRunning() != 1)
			{
				return null;
			}

			if (RefiiHostGetTimeOfDay(out float fraction, out int day, out int frozen) == 0)
			{
				return null;
			}

			return new GuestClock(fraction, day, frozen != 0);
		}
		catch (DllNotFoundException)
		{
			return null;
		}
	}

	/// <param name="Frozen">
	/// The guest's own pause: non-zero means something holds a stop-time request and the clock is
	/// not advancing. Worth carrying rather than inferring from an unchanging fraction, because a
	/// clock that merely looks still is indistinguishable from one we have stopped reading.
	/// </param>
	public readonly record struct GuestClock(float Fraction, int DayCount, bool Frozen);

	/// <summary>The engine-preloading gate state 7 of CmdLevelChange_Update polls.</summary>
	public readonly record struct RenderFence(
		uint Manager, double Submitted, double Deadline, bool Busy,
		uint ViewCursor, uint ViewSubmitted, uint PreloadState, uint TextureResidency,
		uint PreloadFlag)
	{
		/// <summary>
		/// What <c>sub_82387B38</c> (the guest's own <c>IsEnginePreLoading</c>) answers. The
		/// level load only advances past state 7 when this is false.
		/// </summary>
		public bool BlocksLevelLoad => Submitted <= Deadline || Busy;

		/// <summary>
		/// Which producer is holding the flag, in words.
		///
		/// The flag itself carries no state — it is <c>byte_83496E20</c> re-copied every
		/// render-thread iteration — so "busy" alone names nothing. Its one writer has three
		/// arms, and the third times itself out, which leaves a short list.
		/// </summary>
		public string Holder =>
			ViewCursor == 0xFFFFFFFFu && ViewSubmitted == 0xFFFFFFFFu
				? "STALE — no view submitted, so nothing is recomputing the flag"
				: PreloadState is >= 1 and <= 4
					? $"resource preload (state {PreloadState})"
					: TextureResidency != 0
						? "streamed-texture residency"
						: PreloadFlag != 0
							? "scene walk (self-limiting) or a producer we cannot see"
							: "nothing — the mirror at +600 disagrees with 0x83496E20";
	}

	/// <summary>
	/// The loading screen's take-down handshake — see RefiiHostGetLoadingScreen.
	///
	/// <c>Fade</c> counts 1.0 down to ~0 at 1/15 per render-thread tick; <c>Requested</c> is
	/// set by the game thread just before it blocks on an INFINITE wait; <c>Done</c> is
	/// latched by the render thread when it signals. Fade pinned at 1.0 with Requested and
	/// no Done is the stall.
	/// </summary>
	public readonly record struct LoadingScreen(
		uint Manager, uint Screen, float Fade, bool Requested, bool Done);

	/// <summary>
	/// How far the guest has got into starting a game: the GDB manager, the in-game flag,
	/// and the two mode singletons. See RefiiHostGetInitMarkers.
	/// </summary>
	public readonly record struct InitMarkers(
		uint GdbManager, bool InGame, uint GameMode, uint FrontEndMode);

	public static InitMarkers GetInitMarkers()
	{
		uint[] words = new uint[4];
		RefiiHostGetInitMarkers(words);
		return new InitMarkers(words[0], words[1] != 0, words[2], words[3]);
	}

	/// <summary>One command-stream ring: the four numbers that decide whether a reserve blocks.</summary>
	public readonly record struct SceneStream(
		uint Object, uint Capacity, uint WriteCursor, uint WriteConsumed,
		uint ReaderConsumed, uint LastRead)
	{
		/// <summary>
		/// sub_8222ABE0's own condition: it spins while the reader is at or ahead of the
		/// writer AND the write would cross it.
		/// </summary>
		public bool WouldBlock(uint bytes) =>
			ReaderConsumed >= WriteCursor && WriteCursor + bytes > ReaderConsumed;
	}

	/// <summary>Where the guest said it is going. Level is empty until it has decided.</summary>
	public readonly record struct StartLevel(string World, string Level, string Entrance);

	/// <summary>
	/// The world/level/entrance the guest asked for, or null before it has asked.
	///
	/// This is the guest STATING its destination — Godot builds the region it is told to
	/// rather than inferring one, which is the whole split in a single call.
	/// </summary>
	public static StartLevel? GetStartLevel()
	{
		var world = new byte[128];
		var level = new byte[128];
		var entrance = new byte[128];
		if (RefiiHostGetStartLevel(world, level, entrance, (uint)level.Length) == 0)
		{
			return null;
		}

		return new StartLevel(Decode(world), Decode(level), Decode(entrance));
	}

	/// <summary>
	/// The level the guest is STANDING IN, with the key that identifies it.
	///
	/// <see cref="Key"/> is the guest's own FNV-1 hash of lowercase "world/level", held at
	/// Level+0xB4. It is the value to compare frame to frame: one aligned dword that changes on
	/// exactly the transitions that reload a level, whichever mechanism caused them.
	/// </summary>
	public readonly record struct CurrentLevel(
		string World, string Level, string Entrance, uint Key);

	/// <summary>
	/// Where the guest actually IS, or null when no level is standing.
	///
	/// USE THIS, NOT <see cref="GetStartLevel"/>, TO NOTICE A LEVEL CHANGE. The start globals
	/// are written only by game-START paths — the new-game entry, the front-end target setter,
	/// the dev override, the network-join handler and the debug level menu. A door, a
	/// teleporter, fast travel and `World.LoadLevel` all put their destination on the
	/// CmdLevelChange command object instead and never touch a global, so polling the start
	/// values sees a new game and nothing else.
	///
	/// Null is ordinary, not a failure: it is the state before the first load, and the window
	/// during a transition when the old Level has been released and the new one not yet built.
	/// </summary>
	/// <summary>
	/// A level change the guest has queued, as its own command describes it.
	///
	/// <see cref="State"/> is the command's state word (1..9, never 0). The entity teardown
	/// happens at state 2, so state >= 2 is the moment a host may free its scene.
	///
	/// <see cref="FullLoad"/> is the one that must not be ignored: when it is false the guest
	/// tears NOTHING down — it is a reposition inside the resident level — so freeing a scene
	/// on it rebuilds a region the guest never unloaded.
	/// </summary>
	public readonly record struct LevelChange(
		string World, string Level, string Entrance, uint State, uint TravelType, bool FullLoad);

	/// <summary>
	/// The level change in flight, or null when none is.
	///
	/// Prefer this to the falling edge of <see cref="WorldIsReady"/> for deciding when to take a
	/// world down: that byte is not cleared until state 3, after the blocking load has already
	/// returned, while the entities were freed back in state 2.
	/// </summary>
	/// <summary>
	/// Holds the guest in its own loading screen while Godot builds a region.
	///
	/// The guest asks its renderer "IsEnginePreLoading" from state 7 of its level-change command
	/// and waits while the answer is yes. Godot is that renderer, so this is answering a stated
	/// question rather than stalling the simulation from outside — the guest keeps its loading
	/// screen, its music and its own pacing throughout.
	///
	/// MUST BE CLEARED on every path that leaves a build, success or failure. A hold that is
	/// never released parks the guest at state 7 indefinitely.
	/// </summary>
	public static void SetWorldBuildPending(bool pending)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		RefiiHostSetWorldBuildPending(pending ? 1u : 0u);
	}

	/// <summary>
	/// Whether the guest's world is ACTIVATED and populated — the one signal both load paths
	/// write, and the right question to ask before mounting a region against it.
	///
	/// `World+0xA8`, set by the entity activator and cleared by the world teardown. Travel
	/// reaches it from level-change state 5, a load-from-save from its own state 2, so this
	/// needs no knowledge of which mechanism moved the player and nothing walked.
	///
	/// Prefer this to <see cref="WorldIsReady"/>: that byte is only set at the terminal state,
	/// behind the render fence and loading-screen gates a headless host may never satisfy, and
	/// it boots as true.
	/// </summary>
	public static bool WorldIsActive()
	{
		if (TryGetAbiVersion() is null)
		{
			return false;
		}

		return RefiiHostWorldIsActive() != 0;
	}

	/// <summary>
	/// The gameflow command queue as text — what is actually in it, for when a walk of it comes
	/// back empty and the alternative is another guess at the layout.
	/// </summary>
	public static string DescribeGameFlowQueue()
	{
		if (TryGetAbiVersion() is null)
		{
			return "bridge unavailable";
		}

		var text = new byte[512];
		return RefiiHostDescribeGameFlowQueue(text, (uint)text.Length) == 0
			? "unreadable"
			: Decode(text);
	}

	public static LevelChange? GetLevelChange()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		var world = new byte[128];
		var level = new byte[128];
		var entrance = new byte[128];
		if (RefiiHostGetLevelChange(world, level, entrance, (uint)level.Length,
				out uint state, out uint travel, out uint fullLoad) == 0)
		{
			return null;
		}

		return new LevelChange(
			Decode(world), Decode(level), Decode(entrance), state, travel, fullLoad != 0);
	}

	public static CurrentLevel? GetCurrentLevel()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		var world = new byte[128];
		var level = new byte[128];
		var entrance = new byte[128];
		if (RefiiHostGetCurrentLevel(world, level, entrance, (uint)level.Length, out uint key) == 0)
		{
			return null;
		}

		return new CurrentLevel(Decode(world), Decode(level), Decode(entrance), key);
	}

	/// <summary>
	/// The scenario the guest starts in, or null when it has not stated one yet.
	///
	/// This is what picks the map. The guest builds
	/// <c>Data\Worlds\&lt;World&gt;\&lt;Level&gt;\&lt;Scenario&gt;\&lt;Scenario&gt;.list</c> and loads every
	/// heightfield that manifest names, so a level with five heightfields has five scenarios
	/// and the level name alone cannot choose between them.
	/// </summary>
	public static string? GetStartScenario()
	{
		var scenario = new byte[128];
		return RefiiHostGetStartScenario(scenario, (uint)scenario.Length) == 0
			? null
			: Decode(scenario);
	}

	/// <summary>
	/// The scenario the guest is ACTUALLY in, or null when no level is loaded.
	///
	/// Prefer this over <see cref="GetStartScenario"/>: `Layers.ActivateScenario` rewrites it
	/// and reloads the level, so the start value goes stale the moment a quest advances.
	/// </summary>
	public static string? GetActiveScenario()
	{
		var scenario = new byte[128];
		return RefiiHostGetActiveScenario(scenario, (uint)scenario.Length) == 0
			? null
			: Decode(scenario);
	}

	/// <summary>
	/// FNV-1 hashes of the layers the guest currently has active.
	///
	/// Layers gate ENTITY INSTANTIATION, so a host that places every placement it can find
	/// places things the guest has not. The hash is over the layer name's EXACT bytes — no
	/// lowercasing, no separator normalisation, unlike the world+level key.
	/// </summary>
	public static uint[] GetActiveLayers()
	{
		var hashes = new uint[256];
		uint count = RefiiHostGetActiveLayers(hashes, (uint)hashes.Length);
		if (count == 0)
		{
			return System.Array.Empty<uint>();
		}

		System.Array.Resize(ref hashes, (int)System.Math.Min(count, (uint)hashes.Length));
		return hashes;
	}

	/// <summary>
	/// Asks the guest to perform a front-end action, by the NAME it dispatches on.
	///
	/// Nothing happens on this thread. The name is queued and the guest picks it up inside
	/// its own tick, which is what makes this safe to call from Godot: it touches no guest
	/// state here, and the work lands where the guest's own code would have done it.
	///
	/// Returns the queue depth after posting — a depth that climbs means the pump is holding
	/// the action because the front end is not up yet, which is the normal state during the
	/// boot movies.
	/// </summary>
	public static uint PostFrontEndAction(string name) =>
		RefiiHostPostFrontEndAction(name);

	/// <summary>
	/// The front end's own storage criteria.
	///
	/// <paramref name="Count"/> is -1 until the tick pump has taken a sample, and that is NOT
	/// zero. A menu built while it is -1 has no answer yet and must not conclude "no saves" —
	/// answering early is exactly how Load Game went missing on a profile that had some.
	/// </summary>
	public readonly record struct SaveState(bool SignedIn, int Count, int Limit)
	{
		/// <summary>True once the pump has answered at all.</summary>
		public bool Known => Count >= 0;

		/// <summary>
		/// The guest's own Load Game spawn guard, at 0x826E2264..0x826E2278:
		/// signed in, and `sub_82443308()` returned non-zero.
		/// </summary>
		public bool CanLoad => SignedIn && Count > 0;

		/// <summary>
		/// The guest's own New Game slot cap, at 0x826E0148. Unsigned compare there, and the
		/// limit is 6 once the front end has entered — `sub_82378618` stores it over the 150
		/// that sits in the image.
		/// </summary>
		public bool AtSaveLimit => Known && Limit > 0 && Count >= Limit;
	}

	/// <summary>
	/// What the guest would gate its own New Game and Load Game entries on.
	///
	/// Two of the three numbers are plain guest memory; the count is a guest CALL and therefore
	/// arrives from the tick pump's sample rather than live. See RefiiHostGetSaveState.
	/// </summary>
	public static SaveState GetSaveState()
	{
		try
		{
			RefiiHostGetSaveState(out uint signedIn, out int count, out uint limit);
			return new SaveState(signedIn != 0, count, (int)limit);
		}
		catch (EntryPointNotFoundException)
		{
			// An older native library. Unknown rather than empty, so the caller holds the entry
			// back instead of asserting there are no saves.
			return new SaveState(false, -1, 0);
		}
	}

	/// <summary>Asks the pump to re-take the saved-game count on its next front-end tick.</summary>
	public static void RefreshSaveState()
	{
		try
		{
			RefiiHostRefreshSaveState();
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// Loads the save at <paramref name="index"/> in the guest's own enumerated list.
	///
	/// AN INDEX, NOT A NAME, because the guest identifies a save by the ADDRESS of its handle
	/// slot inside its own vector — `Frontend_SetStartGameTarget` takes `*(screen+84) + 4*i` and
	/// `*(screen+148) + 40*i + 12`, both pointers to live handles. Sending the text would mean
	/// finding it again on the guest's side and trusting two orderings to agree; the index is
	/// the join the guest itself uses.
	///
	/// Posted, not called — the commit allocates, rewrites `dword_83496AB8` and mutates
	/// refcounts non-atomically, so it belongs on the guest's tick thread and nowhere else.
	/// </summary>
	public static uint LoadSave(int index) => PostFrontEndAction($"LoadSave:{index}");

	/// <summary>One installed language, as the guest's own table holds it.</summary>
	public readonly record struct Language(int Index, string Name, bool IsCurrent);

	/// <summary>
	/// The languages the guest has installed, in its own table order.
	///
	/// <see cref="Language.Name"/> is the DISPLAY NAME the guest spawns its menu entry with, not
	/// the locale code — `sub_826E1C98` hands that string to the spawner and the front end then
	/// resolves it through Babel, so a caller should put it through the same book.
	///
	/// The INDEX is the row, and it is the whole reason this exists: the guest's language
	/// entries are dispatched by command 302 with that index, never by name, so a host menu that
	/// only knows the label cannot perform the action.
	/// </summary>
	public static System.Collections.Generic.List<Language> GetLanguages()
	{
		var languages = new System.Collections.Generic.List<Language>();

		if (TryGetAbiVersion() is null || !IsRunning)
		{
			return languages;
		}

		var names = new byte[2048];
		uint count;
		uint current;

		try
		{
			count = RefiiHostGetLanguages(out current, names, (uint)names.Length);
		}
		catch (EntryPointNotFoundException)
		{
			// An older native library. Degrading to "no languages" is right rather than fatal:
			// the caller then leaves the Language entry childless, which is what it was before
			// this export existed — and a menu that throws while being built takes the whole
			// front end with it.
			return languages;
		}

		int at = 0;
		for (uint i = 0; i < count; ++i)
		{
			int end = System.Array.IndexOf(names, (byte)0, at);
			if (end < 0)
			{
				break;
			}

			languages.Add(new Language(
				(int)i,
				System.Text.Encoding.UTF8.GetString(names, at, end - at),
				i == current));
			at = end + 1;
		}

		return languages;
	}

	/// <summary>
	/// Pump health. Three numbers because they fail differently: zero ticks is a hook that
	/// never landed, ticks climbing with a non-empty queue is the guest not yet ready, and
	/// ticks climbing with nothing drained is nobody posting.
	/// </summary>
	public static (ulong Ticks, ulong Drained, uint Queued) GuestTickPump()
	{
		RefiiHostGuestTickPump(out ulong ticks, out ulong drained, out uint queued);
		return (ticks, drained, queued);
	}

	/// <summary>The live rings, empty slots dropped.</summary>
	public static List<SceneStream> GetSceneStreams()
	{
		uint[] words = new uint[24];
		RefiiHostGetSceneStreams(words);

		var streams = new List<SceneStream>(4);
		for (int slot = 0; slot < 4; ++slot)
		{
			int at = slot * 6;
			if (words[at] == 0)
			{
				continue;
			}

			streams.Add(new SceneStream(words[at], words[at + 1], words[at + 2],
				words[at + 3], words[at + 4], words[at + 5]));
		}

		return streams;
	}

	/// <summary>
	/// WHICH loading screen the guest picked — its type and the texture name it chose.
	///
	/// The loading screen is NOT a Gameface presentation: `sub_8235F108` submits direct textured
	/// quads, so there is no element tree to mirror and this pair is the whole interface. The
	/// name is read through an LHString HANDLE (two dereferences from `screen+272`), verified in
	/// `sub_8235F2F8` — every writer there is `sub_822651A0(a1 + 272, ...)`.
	///
	/// `Body` is reported so a run where that indirection is wrong shows an address rather than
	/// quietly producing an empty name.
	/// </summary>
	public readonly record struct LoadingArt(uint Type, uint Body, string Name);

	/// <summary>The chosen loading art, or null when there is no loading screen.</summary>
	public static LoadingArt? GetLoadingScreenArt()
	{
		var buffer = new byte[128];
		try
		{
			uint ok = RefiiHostGetLoadingScreenArt(
				out uint type, out uint body, buffer, (uint)buffer.Length);
			if (body == 0 && ok == 0)
			{
				return null;
			}

			int end = System.Array.IndexOf(buffer, (byte)0);
			return new LoadingArt(type, body,
				System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? 0 : end));
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>
	/// The guest's own "the world is loaded and ready" byte — `game + 0x105`.
	///
	/// NOT the same as the loading screen coming down. `CmdLevelChange_Update` clears it when
	/// the load begins and sets it when its nine-state machine finishes.
	/// </summary>
	public static bool WorldIsReady()
	{
		try
		{
			return RefiiHostWorldIsReady() != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>The handshake, or null when no loading screen object exists.</summary>
	public static LoadingScreen? GetLoadingScreen() =>
		RefiiHostGetLoadingScreen(out uint manager, out uint screen, out float fade,
			out uint requested, out uint done) != 0
			? new LoadingScreen(manager, screen, fade, requested != 0, done != 0)
			: null;

	private static uint[]? _fenceArms;

	/// <summary>
	/// The engine-preloading gate the level load waits on, or null when the chain did not
	/// resolve.
	///
	/// The object is the guest's "3D Engine" and the predicate is its own `IsEnginePreLoading`
	/// — state 7 of the level change is literally "while preloading, wait". `Deadline` is the
	/// render deadline barrier at +176, NOT a retired-frame count: it sitting still while
	/// `Submitted` climbs past it is the normal resting state.
	///
	/// The arms say WHICH of the three producers is holding the flag, so a stall names its own
	/// cause instead of being a byte that is mysteriously 1.
	/// </summary>
	public static RenderFence? GetRenderFence()
	{
		_fenceArms ??= new uint[5];
		return RefiiHostGetRenderFence(out uint manager, out double submitted,
			out double deadline, out uint busy, _fenceArms) != 0
			? new RenderFence(manager, submitted, deadline, busy != 0,
				_fenceArms[0], _fenceArms[1], _fenceArms[2], _fenceArms[3], _fenceArms[4])
			: null;
	}

	public static bool FrontEndGuiIsUp =>
		GetFrontEndRaw() is { } words && words[9] == FrontEndGuiVtable;

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
		uint[] words = new uint[10];
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
	/// <summary>
	/// One element of the guest's live front end.
	///
	/// <paramref name="Type"/> is the element kind byte the runtime holds. It carries no live
	/// state of its own — it is here so the POSITIONAL join against the .bgf can be verified.
	/// Slide, text and opacity are all applied by ordinal, on the belief that the guest's
	/// depth-first walk and the file's element order are the same sequence; comparing types
	/// turns that belief into a check that names the first index where it fails.
	/// </summary>
	public readonly record struct UiElement(
		uint NameHash, int Slide, string? Text, int Type, int Flags)
	{
		/// <summary>
		/// Whether the guest is DRAWING this element.
		///
		/// `globalactive`, bit 0x1000 of the element's flags halfword — the runtime derives it
		/// every frame as <c>active AND in-time-range AND parent's globalactive</c>, and
		/// `sub_82188B70` copies exactly this bit onto the render node's draw gate, which
		/// `sub_8229E400` tests before descending. A false here prunes the whole subtree.
		///
		/// This is the value to follow. Computing visibility from the .bgf's authored state
		/// plus slide overrides reproduces only what the FILE says, and the screens' own Lua
		/// changes it at runtime.
		/// </summary>
		public bool IsGloballyActive => (Flags & 0x1000) != 0;

		/// <summary>
		/// The AUTHORED active flag, bit 0x2000 — what the .bgf carried and setAttribute
		/// writes. Not the draw gate: an active element inside an inactive parent still does
		/// not draw. Kept because the two disagreeing is worth being able to see.
		/// </summary>
		public bool IsActive => (Flags & 0x2000) != 0;
	}

	/// <summary>One live Gameface presentation: where it is, its root, its path, and whether
	/// it is actually on screen.</summary>
	public readonly record struct Presentation(
		uint Address, uint Root, string Name, uint State, uint ActiveElements)
	{
		/// <summary>
		/// The guest is drawing this screen.
		///
		/// LIVE IS NOT VISIBLE. The guest keeps closed menus in the registry permanently with
		/// valid roots, so "it exists" says nothing about whether it is on screen.
		/// What decides it is the ROOT element's own `active` bit — <c>CPresentation::Update</c>
		/// skips the entire tree walk when it is clear, leaving the screen contributing no
		/// elements at all.
		///
		/// Frozen screens are excluded too. A non-zero freeze byte returns before the per-frame
		/// lists are rebuilt, so the screen holds stale content rather than current content.
		/// </summary>
		/// <remarks>
		/// THREE gates, at three levels, and all of them must pass. The guest suppresses a
		/// screen at whichever level suits it, so checking one is not enough:
		/// the presentation's own draw byte, its update byte, and the root element's
		/// <c>active</c> bit that its element traversal tests.
		/// </remarks>
		public bool IsShowing =>
			(State & 1u) != 0u && (State & 2u) == 0u && (State & 4u) == 0u && Root != 0u;

		/// <summary>Held at last frame's content rather than being updated.</summary>
		public bool IsFrozen => (State & 2u) != 0u;

		/// <summary>
		/// Parked by the guest: loaded, kept in the registry, and skipped by the scene draw.
		///
		/// This is how the pause menu and the menu cycler spend the whole game. Their loaders
		/// write the suppression pair immediately after loading; the HUD's loader never touches
		/// it, which is the entire reason only the HUD is on screen.
		/// </summary>
		public bool IsSuppressed => (State & 4u) != 0u;

		/// <summary>
		/// True when the path ends with the given file name.
		///
		/// A SUFFIX test rather than an equality one, deliberately. The string is the path the
		/// factory was handed, built by the guest's own path conformer, and neither its prefix
		/// nor its slash direction is established — matching the whole thing would be matching
		/// a format we have not proven.
		/// </summary>
		public bool Is(string fileName) =>
			Name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Every presentation the guest has live — the HUD, whichever menus are open, and the
	/// front end, all from one registry.
	///
	/// This is what replaced "the front end's active screen" as the way to find UI. That older
	/// route went through a field only the front-end GUI object has, so it could see exactly
	/// one screen and only before the game took over; everything in game was invisible to it.
	/// </summary>
	public static List<Presentation> GetPresentations()
	{
		uint count = RefiiHostGetPresentations(null, 0, null, 0);
		if (count == 0)
		{
			return new List<Presentation>();
		}

		// Headroom on both, for the same reason the element walk takes it: the guest is
		// mutating the registry while we read, so the second call can find more than the probe.
		// Stride 5 since ABI 38 — the two words that say whether it is on screen.
		var words = new uint[count * 5 + 32];
		var names = new byte[count * 96 + 512];
		uint written = RefiiHostGetPresentations(
			words, (uint)words.Length, names, (uint)names.Length);

		int usable = (int)Math.Min(written, (uint)(words.Length / 5));
		var live = new List<Presentation>(usable);
		for (int i = 0; i < usable; ++i)
		{
			uint offset = words[i * 5 + 2];
			string name = string.Empty;
			if (offset != 0xFFFFFFFFu && offset < names.Length)
			{
				int end = (int)offset;
				while (end < names.Length && names[end] != 0)
				{
					++end;
				}

				name = Encoding.ASCII.GetString(names, (int)offset, end - (int)offset);
			}

			live.Add(new Presentation(
				words[i * 5], words[i * 5 + 1], name, words[i * 5 + 3], words[i * 5 + 4]));
		}

		return live;
	}

	/// <summary>
	/// Every element of the guest's active screen: its slide and the text it is actually
	/// displaying.
	///
	/// The text is what makes this worth reading rather than inferring — the shipped
	/// presentation carries placeholders the game overwrites at runtime, and some tags
	/// resolve through a table that is not the Babel book.
	/// </summary>
	/// <param name="presentation">
	/// A presentation from <see cref="GetPresentations"/>, or 0 for the front end's active
	/// screen — which is what this meant before the HUD and the in-game menus needed reaching.
	/// </param>
	public static List<UiElement>? GetUiElements(uint presentation = 0)
	{
		uint count = RefiiHostGetUiElements(presentation, null, 0, null, 0);
		if (count == 0)
		{
			return null;
		}

		// Sized from the probe, with headroom: the guest is mutating the tree, so the
		// second walk can legitimately find more than the first.
		// Stride 5 since ABI 30 — type byte and flags halfword.
		var words = new uint[count * 5 + 64];
		var chars = new ushort[count * 24 + 1024];
		uint written = RefiiHostGetUiElements(
			presentation, words, (uint)words.Length, chars, (uint)chars.Length);

		int usable = (int)Math.Min(written, (uint)(words.Length / 5));
		var elements = new List<UiElement>(usable);
		for (int i = 0; i < usable; ++i)
		{
			uint offset = words[i * 5 + 2];
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

			elements.Add(new UiElement(words[i * 5], (int)words[i * 5 + 1], text,
				(int)words[i * 5 + 3], (int)words[i * 5 + 4]));
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
	private static extern uint RefiiHostMovieState();

	[DllImport(Library)]
	private static extern int RefiiHostGetCamera(float[] outValues);

	[DllImport(Library)]
	private static extern uint RefiiHostGetUiAttributes(
		uint presentation, uint[] ids, uint idCount, uint[]? outWords, uint outCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFrontEndState();

	[DllImport(Library)]
	private static extern uint RefiiHostIsInGame();

	[DllImport(Library)]
	private static extern uint RefiiHostGetSimTick();

	[DllImport(Library)]
	private static extern uint RefiiHostGetTimeOfDay(
		out float fraction, out int dayCount, out int frozen);

	[DllImport(Library)]
	private static extern int RefiiHostGetRenderFence(
		out uint manager, out double submitted, out double deadline, out uint busy,
		uint[] outArms);

	[DllImport(Library)]
	private static extern void RefiiHostGetInitMarkers(uint[] outWords);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntities(
		uint[] outWords, uint maxEntities, out uint outUnknown,
		uint[] outUnknownVtables, uint maxUnknownVtables, out uint outUnknownVtableCount);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityPose(
		uint entity, float[] outTransforms, int[] outParents, uint maxBones, out uint outSpace,
		out uint outTorn);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityRestPose(
		uint entity, float[] outTransforms, int[] outParents, uint maxBones);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityBoneNames(
		uint entity, byte[] outNames, uint stride, uint maxBones);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityComponents(
		uint entity, uint[] outPairs, uint maxPairs);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityMorphParts(
		uint entity, uint[] outHashes, uint maxHashes, out uint outBuilt);

	[DllImport(Library)]
	private static extern void RefiiHostGetSceneStreams(uint[] outWords);

	[DllImport(Library, CharSet = CharSet.Ansi)]
	private static extern uint RefiiHostPostFrontEndAction(string name);

	[DllImport(Library)]
	private static extern uint RefiiHostGetLanguages(
		out uint current, byte[] names, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetLoadingScreenArt(
		out uint type, out uint body, byte[] name, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostWorldIsReady();

	[DllImport(Library)]
	private static extern uint RefiiHostGetSaveState(
		out uint signedIn, out int count, out uint limit);

	[DllImport(Library)]
	private static extern void RefiiHostRefreshSaveState();

	[DllImport(Library)]
	private static extern void RefiiHostGuestTickPump(
		out ulong ticks, out ulong drained, out uint queued);

	[DllImport(Library)]
	private static extern void RefiiHostSetWorldBuildPending(uint pending);

	[DllImport(Library)]
	private static extern uint RefiiHostDescribeGameFlowQueue(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostWorldIsActive();

	[DllImport(Library)]
	private static extern uint RefiiHostGetLevelChange(
		byte[] world, byte[] level, byte[] entrance, uint capacity,
		out uint outState, out uint outTravelType, out uint outFullLoad);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCurrentLevel(
		byte[] world, byte[] level, byte[] entrance, uint capacity, out uint outKey);

	[DllImport(Library)]
	private static extern int RefiiHostGetStartLevel(
		byte[] world, byte[] level, byte[] entrance, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetStartScenario(byte[] scenario, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetActiveScenario(byte[] scenario, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetActiveLayers(uint[] outHashes, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetLoadingScreen(
		out uint manager, out uint screen, out float fade, out uint requested, out uint done);

	[DllImport(Library)]
	private static extern int RefiiHostGetFrontEnd(uint[] outWords);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFrontEndModel(uint[] outWords);

	[DllImport(Library)]
	private static extern uint RefiiHostGetUiElements(
		uint presentation, uint[]? outWords, uint wordCapacity,
		ushort[]? outText, uint textCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetPresentations(
		uint[]? outWords, uint wordCapacity, byte[]? outNames, uint nameCapacity);

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
