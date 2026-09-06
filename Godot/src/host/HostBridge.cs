using Godot;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Refii.Core;
using Refii.Text;
using Refii.Ui;

using static Refii.Ui.Engine_UiModel;

namespace Refii.Host;

/// <summary>
/// P/Invoke over <c>refii_hostbridge</c>, the C ABI that hosts the recompiled guest in
/// Godot's own process: Godot renders, the guest is a simulation server behind a null D3D9 device.
/// </summary>
public static class HostBridge
{
	private const string Library = "refii_hostbridge";

	/// <summary>
	/// ABI this binding expects; bump together with the native `RefiiHostBridgeAbiVersion`.
	/// The check is exact and `GameSession` treats a mismatch as fatal before the guest initialises.
	/// </summary>
	public const int ExpectedAbiVersion = 119;

	private const int MessageCapacity = 512;

	static HostBridge() => Engine_NativeLibraries.Install();

	/// <summary>
	/// The bridge's version if it is usable, null otherwise (library missing, wrong ABI, or
	/// <see cref="MarkUnusable"/> latched). Guards use this; <see cref="TryGetRawAbiVersion"/> reports.
	/// </summary>
	public static int? TryGetAbiVersion() =>
		!Unusable &&
		TryGetRawAbiVersion() is { } version && version == ExpectedAbiVersion ? version : null;

	/// <summary>
	/// Why the bridge was given up on, or empty while it is usable. A matching ABI is not a
	/// running guest: a failed boot leaves the version answering correctly, so this latch exists.
	/// </summary>
	public static string UnusableReason { get; private set; } = string.Empty;

	private static bool Unusable => UnusableReason.Length != 0;

	/// <summary>
	/// Gives up on the bridge so every accessor guarded on <see cref="TryGetAbiVersion"/> refuses at
	/// once. Called by <c>GameSession</c> on an abandoned boot; cleared by a successful <see cref="Initialize"/>.
	/// </summary>
	public static void MarkUnusable(string reason)
	{
		if (Unusable)
		{
			return;
		}

		UnusableReason = reason.Length != 0 ? reason : "the session gave up on the guest";
		GD.PrintErr($"[host] the bridge is now REFUSING every read: {UnusableReason}. " +
			"Anything that polls the guest will behave as though there is none, which is the " +
			"honest answer.");
	}

	/// <summary>
	/// The loaded library's ABI version whatever it is, or null when it could not be loaded.
	/// For diagnostics only; guards use <see cref="TryGetAbiVersion"/>.
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
	/// Reserves the guest address space inside this process, reports where it landed, and releases it.
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
	/// Boots the guest on its own thread in this process and returns immediately. True means the
	/// thread started, not that the guest is up; poll <see cref="IsRunning"/> and <see cref="Status"/>.
	/// One boot per process.
	/// </summary>
	/// <param name="arguments">Passed to the guest, joined with newlines (an argument may contain spaces).</param>
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

	/// <summary>
	/// True while the guest thread is alive — the <see cref="Start"/> path only; false throughout a
	/// normal session. Accessors use <see cref="IsGuestUp"/>.
	/// </summary>
	public static bool IsRunning => RefiiHostIsRunning() == 1;

	/// <summary>
	/// True while a guest is up by either boot path; the test every guest-memory accessor uses.
	/// Falls back to <see cref="IsRunning"/> against an older native library.
	/// </summary>
	public static bool IsGuestUp
	{
		get
		{
			// Probed once: this guards per-entity accessors that run thousands of times a frame,
			// and against an older library the P/Invoke throws.
			if (_hasGuestUpProbe is null)
			{
				try
				{
					_ = RefiiHostIsGuestUp();
					_hasGuestUpProbe = true;
				}
				catch (EntryPointNotFoundException)
				{
					_hasGuestUpProbe = false;
				}
			}

			return _hasGuestUpProbe == true ? RefiiHostIsGuestUp() == 1 : IsRunning;
		}
	}

	/// <summary>Whether the native library exports the liveness probe. Asked once — see above.</summary>
	private static bool? _hasGuestUpProbe;

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
	/// Initialises the guest on this thread and returns once it is up; Godot then drives it with
	/// <see cref="Step"/>. Synchronous, takes seconds, must run on the main thread (SDL).
	/// </summary>
	public static ProbeResult Initialize(string gameDirectory, params string[] arguments)
	{
		var message = new byte[MessageCapacity];
		string packed = arguments is { Length: > 0 } ? string.Join('\n', arguments) : string.Empty;

		try
		{
			// Before the guest comes up: paths are resolved once during its startup, and the
			// guest would otherwise read refii.toml beside the Godot binary.
			RefiiHostSetAppRoot(Refii.Core.Engine_AppRoot.Path);

			int ok = RefiiHostInitialize(gameDirectory, packed, message, (nuint)message.Length);
			if (ok == 1)
			{
				// Cleared on success only; a second failed boot must not un-refuse the first.
				UnusableReason = string.Empty;
			}

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
	/// Tears the guest down. Idempotent; call from <c>_ExitTree</c> so guest threads are not live at exit.
	/// </summary>
	/// <summary>One clip leaf of the guest's stated blend tree for a rig.</summary>
	/// <param name="Key">The animation TOC key (the guest's clip hash).</param>
	/// <param name="Time">The guest's current clip time, seconds, as of its last tick.</param>
	/// <param name="PreviousTime">The clip time at the tick before.</param>
	/// <param name="Weight">The product of the weights down the tree.</param>
	/// <param name="Overlay">
	/// From the controller's second list: composited per bone through <paramref name="Mask"/>
	/// onto the primaries' pose, never a primary itself.
	/// </param>
	/// <param name="Mask">The guest mask node above this leaf (0 = whole body); see GetAnimMask.</param>
	/// <param name="PriorityMask">The record's priority bitmask; an overlay plays only when it ANDs with the top primary's priority.</param>
	public readonly record struct GuestAnimLeaf(
		uint Key, float Time, float PreviousTime, float Weight, bool Loop, bool Additive,
		int Priority, int Track, uint Uid, float BlendIn, float BlendOut,
		bool Overlay, uint Mask, uint PriorityMask);

	private const int AnimLeafWords = 10;

	/// <summary>
	/// One overlay's per-bone weights, 0..255 by LIVE (guest) bone index, from the mask node
	/// the leaf names. Empty when the node is not a mask.
	/// </summary>
	/// <summary>One item an entity wears: its GDB record hash, and whether the wearer is female
	/// and whether the appearance component's open byte is set (the open-coat variants).</summary>
	public readonly record struct GuestWornItem(uint Hash, bool Female, bool Open);

	/// <summary>
	/// The items the entity wears, from its appearance-modifier component. Empty when the
	/// entity wears nothing or carries no such component.
	/// </summary>
	public static List<GuestWornItem> GetEntityWornItems(uint entity)
	{
		var result = new List<GuestWornItem>();
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return result;
		}

		var words = new uint[128];
		uint count;
		try
		{
			count = RefiiHostGetEntityWornItems(entity, words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return result;
		}

		for (uint i = 0; i < count && 2 * i + 1 < words.Length; i++)
		{
			uint flags = words[2 * i + 1];
			result.Add(new GuestWornItem(words[2 * i], (flags & 1) != 0, (flags & 2) != 0));
		}

		return result;
	}

	/// <summary>
	/// One post-fold aim or look op: the record it belongs to, which chain (0 aim, 1 look),
	/// the blend mode and weight, the record's weight, and one target rotation per chain bone.
	/// </summary>
	public readonly record struct GuestPostOp(uint RecordUid, int Kind, int Mode, float Weight,
		float RecordWeight, Quaternion[] Targets);

	/// <summary>The skeleton's aim and look chains plus every op the playing records carry.</summary>
	public readonly record struct GuestPostOps(bool AimOn, bool LookOn, int[] AimBones, int[] LookBones,
		List<GuestPostOp> Ops);

	/// <summary>
	/// The guest's post-fold aim and look records for the entity (`sub_822DC980` tail): per
	/// chain bone, a target rotation the folded pose is blended toward. Null without a skeleton.
	/// </summary>
	public static GuestPostOps? GetAnimPostOps(uint entity)
	{
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		var words = new uint[13 + 64 * 29];
		uint count;
		try
		{
			count = RefiiHostGetAnimPostOps(entity, words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		if (words[0] == 0 && words[1] == 0 && words[2] == 0 && words[7] == 0)
		{
			return null;
		}

		var aim = new int[5];
		var look = new int[6];
		for (int i = 0; i < 5; i++)
		{
			aim[i] = (int)words[2 + i];
		}

		for (int i = 0; i < 6; i++)
		{
			look[i] = (int)words[7 + i];
		}

		var ops = new List<GuestPostOp>();
		int at = 13;
		for (uint i = 0; i < count; i++)
		{
			if (at + 5 > words.Length)
			{
				break;
			}

			int kind = (int)words[at + 1];
			int bones = kind == 0 ? 5 : 6;
			if (at + 5 + 4 * bones > words.Length)
			{
				break;
			}

			var targets = new Quaternion[bones];
			for (int b = 0; b < bones; b++)
			{
				int q = at + 5 + 4 * b;
				targets[b] = new Quaternion(
					BitConverter.Int32BitsToSingle((int)words[q]),
					BitConverter.Int32BitsToSingle((int)words[q + 1]),
					BitConverter.Int32BitsToSingle((int)words[q + 2]),
					BitConverter.Int32BitsToSingle((int)words[q + 3]));
			}

			ops.Add(new GuestPostOp(words[at], kind, (int)words[at + 2],
				BitConverter.Int32BitsToSingle((int)words[at + 3]),
				BitConverter.Int32BitsToSingle((int)words[at + 4]), targets));
			at += 5 + 4 * bones;
		}

		return new GuestPostOps(words[0] != 0, words[1] != 0, aim, look, ops);
	}

	/// <summary>One built part of a composite character: its model hash, its cluster sort
	/// layer, whether it is a body part, and the cluster names it covers.</summary>
	public readonly record struct GuestPartHides(uint Hash, int Layer, bool Body, string[] Covers);

	/// <summary>
	/// The guest's per-part cluster sets and sort layers for a composite entity (component
	/// 17's built vector). A part hides the union of the sets of every other part with a
	/// strictly greater layer. Empty for a single-model entity.
	/// </summary>
	public static List<GuestPartHides> GetEntityPartHides(uint entity)
	{
		var result = new List<GuestPartHides>();
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return result;
		}

		var text = new byte[8192];
		uint count;
		try
		{
			count = RefiiHostGetEntityPartHides(entity, text, (uint)text.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return result;
		}

		if (count == 0)
		{
			return result;
		}

		int end = System.Array.IndexOf(text, (byte)0);
		string all = System.Text.Encoding.ASCII.GetString(text, 0, end < 0 ? text.Length : end);
		foreach (string line in all.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] fields = line.Split(' ');
			if (fields.Length < 3 ||
				!uint.TryParse(fields[0], System.Globalization.NumberStyles.HexNumber, null, out uint hash) ||
				!int.TryParse(fields[1], out int layer))
			{
				continue;
			}

			string[] covers = fields.Length >= 4 && fields[3].Length != 0
				? fields[3].Split(';', StringSplitOptions.RemoveEmptyEntries)
				: System.Array.Empty<string>();
			result.Add(new GuestPartHides(hash, layer, fields[2] == "1", covers));
		}

		return result;
	}

	/// <summary>One bone's skeletal morph: a translation offset and a scale, guest bone index.</summary>
	public readonly record struct GuestBoneMorph(int Bone, Vector3 Offset, Vector3 Scale);

	private const int BoneMorphWords = 7;

	/// <summary>
	/// The entity's combined skeletal morph record — what makes a fat villager fat in the rig.
	/// Empty for an entity without a mesh component or a record.
	/// </summary>
	public static List<GuestBoneMorph> GetBoneMorph(uint entity)
	{
		var result = new List<GuestBoneMorph>();
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return result;
		}

		var words = new uint[BoneMorphWords * 256];
		uint count;
		try
		{
			count = RefiiHostGetBoneMorph(entity, words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return result;
		}

		for (int i = 0; i < count; i++)
		{
			int at = i * BoneMorphWords;
			result.Add(new GuestBoneMorph(
				(int)words[at],
				new Vector3(BitConverter.Int32BitsToSingle((int)words[at + 1]),
					BitConverter.Int32BitsToSingle((int)words[at + 2]),
					BitConverter.Int32BitsToSingle((int)words[at + 3])),
				new Vector3(BitConverter.Int32BitsToSingle((int)words[at + 4]),
					BitConverter.Int32BitsToSingle((int)words[at + 5]),
					BitConverter.Int32BitsToSingle((int)words[at + 6]))));
		}

		return result;
	}

	public static byte[] GetAnimMask(uint node, out uint skeletonId)
	{
		skeletonId = 0;
		if (node == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return System.Array.Empty<byte>();
		}

		var bytes = new byte[MaxBones];
		uint count = RefiiHostGetAnimMask(node, bytes, (uint)bytes.Length, out skeletonId);
		if (count == 0)
		{
			return System.Array.Empty<byte>();
		}

		System.Array.Resize(ref bytes, (int)count);
		return bytes;
	}

	/// <summary>
	/// One attachment the guest has STATED: <paramref name="Child"/> hangs on
	/// <paramref name="Parent"/>'s dummy <paramref name="Dummy"/>.
	/// </summary>
	/// <param name="Index">The ordinal among equally-named dummies on the parent model.</param>
	/// <param name="Ticket">The attachment's id; it changes when the child is re-attached.</param>
	public readonly record struct GuestAttachment(
		uint Child, uint Parent, int Index, uint Ticket, string Dummy);

	/// <summary>How many attachments one read brings back. A busy street is well under this.</summary>
	private const int MaxAttachments = 512;

	private const int AttachmentWords = 4;

	private const int AttachmentNameBytes = 64;

	private static uint[]? _attachmentWords;

	private static byte[]? _attachmentNames;

	/// <summary>
	/// Every live attachment in the world (see RefiiHostGetAttachments). The guest places an
	/// attached entity from ITS OWN pose; the host reads the statement instead and hangs the
	/// child off its own bone.
	/// </summary>
	public static int GetAttachments(List<GuestAttachment> into)
	{
		into.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		_attachmentWords ??= new uint[MaxAttachments * AttachmentWords];
		_attachmentNames ??= new byte[MaxAttachments * AttachmentNameBytes];
		uint count = RefiiHostGetAttachments(
			_attachmentWords, MaxAttachments, _attachmentNames);

		for (uint i = 0; i < count; ++i)
		{
			int at = (int)(i * AttachmentWords);
			int nameAt = (int)(i * AttachmentNameBytes);
			int length = 0;
			while (length < AttachmentNameBytes && _attachmentNames[nameAt + length] != 0)
			{
				++length;
			}

			into.Add(new GuestAttachment(
				_attachmentWords[at],
				_attachmentWords[at + 1],
				(int)_attachmentWords[at + 2],
				_attachmentWords[at + 3],
				System.Text.Encoding.ASCII.GetString(_attachmentNames, nameAt, length)));
		}

		return into.Count;
	}

	private static uint[]? _animWords;

	/// <summary>
	/// The guest's stated animation for one rig: the clip leaves of its controller's blend
	/// tree (see RefiiHostGetEntityAnimState). Empty when the entity has no active controller.
	/// <paramref name="flags"/>: bit0 controller active, bit1 a procedural IK component is
	/// present, bit2 the tree is netplay-replicated and was not walked.
	/// </summary>
	public static int GetEntityAnimState(uint entity, List<GuestAnimLeaf> into, out uint flags)
	{
		into.Clear();
		flags = 0;
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		_animWords ??= new uint[AnimLeafWords * 64];
		uint leaves = RefiiHostGetEntityAnimState(entity, _animWords, (uint)_animWords.Length, out flags);
		for (int i = 0; i < leaves; ++i)
		{
			int at = i * AnimLeafWords;
			uint f = _animWords[at + 4];
			into.Add(new GuestAnimLeaf(
				_animWords[at],
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 1]),
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 2]),
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 3]),
				(f & 1) != 0, (f & 2) != 0, (int)((f >> 8) & 0xFF), (int)((f >> 16) & 0xFF),
				_animWords[at + 5],
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 6]),
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 7]),
				(f & 4) != 0,
				_animWords[at + 8],
				_animWords[at + 9]));
		}

		return (int)leaves;
	}

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
	/// Publishes one controller's state in the guest's units: X_INPUT_GAMEPAD_* button bits,
	/// triggers 0..255, sticks -32768..32767 with +Y up. Callers convert; see GameInput.
	/// </summary>
	public static void SetControllerState(
		int userIndex, bool connected, uint buttons, uint leftTrigger, uint rightTrigger,
		int thumbLx, int thumbLy, int thumbRx, int thumbRy) =>
		RefiiHostSetControllerState(
			(uint)userIndex, connected ? 1 : 0, buttons, leftTrigger, rightTrigger,
			thumbLx, thumbLy, thumbRx, thumbRy);

	/// <summary>
	/// Copies the current Bink frame as RGBA8. Pass null to probe the size; width/height are
	/// reported either way.
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
	/// Copies the .bik buffer the guest handed to BinkOpen (the startup logos). Movies opened by
	/// name return null here; see <see cref="GetRequestedVideoName"/>.
	/// </summary>
	public static byte[]? GetRequestedVideo(out uint generation) =>
		GetRequestedVideo(out generation, uint.MaxValue);

	/// <summary>
	/// The requested movie's bytes, or null without a copy when its generation is still
	/// <paramref name="unlessGeneration"/>. The guest keeps stating the last request after the
	/// movie ends, and copying a whole .bik out of guest memory every frame cost 25 ms and tens
	/// of megabytes of garbage per frame in the world (measured 2026-09-04).
	/// </summary>
	public static byte[]? GetRequestedVideo(out uint generation, uint unlessGeneration)
	{
		uint size = 0;
		uint gen = 0;
		if (RefiiHostGetRequestedVideo(null, 0, out size, out gen) == 0 || size == 0)
		{
			generation = gen;
			return null;
		}

		if (gen == unlessGeneration)
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
	/// The path the guest handed to BinkOpen when it opened a movie by name; empty when the
	/// request carried bytes. Generation is shared with <see cref="GetRequestedVideo"/>.
	/// </summary>
	public static string GetRequestedVideoName(out uint generation)
	{
		var buffer = new byte[512];
		uint length = RefiiHostGetRequestedVideoName(buffer, (uint)buffer.Length, out generation);
		return length == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(buffer, 0, (int)length);
	}

	/// <summary>
	/// Tells the guest the host's movie has finished. Returns the player address retired, or 0.
	/// </summary>
	public static uint FinishMovie() => RefiiHostFinishMovie();

	/// <summary>
	/// One of the guest's XAudio source voices. Layout mirrors <c>refii::vfs::VoiceRecord</c>
	/// (static_asserted natively).
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct GuestVoice
	{
		/// <summary>The guest's voice pointer. Unique only together with Generation.</summary>
		public uint Handle;

		/// <summary>Bumped per voice creation; the guest reuses handles.</summary>
		public uint Generation;

		public uint SampleRate;
		public uint Channels;

		/// <summary>Non-zero between the guest's Start and Stop.</summary>
		public uint Started;

		/// <summary>Set once the guest has destroyed the voice.</summary>
		public uint Retired;

		/// <summary>Linear amplitude, 1.0 unity.</summary>
		public float Volume;

		/// <summary>
		/// Frequency ratio (Godot's PitchScale). The guest states octaves; CSourceStream::SetPitch
		/// stores <c>powf(2.0f, arg)</c>, converted natively.
		/// </summary>
		public float PitchRatio;

		/// <summary>PCM bytes waiting to be drained for this voice.</summary>
		public uint QueuedBytes;

		/// <summary>Bumped per submit, so a consumer can spot buffers it missed.</summary>
		public uint SubmittedSeq;

		/// <summary>
		/// The guest's X3DAudio matrix (XAudioSetChannelVolumes) folded to a stereo balance.
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

	// [Out] is required: a non-blittable array marshals as an in-only copy and the native
	// writes are discarded without it.
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
	/// Takes up to <paramref name="maxFrames"/> mixed frames as interleaved stereo float. This is
	/// the guest's audio clock: each frame taken credits one more, so drain only at playback rate.
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
	/// Audio health. <paramref name="pushed"/> settles at 187.5/s when the clock is right;
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

		// The native side never reports a null handle, so a zeroed first record with a non-zero
		// count is a marshalling fault. Reported once.
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
	/// Drains one voice's PCM as interleaved 16-bit samples. A generation mismatch returns 0.
	/// </summary>
	public static uint TakeVoicePcm(uint handle, uint generation, byte[] outPcm) =>
		RefiiHostTakeVoicePcm(handle, generation, outPcm, (uint)outPcm.Length);

	/// <summary>True once the voice hooks have seen any voice.</summary>
	public static bool VoiceCaptureActive() => RefiiHostVoiceCaptureActive() != 0;

	/// <summary>
	/// Hands per-voice playback to Godot: the guest's mixed frame is discarded rather than
	/// queued, but its mixer keeps running and being credited (it remains the clock).
	/// </summary>
	public static void SetVoicePlaybackOwned(bool owned) =>
		RefiiHostSetVoicePlaybackOwned(owned ? 1 : 0);

	/// <summary>
	/// Every element's live attribute values (runtime setAttribute, not the authored .bgf), in
	/// <see cref="GetUiElements"/> walk order, one row per element, one column per id.
	/// Ids are sdbm hashes of attribute names: <c>Engine_GameHash.Sdbm</c>, mirroring sub_82189140.
	/// </summary>
	/// <param name="presentation">
	/// A presentation from <see cref="GetPresentations"/>, or 0 for the front end's active screen.
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

	/// <summary>
	/// How many entities <see cref="GetEntities"/> reads in one call. Sized above the biggest
	/// region: the native walk truncates silently, and `Engine_StaticTwins.Reconcile` treats
	/// absence as "no such entity".
	/// </summary>
	private const int MaxEntities = 32768;

	/// <summary>
	/// The last <see cref="GetEntities"/> filled its buffer, so the list may be a prefix; nothing
	/// may conclude from an entity's absence while this is set.
	/// </summary>
	public static bool EntitiesTruncated { get; private set; }

	private const int EntityStride = 12;

	/// <summary>How many distinct unrecognised placement vtables to bring back.</summary>
	private const uint MaxUnknownVtables = 32;

	private static uint[]? _entityWords;

	private static bool _reportedTruncation;

	/// <summary>A placement vtable the bridge does not know, and how many live entities carry it.</summary>
	public readonly record struct UnknownPlacement(uint Vtable, uint Entities);

	/// <summary>
	/// Every live entity's transform and model from guest memory. One pass, no guest code, safe
	/// per frame (a torn read is one bad frame). Entities with an unrecognised placement vtable
	/// are skipped and counted in <paramref name="unknown"/>; <paramref name="unknownVtables"/>
	/// lists them (explicit length: a zero vtable word is a real observation).
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

		EntitiesTruncated = count >= MaxEntities;
		if (EntitiesTruncated && !_reportedTruncation)
		{
			_reportedTruncation = true;
			GD.PrintErr($"[host] the entity walk filled its {MaxEntities}-entry buffer — the " +
				"list is a PREFIX of the guest's entities. Nothing may treat an entity's " +
				"absence from it as a statement that the guest does not have one.");
		}

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
	/// A live character's skeleton pose. <see cref="BonePose.Space"/> is the guest's own statement:
	/// 2 model, 1 local. The update writes local then converts in place, so mid-update samples are local.
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
		if (TryGetAbiVersion() is null || !IsGuestUp)
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
	/// Unlike the live pose this is LOCAL and the caller composes it through the parent chain.
	/// Returns a copy, not the shared buffer, since it is kept for the life of the skeleton.
	/// Cheap to ask and usually null: the bridge answers from one bit at `entity+0x24`, and
	/// nothing is allocated until the entity turns out to have a rig.
	/// </summary>
	public static BonePose? GetEntityRestPose(uint entity)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
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

		// Space 1 — local, unlike the live pose. Never torn: this is the SKELETON's array,
		// written once at load, not the component's.
		return new BonePose((int)bones, 1, false, transforms, parents);
	}

	/// <summary>
	/// The guest's own name for each bone of a character's rig, or null when it has none. The
	/// name is the ONLY key the two skeletons share: a `.mdl` numbers its bones differently from
	/// the guest's runtime rig, so an index means different things on the two sides. An empty
	/// entry is a real answer (an unnamed bone), and names are interned, so duplicates occur.
	/// </summary>
	public static string[]? GetEntityBoneNames(uint entity)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
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
	/// Bytes per name in the bone-name buffer, passed to the bridge rather than agreed with it.
	/// The longest name in the shipped rigs is 40 (`Carry_SheathRangedWeaponBack_DummyObject`).
	/// </summary>
	private const uint BoneNameStride = 64;

	private static byte[]? _boneNames;

	/// <summary>
	/// The component ids one entity carries, in the order its own vector holds them. Whether an
	/// entity carries <c>CECGraphicAppearanceMorph</c> (id 17) — the parts list a character with
	/// a zero model hash is drawn from, see <see cref="GetEntityMorphParts"/> — is a live-memory
	/// question no decompile settles. The pointer half is dropped deliberately: existence is the
	/// question, and an address invites reading through an unverified layout.
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
	/// The locomotion state a character is in, as the guest itself names it.
	/// <see cref="State"/> is empty when the entity carries no locomotion mode, which most do
	/// not.
	/// </summary>
	public readonly record struct GuestLocomotion(
		uint Machine, uint StateAddress, uint ModeType, float Dwell,
		uint Stance, uint CombatState, uint Confinement, string State)
	{
		/// <summary>True when the guest is running a locomotion machine on this entity.</summary>
		public bool IsPresent => Machine != 0;

		/// <summary>Walk (2), strafe (3) or aiming (4), from `GetModeType()`.</summary>
		public bool IsStrafing => ModeType == 3;

		public bool IsAiming => ModeType == 4;
	}

	private const int LocomotionWords = 7;

	/// <summary>Longest state name the shipped script defines is 47 characters.</summary>
	private const int LocomotionNameCapacity = 128;

	private static uint[]? _locomotionWords;

	private static byte[]? _locomotionName;

	/// <summary>
	/// What the guest's own locomotion machine has selected for this entity — a read of its
	/// decision, not a re-derivation. The machine is an entity mode (component 21, whose
	/// `GetModeType()` is 2/3/4) holding its current state as a named object. The velocity and
	/// control magnitudes it tests are NOT available: the guest builds them into a stack
	/// temporary each tick and nothing resident holds them.
	/// </summary>
	public static GuestLocomotion GetEntityLocomotion(uint entity)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		_locomotionWords ??= new uint[LocomotionWords];
		_locomotionName ??= new byte[LocomotionNameCapacity];

		uint written = RefiiHostGetEntityLocomotion(
			entity, _locomotionWords, LocomotionWords,
			_locomotionName, LocomotionNameCapacity);

		if (written < LocomotionWords)
		{
			return default;
		}

		uint[] w = _locomotionWords;

		int length = System.Array.IndexOf(_locomotionName, (byte)0);
		string state = length <= 0
			? string.Empty
			: System.Text.Encoding.ASCII.GetString(_locomotionName, 0, length);

		return new GuestLocomotion(
			w[0], w[1], w[2],
			System.BitConverter.Int32BitsToSingle(unchecked((int)w[3])),
			w[4], w[5], w[6], state);
	}

	/// <summary>
	/// Component pairs read per entity. The class registry in `sub_826399A8` holds 264 classes,
	/// so no entity can carry more than that and 128 is far past anything real.
	/// </summary>
	private const uint MaxComponents = 128;

	private static uint[]? _componentPairs;

	public static PauseState? GetPauseState()
	{
		if (TryGetAbiVersion() is null ||
			RefiiHostGetPauseState(out uint screen, out uint modal, out uint mode, out uint tab) == 0)
		{
			return null;
		}

		return new PauseState(screen, modal, mode, tab);
	}

	/// <summary>
	/// Selects a pause tab through the guest's own lever — a pure write its GUI tick polls,
	/// after which the guest pushes the screen itself (`sub_826B89E0`).
	/// </summary>
	/// <remarks>
	/// Tabs: 1 root, 2 map, 3 abilities, 4 items, 5 weapons, 6 clothing, 7 logbook, 8 options.
	/// The bridge refuses outside the pause root or under a modal. The guest's push does not
	/// re-check the unlock flags, so a ghosted entry must never reach this call.
	/// </remarks>
	public static bool PauseTabSelect(uint tab) =>
		TryGetAbiVersion() is not null && RefiiHostPauseTabSelect(tab) != 0;

	public static MenuBoxState? GetMenuBox()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		uint count = RefiiHostGetMenuBox(out uint selected, out uint flags, out uint enabled);
		return count == 0
			? null
			: new MenuBoxState((int)count, (int)selected, (flags & 1) != 0, (flags & 2) != 0, enabled);
	}

	/// <summary>All seven tab bits — what <see cref="PauseTabsAvailable"/> means by "everything".</summary>
	public const uint AllPauseTabs = 0x1FCu;

	/// <summary>
	/// Which pause tabs the guest itself would offer — bit N set means tab N (2..8) is
	/// available. The guest's own prev/next skips an unavailable tab (`sub_826B83D0`) but its
	/// push does NOT re-check, so the owned menu must ghost these entries itself. Fails open
	/// to all-available; unavailability only ever comes from a completed read of the guest's
	/// own restriction flags (a childhood/cutscene gate on the hero entity).
	/// </summary>
	public static uint PauseTabsAvailable() =>
		TryGetAbiVersion() is null ? AllPauseTabs : RefiiHostPauseTabsAvailable();

	/// <summary>
	/// True while the abilities screen's spell-assignment blade is up — the guest's own mode
	/// byte at `screen14+620`, whose only writers are the enter/exit pair. False on the normal
	/// list AND when the screen object does not exist; the caller needs no distinction, both
	/// mean "drive the list, not the blade".
	/// </summary>
	public static bool AbilitiesAssignmentOpen() =>
		TryGetAbiVersion() is not null && RefiiHostGetAbilitiesMode() == 1;

	/// <summary>
	/// The pause-root minimap's quest markers — the C++ container the element mirror could
	/// never see. At most three; hero and dog are ordinary elements and never appear here.
	/// Valid only while the pause root is up (the guest empties the tree on deactivate).
	/// </summary>
	public static System.Collections.Generic.List<MapMarker> GetMapMarkers()
	{
		var markers = new System.Collections.Generic.List<MapMarker>();
		if (TryGetAbiVersion() is null)
		{
			return markers;
		}

		var keys = new uint[8];
		var values = new float[80];
		uint count = RefiiHostGetMapMarkers(keys, values, 8);
		for (uint i = 0; i < count; ++i)
		{
			float uMin = float.MaxValue;
			float vMin = float.MaxValue;
			float uMax = float.MinValue;
			float vMax = float.MinValue;
			for (uint corner = 0; corner < 4; ++corner)
			{
				float u = values[(i * 10) + 2 + (corner * 2)];
				float v = values[(i * 10) + 3 + (corner * 2)];
				uMin = System.Math.Min(uMin, u);
				vMin = System.Math.Min(vMin, v);
				uMax = System.Math.Max(uMax, u);
				vMax = System.Math.Max(vMax, v);
			}

			markers.Add(new MapMarker(
				keys[i], values[i * 10], values[(i * 10) + 1], uMin, vMin, uMax, vMax));
		}

		return markers;
	}

	/// <summary>
	/// The subtitles setting the guest last stated: true/false, or null before the first
	/// sample. Sampled by the tick pump through the guest's own reader while the front end
	/// is up, so the first-boot default (non-en-uk forces subtitles on) is the guest's rule,
	/// not ours.
	/// </summary>
	public static bool? GetSubtitles()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		return RefiiHostGetSubtitles() switch { 1 => true, 0 => false, _ => null };
	}

	/// <summary>
	/// The UIDs the guest holds in its DeadEntities set — placements it will not respawn.
	/// </summary>
	/// <remarks>
	/// The same set the guest consults before spawning a placement, and persists to the save.
	/// Never cache it: deactivating a layer purges that layer's entries, and an entry vanishing
	/// means those props legitimately come back.
	/// </remarks>
	public static ulong[] GetDeadEntities()
	{
		if (TryGetAbiVersion() is null)
		{
			return System.Array.Empty<ulong>();
		}

		_deadUids ??= new ulong[MaxDeadEntities];
		uint written = RefiiHostGetDeadEntities(_deadUids, MaxDeadEntities);
		if (written == 0)
		{
			return System.Array.Empty<ulong>();
		}

		var uids = new ulong[written];
		System.Array.Copy(_deadUids, uids, written);
		return uids;
	}

	private const uint MaxDeadEntities = 4096;

	private static ulong[]? _deadUids;

	/// <summary>
	/// The meshes a multi-mesh appearance component (id 5) draws, or empty.
	/// </summary>
	/// <remarks>
	/// `GetEntities` reports ONE model hash, from the single-slot appearance components;
	/// component 5 keeps a vector of mesh records instead, so an entity using one reports no
	/// model there. Buildings are the case in point — `… interior` / `… exterior` are two
	/// records of one `MeshRecords` list. The hashes are lower-cased resource-name hashes,
	/// identical in kind to `ModelFile`.
	/// </remarks>
	public static uint[] GetEntityMeshParts(uint entity, out int totalParts)
	{
		totalParts = 0;
		if (TryGetAbiVersion() is null)
		{
			return System.Array.Empty<uint>();
		}

		_meshParts ??= new uint[MaxMorphParts];
		uint written = RefiiHostGetEntityMeshParts(
			entity, _meshParts, MaxMorphParts, out uint total);
		totalParts = (int)total;

		if (written == 0)
		{
			return System.Array.Empty<uint>();
		}

		var hashes = new uint[written];
		System.Array.Copy(_meshParts, hashes, written);
		return hashes;
	}

	/// <summary>
	/// The model hashes a composite character is assembled from, or empty for anything else.
	/// A character whose <c>CECGraphicAppearanceAnimatedMesh</c> states a model hash of zero is
	/// drawn from a LIST on <c>CECGraphicAppearanceMorph</c> (component 17) instead. Returns
	/// that component's BUILT list, the only one carrying WORN CLOTHING — garments are appended
	/// to it from CECAppearanceModifierManager (component 15) and never appear in the source
	/// list. <paramref name="builtParts"/> is that list's length.
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
	private static uint[]? _meshParts;

	private static uint[]? _frontEndModelWords;

	/// <summary>
	/// The front-end model, or null when the front-end GUI is not up — including once the game
	/// starts, where the guest republishes its GUI global as a base-only instance with no
	/// screens.
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
		/// True when this is the engine's "no camera" default rather than a live one. There is
		/// no validity bit; the tell is the exact signature the guest writes when it has no
		/// camera — position zero, up +Z, forward +Y, 1.2217305 rad (70°).
		/// </summary>
		public bool IsDefault =>
			Position == Vector3.Zero && Up == new Vector3(0f, 0f, 1f) &&
			Forward == new Vector3(0f, 1f, 0f) && Mathf.IsEqualApprox(HorizontalFov, 1.2217305f);

		/// <summary>
		/// The VERTICAL field of view in degrees, for Godot. The guest stores a HORIZONTAL fov,
		/// and both of its aspect branches collapse to the same vertical angle, so widescreen
		/// widens horizontally only and this needs no aspect input.
		/// </summary>
		public float VerticalFovDegrees =>
			Mathf.RadToDeg(2f * Mathf.Atan(0.75f * Mathf.Tan(HorizontalFov * 0.5f)));
	}

	/// <summary>
	/// The guest's camera, or null when it could not be read coherently. The snapshot is five
	/// unsynchronised stores with no sequence counter, so the bridge reads it twice and requires
	/// agreement rather than returning a torn camera.
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
	/// The guest's view of the movie in progress. Either side can end one: the host by running
	/// out of frames, the guest by finishing its own decode or by skipping.
	/// </summary>
	public static MoviePlayback MovieState() => (MoviePlayback)RefiiHostMovieState();

	/// <summary>
	/// The 3D object the current in-game menu panel shows, as the guest states it. Null when
	/// no screen has one spawned.
	/// </summary>
	/// <remarks>
	/// `ScreenId` names which panel it belongs to (8 receive-items, 15 items/weapons, 22 shop,
	/// 25 gift, 26 selection, 27 decoration) — and therefore which anchor element to hang it
	/// on. The model hash is the same currency the model index keys on.
	/// </remarks>
	public readonly record struct UiPreviewObject(
		uint ScreenId, uint ModelHash, uint Entity,
		Vector3 Translation, Vector3 Rotation, float Scale, int RotationAxis);

	public static UiPreviewObject? GetUiPreviewObject()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		var values = new float[8];
		uint screen = RefiiHostGetUiPreviewObject(
			out uint hash, out uint entity, values, (uint)values.Length);
		if (screen == 0 || hash == 0)
		{
			return null;
		}

		return new UiPreviewObject(
			screen, hash, entity,
			new Vector3(values[0], values[1], values[2]),
			new Vector3(values[3], values[4], values[5]),
			values[6], (int)values[7]);
	}

	/// <summary>
	/// The world-icon queue and its two gates — the channel property income travels on.
	/// </summary>
	/// <remarks>
	/// `Enabled` is the guest's master gate (`WorldIconManager+48`), armed only when its HUD
	/// load binds 16 elements through binding registry 7; `IncomeIcons` is the hero's
	/// `CECMoney.ShowIncomeIcon`, without which the income payout never labels its icon as
	/// income. An always-empty queue means one of the two is dark.
	/// </remarks>
	public readonly record struct WorldIcons(
		bool Enabled, bool IncomeIcons, int Queued, uint[] Types, uint[] Entities,
		int Lit, uint Singles)
	{
		/// <summary>
		/// How many icons the guest currently has on screen, through its own elements:
		/// `sub_82825618` claims one of 16 HUD elements bound from binding registry 7 and
		/// `sub_82824D60` writes its projected screen position every frame, so a lit icon is
		/// one the HUD mirror should already carry.
		/// </summary>
		public int Showing => Lit +
			((Singles & 1) != 0 ? 1 : 0) +
			((Singles & 2) != 0 ? 1 : 0) +
			((Singles & 4) != 0 ? 1 : 0);
	}

	/// <summary>
	/// One world-space icon the guest would have drawn through its renderer — property sign,
	/// income, gift gold, opinion splash, quest marker, dog point. `Anchor` is in the guest's
	/// world space; `Colour` carries the fade; `Scale` is the guest's own tween.
	/// </summary>
	/// <param name="Pooled">A pooled HUD-element icon (a prompt), not a renderer sprite: the HUD
	/// element draws it; only the world point is taken, into <c>WorldIcon{Slot+1}</c>.</param>
	public readonly record struct WorldSprite(
		uint Type, uint TextureHash, Color Colour, uint Entity, bool Persistent, bool Fading,
		Vector3 Anchor, float Scale, float SecondsLeft, float Age, string Title, string Opinion,
		bool Pooled = false, int Slot = -1);

	private const int SpriteWords = 7;
	private static readonly uint[] _spriteWords = new uint[64 * SpriteWords];
	private static readonly float[] _spriteValues = new float[64 * 6];
	private static readonly byte[] _spriteText = new byte[8192];

	public static List<WorldSprite> GetWorldSprites()
	{
		var sprites = new List<WorldSprite>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return sprites;
		}

		uint count;
		try
		{
			count = RefiiHostGetWorldSprites(
				_spriteWords, (uint)_spriteWords.Length, _spriteValues, (uint)_spriteValues.Length,
				_spriteText, (uint)_spriteText.Length, 64u);
		}
		catch (EntryPointNotFoundException)
		{
			return sprites;
		}

		int textAt = 0;
		string Next()
		{
			int start = textAt;
			while (textAt < _spriteText.Length && _spriteText[textAt] != 0)
			{
				++textAt;
			}

			string value = System.Text.Encoding.UTF8.GetString(_spriteText, start, textAt - start);
			if (textAt < _spriteText.Length)
			{
				++textAt;
			}

			return value;
		}

		for (int i = 0; i < count; ++i)
		{
			int w = i * SpriteWords;
			uint rgba = _spriteWords[w + 2];
			var colour = new Color(
				((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f,
				((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);
			string title = Next();
			string opinion = Next();
			uint flags = _spriteWords[w + 5];
			sprites.Add(new WorldSprite(
				_spriteWords[w], _spriteWords[w + 1], colour, _spriteWords[w + 3],
				_spriteWords[w + 4] != 0, (flags & 1) != 0,
				new Vector3(_spriteValues[i * 6], _spriteValues[i * 6 + 1], _spriteValues[i * 6 + 2]),
				_spriteValues[i * 6 + 3], _spriteValues[i * 6 + 4], _spriteValues[i * 6 + 5],
				title, opinion,
				Pooled: (flags & 2) != 0,
				Slot: _spriteWords[w + 6] == 0xFFFFFFFFu ? -1 : (int)_spriteWords[w + 6]));
		}

		return sprites;
	}

	public static WorldIcons? GetWorldIcons(int max = 16)
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		var types = new uint[max];
		var entities = new uint[max];
		uint count = RefiiHostGetWorldIcons(out uint flags, types, entities, (uint)max);
		return new WorldIcons((flags & 1) != 0, (flags & 2) != 0, (int)count, types, entities,
			(int)((flags >> 8) & 0xFF), (flags >> 16) & 0x7);
	}

	/// <summary>
	/// Where the guest wants the live movie drawn, in its own 2D screen space — the rect it
	/// states right after every open and builds its own quad from. Null when no movie is live.
	/// </summary>
	/// <remarks>
	/// The fullscreen-vs-embedded answer, stated at OPEN: the fullscreen owners state the screen
	/// rect, the UI sprite owner states its element's.
	/// </remarks>
	public static Rect2? GetMovieRect()
	{
		if (TryGetAbiVersion() is null ||
			RefiiHostGetMovieRect(out float x0, out float y0, out float x1, out float y1) == 0)
		{
			return null;
		}

		return new Rect2(x0, y0, x1 - x0, y1 - y0);
	}

	/// <summary>
	/// The guest's front-end (title/main-menu) state object, or 0 while it is still
	/// booting. The guest builds it only once every startup logo has played and its bank
	/// mounter has finished, so a non-zero value is the guest saying it has reached the
	/// front end — rather than the host inferring it from a movie ending.
	/// </summary>
	public static uint FrontEndState() => RefiiHostGetFrontEndState();

	/// <summary>
	/// Which of the three ways the guest lays its loading art out. A normal level load packs the
	/// 1280x720 image into a square texture in two pieces with the right-hand strip stored
	/// ROTATED; a fixed-art screen is one plain full-bleed quad.
	/// </summary>
	/// <param name="FixedArt">`screen+315` — one quad, plain UVs (inset on 4:3).</param>
	/// <param name="QuitFade">`screen+310` — the quit-without-save fade; one quad, plain UVs.</param>
	/// <param name="Narrow">`screen+320` — the 4:3 inset variant.</param>
	public readonly record struct LoadingLayout(bool FixedArt, bool QuitFade, bool Narrow)
	{
		/// <summary>
		/// True when the art is the two-piece packing that has to be reassembled. A default
		/// rather than a case, matching `sub_8235FDC0`, which falls through to the two-quad
		/// path whenever neither flag is set.
		/// </summary>
		public bool IsSplit => !FixedArt && !QuitFade;
	}

	/// <summary>
	/// The loading screen's TEXT and its spinner clock.
	/// </summary>
	/// <param name="TipKey">
	/// The Babel key of the tip currently up, or 0 when the list is empty. It is a KEY, not a
	/// string: the guest keeps a list of tag hashes and localises on the fly, so the host
	/// resolves it through its own Babel database rather than calling into the guest.
	/// </param>
	/// <param name="TipIndex">Which tip, and <paramref name="TipCount"/> how many there are.</param>
	/// <param name="SpinnerFrame">
	/// The screen's frame counter. The spinner is `counter % 25` into a 5x5 grid — a timed
	/// flipbook, NOT load progress. The game has no progress bar at all.
	/// </param>
	/// <param name="TipsEnabled">`screen+313`.</param>
	/// <param name="BannerEnabled">`screen+314`.</param>
	/// <param name="LevelName">The level the banner names.</param>
	/// <param name="Overlay">Host-set overlay text, usually empty.</param>
	public readonly record struct LoadingText(
		uint TipKey, int TipIndex, int TipCount, uint SpinnerFrame,
		bool TipsEnabled, bool BannerEnabled, string LevelName, string Overlay);

	/// <summary>Reads the loading screen's text state, or null when there is no screen.</summary>
	public static LoadingText? GetLoadingScreenText()
	{
		uint[] words = new uint[6];
		byte[] buffer = new byte[512];
		try
		{
			if (RefiiHostGetLoadingScreenText(words, buffer, (uint)buffer.Length) == 0)
			{
				return null;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		string level = ReadUtf8(buffer, 0, out int next);
		string overlay = ReadUtf8(buffer, next, out _);

		return new LoadingText(words[2], (int)words[0], (int)words[1], words[3],
			words[4] != 0, words[5] != 0, level, overlay);
	}

	/// <summary>Reads one NUL-terminated UTF-8 string out of a packed buffer.</summary>
	private static string ReadUtf8(byte[] buffer, int start, out int next)
	{
		if (start < 0 || start >= buffer.Length)
		{
			next = buffer.Length;
			return string.Empty;
		}

		int end = start;
		while (end < buffer.Length && buffer[end] != 0)
		{
			++end;
		}

		next = end + 1;
		return end > start
			? System.Text.Encoding.UTF8.GetString(buffer, start, end - start)
			: string.Empty;
	}

	/// <summary>The loading screen's art layout, or null when no screen object exists.</summary>
	public static LoadingLayout? GetLoadingScreenLayout()
	{
		uint[] words = new uint[3];
		try
		{
			if (RefiiHostGetLoadingScreenLayout(words) == 0)
			{
				return null;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		return new LoadingLayout(words[0] != 0, words[1] != 0, words[2] != 0);
	}

	/// <summary>
	/// Why a presentation's texture hashes are what they are, broken down.
	/// <see cref="UiTextureStats.BackPointerMismatch"/> invalidates the rest: the scene node
	/// points back at its element, so a non-zero count means the walk is following a non-node.
	/// </summary>
	public readonly record struct UiTextureStats(
		int Elements, int WithNode, int WithQuad, int ImageNodes, int WithTexture,
		int BackPointerMismatch)
	{
		public string Describe() =>
			$"{Elements} elements, {WithNode} with a scene node, {WithQuad} with a render item, " +
			$"{ImageNodes} image nodes, {WithTexture} carrying a texture" +
			(BackPointerMismatch > 0
				? $" — {BackPointerMismatch} NODE BACK-POINTER MISMATCHES, the walk is wrong"
				: string.Empty);
	}

	/// <summary>Reads the texture-chain breakdown for one presentation, or null if unavailable.</summary>
	public static UiTextureStats? GetUiTextureStats(uint presentation)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		uint[] words = new uint[6];
		try
		{
			if (RefiiHostGetUiTextureStats(presentation, words) == 0)
			{
				return null;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		return new UiTextureStats((int)words[0], (int)words[1], (int)words[2],
			(int)words[3], (int)words[4], (int)words[5]);
	}

	/// <summary>
	/// Reads the screen manager whole: the current screen and every presentation it owns.
	/// Null when there is no screen manager, which is the case before the game starts.
	/// </summary>
	public static InGameScreens? GetInGameScreens()
	{
		uint[] words = new uint[36];
		if (RefiiHostGetInGameScreen(words) == 0 || words[1] != InGameGuiVtable)
		{
			return null;
		}

		var owned = new System.Collections.Generic.Dictionary<uint, int>();
		for (int id = 0; id < 30; ++id)
		{
			// A slot is null until that screen has been constructed, and most stay null for a
			// whole session — only the screens the player actually opens are ever built.
			uint presentation = words[4 + id];
			if (presentation != 0)
			{
				owned[presentation] = id;
			}
		}

		if (words[35] != 0)
		{
			owned[words[35]] = (int)words[34];
		}

		return new InGameScreens((InGameScreen)words[3], owned);
	}

	/// <summary>
	/// The BASE GUI's vtable — the construction the game uses once it is running. Written by
	/// the base ctor <c>sub_823710A8</c> at 0x8237111C and left in place by
	/// <c>Game_InitDatabases</c>, where the front end's derived ctor would overwrite it with
	/// <see cref="FrontEndGuiVtable"/>.
	/// </summary>
	private const uint InGameGuiVtable = 0x82001610u;

	/// <summary>
	/// The front-end GUI's vtable — the class whose slots include the GUI pump at 0x82379170.
	/// The word at the object's address says which of the two constructions built it.
	/// </summary>
	private const uint FrontEndGuiVtable = 0x820A924Cu;

	/// <summary>
	/// Whether the guest is in game, as the guest states it — <c>byte_83496A5E</c>, set by
	/// Game_InitDatabases (0x82311648) and cleared by the game mode's shutdown (0x82311860).
	/// Use this rather than inferring it from the front end disappearing.
	/// </summary>
	public static bool IsInGame => RefiiHostIsInGame() != 0;

	/// <summary>
	/// The guest's simulation tick — <c>dword_83319650</c>, incremented by the world update.
	/// Compare successive values for INEQUALITY only; the difference is not a count of ticks.
	/// </summary>
	public static uint SimTick => RefiiHostGetSimTick();

	/// <summary>
	/// Words per effect in <see cref="GetActiveFx"/>'s buffer. MUST match the native
	/// <c>kStride</c> — both sides index one flat buffer and a disagreement is an ABI break the
	/// compiler cannot see.
	/// </summary>
	public const int ActiveFxStride = 17;

	/// <summary>
	/// How many live effects are read in one frame. The guest's vector holds every particle
	/// NODE, not every effect, so one spell is several entries.
	/// </summary>
	private const uint MaxActiveFx = 512;

	private static uint[]? _activeFx;

	/// <summary>One live particle node as the guest currently has it.</summary>
	/// <param name="Instance">
	/// The guest pointer, and the only STABLE identity. The guest retires effects by swapping
	/// with the last element and popping, so a node's index changes when an unrelated effect
	/// ends — keying a host node on the index makes effects swap places with each other.
	/// </param>
	/// <param name="NameHash">
	/// FNV-1 of the node's lowercased name, which is the same key the particle bank uses.
	/// </param>
	/// <param name="Alive">The guest's own has-work predicate, <c>sub_8322EB88</c>.</param>
	/// <param name="Emitting">Still spawning, as opposed to running its last particles out.</param>
	/// <param name="Stale">
	/// The guest has written the node's transform but not yet recomposed its cached matrix.
	/// At most one tick old.
	/// </param>
	public readonly record struct ActiveFx(
		uint Instance,

		/// <summary>
		/// The EFFECT INSTANCE this node belongs to. One effect is many nodes, and they all share
		/// this — group on it or the whole effect is drawn once per node.
		/// </summary>
		uint Handle,
		uint NameHash,
		bool Alive,
		bool Emitting,
		bool Stale,
		uint LiveParticles,
		Vector3 Position,
		Vector3[] Rows);

	/// <summary>
	/// Every particle effect the guest is currently playing, in world space — the only route
	/// runtime effects have to the screen (level ambients come from the GDB). Safe per frame:
	/// the native side re-reads both ends of the guest's vector together and refuses a span
	/// that is not a whole number of elements.
	/// </summary>
	public static System.Collections.Generic.List<ActiveFx> GetActiveFx()
	{
		var effects = new System.Collections.Generic.List<ActiveFx>();
		if (TryGetAbiVersion() is null)
		{
			return effects;
		}

		_activeFx ??= new uint[MaxActiveFx * ActiveFxStride];
		uint written = RefiiHostGetActiveFx(_activeFx, MaxActiveFx);
		if (written == 0)
		{
			return effects;
		}

		for (uint i = 0; i < written; i++)
		{
			int at = (int)i * ActiveFxStride;
			uint flags = _activeFx[at + 2];

			// Raw guest axes: a node standing BESIDE the region needs `(-x, z, y)` and one built
			// INSIDE it needs `(x, z, y)`, so the consumer applies its own map.
			float bx = Bits(_activeFx[at + 4]);
			float by = Bits(_activeFx[at + 5]);
			float bz = Bits(_activeFx[at + 6]);

			effects.Add(new ActiveFx(
				_activeFx[at + 0],
				_activeFx[at + 16],
				_activeFx[at + 1],
				(flags & 1u) != 0,
				(flags & 4u) != 0,
				(flags & 2u) != 0,
				_activeFx[at + 3],
				new Vector3(bx, by, bz),
				GuestRows(_activeFx, at + 7)));
		}

		return effects;
	}

	private static float Bits(uint raw) => BitConverter.Int32BitsToSingle(unchecked((int)raw));

	/// <summary>
	/// Rows 0..2 of the guest's world matrix, normalised, in GUEST axes. The rows carry the
	/// node's scale (<c>sub_8222E678</c> pre-multiplies each by its scale lane), hence the
	/// normalise. Row 0 is the image of the guest's local X, row 1 of Y, row 2 of Z; converting
	/// to Godot's axes is the caller's job.
	/// </summary>
	private static Vector3[] GuestRows(uint[] words, int at)
	{
		var rows = new Vector3[3];
		for (int r = 0; r < 3; r++)
		{
			var row = new Vector3(
				Bits(words[at + (r * 3) + 0]),
				Bits(words[at + (r * 3) + 1]),
				Bits(words[at + (r * 3) + 2]));
			rows[r] = row.LengthSquared() > 1e-8f ? row.Normalized() : Vector3.Zero;
		}

		return rows;
	}

	/// <summary>The guest's live time of day, or null while the world clock does not exist.</summary>
	/// <remarks>
	/// Null must not be substituted with a default: `GameTimeManager` is built at game-database
	/// init and destroyed with the world, so before a level exists there is no clock and callers
	/// hold their last good value. The fraction is in [0, 1) and is the same value the guest's
	/// own day-set selector blends on.
	/// </remarks>
	public static GuestClock? TimeOfDay()
	{
		try
		{
			// IsGuestUp, NOT IsRunning. IsRunning reports false for the whole of in-game play
			// (the debug overlay prints "guest running: False" beside "inGame=True"), so gating
			// on it made this return null every frame and the sky held its load-time sun for
			// the entire session. The native reader validates its own chain by vtable, so an
			// unbuilt world still answers 0.
			if (!IsGuestUp)
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
	/// The guest's own pause: non-zero means something holds a stop-time request and the clock
	/// is not advancing.
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
		/// Which producer is holding the flag, in words. The flag itself carries no state — it
		/// is <c>byte_83496E20</c> re-copied every render-thread iteration — so "busy" alone
		/// names nothing.
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
	/// The loading screen's take-down handshake — see RefiiHostGetLoadingScreen. <c>Fade</c>
	/// counts 1.0 down to ~0 at 1/15 per render-thread tick; <c>Requested</c> is set by the game
	/// thread just before it blocks on an INFINITE wait; <c>Done</c> is latched by the render
	/// thread when it signals. Fade pinned at 1.0 with Requested and no Done is the stall.
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
	/// The level the guest is standing in. <see cref="Key"/> is the guest's FNV-1 hash of
	/// lowercase "world/level" at Level+0xB4 — one aligned dword, the value to compare frame to
	/// frame, changing on exactly the transitions that reload a level.
	/// </summary>
	public readonly record struct CurrentLevel(
		string World, string Level, string Entrance, uint Key);

	/// <summary>
	/// <c>GetCurrentLevel</c> — where the guest actually is, or null when no level is standing
	/// (also before the first load, and during a transition). Use this, not
	/// <see cref="GetStartLevel"/>, to notice a level change: the start globals are written only
	/// by game-START paths, while a door, a teleporter, fast travel and `World.LoadLevel` all
	/// put their destination on the CmdLevelChange command object and touch no global.
	/// </summary>
	/// <summary>
	/// A level change the guest has queued, as its own command describes it.
	/// <see cref="State"/> is the command's state word (1..9, never 0); the entity teardown
	/// happens at state 2, so state >= 2 is the moment a host may free its scene. When
	/// <see cref="FullLoad"/> is false the guest tears NOTHING down — it is a reposition inside
	/// the resident level — so freeing a scene on it rebuilds a region still loaded.
	/// </summary>
	public readonly record struct LevelChange(
		string World, string Level, string Entrance, uint State, uint TravelType, bool FullLoad);

	/// <summary>
	/// <c>GetLevelChange</c> — the level change in flight, or null when none is. Prefer it to
	/// the falling edge of <see cref="WorldIsReady"/> for deciding when to take a world down:
	/// that byte is not cleared until state 3, after the blocking load has returned, while the
	/// entities were freed back in state 2.
	/// </summary>
	/// <summary>
	/// Holds the guest in its own loading screen while Godot builds a region, by answering its
	/// renderer's "IsEnginePreLoading" from state 7 of the level-change command. MUST BE CLEARED
	/// on every path that leaves a build, success or failure, or the guest parks at state 7.
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
	/// Whether the guest's world is activated and populated — `World+0xA8`, set by the entity
	/// activator and cleared by the world teardown, and the one signal both load paths write
	/// (travel from level-change state 5, load-from-save from its own state 2). Prefer this to
	/// <see cref="WorldIsReady"/>, which boots as true and is only set at the terminal state,
	/// behind render-fence and loading-screen gates a headless host may never satisfy.
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
	/// The scenario the guest starts in, or null when it has not stated one yet — this is what
	/// picks the map. The guest builds
	/// <c>Data\Worlds\&lt;World&gt;\&lt;Level&gt;\&lt;Scenario&gt;\&lt;Scenario&gt;.list</c> and loads every
	/// heightfield it names, so the level name alone cannot choose between scenarios.
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
	/// FNV-1 hashes of the layers the guest has explicitly DEACTIVATED — worldMgr+72. This is
	/// the gate, NOT <see cref="GetActiveLayers"/>: `sub_823662C8` reads a container per the
	/// layer's `load` attribute — `AlwaysOn` consults neither list, `InitiallyOn` spawns iff
	/// ABSENT from +72, anything else spawns iff PRESENT in +60 — and the props build emits only
	/// the first two. Both lists are empty on a new game, so gating on the activated list would
	/// hide every `InitiallyOn` layer. Returns null, not empty, when the native library predates
	/// ABI 55; empty is a real answer meaning nothing is deactivated.
	/// </summary>
	public static uint[]? GetDeactivatedLayers()
	{
		if (_deactivatedLayersMissing)
		{
			return null;
		}

		var hashes = new uint[256];
		uint count;
		try
		{
			count = RefiiHostGetDeactivatedLayers(hashes, (uint)hashes.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_deactivatedLayersMissing = true;
			GD.PrintErr("[host] the native bridge predates ABI 55 and cannot report the " +
				"deactivated layers — the layer gate is OFF, so nothing will be hidden. " +
				"Rebuild the native library.");
			return null;
		}

		if (count == 0)
		{
			return System.Array.Empty<uint>();
		}

		System.Array.Resize(ref hashes, (int)System.Math.Min(count, (uint)hashes.Length));
		return hashes;
	}

	private static bool _deactivatedLayersMissing;

	/// <summary>
	/// FNV-1 hashes of the layers the guest has explicitly ACTIVATED — worldMgr+60. Diagnostic
	/// only; the gate is <see cref="GetDeactivatedLayers"/>. The hash is over the layer name's
	/// EXACT bytes: no lowercasing, no separator normalisation, unlike the world+level key.
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
	/// <c>PostFrontEndAction</c> — asks the guest to perform a front-end action by the NAME it
	/// dispatches on. Nothing happens on this thread: the name is queued and the guest picks it
	/// up inside its own tick. Returns the queue depth after posting; a climbing depth means the
	/// pump is holding the action because the front end is not up yet.
	/// </summary>
	/// <summary>
	/// Drains the <c>GUI.*</c> native calls the game VM has made since the last drain.
	/// </summary>
	/// <remarks>
	/// The UI cut's arrival point: the name the script used and the values it passed, before the
	/// guest's own implementation has done anything with them. Taking the call does not suppress
	/// it. The payload is NUL-separated fields plus a parallel kind array — one length byte per
	/// record, then one kind per argument. See docs/ui_native_cut.md.
	/// </remarks>
	public static System.Collections.Generic.List<Engine_UiModel.GuiNativeCall> TakeGuiNativeCalls()
	{
		var calls = new System.Collections.Generic.List<Engine_UiModel.GuiNativeCall>();
		if (TryGetAbiVersion() is null)
		{
			return calls;
		}

		byte[] text = new byte[64 * 1024];
		byte[] kinds = new byte[4096];
		uint count;
		try
		{
			count = RefiiHostTakeGuiNativeCalls(
				text, (uint)text.Length, kinds, (uint)kinds.Length, 256u);
		}
		catch (EntryPointNotFoundException)
		{
			return calls;
		}

		int at = 0;
		int kindAt = 0;
		string Next()
		{
			int end = System.Array.IndexOf(text, (byte)0, at);
			if (end < 0)
			{
				end = text.Length;
			}

			string value = Encoding.UTF8.GetString(text, at, end - at);
			at = end + 1;
			return value;
		}

		for (uint record = 0; record < count && kindAt < kinds.Length; ++record)
		{
			string name = Next();
			int arity = kinds[kindAt++];
			var arguments = new System.Collections.Generic.List<Engine_UiModel.GuiNativeArgument>(arity);
			for (int i = 0; i < arity && kindAt < kinds.Length; ++i)
			{
				var kind = (Engine_UiModel.GuiNativeArgumentKind)kinds[kindAt++];
				if (kind != Engine_UiModel.GuiNativeArgumentKind.Table)
				{
					arguments.Add(new Engine_UiModel.GuiNativeArgument(kind, Next()));
					continue;
				}

				// A table spends a count field, then name/kind/value per entry (ABI 84). Its
				// own text field is the count.
				string countText = Next();
				int fields = int.TryParse(countText,
					System.Globalization.NumberStyles.Integer,
					System.Globalization.CultureInfo.InvariantCulture, out int parsedFields)
					? parsedFields
					: 0;

				var table = new System.Collections.Generic.Dictionary<
					string, Engine_UiModel.GuiNativeArgument>(fields, System.StringComparer.Ordinal);
				for (int f = 0; f < fields && kindAt < kinds.Length; ++f)
				{
					string fieldName = Next();
					var fieldKind = (Engine_UiModel.GuiNativeArgumentKind)kinds[kindAt++];
					table[fieldName] = new Engine_UiModel.GuiNativeArgument(fieldKind, Next());
				}

				arguments.Add(new Engine_UiModel.GuiNativeArgument(kind, countText, table));
			}

			calls.Add(new Engine_UiModel.GuiNativeCall(name, arguments));
		}

		return calls;
	}

	/// <summary>
	/// How many `GUI` calls have been intercepted, how many wait, and how many names the
	/// registration hook resolved. <c>named</c> is 0 until `RegisterGUI` has run, which is what
	/// separates "intercepted nothing" from "never saw the namespace".
	/// </summary>
	public static (ulong Intercepted, uint Pending, uint Named) GuiNativeCounters()
	{
		if (TryGetAbiVersion() is null)
		{
			return (0UL, 0u, 0u);
		}

		try
		{
			RefiiHostGuiNativeCounters(out ulong intercepted, out uint pending, out uint named);
			return (intercepted, pending, named);
		}
		catch (EntryPointNotFoundException)
		{
			return (0UL, 0u, 0u);
		}
	}

	/// <summary>
	/// True while a menu population is in flight and the captured rows are not yet whole.
	/// </summary>
	/// <remarks>
	/// The host reads asynchronously and there is no end marker — the in-game lists never call
	/// `UpdateAfterSpawning` — so the window between `DeleteAllSpawnedMenuItems` and the last
	/// `SpawnButton` reads as an empty menu. Hold the last whole population while this is true.
	/// </remarks>
	public static bool MenuPopulating()
	{
		if (TryGetAbiVersion() is null)
		{
			return false;
		}

		try
		{
			return RefiiHostMenuPopulating() != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// Drains the `g_Menu:` chunk calls the game's C++ has made since the last drain.
	/// </summary>
	/// <remarks>
	/// The whole menu contract: the C++ stages attributes onto the `Parameters` element then
	/// runs a one-line Lua chunk that reads them back, so `SpawnButton`, `SetItemGhosting`,
	/// `SetItemEmphasis`, `RefreshButton`, `DeleteAllSpawnedMenuItems` and the rest all arrive
	/// here — including the channels written AFTER the spawn. Each record's presentation is part
	/// of its identity: a second `SetItemEmphasis`/`SetItemGhosting` with different semantics
	/// exists for the non-expandable menu.
	/// </remarks>
	/// <summary>
	/// What the `g_Menu:` capture has actually SEEN — recorded, still pending, and dropped to
	/// the ceiling. Nothing read this until a run showed zero statements crossing in 6,777 log
	/// lines, which is indistinguishable from "the guest states no menus" without it: recorded
	/// climbing means the hook fires and the crossing is at fault, recorded stuck at zero means
	/// the guest's populations never reach `sub_82426470` and the hook is on the wrong function.
	/// </summary>
	public static (ulong Recorded, uint Pending, ulong Dropped) MenuChunkCounters()
	{
		if (TryGetAbiVersion() is null)
		{
			return (0UL, 0u, 0UL);
		}

		RefiiHostMenuChunkCounters(out ulong recorded, out uint pending, out ulong dropped);
		return (recorded, pending, dropped);
	}

	public static System.Collections.Generic.List<Engine_UiModel.MenuChunk> TakeMenuChunks()
	{
		var chunks = new System.Collections.Generic.List<Engine_UiModel.MenuChunk>();
		if (TryGetAbiVersion() is null)
		{
			return chunks;
		}

		byte[] text = new byte[128 * 1024];
		byte[] kinds = new byte[8192];
		uint count;
		try
		{
			count = RefiiHostTakeMenuChunks(text, (uint)text.Length, kinds, (uint)kinds.Length, 1024u);
		}
		catch (EntryPointNotFoundException)
		{
			return chunks;
		}

		int at = 0;
		int kindAt = 0;
		string Next()
		{
			int end = System.Array.IndexOf(text, (byte)0, at);
			if (end < 0)
			{
				end = text.Length;
			}

			string value = Encoding.UTF8.GetString(text, at, end - at);
			at = end + 1;
			return value;
		}

		for (uint record = 0; record < count && kindAt < kinds.Length; ++record)
		{
			uint presentation = uint.TryParse(Next(),
				System.Globalization.NumberStyles.HexNumber,
				System.Globalization.CultureInfo.InvariantCulture, out uint parsed) ? parsed : 0u;
			string chunk = Next();
			int staged = kinds[kindAt++];

			var attributes = new System.Collections.Generic.Dictionary<string, string>(
				staged, System.StringComparer.Ordinal);
			for (int i = 0; i < staged && kindAt < kinds.Length; ++i)
			{
				++kindAt;
				string name = Next();
				attributes[name] = Next();
			}

			chunks.Add(new Engine_UiModel.MenuChunk(presentation, chunk, attributes));
		}

		return chunks;
	}

	/// <summary>
	/// Every element state change the guest wrote since the last drain — ABI 83.
	/// </summary>
	/// <remarks>
	/// Taken at the three writers, not from the guest's dirty list. `sub_82284798` is the one
	/// attribute writer — its only data reference is slot +0x48 of the attribute-service vtable,
	/// so authored, scripted, COMMAND_SETPROPERTY, text-layout and keyframe-interpolated values
	/// all arrive through it. `sub_82284BE8` carries the flags and `sub_82BB5760` the slide.
	/// </remarks>
	public static System.Collections.Generic.List<Engine_UiModel.UiElementChange>
		TakeUiElementChanges()
	{
		var changes = new System.Collections.Generic.List<Engine_UiModel.UiElementChange>();
		if (TryGetAbiVersion() is null)
		{
			return changes;
		}

		// Sized for a whole population: a screen opening writes every element it owns in one
		// guest frame. What the buffer cannot hold stays queued; the drop counter says whether
		// the host fell behind.
		const int Capacity = 16384;
		uint[] words = _elementWords ??= new uint[Capacity * 5];
		byte[] kinds = _elementKinds ??= new byte[Capacity];

		uint count;
		try
		{
			count = RefiiHostTakeUiElementChanges(
				words, (uint)words.Length, kinds, (uint)kinds.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return changes;
		}

		int usable = (int)Math.Min(count, (uint)Capacity);
		changes.Capacity = usable;
		for (int i = 0; i < usable; ++i)
		{
			changes.Add(new Engine_UiModel.UiElementChange(
				words[i * 5], words[i * 5 + 1], words[i * 5 + 2],
				words[i * 5 + 3], words[i * 5 + 4],
				(Engine_UiModel.UiElementChangeKind)kinds[i]));
		}

		return changes;
	}

	// Reused across frames: this drains every frame and the buffers are 80 KB together.
	private static uint[]? _elementWords;
	private static byte[]? _elementKinds;

	/// <summary>Recorded, pending and dropped element changes — ABI 83.</summary>
	public static (ulong Recorded, uint Pending, ulong Dropped) UiElementCounters()
	{
		if (TryGetAbiVersion() is null)
		{
			return (0ul, 0u, 0ul);
		}

		try
		{
			RefiiHostUiElementCounters(out ulong recorded, out uint pending, out ulong dropped);
			return (recorded, pending, dropped);
		}
		catch (EntryPointNotFoundException)
		{
			return (0ul, 0u, 0ul);
		}
	}

	/// <summary>
	/// Answers one `GUI` predicate from the host INSTEAD of the guest — ABI 85.
	/// </summary>
	/// <remarks>
	/// The suppression lever: the thunk writes the value onto the Lua stack and returns without
	/// calling through. Opt-in by name — fifteen of the twenty predicates read state the guest's
	/// own setters maintain, so answering one whose setters the host has not taken over replaces
	/// a correct answer with a guess.
	/// </remarks>
	public static void SetGuiPredicate(string name, bool value)
	{
		if (TryGetAbiVersion() is not null)
		{
			try
			{
				RefiiHostSetGuiPredicate(name, 1u, value ? 1u : 0u);
			}
			catch (EntryPointNotFoundException)
			{
			}
		}
	}

	/// <summary>Answers a float-returning predicate — `GetFillPercent` is the only one.</summary>
	public static void SetGuiPredicate(string name, float value)
	{
		if (TryGetAbiVersion() is not null)
		{
			try
			{
				RefiiHostSetGuiPredicate(name, 2u, BitConverter.SingleToUInt32Bits(value));
			}
			catch (EntryPointNotFoundException)
			{
			}
		}
	}

	/// <summary>
	/// Stops the guest running a native this host owns outright — ABI 85.
	/// </summary>
	/// <remarks>
	/// Only for a native whose side effects are inert. Owning the family is necessary and not
	/// sufficient: opening a display box pushes a GameFlow command that suspends gameplay, the
	/// minigame tally opens a screen, the golf meter plays sounds.
	/// </remarks>
	public static void SuppressGuiNative(string name)
	{
		if (TryGetAbiVersion() is not null)
		{
			try
			{
				RefiiHostSetGuiPredicate(name, 3u, 0u);
			}
			catch (EntryPointNotFoundException)
			{
			}
		}
	}

	/// <summary>Hands a predicate back to the guest.</summary>
	public static void ClearGuiPredicate(string name)
	{
		if (TryGetAbiVersion() is not null)
		{
			try
			{
				RefiiHostSetGuiPredicate(name, 0u, 0u);
			}
			catch (EntryPointNotFoundException)
			{
			}
		}
	}

	public static uint PostFrontEndAction(string name) =>
		RefiiHostPostFrontEndAction(name);

	/// <summary>The guest's achievement writes newer than <paramref name="afterSequence"/>, oldest first.</summary>
	public static AchievementUnlock[] GetAchievementUnlocks(ulong afterSequence, int max = 16)
	{
		if (TryGetAbiVersion() is null || max <= 0)
		{
			return System.Array.Empty<AchievementUnlock>();
		}

		var ids = new uint[max];
		var users = new uint[max];
		var sequences = new ulong[max];
		uint count = RefiiHostGetAchievementUnlocks(afterSequence, ids, users, sequences, (uint)max);
		if (count == 0)
		{
			return System.Array.Empty<AchievementUnlock>();
		}

		var unlocks = new AchievementUnlock[System.Math.Min(count, (uint)max)];
		for (int i = 0; i < unlocks.Length; ++i)
		{
			unlocks[i] = new AchievementUnlock(users[i], ids[i], sequences[i]);
		}

		return unlocks;
	}

	/// <summary>The title's XDBF resource — sized with a null buffer first — or null before the module has launched.</summary>
	public static byte[]? GetTitleXdbf()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		uint size = RefiiHostGetTitleXdbf(null, 0);
		if (size == 0)
		{
			return null;
		}

		var bytes = new byte[size];
		uint written = RefiiHostGetTitleXdbf(bytes, size);
		return written == 0 ? null : bytes;
	}

	/// <summary>States the unlocked set for the guest's own achievement enumerator.</summary>
	public static void SetAchievementUnlocks(uint[] ids, ulong[] unlockFiletimes)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		RefiiHostSetAchievementUnlocks(ids, unlockFiletimes, (uint)System.Math.Min(ids.Length, unlockFiletimes.Length));
	}

	/// <summary>How many times the guest has called `XamShowAchievementsUI` this session.</summary>
	public static uint GetAchievementsUiRequests() =>
		TryGetAbiVersion() is null ? 0u : RefiiHostGetAchievementsUiRequests();

	/// <summary>
	/// Presses or releases a direction on whichever in-game screen is up — an EDGE, not a step.
	/// The guest's own path (`sub_82404ED0` / `sub_82405030`) sets the presentation's `String1`
	/// attribute to the direction and runs `GUI:SetDirectionToHeld()` /
	/// `GUI:SetDirectionToReleased()`; Lua's `g_Menu:onUpdate` polls that latch and calls
	/// `ScrollUp` itself, so the repeat cadence and `UpDownInputDisabled` stay the original's.
	/// Because it is a latch, both edges must be sent: a press with no release leaves the guest
	/// scrolling for ever, including after the menu closes.
	/// </summary>
	public static uint MenuScroll(MenuStick direction, bool held) =>
		PostFrontEndAction($"MenuScroll:{direction.ToString().ToLowerInvariant()}" +
			(held ? string.Empty : ":released"));

	/// <summary>
	/// Feeds one action code to the current in-game screen's own <c>vtable+24</c> handler.
	///
	/// 36 select (the uniform <c>sub_826D58F0</c> "OnPressA"), 37 back, 39 the per-screen Y
	/// action, 59 inspect, 72/73 tab prev/next. The pump passes player index 1; see
	/// <see cref="MenuScroll"/> for why the highlight has to be moved through the guest too.
	/// </summary>
	/// <param name="atRoot">
	/// The host's answer to `g_Menu:IsHighlightedItemAtRoot()`, carried with the press. Only
	/// code 37 reads it, and it must be supplied: `sub_826C1D98` posts the chunk that would
	/// compute it and reads `Float1` back ten instructions later, so the guest always sees
	/// whatever last wrote that slot.
	/// </param>
	public static uint ScreenAction(uint code, bool atRoot = false) =>
		PostFrontEndAction($"ScreenAction:{code}{(atRoot ? ":root" : string.Empty)}");

	/// <summary>
	/// Commits a menu row BY IDENTITY, the way the shipped script does: `g_Menu:SelectMenuItem`
	/// sends `GUI:PushButtonEvent(item.Name, item.EventType, item.IDLow, item.IDHigh)` — never
	/// an index — and `PushButtonEvent` is Lua, pushing onto four parallel arrays the guest's
	/// C++ drain (`sub_82370450`) pops one per tick into the current screen's `vtable+20`.
	/// Naming the row is what takes the cursor off the guest, since every list screen otherwise
	/// reads `g_Menu:GetHighlightedMenuItemData()`.
	/// <paramref name="id"/> is the FULL 32-bit row id — the halves the guest split into
	/// `Float1`/`Float2` at spawn, recombined here and split again on the way out.
	/// A ghosted row is the caller's to refuse: the script's early return is in `SelectMenuItem`,
	/// which this bypasses.
	/// </summary>
	public static uint MenuEvent(string name, int eventType, uint id) =>
		PostFrontEndAction($"MenuEvent:{name}:{eventType}:{id}");

	/// <summary>
	/// The shipped commit — `g_Menu:SelectMenuItem()` on the guest's own tick thread. It is an
	/// event AND a toggle, and the toggle is what <see cref="MenuEvent"/> cannot do: only the
	/// guest's Lua menu spawns a category's child rows, off the expand `SelectMenuItem` fires
	/// (a world-map category is event type 301, which the screen's C++ handler has no case for).
	/// Acts on the GUEST's cursor, so use it only while that cursor is the row meant.
	/// </summary>
	public static uint MenuSelect() => PostFrontEndAction("MenuSelect");

	/// <summary>
	/// Every row the guest has spawned into the current list, in spawn order: the identity —
	/// name, parent link and the `(EventType, id)` pair a commit is made of — which lives only
	/// in a Lua table and is captured at `sub_826CE8D0` on its way there. The labels come off
	/// the element tree instead. <paramref name="generation"/> is bumped on every spawn, so an
	/// unchanged value means the same list.
	/// </summary>
	/// <summary>
	/// The screen the captured rows belong to, or `Engine_UiMenuSources.AnyScreen` when none —
	/// the front end, or before the guest is up. See the native side for why the key exists.
	/// </summary>
	/// <summary>Raw guest words — the debug channel's microscope. Empty when unreadable.</summary>
	public static uint[] Peek(uint addr, uint count)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || count == 0 || count > 64)
		{
			return System.Array.Empty<uint>();
		}

		var words = new uint[count];
		try
		{
			uint got = RefiiHostPeek(addr, words, count);
			if (got < count)
			{
				System.Array.Resize(ref words, (int)got);
			}
		}
		catch (EntryPointNotFoundException)
		{
			return System.Array.Empty<uint>();
		}

		return words;
	}

	// =================== ReXGlue runtime introspection (debug channel) ===================

	/// <summary>One kernel-registered thread, with the live r1/lr its backtrace starts from.</summary>
	public readonly record struct GuestThreadInfo(
		uint Tid, bool IsGuestThread, bool IsMainThread, bool Running,
		int Priority, uint Cpu, uint R1, uint Lr, string Name);

	private const uint IntrospectStride = 96;
	private const uint IntrospectCapacity = 128;

	private static string SlotString(byte[] buffer, int index, uint stride)
	{
		int start = index * (int)stride;
		int length = 0;
		while (length < stride && buffer[start + length] != 0)
		{
			++length;
		}

		return Encoding.UTF8.GetString(buffer, start, length);
	}

	/// <summary>
	/// Every thread the kernel knows, guest-created and host-side both. r1/lr are read from the
	/// live context without stopping the thread — exact for a parked thread, torn at worst for a
	/// running one, and the parked ones are the ones a freeze asks about.
	/// </summary>
	public static System.Collections.Generic.List<GuestThreadInfo> GetGuestThreads()
	{
		var threads = new System.Collections.Generic.List<GuestThreadInfo>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return threads;
		}

		var tids = new uint[IntrospectCapacity];
		var flags = new uint[IntrospectCapacity];
		var priority = new int[IntrospectCapacity];
		var cpu = new uint[IntrospectCapacity];
		var r1 = new uint[IntrospectCapacity];
		var lr = new uint[IntrospectCapacity];
		var names = new byte[IntrospectCapacity * IntrospectStride];
		uint count = RefiiHostGetGuestThreads(
			tids, flags, priority, cpu, r1, lr, names, IntrospectStride, IntrospectCapacity);
		for (int i = 0; i < count; ++i)
		{
			threads.Add(new GuestThreadInfo(
				tids[i], (flags[i] & 1u) != 0, (flags[i] & 2u) != 0, (flags[i] & 4u) != 0,
				priority[i], cpu[i], r1[i], lr[i], SlotString(names, i, IntrospectStride)));
		}

		return threads;
	}

	/// <summary>
	/// Guest return addresses for one thread, innermost first. Frame 0 is the context's LR —
	/// a leaf's return address exists nowhere else — and the rest walk the back chain the
	/// title's own ABI maintains (32-bit back chain at [r1], saved LR at back-chain − 8;
	/// verified in the binary, see RefiiHostGuestBacktrace).
	/// </summary>
	public static uint[] GetGuestBacktrace(uint tid, int max = 48)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || max < 1)
		{
			return System.Array.Empty<uint>();
		}

		var frames = new uint[max];
		uint count = RefiiHostGuestBacktrace(tid, frames, (uint)max);
		System.Array.Resize(ref frames, (int)count);
		return frames;
	}

	/// <summary>One entry of the kernel handle table. Type is rex XObject::Type.</summary>
	public readonly record struct KernelObjectInfo(uint Handle, uint Type, string Name);

	public static System.Collections.Generic.List<KernelObjectInfo> GetKernelObjects(
		out uint total)
	{
		total = 0;
		var objects = new System.Collections.Generic.List<KernelObjectInfo>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return objects;
		}

		const uint Capacity = 512;
		var handles = new uint[Capacity];
		var types = new uint[Capacity];
		var names = new byte[Capacity * IntrospectStride];
		uint count = RefiiHostGetKernelObjects(
			handles, types, names, IntrospectStride, Capacity, out total);
		for (int i = 0; i < count; ++i)
		{
			objects.Add(new KernelObjectInfo(
				handles[i], types[i], SlotString(names, i, IntrospectStride)));
		}

		return objects;
	}

	/// <summary>
	/// Every rex::perf counter, LIVE values — run totals or gauges, never per-frame rates:
	/// the snapshot rotation lives in the renderer, which this build compiles out. Rates are
	/// deltas between two reads.
	/// </summary>
	public static System.Collections.Generic.List<(string Name, long Value)> GetPerfCounters()
	{
		var counters = new System.Collections.Generic.List<(string, long)>();
		if (TryGetAbiVersion() is null)
		{
			return counters;
		}

		const uint Capacity = 64;
		var values = new long[Capacity];
		var names = new byte[Capacity * IntrospectStride];
		uint count = RefiiHostGetPerfCounters(values, names, IntrospectStride, Capacity);
		for (int i = 0; i < count; ++i)
		{
			counters.Add((SlotString(names, i, IntrospectStride), values[i]));
		}

		return counters;
	}

	/// <summary>One kernel export, named "module!Name". No call counts: the runtime's
	/// call_count field is dead in recompiled builds.</summary>
	public readonly record struct KernelExportInfo(
		string Name, uint Ordinal, bool Implemented, bool IsVariable);

	public static System.Collections.Generic.List<KernelExportInfo> GetKernelExports(
		uint first, bool onlyUnimplemented, out uint total)
	{
		total = 0;
		var exports = new System.Collections.Generic.List<KernelExportInfo>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return exports;
		}

		const uint Capacity = 256;
		var ordinals = new uint[Capacity];
		var flags = new uint[Capacity];
		var names = new byte[Capacity * IntrospectStride];
		uint count = RefiiHostGetKernelExports(
			first, onlyUnimplemented ? 1u : 0u, ordinals, flags, names, IntrospectStride,
			Capacity, out total);
		for (int i = 0; i < count; ++i)
		{
			exports.Add(new KernelExportInfo(
				SlotString(names, i, IntrospectStride), ordinals[i],
				(flags[i] & 1u) != 0, (flags[i] & 2u) != 0));
		}

		return exports;
	}

	/// <summary>One cvar. Type is rex FlagType (Boolean..Command), Lifecycle 0=init-only
	/// 1=hot-reload 2=requires-restart.</summary>
	public readonly record struct CvarInfo(
		string Name, string Value, string Default, string Category,
		uint Type, uint Lifecycle, bool Modified, bool DebugOnly);

	public static int GetCvarCount() =>
		TryGetAbiVersion() is null ? 0 : RefiiHostGetCvarCount();

	public static CvarInfo? GetCvar(int index)
	{
		if (TryGetAbiVersion() is null || index < 0)
		{
			return null;
		}

		var name = new byte[128];
		var value = new byte[256];
		var fallback = new byte[256];
		var category = new byte[64];
		if (RefiiHostGetCvarInfo((uint)index, name, (uint)name.Length, value,
				(uint)value.Length, fallback, (uint)fallback.Length, category,
				(uint)category.Length, out uint type, out uint lifecycle, out uint flags) == 0)
		{
			return null;
		}

		return new CvarInfo(
			Decode(name), Decode(value), Decode(fallback), Decode(category),
			type, lifecycle, (flags & 1u) != 0, (flags & 2u) != 0);
	}

	/// <summary>Set one cvar through the registry's own validator; false means it refused.</summary>
	public static bool SetCvar(string name, string value) =>
		TryGetAbiVersion() is not null && RefiiHostSetCvar(name, value) != 0;

	/// <summary>Where one guest path actually lands in the live VFS.</summary>
	public readonly record struct VfsResolution(
		bool Found, bool RootIsLink, string AbsolutePath, string LinkTarget,
		ulong Size, uint Attributes);

	public static VfsResolution? VfsResolve(string guestPath)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || string.IsNullOrEmpty(guestPath))
		{
			return null;
		}

		var absolute = new byte[1024];
		var link = new byte[256];
		uint result = RefiiHostVfsResolve(
			guestPath, absolute, (uint)absolute.Length, link, (uint)link.Length,
			out ulong size, out uint attributes);
		return new VfsResolution(
			(result & 1u) != 0, (result & 2u) != 0, Decode(absolute), Decode(link),
			size, attributes);
	}

	/// <summary>Every socket in the kernel handle table.</summary>
	public static System.Collections.Generic.List<(uint Handle, uint Port, ulong NativeHandle)>
		GetGuestSockets()
	{
		var sockets = new System.Collections.Generic.List<(uint, uint, ulong)>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return sockets;
		}

		const uint Capacity = 64;
		var handles = new uint[Capacity];
		var ports = new uint[Capacity];
		var native = new ulong[Capacity];
		uint count = RefiiHostGetGuestSockets(handles, ports, native, Capacity);
		for (int i = 0; i < count; ++i)
		{
			sockets.Add((handles[i], ports[i], native[i]));
		}

		return sockets;
	}


	// ======================= Debug instrumentation batch (ABI 113) =======================

	/// <summary>One Tracy FrameMark + perf counter flip per presented frame. Also rotates the
	/// per-frame counter snapshots and feeds the perf_log_csv writer when that cvar is set.</summary>
	public static void FrameMark()
	{
		if (TryGetAbiVersion() is not null)
		{
			RefiiHostFrameMark();
		}
	}

	/// <summary>Per-frame counter snapshots — the LAST frame's accumulators and gauges.
	/// Meaningful only while FrameMark runs each frame.</summary>
	public static System.Collections.Generic.List<(string Name, long Value)> GetPerfSnapshots()
	{
		var counters = new System.Collections.Generic.List<(string, long)>();
		if (TryGetAbiVersion() is null)
		{
			return counters;
		}

		const uint Capacity = 64;
		var values = new long[Capacity];
		var names = new byte[Capacity * IntrospectStride];
		uint count = RefiiHostGetPerfSnapshots(values, names, IntrospectStride, Capacity);
		for (int i = 0; i < count; ++i)
		{
			counters.Add((SlotString(names, i, IntrospectStride), values[i]));
		}

		return counters;
	}

	/// <summary>A message on the Tracy timeline. No-op in unprofiled builds.</summary>
	public static void TraceMessage(string text)
	{
		if (TryGetAbiVersion() is not null && !string.IsNullOrEmpty(text))
		{
			RefiiHostTraceMessage(text);
		}
	}

	/// <summary>A named Tracy zone; dispose ends it. Zero-cost when no profiler is attached.</summary>
	public readonly struct TraceZone : System.IDisposable
	{
		private readonly ulong _zone;
		public TraceZone(string name) =>
			_zone = TryGetAbiVersion() is not null ? RefiiHostTraceZoneBegin(name) : 0;
		public void Dispose()
		{
			if (_zone != 0)
			{
				RefiiHostTraceZoneEnd(_zone);
			}
		}
	}

	public static void TracePlot(string name, double value)
	{
		if (TryGetAbiVersion() is not null)
		{
			RefiiHostTracePlot(name, value);
		}
	}

	/// <summary>Which heap owns an address and its protection — the pointer-validity check.</summary>
	public static (uint HeapBase, uint HeapSize, uint PageSize, uint Protect)? HeapLookup(uint addr)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		if (RefiiHostHeapLookup(addr, out uint heapBase, out uint heapSize, out uint page,
				out uint protect) == 0)
		{
			return null;
		}

		return (heapBase, heapSize, page, protect);
	}

	/// <summary>One deliberate guest word write. Refused on unwritable or untracked pages.</summary>
	public static bool Poke(uint addr, uint value) =>
		TryGetAbiVersion() is not null && IsGuestUp && RefiiHostPoke(addr, value) != 0;

	/// <summary>Arms a recording probe on a guest function start. Slot, or a negative refusal:
	/// -1 all slots used, -2 not a recompiled function start, -3 no runtime.</summary>
	public static int ProbeSet(uint addr) =>
		TryGetAbiVersion() is null || !IsGuestUp ? -3 : RefiiHostProbeSet(addr);

	public static bool ProbeClear(int slot) =>
		TryGetAbiVersion() is not null && RefiiHostProbeClear(slot) != 0;

	public readonly record struct ProbeState(
		uint Addr, ulong Hits, uint LastLr, ulong[] LastArgs);

	public static ProbeState? ProbeRead(int slot)
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		var args = new ulong[6];
		if (RefiiHostProbeRead(slot, out uint addr, out ulong hits, out uint lastLr, args,
				(uint)args.Length) == 0)
		{
			return null;
		}

		return new ProbeState(addr, hits, lastLr, args);
	}

	/// <summary>The last debug-call/debug-lua outcome the tick pump recorded, with the
	/// sequence number a poster compares to know its own call finished.</summary>
	public static (ulong Seq, uint Addr, ulong R3, string Note)? DebugCallResult()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		var note = new byte[192];
		RefiiHostGetDebugCallResult(out ulong seq, out uint addr, out ulong r3, note,
			(uint)note.Length);
		return (seq, addr, r3, Decode(note));
	}

	/// <summary>
	/// The guest arena's identity for OUT-OF-PROCESS readers: the shared-memory object backing
	/// all of guest memory (bare name — /dev/shm/&lt;name&gt; on Linux, Local\&lt;name&gt; on
	/// Windows) and the host virtual base its views are mapped at (what ReadProcessMemory
	/// needs). Published into the handshake so a FROZEN run — the one state the debug channel
	/// cannot answer from — can still be read.
	/// </summary>
	public static (string Name, ulong Membase)? GetGuestArenaInfo()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		var name = new byte[256];
		if (RefiiHostGetGuestArenaInfo(name, (uint)name.Length, out ulong membase) == 0)
		{
			return null;
		}

		return (Decode(name), membase);
	}

	public static uint GetMenuScreenId()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return Refii.Ui.Engine_UiMenuSources.AnyScreen;
		}

		try
		{
			return RefiiHostGetMenuScreenId();
		}
		catch (EntryPointNotFoundException)
		{
			return Refii.Ui.Engine_UiMenuSources.AnyScreen;
		}
	}

	public static System.Collections.Generic.List<MenuRow> GetMenuRows(out ulong generation)
	{
		var rows = new System.Collections.Generic.List<MenuRow>();
		generation = 0;
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return rows;
		}

		byte[] name = new byte[128];
		byte[] parent = new byte[128];
		byte[] label = new byte[192];
		uint total;
		try
		{
			total = RefiiHostGetMenuRow(0, name, (uint)name.Length, parent,
				(uint)parent.Length, out uint firstId, out int firstType, out generation,
				out int firstRating);

			if (total == 0)
			{
				return rows;
			}

			RefiiHostGetMenuRowLabel(0, label, (uint)label.Length);
			rows.Add(new MenuRow(
				Text(name), Text(parent), firstId, firstType, firstRating, Text(label)));
			for (uint i = 1; i < total && i < 104u; ++i)
			{
				RefiiHostGetMenuRow(i, name, (uint)name.Length, parent, (uint)parent.Length,
					out uint id, out int type, out _, out int rating);
				RefiiHostGetMenuRowLabel(i, label, (uint)label.Length);
				rows.Add(new MenuRow(Text(name), Text(parent), id, type, rating, Text(label)));
			}
		}
		catch (EntryPointNotFoundException)
		{
			return rows;
		}

		return rows;

		static string Text(byte[] buffer)
		{
			int end = System.Array.IndexOf(buffer, (byte)0);
			return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
		}
	}

	/// <summary>
	/// One record in the hero's inventory. NOT one item — see <see cref="HostBridge.GetHeroInventory"/>.
	/// </summary>
	/// <param name="RecordId">The FNV-1 GDB key. NOT unique across records.</param>
	/// <param name="Count">How many of this thing the record holds.</param>
	/// <param name="Category">−1 when the item states none.</param>
	/// <param name="Viewed">The new-item marker the pause tabs decorate with.</param>
	public readonly record struct InventoryRecord(
		uint RecordId, int Count, int Category, bool Viewed);

	/// <summary>
	/// The hero's vitals, read straight off its components — where a menu's contents come from.
	/// The `GUI` natives open screens but never populate them.
	/// </summary>
	/// <param name="MoralityBase">
	/// Base, never the number to display: the guest's accessor adds real-estate and worship
	/// bonuses plus an Evil/Holy term held on another component and a global config string.
	/// Same for <paramref name="PurityBase"/> and <paramref name="RenownBase"/>.
	/// </param>
	/// <param name="Strength">
	/// A BODY-MORPH percentage in [0,1] — how the hero LOOKS, not what they have spent. The
	/// experience arrays are the progression. Same for skill and will.
	/// </param>
	/// <param name="AbilityLevels">
	/// Raw levels for ability ids 1..14, indexed by id. Ids 2, 3 and 6 are BUFFED by the guest's
	/// accessor and clamped against a ceiling from the game defs, so those three are the raw
	/// slot rather than the displayed number.
	/// </param>
	public readonly record struct HeroVitals(
		uint Hero,
		int Gold,
		float Health, float BaseHealth, float MaxHealth,
		bool Invulnerable, bool CantDie,
		float MoralityBase, float PurityBase, int RenownBase,
		float MoralityMin, float MoralityMax, float PurityMin, float PurityMax,
		float Strength, float Skill, float Will,
		float[] ExperienceAvailable, float[] ExperienceEarned,
		int[] AbilityLevels)
	{
		/// <summary>True once a hero exists — false for every moment before a save is loaded.</summary>
		public bool Known => Hero != 0u;

		/// <summary>The pools, in the order the guest stores them.</summary>
		public enum Pool { General = 0, Strength = 1, Skill = 2, Will = 3 }

		/// <summary>Spent in a pool. The guest keeps no spent counter; this is the derivation.</summary>
		public float Spent(Pool pool) =>
			ExperienceEarned[(int)pool] - ExperienceAvailable[(int)pool];
	}

	/// <summary>
	/// The guest game loop's own fixed-step arithmetic — whether it is ticking, and if not, why.
	/// A spin sample at <c>0x82277348</c> is the ordinary "no tick is due yet" yield of a 15 Hz
	/// pacer and says nothing about health; <see cref="SimTime"/> is what separates the cases,
	/// because `rex_lhGameLoop_Tick` rewrites <c>game+0x110</c> on EVERY iteration including the
	/// yielding arm, so a live thread with a frozen sim time means the clock stopped.
	/// <see cref="Completed"/> is read from the record the frame-sync index names; the word at
	/// 0x83497124 is a 0/1 slot selector, not a completion count.
	/// </summary>
	public readonly record struct GameLoopPacer(
		uint Game, bool SimulationGate, bool WorldGate, uint QuitByte,
		uint Presented, uint Completed, uint SyncSlot, uint ReaderLock,
		double SimTime, double TimebaseEpoch, double TimebaseTicksPerSecond,
		uint GuiRegisteredPlayers = 0, uint GuiUpdateEnabled = 0, uint GuiDoStringWindow = 0,
		double HudPresentationTime = 0.0, double ScreenPresentationTime = 0.0)
	{
		/// <summary>True once the walk found a game object at all.</summary>
		public bool Known => Game != 0;

		/// <summary>Frames presented and not yet completed — the term the render deadline grows by.</summary>
		public int InFlight => (int)(Presented - Completed);
	}

	/// <summary>Reads the game loop's pacer. See <see cref="GameLoopPacer"/>.</summary>
	public static GameLoopPacer GetGameLoopPacer()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		var words = new uint[11];
		var values = new double[5];
		uint game;
		try
		{
			game = RefiiHostGetGameLoopPacer(
				words, (uint)words.Length, values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		return new GameLoopPacer(
			game, words[1] != 0u, words[2] != 0u, words[7],
			words[3], words[5], words[4], words[6],
			values[0], values[1], values[2],
			words[8], words[9], words[10], values[3], values[4]);
	}

	/// <summary>
	/// The hero's DERIVED state — the guest accessors' own answers, which add GDB script
	/// constants, real-estate and worship terms and a renown modifier to the base values
	/// <see cref="HeroVitals"/> carries. The logbook's personality bands are tested at 0.375 and
	/// 0.625 of the normalised range, so the base names the wrong title near every threshold.
	/// Sampled by the pump on the guest's own thread, the only one allowed to call them.
	/// </summary>
	public readonly record struct HeroDerived(
		bool Known,
		float Morality, float MoralityMin, float MoralityMax,
		float Purity, float PurityMin, float PurityMax,
		int Renown,
		string TitleTag,
		float[] Attractiveness, float AttractivenessTotal,
		bool DogKnown, int DogCombat, int DogSearching, int DogState, string DogName,
		uint DogNameKey,
		uint[] DogTrickIds,
		int[] Stats, bool StatsKnown,
		float[] SpellDamage)
	{
		/// <summary>Morality as a 0..1 position in its own range — what the bands are tested on.</summary>
		public float MoralityFraction => Span(Morality, MoralityMin, MoralityMax);

		/// <summary>Purity as a 0..1 position in its own range.</summary>
		public float PurityFraction => Span(Purity, PurityMin, PurityMax);

		/// <summary>
		/// Whether the dog has learned the trick with this GDB record id — a RECORD ID, not a
		/// name hash: the guest runs `FNV1(name)` through its hash-to-id table before storing.
		/// </summary>
		public bool DogKnowsTrick(uint recordId)
		{
			if (DogTrickIds is null || recordId == 0u)
			{
				return false;
			}

			foreach (uint id in DogTrickIds)
			{
				if (id == recordId)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>One logbook stat counter by id, or 0 when out of range.</summary>
		public int Stat(int id) =>
			Stats is not null && id >= 0 && id < Stats.Length ? Stats[id] : 0;

		/// <summary>
		/// A degenerate range answers 0.5, not a divide-by-zero.
		/// </summary>
		private static float Span(float value, float min, float max) =>
			max - min <= float.Epsilon ? 0.5f : (value - min) / (max - min);
	}

	/// <summary>
	/// The options screen's settings — pages 1 and 2.
	///
	/// `Breadcrumb` is the raw SIZE, a float, not an index: the row picks the last of
	/// {0.0, 0.5, 1.0, 1.5, 2.0} that is less than or equal to it.
	/// </summary>
	public readonly record struct GameOptions(
		bool Known, bool Subtitles, bool TutorialBoxes, int MultiplayerOrbs,
		bool AutoJoinable, bool InvertCamera, float Breadcrumb);

	/// <summary>
	/// The options screen's SLIDER pages — 3 and 4. Separate from <see cref="GameOptions"/>
	/// because the lifetime is: these five live on the options screen's two page controllers and
	/// exist only while that screen is open, so `Known` false means the screen is not up. Every
	/// value is 0..1, the slider's own clamp. `Speaker` is 0 TV, 1 hi-fi, 2 surround.
	/// </summary>
	public readonly record struct OptionSliders(
		bool Known, float Gamma, float Sfx, float Music, float Speech, int Speaker);

	/// <summary>
	/// The signature the guest calls back on. See `core/host_asset_reader.h`. `IntPtr` rather
	/// than `byte*` throughout, so none of this needs `unsafe`.
	/// </summary>
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int AssetReaderDelegate(
		IntPtr path, IntPtr buffer, uint capacity, IntPtr outSize);

	/// <summary>
	/// Held for the process's lifetime, which is the whole reason this field exists:
	/// `GetFunctionPointerForDelegate` does not keep the delegate alive, and the guest would
	/// then be calling a pointer into reclaimed memory.
	/// </summary>
	private static AssetReaderDelegate? _assetReader;

	/// <summary>
	/// The hash side of the same boundary. See `core/host_asset_reader.h`. Returns the length
	/// written, 0 for "no such hash"; a name that would be truncated is refused, not shortened.
	/// </summary>
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate uint AssetNameForHashDelegate(uint hash, IntPtr buffer, uint capacity);

	/// <summary>Held for the process's lifetime, for the reason on <see cref="_assetReader"/>.</summary>
	private static AssetNameForHashDelegate? _assetNameResolver;

	/// <summary>Where a hash becomes a path — Godot's index over its own mount.</summary>
	private static Func<uint, string?>? _assetNameSource;

	/// <summary>Where the reader gets its bytes — Godot's own mount.</summary>
	private static Func<string, byte[]?>? _assetSource;

	/// <summary>
	/// Where it gets a LENGTH without reading — see `Engine_GameAssets.FileSize`. Separate from
	/// the byte source: answering a size probe with a read decompresses a whole bank entry.
	/// </summary>
	private static Func<string, long>? _assetSize;

	/// <summary>
	/// Points the guest's loaders at Godot's. Call after the mount succeeds, with the SAME reader
	/// every Godot loader uses. Pass null on the way out, so a guest read arriving after teardown
	/// gets "no reader" instead of a torn-down runtime.
	/// </summary>
	public static void SetAssetReader(Func<string, byte[]?>? source, Func<string, long>? size = null)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		_assetSource = source;
		_assetSize = size;

		try
		{
			if (source is null)
			{
				RefiiHostSetAssetReader(IntPtr.Zero);
				_assetReader = null;
				return;
			}

			_assetReader ??= ReadAssetForGuest;
			RefiiHostSetAssetReader(Marshal.GetFunctionPointerForDelegate(_assetReader));
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// Points the guest's HASH lookups at Godot's index — the other half of
	/// <see cref="SetAssetReader"/>, registered separately. Pass null on the way out.
	/// </summary>
	public static void SetAssetNameResolver(Func<uint, string?>? source)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		_assetNameSource = source;

		try
		{
			if (source is null)
			{
				RefiiHostSetAssetNameResolver(IntPtr.Zero);
				_assetNameResolver = null;
				return;
			}

			_assetNameResolver ??= AssetNameForHashForGuest;
			RefiiHostSetAssetNameResolver(
				Marshal.GetFunctionPointerForDelegate(_assetNameResolver));
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// One hash, resolved on the GUEST's thread. No exception may escape — it is reached through
	/// a C function pointer, so every failure leaves as a 0 return. ASCII, one byte per
	/// character; a name that would not fit is reported as a miss rather than truncated.
	/// </summary>
	private static uint AssetNameForHashForGuest(uint hash, IntPtr buffer, uint capacity)
	{
		try
		{
			if (_assetNameSource is not { } source || buffer == IntPtr.Zero || capacity == 0u)
			{
				return 0;
			}

			if (source(hash) is not { Length: > 0 } name)
			{
				return 0;
			}

			if ((uint)name.Length >= capacity)
			{
				return 0;
			}

			for (int i = 0; i < name.Length; i++)
			{
				Marshal.WriteByte(buffer, i, (byte)name[i]);
			}

			Marshal.WriteByte(buffer, name.Length, 0);
			return (uint)name.Length;
		}
		catch (Exception e)
		{
			GD.PushWarning($"[host-assets] the guest's hash lookup threw: {e.Message}");
			return 0;
		}
	}

	/// <summary>
	/// One asset, read on the GUEST's thread. No exception may escape — it is called through a
	/// C function pointer, so every failure leaves as a 0 return. Asked twice per asset: once
	/// with `buffer` null to state the size, once to fill it.
	/// </summary>
	private static int ReadAssetForGuest(IntPtr path, IntPtr buffer, uint capacity, IntPtr outSize)
	{
		try
		{
			if (outSize != IntPtr.Zero)
			{
				Marshal.WriteInt32(outSize, 0);
			}

			if (_assetSource is not { } source || path == IntPtr.Zero)
			{
				return 0;
			}

			if (Marshal.PtrToStringUTF8(path) is not { Length: > 0 } name)
			{
				return 0;
			}

			// A null buffer is the caller learning the length, not a failure — and nearly every
			// ask is this one, so it is served from the entry rather than a decompressed copy.
			if (buffer == IntPtr.Zero || capacity == 0u)
			{
				long known = _assetSize?.Invoke(name) ?? -1;
				if (known < 0)
				{
					return 0;
				}

				if (outSize != IntPtr.Zero)
				{
					Marshal.WriteInt32(outSize, (int)known);
				}

				return 1;
			}

			if (source(name) is not { } bytes)
			{
				return 0;
			}

			if (outSize != IntPtr.Zero)
			{
				Marshal.WriteInt32(outSize, bytes.Length);
			}

			int copied = bytes.Length < (int)capacity ? bytes.Length : (int)capacity;
			Marshal.Copy(bytes, 0, buffer, copied);
			if (outSize != IntPtr.Zero)
			{
				Marshal.WriteInt32(outSize, copied);
			}

			return 1;
		}
		catch (Exception e)
		{
			GD.PushWarning($"[host-assets] the guest's asset read threw: {e.Message}");
			return 0;
		}
	}

	/// <summary>Reads the options screen's sliders. See <see cref="OptionSliders"/>.</summary>
	public static OptionSliders GetOptionSliders()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		try
		{
			if (RefiiHostGetOptionsSliders(
					out float gamma, out float sfx, out float music, out float speech,
					out uint speaker) == 0)
			{
				return default;
			}

			return new OptionSliders(true, gamma, sfx, music, speech, unchecked((int)speaker));
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}
	}

	/// <summary>Reads the options screen's settings. See <see cref="GameOptions"/>.</summary>
	public static GameOptions GetOptions()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		var words = new uint[6];
		try
		{
			if (RefiiHostGetOptions(words, (uint)words.Length, out float breadcrumb) == 0)
			{
				return default;
			}

			return new GameOptions(
				true, words[1] != 0u, words[2] != 0u, unchecked((int)words[3]),
				words[4] != 0u, words[5] != 0u, breadcrumb);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}
	}

	/// <summary>
	/// One world-map region's info page. Every field is already worded by the guest.
	/// </summary>
	public readonly record struct WorldMapRegion(
		uint Count, uint Index, string Name, string Distance, string Travel,
		string Fine, string Owned, string Chests, string Digspots,
		string Divespots, string Gargoyles, string SilverKeys, string Merchants,
		int Stars)
	{
		/// <summary>Whether this sample is of the region a caller asked for.</summary>
		public bool IsFor(int wanted) => Name.Length != 0 && wanted >= 0 && Index == (uint)wanted;
	}

	/// <summary>
	/// Names the region to sample. The pump answers one tick later — it cannot know which row
	/// is highlighted, and sampling calls eleven guest providers so it has to run over there.
	/// </summary>
	public static void SetWorldMapRegion(uint index)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		try
		{
			RefiiHostSetWorldMapRegion(index);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>Reads back the sampled region. See <see cref="WorldMapRegion"/>.</summary>
	public static WorldMapRegion GetWorldMapRegion()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		var name = new byte[64];
		var distance = new byte[64];
		var travel = new byte[64];
		var counts = new byte[256];
		var merchants = new byte[512];
		uint total;
		uint index;
		int stars;
		try
		{
			total = RefiiHostGetWorldMapRegion(
				out index, name, (uint)name.Length, distance, (uint)distance.Length,
				travel, (uint)travel.Length, counts, (uint)counts.Length,
				merchants, (uint)merchants.Length, out stars);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		// Seven tab-separated counts in the page's own order — see the export.
		string[] parts = Utf8(counts).Split('\t');
		string Part(int at) => at < parts.Length ? parts[at] : string.Empty;

		return new WorldMapRegion(
			total, index, Utf8(name), Utf8(distance), Utf8(travel),
			Part(0), Part(1), Part(2), Part(3), Part(4), Part(5), Part(6),
			Utf8(merchants), stars);
	}

	/// <summary>
	/// Screen 28's one building. The screen's left column is a list of COMMANDS, not of
	/// properties: there is a single building, resolved when it opens, and nothing to select.
	///
	/// Every number is the guest's own answer, because each walks something the host has no
	/// business reproducing — a prosperity term off a world component, a GDB adjustment array
	/// matched against <see cref="TypeMask"/>, an occupant-opinion term weighted over five
	/// personality axes. The PRESENTATION is entirely ours: the star bin, the gold prefix, the
	/// negative recolour, which Variable row stands and which legend Page 2 carries.
	///
	/// <paramref name="Record"/> is the GDB record the text hangs off — NOT the building
	/// entity's own record. `sub_821DA3B0` takes the entity's def handle, then a property named
	/// after the component's class, and the entry-ref that yields is this hash.
	/// </summary>
	public readonly record struct RealEstate(
		bool Known, uint Record, uint TypeMask, bool Owned, bool HasTenant,
		int Value, int Economy, int Decoration, int Income, int SalePrice, int SellingFees,
		int NotVacant, int OwnerKilled, int HeroOpinion, uint BonusesRecord, string Occupant)
	{
		/// <summary>The guest's own "is a business" test — `mask &amp; 0xEFE` at 0x82999CF8.
		/// Every type but HOME and UNIQUE.</summary>
		public bool IsBusiness => (TypeMask & 0xEFEu) != 0u;

		/// <summary>Which BuildingType this is: the FIRST set bit, as `sub_8299A550` decodes it.
		/// 0 HOME, 1 DOCK, 2 FARM, 3 TOWNHALL, 4 WAREHOUSE, 5 SHOP, 6 BAR, 7 BORDELLO, 8 UNIQUE,
		/// 9 STALL, 10 HOUSE, 11 TEMPLE; -1 when nothing is set.</summary>
		public int Type
		{
			get
			{
				for (int bit = 0; bit < 12; ++bit)
				{
					if ((TypeMask & (1u << bit)) != 0u)
					{
						return bit;
					}
				}

				return -1;
			}
		}

		/// <summary>`sub_8299AF88`'s bins, over the building's current value.</summary>
		public int Stars => Value <= 0 ? 0
			: Value < 5000 ? 1
			: Value < 12000 ? 2
			: Value < 26000 ? 3
			: Value < 55000 ? 4
			: 5;
	}

	/// <summary>Reads screen 28's building. See <see cref="RealEstate"/>.</summary>
	public static RealEstate GetRealEstate()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		uint[] words = new uint[14];
		var occupant = new byte[128];
		uint known;
		try
		{
			known = RefiiHostGetRealEstate(words, (uint)words.Length,
				occupant, (uint)occupant.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		return new RealEstate(
			known != 0u, words[0], words[1], words[2] != 0u, words[3] != 0u,
			(int)words[4], (int)words[5], (int)words[6], (int)words[7], (int)words[8],
			(int)words[9], (int)words[10], (int)words[11], (int)words[12], words[13],
			Utf8(occupant));
	}

	/// <summary>
	/// The crime screen's arrest notice. Both strings are already localised — the guest's opener
	/// composed them through Babel — so they are drawn verbatim, not resolved again.
	/// </summary>
	public readonly record struct CrimeScreen(
		bool Known, int Fine, bool CanPayFine, bool CanServe, string Charge, string Service);

	/// <summary>Reads the crime screen. See <see cref="CrimeScreen"/>.</summary>
	public static CrimeScreen GetCrimeScreen()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		var charge = new byte[512];
		var service = new byte[512];
		try
		{
			if (RefiiHostGetCrimeScreen(
					out int fine, out uint flags, charge, (uint)charge.Length,
					service, (uint)service.Length) == 0)
			{
				return default;
			}

			return new CrimeScreen(
				true, fine, (flags & 1u) != 0u, (flags & 2u) != 0u,
				Utf8(charge), Utf8(service));
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}
	}

	/// <summary>One completed quest, as the logbook's page shows it.</summary>
	public readonly record struct CompletedQuest(uint Record, int Money, int Renown, string Conclusion);

	/// <summary>
	/// The hero's completed quests, UNSORTED. The page's order is descending by the GDB property
	/// `DisplayPriority`, which is a lookup for the caller to do.
	/// </summary>
	public static List<CompletedQuest> GetCompletedQuests()
	{
		var quests = new List<CompletedQuest>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return quests;
		}

		var conclusion = new byte[64];
		uint total;
		try
		{
			total = RefiiHostGetCompletedQuests(0u, out _, out _, out _, conclusion, 0u);
		}
		catch (EntryPointNotFoundException)
		{
			return quests;
		}

		for (uint index = 0; index < total; ++index)
		{
			RefiiHostGetCompletedQuests(
				index, out uint record, out int money, out int renown,
				conclusion, (uint)conclusion.Length);
			quests.Add(new CompletedQuest(record, money, renown, Utf8(conclusion)));
		}

		return quests;
	}

	/// <summary>
	/// The guest's expression/job timing meter object (`*(*(GUI+32)+44)`), read for drawing —
	/// the guest runs it and judges it. <see cref="Known"/> is false with no meter reachable.
	/// </summary>
	public readonly record struct ExpressionMeter(
		bool Known, bool On, int Mode, bool ArmedA, bool ArmedB, bool Latched,
		float Position, float Speed, bool HitA, bool HitB, bool Dynamic, int Band)
	{
		/// <summary>Whether a pip is travelling: modes 1 and 2 with a latched start.</summary>
		public bool PipRunning => Known && Mode is 1 or 2 && Latched && (ArmedA || ArmedB);
	}

	/// <summary>
	/// The TopBox manager's front record — see the bridge export. <see cref="Present"/> is false
	/// with no record up; <see cref="Known"/> is false with no manager reachable.
	/// </summary>
	public readonly record struct TopBoxFront(
		bool Known, bool Present, uint TitleTag, bool ShowA, bool HoldA, bool ShowY, bool HoldY,
		int Channel, uint NameHash, int LifetimeMs);

	/// <summary>
	/// The front TopBox record's body provider, as data: <paramref name="Class"/> 0 Babel key,
	/// 1 key with %1/%2/%3 numbers, 2 key with %s/%1/%2 keys, 3 a literal; see the native.
	/// </summary>
	public readonly record struct TopBoxBody(
		bool Known, bool Present, int Class, uint Key, uint Second, uint Third, uint Fourth,
		uint TitleTag, uint NameHash, string Literal);

	private static byte[]? _topBoxLiteral;

	public static TopBoxBody GetTopBoxBody()
	{
		var words = new uint[8];
		_topBoxLiteral ??= new byte[1024];
		uint count;
		try
		{
			count = RefiiHostGetTopBoxBody(words, (uint)words.Length, _topBoxLiteral, (uint)_topBoxLiteral.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		if (count < 8u)
		{
			return default;
		}

		int length = System.Array.IndexOf(_topBoxLiteral, (byte)0);
		string literal = length > 0
			? System.Text.Encoding.UTF8.GetString(_topBoxLiteral, 0, length)
			: string.Empty;
		return new TopBoxBody(true, words[0] != 0u, (int)words[1], words[2], words[3], words[4],
			words[5], words[6], words[7], literal);
	}

	public static TopBoxFront GetTopBoxFront()
	{
		var words = new uint[9];
		uint count;
		try
		{
			count = RefiiHostGetTopBoxFront(words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		if (count < 9u)
		{
			return default;
		}

		return new TopBoxFront(
			true, words[0] != 0u, words[1], words[2] != 0u, words[3] != 0u, words[4] != 0u,
			words[5] != 0u, (int)words[6], words[7], unchecked((int)words[8]));
	}

	public static ExpressionMeter GetExpressionMeter()
	{
		var values = new float[11];
		uint count;
		try
		{
			count = RefiiHostGetExpressionMeter(values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		if (count < 11u)
		{
			return default;
		}

		return new ExpressionMeter(
			true, values[0] != 0f, (int)values[1], values[2] != 0f, values[3] != 0f,
			values[4] != 0f, values[5], values[6], values[7] != 0f, values[8] != 0f,
			values[9] != 0f, (int)values[10]);
	}

	/// <summary>Reads the hero's derived state. See <see cref="HeroDerived"/>.</summary>
	public static HeroDerived GetHeroDerived()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		// Nine words: the bridge fills slots 0..8 and clamps to the capacity given, so a
		// shorter buffer silently drops the trick count and the flags read below.
		var words = new uint[10];
		var values = new float[26];
		var title = new byte[64];
		var dog = new byte[64];
		var stats = new int[124];
		var tricks = new uint[32];
		int known;
		try
		{
			known = RefiiHostGetHeroDerived(
				words, (uint)words.Length, values, (uint)values.Length,
				title, (uint)title.Length, dog, (uint)dog.Length,
				stats, (uint)stats.Length, tricks, (uint)tricks.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		if (known == 0)
		{
			return default;
		}

		var attractiveness = new float[11];
		System.Array.Copy(values, 6, attractiveness, 0, 11);

		var spell = new float[6];
		System.Array.Copy(values, 20, spell, 0, 6);

		return new HeroDerived(
			true,
			values[0], values[1], values[2],
			values[3], values[4], values[5],
			unchecked((int)words[1]),
			Utf8(title),
			attractiveness, values[17],
			words[2] != 0u,
			unchecked((int)words[5]), unchecked((int)words[6]),
			unchecked((int)words[3]), Utf8(dog), words[9],
			Trim(tricks, words[7]),
			stats, words[8] != 0u,
			spell);
	}

	/// <summary>The used prefix of a fixed buffer — the bridge reports how many it filled.</summary>
	private static uint[] Trim(uint[] buffer, uint count)
	{
		uint used = count < (uint)buffer.Length ? count : (uint)buffer.Length;
		var trimmed = new uint[used];
		System.Array.Copy(buffer, trimmed, (int)used);
		return trimmed;
	}

	/// <summary>A NUL-terminated buffer from the bridge as a string.</summary>
	private static string Utf8(byte[] buffer)
	{
		int length = System.Array.IndexOf(buffer, (byte)0);
		return length <= 0
			? string.Empty
			: System.Text.Encoding.UTF8.GetString(buffer, 0, length);
	}

	/// <summary>Reads the hero's vitals. See <see cref="HeroVitals"/>.</summary>
	/// <summary>
	/// The player hero's entity address, or 0 — the handle every hero read resolves first.
	/// </summary>
	public static uint HeroEntityAddress()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		var words = new uint[17];
		var values = new float[20];
		try
		{
			return RefiiHostGetHeroVitals(words, (uint)words.Length, values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	public static HeroVitals GetHeroVitals()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		var words = new uint[17];
		var values = new float[20];
		uint hero;
		try
		{
			hero = RefiiHostGetHeroVitals(
				words, (uint)words.Length, values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		if (hero == 0)
		{
			return default;
		}

		var abilities = new int[15];
		for (int id = 1; id <= 14; ++id)
		{
			abilities[id] = (int)words[2 + id];
		}

		var available = new float[4];
		var earned = new float[4];
		for (int pool = 0; pool < 4; ++pool)
		{
			available[pool] = values[12 + pool];
			earned[pool] = values[16 + pool];
		}

		return new HeroVitals(
			hero, (int)words[0],
			values[0], values[1], values[2],
			(words[2] & 1u) != 0u, (words[2] & 2u) != 0u,
			values[3], values[4], (int)words[1],
			values[5], values[6], values[7], values[8],
			values[9], values[10], values[11],
			available, earned, abilities);
	}

	/// <summary>
	/// The hero's inventory, one entry per RECORD, which is not one item: the guest keeps an
	/// `std::list` of records each carrying a `Count`, and the item count is their sum (what
	/// `GetNumberOfItems` returns). `RecordId` is not unique either — two records share one id
	/// whenever the first still holds instance data.
	/// </summary>
	/// <summary>
	/// The active sales and shortages — the world map's `Sales`. `NameKey` is FNV-1 of the
	/// event's text tag, taken from the record as a raw hash, so nothing but numbers crosses.
	/// </summary>
	/// <summary>
	/// One sale or shortage, as its page needs it. The three keys are Babel key HASHES, not
	/// tags — the guest hashed them when the sale activated — so they resolve through the book
	/// by key rather than by name. <paramref name="DaysLeft"/> is the guest's own
	/// `Duration - today + activationDay`, and <paramref name="Multiplier"/> is signed against
	/// 1.0: below is a sale, above a shortage, and the page shows `(m - 1) * 100` as a percent.
	/// </summary>
	public readonly record struct Sale(
		uint NameKey, uint DescriptionKey, uint RegionKey, int DaysLeft, float Multiplier)
	{
		public bool IsShortage => Multiplier > 1f;
	}

	/// <summary>Every live sale and shortage, in the guest's own order — sales first.</summary>
	public static List<Sale> GetSaleDetails()
	{
		var sales = new List<Sale>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return sales;
		}

		const int max = 32;
		const int stride = 5;
		uint[] words = new uint[max * stride];
		uint found;
		try
		{
			found = RefiiHostGetSales(words, (uint)words.Length);
		}
		catch (System.EntryPointNotFoundException)
		{
			return sales;
		}

		for (uint i = 0; i < found && i < max; ++i)
		{
			uint at = i * stride;
			sales.Add(new Sale(
				words[at], words[at + 1], words[at + 2], (int)words[at + 3],
				System.BitConverter.Int32BitsToSingle((int)words[at + 4])));
		}

		return sales;
	}

	public static List<(uint NameKey, bool IsShortage)> GetSales()
	{
		var sales = new List<(uint, bool)>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return sales;
		}

		const int max = 32;
		const int stride = 5;
		uint[] words = new uint[max * stride];
		uint found;
		try
		{
			found = RefiiHostGetSales(words, (uint)words.Length);
		}
		catch (System.DllNotFoundException)
		{
			return sales;
		}
		catch (System.EntryPointNotFoundException)
		{
			return sales;
		}

		for (uint i = 0; i < found && i < max; ++i)
		{
			float multiplier =
				System.BitConverter.Int32BitsToSingle((int)words[(i * stride) + 4]);
			sales.Add((words[i * stride], multiplier > 1f));
		}

		return sales;
	}

	/// <summary>
	/// The fast-travel regions that have at least one active stop — the world map's `Regions`.
	/// Each is FNV-1 of the region's NAME, which is its own Babel key, so no string crosses the
	/// boundary and a misread name resolves to nothing rather than to rubbish.
	/// </summary>
	/// <summary>
	/// The fast-travel regions with an active stop, each with the ORDINAL that identifies it.
	///
	/// The ordinal, not the name, is the identity: event 41's handler `sub_826C7E90` takes the
	/// row's `Float1`, subtracts the 999 the spawner added, and indexes the guest's own walk of
	/// the region container with it. That walk counts every non-null entry, so a region with no
	/// active stop still consumes an ordinal even though no row is ever spawned for it.
	/// </summary>
	/// <summary>
	/// The inventory screen's mode — 1 Items, 3 Weapons, 4 Clothing, 0 when unknown.
	///
	/// Screen 15 is one screen with three lists, chosen by a word at `screen+372` that
	/// `sub_829B0A58` branches on. Without it the screen cannot be bound to a menu template by
	/// its id alone: the weapons list would come up holding the item categories.
	/// </summary>
	public static uint GetInventoryScreenMode()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0u;
		}

		try
		{
			return RefiiHostGetInventoryScreenMode();
		}
		catch (System.DllNotFoundException)
		{
			return 0u;
		}
		catch (System.EntryPointNotFoundException)
		{
			return 0u;
		}
	}

	public static List<(uint NameHash, uint Ordinal)> GetFastTravelRegions()
	{
		var regions = new List<(uint, uint)>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return regions;
		}

		const int max = 64;
		uint[] words = new uint[max * 2];
		uint found;
		try
		{
			found = RefiiHostGetFastTravelRegions(words, (uint)words.Length);
		}
		catch (System.DllNotFoundException)
		{
			return regions;
		}
		catch (System.EntryPointNotFoundException)
		{
			return regions;
		}

		for (uint i = 0; i < found && i < max; ++i)
		{
			regions.Add((words[i * 2], words[(i * 2) + 1]));
		}

		return regions;
	}

	/// <summary>One entry in the hero's quest log. `RecordId` is the quest DEFINITION's GDB key —
	/// the same currency the item lists use — and the flags are the guest's own state bytes.</summary>
	/// <summary>
	/// One quest as the world map's pages need it. Every number is a plain field on the quest
	/// object, named by the save serialiser `sub_825237C8`: `Rating` at +28 (the star strip's
	/// slide, written unclamped), `RewardRenown` +32, `RewardMoney` +36,
	/// `CurrentObjectiveParameter` +40 (the `%1` every task line substitutes) and
	/// `DaysRemaining` +44.
	///
	/// <paramref name="Primary"/> and <paramref name="Active"/> choose the banner the quest
	/// page opens with; <paramref name="TaskCount"/> is the length of the task vector at
	/// +104/+108, whose tags come across separately — see <see cref="GetQuestTasks"/>.
	/// </summary>
	public readonly record struct QuestEntry(
		uint RecordId, bool Unlocked, bool Completed, bool Failed, bool OnWorldMap, bool Viewed,
		bool Primary, bool Active,
		int Rating, int RewardRenown, int RewardMoney, int ObjectiveParameter,
		float DaysRemaining, int TaskCount);

	/// <summary>
	/// One job's journey — distance and travel time, already worded by the guest's own
	/// providers. <paramref name="Record"/> says which quest the answer is FOR: the pump replies
	/// a tick after the ask, so a page that has moved on must not draw the previous row's numbers.
	/// An empty <paramref name="Travel"/> is a real answer for a job under an hour away.
	/// </summary>
	public readonly record struct QuestJourney(
		bool Known, uint Record, string Distance, string Travel, string Region, string Prompt,
		string Days)
	{
		public bool IsFor(uint record) => Known && Record == record && record != 0u;
	}

	/// <summary>Names the quest whose journey the pump should answer for next.</summary>
	public static void SetQuestTravel(uint record)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostSetQuestTravel(record);
		}
		catch (System.EntryPointNotFoundException)
		{
		}
	}

	/// <summary>Reads back the journey — see <see cref="QuestJourney"/>.</summary>
	public static QuestJourney GetQuestTravel()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		var distance = new byte[64];
		var travel = new byte[64];
		var region = new byte[64];
		var prompt = new byte[64];
		var days = new byte[64];
		try
		{
			uint known = RefiiHostGetQuestTravel(
				out uint record, distance, (uint)distance.Length, travel, (uint)travel.Length,
				region, (uint)region.Length, prompt, (uint)prompt.Length, days, (uint)days.Length);
			return new QuestJourney(
				known != 0u, record, Utf8(distance), Utf8(travel), Utf8(region), Utf8(prompt),
			Utf8(days));
		}
		catch (System.EntryPointNotFoundException)
		{
			return default;
		}
	}

	/// <summary>
	/// One quest's task lines, as Babel TAGS in the guest's own order — the LAST is the current
	/// task and everything before it is completed. Position is what decides that; the
	/// `Succeeded` byte beside each tag is stored and never read by the page.
	/// </summary>
	public static List<string> GetQuestTasks(uint recordId)
	{
		var tasks = new List<string>();
		if (TryGetAbiVersion() is null || !IsGuestUp || recordId == 0u)
		{
			return tasks;
		}

		var packed = new byte[2048];
		try
		{
			if (RefiiHostGetQuestTasks(recordId, packed, (uint)packed.Length) == 0u)
			{
				return tasks;
			}
		}
		catch (System.EntryPointNotFoundException)
		{
			return tasks;
		}

		foreach (string tag in Utf8(packed).Split('\t'))
		{
			if (tag.Length != 0)
			{
				tasks.Add(tag);
			}
		}

		return tasks;
	}

	/// <summary>
	/// The hero's quest log. The world map's Quests and Jobs are this one list filtered
	/// differently — the split is the definition's GDB `Type` (5 is a Job), which the caller
	/// resolves because the host has no GDB. Empty when the walk saw the container move.
	/// </summary>
	public static List<QuestEntry> GetQuestLog()
	{
		var quests = new List<QuestEntry>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return quests;
		}

		const int max = 256;
		const int stride = 8;
		uint[] words = new uint[max * stride];
		uint found;
		try
		{
			found = RefiiHostGetQuestLog(words, (uint)words.Length);
		}
		catch (System.DllNotFoundException)
		{
			return quests;
		}
		catch (System.EntryPointNotFoundException)
		{
			return quests;
		}

		for (uint i = 0; i < found && i < max; ++i)
		{
			uint at = i * stride;
			uint flags = words[at + 1];
			quests.Add(new QuestEntry(
				words[at],
				(flags & 1u) != 0u,
				(flags & 2u) != 0u,
				(flags & 4u) != 0u,
				(flags & 8u) != 0u,
				(flags & 16u) != 0u,
				(flags & 32u) != 0u,
				(flags & 64u) != 0u,
				(int)words[at + 2],
				(int)words[at + 3],
				(int)words[at + 4],
				(int)words[at + 5],
				System.BitConverter.Int32BitsToSingle((int)words[at + 6]),
				(int)words[at + 7]));
		}

		return quests;
	}

	/// <summary>
	/// The items the hero is WEARING, by GDB record id — what a clothing list must not offer
	/// again. Empty when nothing is worn, when the guest is down, and also when the walk saw the
	/// container move under it (a push reallocates); see the native side.
	/// </summary>
	/// <summary>
	/// One shop row's ASKING PRICE — what the shop charges, which is not the item's worth.
	/// </summary>
	public readonly record struct ShopPrice(bool Known, uint Ticket, int Price)
	{
		public bool IsFor(uint ticket) => Known && Ticket == ticket && ticket != 0u;
	}

	/// <summary>Names the row whose price the pump should answer for next.</summary>
	public static void SetShopPrice(uint ticket, bool sell)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostSetShopPrice(ticket, sell ? 1u : 0u);
		}
		catch (System.EntryPointNotFoundException)
		{
		}
	}

	/// <summary>Reads back the asking price — see <see cref="ShopPrice"/>.</summary>
	public static ShopPrice GetShopPrice()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		try
		{
			uint known = RefiiHostGetShopPrice(out uint ticket, out int price);
			return new ShopPrice(known != 0u, ticket, price);
		}
		catch (System.EntryPointNotFoundException)
		{
			return default;
		}
	}

	/// <summary>
	/// ONE family member's page, in the guest's own field order.
	///
	/// Ten of the eleven are already-formatted text the collector wrote into the record —
	/// `sub_829B04E0` sets them with `sub_82425DD0`, which takes formatted text rather than a
	/// tag — so they are shown as they arrive. <see cref="Region"/> is the exception and IS a
	/// Babel tag, because the page sets it with `sub_82425F40`.
	/// </summary>
	public readonly record struct FamilyDetails(
		bool Known, string Name, string Children, string Region, string Distance, string Travel,
		string Note, string DailyUpkeep, string TotalUpkeep, string Happiness,
		string HomeHappiness, string UpkeepHappiness, string Prompt);

	/// <summary>
	/// The page for the family member at <paramref name="index"/> — the row's own id, which is
	/// the ordinal the guest's page indexes by.
	/// </summary>
	public static FamilyDetails GetFamilyDetails(uint index)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		const int fields = 12;
		const int stride = 192;
		byte[] block = new byte[fields * stride];
		uint found;
		try
		{
			found = RefiiHostGetFamilyDetails(index, block, stride, fields);
		}
		catch (System.DllNotFoundException)
		{
			return default;
		}
		catch (System.EntryPointNotFoundException)
		{
			return default;
		}

		if (found == 0u)
		{
			return default;
		}

		string Field(int at)
		{
			if (at >= found)
			{
				return string.Empty;
			}

			int start = at * stride;
			int end = System.Array.IndexOf(block, (byte)0, start, stride);
			int length = (end < 0 ? start + stride : end) - start;
			return length > 0 ? Encoding.UTF8.GetString(block, start, length) : string.Empty;
		}

		return new FamilyDetails(
			true, Field(0), Field(1), Field(2), Field(3), Field(4), Field(5), Field(6),
			Field(7), Field(8), Field(9), Field(10), Field(11));
	}

	/// <summary>
	/// The world map's family members, in the order the guest's own collector produced them.
	///
	/// Sampled by the pump while the world map is up (`sub_8246F020`, freed with
	/// `sub_826C9450`) rather than walked here — the read goes through an entity-UID map and a
	/// component-20 name fallback, and the guest already has the function for it.
	/// </summary>
	public static List<string> GetFamily()
	{
		var family = new List<string>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return family;
		}

		const int max = 32;
		const int stride = 64;
		byte[] names = new byte[max * stride];
		uint found;
		try
		{
			found = RefiiHostGetFamily(names, stride, max);
		}
		catch (System.DllNotFoundException)
		{
			return family;
		}
		catch (System.EntryPointNotFoundException)
		{
			return family;
		}

		for (uint i = 0; i < found && i < max; ++i)
		{
			int start = (int)i * stride;
			int end = System.Array.IndexOf(names, (byte)0, start, stride);
			int length = (end < 0 ? start + stride : end) - start;
			if (length > 0)
			{
				family.Add(Encoding.UTF8.GetString(names, start, length));
			}
		}

		return family;
	}

	public static List<(uint RecordId, int Category)> GetWornItems()
	{
		var worn = new List<(uint, int)>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return worn;
		}

		const int max = 64;
		uint[] words = new uint[max * 2];
		uint found;
		try
		{
			found = RefiiHostGetWornItems(words, (uint)words.Length);
		}
		catch (System.DllNotFoundException)
		{
			return worn;
		}
		catch (System.EntryPointNotFoundException)
		{
			return worn;
		}

		for (uint i = 0; i < found && i < max; ++i)
		{
			worn.Add((words[i * 2], (int)words[(i * 2) + 1]));
		}

		return worn;
	}

	public static List<InventoryRecord> GetHeroInventory()
	{
		var records = new List<InventoryRecord>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return records;
		}

		const int stride = 4;
		uint count;
		try
		{
			count = RefiiHostGetHeroInventory(null, 0);
		}
		catch (EntryPointNotFoundException)
		{
			return records;
		}

		if (count == 0)
		{
			return records;
		}

		// Headroom: the guest mutates the list during the walk, so the second call can find
		// more than the probe did.
		var words = new uint[(count + 32u) * stride];
		uint written = RefiiHostGetHeroInventory(words, (uint)words.Length);

		int usable = (int)Math.Min(written, (uint)(words.Length / stride));
		for (int i = 0; i < usable; ++i)
		{
			int at = i * stride;
			records.Add(new InventoryRecord(
				words[at], (int)words[at + 1], (int)words[at + 2], words[at + 3] != 0u));
		}

		return records;
	}

	/// <summary>Reads the live fade. See <see cref="ScreenFade"/>.</summary>
	public static ScreenFade GetScreenFade()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		float[] rgba = new float[4];
		try
		{
			uint address = RefiiHostGetScreenFade(rgba, out float progress, out uint locked);
			return new ScreenFade(
				address, new Color(rgba[0], rgba[1], rgba[2], rgba[3]), progress, locked != 0u);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}
	}

	/// <summary>Reads the will assignment state. Never null; unknown reads as an empty state.</summary>
	public static SpellAssignState GetSpellAssign()
	{
		var assigned = new int[SpellAssignState.Slots];
		var offered = new uint[SpellAssignState.Slots];
		for (int i = 0; i < assigned.Length; ++i)
		{
			assigned[i] = -1;
		}

		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return new SpellAssignState(0u, assigned, offered, -1, false, false);
		}

		uint[] words = new uint[14];
		try
		{
			RefiiHostGetSpellAssign(words);
		}
		catch (EntryPointNotFoundException)
		{
			return new SpellAssignState(0u, assigned, offered, -1, false, false);
		}

		for (int i = 0; i < SpellAssignState.Slots; ++i)
		{
			assigned[i] = words[1 + i] == uint.MaxValue ? -1 : (int)words[1 + i];
			offered[i] = words[6 + i];
		}

		return new SpellAssignState(
			words[0], assigned, offered,
			words[11] == uint.MaxValue ? -1 : (int)words[11],
			words[12] != 0, words[13] != 0);
	}

	/// <summary>
	/// Puts a will on a d-pad slot, through the guest's own committing wrapper. Commit on
	/// LEFT/RIGHT, not on A: the guest's widget writes the moment the carousel moves
	/// (`sub_826B5330` / `sub_826B5240`), while A copies the current row's will down into every
	/// later row that also offers it.
	///
	/// The writer validates nothing, so only send a pair
	/// <see cref="SpellAssignState.Offers"/> admits.
	/// </summary>
	public static uint AssignSpell(int slot, int spell) =>
		PostFrontEndAction($"SpellAssign:{slot}:{spell}");

	/// <summary>
	/// What the guest would gate its own New Game and Load Game entries on. Two of the three
	/// numbers are plain guest memory; the count is a guest CALL and so arrives from the tick
	/// pump's sample rather than live. See RefiiHostGetSaveState.
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
	/// Loads the save at <paramref name="index"/> in the guest's own enumerated list — an INDEX,
	/// not a name: the guest identifies a save by the address of its handle slot inside its own
	/// vector (`Frontend_SetStartGameTarget` takes `*(screen+84) + 4*i` and
	/// `*(screen+148) + 40*i + 12`). Posted, not called: the commit allocates, rewrites
	/// `dword_83496AB8` and mutates refcounts non-atomically, so it must run on the tick thread.
	/// </summary>
	public static uint LoadSave(int index) => PostFrontEndAction($"LoadSave:{index}");

	/// <summary>
	/// The languages the guest has installed, in its own table order.
	/// <see cref="Language.Name"/> is the DISPLAY NAME the guest spawns its menu entry with, not
	/// the locale code — `sub_826E1C98` hands it to the spawner and the front end resolves it
	/// through Babel, so a caller should use the same book. The INDEX is the row: language
	/// entries are dispatched by command 302 with that index, never by name.
	/// </summary>
	public static System.Collections.Generic.List<Language> GetLanguages()
	{
		var languages = new System.Collections.Generic.List<Language>();

		// IsGuestUp, NOT IsRunning: IsRunning is false once the guest is past its boot, which
		// is precisely when the front end asks.
		if (TryGetAbiVersion() is null || !IsGuestUp)
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
			// An older native library. Degrade to "no languages" rather than throwing: a menu
			// that throws while being built takes the whole front end with it.
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
	/// Which loading screen the guest picked — its type and the texture name it chose. The
	/// loading screen is NOT a Gameface presentation: `sub_8235F108` submits direct textured
	/// quads, so this pair is the whole interface. The name is read through an LHString handle
	/// — two dereferences from `screen+272`; every writer is `sub_822651A0(a1 + 272, ...)`.
	/// `Body` is reported so a wrong indirection shows an address rather than an empty name.
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
	/// The guest's own "the world is loaded and ready" byte — `game + 0x105`, cleared by
	/// `CmdLevelChange_Update` when the load begins and set when its nine-state machine
	/// finishes. Not the same as the loading screen coming down.
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
	public static LoadingScreen? GetLoadingScreen()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		return RefiiHostGetLoadingScreen(out uint manager, out uint screen, out float fade,
			out uint requested, out uint done) != 0
			? new LoadingScreen(manager, screen, fade, requested != 0, done != 0)
			: null;
	}

	private static uint[]? _fenceArms;

	/// <summary>
	/// The engine-preloading gate the level load waits on (state 7), or null when the chain did
	/// not resolve. The object is the guest's "3D Engine" and the predicate its own
	/// `IsEnginePreLoading`. `Deadline` is the render deadline barrier at +176, NOT a
	/// retired-frame count: standing still while `Submitted` climbs past it is the resting
	/// state. The arms say which of the three producers is holding the flag.
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
	/// <summary>
	/// The raw eight words behind <see cref="TryGetFrontEnd"/>: the GUI object, the ACTIVE
	/// screen, which one it is, state, press-A-armed, cursor, scene slide, slide count.
	/// <see cref="Refii.Ui.Engine_UiModel.FrontEndScreen.None"/> is not "no screen" — it is a
	/// screen that is neither the title nor the menu, which is what the front end hands over to.
	/// </summary>
	public static uint[]? GetFrontEndRaw()
	{
		uint[] words = new uint[10];
		return RefiiHostGetFrontEnd(words) != 0 ? words : null;
	}

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


	/// <summary>Where the front end got to, for telling its failure modes apart.</summary>
	public readonly record struct FrontEndInit(
		uint LogoCell, uint ButtonCell, uint LogoReady, uint ButtonReady,
		uint Presentation, bool Stamped)
	{
		/// <summary>
		/// Whether the front end's Init ever ran. The constructor zeroes both cells and only
		/// Init writes them, so two nulls is the one unambiguous reading here.
		/// </summary>
		public bool InitRan => LogoCell != 0u || ButtonCell != 0u;

		/// <summary>
		/// Whether `sub_82379170` will get past its gate. Both cells must report ready or the
		/// whole front-end frame is discarded and the title can never tick.
		/// </summary>
		public bool TickGatePasses => LogoReady != 0u && ButtonReady != 0u;
	}

	/// <summary>The front end's init and gate state, or null when there is no front-end GUI.</summary>
	public static FrontEndInit? TryGetFrontEndInit()
	{
		uint[] words = new uint[6];
		if (RefiiHostGetFrontEndInit(words) == 0)
		{
			return null;
		}

		return new FrontEndInit(words[0], words[1], words[2], words[3], words[4], words[5] != 0u);
	}

	/// <summary>
	/// Every presentation the guest has live — the HUD, whichever menus are open, and the front
	/// end — all from one registry. The front end's own "active screen" field sees only one
	/// screen, and only before the game takes over.
	/// </summary>
	public static List<Presentation> GetPresentations()
	{
		uint count = RefiiHostGetPresentations(null, 0, null, 0);
		if (count == 0)
		{
			return new List<Presentation>();
		}

		// Headroom: the guest mutates the registry while this reads, so the second call can find
		// more than the probe. Stride 5 since ABI 38 — two words say whether it is on screen.
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
	/// displaying. The shipped presentation carries placeholders the game overwrites at runtime,
	/// and some tags resolve through a table that is not the Babel book.
	/// </summary>
	/// <param name="presentation">
	/// A presentation from <see cref="GetPresentations"/>, or 0 for the front end's active
	/// screen.
	/// </param>
	/// <summary>
	/// How many elements one presentation has, without reading any of them.
	/// </summary>
	/// <remarks>
	/// `RefiiHostGetUiElements` with no buffers still follows the child/sibling links but copies,
	/// allocates and marshals nothing. <see cref="Engine_UiElementState"/> uses it to notice a
	/// presentation reloaded under it, since the element arena is bump-allocated and reuses
	/// addresses.
	/// </remarks>
	public static uint GetUiElementCount(uint presentation = 0)
	{
		if (TryGetAbiVersion() is null)
		{
			return 0;
		}

		try
		{
			return RefiiHostGetUiElements(presentation, null, 0, null, 0);
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	public static List<UiElement>? GetUiElements(uint presentation = 0)
	{
		uint count = RefiiHostGetUiElements(presentation, null, 0, null, 0);
		if (count == 0)
		{
			return null;
		}

		// Sized from the probe, with headroom: the guest mutates the tree, so the second walk
		// can find more than the first. Stride 6 since ABI 84 — type byte, flags halfword,
		// element ADDRESS.
		var words = new uint[count * 6 + 64];
		var chars = new ushort[count * 24 + 1024];
		uint written = RefiiHostGetUiElements(
			presentation, words, (uint)words.Length, chars, (uint)chars.Length);

		int usable = (int)Math.Min(written, (uint)(words.Length / 6));
		var elements = new List<UiElement>(usable);
		for (int i = 0; i < usable; ++i)
		{
			uint offset = words[i * 6 + 2];
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

			elements.Add(new UiElement(words[i * 6], (int)words[i * 6 + 1], text,
				(int)words[i * 6 + 3], (int)words[i * 6 + 4], words[i * 6 + 5]));
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
	private static extern uint RefiiHostGetUiMovie(
		uint presentation, out uint outOrdinal, byte[]? outPath, uint capacity);

	/// <summary>
	/// The element of <paramref name="presentation"/> currently hosting a Bink movie, and the
	/// path it is playing, or null when none is. The guest has no embedded-video flag: a sprite
	/// whose `Movie` string is non-empty draws a movie into its own rect instead of a texture,
	/// and that string is the only statement of which element is playing. The ordinal is in the
	/// same traversal every other live array uses.
	/// </summary>
	public static (int Ordinal, string Path)? GetUiMovie(uint presentation)
	{
		var buffer = new byte[260];
		if (RefiiHostGetUiMovie(presentation, out uint ordinal, buffer, (uint)buffer.Length) == 0)
		{
			return null;
		}

		int length = System.Array.IndexOf(buffer, (byte)0);
		return ((int)ordinal,
			System.Text.Encoding.ASCII.GetString(buffer, 0, length < 0 ? buffer.Length : length));
	}

	[DllImport(Library)]
	private static extern int RefiiHostGetRequestedVideo(
		byte[]? outBuffer, uint capacity, out uint outSize, out uint outGeneration);

	[DllImport(Library)]
	private static extern uint RefiiHostFinishMovie();

	[DllImport(Library)]
	private static extern uint RefiiHostMovieState();

	[DllImport(Library)]
	private static extern uint RefiiHostGetUiPreviewObject(
		out uint modelHash, out uint entity, [Out] float[] values, uint valueCount);

	[DllImport(Library)]
	private static extern uint RefiiHostGetWorldSprites(
		[Out] uint[] words, uint wordCapacity, [Out] float[] values, uint valueCapacity,
		[Out] byte[] text, uint textCapacity, uint maxIcons);

	[DllImport(Library)]
	private static extern uint RefiiHostGetWorldIcons(
		out uint flags, [Out] uint[] types, [Out] uint[] entities, uint maxIcons);

	[DllImport(Library)]
	private static extern int RefiiHostGetMovieRect(
		out float x0, out float y0, out float x1, out float y1);

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
	private static extern uint RefiiHostGetActiveFx(uint[]? outWords, uint maxEffects);

	[DllImport(Library)]
	private static extern uint RefiiHostGetSaveCards(out uint cursor, out uint baseSlot, byte[]? text, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHudMeters([Out] uint[]? hashes, [Out] float[]? values, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetExpressionMeter([Out] float[] values, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetTopBoxFront([Out] uint[] words, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHudPulsers([Out] uint[]? hashes, [Out] float[]? phases, uint capacity);


	[DllImport(Library)]
	private static extern uint RefiiHostGetHudTexts([Out] uint[]? hashes, uint capacity, [Out] byte[]? text, uint textCapacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetSpellAssign([Out] uint[] outWords);

	[DllImport(Library)]
	private static extern uint RefiiHostGetMenuRow(
		uint index, [Out] byte[]? name, uint nameCapacity, [Out] byte[]? parent,
		uint parentCapacity, out uint id, out int eventType, out ulong generation,
		out int rating);

	[DllImport(Library)]
	private static extern uint RefiiHostTakeUiElementChanges(
		uint[]? outWords, uint wordCapacity, byte[]? outKinds, uint kindCapacity);

	[DllImport(Library, CharSet = CharSet.Ansi)]
	private static extern void RefiiHostSetGuiPredicate(string name, uint kind, uint bits);

	[DllImport(Library)]
	private static extern void RefiiHostUiElementCounters(
		out ulong outRecorded, out uint outPending, out ulong outDropped);

	[DllImport(Library)]
	private static extern uint RefiiHostTakeMenuChunks(
		byte[] outUtf8, uint utf8Capacity, byte[] outKinds, uint kindsCapacity, uint maxRecords);

	[DllImport(Library)]
	private static extern uint RefiiHostGetWornItems(uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFamily([Out] byte[]? outNames, uint stride, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFamilyDetails(
		uint index, [Out] byte[] outFields, uint stride, uint count);

	[DllImport(Library)]
	private static extern void RefiiHostSetShopPrice(uint ticket, uint sell);

	[DllImport(Library)]
	private static extern uint RefiiHostGetShopPrice(out uint outTicket, out int outPrice);

	[DllImport(Library)]
	private static extern uint RefiiHostGetInventoryScreenMode();

	[DllImport(Library)]
	private static extern uint RefiiHostGetQuestLog(uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFastTravelRegions([Out] uint[]? outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetSales(uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern void RefiiHostMenuChunkCounters(
		out ulong outRecorded, out uint outPending, out ulong outDropped);

	[DllImport(Library)]
	private static extern int RefiiHostMenuPopulating();

	[DllImport(Library)]
	private static extern uint RefiiHostTakeGuiNativeCalls(
		byte[] outUtf8, uint utf8Capacity, byte[] outKinds, uint kindsCapacity, uint maxRecords);

	[DllImport(Library)]
	private static extern void RefiiHostGuiNativeCounters(
		out ulong outIntercepted, out uint outPending, out uint outNamed);

	[DllImport(Library)]
	private static extern uint RefiiHostGetMenuRowLabel(
		uint index, [Out] byte[]? outLabel, uint labelCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetMenuScreenId();

	[DllImport(Library)]
	private static extern uint RefiiHostPeek(uint addr, [Out] uint[] outWords, uint count);

	[DllImport(Library)]
	private static extern uint RefiiHostGetGuestThreads(
		[Out] uint[] outTids, [Out] uint[] outFlags, [Out] int[] outPriority, [Out] uint[] outCpu,
		[Out] uint[] outR1, [Out] uint[] outLr, [Out] byte[] outNames, uint nameStride,
		uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGuestBacktrace(uint tid, [Out] uint[] outFrames,
		uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetKernelObjects(
		[Out] uint[] outHandles, [Out] uint[] outTypes, [Out] byte[] outNames, uint nameStride,
		uint capacity, out uint total);

	[DllImport(Library)]
	private static extern uint RefiiHostGetPerfCounters(
		[Out] long[] outValues, [Out] byte[] outNames, uint nameStride, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetKernelExports(
		uint first, uint onlyUnimplemented, [Out] uint[] outOrdinals, [Out] uint[] outFlags,
		[Out] byte[] outNames, uint nameStride, uint capacity, out uint total);

	[DllImport(Library)]
	private static extern int RefiiHostGetCvarCount();

	[DllImport(Library)]
	private static extern uint RefiiHostGetCvarInfo(
		uint index, [Out] byte[] outName, uint nameCapacity, [Out] byte[] outValue,
		uint valueCapacity, [Out] byte[] outDefault, uint defaultCapacity,
		[Out] byte[] outCategory, uint categoryCapacity, out uint type, out uint lifecycle,
		out uint flags);

	[DllImport(Library)]
	private static extern uint RefiiHostSetCvar(string name, string value);

	[DllImport(Library)]
	private static extern uint RefiiHostVfsResolve(
		string guestPath, [Out] byte[] outAbsolute, uint absoluteCapacity, [Out] byte[] outLink,
		uint linkCapacity, out ulong size, out uint attributes);

	[DllImport(Library)]
	private static extern uint RefiiHostGetGuestSockets(
		[Out] uint[] outHandles, [Out] uint[] outPorts, [Out] ulong[] outNative, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetGuestArenaInfo(
		[Out] byte[] outName, uint nameCapacity, out ulong membase);

	[DllImport(Library)]
	private static extern void RefiiHostFrameMark();

	[DllImport(Library)]
	private static extern uint RefiiHostGetPerfSnapshots(
		[Out] long[] outValues, [Out] byte[] outNames, uint nameStride, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostTraceMessage(string text);

	[DllImport(Library)]
	private static extern ulong RefiiHostTraceZoneBegin(string name);

	[DllImport(Library)]
	private static extern void RefiiHostTraceZoneEnd(ulong zone);

	[DllImport(Library)]
	private static extern void RefiiHostTracePlot(string name, double value);

	[DllImport(Library)]
	private static extern uint RefiiHostHeapLookup(
		uint addr, out uint heapBase, out uint heapSize, out uint pageSize, out uint protect);

	[DllImport(Library)]
	private static extern uint RefiiHostPoke(uint addr, uint value);

	[DllImport(Library)]
	private static extern int RefiiHostProbeSet(uint addr);

	[DllImport(Library)]
	private static extern uint RefiiHostProbeClear(int slot);

	[DllImport(Library)]
	private static extern uint RefiiHostProbeRead(
		int slot, out uint addr, out ulong hits, out uint lastLr, [Out] ulong[] outArgs,
		uint argsCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetDebugCallResult(
		out ulong seq, out uint addr, out ulong r3, [Out] byte[] outNote, uint noteCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHudScriptMeters([Out] uint[]? hashes, [Out] float[]? ratios, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHudScriptTexts([Out] uint[]? hashes, [Out] uint[]? textIds, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetUiTextureHashes(uint presentation, [Out] uint[]? hashes, uint capacity);

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
	private static extern uint RefiiHostGetEntityMeshParts(
		uint entity, uint[] outHashes, uint maxHashes, out uint outTotal);

	[DllImport(Library)]
	private static extern uint RefiiHostGetDeadEntities(ulong[] outUids, uint maxUids);

	[DllImport(Library)]
	private static extern int RefiiHostGetSubtitles();

	[DllImport(Library)]
	private static extern int RefiiHostGetPauseState(
		out uint screenId, out uint modalId, out uint modalMode, out uint currentTab);

	[DllImport(Library)]
	private static extern int RefiiHostPauseTabSelect(uint tab);

	[DllImport(Library)]
	private static extern uint RefiiHostPauseTabsAvailable();

	[DllImport(Library)]
	private static extern int RefiiHostGetAbilitiesMode();

	[DllImport(Library)]
	private static extern uint RefiiHostGetMapMarkers(uint[] outHashes, float[] outValues, uint cap);

	[DllImport(Library)]
	private static extern uint RefiiHostGetMenuBox(
		out uint selected, out uint flags, out uint enabledMask);

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
	private static extern uint RefiiHostGetDeactivatedLayers(uint[] outHashes, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetLoadingScreen(
		out uint manager, out uint screen, out float fade, out uint requested, out uint done);

	[DllImport(Library)]
	private static extern int RefiiHostGetFrontEnd(uint[] outWords);

	[DllImport(Library)]
	private static extern int RefiiHostGetFrontEndInit(uint[] outWords);

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
	private static extern uint RefiiHostGetScreenFade(
		float[]? outRgba, out float outProgress, out uint outLocked);

	[DllImport(Library)]
	private static extern uint RefiiHostGetWorldMapRegion(
		out uint outIndex, byte[] name, uint nameCapacity, byte[] distance, uint distanceCapacity,
		byte[] travel, uint travelCapacity, byte[] counts, uint countsCapacity,
		byte[] merchants, uint merchantsCapacity, out int outStars);

	[DllImport(Library)]
	private static extern void RefiiHostSetWorldMapRegion(uint index);

	[DllImport(Library)]
	private static extern uint RefiiHostGetQuestTasks(uint recordId, byte[] packed, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostSetQuestTravel(uint record);

	[DllImport(Library)]
	private static extern uint RefiiHostGetQuestTravel(
		out uint outRecord, byte[] distance, uint distanceCapacity, byte[] travel,
		uint travelCapacity, byte[] region, uint regionCapacity, byte[] prompt,
		uint promptCapacity, byte[] days, uint daysCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetRealEstate(
		uint[] words, uint wordCapacity, byte[] occupant, uint occupantCapacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetOptions(
		uint[] words, uint wordCapacity, out float outBreadcrumb);

	[DllImport(Library)]
	private static extern void RefiiHostSetAssetReader(IntPtr reader);

	[DllImport(Library)]
	private static extern void RefiiHostSetAssetNameResolver(IntPtr resolver);

	[DllImport(Library)]
	private static extern int RefiiHostGetOptionsSliders(
		out float outGamma, out float outSfx, out float outMusic, out float outSpeech,
		out uint outSpeaker);

	[DllImport(Library)]
	private static extern int RefiiHostGetCrimeScreen(
		out int outFine, out uint outFlags, byte[] charge, uint chargeCapacity,
		byte[] service, uint serviceCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCompletedQuests(
		uint index, out uint outRecord, out int outMoney, out int outRenown,
		byte[] outConclusion, uint conclusionCapacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetHeroDerived(
		uint[] words, uint wordCapacity, float[] values, uint valueCapacity,
		byte[] titleTag, uint titleCapacity, byte[] dogName, uint dogCapacity,
		int[] stats, uint statsCapacity, uint[] trickIds, uint trickCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHeroVitals(
		uint[]? outWords, uint wordCapacity, float[]? outValues, uint valueCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHeroInventory(uint[]? outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetGameLoopPacer(
		uint[]? outWords, uint wordCapacity, double[]? outValues, uint valueCapacity);

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

	/// <summary>
	/// What the guest's pub-game manager currently says. §the pub games are the guest's, not ours.
	/// </summary>
	/// <remarks>
	/// The guest drives the whole lifecycle — which game, which variation, which pad and
	/// language, seeding the bank from the hero's gold, and settling it afterwards. None of that
	/// is reimplemented here; see the block above <c>RefiiHostGetPubGame</c> in
	/// <c>host_bridge.cpp</c> for the addresses. State 2 is the seam: 2 through 8 are the
	/// Scaleform module Godot replaces, so the host waits for 2, plays, and writes the result.
	/// </remarks>
	public readonly record struct PubGameState(
		uint Stage, uint Profile, long Gold, long Debt, string Game, string Variation)
	{
		/// <summary>The guest has a pub game up and is waiting for whoever draws it.</summary>
		public bool WantsHost => Stage == HostStage && Profile != 0;

		/// <summary>
		/// The state that starts a game — a ONE-SHOT, not the state it plays in.
		/// </summary>
		/// <remarks>
		/// From the manager tick <c>sub_82467B08</c>: case 2 creates the 1 MB heap
		/// (<c>HeapCreate(0, 0x100000)</c> → <c>mgr+32</c>) and the audio emitter (<c>mgr+64</c>),
		/// builds the game object via <c>sub_82468378</c>, then immediately moves to 3 — or to 6
		/// if the abandon flag at <c>mgr+136</c> is set.
		/// </remarks>
		public const uint HostStage = 2;

		/// <summary>
		/// True while a session is live and the host should keep drawing.
		/// </summary>
		/// <remarks>
		/// States 3 and 4 are where the game runs — both call <c>sub_82467AA8</c>, which advances
		/// and displays the movie. 5 is a one-tick bounce back to 3; 7 and 8 are the fade-out the
		/// guest runs when something in the world ends the session. The guest moves 2 → 3 on its
		/// own, so only stage 2 must not be treated as the live state.
		/// </remarks>
		public bool IsLive => Stage is 2 or 3 or 4 or 5 or 7 or 8;

		/// <summary>
		/// The state to hand back to end a session — teardown, NOT the settle.
		/// </summary>
		/// <remarks>
		/// Writing 9 directly would leak the session: case 2 created a 1 MB heap, an audio
		/// emitter, the game object at <c>mgr+60</c> and a GPU resource at <c>mgr+40</c>. Case 6
		/// releases all of them and then sets 9 itself, so handing back 6 gets the settle and the
		/// cleanup in the guest's own order.
		/// </remarks>
		public const uint TeardownStage = 6;

		/// <summary>The state whose tick settles the money and resets the manager.</summary>
		/// <remarks>Reached by the guest from <see cref="TeardownStage"/>; not written directly.</remarks>
		public const uint SettleStage = 9;
	}

	/// <summary>Reads the manager, or null when the guest is not up.</summary>
	public static PubGameState? GetPubGame()
	{
		if (!IsGuestUp)
		{
			return null;
		}

		var values = new uint[6];
		var name = new byte[64];
		var variation = new byte[64];

		try
		{
			if (RefiiHostGetPubGame(
				values, name, (uint)name.Length, variation, (uint)variation.Length) == 0)
			{
				return null;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		// High word first: the guest is big endian and these are int64.
		long gold = ((long)values[2] << 32) | values[3];
		long debt = ((long)values[4] << 32) | values[5];

		return new PubGameState(
			values[0], values[1], gold, debt, Ascii(name), Ascii(variation));
	}

	/// <summary>
	/// Writes the game's result into the guest's profile and hands it the settlement state.
	/// </summary>
	/// <remarks>
	/// Gold is an ABSOLUTE balance, not a delta: the guest seeded the profile's bank from the
	/// hero's real gold and settles by crediting the difference, so pass what the player
	/// finished with.
	/// </remarks>
	public static bool SetPubGameResult(long gold, long debt) =>
		SetPubGameResult(gold, debt, PubGameState.TeardownStage);

	public static bool SetPubGameResult(long gold, long debt, uint state)
	{
		if (!IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostSetPubGameResult(gold, debt, state) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	private static string Ascii(byte[] buffer)
	{
		int length = System.Array.IndexOf(buffer, (byte)0);
		return length <= 0
			? string.Empty
			: System.Text.Encoding.ASCII.GetString(buffer, 0, length);
	}

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int RefiiHostGetPubGame(
		[Out] uint[] values,
		[Out] byte[] name,
		uint nameCapacity,
		[Out] byte[] variation,
		uint variationCapacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int RefiiHostSetPubGameResult(long gold, long debt, uint state);

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
	private static extern void RefiiHostSetAppRoot(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string root);

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
	private static extern uint RefiiHostGetAchievementUnlocks(
		ulong afterSequence, [Out] uint[] ids, [Out] uint[] users, [Out] ulong[] sequences,
		uint maxUnlocks);

	[DllImport(Library)]
	private static extern uint RefiiHostGetTitleXdbf([Out] byte[]? bytes, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostSetAchievementUnlocks(
		uint[] ids, ulong[] unlockFiletimes, uint count);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAchievementsUiRequests();

	[DllImport(Library)]
	private static extern int RefiiHostShutdown(byte[] message, nuint capacity);

	// The members below were recovered by decompiling `refii-launcher.dll`. The bodies are the
	// original logic; decompilation carried no comments, so an offset here is undocumented
	// rather than self-evident.

		public readonly record struct SaveCard(int Index, string Description);

		public static List<SaveCard> GetSaveCards(out int cursor)
		{
			List<SaveCard> list = new List<SaveCard>();
			cursor = 0;
			if (!TryGetAbiVersion().HasValue || !IsGuestUp)
			{
				return list;
			}
			byte[] array = new byte[4096];
			uint num;
			try
			{
				num = RefiiHostGetSaveCards(out var cursor2, out var _, array, (uint)array.Length);
				cursor = (int)cursor2;
			}
			catch (EntryPointNotFoundException)
			{
				return list;
			}
			int num2 = 0;
			for (uint num3 = 0u; num3 < num; num3++)
			{
				if (num2 >= array.Length)
				{
					break;
				}
				int num4 = Array.IndexOf(array, (byte)0, num2);
				if (num4 < 0)
				{
					break;
				}
				list.Add(new SaveCard((int)num3, Encoding.UTF8.GetString(array, num2, num4 - num2)));
				num2 = num4 + 1;
			}
			return list;
		}

		public static Dictionary<uint, float> GetHudMeters()
		{
			Dictionary<uint, float> dictionary = new Dictionary<uint, float>();
			if (!TryGetAbiVersion().HasValue || !IsGuestUp)
			{
				return dictionary;
			}
			uint[] array = new uint[128];
			float[] array2 = new float[128];
			uint num;
			try
			{
				num = RefiiHostGetHudMeters(array, array2, (uint)array.Length);
			}
			catch (EntryPointNotFoundException)
			{
				return dictionary;
			}
			if (num > array.Length)
			{
				GD.PushWarning($"[host] the guest holds {num} HUD meters, more than the {array.Length} asked for — the tail was not read");
				num = (uint)array.Length;
			}
			for (uint num2 = 0u; num2 < num; num2++)
			{
				dictionary[array[num2]] = array2[num2];
			}
			return dictionary;
		}

		/// <summary>
		/// The HUD's pulsers — registry 4 — as their loop PHASE 0..1 (`rec+20`), keyed by
		/// `FNV1(PulserName)`. The guest advances them; the host scrubs at `phase × 1000`.
		/// </summary>
		public static Dictionary<uint, float> GetHudPulsers()
		{
			Dictionary<uint, float> dictionary = new Dictionary<uint, float>();
			if (!TryGetAbiVersion().HasValue || !IsGuestUp)
			{
				return dictionary;
			}
			uint[] array = new uint[64];
			float[] array2 = new float[64];
			uint num;
			try
			{
				num = RefiiHostGetHudPulsers(array, array2, (uint)array.Length);
			}
			catch (EntryPointNotFoundException)
			{
				return dictionary;
			}
			if (num > array.Length)
			{
				num = (uint)array.Length;
			}
			for (uint i = 0u; i < num; i++)
			{
				dictionary[array[i]] = array2[i];
			}
			return dictionary;
		}

		/// <summary>
		/// The HUD values the shipped Lua sets — the other fifty meters, as 0..100 to match
		/// <see cref="GetHudMeters"/>. Two routes feed one HUD: the C++ publishers cover about
		/// twenty names, of which only `PlayerHealth` and `PlayerHealthMax` persist through
		/// gameplay (edge-triggered on the value changing), while the rest of `hud.bgf` is driven
		/// by name from Lua through `SetMeterRatioOverTime` into a store the element registry
		/// never sees. Scaled here because the guest states a RATIO and the other route publishes
		/// `ratio * 100`.
		/// </summary>
		public static Dictionary<uint, float> GetHudScriptMeters()
		{
			var values = new Dictionary<uint, float>();
			if (!TryGetAbiVersion().HasValue || !IsGuestUp)
			{
				return values;
			}

			uint[] hashes = new uint[256];
			float[] ratios = new float[256];
			uint found;
			try
			{
				found = RefiiHostGetHudScriptMeters(hashes, ratios, (uint)hashes.Length);
			}
			catch (EntryPointNotFoundException)
			{
				return values;
			}

			if (found > hashes.Length)
			{
				GD.PushWarning($"[host] the guest's HUD script store holds {found} meters, more " +
					$"than the {hashes.Length} asked for — the tail was not read");
				found = (uint)hashes.Length;
			}

			for (uint i = 0; i < found; ++i)
			{
				values[hashes[i]] = ratios[i] * 100f;
			}

			return values;
		}

		/// <summary>
		/// The HUD texts the shipped Lua sets, as name hash to BABEL TEXT ID — an id, not a
		/// string: `SetGUIText` caches the id it was handed. Keyed exactly as `book.babel` is.
		/// </summary>
		public static Dictionary<uint, uint> GetHudScriptTexts()
		{
			var values = new Dictionary<uint, uint>();
			if (!TryGetAbiVersion().HasValue || !IsGuestUp)
			{
				return values;
			}

			uint[] hashes = new uint[256];
			uint[] ids = new uint[256];
			uint found;
			try
			{
				found = RefiiHostGetHudScriptTexts(hashes, ids, (uint)hashes.Length);
			}
			catch (EntryPointNotFoundException)
			{
				return values;
			}

			if (found > hashes.Length)
			{
				GD.PushWarning($"[host] the guest's HUD script store holds {found} texts, more " +
					$"than the {hashes.Length} asked for — the tail was not read");
				found = (uint)hashes.Length;
			}

			for (uint i = 0; i < found; ++i)
			{
				values[hashes[i]] = ids[i];
			}

			return values;
		}

		public static Dictionary<uint, string> GetHudTexts()
		{
			Dictionary<uint, string> dictionary = new Dictionary<uint, string>();
			if (!TryGetAbiVersion().HasValue || !IsGuestUp)
			{
				return dictionary;
			}
			uint[] array = new uint[64];
			byte[] array2 = new byte[8192];
			uint num;
			try
			{
				num = RefiiHostGetHudTexts(array, (uint)array.Length, array2, (uint)array2.Length);
			}
			catch (EntryPointNotFoundException)
			{
				return dictionary;
			}
			if (num > array.Length)
			{
				GD.PushWarning($"[host] the guest holds {num} HUD texts, more than the {array.Length} asked for — the tail was not read");
				num = (uint)array.Length;
			}
			int num2 = 0;
			for (uint num3 = 0u; num3 < num; num3++)
			{
				if (num2 >= array2.Length)
				{
					break;
				}
				int num4 = Array.IndexOf(array2, (byte)0, num2);
				if (num4 < 0)
				{
					break;
				}
				dictionary[array[num3]] = Encoding.UTF8.GetString(array2, num2, num4 - num2);
				num2 = num4 + 1;
			}
			return dictionary;
		}

		public static uint[]? GetUiTextureHashes(uint presentation, int elementCount)
		{
			if (!TryGetAbiVersion().HasValue || !IsGuestUp || elementCount <= 0)
			{
				return null;
			}
			uint[] array = new uint[elementCount];
			uint num;
			try
			{
				num = RefiiHostGetUiTextureHashes(presentation, array, (uint)array.Length);
			}
			catch (EntryPointNotFoundException)
			{
				return null;
			}
			return (num == (uint)elementCount) ? array : null;
		}

		[DllImport("refii_hostbridge")]
		private static extern uint RefiiHostGetEntityLocomotion(uint entity, uint[] outWords, uint wordCount, byte[] outName, uint nameCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityAnimState(uint entity, [Out] uint[] outWords, uint wordCapacity, out uint outFlags);

	[DllImport(Library)]
	private static extern uint RefiiHostGetTopBoxBody(
		[Out] uint[] outWords, uint capacity, [Out] byte[] outText, uint textCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetBoneMorph(uint entity, [Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAnimPostOps(uint entity, [Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityPartHides(uint entity, [Out] byte[] outText, uint textCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityWornItems(uint entity, [Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAnimMask(
		uint node, [Out] byte[] outBytes, uint capacity, out uint outSkeletonId);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAttachments(
		[Out] uint[] outWords, uint maxRecords, [Out] byte[] outNames);

		[DllImport("refii_hostbridge")]
		private static extern int RefiiHostGetInGameScreen(uint[] outWords);

		[DllImport("refii_hostbridge")]
		private static extern int RefiiHostGetUiTextureStats(uint presentation, uint[] outWords);

		[DllImport("refii_hostbridge")]
		private static extern int RefiiHostGetLoadingScreenLayout(uint[] outWords);

		[DllImport("refii_hostbridge")]
		private static extern int RefiiHostGetLoadingScreenText(
			uint[] outWords, byte[] text, uint capacity);

		[DllImport("refii_hostbridge")]
		private static extern int RefiiHostIsGuestUp();
}
