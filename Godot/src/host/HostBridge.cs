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
	public const int ExpectedAbiVersion = 223;

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern uint RefiiHostGetSaveRoot(byte[] output, uint capacity);

	public static string? GetSaveRoot()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp) return null;
		var buffer = new byte[32768];
		return RefiiHostGetSaveRoot(buffer, (uint)buffer.Length) != 0 ? Decode(buffer) : null;
	}

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern ulong RefiiHostGetNewGameState(out uint generation);

	public static (uint Generation, ulong Result) GetNewGameState()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp) return default;
		ulong result = RefiiHostGetNewGameState(out uint generation);
		return (generation, result);
	}

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern uint RefiiHostGetGoldenCockInteraction();

	public static uint GetGoldenCockInteraction() =>
		TryGetAbiVersion() is not null && IsGuestUp ? RefiiHostGetGoldenCockInteraction() : 0u;

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
		// The loaded library cannot change under a running process: the first answer stands.
		// Every guarded accessor asks, thousands of times a frame.
		if (_rawAbiVersion is { } known)
		{
			return known;
		}
		try
		{
			_rawAbiVersion = RefiiHostBridgeAbiVersion();
			return _rawAbiVersion;
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

	private static int? _rawAbiVersion;

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

			// Once a frame: thousands of guarded accessors ask, and the answer moves only when the
			// guest starts or stops.
			long frame = Refii.Core.Engine_FramePhases.FrameIndex;
			if (frame == _guestUpFrame && _guestUp)
			{
				return _guestUp;
			}
			_guestUp = _hasGuestUpProbe == true ? RefiiHostIsGuestUp() == 1 : IsRunning;
			_guestUpFrame = frame;
			return _guestUp;
		}
	}

	private static long _guestUpFrame = -1;
	private static bool _guestUp;

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
	/// <remarks>
	/// <paramref name="Weight"/> is this leaf's share of
	/// its record, already scaled by the blend tree above it; <paramref name="RecordPrevious"/>
	/// and <paramref name="RecordCurrent"/> are the record's OWN weight pair, unscaled, latched
	/// at the tick start and live respectively — the same prev/cur contract as
	/// <paramref name="PreviousTime"/> and <paramref name="Time"/> beside them. The guest's
	/// crossfade factor is `(1 - alpha) * RecordPrevious + alpha * RecordCurrent`
	/// (`sub_82211660` 0x82211B08..0x82211B24, reading 0x18 and 0x14 off the winner record).
	/// </remarks>
	public readonly record struct GuestAnimLeaf(
		uint Key, float Time, float PreviousTime, float Weight, bool Loop, bool Additive,
		int Priority, int Track, uint Uid, float BlendIn, float BlendOut,
		bool Overlay, uint Mask, uint PriorityMask,
		float RecordPrevious, float RecordCurrent, bool Network = false,
		bool IsWinner = false, bool IsRunner = false);

	private const int AnimLeafWords = 12;

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
	private static uint[]? _postOpWords;

	public static GuestPostOps? GetAnimPostOps(uint entity)
	{
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		uint[] words = _postOpWords ??= new uint[13 + 64 * 29];
		System.Array.Clear(words);
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

		// One set of buffers, refilled per call: the result is read at once by its caller.
		int[] aim = _postAim;
		int[] look = _postLook;
		for (int i = 0; i < 5; i++)
		{
			aim[i] = (int)words[2 + i];
		}

		for (int i = 0; i < 6; i++)
		{
			look[i] = (int)words[7 + i];
		}

		List<GuestPostOp> ops = _postOps;
		ops.Clear();
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

			Quaternion[] targets = ops.Count < _postTargets.Length
				? (_postTargets[ops.Count] ??= new Quaternion[6])
				: new Quaternion[6];
			System.Array.Clear(targets);
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

	private static readonly int[] _postAim = new int[5];
	private static readonly int[] _postLook = new int[6];
	private static readonly List<GuestPostOp> _postOps = new();
	private static readonly Quaternion[]?[] _postTargets = new Quaternion[]?[64];

	/// <summary>
	/// An entity's stated draw distance D and fade factor, off its renderable (component 3): the
	/// definition's `MaxDrawDistance` times the guest's live class multiplier, and
	/// `DrawDistanceFadeRangeFactor`. The guest fades the instance out over the last factor of D
	/// and cuts its shadow hard at D. False when the entity carries no renderable.
	/// </summary>
	public static bool GetEntityDrawRange(uint entity, out float distance, out float fadeFactor)
	{
		distance = 0f;
		fadeFactor = 0f;
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostGetEntityDrawRange(entity, out distance, out fadeFactor) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	private static bool _lightZoneMissing;
	private static bool _casterFlagsMissing;

	/// <summary>
	/// An entity's stated sun-shadow casting: its renderable's flag word (0x40 casts nothing, 0x80
	/// no distant cascade) with 0x100 for a caster drawn with culling off. Null when the entity has
	/// no renderable or the bridge does not export it.
	/// </summary>
	public static uint? GetEntityCasterFlags(uint entity)
	{
		if (entity == 0 || _casterFlagsMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			uint flags = RefiiHostGetEntityCasterFlags(entity);
			return flags == 0xFFFFFFFFu ? null : flags;
		}
		catch (EntryPointNotFoundException)
		{
			_casterFlagsMissing = true;
			return null;
		}
	}

	/// <summary>
	/// The lighting zone an entity's geometry is drawn in (a building's own geometry: its
	/// definition hash; anything else: its renderable's zone). 1, outdoors, when the guest is not
	/// up or the native library predates the call.
	/// </summary>
	public static uint GetEntityLightZone(uint entity)
	{
		if (entity == 0 || _lightZoneMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 1u;
		}

		try
		{
			return RefiiHostGetEntityLightZone(entity);
		}
		catch (EntryPointNotFoundException)
		{
			_lightZoneMissing = true;
			return 1u;
		}
	}

	/// <summary>One built part of a composite character: its model hash, its cluster sort
	/// layer, whether it is a body part, and the cluster names it covers.</summary>
	public readonly record struct GuestPartHides(uint Hash, int Layer, bool Body, string[] Covers);

	/// <summary>
	/// The guest's per-part cluster sets and sort layers for a composite entity (component
	/// 17's built vector). A part hides the union of the sets of every other part with a
	/// strictly greater layer. Empty for a single-model entity.
	/// </summary>
	private static byte[]? _partHidesText;

	public static List<GuestPartHides> GetEntityPartHides(uint entity)
	{
		var result = new List<GuestPartHides>();
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return result;
		}

		byte[] text = _partHidesText ??= new byte[8192];
		System.Array.Clear(text);
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

	/// <summary>
	/// The morph record's height factor, 0 when there is none: above 0 the guest scales bone 1's
	/// horizontal translation by it and adds only the offset's vertical lane.
	/// </summary>
	public static float GetBoneMorphHeight(uint entity)
	{
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0f;
		}

		try
		{
			float h = BitConverter.Int32BitsToSingle((int)RefiiHostGetBoneMorphHeight(entity));
			return float.IsFinite(h) ? h : 0f;
		}
		catch (EntryPointNotFoundException)
		{
			return 0f;
		}
	}

	/// <summary>One part's morph targets: the models the guest blends into it and their weights.</summary>
	public readonly record struct GuestPartMorph(uint BaseModel, (uint Model, float Weight)[] Targets);

	private static readonly uint[] _partMorphWords = new uint[24 * (2 + 8 * 2)];

	/// <summary>Run total of part submissions the hook has seen; 0 means it never ran.</summary>
	public static ulong PartMorphStatements { get; private set; }

	/// <summary>Of those, the submissions that listed at least one morph target.</summary>
	public static ulong PartMorphsMorphed { get; private set; }

	/// <summary>
	/// The entity's morphed parts as the guest stated them to <c>sub_82A48B90</c>; a part not
	/// listed draws as its own model. <paramref name="generation"/> moves when the statement did.
	/// </summary>
	public static GuestPartMorph[] GetEntityPartMorphs(uint entity, out uint generation,
		uint? knownGeneration = null, GuestPartMorph[]? knownParts = null)
	{
		generation = 0;
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return System.Array.Empty<GuestPartMorph>();
		}

		uint[] words = _partMorphWords;
		uint written;
		try
		{
			written = RefiiHostGetEntityPartMorphs(
				entity, words, (uint)words.Length, out generation, out ulong statements);
			PartMorphStatements = statements & 0xFFFFFFFFUL;
			PartMorphsMorphed = statements >> 32;
		}
		catch (EntryPointNotFoundException)
		{
			return System.Array.Empty<GuestPartMorph>();
		}

		if (knownParts is not null && knownGeneration == generation)
		{
			return knownParts;
		}

		if (written == 0)
		{
			return System.Array.Empty<GuestPartMorph>();
		}

		var parts = new List<GuestPartMorph>();
		int at = 0;
		while (at + 2 <= written)
		{
			uint model = words[at];
			int count = (int)words[at + 1];
			at += 2;
			if (count <= 0 || at + count * 2 > written)
			{
				break;
			}

			var targets = new (uint, float)[count];
			for (int i = 0; i < count; i++)
			{
				float weight = BitConverter.Int32BitsToSingle((int)words[at + i * 2 + 1]);
				targets[i] = (words[at + i * 2], float.IsFinite(weight) ? weight : 0f);
			}

			at += count * 2;
			parts.Add(new GuestPartMorph(model, targets));
		}

		return parts.ToArray();
	}

	/// <summary>
	/// One layer of a texture composite. Mode: 0 opaque, 1 alpha, 2 additive, 3 blend-factor,
	/// 4 masked. Tint is the guest's bytes B, G, R, A in memory order.
	/// </summary>
	public readonly record struct GuestTextureLayer(
		uint Texture, int Mode, float Weight, bool HasTint, uint Tint, float Brightness, uint Bump = 0);

	/// <summary>
	/// An original texture and the layers the guest composites in its place, base first; or,
	/// with <paramref name="Overlay"/>, the scars and mode-0 morphs it draws over the finished
	/// material whose diffuse is that texture; or, with <paramref name="Wildcard"/>, a swap of
	/// EVERY map of one kind on the entity (`sub_825B7100`: the shadow creature's Spire Rock) —
	/// Original 0 diffuse, 1 specular, 2 normal, one layer.
	/// </summary>
	public readonly record struct GuestTextureComposite(
		uint Original, GuestTextureLayer[] Layers, bool Overlay = false, bool Wildcard = false);

	private const int TextureLayerWords = 7;

	private static readonly uint[] _textureLayerWords = new uint[40 * (2 + 8 * TextureLayerWords)];

	/// <summary>Run total of composites the hook has seen stated; 0 means it never ran.</summary>
	public static ulong TextureLayerStatements { get; private set; }

	/// <summary>
	/// Every texture composite the guest states for the entity (<c>sub_825B38B0</c>).
	/// <paramref name="generation"/> moves when the statement did.
	/// </summary>
	public static GuestTextureComposite[] GetEntityTextureLayers(uint entity, out uint generation)
	{
		generation = 0;
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return System.Array.Empty<GuestTextureComposite>();
		}

		uint[] words = _textureLayerWords;
		uint written;
		try
		{
			written = RefiiHostGetEntityTextureLayers(
				entity, words, (uint)words.Length, out generation, out ulong statements);
			TextureLayerStatements = statements;
		}
		catch (EntryPointNotFoundException)
		{
			return System.Array.Empty<GuestTextureComposite>();
		}

		// The generation moves when the statement did, so the same generation at the same
		// length is the same statement: the composites decoded for it last time are returned,
		// not decoded again — the entity reconcile asks every few ticks and held the answer
		// only after comparing lengths, so each ask was a fresh array per composite per entity.
		if (_entityTextureLayers.TryGetValue(entity, out (uint Generation, uint Written, GuestTextureComposite[] Composites) held) &&
			held.Generation == generation && held.Written == written)
		{
			return held.Composites;
		}

		GuestTextureComposite[] decoded = DecodeTextureLayers(words, written);
		_entityTextureLayers[entity] = (generation, written, decoded);
		return decoded;
	}

	private static readonly Dictionary<uint, (uint Generation, uint Written, GuestTextureComposite[] Composites)>
		_entityTextureLayers = new();

	/// <summary>
	/// The layers the guest's own dye builder states for ONE inventory item (worn or not), by
	/// record id, in the same words a character's composites use — so the menu shows a dyed
	/// garment through the very compositor that dresses the hero. Empty for an undyed item.
	/// </summary>
	public static GuestTextureComposite[] GetItemDyeLayers(uint record, out uint generation)
	{
		generation = 0;
		if (record == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return System.Array.Empty<GuestTextureComposite>();
		}

		uint[] words = _textureLayerWords;
		uint written;
		try
		{
			written = RefiiHostGetItemDyeLayers(record, words, (uint)words.Length, out generation);
		}
		catch (EntryPointNotFoundException)
		{
			return System.Array.Empty<GuestTextureComposite>();
		}

		return DecodeTextureLayers(words, written);
	}

	/// <summary>[original, count | kind &lt;&lt; 16, count x layer words] per texture, as the tables write them.</summary>
	private static GuestTextureComposite[] DecodeTextureLayers(uint[] words, uint written)
	{
		if (written == 0)
		{
			return System.Array.Empty<GuestTextureComposite>();
		}

		// Counted first, allocated once: a list grown and then copied was two arrays per decode.
		int total = 0;
		for (int at = 0; at + 2 <= written;)
		{
			int count = (int)(words[at + 1] & 0xFFFF);
			at += 2;
			if (count <= 0 || at + count * TextureLayerWords > written)
			{
				break;
			}

			at += count * TextureLayerWords;
			++total;
		}

		if (total == 0)
		{
			return System.Array.Empty<GuestTextureComposite>();
		}

		var composites = new GuestTextureComposite[total];
		int filled = 0;
		for (int at = 0; filled < total;)
		{
			uint original = words[at];
			int count = (int)(words[at + 1] & 0xFFFF);
			uint kind = words[at + 1] >> 16;
			at += 2;

			var layers = new GuestTextureLayer[count];
			for (int i = 0; i < count; i++)
			{
				int w = at + i * TextureLayerWords;
				layers[i] = new GuestTextureLayer(
					words[w],
					(int)words[w + 1],
					BitConverter.Int32BitsToSingle((int)words[w + 2]),
					words[w + 3] != 0,
					words[w + 4],
					BitConverter.Int32BitsToSingle((int)words[w + 5]),
					words[w + 6]);
			}

			at += count * TextureLayerWords;
			composites[filled++] = new GuestTextureComposite(original, layers, kind == 1u, kind == 2u);
		}

		return composites;
	}

	/// <summary>One bone's skeletal morph: a translation offset and a scale, guest bone index.</summary>
	public readonly record struct GuestBoneMorph(int Bone, Vector3 Offset, Vector3 Scale);

	private const int BoneMorphWords = 7;
	private static readonly uint[] _boneMorphWords = new uint[BoneMorphWords * 256];
	private static readonly List<GuestBoneMorph> _boneMorphs = new();

	/// <summary>
	/// The entity's combined skeletal morph record — what makes a fat villager fat in the rig.
	/// Empty for an entity without a mesh component or a record.
	/// </summary>
	/// <remarks>Fills a shared list, valid until the next call.</remarks>
	public static List<GuestBoneMorph> GetBoneMorph(uint entity)
	{
		List<GuestBoneMorph> result = _boneMorphs;
		result.Clear();
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return result;
		}

		uint[] words = _boneMorphWords;
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

	private static readonly ByteStrings AttachmentDummies = new(Encoding.ASCII);

	/// <summary>
	/// Strings decoded from a host byte buffer, kept across reads. The same bone name or sprite
	/// title arrives every frame, and a fresh string per row per frame was among the largest
	/// allocators in a live trace. One instance per caller: not thread-safe.
	/// </summary>
	private sealed class ByteStrings
	{
		private const int Capacity = 4096;

		private readonly Encoding _encoding;
		private readonly Dictionary<ulong, (byte[] Bytes, string Text)> _known = new();

		public ByteStrings(Encoding encoding) => _encoding = encoding;

		public string Get(byte[] buffer, int at, int length)
		{
			if (length == 0)
			{
				return string.Empty;
			}

			ReadOnlySpan<byte> bytes = buffer.AsSpan(at, length);
			ulong hash = 14695981039346656037UL;
			foreach (byte value in bytes)
			{
				hash = (hash ^ value) * 1099511628211UL;
			}

			if (_known.TryGetValue(hash, out (byte[] Bytes, string Text) known) &&
				bytes.SequenceEqual(known.Bytes))
			{
				return known.Text;
			}

			if (_known.Count >= Capacity)
			{
				_known.Clear();
			}

			string text = _encoding.GetString(bytes);
			_known[hash] = (bytes.ToArray(), text);
			return text;
		}
	}

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
				AttachmentDummies.Get(_attachmentNames, nameAt, length)));
		}

		return into.Count;
	}

	private const int AttachmentGripWords = 18;

	private static uint[]? _gripWords;
	private static readonly List<float[]> _gripMatrices = new();

	private static bool _gripExportMissing;

	/// <summary>
	/// The offset each attached child hangs at, as the guest states it (see
	/// RefiiHostGetAttachmentGrips): record+0x50, the rigid inverse of the child's own grip
	/// dummy, by child, with the attachment's ticket. Row-major, row vectors, translation in row
	/// 3, in the child model's raw space. False when the library predates the export.
	/// </summary>
	public static bool GetAttachmentGrips(Dictionary<uint, (uint Ticket, float[] Matrix)> into)
	{
		into.Clear();
		if (_gripExportMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		_gripWords ??= new uint[MaxAttachments * AttachmentGripWords];
		uint count;
		try
		{
			count = RefiiHostGetAttachmentGrips(_gripWords, MaxAttachments);
		}
		catch (EntryPointNotFoundException)
		{
			_gripExportMissing = true;
			return false;
		}

		for (uint i = 0; i < count; ++i)
		{
			int at = (int)(i * AttachmentGripWords);
			while (_gripMatrices.Count <= (int)i)
			{
				_gripMatrices.Add(new float[16]);
			}

			float[] matrix = _gripMatrices[(int)i];
			for (int k = 0; k < 16; ++k)
			{
				matrix[k] = System.BitConverter.Int32BitsToSingle(unchecked((int)_gripWords[at + 2 + k]));
			}

			into[_gripWords[at]] = (_gripWords[at + 1], matrix);
		}

		return true;
	}

	private static uint[]? _animWords;

	/// <summary>
	/// The guest's stated animation for one rig: the clip leaves of its controller's blend
	/// tree (see RefiiHostGetEntityAnimState). Empty when the entity has no active controller.
	/// <paramref name="flags"/>: bit0 controller active, bit1 a procedural IK component is
	/// present, bit2 the tree is netplay-replicated and was not walked. A leaf's
	/// <see cref="GuestAnimLeaf.Network"/> marks clip times extrapolated from a replica's packet
	/// alpha: their tick-to-tick continuity is not stated.
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
				_animWords[at + 9],
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 10]),
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 11]),
				(f & 8) != 0,
				(f & 16) != 0,
				(f & 32) != 0));
		}

		return (int)leaves;
	}

	/// <summary>One effect's transform AS THE GUEST STATED IT, keyed by particle instance.</summary>
	/// <remarks>
	/// Position and orientation arrive as four lanes each because which lane holds x is a
	/// property of the recompiler's VMX layout, not of the guest. They are named here only once
	/// compared against the walk; do not assume an order.
	/// </remarks>
	public readonly record struct GuestStatedFx(
		uint Instance, uint Sequence,
		float P0, float P1, float P2, float P3,
		float Q0, float Q1, float Q2, float Q3);

	private const int StatedFxWords = 10;

	private static uint[]? _statedFxWords;

	/// <summary>The transforms the guest has stated through the particle hooks this frame.</summary>
	public static int GetStatedFx(List<GuestStatedFx> into)
	{
		into.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		uint[] words = _statedFxWords ??= new uint[StatedFxWords * 512];
		uint rows;
		try
		{
			rows = RefiiHostGetStatedFx(words, (uint)words.Length);
		}
		catch (System.EntryPointNotFoundException)
		{
			return 0;
		}

		for (int i = 0; i < rows; ++i)
		{
			int at = i * StatedFxWords;
			float F(int k) => System.BitConverter.Int32BitsToSingle((int)words[at + k]);
			into.Add(new GuestStatedFx(
				words[at], words[at + 1],
				F(2), F(3), F(4), F(5), F(6), F(7), F(8), F(9)));
		}

		return (int)rows;
	}

	/// <summary>Transform statements the hook has seen; 0 means the hook is not loaded.</summary>
	public static uint StatedFxCount()
	{
		try
		{
			return TryGetAbiVersion() is null ? 0u : RefiiHostGetStatedFxCount();
		}
		catch (System.EntryPointNotFoundException)
		{
			return 0u;
		}
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
	/// The sound the guest states for the movie it last opened: whether it is audible (the
	/// opener's `sound` argument), which Bink track IDS play (its `BinkSetSoundTrack`), and the
	/// linear volume it states for each (`sub_82A41D88`), which can change during playback.
	/// </summary>
	public static bool GetMovieSound(uint[] ids, float[] volumes, out int count)
	{
		count = (int)RefiiHostGetMovieSound(
			out uint sound, ids, volumes, (uint)Math.Min(ids.Length, volumes.Length));
		return sound != 0;
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
	private static extern uint RefiiHostAudioSampleRate();

	[DllImport(Library)]
	private static extern uint RefiiHostAudioSamplesPerFrame();

	[DllImport(Library)]
	private static extern float RefiiHostAudioFrontGain(uint channels);

	[DllImport(Library)]
	private static extern void RefiiHostAudioOutputConfigure(uint speakerPairs, uint mixRate);

	[DllImport(Library)]
	private static extern void RefiiHostAudioOutputDetach();

	[DllImport(Library)]
	private static extern void RefiiHostAudioOutputStats(
		out ulong outFramesPulled, out ulong outUnderrunSteps, out uint outChannels);

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

	/// <summary>The guest's output rate — 48000, stated by its mastering voice.</summary>
	public static uint AudioSampleRate() => RefiiHostAudioSampleRate();

	/// <summary>256 — one frame is 5.333 ms, so 187.5 frames a second.</summary>
	public static uint AudioSamplesPerFrame() => RefiiHostAudioSamplesPerFrame();

	/// <summary>
	/// The linear weight the output stage gives the guest's front channels for a device of
	/// <paramref name="channels"/> (2 folds, 6 passes through). Sound the host decodes itself is
	/// scaled by this so it sits at the level of the guest's mix.
	/// </summary>
	public static float AudioFrontGain(uint channels) => RefiiHostAudioFrontGain(channels);

	/// <summary>
	/// Tells the output stage how many speaker pairs the bus has (Godot's bus channel count: 1
	/// stereo, 2 for 3.1, 3 for 5.1, 4 for 7.1) and the mix rate, and attaches it to the guest's
	/// frame ring. Call before the effect's first mix step, and again if the device changes.
	/// </summary>
	public static void AudioOutputConfigure(uint speakerPairs, uint mixRate) =>
		RefiiHostAudioOutputConfigure(speakerPairs, mixRate);

	/// <summary>Detaches the output stage; the guest's frames are then dropped, not waited for.</summary>
	public static void AudioOutputDetach() => RefiiHostAudioOutputDetach();

	/// <summary>
	/// Frames the effect has pulled, mix steps it found the ring empty on once the guest was
	/// producing, and the channel count it is producing (2 or 6).
	/// </summary>
	public static void AudioOutputStats(out ulong framesPulled, out ulong underrunSteps, out uint channels) =>
		RefiiHostAudioOutputStats(out framesPulled, out underrunSteps, out channels);

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

		// Two identity columns then the value pairs: `2 + ids * 2` words an element.
		int stride = 2 + (ids.Length * 2);
		var words = new uint[elements * stride];
		uint written = RefiiHostGetUiAttributes(
			presentation, ids, (uint)ids.Length, words, (uint)words.Length);

		int rows = (int)Math.Min(written, elements);
		var table = new LiveAttribute[rows, ids.Length];
		var found = new uint[rows];
		var hashes = new uint[rows];
		for (int row = 0; row < rows; ++row)
		{
			int at = row * stride;
			found[row] = words[at];
			hashes[row] = words[at + 1];
			for (int column = 0; column < ids.Length; ++column)
			{
				int slot = at + 2 + (column * 2);
				table[row, column] = new LiveAttribute(words[slot], words[slot + 1] != 0);
			}
		}

		LastAttributeElements = found;
		LastAttributeNameHashes = hashes;
		return table;
	}

	/// <summary>
	/// The element addresses and name hashes behind the last <see cref="GetUiAttributes"/> table,
	/// row for row. The table is still indexed by ORDINAL, which is a join by position across two
	/// separate walks; these let a caller prove that join rather than assume it.
	/// </summary>
	public static uint[] LastAttributeElements { get; private set; } = System.Array.Empty<uint>();

	public static uint[] LastAttributeNameHashes { get; private set; } = System.Array.Empty<uint>();

	/// <summary>One live guest entity, in guest world space.</summary>
	/// <summary>
	/// One entity as the guest states it — including the PREVIOUS transform it interpolates from.
	/// </summary>
	/// <remarks>
	/// `PreviousPosition`/`PreviousRotation` are the guest's own latch: the first act of every
	/// game tick copies each renderable's transform to `obj+0xA0`/`+0xB0` (`sub_8222B2E0`), the
	/// tick then moves it, and the guest draws `prev*(1-alpha) + live*alpha` with a slerp for the
	/// rotation (`sub_822112D8`).
	///
	/// `Interpolate` is `obj+0xC9`, and it is an ENABLE: non-zero means blend, zero means take
	/// the live transform as-is. Set to 1 at construction and cleared only by the Havok
	/// rigid-body component's init, so it reads "interpolate unless physics owns this".
	/// </remarks>
	public readonly record struct GuestEntity(
		Vector3 Position, Quaternion Rotation, uint ModelHash,
		uint Address, ulong UniqueId, uint RecordHash, bool Changed = true,
		Vector3 PreviousPosition = default, Quaternion PreviousRotation = default,
		bool Interpolate = false, uint Highlight = 0x000000FFu,
		EntityAppearance Appearance = default)
	{
		/// <summary>
		/// Whether the guest is drawing a targeting outline on this entity right now.
		///
		/// The off state is a colour, not a flag: `sub_82429BB0` clears the highlight by writing
		/// `{0,0,0,255}` and leaves the flag word alone, so the 0x20 bit at component+144 stays
		/// set after the first colour. `sub_82572C18` submits the shader constant only when the
		/// bytes differ from `dword_8331B158` = 00 00 00 FF; that comparison defines on.
		/// </summary>
		public bool Highlighted => Highlight != 0x000000FFu;

		/// <summary>
		/// The stated outline colour. Taken, never re-derived from the category tables. The lanes
		/// are the guest's: `sub_82572C18` packs `(+98, +97, +96, +99) / 255` (0x82572d8c..
		/// 0x82572e58), so red is the THIRD stated byte and blue the first.
		/// </summary>
		public Color HighlightColour => Color.Color8(
			(byte)(Highlight >> 8), (byte)(Highlight >> 16), (byte)(Highlight >> 24), (byte)Highlight);
	}

	/// <summary>
	/// A renderable's look as the guest's appearance setters stated it — CECGraphicAppearance
	/// (component 3), whose setters all end in `sub_82573318`, hooked in entity_appearance.cpp.
	/// </summary>
	/// <remarks>
	/// Alpha (+112) is what gameplay fades: every creature death of DeathType 5, 6 and 7 ramps it
	/// 1 → 0 before the body is destroyed, anim "ALPHA" tracks drive it, a banshee and a shadow
	/// creature set it. CameraAlpha (+116) is the camera-occlusion fade alone. The guest draws
	/// their product, and draws nothing at all when it is not above 1e-4 or when Drawable (+197)
	/// is clear (`sub_822112D8`, 0x82211364) — except a PRINCIPAL, whom camera fade never hides.
	///
	/// The default is the constructor's (`sub_82571DF8`), so an entity the bridge said nothing
	/// about is fully drawn: `default` of this struct is NOT that, hence <see cref="Constructed"/>.
	/// </remarks>
	public readonly record struct EntityAppearance(
		float Alpha, float CameraAlpha, float Scale, Vector4 TintLanes, uint Flags)
	{
		public static readonly EntityAppearance Constructed = new(1f, 1f, 1f, Vector4.One, DrawableFlag);

		public const uint DrawableFlag = 1u;
		public const uint SaturationFlag = 2u;

		/// <summary>SetAsDrawable (+197). Clear: the guest releases the proxies — no draw, no shadow.</summary>
		public bool Drawable => (Flags & DrawableFlag) != 0u;

		/// <summary>SetSaturationMask (+196).</summary>
		public bool SaturationMask => (Flags & SaturationFlag) != 0u;

		/// <summary>The opacity the guest blends by: `+116 * +112` (`sub_82572A58`).</summary>
		public float Opacity => Alpha * CameraAlpha;

		/// <summary>
		/// Whether the guest draws it at all (`sub_822112D8`): drawable, and the product above
		/// 1e-4 — or, for a principal, the gameplay alpha alone at or above it.
		/// </summary>
		public bool Drawn(bool principal) =>
			Drawable && (Opacity > 1e-4f || (principal && Alpha >= 1e-4f));

		public bool IsConstructed => this == Constructed;
	}

	/// <summary>Each address's stated words from the last walk, for <see cref="GuestEntity.Changed"/>.</summary>
	private static readonly Dictionary<uint, (uint X, uint Y, uint Z, uint Qx, uint Qy, uint Qz, uint Qw, uint Model, uint Record)> _lastStated = new();

	/// <summary>Each address's highlight and appearance words from the last walk, the other half of Changed.</summary>
	private static readonly Dictionary<uint, (uint Highlight, uint A0, uint A1, uint A2, uint A3, uint A4, uint A5, uint A6, uint A7)> _lastStatedLook = new();

	private static int _walksSinceStatedPrune;

	/// <summary>Entities the last walk found changed, and the walk's size; reported.</summary>
	public static int LastWalkChanged { get; private set; }

	public static int LastWalkCount { get; private set; }

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

	/// <summary>
	/// Words per entity: 12 stated, the guest's previous transform and its flag, the highlight,
	/// and eight of appearance (alpha, camera alpha, scale, four tint lanes, flags).
	/// </summary>
	private const int EntityStride = 29;

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
		out uint unknown, out UnknownPlacement[] unknownVtables) =>
		GetEntities(new System.Collections.Generic.List<GuestEntity>(), out unknown, out unknownVtables);

	/// <summary>The walk into a caller-owned list, cleared first: a per-frame walk keeps its list.</summary>
	public static System.Collections.Generic.List<GuestEntity> GetEntities(
		System.Collections.Generic.List<GuestEntity> entities,
		out uint unknown, out UnknownPlacement[] unknownVtables)
	{
		unknown = 0;
		unknownVtables = System.Array.Empty<UnknownPlacement>();
		entities.Clear();
		if (TryGetAbiVersion() is null)
		{
			return entities;
		}

		// One walk at a time: the buffers are shared, and HostEntityWalk calls this off the
		// main thread.
		lock (_entityWalkGate)
		{
		_entityWords ??= new uint[MaxEntities * EntityStride];
		uint[] pairs = _unknownPairs ??= new uint[MaxUnknownVtables * 2];
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

		// CHANGED = any stated word differs from the last walk, bit for bit. The feed skips the
		// placement of an entity whose statement did not move; the walk is where the comparison
		// costs nothing the main thread pays.
		int walkChanged = 0;
		bool prune = ++_walksSinceStatedPrune >= 600;
		HashSet<uint>? seen = prune ? new HashSet<uint>((int)count) : null;
		for (uint i = 0; i < count; ++i)
		{
			int at = (int)(i * EntityStride);
			uint address = _entityWords[at + 8];
			var stated = (_entityWords[at], _entityWords[at + 1], _entityWords[at + 2],
				_entityWords[at + 3], _entityWords[at + 4], _entityWords[at + 5], _entityWords[at + 6],
				_entityWords[at + 7], _entityWords[at + 11]);

			// The look words too — highlight and appearance — so a walk that skips an unchanged
			// entity outright (GameEntities' still-adopted fast path) cannot miss a targeting
			// outline or a fade that arrives without the placement moving.
			var statedLook = (_entityWords[at + 20], _entityWords[at + 21], _entityWords[at + 22],
				_entityWords[at + 23], _entityWords[at + 24], _entityWords[at + 25], _entityWords[at + 26],
				_entityWords[at + 27], _entityWords[at + 28]);
			bool changed = !_lastStated.TryGetValue(address, out var last) || !last.Equals(stated) ||
				!_lastStatedLook.TryGetValue(address, out var lastLook) || !lastLook.Equals(statedLook);
			if (changed)
			{
				_lastStated[address] = stated;
				_lastStatedLook[address] = statedLook;
				++walkChanged;
			}

			seen?.Add(address);
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
				address,
				((ulong)_entityWords[at + 9] << 32) | _entityWords[at + 10],
				_entityWords[at + 11],
				changed,
				new Vector3(
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 12]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 13]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 14])),
				new Quaternion(
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 15]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 16]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 17]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 18])),
				_entityWords[at + 19] != 0u,
				_entityWords[at + 20],
				new EntityAppearance(
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 21]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 22]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 23]),
					new Vector4(
						BitConverter.Int32BitsToSingle((int)_entityWords[at + 24]),
						BitConverter.Int32BitsToSingle((int)_entityWords[at + 25]),
						BitConverter.Int32BitsToSingle((int)_entityWords[at + 26]),
						BitConverter.Int32BitsToSingle((int)_entityWords[at + 27])),
					_entityWords[at + 28])));
		}

		LastWalkChanged = walkChanged;
		LastWalkCount = (int)count;
		if (prune && seen is not null)
		{
			_walksSinceStatedPrune = 0;
			var gone = new List<uint>();
			foreach (uint address in _lastStated.Keys)
			{
				if (!seen.Contains(address))
				{
					gone.Add(address);
				}
			}

			foreach (uint address in gone)
			{
				_lastStated.Remove(address);
				_lastStatedLook.Remove(address);
			}
		}
		}

		return entities;
	}

	private static readonly object _entityWalkGate = new();
	private static uint[]? _unknownPairs;

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

	/// <summary>The QueryBox class — `GUI.DisplayYesNoQuestion` and its two siblings.</summary>
	public const uint DisplayQueryBoxVtable = 0x820AE688u;

	/// <summary>The NumericAdjuster class — `GUI.DisplayNumericAdjusterParams`.</summary>
	public const uint DisplayAdjusterVtable = 0x820AE6A8u;

	/// <summary>
	/// The display box that is up and its class vtable, or null when none is open. See
	/// <c>RefiiHostGetDisplayBox</c>.
	/// </summary>
	public static (uint Box, uint Vtable)? GetDisplayBox()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		uint box = RefiiHostGetDisplayBox(out uint vtable);

		// Stated in TU1 terms, so the vtable constants in this assembly classify it whichever
		// build is running.
		vtable = GuestAddressToTu1(vtable);
		return box == 0 ? null : (box, vtable);
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

	private static bool _spotFrustumsMissing;

	/// <summary>
	/// The spots' receive frustums: out[0] the count, then 66 floats each — the manager entry
	/// index, the plane count, 16 planes (n.xyz, w; inside is dot(n, p) + w &gt;= 0). Returns the
	/// floats written.
	/// </summary>
	public static uint GetSpotFrustums(float[] into)
	{
		if (_spotFrustumsMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetSpotFrustums(into, (uint)into.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_spotFrustumsMissing = true;
			return 0;
		}
	}

	private static bool _fxMeshesMissing;

	/// <summary>Floats per FX mesh in <see cref="GetFxMeshes"/>.</summary>
	public const int FxMeshFloats = 24;

	/// <summary>
	/// The effects' mesh particles the guest states: out[0] the count, then 24 floats each —
	/// descriptor and model hash (bits), flags (0x800 displacement), the twin's world rows (3 x
	/// vec4, translation in w), tint, alpha, distance fade (full within, slope, gone beyond), twin
	/// type. Returns the floats written.
	/// </summary>
	public static uint GetFxMeshes(float[] into)
	{
		if (_fxMeshesMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetFxMeshes(into, (uint)into.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_fxMeshesMissing = true;
			return 0;
		}
	}

	private static bool _quadsMissing;

	/// <summary>
	/// The render scene's displacement quads (the drunk warp): out[0] the count, then 16 floats
	/// each — x0 y0 x1 y1 (pixels), z, rotation, the corners' uv, the guest's screen size. Returns
	/// the floats written, 0 without a scene or the export.
	/// </summary>
	public static uint GetDisplacementQuads(float[] into)
	{
		if (_quadsMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetDisplacementQuads(into, (uint)into.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_quadsMissing = true;
			return 0;
		}
	}

	private static uint[]? _displacementParts;
	private static bool _displacementPartsMissing;

	/// <summary>
	/// A composite character's displacement-only parts (component 17's built records with +0x15,
	/// from GDB DisplacementMeshes): drawn only as distortion. Empty when none.
	/// </summary>
	public static uint[] GetEntityDisplacementParts(uint entity)
	{
		if (_displacementPartsMissing || TryGetAbiVersion() is null)
		{
			return System.Array.Empty<uint>();
		}
		_displacementParts ??= new uint[64];
		uint written;
		try
		{
			written = RefiiHostGetEntityDisplacementParts(entity, _displacementParts, (uint)_displacementParts.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_displacementPartsMissing = true;
			return System.Array.Empty<uint>();
		}
		if (written == 0)
		{
			return System.Array.Empty<uint>();
		}
		var hashes = new uint[written];
		System.Array.Copy(_displacementParts, hashes, written);
		return hashes;
	}

	private static uint[]? _interiorParts;
	private static bool _interiorMissing;

	/// <summary>
	/// The INTERIOR records of a multi-mesh appearance (component 5): the model hashes the guest
	/// marks 0x2000 from the record's GDB `Interior`, which its rain and snow treat as roofs.
	/// Empty when none, or the bridge does not export it.
	/// </summary>
	public static uint[] GetEntityInteriorMeshes(uint entity)
	{
		if (_interiorMissing || TryGetAbiVersion() is null)
		{
			return System.Array.Empty<uint>();
		}
		_interiorParts ??= new uint[64];
		uint written;
		try
		{
			written = RefiiHostGetEntityInteriorMeshes(entity, _interiorParts, (uint)_interiorParts.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_interiorMissing = true;
			return System.Array.Empty<uint>();
		}
		if (written == 0)
		{
			return System.Array.Empty<uint>();
		}
		var hashes = new uint[written];
		System.Array.Copy(_interiorParts, hashes, written);
		return hashes;
	}

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

	/// <summary>
	/// Why rigs are not clip-driven: no component bit, component unresolved, controller found,
	/// controller found but empty — and rigs a linked animation posed. See RefiiHostGetAnimWalkStats.
	/// </summary>
	public static (uint NoBit, uint Unresolved, uint Found, uint Empty, uint Linked)? AnimWalkStats()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		// Five words from a bridge that counts linked animations, four from one that does not.
		_animStatWords ??= new uint[5];
		uint words = RefiiHostGetAnimWalkStats(_animStatWords, 5u);
		return words >= 4u
			? (_animStatWords[0], _animStatWords[1], _animStatWords[2], _animStatWords[3],
				words >= 5u ? _animStatWords[4] : 0u)
			: null;
	}

	private static uint[]? _animStatWords;

	/// <summary>
	/// The two cameras the guest blends between — the live one and the one it latched at the
	/// start of the current game tick — or null when the chain is not up.
	/// </summary>
	/// <remarks>
	/// `sub_8228FEF0` blends `game+0x10` to the live block by the same alpha register
	/// `sub_822112D8` gets for every renderable. `Live.Flag` is the guest's snap override: when
	/// set, the blend is skipped and the live camera stands (a cut).
	/// </remarks>
	public static (GuestCamera Previous, GuestCamera Live)? GetCameraPair()
	{
		_cameraPairValues ??= new float[22];
		float[] values = _cameraPairValues;
		if (RefiiHostGetCameraPair(values) == 0)
		{
			return null;
		}

		static GuestCamera At(float[] v, int from) => new(
			new Vector3(v[from], v[from + 1], v[from + 2]),
			new Vector3(v[from + 3], v[from + 4], v[from + 5]),
			new Vector3(v[from + 6], v[from + 7], v[from + 8]),
			v[from + 9],
			v[from + 10] != 0f);

		return (At(values, 11), At(values, 0));
	}

	private static float[]? _cameraPairValues;

	/// <summary>
	/// One player's own live camera (1 or 2), or null when that player has no camera manager —
	/// local player two has one only on split screen. Not blended by the guest: the caller keeps
	/// its own timeline, as it does for player one's.
	/// </summary>
	public static GuestCamera? GetPlayerCamera(int player)
	{
		if (player != 1 && player != 2)
		{
			return null;
		}

		_playerCameraValues ??= new float[11];
		float[] v = _playerCameraValues;
		if (RefiiHostGetPlayerCamera(player, v) == 0)
		{
			return null;
		}

		return new GuestCamera(
			new Vector3(v[0], v[1], v[2]),
			new Vector3(v[3], v[4], v[5]),
			new Vector3(v[6], v[7], v[8]),
			v[9],
			v[10] != 0f);
	}

	private static float[]? _playerCameraValues;

	/// <summary>
	/// The player who owns the open in-game GUI (1 or 2) — the guest's own owner of its pause and
	/// what opens from it, sampled every guest tick. Meaningful while a menu is open.
	/// </summary>
	public static int GetMenuOwner() => TryGetAbiVersion() is null ? 1 : RefiiHostGetMenuOwner();

	/// <summary>
	/// The players a region exit refused since the last call because their partner was out of
	/// range (bit 0 player one, bit 1 player two) — the co-op travel rule, once per approach.
	/// Read-and-clear.
	/// </summary>
	public static int TakeExitRefused() => TryGetAbiVersion() is null ? 0 : RefiiHostTakeExitRefused();

	/// <summary>
	/// Online: this machine's player stands at a region exit waiting for the partner (the co-op
	/// travel rule) — restated to the peer, which tells its player.
	/// </summary>
	public static bool ExitWaiting() => TryGetAbiVersion() is not null && IsGuestUp && RefiiHostGetExitWaiting() != 0;

	/// <summary>
	/// The online joiner's newest region-exit use: a sequence (0 = none yet) and the exit's guest
	/// position. Restated to the host, which fires the exit standing there.
	/// </summary>
	public static (uint Sequence, float X, float Y, float Z) ExitRequest()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return (0u, 0f, 0f, 0f);
		}

		float[] at = _exitRequestAt ??= new float[3];
		uint sequence = RefiiHostGetExitRequest(at);
		return (sequence, at[0], at[1], at[2]);
	}

	private static float[]? _exitRequestAt;

	/// <summary>
	/// The things whose use prompt answers player two this frame — the nearer player's, in co-op
	/// (coop_hero_hooks.cpp) — into <paramref name="into"/>; returns the count. A pooled world
	/// icon's <see cref="WorldSprite.Entity"/> is its prompted thing.
	/// </summary>
	public static int GetPlayerTwoPrompts(uint[] into) =>
		TryGetAbiVersion() is null || !IsGuestUp ? 0 : RefiiHostGetPlayerTwoPrompts(into, into.Length);

	/// <summary>The things whose use prompt only player two sees this frame (player one's half hides them).</summary>
	public static int GetPlayerOneHiddenPrompts(uint[] into) =>
		TryGetAbiVersion() is null || !IsGuestUp ? 0 : RefiiHostGetPlayerOneHiddenPrompts(into, into.Length);

	/// <summary>Player two's own hold-to-use meter: the fraction 0..1 and whether they hold A.</summary>
	public static (float Fraction, bool Pressed) GetPlayerTwoHold()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return (0f, false);
		}

		try
		{
			RefiiHostGetPlayerTwoHold(out float fraction, out int pressed);
			return (fraction, pressed != 0);
		}
		catch (EntryPointNotFoundException)
		{
			return (0f, false);
		}
	}

	private static readonly float[] _holdPromptAt = new float[3];
	private static readonly byte[] _holdPromptTitle = new byte[256];

	/// <summary>
	/// PLAYER TWO'S OWN HOLD PROMPT in local co-op: the guest keeps one hold prompt, player one's;
	/// player two's is kept beside it (coop_hero_hooks.cpp). Its guest world point and title, or
	/// null when they have none.
	/// </summary>
	public static (Vector3 Anchor, string Title)? GetPlayerTwoHoldPrompt()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			if (RefiiHostGetPlayerTwoHoldPrompt(_holdPromptAt, _holdPromptTitle, (uint)_holdPromptTitle.Length) == 0)
			{
				return null;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		int length = Array.IndexOf(_holdPromptTitle, (byte)0);
		string title = Encoding.UTF8.GetString(_holdPromptTitle, 0, length < 0 ? _holdPromptTitle.Length : length);
		return (new Vector3(_holdPromptAt[0], _holdPromptAt[1], _holdPromptAt[2]), title);
	}

	private static readonly byte[] _promptTitles = new byte[8192];

	/// <summary>
	/// Local co-op: the use-prompt text the player the guest's prompt is NOT for would be shown,
	/// per thing — (thing, player 1 or 2, title). Read into <paramref name="into"/>, cleared first.
	/// </summary>
	public static void GetPromptTitles(Dictionary<uint, (int Player, string Title)> into)
	{
		into.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		uint written;
		try
		{
			written = RefiiHostGetPromptTitles(_promptTitles, (uint)_promptTitles.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return;
		}

		if (written == 0 || written >= _promptTitles.Length)
		{
			return;
		}

		foreach (string line in Encoding.UTF8.GetString(_promptTitles, 0, (int)written).Split('\n'))
		{
			string[] parts = line.Split('\t');
			if (parts.Length == 3 && uint.TryParse(parts[0], out uint thing) && int.TryParse(parts[1], out int player))
			{
				into[thing] = (player, parts[2]);
			}
		}
	}

	/// <summary>
	/// One of a player's properties: the building's UID, its BuildingName tag, flags (bit 0 marital
	/// home, 1 rented out, 2 dungeon, 3 home, 4 tenants, 5 workers, 6 shared with the holder's
	/// partner), rent per drop, value, condition 0..1, a full repair's bill and what it was bought for.
	/// </summary>
	public readonly record struct Property(
		ulong Uid, string NameTag, uint Flags, int Income, int Value, float Condition, int RepairCost,
		int BoughtFor = 0)
	{
		public bool IsMarital => (Flags & 1u) != 0u;
		public bool HasWorkers => (Flags & 32u) != 0u;
		public bool IsHome => (Flags & 8u) != 0u;
		public bool IsRented => (Flags & 2u) != 0u;
		public bool HasTenants => (Flags & 16u) != 0u;
		public bool IsShared => (Flags & 64u) != 0u;
	}

	private static readonly byte[] _properties = new byte[32768];

	/// <summary>A player's properties (1 or 2; 0 the player whose menus are open).</summary>
	public static List<Property> GetProperties(int player)
	{
		var list = new List<Property>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return list;
		}

		uint written;
		try
		{
			written = RefiiHostGetProperties(player, _properties, (uint)_properties.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return list;
		}

		if (written == 0 || written >= _properties.Length)
		{
			return list;
		}

		foreach (string line in Encoding.UTF8.GetString(_properties, 0, (int)written).Split('\n'))
		{
			string[] p = line.Split('\t');
			if (p.Length >= 7 &&
				ulong.TryParse(p[0], System.Globalization.NumberStyles.HexNumber, null, out ulong uid) &&
				uint.TryParse(p[2], out uint flags) && int.TryParse(p[3], out int income) &&
				int.TryParse(p[4], out int value) &&
				float.TryParse(p[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float condition) &&
				int.TryParse(p[6], out int cost))
			{
				int bought = p.Length >= 8 && int.TryParse(p[7], out int paid) ? paid : 0;
				list.Add(new Property(uid, p[1], flags, income, value, condition, cost, bought));
			}
		}

		return list;
	}

	/// <summary>
	/// The oldest property notice: kind 0 disrepair, 1 repaired, 2 not enough gold, 3 nothing to
	/// repair, 6 a purchase shared with the partner (the building's UID); 4 owed gold paid on return,
	/// 5 a sale's half paid to the partner (the gold in <c>Uid</c>).
	/// </summary>
	public static (int Kind, ulong Uid)? TakePropertyNotice()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		try
		{
			return RefiiHostTakePropertyNotice(out int kind, out ulong uid) != 0 ? (kind, uid) : null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>
	/// The partnership between the two heroes in the game (Fable 3's): 0 none, 1 business partners,
	/// 2 married; null when both heroes are not here with their identities known.
	/// </summary>
	public static int? GetPartnership()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			return RefiiHostGetPartnership(out int kind) != 0 ? kind : null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	private static readonly byte[] _estateIntent = new byte[128];

	/// <summary>
	/// The oldest household action for the co-op peer to run (<c>&lt;action&gt;:&lt;uid&gt;:&lt;value&gt;</c>):
	/// a joiner's estate action, gift, furniture level or bed, or the host's kept gift; null when none.
	/// </summary>
	public static string? TakeEstateIntent()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		try
		{
			uint written = RefiiHostTakeEstateIntent(_estateIntent, (uint)_estateIntent.Length);
			return written == 0 ? null : Encoding.UTF8.GetString(_estateIntent, 0, (int)written);
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>A player's store value (the same on both co-op machines), or null when unset.</summary>
	public static float? GetPlayerValue(int player, string key)
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		try
		{
			return RefiiHostGetPlayerValue(player, key, out float value) != 0 ? value : null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>Writes a SAVED value into a player's store (kept with that player's save).</summary>
	public static bool SetPlayerValue(int player, string key, float value)
	{
		if (TryGetAbiVersion() is null)
		{
			return false;
		}

		try
		{
			return RefiiHostSetPlayerValue(player, key, value) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>The game's FNV-1 of a name: a GDB record's hash (refii_mod_fnv1).</summary>
	public static uint Fnv1(string name)
	{
		uint hash = 0x811C9DC5u;
		foreach (byte c in Encoding.UTF8.GetBytes(name))
		{
			hash = unchecked(hash * 0x01000193u) ^ c;
		}

		return hash;
	}

	/// <summary>How many of an item (by its GDB record name) player <paramref name="player"/>'s hero carries; -1 without the hero.</summary>
	public static int GetItemCount(int player, string record)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return -1;
		}

		try
		{
			return RefiiHostGetItemCount(player, Fnv1(record));
		}
		catch (EntryPointNotFoundException)
		{
			return -1;
		}
	}

	/// <summary>Sets the partnership in both heroes' saved values (0 ends it). False when it could not.</summary>
	public static bool SetPartnership(int kind)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostSetPartnership(kind) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>An entity's network id (the guest's 64-bit UID), or 0.</summary>
	public static ulong GetEntityUid(uint entity)
	{
		if (entity == 0u || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0ul;
		}

		try
		{
			return RefiiHostGetEntityUid(entity);
		}
		catch (EntryPointNotFoundException)
		{
			return 0ul;
		}
	}

	/// <summary>
	/// A villager's opinion of player 1 or 2 by its network id: love, fear, attractiveness,
	/// morality and respect into <paramref name="axes"/> (five). False when it is not found here.
	/// </summary>
	public static bool GetOpinionOfUid(ulong uid, int player, float[] axes)
	{
		if (uid == 0ul || axes.Length < 5 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostGetOpinionOfUid(uid, player, axes) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
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
		Vector3 Translation, Vector3 Rotation, float Scale, int RotationAxis,
		uint Item = 0);

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
		bool Pooled = false, int Slot = -1, uint Object = 0, bool PlayerTwo = false);

	private const int SpriteWords = 8;

	/// <summary>A town of owned houses and admirers holds well over 64 at once (53 measured
	/// with four houses in view), and an icon past the cap is simply never drawn.</summary>
	private const int MaxSprites = 256;

	private static readonly uint[] _spriteWords = new uint[MaxSprites * SpriteWords];
	private static readonly float[] _spriteValues = new float[MaxSprites * 6];
	private static readonly byte[] _spriteText = new byte[32768];
	private static readonly List<WorldSprite> _sprites = new();
	private static readonly ByteStrings SpriteTexts = new(Encoding.UTF8);

	/// <remarks>The list is reused: it is valid until the next call.</remarks>
	public static List<WorldSprite> GetWorldSprites()
	{
		List<WorldSprite> sprites = _sprites;
		sprites.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return sprites;
		}

		uint count;
		try
		{
			count = RefiiHostGetWorldSprites(
				_spriteWords, (uint)_spriteWords.Length, _spriteValues, (uint)_spriteValues.Length,
				_spriteText, (uint)_spriteText.Length, (uint)MaxSprites);
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

			string value = SpriteTexts.Get(_spriteText, start, textAt - start);
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
				Slot: _spriteWords[w + 6] == 0xFFFFFFFFu ? -1 : (int)_spriteWords[w + 6],
				Object: _spriteWords[w + 7],
				PlayerTwo: (flags & 4) != 0));
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
	/// <remarks>
	/// Cached on the words: this is asked several times a frame (`OwnedScreenId`,
	/// `PresentationFor`, the book set) and the map changes only when a screen is constructed.
	/// The guest is still read every call; only the rebuild is skipped, and only when all 36
	/// words match the ones the cached answer was built from.
	/// </remarks>
	public static InGameScreens? GetInGameScreens()
	{
		uint[] words = _screenWords ??= new uint[36];
		if (RefiiHostGetInGameScreen(words) == 0 || GuestAddressToTu1(words[1]) != InGameGuiVtable)
		{
			_screenCache = null;
			_screenWordsSeen = null;
			return null;
		}

		if (_screenWordsSeen is { } seen &&
			System.MemoryExtensions.SequenceEqual<uint>(words, seen))
		{
			return _screenCache;
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

		var built = new InGameScreens((InGameScreen)words[3], owned);
		_screenWordsSeen ??= new uint[36];
		System.Array.Copy(words, _screenWordsSeen, words.Length);
		_screenCache = built;
		return built;
	}

	[System.ThreadStatic]
	private static uint[]? _screenWords;

	[System.ThreadStatic]
	private static uint[]? _screenWordsSeen;

	[System.ThreadStatic]
	private static InGameScreens? _screenCache;

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
	/// This player's role in a co-op session, as the network manager states it: 0 no session,
	/// 1 host, 2 joiner. SOLO READS AS HOST — a single-player game has <c>+52</c> and <c>+53</c>
	/// set — so 1 is "host or solo", not "co-op host". The manager is <c>g_XrnmNetContext</c>
	/// (<c>0x83497490</c>; <c>*(*(0x83496AB8)+12)+140</c> is its counted copy, the fallback);
	/// <c>+52</c> is "in a session", <c>+53</c> host and <c>+54</c> joiner, and the guest's own
	/// joiner-side front-end teardown is gated on exactly <c>+52 &amp;&amp; +54</c>.
	/// </summary>
	public static uint CoopRole => RefiiHostGetCoopRole();

	/// <summary>True when this player joined someone else's session.</summary>
	public static bool IsCoopJoiner => CoopRole == 2u;

	/// <summary>The co-op screen's two share sliders, 0..1. Both 0.5 until somebody moves one.</summary>
	public readonly record struct CoopShare(bool Known, float Gold, float Experience);

	/// <summary>
	/// The shares as the sliders state them: the globals at <c>0x833193D4</c> (gold) and
	/// <c>0x833193D8</c> (experience). THE GLOBALS ARE THE LIVE VALUE — the co-op manager's own
	/// copies at <c>+176</c>/<c>+172</c> sit at zero under a centred pip and read as 100/0.
	/// </summary>
	public static CoopShare CoopShares()
	{
		int known = RefiiHostGetCoopShares(out float gold, out float experience);
		return new CoopShare(known != 0, gold, experience);
	}

	/// <summary>The chosen co-op hero save's directory name, or null when none is chosen.</summary>
	public static string? CoopHeroSaveName()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		byte[] buffer = new byte[256];
		uint written = RefiiHostGetCoopHeroSaveName(buffer, (uint)buffer.Length);
		return written == 0 ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(written, (uint)buffer.Length - 1));
	}

	/// <summary>
	/// The co-op hero save the player chose on the co-op screen, as the guest read it: money,
	/// gender (2 = male), morality, purity, renown, fifteen ranks, four experience pools, four
	/// totals. Null until a save is chosen.
	/// </summary>
	public static float[]? CoopHeroSave()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		var values = new float[28];
		return RefiiHostGetCoopHeroSave(values, (uint)values.Length) != 0 ? values : null;
	}

	/// <summary>The hero's face record (component 17) and its tan, fat (0..1) and height (-1..1).</summary>
	public static (uint Face, float Fat, float Height, float Tan)? HeroLooks()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		return RefiiHostGetHeroLooks(out uint face, out float fat, out float height, out float tan) != 0
			? (face, fat, height, tan)
			: null;
	}

	/// <summary>
	/// The net ids of the item entities player 1 or 2 holds on THIS machine (ABI 135; the full
	/// 64-bit uid since 170) — what the host sends so a client attaches the replicas rather than
	/// making its own. Null when the player has no body here; zero for an empty slot.
	/// </summary>
	public static (ulong Melee, ulong Ranged)? CoopHeldUids(uint player)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		return RefiiHostGetCoopHeldUids(player, out ulong melee, out ulong ranged) != 0
			? (melee, ranged)
			: null;
	}

	/// <summary>
	/// The hero's equipped weapons as GDB record ids — the same id space the inventory records
	/// carry — or null when the hero has no weapon set. 0 in a slot means it is empty.
	/// </summary>
	public static (uint Melee, uint Ranged)? HeroWeapons()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		return RefiiHostGetHeroWeapons(out uint melee, out uint ranged) != 0 ? (melee, ranged) : null;
	}

	/// <summary>The co-op session's raw words off the authoritative network manager (ABI 134).</summary>
	public readonly record struct CoopSessionWords(
		bool ManagerPresent, bool Session, bool Host, bool Client, bool Lost, bool ClientJoined,
		uint PeerCount, uint PlayerOne, uint PlayerTwo, bool GameCopyPresent,
		uint GameMode, uint PlayerTwoPad)
	{
		/// <summary>`game+284 == 2` — the guest's own `IsLocalCoopGame` (`sub_82340300`).</summary>
		public bool LocalCoop => GameMode == 2u;
	}

	private static uint[]? _coopSessionWords;

	public static CoopSessionWords? CoopSessionRaw()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		_coopSessionWords ??= new uint[12];
		uint count = RefiiHostGetCoopSession(_coopSessionWords, (uint)_coopSessionWords.Length);
		if (count == 0)
		{
			return null;
		}

		uint[] w = _coopSessionWords;
		return new CoopSessionWords(
			w[0] != 0, w[1] != 0, w[2] != 0, w[3] != 0, w[4] != 0, w[5] != 0, w[6], w[7], w[8],
			w[9] != 0,
			count >= 12 ? w[10] : 0u, count >= 12 ? w[11] : 0xFFFFFFFFu);
	}

	/// <summary>
	/// The two players' live entity addresses (key 1 and key 2: the entity manager's own slots first, the player map second), 0 for
	/// a slot that has no entity. ABI 133 — so the entity feed can name player two's rig.
	/// </summary>
	public static (uint One, uint Two) CoopPlayerEntities()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return (0u, 0u);
		}

		RefiiHostGetCoopPlayerEntities(out uint one, out uint two);
		return (one, two);
	}

	/// <summary>
	/// The clothing screen's (16) preview mannequin entity — spawned by the guest at screen open
	/// in the hero's clothes — or null when the screen is not up.
	/// </summary>
	public static uint? ClothingMannequin()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			uint entity = RefiiHostGetClothingMannequin();
			return entity != 0u ? entity : null;
		}
		catch (System.EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>The co-op screen's own state — see <see cref="CoopScreen"/>.</summary>
	/// <param name="LeftFocus">Player one's focused row (`+44`).</param>
	/// <param name="LeftAccepted">Player one has accepted (`+48`).</param>
	/// <param name="RightFocus">Player two's focused pickbox, 0..2 (`+156`).</param>
	/// <param name="RightAccepted">Player two has accepted (`+160`).</param>
	/// <param name="PickerOpen">Player two's save picker is up (`+304`).</param>
	/// <param name="PickerCursor">The picked save, 0-based (`+392`).</param>
	/// <param name="PickerBaseSlot">The card the first save sits on, 1..3 (`+396`).</param>
	/// <param name="Saves">The picker's cards, in save order: the guest's finished text and whether
	/// the save's header parsed (A is refused on one that did not).</param>
	/// <param name="Closing">The screen is closing (`+268`); it takes no input.</param>
	public readonly record struct CoopScreenState(
		int LeftFocus, bool LeftAccepted, int RightFocus, bool RightAccepted, bool PickerOpen,
		int PickerCursor, int PickerBaseSlot, IReadOnlyList<(string Text, bool Valid)> Saves, bool Closing);

	/// <summary>
	/// The co-op screen's state, or null when screen 24 is not up. <paramref name="withText"/>
	/// reads the picker's card text too, which only matters when the picker opens.
	/// </summary>
	public static CoopScreenState? CoopScreen(bool withText)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		uint[] words = new uint[10];
		byte[]? text = withText ? new byte[8192] : null;
		if (RefiiHostGetCoopScreen(words, (uint)words.Length, text, (uint)(text?.Length ?? 0)) < 10u)
		{
			return null;
		}

		var saves = new List<(string Text, bool Valid)>();
		int at = 0;
		for (int i = 0; i < (int)words[7]; ++i)
		{
			string line = string.Empty;
			if (text is not null && at < text.Length && Array.IndexOf(text, (byte)0, at) is var end and >= 0)
			{
				line = Encoding.UTF8.GetString(text, at, end - at);
				at = end + 1;
			}

			saves.Add((line, i < 32 && (words[8] & (1u << i)) != 0u));
		}

		return new CoopScreenState(
			(int)words[0], words[1] != 0u, (int)words[2], words[3] != 0u, words[4] != 0u,
			(int)words[5], (int)words[6], saves, words[9] != 0u);
	}

	/// <summary>
	/// Both co-op screen figures: player two's henchman (`screen+276`) and the player-one mannequin
	/// the original spawns for the left page (`screen+280`'s preview ticket). An entity is 0 for a
	/// figure not standing; null when screen 24 is not up.
	/// </summary>
	public static (uint Henchman, uint PlayerOne)? CoopFigures()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		uint[] words = new uint[4];
		return RefiiHostGetCoopFigures(words, (uint)words.Length) >= 4u ? (words[0], words[2]) : null;
	}

	/// <summary>
	/// Sends one message to the co-op peer over Refii's own channel — port 0xEF11, carrying host
	/// state the title has no concept of. The guest wire is untouched.
	///
	/// Delivery is unreliable BY DESIGN, so send idempotent STATE ("player two's body is X"),
	/// restated while it matters, never a one-shot event. A lost datagram then costs one
	/// restatement instead of a desync.
	/// </summary>
	public static bool CoopSend(string message)
	{
		if (string.IsNullOrEmpty(message) || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiNetCoopSend(message) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// The HeroState version the pump last APPLIED to a player's body (1 host, 2 joiner), 0 when
	/// none has been, or when the bridge is down. The receiver acks a version only once this
	/// reports it.
	/// </summary>
	public static uint HeroStateApplied(uint player)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0u;
		}

		try
		{
			return RefiiHostGetHeroStateApplied(player);
		}
		catch (DllNotFoundException)
		{
			return 0u;
		}
		catch (EntryPointNotFoundException)
		{
			return 0u;
		}
	}

	/// <summary>
	/// The players' mod values this machine owes the co-op peer (core/player_state.h), one channel
	/// message each; empty when nothing is due. Call every frame: the bridge also learns the save
	/// root here. Send them only inside a session.
	/// </summary>
	public static string[] PlayerStateOutgoing()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return System.Array.Empty<string>();
		}

		uint written;
		try
		{
			written = RefiiHostPlayerStateOutgoing(PlayerStateBuffer, (uint)PlayerStateBuffer.Length);
		}
		catch (DllNotFoundException)
		{
			return System.Array.Empty<string>();
		}
		catch (EntryPointNotFoundException)
		{
			return System.Array.Empty<string>();
		}

		return written == 0 || written > PlayerStateBuffer.Length
			? System.Array.Empty<string>()
			: System.Text.Encoding.UTF8.GetString(PlayerStateBuffer, 0, (int)written)
				.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
	}

	/// <summary>One co-op channel message offered to the player state; true when it was one (taken).</summary>
	public static bool PlayerStateReceive(string message)
	{
		if (!message.StartsWith("ps.", System.StringComparison.Ordinal) || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(message);
		try
		{
			return RefiiHostPlayerStateReceive(bytes, (uint)bytes.Length) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Both players' mod values and their owners as one line, or empty.</summary>
	public static string PlayerStateDescribe()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return string.Empty;
		}

		try
		{
			uint written = RefiiHostPlayerStateDescribe(PlayerStateBuffer, (uint)PlayerStateBuffer.Length);
			return written == 0 || written > PlayerStateBuffer.Length
				? string.Empty
				: System.Text.Encoding.UTF8.GetString(PlayerStateBuffer, 0, (int)written);
		}
		catch (DllNotFoundException)
		{
			return string.Empty;
		}
		catch (EntryPointNotFoundException)
		{
			return string.Empty;
		}
	}

	/// <summary>
	/// The next question a co-op script put to the partner (core/coop_consent.cpp): its ticket,
	/// kind (1 sleep, 2 sex), asking player (1 or 2) and detail (the sleep type); null when none.
	/// </summary>
	[ThreadStatic]
	private static byte[]? _consentText;

	public static (uint Ticket, int Kind, int Asker, int Detail, string Text)? ConsentTake()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			byte[] text = _consentText ??= new byte[256];
			if (RefiiHostConsentTake(out uint ticket, out int kind, out int asker, out int detail, text, (uint)text.Length) == 0)
			{
				return null;
			}

			int length = Array.IndexOf(text, (byte)0);
			return (ticket, kind, asker, detail,
				System.Text.Encoding.UTF8.GetString(text, 0, length < 0 ? text.Length : length));
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

	/// <summary>
	/// The silver keys in the world a player has already taken (core/coop_collectables.cpp): entity
	/// address → mask (bit 0 player one, bit 1 player two). Filled into <paramref name="into"/>.
	/// </summary>
	public static void CollectedKeys(System.Collections.Generic.Dictionary<uint, uint> into)
	{
		into.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		try
		{
			uint pairs = RefiiHostGetCollectedKeys(CollectedKeysBuffer, (uint)CollectedKeysBuffer.Length);
			for (uint i = 0; i < pairs; ++i)
			{
				into[CollectedKeysBuffer[i * 2]] = CollectedKeysBuffer[(i * 2) + 1];
			}
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	private static readonly uint[] CollectedKeysBuffer = new uint[512];

	/// <summary>The partner's answer to a question from <see cref="ConsentTake"/>.</summary>
	public static void ConsentGive(uint ticket, bool yes)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		try
		{
			RefiiHostConsentGive(ticket, yes ? 1 : 0);
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>Staging for the player state's messages; main thread only.</summary>
	private static readonly byte[] PlayerStateBuffer = new byte[16384];

	/// <summary>
	/// Takes the oldest message the peer sent, or null when nothing is waiting. Drain it every
	/// frame — the inbox holds 32 and drops the oldest.
	/// </summary>
	public static string? CoopReceive()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		uint written;
		try
		{
			written = RefiiNetCoopReceive(CoopInbox, (uint)CoopInbox.Length);
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		return written == 0 || written > CoopInbox.Length
			? null
			: System.Text.Encoding.UTF8.GetString(CoopInbox, 0, (int)written);
	}

	/// <summary>The state of Refii's mod sync stream (ABI 217).</summary>
	public enum SyncStreamState
	{
		Idle = 0,
		Connecting = 1,
		Open = 2,
		ClosedByPeer = 3,
		ConnectFailed = 4,
		Overflowed = 5,
	}

	/// <summary>
	/// Opens the mod sync stream to the co-op peer — the guest TCP shim's reliable, ordered
	/// rexnet stream on Refii's own port — for bulk bytes the datagram channel is the wrong shape
	/// for. True when requested or already up; the outcome is <see cref="SyncStreamStatus"/>.
	/// </summary>
	public static bool SyncStreamConnect()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiNetSyncConnect() != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	public static SyncStreamState SyncStreamStatus()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return SyncStreamState.Idle;
		}

		try
		{
			return (SyncStreamState)RefiiNetSyncState();
		}
		catch (DllNotFoundException)
		{
			return SyncStreamState.Idle;
		}
		catch (EntryPointNotFoundException)
		{
			return SyncStreamState.Idle;
		}
	}

	/// <summary>Writes <paramref name="length"/> bytes of <paramref name="data"/> to the open stream.</summary>
	public static bool SyncStreamSend(byte[] data, int length)
	{
		if (length <= 0 || length > data.Length || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiNetSyncSend(data, (uint)length) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Takes received bytes into <paramref name="buffer"/>, in order; the count taken.</summary>
	public static int SyncStreamReceive(byte[] buffer)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		try
		{
			uint taken = RefiiNetSyncReceive(buffer, (uint)buffer.Length);
			return taken > buffer.Length ? 0 : (int)taken;
		}
		catch (DllNotFoundException)
		{
			return 0;
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	public static void SyncStreamClose()
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiNetSyncClose();
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// The co-op inbox staging buffer. Must be LARGER THAN ANY MESSAGE THE KIT SENDS
	/// (WireLimit 1100): a truncated inventory chunk is refused by the decoder.
	/// </summary>
	/// <remarks>
	/// One buffer for the process, since the UI drive drains this every frame. Safe to share:
	/// the string is decoded before returning, and every caller is on Godot's main thread.
	/// </remarks>
	private static readonly byte[] CoopInbox = new byte[4096];

	/// <summary>
	/// The guest's simulation tick — <c>dword_83319650</c>, incremented by the world update.
	/// Compare successive values for INEQUALITY only; the difference is not a count of ticks.
	/// </summary>
	public static uint SimTick => RefiiHostGetSimTick();

	/// <summary>How the guest's time base is driven.</summary>
	public enum TimeBaseMode
	{
		/// <summary>The real host clock. Original behaviour, and it tears under load.</summary>
		Off = 0,

		/// <summary>Cap the advance between reads on a thread, so the guest never falls behind.</summary>
		Paced = 1,

		/// <summary>Time advances only when Godot grants it. The guest's rate becomes Godot's.</summary>
		Driven = 2,
	}

	/// <summary>
	/// Sets how the guest's time base is driven, and reads the counters back.
	/// </summary>
	/// <remarks>
	/// The guest's in-game loop runs its own wall-clock accumulator and, when it falls behind,
	/// advances ONE step and re-bases — discarding the backlog (0x82276FA4). Godot does not gate
	/// that loop. <see cref="TimeBaseMode.Driven"/> steps the guest at the host's rate,
	/// deterministically.
	///
	/// `Watchdog` counts lapses where no grant arrived and time resumed on its own so guest
	/// threads on timed waits did not freeze. It should stay flat while the game is stepping.
	/// </remarks>
	public static (TimeBaseMode Mode, ulong Capped, ulong Granted, ulong Watchdog)? TimeBase(
		TimeBaseMode? set = null)
	{
		try
		{
			int mode = RefiiHostSetTimeBaseMode(
				set is { } wanted ? (int)wanted : -1,
				out ulong capped, out ulong granted, out ulong watchdog);
			return ((TimeBaseMode)mode, capped, granted, watchdog);
		}
		catch (System.DllNotFoundException)
		{
			return null;
		}
		catch (System.EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>
	/// Grants the guest one or more high-frequency steps of time. Driven mode only.
	/// </summary>
	/// <remarks>
	/// The guest's loop takes exactly one step per grant and then yields, so this IS its tick.
	/// Called from the session's fixed-step accumulator.
	/// </remarks>
	public static void AdvanceTimeBase(uint steps)
	{
		try
		{
			RefiiHostAdvanceTimeBase(steps);
		}
		catch (System.DllNotFoundException)
		{
		}
		catch (System.EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// The guest's OWN simulation clock in seconds, and its per-tick counter — or null when the
	/// world is not up.
	/// </summary>
	/// <remarks>
	/// `sub_82276D10` runs a wall-clock accumulator and stores its clock at `game+0x110`
	/// (0x82276F18). It does NOT catch up: more than one step behind, it advances ONE step and
	/// re-bases to now, discarding the lost time (0x82276FA4..0x82276FC0). So the tick rate is
	/// 15/s while the guest keeps up, lower under load, and it resets discontinuously.
	/// </remarks>
	public static (double Seconds, uint Tick)? SimClock()
	{
		try
		{
			return RefiiHostGetSimClock(out double seconds, out uint tick) != 0u
				? (seconds, tick)
				: null;
		}
		catch (System.DllNotFoundException)
		{
			return null;
		}
		catch (System.EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>
	/// Samples the guest's render-frame counter into <see cref="Engine_GuestFrame"/>. Once per
	/// host frame, first thing. Without the export (an older native) the frame is synthesised
	/// from the game tick: the tick's change, then a second frame half a tick later, which is
	/// where the guest's second HF iteration falls.
	/// </summary>
	public static void SampleGuestFrame()
	{
		uint frame = 0u;
		if (!_guestFrameExportMissing && TryGetAbiVersion() is not null)
		{
			try
			{
				frame = RefiiHostGetGuestFrame();
			}
			catch (EntryPointNotFoundException)
			{
				_guestFrameExportMissing = true;
			}
		}

		// THE FRAME'S STAMP, read in the same instant as the frame counter: every sample this
		// frame reads is stamped from this one pair (Engine_TickClock.FrameStamp), never from a
		// read taken later while the guest may already be in its next step.
		uint tick = SimClock() is { } clock ? clock.Tick : 0u;
		Engine_TickClock.PublishGuestGameTick(tick);
		if (!_guestFrameExportMissing)
		{
			Engine_TickClock.PublishGuestFrame(frame, tick);
		}

		if (_guestFrameExportMissing)
		{
			frame = SyntheticGuestFrame();
		}

		// REBASED TO ZERO WHILE THE GUEST IS OUT OF THE GAME. The counter is advanced by one
		// hook only — the 30 Hz HF pass `sub_821A61B0` of the in-game loop — and the front end
		// has no HF pass, so on a front end reached from a game it holds its last value forever
		// and `Changed` never fires again: every guest-frame-gated drive (the presentation
		// sampler and the backdrop with it, the owned books, the menu capture, the predicates)
		// stopped after Quit to Title. First boot only worked because the counter was still
		// zero, which Publish treats as "no guest state to wait for". Latching the base while
		// `byte_83496A5E` is clear gives the second front end exactly the first one's contract:
		// zero until the first HF pass of the next game, counting from there.
		if (!IsInGame)
		{
			_guestFrameBase = frame;
		}

		Engine_GuestFrame.Publish(unchecked(frame - _guestFrameBase));
	}

	private static bool _guestFrameExportMissing;
	private static uint _guestFrameBase;
	private static uint _syntheticTick;
	private static double _syntheticTickUsec;
	private static bool _syntheticHalf;

	private static uint SyntheticGuestFrame()
	{
		uint tick = SimTick;
		if (tick == 0u)
		{
			return 0u;
		}

		double now = Godot.Time.GetTicksUsec();
		if (tick != _syntheticTick)
		{
			_syntheticTick = tick;
			_syntheticTickUsec = now;
			_syntheticHalf = false;
		}
		else if (!_syntheticHalf && now - _syntheticTickUsec >= Engine_TickClock.TickUsec * 0.5)
		{
			_syntheticHalf = true;
		}

		return (tick * 2u) + (_syntheticHalf ? 1u : 0u);
	}

	/// <summary>
	/// Words per effect in <see cref="GetActiveFx"/>'s buffer. MUST match the native
	/// <c>kStride</c> — both sides index one flat buffer and a disagreement is an ABI break the
	/// compiler cannot see.
	/// </summary>
	public const int ActiveFxStride = 22;

	/// <summary>
	/// How many live particle NODES are read in one frame. The guest's vector holds every node,
	/// not every effect, so one spell is several entries.
	///
	/// The node count is `(dword_8349E494 - dword_8349E490)/8` and reaches ~500 in ordinary play.
	/// New effects are appended at the END (`sub_82450DB8` writes at `result[2]` and advances
	/// it) and the guest retires by swap-with-last, so a cap below the live count drops the
	/// newest effects and changes which survive every tick.
	///
	/// The native walk refuses a span above 0x40000 bytes (32768 nodes). The walk iterates the
	/// guest's own count, so the cap costs nothing when the guest holds fewer.
	/// </summary>
	private const uint MaxActiveFx = 4096;

	/// <summary>
	/// Whether the last walk filled the buffer, meaning the guest may have had more nodes than
	/// were read, since the walk stops writing at the cap. Callers must not treat an effect
	/// missing from a saturated walk as retired.
	/// </summary>
	public static bool ActiveFxSaturated { get; private set; }

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
		Vector3[] Rows,

		/// <summary>
		/// The type-vector's synthetic root: its transform is the effect's PLACEMENT, and it
		/// belongs to no effect of its own.
		/// </summary>
		bool Root,

		/// <summary>
		/// The effect instance's intensity, <c>handle+52</c>: 1.0 untouched, driven every tick
		/// by the breadcrumber, baked by the guest into each particle at spawn.
		/// </summary>
		float Intensity,

		/// <summary>
		/// The element's size scale, <c>element+412</c> (Lua SetScale, the spell action's
		/// ParticleScale). Garbage on a node that is not a standard emitter; the consumer
		/// checks the type through the bank first.
		/// </summary>
		float Scale,

		/// <summary>
		/// The element's fade-out fraction, +196/+192 while a kill-with-fade runs, else 1.
		/// </summary>
		float Fade,

		/// <summary>
		/// The entity whose emitter component plays this effect (the vortex entity, the
		/// fireball's shot, a sword's effect entity); 0 for a spell-owned trail or an unknown.
		/// </summary>
		uint Owner = 0,

		/// <summary>The entity the effect's own entity was attached to, or 0.</summary>
		uint Attached = 0);

	/// <summary>
	/// Every particle effect the guest is currently playing, in world space — the only route
	/// runtime effects have to the screen (level ambients come from the GDB). Safe per frame:
	/// the native side re-reads both ends of the guest's vector together and refuses a span
	/// that is not a whole number of elements.
	/// </summary>
	public static System.Collections.Generic.List<ActiveFx> GetActiveFx() =>
		GetActiveFx(new System.Collections.Generic.List<ActiveFx>());

	/// <summary>The live effects into a caller-owned list, cleared first.</summary>
	public static System.Collections.Generic.List<ActiveFx> GetActiveFx(
		System.Collections.Generic.List<ActiveFx> effects)
	{
		effects.Clear();
		if (TryGetAbiVersion() is null)
		{
			return effects;
		}

		_activeFx ??= new uint[MaxActiveFx * ActiveFxStride];
		uint written = RefiiHostGetActiveFx(_activeFx, MaxActiveFx);
		ActiveFxSaturated = written >= MaxActiveFx;
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
				GuestRows(_activeFx, at + 7, RowSlot((int)i)),
				(flags & 8u) != 0,
				Bits(_activeFx[at + 17]),
				Bits(_activeFx[at + 18]),
				Bits(_activeFx[at + 19]),
				_activeFx[at + 20],
				_activeFx[at + 21]));
		}

		return effects;
	}

	private static float Bits(uint raw) => BitConverter.Int32BitsToSingle(unchecked((int)raw));

	/// <summary>
	/// The rows array for list slot i, reused across reads: the record is consumed within the
	/// frame it was read and the array is overwritten at the next read of that slot.
	/// </summary>
	private static readonly List<Vector3[]> _rowSlots = new();

	private static Vector3[] RowSlot(int i)
	{
		while (_rowSlots.Count <= i)
		{
			_rowSlots.Add(new Vector3[3]);
		}

		return _rowSlots[i];
	}

	/// <summary>
	/// Rows 0..2 of the guest's world matrix, normalised, in GUEST axes. The rows carry the
	/// node's scale (<c>sub_8222E678</c> pre-multiplies each by its scale lane), hence the
	/// normalise. Row 0 is the image of the guest's local X, row 1 of Y, row 2 of Z; converting
	/// to Godot's axes is the caller's job.
	/// </summary>
	private static Vector3[] GuestRows(uint[] words, int at, Vector3[] rows)
	{
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

			if (RefiiHostGetTimeOfDay(out float fraction, out int day, out int frozen,
				out int moonPhase) == 0)
			{
				return null;
			}

			return new GuestClock(fraction, day, frozen != 0, moonPhase);
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
	/// <param name="MoonPhase">
	/// The phase the guest states (`Timing.GetMoonPhase`), 0..7 (a frame of the eight-frame
	/// atlas), or -1 when it could not be read.
	/// </param>
	public readonly record struct GuestClock(float Fraction, int DayCount, bool Frozen, int MoonPhase = -1);

	/// <summary>
	/// The guest's live precipitation: the four floats of its BLENDED environment theme that its
	/// own rain and snow draws gate on (0.05 in both). Null until the world is built, and null
	/// again while a value is not a sane float — hold what was last read in that case.
	/// </summary>
	public static GuestWeather? Weather()
	{
		try
		{
			if (!IsGuestUp)
			{
				return null;
			}

			if (RefiiHostGetWeather(out float snowSize, out float snowFallSpeed,
				out float rainSize, out float rainDensity) == 0)
			{
				return null;
			}

			return new GuestWeather(snowSize, snowFallSpeed, rainSize, rainDensity);
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

	/// <summary>Blended theme slots T+0x480, +0x490, +0x4A0, +0x4B0 — see RefiiHostGetWeather.</summary>
	public readonly record struct GuestWeather(
		float SnowSize, float SnowFallSpeed, float RainSize, float RainDensity);

	/// <summary>
	/// One theme the guest has pushed on its environment theme stack: the FNV-1 of its name,
	/// which is its record hash in `environmentthemes.gdb`, and the weight the guest is blending
	/// it at this tick (0..1, rising over the push's blend-in, falling over its fade-out) with
	/// the weight it is heading for.
	/// </summary>
	public readonly record struct GuestThemeLayer(uint RecordHash, float Weight, float Target);

	/// <summary>
	/// The themes on the guest's environment theme stack right now — Time Control's
	/// `EnvThemeSlowTime2`, spell charging, the banshee — see RefiiHostGetThemeStack. Empty at
	/// rest, so a host blending these over its own tone map changes nothing until the guest
	/// states a push.
	/// </summary>
	public static void ThemeStack(System.Collections.Generic.List<GuestThemeLayer> into) =>
		ReadThemeLayers(into, stack: true);

	/// <summary>
	/// The themes scripts have blended in with `EnvironmentTheme.BlendToEnvironmentTheme` — the
	/// Cursed Snowglobe's monochrome, combat failure, XP, the balverine poison — see
	/// RefiiHostGetThemeBlends. The guest folds these as one layer BEFORE the stack. Empty unless
	/// a script states one.
	/// </summary>
	public static void ThemeBlends(System.Collections.Generic.List<GuestThemeLayer> into) =>
		ReadThemeLayers(into, stack: false);

	private static void ReadThemeLayers(System.Collections.Generic.List<GuestThemeLayer> into, bool stack)
	{
		into.Clear();
		try
		{
			if (TryGetAbiVersion() is null || !IsGuestUp)
			{
				return;
			}

			uint[] words = _themeStackWords ??= new uint[ThemeStackMax * 3];
			uint count = stack
				? RefiiHostGetThemeStack(words, ThemeStackMax)
				: RefiiHostGetThemeBlends(words, ThemeStackMax);
			for (int i = 0; i < count; ++i)
			{
				into.Add(new GuestThemeLayer(
					words[i * 3],
					System.BitConverter.Int32BitsToSingle(unchecked((int)words[(i * 3) + 1])),
					System.BitConverter.Int32BitsToSingle(unchecked((int)words[(i * 3) + 2]))));
			}
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	private const uint ThemeStackMax = 16;

	[System.ThreadStatic]
	private static uint[]? _themeStackWords;

	/// <summary>
	/// The address of one component on an entity, by id, or 0 when the entity does not carry it
	/// — the pointer half of <see cref="GetEntityComponents"/>, for the reads that have a
	/// verified layout to apply to it.
	/// </summary>
	public static uint EntityComponentAddress(uint entity, uint id)
	{
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		_componentPairs ??= new uint[MaxComponents * 2];
		uint pairs = RefiiHostGetEntityComponents(entity, _componentPairs, MaxComponents);
		for (int i = 0; i < pairs; ++i)
		{
			if (_componentPairs[i * 2] == id)
			{
				return _componentPairs[(i * 2) + 1];
			}
		}

		return 0;
	}

	/// <summary>
	/// The hero's text suffix as `sub_8230FC40` states it: component 12's word at +12, 2 for
	/// <c>_HM</c> and 1 for <c>_HF</c> (0x8230FDD8..0x8230FE14); empty without a hero or a value.
	/// </summary>
	public static string HeroTextSuffix()
	{
		uint record = EntityComponentAddress(HeroEntityAddress(), 12);
		if (record == 0 || !TryPeekWord(record + 12, out uint sex))
		{
			return string.Empty;
		}

		return sex switch
		{
			2 => "_HM",
			1 => "_HF",
			_ => string.Empty,
		};
	}

	/// <summary>
	/// The brightness the guest's camera holds, `G = 1.4 − 0.8·slider` (0.6..1.4, 1.0 at rest):
	/// the Video page writes it at `S+0x14` (0x829C08A4 commit, plus preview, revert and the
	/// profile restores), the camera publishes it every frame (0x821A6188) and the tonemap takes
	/// `pow(colour, G × 0.45454544)` as c78. Null when no camera is up.
	/// </summary>
	/// <remarks>
	/// Six guest sites write the field and none carries it as an argument, so it is read where
	/// the camera keeps it. Which camera follows `sub_823893C0`: the front end's
	/// (`dword_83496AC0`, `vt[5]` = `+0xC` → `+0x1C`) while `byte_83496AC4` is set, else the
	/// game's (`dword_83496AB8`, `vt[5]` = `+0xC` → `+0x6C`); either way `S = *(R+4)`.
	/// </remarks>
	public static float? GuestBrightness()
	{
		uint frontEnd = PickByBuild(0x83496AC0u, 0x83496928u);
		uint frontEndUp = PickByBuild(0x83496AC4u, 0x83496925u);
		uint game = PickByBuild(0x83496AB8u, 0x83496920u);

		static uint Word(uint address) => TryPeekWord(address, out uint value) ? value : 0u;
		static bool Plausible(uint address) => address >= 0x10000u && address < 0xC0000000u;

		uint aligned = frontEndUp & ~3u;
		int shift = 24 - (int)((frontEndUp & 3u) * 8u);
		bool useFrontEnd = Word(frontEnd) != 0 && ((Word(aligned) >> shift) & 0xFFu) != 0;

		uint root = Word(useFrontEnd ? frontEnd : game);
		if (!Plausible(root))
		{
			return null;
		}

		uint inner = Word(root + 0xC);
		uint wrapper = Plausible(inner) ? Word(inner + (useFrontEnd ? 0x1Cu : 0x6Cu)) : 0u;
		uint camera = Plausible(wrapper) ? Word(wrapper + 4) : 0u;
		if (!Plausible(camera))
		{
			return null;
		}

		float g = System.BitConverter.Int32BitsToSingle((int)Word(camera + 0x14));
		return float.IsFinite(g) && g is >= 0.5f and <= 1.5f ? g : null;
	}

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

	/// <summary>
	/// Name scratch for the per-frame level polls below: three 128-byte names each, kept rather
	/// than allocated per call (the trace put them near the top of the host's steady churn).
	/// Cleared before each call so a shorter name never reads a stale tail. Main thread only,
	/// like every other kept buffer in this file.
	/// </summary>
	public static LevelChange? GetLevelChange()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		byte[] world = _levelWorld ??= new byte[128];
		byte[] level = _levelLevel ??= new byte[128];
		byte[] entrance = _levelEntrance ??= new byte[128];
		System.Array.Clear(world);
		System.Array.Clear(level);
		System.Array.Clear(entrance);
		if (RefiiHostGetLevelChange(world, level, entrance, (uint)level.Length,
				out uint state, out uint travel, out uint fullLoad) == 0)
		{
			return null;
		}

		return new LevelChange(
			Decode(world), Decode(level), Decode(entrance), state, travel, fullLoad != 0);
	}

	private static byte[]? _levelWorld;
	private static byte[]? _levelLevel;
	private static byte[]? _levelEntrance;
	private static byte[]? _currentWorld;
	private static byte[]? _currentLevel;
	private static byte[]? _currentEntrance;

	public static CurrentLevel? GetCurrentLevel()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		byte[] world = _currentWorld ??= new byte[128];
		byte[] level = _currentLevel ??= new byte[128];
		byte[] entrance = _currentEntrance ??= new byte[128];
		System.Array.Clear(world);
		System.Array.Clear(level);
		System.Array.Clear(entrance);
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

		byte[] text = _guiText ??= new byte[64 * 1024];
		byte[] kinds = _guiKinds ??= new byte[4096];
		System.Array.Clear(text);
		System.Array.Clear(kinds);
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

	private static byte[]? _menuText;
	private static byte[]? _menuKinds;
	private static byte[]? _guiText;
	private static byte[]? _guiKinds;
	private static uint[]? _hudTextHashes;
	private static byte[]? _hudTextBytes;

	public static System.Collections.Generic.List<Engine_UiModel.MenuChunk> TakeMenuChunks()
	{
		var chunks = new System.Collections.Generic.List<Engine_UiModel.MenuChunk>();
		if (TryGetAbiVersion() is null)
		{
			return chunks;
		}

		byte[] text = _menuText ??= new byte[128 * 1024];
		byte[] kinds = _menuKinds ??= new byte[8192];
		System.Array.Clear(text);
		System.Array.Clear(kinds);
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
		// ONE list, drained and refilled: the result is valid until the next drain, and the
		// consumer (Engine_UiElementState.Drain) applies it in place and keeps nothing.
		System.Collections.Generic.List<Engine_UiModel.UiElementChange> changes = _elementChanges;
		changes.Clear();
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
		if (changes.Capacity < usable)
		{
			changes.Capacity = usable;
		}

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
	private static readonly System.Collections.Generic.List<Engine_UiModel.UiElementChange> _elementChanges = new();

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

	public static uint PostFrontEndAction(string name) => RefiiHostPostFrontEndAction(name);

	/// <summary>
	/// The load fan's save FILE names in card order (ABI 218): the folder names under the
	/// content root the cards were enumerated from, so index N of <see cref="GetSaveCards"/> and of
	/// <see cref="LoadSave"/> is the save named here at N. Empty when the fan is not up.
	/// </summary>
	public static List<string> GetSaveCardNames()
	{
		var names = new List<string>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return names;
		}

		byte[] buffer = new byte[4096];
		uint count;
		try
		{
			count = RefiiHostGetSaveCardNames(buffer, (uint)buffer.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return names;
		}

		int at = 0;
		for (uint i = 0; i < count && at < buffer.Length; i++)
		{
			int end = Array.IndexOf(buffer, (byte)0, at);
			if (end < 0)
			{
				break;
			}

			names.Add(System.Text.Encoding.UTF8.GetString(buffer, at, end - at));
			at = end + 1;
		}

		return names;
	}

	/// <summary>
	/// Adds the mouse's camera turn since the last post — degrees at the profile's middle camera
	/// sensitivity, right and up positive. The guest hands all of it to the local player's active
	/// camera, through that camera's own ProcessGameAction, and the camera turns it whole in its
	/// next update.
	/// </summary>
	public static void PostMouseLook(float yawDegrees, float pitchDegrees)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		RefiiHostPostMouseLook(yawDegrees, pitchDegrees);
	}

	/// <summary>
	/// States whether the mouse is the player's look device (the device switch). While it is, the
	/// guest's camera it drives keeps its heading as the hero moves and a target lock takes the mouse
	/// as flicks between targets; a pad keeps the shipped camera. Once per frame.
	/// </summary>
	public static void SetMouseLookDevice(bool mouse)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		RefiiHostSetMouseLookDevice(mouse ? 1 : 0);
	}

	/// <summary>
	/// Adds mouse-wheel notches made while the mouse is the camera's, rightward positive: a notch
	/// down +1, up -1. Under a target lock a mod states switches by the wheel, each notch is one
	/// switch to the next target that way; otherwise the guest drops them.
	/// </summary>
	public static void PostMouseWheel(int notches)
	{
		if (notches == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		RefiiHostPostMouseWheel(notches);
	}

	/// <summary>The mouse's account as of the guest's last camera update; see <see cref="GetMouseLook"/>.</summary>
	/// <param name="Sequence">Odd while an update is between taking and stating.</param>
	/// <param name="Tick">The game tick (`game+0xFC`) of the last take.</param>
	/// <param name="Posted">Yaw posted, cumulative, in posted degrees — the same origin as <paramref name="Taken"/>.</param>
	/// <param name="Taken">Posted yaw the camera updates have taken, cumulative.</param>
	/// <param name="Absorbed">Yaw the camera turned for it, cumulative, in the camera's degrees, rightward.</param>
	/// <param name="PostedPitch">Pitch posted, cumulative, posted degrees, upward.</param>
	/// <param name="TakenPitch">Posted pitch the camera updates have taken, cumulative.</param>
	/// <param name="AbsorbedPitch">Pitch the camera turned for it, cumulative, its degrees, upward.</param>
	/// <param name="Factor">The camera's degrees of turn per posted degree.</param>
	/// <param name="Pivot">
	/// Where a turn not yet taken is drawn about: for <see cref="Refii.Core.Engine_MouseLook.ModeOrbit"/>
	/// X is the focus's distance along the level line of sight; for
	/// <see cref="Refii.Core.Engine_MouseLook.ModeAim"/> the point X metres along the camera's level
	/// forward, Y along its level right, Z up.
	/// </param>
	/// <param name="Mode">How a turn not yet taken is drawn (<see cref="Refii.Core.Engine_MouseLook"/> modes).</param>
	/// <param name="Flicks">Target switches the mouse handed to a target lock (flicks and wheel notches), run total.</param>
	/// <param name="PitchLimit">For the aim mode: the camera's elevation bound, degrees.</param>
	/// <param name="Aim">For the aim mode: the ranged aim object whose reticule turns with the view.</param>
	public readonly record struct MouseLookState(
		uint Sequence, uint Tick, double Posted, double Taken, double Absorbed,
		double PostedPitch, double TakenPitch, double AbsorbedPitch, float Factor,
		Vector3 Pivot, int Mode, uint Flicks, float PitchLimit, uint Aim)
	{
		/// <summary>The focus distance along the level line of sight (the orbit mode's pivot).</summary>
		public float PivotMetres => Pivot.X;
	}

	private const int MouseLookValues = 15;

	/// <summary>The mouse's account as of the guest's last camera update, or null without a guest.</summary>
	public static MouseLookState? GetMouseLook()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		var v = new double[MouseLookValues];
		uint sequence = RefiiHostGetMouseLook(v, MouseLookValues);
		return new MouseLookState(
			sequence, (uint)v[0], v[1], v[2], v[3], v[4], v[5], v[6], (float)v[7],
			new Vector3((float)v[8], (float)v[9], (float)v[10]), (int)v[11], (uint)v[12],
			(float)v[13], (uint)v[14]);
	}

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
	/// The same identity commit, with the abilities screen's `+198` byte CLEARED instead of set
	/// — the guest's own Y. `sub_826C2D40` @0x826c2d78 branches on it: set runs `sub_8246C970`
	/// (the PURCHASE), clear opens the discard confirm. `sub_826C2958` case 39 stores 0 there
	/// and case 36 stores 1, so the arm is part of the press, not a constant.
	/// </summary>
	public static uint MenuDiscard(string name, int eventType, uint id) =>
		PostFrontEndAction($"MenuDiscard:{name}:{eventType}:{id}");

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
	/// <summary>
	/// The building entity the guest's own per-frame query finds the HERO standing in, 0 when it
	/// finds none. Null when the guest is not up, the hero is not built, or the guest did not
	/// ask this frame — hold what was last read.
	/// </summary>
	/// <remarks>
	/// The query is `sub_828330C8` (through `sub_821D8508`): a ray 10.5 units down from the
	/// entity, kept when it lands on an entity whose byte +144 has 0x40. `sub_821AA080` runs it
	/// on the hero to scale the light shafts by `flt_833191F8` indoors. Its answer is cached on
	/// the hero's component 128 — a weak reference at +12 (node pointer at +16) stamped at +20
	/// with the frame counter `dword_83319650` — and that cached answer is what is read here.
	/// Hero: `World = *(*(*(*(0x83496AB8)+12)+88)+4)`, node `*(*(World)+108)`, entity `*node`;
	/// component: entity+52 bit 0, slot byte at `*(entity+140)+128`, pointer at
	/// `*(entity+72) + slot*8 + 4` — all as `sub_821AA080` walks them.
	/// </remarks>
	public static uint? HeroBuilding()
	{
		static uint Word(uint at) => at >= 0x10000u && TryPeekWord(at, out uint w) ? w : 0u;

		uint root = Word(GuestAddress(0x83496AB8u));
		uint world = Word(Word(Word(root + 12u) + 88u) + 4u);
		uint hero = Word(Word(Word(world) + 108u));
		return root == 0 || world == 0 ? null : BuildingOf(hero);
	}

	/// <summary>
	/// <see cref="HeroBuilding"/> for player two: the same cached answer on player two's own
	/// component 128, which the guest fills for either player (`sub_821CC460`, gated by
	/// `sub_82232778` on P+104 and P+112, queries the owner's component 128 through
	/// `sub_821D8508`). Null without a player two or an answer this frame.
	/// </summary>
	public static uint? PlayerTwoBuilding()
	{
		(_, uint two) = CoopPlayerEntities();
		return two == 0 ? null : BuildingOf(two);
	}

	/// <summary>The building cached on <paramref name="hero"/>'s component 128 within the last two frames.</summary>
	private static uint? BuildingOf(uint hero)
	{
		static uint Word(uint at) => at >= 0x10000u && TryPeekWord(at, out uint w) ? w : 0u;

		if (hero == 0 || (Word(hero + 52u) & 1u) == 0)
		{
			return null;
		}

		uint slots = Word(hero + 140u);
		uint table = Word(hero + 72u);
		if (slots == 0 || table == 0)
		{
			return null;
		}

		uint component = Word(table + ((Word(slots + 128u) >> 24) * 8u) + 4u);
		if (component == 0)
		{
			return null;
		}

		Span<uint> cache = stackalloc uint[2];
		int cacheCount = Peek(component + 16u, cache);
		uint frame = Word(GuestAddress(0x83319650u));
		if (cacheCount != 2 || frame - cache[1] > 2u)
		{
			return null;
		}

		return cache[0] != 0 ? Word(cache[0]) : 0u;
	}

	/// <summary>Raw guest words — the debug channel's microscope. Empty when unreadable.</summary>
	/// <summary>
	/// The running build's address for one this source names by its TU1 value.
	/// </summary>
	/// <remarks>
	/// TWO EXECUTABLES ARE SUPPORTED and no guest address is shared between them. Source keeps
	/// the TU1 address as the name; the table for the other build is the native side's
	/// (versions/Fable2/guest_addresses.inc). Apply this to LITERALS only: a pointer read from
	/// the guest is already the running build's.
	/// </remarks>
	public static uint GuestAddress(uint tu1Address)
	{
		if (TryGetAbiVersion() is null)
		{
			return tu1Address;
		}

		try
		{
			return RefiiHostGuestAddress(tu1Address);
		}
		catch (EntryPointNotFoundException)
		{
			return tu1Address;
		}
	}

	/// <summary>
	/// The TU1 address for a value READ from the guest, so it can be matched against the TU1
	/// constants this source holds — a vtable, a handler.
	/// </summary>
	public static uint GuestAddressToTu1(uint liveAddress)
	{
		if (TryGetAbiVersion() is null)
		{
			return liveAddress;
		}

		try
		{
			return RefiiHostGuestAddressToTu1(liveAddress);
		}
		catch (EntryPointNotFoundException)
		{
			return liveAddress;
		}
	}

	/// <summary>True when the running game was installed without the title update.</summary>
	public static bool IsUnpatchedBuild
	{
		get
		{
			if (TryGetAbiVersion() is null)
			{
				return false;
			}

			try
			{
				return RefiiHostGuestBuild() == 1;
			}
			catch (EntryPointNotFoundException)
			{
				return false;
			}
		}
	}

	/// <summary>A value that is not an address and differs between the two builds.</summary>
	public static T PickByBuild<T>(T tu1, T unpatched) => IsUnpatchedBuild ? unpatched : tu1;

	[ThreadStatic]
	private static uint[]? _peekScratch;

	public static int Peek(uint addr, Span<uint> into)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || into.IsEmpty || into.Length > 64)
		{
			return 0;
		}

		uint[] scratch = _peekScratch ??= new uint[64];
		try
		{
			uint got = RefiiHostPeek(addr, scratch, (uint)into.Length);
			int count = (int)Math.Min(got, (uint)into.Length);
			scratch.AsSpan(0, count).CopyTo(into);
			return count;
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	public static bool TryPeekWord(uint addr, out uint value)
	{
		Span<uint> one = stackalloc uint[1];
		if (Peek(addr, one) == 1)
		{
			value = one[0];
			return true;
		}

		value = 0u;
		return false;
	}

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

	/// <summary>
	/// The guest's stated blur width for the targeting highlight, in texels of its
	/// half-resolution highlight buffer. 0 when the guest is not up or the word is unreadable.
	/// </summary>
	/// <remarks>
	/// `dword_83318558` is the EXECUTE result of the thickness command and is already clamped:
	/// `sub_822BF690` truncates `flt_83319478 * flt_83496C70` with `fctiwz f13, f0`, then applies
	/// max(x, 1) (`subf r10, r7, r11`) and min(x, 16) (`add r11, r11, r10`). It has exactly two
	/// xrefs — that store at 0x822bf6c8 and the read at 0x82197c08 — so it is the whole of the
	/// guest's width contract, post-clamp. An absolute global, not a struct offset.
	///
	/// Peeked rather than exported through the ABI: one word at one fixed address. MAIN THREAD ONLY — the
	/// post chain runs on the render thread, so the layer samples this in `_Process` and
	/// publishes the result.
	/// </remarks>
	public static int HighlightWidthTexels()
	{
		return TryPeekWord(GuestAddress(0x83318558u), out uint word)
			? (int)Math.Clamp(word, 1u, 16u)
			: 0;
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

	/// <summary>
	/// Brings the profiler up. True when it is running and a profiler may connect.
	/// </summary>
	/// <remarks>
	/// REQUIRED BEFORE ANY ZONE MEANS ANYTHING: the runtime is built with
	/// `TRACY_MANUAL_LIFETIME`, so the client is inert until it is started explicitly. With
	/// `TRACY_ON_DEMAND` also set it collects nothing until the game's own in-process capture
	/// connects, so starting it costs its idle threads and no more.
	/// </remarks>
	public static bool TraceStartup() =>
		TryGetAbiVersion() is not null && RefiiHostTraceStartup() != 0;

	/// <summary>Opens a profiler zone by name, or 0 when the bridge is not up.</summary>
	/// <remarks>
	/// The loose pair behind <see cref="Refii.Core.Engine_FramePhases.ZoneBegin"/>. `TraceZone`
	/// stays for a call site that wants a `using` and nothing else.
	/// </remarks>
	public static ulong TraceZoneBegin(string name) =>
		TryGetAbiVersion() is not null ? RefiiHostTraceZoneBegin(name) : 0UL;

	// ---- hang watchdog ----
	//
	// The bridge's watchdog thread watches the MAIN thread's frame stamp and, when it stops for
	// 8 s, writes exe_dir/logs/refii_hang_<utc>.txt: the main thread's native backtrace, every
	// thread's /proc state, the guest thread table, the frame phase, the last host log lines and
	// the memory numbers — without ptrace, without the debug channel (which that thread serves)
	// and without Godot's logger (whose mutex it may hold). The feeds below are the stamps.

	/// <summary>
	/// False once a watchdog export proved missing from the loaded bridge: the feeds go quiet for
	/// the rest of the run instead of throwing every frame. A diagnostic never takes the game down.
	/// </summary>
	private static bool _watchdogUsable = true;

	private static bool WatchdogReady => _watchdogUsable && TryGetAbiVersion() is not null;

	private static void WatchdogMissing(EntryPointNotFoundException e)
	{
		_watchdogUsable = false;
		GD.PushWarning($"[hang-watchdog] off for this run: the bridge lacks its exports ({e.Message})");
	}

	/// <summary>The main thread's per-frame stamp; the first call starts the watchdog.</summary>
	public static void FrameAlive(long frame)
	{
		if (!WatchdogReady)
		{
			return;
		}

		try
		{
			RefiiHostFrameAlive((ulong)frame);
		}
		catch (EntryPointNotFoundException e)
		{
			WatchdogMissing(e);
		}
	}

	/// <summary>Registers a frame-phase name; its index (1-based), or 0 when the table is full or the bridge is down.</summary>
	public static uint RegisterFramePhase(string name)
	{
		if (!WatchdogReady)
		{
			return 0u;
		}

		try
		{
			return RefiiHostRegisterFramePhase(name);
		}
		catch (EntryPointNotFoundException e)
		{
			WatchdogMissing(e);
			return 0u;
		}
	}

	/// <summary>The main thread entered the phase with this registered index.</summary>
	public static void SetFramePhase(uint index)
	{
		if (index == 0u || !WatchdogReady)
		{
			return;
		}

		try
		{
			RefiiHostSetFramePhase(index);
		}
		catch (EntryPointNotFoundException e)
		{
			WatchdogMissing(e);
		}
	}

	/// <summary>
	/// The host's log file, stated once so a hang report can name it. The watchdog never takes
	/// a per-print feed: Godot's logger chain dispatches into a managed Logger through the C#
	/// glue, which looks StringNames up — a hang vector on the StringName mutex.
	/// </summary>
	public static void SetHostLogPath(string path)
	{
		if (!WatchdogReady)
		{
			return;
		}

		try
		{
			RefiiHostSetHostLogPath(path);
		}
		catch (EntryPointNotFoundException e)
		{
			WatchdogMissing(e);
		}
	}

	/// <summary>What the watchdog has written this run, or null when the bridge is down.</summary>
	public static (uint Reports, string LastPath, double LastStallSeconds, bool Stalled)? HangReport()
	{
		if (!WatchdogReady)
		{
			return null;
		}

		var path = new byte[4096];
		try
		{
			uint reports = RefiiHostHangReport(path, (uint)path.Length, out double seconds, out int stalled);
			return (reports, Decode(path), seconds, stalled != 0);
		}
		catch (EntryPointNotFoundException e)
		{
			WatchdogMissing(e);
			return null;
		}
	}

	public static void TraceZoneEnd(ulong zone)
	{
		if (zone != 0UL)
		{
			RefiiHostTraceZoneEnd(zone);
		}
	}

	public static void TracePlot(string name, double value)
	{
		if (TryGetAbiVersion() is not null)
		{
			RefiiHostTracePlot(name, value);
		}
	}

	/// <summary>
	/// One frame of Godot's GPU pass timestamps onto the Tracy timeline, as GPU zones and
	/// per-pass plots. The sink behind <see cref="Refii.Core.Engine_GpuPasses.Sink"/>;
	/// <paramref name="packedNames"/> is <paramref name="count"/> entries of
	/// <paramref name="nameStride"/> bytes; the GPU times are nanoseconds.
	/// </summary>
	public static void TraceGpuPasses(byte[] packedNames, uint nameStride, long[] cpuTimes, long[] gpuNs, int count)
	{
		if (count >= 2 && TryGetAbiVersion() is not null)
		{
			RefiiHostTraceGpuPasses(packedNames, nameStride, cpuTimes, gpuNs, (uint)count);
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

	/// <summary>Arms a one-shot snapshot of the device constant shadows on a probe's next return.</summary>
	public static bool ProbeSnapshotArm(int slot)
	{
		try
		{
			return TryGetAbiVersion() is not null && RefiiHostProbeSnapshotArm(slot) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>The snapshot: VS c0..c255 then PS c0..c255 (2048 floats), or null before one is taken.</summary>
	public static float[]? ProbeSnapshotRead(int slot)
	{
		var values = new float[2048];
		try
		{
			return TryGetAbiVersion() is not null && RefiiHostProbeSnapshotRead(slot, values, (uint)values.Length) != 0
				? values
				: null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
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
		float[] Attractiveness, float AttractivenessFraction,
		bool DogKnown, int DogCombat, int DogSearching, int DogState, string DogName,
		uint DogNameKey,
		uint[] DogTrickIds,
		int[] Stats, bool StatsKnown,
		float[] SpellDamage,
		uint DogDefinition = 0u,
		uint DogEntity = 0u)
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
	/// One options page as the GUEST holds it while the screen is open: the row its cursor is on
	/// (<see cref="Cursor"/>, -1 before the page has been shown) and each row widget's own state —
	/// a pickbox's index in <see cref="Indices"/>, a slider's 0..1 value in <see cref="Values"/>;
	/// the other kind's slot is noise, the caller knows which rows are which. Pages are in the
	/// screen's order: 0 Game, 1 Controls, 2 Video, 3 Audio. <see cref="Known"/> false when the
	/// screen or the page is not live. See `RefiiHostGetOptionsPage` for the guest's layout.
	/// </summary>
	public readonly record struct GuestOptionsPage(bool Known, int Cursor, int[] Indices, float[] Values)
	{
		public static readonly GuestOptionsPage None =
			new(false, -1, Array.Empty<int>(), Array.Empty<float>());
	}

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

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int AssetIsOverrideDelegate(IntPtr path, UIntPtr length);

	private static AssetIsOverrideDelegate? _assetIsOverride;
	private static Func<string, bool>? _assetOverrideSource;

	/// <summary>
	/// Points the guest's loaders at Godot's. Call after the mount succeeds, with the SAME reader
	/// every Godot loader uses. Pass null on the way out, so a guest read arriving after teardown
	/// gets "no reader" instead of a torn-down runtime.
	/// </summary>
	public static void SetAssetReader(Func<string, byte[]?>? source, Func<string, long>? size = null)
	{
		_assetSource = source;
		_assetSize = size;

		if (TryGetAbiVersion() is null)
		{
			return;
		}

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
	/// Points the guest VFS at Godot's asset override predicate.
	/// When Godot serves a virtual mod overlay (e.g. globals.gdb or book.babel), this answers 1
	/// so native VFS prioritizes Godot's virtual asset over loose files on disk.
	/// </summary>
	public static void SetAssetOverrideQuery(Func<string, bool>? source)
	{
		// Preserve the predicate callback so HostBridge.Initialize() can bind it once ABI is ready.
		_assetOverrideSource = source;

		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			if (source is null)
			{
				RefiiHostSetAssetOverrideQuery(IntPtr.Zero);
				_assetIsOverride = null;
				return;
			}

			_assetIsOverride ??= IsAssetOverrideForGuest;
			RefiiHostSetAssetOverrideQuery(Marshal.GetFunctionPointerForDelegate(_assetIsOverride));
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	private static int IsAssetOverrideForGuest(IntPtr path, UIntPtr length)
	{
		try
		{
			if (_assetOverrideSource is not { } source || path == IntPtr.Zero || length == UIntPtr.Zero)
			{
				return 0;
			}

			string name = Marshal.PtrToStringUTF8(path, (int)length);
			if (string.IsNullOrEmpty(name))
			{
				return 0;
			}

			bool isOverride = source(name);
			if (isOverride || name.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".babel", StringComparison.OrdinalIgnoreCase))
			{
				GD.Print($"[host-bridge] IsAssetOverride('{name}') => {isOverride}");
			}
			return isOverride ? 1 : 0;
		}
		catch (Exception e)
		{
			GD.PushWarning($"[host-assets] the guest's asset override check threw: {e.Message}");
			return 0;
		}
	}

	/// <summary>
	/// Points the guest's HASH lookups at Godot's index — the other half of
	/// <see cref="SetAssetReader"/>, registered separately. Pass null on the way out.
	/// </summary>
	public static void SetAssetNameResolver(Func<uint, string?>? source)
	{
		// Preserve the resolver delegate unconditionally.
		_assetNameSource = source;

		if (TryGetAbiVersion() is null)
		{
			return;
		}

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

	/// <summary>Reads one options page's cursor and row widgets. See <see cref="GuestOptionsPage"/>.</summary>
	public static GuestOptionsPage GetOptionsPage(int page)
	{
		if (page < 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return GuestOptionsPage.None;
		}

		try
		{
			const uint capacity = 8u;
			var indices = new int[capacity];
			var values = new float[capacity];
			uint rows = RefiiHostGetOptionsPage((uint)page, out int cursor, indices, values, capacity);
			if (rows == 0u)
			{
				return GuestOptionsPage.None;
			}

			int count = (int)Math.Min(rows, capacity);
			return new GuestOptionsPage(true, cursor, indices[..count], values[..count]);
		}
		catch (EntryPointNotFoundException)
		{
			return GuestOptionsPage.None;
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
		int Stars,

		/// <summary>The region's first level, the `MinimapInfo` key — see the sampler.</summary>
		string LevelName = "",

		/// <summary>The guest's world/level key for it, compared to the current level's.</summary>
		uint LevelKey = 0,

		/// <summary>The scenario the world names for that key; empty means DefaultScenario.</summary>
		string Scenario = "")
	{
		/// <summary>
		/// Whether this sample is of the region a caller asked for. A `default` sample — what
		/// <see cref="GetWorldMapRegion"/> returns with no guest, and what a caller with no sample
		/// passes — has a NULL name and is a sample of nothing.
		/// </summary>
		public bool IsFor(int wanted) =>
			!string.IsNullOrEmpty(Name) && wanted >= 0 && Index == (uint)wanted;
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
		var levelName = new byte[64];
		var scenario = new byte[64];
		uint total;
		uint index;
		int stars;
		uint levelKey;
		try
		{
			total = RefiiHostGetWorldMapRegion(
				out index, name, (uint)name.Length, distance, (uint)distance.Length,
				travel, (uint)travel.Length, counts, (uint)counts.Length,
				merchants, (uint)merchants.Length, out stars,
				levelName, (uint)levelName.Length, out levelKey, scenario, (uint)scenario.Length);
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
			Utf8(merchants), stars, Utf8(levelName), levelKey, Utf8(scenario));
	}

	/// <summary>
	/// What the world map's minimap page plots from live state — see RefiiHostGetMinimapLive.
	/// <see cref="Known"/> is false before a level stands.
	/// </summary>
	public readonly record struct MinimapLive(
		uint LevelKey, int MinX, int MinY, int MaxX, int MaxY,
		uint Dog, bool HasTrailTarget, Vector3 TrailTarget, bool PrimaryQuestHere, uint Hero,
		uint[] Merchants)
	{
		public bool Known => LevelKey != 0u || MaxX != MinX;

		/// <summary>`max(maxX − minX, maxY − minY)` — the page's projection divisor.</summary>
		public float Extent => System.Math.Max(MaxX - MinX, MaxY - MinY);
	}

	public static MinimapLive GetMinimapLive()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		uint[] words = _minimapWords ??= new uint[13 + 32];
		try
		{
			if (RefiiHostGetMinimapLive(words, (uint)words.Length) == 0u)
			{
				return default;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		static float F(uint word) => System.BitConverter.Int32BitsToSingle(unchecked((int)word));
		int merchantCount = (int)System.Math.Min(words[12], 32u);
		var merchants = new uint[merchantCount];
		System.Array.Copy(words, 13, merchants, 0, merchantCount);
		return new MinimapLive(
			words[0], unchecked((int)words[1]), unchecked((int)words[2]),
			unchecked((int)words[3]), unchecked((int)words[4]),
			words[5], words[6] != 0u, new Vector3(F(words[7]), F(words[8]), F(words[9])),
			words[10] != 0u, words[11], merchants);
	}

	[System.ThreadStatic]
	private static uint[]? _minimapWords;

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
	///
	/// <paramref name="TitleRecord"/> is the hero's current title record (`heroComponent57+92`),
	/// which is how the guest names the owner of a building the hero owns and is not renting
	/// out: `HeroTitleComponent.TextTag` through Babel. <paramref name="Occupant"/> is the
	/// creature name of the tenant, or of the owner when that is not the hero — empty in the
	/// hero-owned case, because the hero has no creature name.
	/// </summary>
	public readonly record struct RealEstate(
		bool Known, uint Record, uint TypeMask, bool Owned, bool HasTenant,
		int Value, int Economy, int Decoration, int Income, int SalePrice, int SellingFees,
		int NotVacant, int OwnerKilled, int HeroOpinion, uint BonusesRecord, string Occupant,
		uint TitleRecord)
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

	/// <summary>
	/// Moves whenever something an open menu lists changes (an ability level, experience, an item,
	/// gold, a drawn weapon, or the pump's own item/ability/shop/repair actions): the open list
	/// re-reads its sources when it does. 0 before the bridge answers.
	/// </summary>
	public static uint UiDataGeneration()
	{
		if (TryGetAbiVersion() is null)
		{
			return 0u;
		}

		try
		{
			return RefiiHostUiDataGeneration();
		}
		catch (EntryPointNotFoundException)
		{
			return 0u;
		}
	}

	/// <summary>Names the property under the world map's cursor (0 for none); the pump samples it.</summary>
	public static void RequestPropertyEstate(ulong uid)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostRequestPropertyEstate(uid);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// Screen 28's numbers for the property <paramref name="uid"/>, sampled while its building's
	/// region is loaded; not <see cref="RealEstate.Known"/> otherwise, or before the pump answers.
	/// </summary>
	public static RealEstate GetPropertyEstate(ulong uid)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || uid == 0ul)
		{
			return default;
		}

		uint[] words = new uint[15];
		var occupant = new byte[128];
		uint known;
		try
		{
			known = RefiiHostGetPropertyEstate(uid, words, (uint)words.Length, occupant, (uint)occupant.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		return known == 0u ? default : new RealEstate(
			true, words[0], words[1], words[2] != 0u, words[3] != 0u,
			(int)words[4], (int)words[5], (int)words[6], (int)words[7], (int)words[8],
			(int)words[9], (int)words[10], (int)words[11], (int)words[12], words[13],
			Utf8(occupant), words[14]);
	}

	/// <summary>Reads screen 28's building. See <see cref="RealEstate"/>.</summary>
	public static RealEstate GetRealEstate()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		uint[] words = new uint[15];
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
			Utf8(occupant), words[14]);
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
		float Position, float Speed, bool HitA, bool HitB, bool Live, int Band)
	{
		/// <summary>
		/// Whether the meter is UP. Not the ON byte alone: `sub_823D1A48`, the path a plain
		/// expression takes, zeroes the mode at its head (0x823d1a70), calls
		/// `TurnOnExpressionMeter` at 0x823d1c1c and then clears that very byte one instruction
		/// later — `*(_BYTE *)a1 = 0` at 0x823d1c20 — before arming through `sub_823D2120`.
		/// So an emote runs with On and Mode both 0 while the guest's own registry-9 toggle says
		/// the meter is open, and every host test on On alone folded it shut.
		///
		/// These four are exactly what the teardown clears together, so their union cannot
		/// outlive the meter: `sub_823D1458` writes +0, +4 and +38 to 0 and drops the latch at
		/// +14 through its two armed bytes (0x823d1474..0x823d157c).
		///
		/// <see cref="Live"/> is `+38` = `+0x26`, the meter's ACTIVE LATCH: raised by
		/// `sub_823D1F98` at 0x823D1FE0 on every fill start, cleared by `TurnOffExpressionMeter`
		/// at 0x823D147C, and read by `sub_823D1A48` at 0x823D1BBC as the gate that skips its own
		/// reset block. On its own it is the most direct answer to "is the meter up".
		/// </summary>
		public bool Up => Known && (On || Mode != 0 || Latched || Live);

		/// <summary>
		/// Whether a pip is travelling. The flags say so; the mode does not.
		/// </summary>
		/// <remarks>
		/// Never gate this on the mode. `sub_823D1A48` — the path a plain expression takes —
		/// ZEROES `meter+4` at 0x823D1A70 and never writes it again. The only writers of the mode
		/// are the Lua natives: `StartExpressionMeterPip` sets 1 or 2 (`sub_82307050`,
		/// `*(meter+4) = (arg3 == 0) + 1`) and `StartBarJobFill` sets 3, used only by
		/// `qj001_jobwoodsman`, `qj002_jobblacksmith` and `qj003_jobbartender`. For the same
		/// reason `IsExpressionMeterActive`, which tests `meter+4 != 0`, reads false for a C++
		/// expression; its active latch is `+0x26` (<see cref="Live"/>) instead.
		///
		/// `sub_823D2120` raises `+0x0C` for the hero and `+0x0D` for a henchman, and seeds the
		/// position and speed behind the `+0x0E` latch.
		///
		/// The latch is part of the gate: `sub_822ACBF8` tests both terms and DESTROYS its draw
		/// item when the pair goes false:
		///
		///     822acc94  if ( *(_BYTE *)a1 || *(_BYTE *)(a1 + 1) ) v8 = 1;   ; +12 || +13
		///     822accb4  if ( v8 && *(_BYTE *)(a1 + 2) )                     ; && +14
		///     822ace74  sub_82A97368(...)                                   ; else destroy
		///
		/// (`a1` is `meter+12`, so `a1+2` is `meter+14`.) `sub_823D1D40` drops the arm bytes one
		/// at a time and only clears the latch once neither remains, so the two disagree for the
		/// window between one player finishing and the other.
		/// </remarks>
		public bool PipRunning => Known && (ArmedA || ArmedB) && Latched;
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

	[ThreadStatic]
	private static uint[]? _topBoxFrontWords;

	public static TopBoxFront GetTopBoxFront()
	{
		uint[] words = _topBoxFrontWords ??= new uint[9];
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

	/// <summary>
	/// The targeted-citizen readout's own record, as `sub_82176808` rebuilds it each HUD tick.
	///
	/// <see cref="Present"/> is the guest's raise BYTE, not a channel heuristic: `sub_823FD8E8`
	/// sets it and `sub_823FD9B0` clears it. <see cref="SlideSet"/> is the opinion axis + 1,
	/// stated VERBATIM on registry 10 — 1 love, 2 fear, 3 ugly.
	///
	/// MARRY AND DIVORCE ARE NOT IN THE ORDER THEY LOOK: the record's +36/+40 publish under
	/// `TargetedDivorce` and +44/+48 under `TargetedMarry` (0x823fd884 / 0x823fd8c4). Both
	/// toggles always publish; only one of the two VALUES does.
	/// </summary>
	public readonly record struct TargetedQuery(
		bool Known, bool Present, uint Entity, bool Gift, float GiftValue, bool Button,
		int SlideSet, bool Divorce, float DivorceValue, bool Marry, float MarryValue,
		float Opinion);

	[ThreadStatic]
	private static uint[]? _targetedQueryWords;

	public static TargetedQuery GetTargetedQuery()
	{
		uint[] words = _targetedQueryWords ??= new uint[12];
		uint count;
		try
		{
			count = RefiiHostGetTargetedQuery(words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		if (count < 12u)
		{
			return default;
		}

		static float Bits(uint value) => BitConverter.Int32BitsToSingle(unchecked((int)value));
		return new TargetedQuery(
			true, words[0] != 0u, words[1], words[3] != 0u, Bits(words[4]), words[5] != 0u,
			unchecked((int)words[6]), words[7] != 0u, Bits(words[8]), words[9] != 0u,
			Bits(words[10]), Bits(words[11]));
	}

	[ThreadStatic]
	private static uint[]? _playerTwoQueryWords;

	/// <summary>
	/// PLAYER TWO'S targeted-citizen readout on split screen, in <see cref="TargetedQuery"/>'s
	/// shape: hero two's own focus target, the guest's gift reads, and the villager's opinion OF
	/// PLAYER TWO. Marry and divorce are hero one's questions in the guest and are never set.
	/// </summary>
	public static TargetedQuery GetPlayerTwoTargetedQuery()
	{
		uint[] words = _playerTwoQueryWords ??= new uint[12];
		uint count;
		try
		{
			count = RefiiHostGetPlayerTwoTargetedQuery(words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		if (count < 12u)
		{
			return default;
		}

		static float Bits(uint value) => BitConverter.Int32BitsToSingle(unchecked((int)value));
		return new TargetedQuery(
			true, words[0] != 0u, words[1], words[3] != 0u, Bits(words[4]), words[5] != 0u,
			unchecked((int)words[6]), words[7] != 0u, Bits(words[8]), words[9] != 0u,
			Bits(words[10]), Bits(words[11]));
	}

	/// <summary>Player two's split-screen HUD as the guest's own builders state it for hero two.</summary>
	/// <param name="Target">The entity the title names, 0 when none.</param>
	/// <param name="Title">The targeted readout's title (`sub_823C87B0`).</param>
	/// <param name="Icons">The d-pad's four icon paths: up, left, down, right; empty for none.</param>
	public readonly record struct PlayerTwoHud(uint Target, string Title, string[] Icons);

	[ThreadStatic]
	private static byte[]? _playerTwoHudText;

	public static PlayerTwoHud? GetPlayerTwoHud()
	{
		byte[] text = _playerTwoHudText ??= new byte[2048];
		uint target = 0u;
		try
		{
			if (RefiiHostGetPlayerTwoHud(ref target, text, (uint)text.Length) == 0u)
			{
				return null;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		int length = Array.IndexOf(text, (byte)0);
		string[] lines = System.Text.Encoding.UTF8.GetString(text, 0, length < 0 ? text.Length : length).Split('\n');
		if (lines.Length < 5)
		{
			return null;
		}

		return new PlayerTwoHud(target, lines[0], new[] { lines[1], lines[2], lines[3], lines[4] });
	}

	[ThreadStatic]
	private static float[]? _expressionMeterValues;

	public static ExpressionMeter GetExpressionMeter()
	{
		float[] values = _expressionMeterValues ??= new float[11];
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

	/// <summary>
	/// A hard mod's published value by key (<c>combat.stamina</c>), or false when its hook has
	/// published none. The hook states the value; this never samples the guest.
	/// </summary>
	public static bool TryGetModValue(string key, out float value)
	{
		value = 0f;
		if (TryGetAbiVersion() is null || string.IsNullOrEmpty(key))
		{
			return false;
		}

		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(key);
		try
		{
			return RefiiHostGetModValue(bytes, (uint)bytes.Length, out value) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Hands a hard mod a setting by key; its hook reads it with refii::mods::Setting.</summary>
	public static void SetModSetting(string key, float value)
	{
		if (TryGetAbiVersion() is null || string.IsNullOrEmpty(key))
		{
			return;
		}

		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(key);
		try
		{
			RefiiHostSetModSetting(bytes, (uint)bytes.Length, value);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	private static byte[]? _modEventBuffer;

	/// <summary>
	/// Every mod event the payload posted since the last drain, oldest first: the kind
	/// (<c>Refii.Mods.ModApi.Events[kind]</c>) and the fields as the bus states them for Lua
	/// (<c>victim=123,damage=4.5,name='x'</c>). Only kinds in the mask set by
	/// <see cref="SetModEventMask"/> are ever posted.
	/// </summary>
	public static List<(int Kind, string Fields)> DrainModEvents()
	{
		var events = new List<(int, string)>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return events;
		}

		_modEventBuffer ??= new byte[64 * 1024];
		try
		{
			for (int round = 0; round < 64; round++)
			{
				uint written = RefiiHostDrainModEvents(_modEventBuffer, (uint)_modEventBuffer.Length);
				if (written == 0)
				{
					break;
				}

				string text = System.Text.Encoding.UTF8.GetString(_modEventBuffer, 0, (int)written);
				foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
				{
					int tab = line.IndexOf('\t');
					if (tab > 0 && int.TryParse(line.AsSpan(0, tab), out int kind))
					{
						events.Add((kind, line[(tab + 1)..]));
					}
				}
			}
		}
		catch (EntryPointNotFoundException)
		{
		}

		return events;
	}

	/// <summary>
	/// Which event kinds the payload posts for <see cref="DrainModEvents"/>; an empty mask posts
	/// nothing. Words 0 and 1 go through the two mask exports, the words past them through the
	/// tick's <c>mask_word</c> command (mod_commands.cpp), so a kind at or past 128 takes effect
	/// on the next guest tick.
	/// </summary>
	public static void SetModEventMask(in Refii.Mods.ModEventMask mask)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostSetModEventMaskHigh(mask.Word(1));
			RefiiHostSetModEventMask(mask.Word(0));
		}
		catch (EntryPointNotFoundException)
		{
		}

		for (int word = 2; word < Refii.Mods.ModEventMask.Words; word++)
		{
			PostModCommand($"mask_word event {word} {mask.Word(word):x}");
		}
	}

	/// <summary>
	/// One edge of a mod's input action ("mod.action", player 1 or 2, pressed or released); the
	/// guest raises the InputAction event on its next tick. Edges only, never per-frame state.
	/// </summary>
	public static void PostModInput(string action, int player, bool pressed)
	{
		if (TryGetAbiVersion() is null || string.IsNullOrEmpty(action) || (player != 1 && player != 2))
		{
			return;
		}

		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(action);
		try
		{
			RefiiHostPostModInput(bytes, (uint)bytes.Length, player, pressed ? 1 : 0);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// A helper call or a query for the guest tick, one line: <c>give_gold 123 50</c>,
	/// <c>query 7 health 123</c> (the grammar is src/vfs/src/hooks/mod_commands.cpp). A query's
	/// reply arrives through <see cref="DrainModEvents"/> under kind 1000 as
	/// <c>id=7,ok=true,current=80,max=120</c>.
	/// </summary>
	public static void PostModCommand(string line)
	{
		if (TryGetAbiVersion() is null || string.IsNullOrEmpty(line))
		{
			return;
		}

		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line);
		try
		{
			RefiiHostPostModCommand(bytes, (uint)bytes.Length);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	private static byte[]? _modErrorBuffer;

	/// <summary>The mods' Lua errors (init and handlers) since the last drain, oldest first.</summary>
	public static List<string> DrainModErrors()
	{
		var errors = new List<string>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return errors;
		}

		_modErrorBuffer ??= new byte[32 * 1024];
		try
		{
			for (int round = 0; round < 8; round++)
			{
				uint written = RefiiHostDrainModErrors(_modErrorBuffer, (uint)_modErrorBuffer.Length);
				if (written == 0)
				{
					break;
				}

				string text = System.Text.Encoding.UTF8.GetString(_modErrorBuffer, 0, (int)written);
				errors.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
			}
		}
		catch (EntryPointNotFoundException)
		{
		}

		return errors;
	}

	/// <summary>
	/// A Godot script verdict handler: called by the payload ON THE GUEST THREAD, inside a seam,
	/// with the event's kind and table text; writes "cancel=1,&lt;field&gt;=&lt;number&gt;,…" into
	/// <c>output</c> and returns its length (0 for no change).
	/// </summary>
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	public delegate int ModVerdictFn(int kind, IntPtr fields, UIntPtr length, IntPtr output, UIntPtr capacity);

	/// <summary>Installs (or with null removes) the verdict handler; keep the delegate alive while installed.</summary>
	public static void SetModVerdictHandler(ModVerdictFn? handler)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostSetModVerdictHandler(handler);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>Which event kinds the verdict handler is asked about; an empty mask asks about none. The words cross as <see cref="SetModEventMask"/>'s do.</summary>
	public static void SetModVerdictMask(in Refii.Mods.ModEventMask mask)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostSetModVerdictMaskHigh(mask.Word(1));
			RefiiHostSetModVerdictMask(mask.Word(0));
		}
		catch (EntryPointNotFoundException)
		{
		}

		for (int word = 2; word < Refii.Mods.ModEventMask.Words; word++)
		{
			PostModCommand($"mask_word verdict {word} {mask.Word(word):x}");
		}
	}

	/// <summary>The guest entity a mod HANDLE stands for (what Lua and Godot scripts hold), or 0.</summary>
	public static uint ModHandleAddress(uint handle)
	{
		if (TryGetAbiVersion() is null || handle == 0)
		{
			return 0;
		}

		try
		{
			return RefiiHostModHandleAddress(handle);
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	/// <summary>Reads the hero's derived state. See <see cref="HeroDerived"/>.</summary>
	public static HeroDerived GetHeroDerived()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		// Twelve words: the bridge fills slots 0..11 and clamps to the capacity given, so a
		// shorter buffer silently drops the trick count and the flags read below.
		var words = new uint[12];
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
			spell,
			words[10],
			words[11]);
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

		// The two buffers are write-only here: this call wants the return value. Per-thread
		// scratch, because two threads asking at once must not share one; see `_poseTransforms`.
		uint[] words = _heroVitalWords ??= new uint[17];
		float[] values = _heroVitalValues ??= new float[20];
		try
		{
			return RefiiHostGetHeroVitals(words, (uint)words.Length, values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	/// <summary>
	/// The hero's companion dog entity address, or 0.
	/// </summary>
	public static uint DogEntityAddress() => GetMinimapLive().Dog;

	[System.ThreadStatic]
	private static uint[]? _heroVitalWords;

	[System.ThreadStatic]
	private static float[]? _heroVitalValues;

	public static HeroVitals GetHeroVitals()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		// Per-thread scratch, not fresh: the HUD asks every frame, and the record built below
		// copies out of the buffers before returning. Matches `_poseTransforms`.
		uint[] words = _heroVitalWords ??= new uint[17];
		float[] values = _heroVitalValues ??= new float[20];
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
		float DaysRemaining, int TaskCount,

		/// <summary>The entity the quest's objective weak-references (`quest+84`), or 0 — what
		/// the minimap plots as an "other quest" marker.</summary>
		uint ObjectiveEntity = 0);

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

	/// <summary>
	/// Whether the trail is following a sale or a family member instead of the primary quest —
	/// the quest log's TrailOverrideInfo at +40. While it is, the world map shows the primary
	/// quest as merely active (`sub_829A3260`). See RefiiHostGetQuestTrailOverride.
	/// </summary>
	public static bool QuestTrailOverridden()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostGetQuestTrailOverride() != 0u;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
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
		const int stride = 9;
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
				(int)words[at + 7],
				words[at + 8]));
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
	/// <param name="Record">The item's GDB record — what the ticket names, read where the price reads it.</param>
	public readonly record struct ShopPrice(bool Known, uint Ticket, int Price, uint Record)
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
			uint known = RefiiHostGetShopPrice(out uint ticket, out int price, out uint record);
			return new ShopPrice(known != 0u, ticket, price, record);
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

	/// <summary>One garment's dye state, by the guest's own test (<c>sub_826CAFA8</c>).</summary>
	public readonly record struct DyeState(bool Slot1, bool Slot2, bool Worn);

	private static readonly uint[] _dyeStateWords = new uint[160 * 2];

	/// <summary>
	/// Which of the hero's garments are dyed, keyed by item record. Sampled on the guest's GUI
	/// tick, so it is current while a book is open; a record not listed has not been sampled.
	/// </summary>
	public static Dictionary<uint, DyeState> GetDyeStates()
	{
		var states = new Dictionary<uint, DyeState>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return states;
		}

		uint found;
		try
		{
			found = RefiiHostGetDyeStates(_dyeStateWords, (uint)_dyeStateWords.Length);
		}
		catch (System.EntryPointNotFoundException)
		{
			return states;
		}

		for (uint i = 0; i < found && (i * 2) + 1 < _dyeStateWords.Length; ++i)
		{
			uint flags = _dyeStateWords[(i * 2) + 1];
			states.TryAdd(_dyeStateWords[i * 2],
				new DyeState((flags & 1u) != 0, (flags & 2u) != 0, (flags & 4u) != 0));
		}

		return states;
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

	/// <summary>
	/// The augment pairs socketed into one inventory record — (PrimaryType, SecondaryType) GDB
	/// record ids, secondary 0 when there is none — off the item's own instance record, which
	/// is what `sub_82469AE8` prints on the weapon page. Null when no record of that id carries
	/// one: the item was never seeded, and the definition's `InitialAugmentations` stand.
	/// </summary>
	public static (uint Primary, uint Secondary)[]? GetItemAugments(uint recordId)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || recordId == 0u)
		{
			return null;
		}

		const int max = 16;
		uint[] words = new uint[max * 2];
		uint found;
		try
		{
			found = RefiiHostGetItemAugments(recordId, words, (uint)words.Length);
		}
		catch (System.DllNotFoundException)
		{
			return null;
		}
		catch (System.EntryPointNotFoundException)
		{
			return null;
		}

		if (found == uint.MaxValue)
		{
			return null;
		}

		var pairs = new (uint, uint)[System.Math.Min(found, max)];
		for (int i = 0; i < pairs.Length; ++i)
		{
			pairs[i] = (words[i * 2], words[(i * 2) + 1]);
		}

		return pairs;
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
	/// The quick spell wheel's cursor as the guest's own widget holds it — see
	/// RefiiHostGetSpellWheel. <see cref="Engine_UiModel.SpellWheelState.Known"/> is false while
	/// the wheel is closed, when its tables are uninitialised heap.
	/// </summary>
	public static Engine_UiModel.SpellWheelState GetSpellWheel(int player)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		uint[] words = _spellWheelWords ??= new uint[53];
		try
		{
			if (RefiiHostGetSpellWheel((uint)player, words, (uint)words.Length) == 0)
			{
				return default;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		return DecodeSpellWidget(words);
	}

	/// <summary>
	/// The abilities book's own spell blade (screen 14 + 200), read while the abilities screen
	/// is up and its blade is open — RefiiHostGetSpellBlade. The same words as the wheel's, so the
	/// one owned widget follows either.
	/// </summary>
	public static Engine_UiModel.SpellWheelState GetSpellBlade()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		uint[] words = _spellWheelWords ??= new uint[53];
		try
		{
			if (RefiiHostGetSpellBlade(words, (uint)words.Length) == 0)
			{
				return default;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		return DecodeSpellWidget(words);
	}

	private static Engine_UiModel.SpellWheelState DecodeSpellWidget(uint[] words)
	{
		var counts = new int[Engine_UiModel.SpellAssignState.Slots];
		var cells = new int[Engine_UiModel.SpellAssignState.Slots];
		var lists = new int[Engine_UiModel.SpellAssignState.Slots][];
		for (int r = 0; r < counts.Length; ++r)
		{
			counts[r] = (int)words[3 + r];
			cells[r] = (int)words[8 + r];
			lists[r] = new int[counts[r]];
			for (int c = 0; c < counts[r]; ++c)
			{
				lists[r][c] = (int)words[13 + (8 * r) + c];
			}
		}

		return new Engine_UiModel.SpellWheelState(
			words[0], (int)words[1], unchecked((int)words[2]), counts, cells, lists);
	}

	[System.ThreadStatic]
	private static uint[]? _spellWheelWords;

	/// <summary>
	/// Puts a will on a d-pad slot, through the guest's own committing wrapper. Commit on
	/// LEFT/RIGHT, not on A: the guest's widget writes the moment the carousel moves
	/// (`sub_826B5330` / `sub_826B5240`), while A copies the current row's will down into every
	/// later row that also offers it.
	///
	/// The writer validates nothing, so only send a pair
	/// <see cref="SpellAssignState.Offers"/> admits.
	/// </summary>
	public static uint AssignSpell(int slot, int spell, int player = 1) =>
		PostFrontEndAction(player == 2 ? $"SpellAssign:{slot}:{spell}:2" : $"SpellAssign:{slot}:{spell}");

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
	/// The locale code of the language the guest is running in ("en-uk", "hu-hu", ...): the
	/// current row of its installed-language table, the code it hands XLip when it switches.
	/// Null when no guest is up, the table is not built yet, or the native library predates it —
	/// the caller keeps the culture it has rather than guessing one.
	/// </summary>
	public static string? GetCurrentCulture()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		var code = new byte[32];
		uint length;
		try
		{
			length = RefiiHostGetCurrentCulture(code, (uint)code.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		return length == 0 || length >= code.Length
			? null
			: System.Text.Encoding.ASCII.GetString(code, 0, (int)length);
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
		GetFrontEndRaw() is { } words && GuestAddressToTu1(words[9]) == FrontEndGuiVtable;

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

	[System.ThreadStatic]
	private static uint[]? _presentationWords;

	[System.ThreadStatic]
	private static byte[]? _presentationNames;

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
		int neededWords = (int)(count * 5 + 32);
		int neededNames = (int)(count * 96 + 512);
		if (_presentationWords is null || _presentationWords.Length < neededWords)
		{
			_presentationWords = new uint[Math.Max(neededWords, 128)];
		}
		if (_presentationNames is null || _presentationNames.Length < neededNames)
		{
			_presentationNames = new byte[Math.Max(neededNames, 2048)];
		}
		uint[] words = _presentationWords;
		byte[] names = _presentationNames;
		uint written = RefiiHostGetPresentations(
			words, (uint)neededWords, names, (uint)neededNames);

		int usable = (int)Math.Min(written, (uint)(neededWords / 5));
		var live = new List<Presentation>(usable);
		for (int i = 0; i < usable; ++i)
		{
			uint offset = words[i * 5 + 2];
			string name = string.Empty;
			if (offset != 0xFFFFFFFFu && offset < neededNames)
			{
				int end = (int)offset;
				while (end < neededNames && names[end] != 0)
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

	[System.ThreadStatic]
	private static uint[]? _uiElementWords;

	[System.ThreadStatic]
	private static ushort[]? _uiElementChars;

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
		int neededWords = (int)(count * 6 + 64);
		// Room for the long strings too: a description is up to 4096 units (the native cap),
		// and text past the buffer's end comes back as no text at all.
		int neededChars = (int)(count * 24 + 16384);
		if (_uiElementWords is null || _uiElementWords.Length < neededWords)
		{
			_uiElementWords = new uint[Math.Max(neededWords, 4096)];
		}
		if (_uiElementChars is null || _uiElementChars.Length < neededChars)
		{
			_uiElementChars = new ushort[Math.Max(neededChars, 65536)];
		}
		uint[] words = _uiElementWords;
		ushort[] chars = _uiElementChars;
		uint written = RefiiHostGetUiElements(
			presentation, words, (uint)neededWords, chars, (uint)neededChars);

		int usable = (int)Math.Min(written, (uint)(neededWords / 6));
		var elements = new List<UiElement>(usable);
		for (int i = 0; i < usable; ++i)
		{
			uint offset = words[i * 6 + 2];
			string? text = null;
			if (offset != 0xFFFFFFFFu && offset < neededChars)
			{
				int end = (int)offset;
				while (end < neededChars && chars[end] != 0)
				{
					++end;
				}

				int len = end - (int)offset;
				text = len == 0
					? string.Empty
					: new string(MemoryMarshal.Cast<ushort, char>(chars.AsSpan((int)offset, len)));
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
	private static extern uint RefiiHostGetMovieSound(
		out uint sound, [Out] uint[] ids, [Out] float[] volumes, uint capacity);

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
	private static extern int RefiiHostGetCameraPair(float[] outValues);

	[DllImport(Library)]
	private static extern void RefiiHostPostMouseLook(float yawDegrees, float pitchDegrees);

	[DllImport(Library)]
	private static extern void RefiiHostSetMouseLookDevice(int mouse);

	[DllImport(Library)]
	private static extern void RefiiHostPostMouseWheel(int notches);

	[DllImport(Library)]
	private static extern uint RefiiHostGetMouseLook(double[] outValues, uint count);

	[DllImport(Library)]
	private static extern int RefiiHostGetPlayerCamera(int player, float[] outValues);

	[DllImport(Library)]
	private static extern int RefiiHostGetMenuOwner();

	[DllImport(Library)]
	private static extern int RefiiHostTakeExitRefused();

	[DllImport(Library)]
	private static extern int RefiiHostGetPlayerTwoPrompts(uint[] outThings, int capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetPlayerOneHiddenPrompts(uint[] outThings, int capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetPlayerTwoHold(out float fraction, out int pressed);

	[DllImport(Library)]
	private static extern int RefiiHostGetPlayerTwoHoldPrompt([Out] float[] at, [Out] byte[] title, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetPromptTitles([Out] byte[] text, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetProperties(int player, [Out] byte[] text, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostTakePropertyNotice(out int kind, out ulong uid);

	[DllImport(Library)]
	private static extern int RefiiHostGetPartnership(out int kind);

	[DllImport(Library)]
	private static extern int RefiiHostGetItemCount(int player, uint definition);

	[DllImport(Library)]
	private static extern int RefiiHostGetPlayerValue(int player, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, out float value);

	[DllImport(Library)]
	private static extern uint RefiiHostTakeEstateIntent(byte[] outBuffer, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostSetPartnership(int kind);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicSceneLights([Out] float[] values, uint capacity);

	[DllImport(Library)]
	private static extern ulong RefiiHostGetEntityUid(uint entity);

	[DllImport(Library)]
	private static extern int RefiiHostGetOpinionOfUid(ulong uid, int player, [Out] float[] axes);

	[DllImport(Library)]
	private static extern int RefiiHostGetExitWaiting();

	[DllImport(Library)]
	private static extern uint RefiiHostGetExitRequest(float[] outXyz);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAnimWalkStats(uint[] outWords, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetUiAttributes(
		uint presentation, uint[] ids, uint idCount, uint[]? outWords, uint outCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFrontEndState();

	[DllImport(Library)]
	private static extern uint RefiiHostIsInGame();

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopRole();

	[DllImport(Library)]
	private static extern int RefiiHostGetCoopShares(out float gold, out float experience);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopHeroSaveName(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetSaveCardNames(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopPlayerEntities(out uint one, out uint two);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopSession(uint[] outWords, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopHeroSave([Out] float[] outValues, uint valueCount);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHeroLooks(out uint face, out float fat, out float height, out float tan);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHeroWeapons(out uint melee, out uint ranged);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopHeldUids(uint player, out ulong melee, out ulong ranged);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopFigures([Out] uint[] outWords, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopScreen([Out] uint[] outWords, uint capacity, [Out] byte[]? text, uint textCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClothingMannequin();

	[DllImport(Library)]
	private static extern int RefiiNetCoopSend([MarshalAs(UnmanagedType.LPUTF8Str)] string text);

	[DllImport(Library)]
	private static extern uint RefiiNetCoopReceive(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiNetSyncConnect();

	[DllImport(Library)]
	private static extern int RefiiNetSyncState();

	[DllImport(Library)]
	private static extern int RefiiNetSyncSend(byte[] data, uint length);

	[DllImport(Library)]
	private static extern uint RefiiNetSyncReceive(byte[] outBytes, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiNetSyncClose();

	[DllImport(Library)]
	private static extern uint RefiiHostGetHeroStateApplied(uint player);

	[DllImport(Library)]
	private static extern uint RefiiHostGetSimTick();

	[System.Runtime.InteropServices.DllImport(Library, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern uint RefiiHostGetSimClock(out double seconds, out uint tick);

	[DllImport(Library)]
	private static extern uint RefiiHostGetStatedFx(uint[] words, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetStatedFxCount();

	[System.Runtime.InteropServices.DllImport(Library, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern int RefiiHostSetTimeBaseMode(int mode, out ulong capped, out ulong granted, out ulong watchdog);

	[System.Runtime.InteropServices.DllImport(Library, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern void RefiiHostAdvanceTimeBase(uint steps);

	[DllImport(Library)]
	private static extern uint RefiiHostGetGuestFrame();

	[DllImport(Library)]
	private static extern uint RefiiHostGetActiveFx(uint[]? outWords, uint maxEffects);

	[DllImport(Library)]
	private static extern uint RefiiHostGetSaveCards(out uint cursor, out uint baseSlot, [Out] byte[]? text, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHudMeters([Out] uint[]? hashes, [Out] float[]? values, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetExpressionMeter([Out] float[] values, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetTopBoxFront([Out] uint[] words, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetTargetedQuery([Out] uint[] words, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetPlayerTwoTargetedQuery([Out] uint[] words, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetDrunkPlayers();

	/// <summary>Which heroes are past the drunk warp's threshold: bit 0 hero one, bit 1 hero two.</summary>
	public static uint GetDrunkPlayers()
	{
		try
		{
			return RefiiHostGetDrunkPlayers();
		}
		catch (EntryPointNotFoundException)
		{
			return 0u;
		}
		catch (DllNotFoundException)
		{
			return 0u;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetPlayerTwoHud(ref uint target, [Out] byte[] text, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHudPulsers([Out] uint[]? hashes, [Out] float[]? phases, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHudPulserStates([Out] uint[]? hashes, [Out] float[]? phases, [Out] uint[]? modes, uint capacity);


	[DllImport(Library)]
	private static extern uint RefiiHostGetHudTexts([Out] uint[]? hashes, uint capacity, [Out] byte[]? text, uint textCapacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetSpellAssign([Out] uint[] outWords);

	[DllImport(Library)]
	private static extern int RefiiHostGetSpellWheel(uint player, [Out] uint[] outWords, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetSpellBlade([Out] uint[] outWords, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetMenuRow(
		uint index, [Out] byte[]? name, uint nameCapacity, [Out] byte[]? parent,
		uint parentCapacity, out uint id, out int eventType, out ulong generation,
		out int rating);

	[DllImport(Library)]
	private static extern uint RefiiHostTakeUiElementChanges(
		uint[]? outWords, uint wordCapacity, byte[]? outKinds, uint kindCapacity);

	[DllImport(Library)]
	private static extern void RefiiHostSetGuiPredicate([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint kind, uint bits);

	[DllImport(Library)]
	private static extern void RefiiHostUiElementCounters(
		out ulong outRecorded, out uint outPending, out ulong outDropped);

	[DllImport(Library)]
	private static extern uint RefiiHostTakeMenuChunks(
		byte[] outUtf8, uint utf8Capacity, byte[] outKinds, uint kindsCapacity, uint maxRecords);

	[DllImport(Library)]
	private static extern uint RefiiHostGetWornItems(uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetDyeStates([Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFamily([Out] byte[]? outNames, uint stride, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFamilyDetails(
		uint index, [Out] byte[] outFields, uint stride, uint count);

	[DllImport(Library)]
	private static extern void RefiiHostSetShopPrice(uint ticket, uint sell);

	[DllImport(Library)]
	private static extern uint RefiiHostGetShopPrice(out uint outTicket, out int outPrice, out uint outRecord);

	[DllImport(Library)]
	private static extern uint RefiiHostGetInventoryScreenMode();

	[DllImport(Library)]
	private static extern uint RefiiHostGetQuestLog(uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetQuestTrailOverride();

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
	private static extern uint RefiiHostSetCvar([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

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
	private static extern void RefiiHostTraceMessage([MarshalAs(UnmanagedType.LPUTF8Str)] string text);

	[DllImport(Library)]
	private static extern int RefiiHostTraceStartup();

	[DllImport(Library)]
	private static extern ulong RefiiHostTraceZoneBegin([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[DllImport(Library)]
	private static extern void RefiiHostTraceZoneEnd(ulong zone);

	[DllImport(Library)]
	private static extern void RefiiHostTracePlot([MarshalAs(UnmanagedType.LPUTF8Str)] string name, double value);

	[DllImport(Library)]
	private static extern void RefiiHostTraceGpuPasses(
		byte[] names, uint nameStride, long[] cpuUs, long[] gpuUs, uint count);

	// ---- hang watchdog (hang_watchdog.cpp) ----

	[DllImport(Library)]
	private static extern void RefiiHostFrameAlive(ulong frame);

	[DllImport(Library)]
	private static extern uint RefiiHostRegisterFramePhase([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[DllImport(Library)]
	private static extern void RefiiHostSetFramePhase(uint index);

	[DllImport(Library)]
	private static extern void RefiiHostSetHostLogPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

	[DllImport(Library)]
	private static extern uint RefiiHostHangReport(
		[Out] byte[] outPath, uint pathCapacity, out double lastStallSeconds, out int stalled);

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
	private static extern uint RefiiHostProbeSnapshotArm(int slot);

	[DllImport(Library)]
	private static extern uint RefiiHostProbeSnapshotRead(int slot, [Out] float[] outValues, uint capacity);

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
		out float fraction, out int dayCount, out int frozen, out int moonPhase);

	[DllImport(Library)]
	private static extern uint RefiiHostGetWeather(
		out float snowSize, out float snowFallSpeed, out float rainSize, out float rainDensity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetThemeStack(uint[] outWords, uint maxEntries);

	[DllImport(Library)]
	private static extern uint RefiiHostGetThemeBlends(uint[] outWords, uint maxEntries);

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
	private static extern uint RefiiHostGetDisplayBox(out uint vtable);

	[DllImport(Library)]
	private static extern uint RefiiHostGetMenuBox(
		out uint selected, out uint flags, out uint enabledMask);

	[DllImport(Library)]
	private static extern void RefiiHostGetSceneStreams(uint[] outWords);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicFrame(float[] outValues, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicLights(float[] outValues, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicMists(float[] outValues, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicParticles(float[] outValues, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostSetGuestRender(int on);

	/// <summary>
	/// Runs (true) or skips (false) the guest's own scene draw, or hands it back to
	/// REFII_GUEST_RENDER (null): a reference instrument for the classic renderer, whose constant
	/// setters then fill the device shadow. False when the native build predates the export.
	/// </summary>
	public static bool SetGuestRender(bool? on)
	{
		if (TryGetAbiVersion() is null)
		{
			return false;
		}
		try
		{
			RefiiHostSetGuestRender(on is null ? -1 : on.Value ? 1 : 0);
			return true;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Floats in a particle group's header, and per record (see the native export).</summary>
	public const int ClassicParticleHeader = 340;

	/// <summary>Floats of a trail group's header (RefiiHostGetClassicTrails); its points follow, 8 floats each.</summary>
	public const int ClassicTrailHeader = 288;

	private static bool _classicTrailsMissing;

	/// <summary>
	/// The render scene's trails and double trails: out[0] groups, out[1] floats used, then per group
	/// a <see cref="ClassicTrailHeader"/>-float header and (c + 3) x 8 control-point floats.
	/// </summary>
	public static uint GetClassicTrails(float[] values)
	{
		if (_classicTrailsMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetClassicTrails(values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_classicTrailsMissing = true;
			return 0;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicTrails([Out] float[] outValues, uint capacity);

	/// <summary>Words per decal in <see cref="GetDecals"/>: 15 stated, 12 of the owner's matrix.</summary>
	public const int DecalWords = 27;

	private static bool _decalsMissing;

	/// <summary>
	/// The decals the guest stated, oldest first (RefiiHostGetDecals): per decal [0] serial, [1]
	/// owner twin, [2] texture name hash, [3..6] position, [7..10] direction, [11]/[12] half sizes,
	/// [13] life, [14] flag (float bits where a float), [15..26] the owner's matrix rows.
	/// <paramref name="serial"/> moves when the set changed.
	/// </summary>
	public static uint GetDecals(uint[] words, out uint serial)
	{
		serial = 0;
		if (_decalsMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetDecals(words, (uint)words.Length, out serial);
		}
		catch (EntryPointNotFoundException)
		{
			_decalsMissing = true;
			return 0;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetDecals([Out] uint[] outWords, uint capacity, out uint outSerial);

	/// <summary>Floats before the lights in <see cref="GetClassicWeather"/>; each light is 8.</summary>
	public const int ClassicWeatherHeader = 80;

	private static bool _classicWeatherMissing;

	/// <summary>
	/// What the title's rain and snow draws read past the theme and wind: [0] a light zone is up,
	/// [1..3] its colour, [4] the guest clock, [5] n lights, then per light position xyz, radius,
	/// colour rgb, 0 — guest axes (RefiiHostGetClassicWeather).
	/// </summary>
	public static uint GetClassicWeather(float[] values)
	{
		if (_classicWeatherMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetClassicWeather(values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_classicWeatherMissing = true;
			return 0;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicWeather([Out] float[] outValues, uint capacity);
	public const int ClassicParticleRecord = 12;

	/// <summary>
	/// The render scene's particle groups as their twins hold them: [0] groups, [1] floats
	/// used, then per group a header and its records. Returns the floats used, 0 when none.
	/// </summary>
	public static uint GetClassicParticles(float[] values)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		return RefiiHostGetClassicParticles(values, (uint)values.Length);
	}

	/// <summary>Floats per mist in <see cref="GetClassicMists"/>.</summary>
	public const int ClassicMistStride = 36;

	/// <summary>The render scene's mist volumes, 36 floats each (see the native export).</summary>
	public static uint GetClassicMists(float[] values)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		return RefiiHostGetClassicMists(values, (uint)values.Length);
	}

	/// <summary>Floats per light in <see cref="GetClassicLights"/>.</summary>
	public const int ClassicLightStride = 20;

	/// <summary>
	/// The render scene's active lights as the light manager holds them, 20 floats each (see
	/// the native export for the layout). Returns the lights written, or 0 before the scene is up.
	/// </summary>
	public static uint GetClassicLights(float[] values)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		return RefiiHostGetClassicLights(values, (uint)values.Length);
	}

	/// <summary>Floats per light in <see cref="GetClassicSceneLights"/>.</summary>
	public const int ClassicSceneLightStride = 36;

	/// <summary>
	/// Every light of the render scene, not only the manager's active list (see the native export
	/// for the 36-float layout). Returns the lights written.
	/// </summary>
	public static uint GetClassicSceneLights(float[] values)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetClassicSceneLights(values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	/// <summary>
	/// The frame-constant sources the title's render passes read — ambient, sun direction,
	/// lightmap factor, normal-map darkening, the five-row atmosphere block, the scene's theme
	/// block, the sun light and the wind — as 148 floats (see the native export for the layout). Returns
	/// the floats written, or 0 before the lighting object exists.
	/// </summary>
	public static uint GetClassicFrame(float[] values)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		return RefiiHostGetClassicFrame(values, (uint)values.Length);
	}

	private static bool _classicThemeMissing;

	/// <summary>
	/// The scene's whole theme block (194 parameters x 4) and the sky object's sun colour and
	/// direction, moon direction and phase, as 789 floats (see the native export). 0 before the
	/// scene is up or when the native library predates the call.
	/// </summary>
	public static uint GetClassicTheme(float[] values)
	{
		if (_classicThemeMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetClassicTheme(values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_classicThemeMissing = true;
			return 0;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicTheme([Out] float[] outValues, uint capacity);

	private static bool _classicThemeTexturesMissing;

	/// <summary>
	/// The theme's texture slots, raw: per slot 17 words, the element count then up to eight
	/// (weight bits, hash) pairs in the vector's order (RefiiHostGetClassicThemeTextures). Words
	/// written, or 0 when the scene is not up.
	/// </summary>
	public static uint GetClassicThemeTextures(uint[] words)
	{
		if (_classicThemeTexturesMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetClassicThemeTextures(words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_classicThemeTexturesMissing = true;
			return 0;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicThemeTextures([Out] uint[] outWords, uint capacity);

	private static bool _lightHalosMissing;

	/// <summary>
	/// The light halos the guest draws this frame (RefiiHostGetLightHalos), 12 floats each:
	/// centre (guest axes), half-extent, colour, water factor and aspect, texture hash bits, light.
	/// Halos written, or 0.
	/// </summary>
	public static uint GetLightHalos(float[] values)
	{
		if (_lightHalosMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetLightHalos(values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_lightHalosMissing = true;
			return 0;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetLightHalos([Out] float[] outValues, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostPostFrontEndAction([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[DllImport(Library)]
	private static extern uint RefiiHostGetLanguages(
		out uint current, byte[] names, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCurrentCulture(byte[] code, uint capacity);

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
		byte[] merchants, uint merchantsCapacity, out int outStars,
		byte[] levelName, uint levelCapacity, out uint outLevelKey, byte[] scenario, uint scenarioCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetMinimapLive(uint[] outWords, uint capacity);

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
	private static extern uint RefiiHostUiDataGeneration();

	[DllImport(Library)]
	private static extern void RefiiHostRequestPropertyEstate(ulong uid);

	[DllImport(Library)]
	private static extern uint RefiiHostGetPropertyEstate(
		ulong uid, uint[] words, uint wordCapacity, byte[] occupant, uint occupantCapacity);

	[DllImport(Library)]
	private static extern int RefiiHostSetPlayerValue(
		int player, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, float value);

	[DllImport(Library)]
	private static extern int RefiiHostGetOptions(
		uint[] words, uint wordCapacity, out float outBreadcrumb);

	[DllImport(Library)]
	private static extern void RefiiHostSetAssetReader(IntPtr reader);

	[DllImport(Library)]
	private static extern void RefiiHostSetAssetOverrideQuery(IntPtr query);

	[DllImport(Library)]
	private static extern void RefiiHostSetAssetNameResolver(IntPtr resolver);

	[DllImport(Library)]
	private static extern int RefiiHostGetOptionsSliders(
		out float outGamma, out float outSfx, out float outMusic, out float outSpeech,
		out uint outSpeaker);

	[DllImport(Library)]
	private static extern uint RefiiHostGetOptionsPage(
		uint page, out int outCursor, int[] indices, float[] values, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetCrimeScreen(
		out int outFine, out uint outFlags, byte[] charge, uint chargeCapacity,
		byte[] service, uint serviceCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCompletedQuests(
		uint index, out uint outRecord, out int outMoney, out int outRenown,
		byte[] outConclusion, uint conclusionCapacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetModValue(byte[] key, uint keyLength, out float value);

	[DllImport(Library)]
	private static extern void RefiiHostSetModSetting(byte[] key, uint keyLength, float value);

	[DllImport(Library)]
	private static extern uint RefiiHostDrainModEvents(byte[] outBuffer, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostSetModEventMask(ulong mask);

	[DllImport(Library)]
	private static extern void RefiiHostSetModEventMaskHigh(ulong mask);

	[DllImport(Library)]
	private static extern void RefiiHostPostModInput(byte[] action, uint actionLength, int player, int pressed);

	[DllImport(Library)]
	private static extern void RefiiHostPostModCommand(byte[] line, uint lineLength);

	[DllImport(Library)]
	private static extern uint RefiiHostDrainModErrors(byte[] outBuffer, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostPlayerStateOutgoing(byte[] outBuffer, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCollectedKeys(uint[] outPairs, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostConsentTake(out uint ticket, out int kind, out int asker, out int detail,
		[Out] byte[] text, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostConsentGive(uint ticket, int yes);

	[DllImport(Library)]
	private static extern int RefiiHostPlayerStateReceive(byte[] message, uint length);

	[DllImport(Library)]
	private static extern uint RefiiHostPlayerStateDescribe(byte[] outBuffer, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostModHandleAddress(uint handle);

	[DllImport(Library)]
	private static extern void RefiiHostSetModVerdictHandler(ModVerdictFn? handler);

	[DllImport(Library)]
	private static extern void RefiiHostSetModVerdictMask(ulong mask);

	[DllImport(Library)]
	private static extern void RefiiHostSetModVerdictMaskHigh(ulong mask);

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
	private static extern uint RefiiHostGetItemAugments(uint recordId, uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern int RefiiHostGuestBuild();

	[DllImport(Library)]
	private static extern uint RefiiHostGuestAddress(uint tu1Address);

	[DllImport(Library)]
	private static extern uint RefiiHostGuestAddressToTu1(uint liveAddress);

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
	/// language, seeding the bank from the hero's gold, and settling it afterwards. Godot claims
	/// the games (<see cref="ClaimPubGames"/>), which takes the Scaleform module out of the
	/// guest's pump (<c>src/vfs/src/hooks/pubgame_pump.cpp</c>): the guest still walks 1 → 2 → 3,
	/// Godot's game plays while it sits in 3, and the game's own fscommands — posted through
	/// <see cref="PostPubGameCommand"/> — carry it to 6 and the settle at 9.
	/// </remarks>
	public readonly record struct PubGameState(
		uint Stage, uint Profile, long Gold, long Debt, string Game, string Variation)
	{
		/// <summary>The session is up and nobody is drawing it yet.</summary>
		public bool WantsHost => Stage == RunningStage && Profile != 0;

		/// <summary>The state the session plays in; the pump enters it from 2 once per session.</summary>
		public const uint RunningStage = 3;

		/// <summary>
		/// True while a session is live and the host should keep drawing.
		/// </summary>
		/// <remarks>
		/// 3 is play; 4 and 5 are the save a holdings update triggers (the tick saves in 4, the
		/// pump hands back EndSave in 5); 7 and 8 are the guest ending the session itself. 2 comes
		/// before the board exists and 6 and 9 after it is gone.
		/// </remarks>
		public bool IsLive => Stage is 3 or 4 or 5 or 7 or 8;
	}

	/// <summary>What Godot's game says, in the guest's fscommand handler's own terms.</summary>
	/// <remarks>Each is one branch of <c>sub_82EF2AC0</c>; see <c>pubgame_pump.cpp</c>.</remarks>
	public enum PubGameCommand : uint
	{
		PlaySound = 1,
		PauseSound = 2,
		Holdings = 3,
		Points = 4,
		Rank = 5,
		SoundVolume = 6,
		MusicVolume = 7,
		GameOver = 8,
	}

	/// <summary>What the pump would have told the movie.</summary>
	[System.Flags]
	public enum PubGameEvent : uint
	{
		None = 0,

		/// <summary>2 → 3: the session is up.</summary>
		Running = 1,

		/// <summary>State 5: the save a holdings update started has finished — <c>pubGame.EndSave</c>.</summary>
		SaveEnded = 2,

		/// <summary>A holdings update arrived outside state 3, so no save follows it.</summary>
		SaveSkipped = 4,

		/// <summary>State 7: the guest is ending the session — <c>pubGame.EndGame</c>.</summary>
		EndGame = 8,
	}

	/// <summary>
	/// What <c>sub_82EF18A0</c> hands the movie before <c>pubGame.Run</c>.
	/// </summary>
	/// <remarks>
	/// <see cref="Points"/> is the profile's three per-game slots; the game reads the one its
	/// settings name as <c>gameIndex</c>, and the total is the sum of all three
	/// (<c>sub_82EE8710</c>). <see cref="SoundVolume"/> and <see cref="MusicVolume"/> are 0..1 —
	/// the movie is handed <c>(int)(v * 10)</c>. <see cref="Items"/> bit N means
	/// <c>pubGame.TournamentItemWon(N)</c>.
	/// </remarks>
	public readonly record struct PubGameProfile(
		string Name, long Rank, long[] Points, uint Items, uint Pad,
		float SoundVolume, float MusicVolume, long StartingGold, long StartingDebt, string Language)
	{
		public int TotalPoints => (int)(Points[0] + Points[1] + Points[2]);
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

	/// <summary>Reads what the movie would be handed at start, or null when there is no profile.</summary>
	public static PubGameProfile? GetPubGameProfile()
	{
		if (!IsGuestUp)
		{
			return null;
		}

		var values = new uint[14];
		var name = new byte[256];
		var language = new byte[4];
		try
		{
			if (RefiiHostGetPubGameProfile(
				values, name, (uint)name.Length, language, (uint)language.Length) == 0)
			{
				return null;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		static long Wide(uint high, uint low) => ((long)high << 32) | low;

		int nameLength = System.Array.IndexOf(name, (byte)0);
		return new PubGameProfile(
			nameLength <= 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(name, 0, nameLength),
			Wide(values[0], values[1]),
			[Wide(values[2], values[3]), Wide(values[4], values[5]), Wide(values[6], values[7])],
			values[8],
			values[9],
			BitConverter.UInt32BitsToSingle(values[10]),
			BitConverter.UInt32BitsToSingle(values[11]),
			(int)values[12],
			(int)values[13],
			Ascii(language));
	}

	/// <summary>
	/// Tells the guest Godot plays the pub games, so its pump never creates the Scaleform movie.
	/// </summary>
	public static bool ClaimPubGames(bool claimed)
	{
		if (!IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostClaimPubGames(claimed ? 1 : 0) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Hands the guest one fscommand from Godot's game.</summary>
	public static void PostPubGameCommand(
		PubGameCommand kind, string? text = null,
		double n0 = 0, double n1 = 0, double n2 = 0, long i0 = 0, long i1 = 0)
	{
		if (!IsGuestUp)
		{
			return;
		}

		try
		{
			RefiiHostPostPubGameCommand((uint)kind, text ?? string.Empty, n0, n1, n2, i0, i1);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>What the pump told the movie since the last call.</summary>
	public static PubGameEvent TakePubGameEvents()
	{
		if (!IsGuestUp)
		{
			return PubGameEvent.None;
		}

		try
		{
			return (PubGameEvent)RefiiHostTakePubGameEvents();
		}
		catch (EntryPointNotFoundException)
		{
			return PubGameEvent.None;
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
	private static extern int RefiiHostGetPubGameProfile(
		[Out] uint[] values,
		[Out] byte[] name,
		uint nameCapacity,
		[Out] byte[] language,
		uint languageCapacity);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int RefiiHostClaimPubGames(int claimed);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern int RefiiHostPostPubGameCommand(
		uint kind,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string text,
		double n0, double n1, double n2, long i0, long i1);

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern uint RefiiHostTakePubGameEvents();

	[DllImport(Library)]

	private static extern int RefiiHostBridgeAbiVersion();

	[DllImport(Library)]
	private static extern int RefiiHostProbeGuestMemory(byte[] message, nuint capacity);

	[DllImport(Library)]
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

	[DllImport(Library)]
	private static extern void RefiiHostSetAppRoot(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string root);

	[DllImport(Library)]
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

		[System.ThreadStatic]
		private static uint[]? _hudMeterHashes;

		[System.ThreadStatic]
		private static float[]? _hudMeterValues;

		[System.ThreadStatic]
		private static uint[]? _hudScriptIds;

		public static Dictionary<uint, float> GetHudMeters()
		{
			Dictionary<uint, float> dictionary = new Dictionary<uint, float>();
			if (!TryGetAbiVersion().HasValue || !IsGuestUp)
			{
				return dictionary;
			}
			uint[] array = _hudMeterHashes ??= new uint[256];
			float[] array2 = _hudMeterValues ??= new float[256];
			const uint cap = 128u;
			uint num;
			try
			{
				num = RefiiHostGetHudMeters(array, array2, cap);
			}
			catch (EntryPointNotFoundException)
			{
				return dictionary;
			}
			if (num > cap)
			{
				GD.PushWarning($"[host] the guest holds {num} HUD meters, more than the {cap} asked for — the tail was not read");
				num = cap;
			}
			for (uint num2 = 0u; num2 < num; num2++)
			{
				dictionary[array[num2]] = array2[num2];
			}
			return dictionary;
		}

		private static uint[]? _hudPulserModes;

		/// <summary>
		/// The HUD's pulsers — registry 4 — keyed by `FNV1(PulserName)`: each one's loop PHASE 0..1
		/// (`rec+20`) and whether it RUNS (`rec+12`: 0 looping, 1 finishing its loop, 2 stopped; the
		/// stop zeroes the phase and applies tick 0). The guest advances a running one; the host
		/// runs it on its own clock between statements and holds a stopped one still.
		/// </summary>
		public static Dictionary<uint, Refii.Ui.Engine_UiSource.HudPulser> GetHudPulsers()
		{
			var dictionary = new Dictionary<uint, Refii.Ui.Engine_UiSource.HudPulser>();
			if (!TryGetAbiVersion().HasValue || !IsGuestUp)
			{
				return dictionary;
			}
			uint[] array = _hudMeterHashes ??= new uint[256];
			float[] array2 = _hudMeterValues ??= new float[256];
			uint[] modes = _hudPulserModes ??= new uint[256];
			const uint cap = 64u;
			uint num;
			bool stated = true;
			try
			{
				num = RefiiHostGetHudPulserStates(array, array2, modes, cap);
			}
			catch (EntryPointNotFoundException)
			{
				// A bridge before 223 states the phase alone; a pulser it lists is taken as running.
				stated = false;
				try
				{
					num = RefiiHostGetHudPulsers(array, array2, cap);
				}
				catch (EntryPointNotFoundException)
				{
					return dictionary;
				}
			}
			if (num > cap)
			{
				num = cap;
			}
			for (uint i = 0u; i < num; i++)
			{
				dictionary[array[i]] = new Refii.Ui.Engine_UiSource.HudPulser(array2[i], !stated || modes[i] != 2u);
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

			uint[] hashes = _hudMeterHashes ??= new uint[256];
			float[] ratios = _hudMeterValues ??= new float[256];
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

			uint[] hashes = _hudMeterHashes ??= new uint[256];
			uint[] ids = _hudScriptIds ??= new uint[256];
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
			uint[] array = _hudTextHashes ??= new uint[64];
			byte[] array2 = _hudTextBytes ??= new byte[8192];
			System.Array.Clear(array);
			System.Array.Clear(array2);
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
			ReadOnlySpan<uint> read = ReadUiTextureHashes(presentation, elementCount);
			return read.Length == elementCount && elementCount > 0 ? read.ToArray() : null;
		}

		/// <summary>
		/// The guest's stated texture hash per element, in a KEPT buffer: the span is valid until
		/// the next call and must be copied to be held. Empty when the guest is down or its count
		/// disagrees with <paramref name="elementCount"/>. Main thread only. The per-sample
		/// readers compare against what they hold and copy only on a change.
		/// </summary>
		public static ReadOnlySpan<uint> ReadUiTextureHashes(uint presentation, int elementCount)
		{
			if (!TryGetAbiVersion().HasValue || !IsGuestUp || elementCount <= 0)
			{
				return ReadOnlySpan<uint>.Empty;
			}

			if (_uiTextureHashes.Length < elementCount)
			{
				_uiTextureHashes = new uint[Math.Max(elementCount, _uiTextureHashes.Length * 2)];
			}

			uint num;
			try
			{
				num = RefiiHostGetUiTextureHashes(presentation, _uiTextureHashes, (uint)elementCount);
			}
			catch (EntryPointNotFoundException)
			{
				return ReadOnlySpan<uint>.Empty;
			}

			return num == (uint)elementCount
				? new ReadOnlySpan<uint>(_uiTextureHashes, 0, elementCount)
				: ReadOnlySpan<uint>.Empty;
		}

		private static uint[] _uiTextureHashes = new uint[1024];

		[DllImport("refii_hostbridge")]
		private static extern uint RefiiHostGetEntityLocomotion(uint entity, uint[] outWords, uint wordCount, byte[] outName, uint nameCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityAnimState(uint entity, [Out] uint[] outWords, uint wordCapacity, out uint outFlags);

	[DllImport(Library)]
	private static extern uint RefiiHostGetTopBoxBody(
		[Out] uint[] outWords, uint capacity, [Out] byte[] outText, uint textCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetBoneMorphHeight(uint entity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityTextureLayers(
		uint entity, [Out] uint[] outWords, uint wordCapacity, out uint generation, out ulong statements);

	[DllImport(Library)]
	private static extern uint RefiiHostGetItemDyeLayers(
		uint record, [Out] uint[] outWords, uint wordCapacity, out uint generation);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityPartMorphs(
		uint entity, [Out] uint[] outWords, uint wordCapacity, out uint generation, out ulong statements);

	[DllImport(Library)]
	private static extern uint RefiiHostGetBoneMorph(uint entity, [Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAnimPostOps(uint entity, [Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityPartHides(uint entity, [Out] byte[] outText, uint textCapacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetEntityDrawRange(uint entity, out float distance, out float fadeFactor);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityLightZone(uint entity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityCasterFlags(uint entity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityInteriorMeshes(uint entity, [Out] uint[] outHashes, uint maxHashes);

	[DllImport(Library)]
	private static extern uint RefiiHostGetDisplacementQuads([Out] float[] outFloats, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFxMeshes([Out] float[] outFloats, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetSpotFrustums([Out] float[] outFloats, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityDisplacementParts(uint entity, [Out] uint[] outHashes, uint maxHashes);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityWornItems(uint entity, [Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAnimMask(
		uint node, [Out] byte[] outBytes, uint capacity, out uint outSkeletonId);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAttachmentGrips([Out] uint[] outWords, uint maxRecords);

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
