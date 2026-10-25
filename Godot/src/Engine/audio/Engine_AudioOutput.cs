using System;
using Godot;
using Refii.Core;
using Refii.Host;

namespace Refii.Audio;

/// <summary>
/// The guest's audio output: a Godot bus whose one effect writes the guest's frame onto
/// every speaker pair the device has.
///
/// This is the seam that makes Godot the owner of the guest's audio rather than a bystander
/// to it. The guest's audio clock is its output device's CONSUMPTION rate — it is credited
/// for each frame something takes, and that credit is what lets its mixer produce the next
/// one. Here the taker is the effect's mix step on Godot's audio thread, so the guest runs on
/// the device's clock with nothing in between.
///
/// Why an effect and not a stream: Godot's buses carry speaker PAIRS, and the only writer of
/// a specific pair is an audio effect's process call, which Godot exposes to GDExtension
/// alone. The host bridge is that extension (godot_audio_effect.cpp); this node loads it,
/// builds the bus and keeps it alive. A 5.1 device gets the guest's own 5.1; a stereo one
/// gets the SDK's ITU fold. Both come out of the same code, chosen by the pair count.
///
/// The keep-alive player is not decoration. Godot copies a bus's pairs to the device only
/// while they are ACTIVE, and only a playing stream marks them so — an effect writing into
/// an inactive pair is heard by nobody. A looping silent stream on the bus touches every
/// pair each step and costs nothing.
/// </summary>
public partial class Engine_AudioOutput : Node
{
	/// <summary>
	/// The guest's rate, stated by its mastering voice rather than assumed. The effect copies
	/// samples one to one, so the project's mix rate must match it.
	/// </summary>
	public const int GuestMixRate = 48000;

	/// <summary>The bus the guest plays through. Sends to Master.</summary>
	public const string BusName = "GuestAudio";

	private const string BridgeLibrary = "refii_hostbridge";
	private const string EffectClass = "RefiiGuestAudioEffect";
	private const string EntrySymbol = "refii_godot_audio_init";
	private const string ManifestPath = "user://refii_godot_audio.gdextension";

	/// <summary>
	/// Channels the output stage produces: 6 when the device has a centre and rears, else 2.
	/// Host-decoded sound (movies) asks the bridge for its front weight with this.
	/// </summary>
	public static int ActiveOutputChannels { get; private set; } = 2;

	/// <summary>True once the bus, the effect and the keep-alive are all up.</summary>
	public bool IsReady { get; private set; }

	/// <summary>Godot's channel count for the bus: 1 stereo, 2 for 3.1, 3 for 5.1, 4 for 7.1.</summary>
	public int SpeakerPairs { get; private set; }

	private AudioStreamPlayer? _keepAlive;
	private int _bus = -1;

	public override void _Ready()
	{
		// The bridge is loaded by the first P/Invoke, which the owner has already made for the
		// frame geometry. Godot must open THAT file: a different copy is a second module with
		// its own, empty ring.
		string? library = Engine_NativeLibraries.LoadedPath(BridgeLibrary);
		if (library is null)
		{
			GD.PushError("[audio] the host bridge is not loaded, so the guest's output cannot be installed");
			return;
		}

		if (!LoadExtension(library) || !ClassDB.ClassExists(EffectClass))
		{
			GD.PushError($"[audio] {EffectClass} is not available after loading {library}; the guest runs silently");
			return;
		}

		int bus = AudioServer.BusCount;
		AudioServer.AddBus();
		AudioServer.SetBusName(bus, BusName);
		AudioServer.SetBusSend(bus, "Master");
		_bus = bus;

		if (ClassDB.Instantiate(EffectClass).As<AudioEffect>() is not { } effect)
		{
			GD.PushError($"[audio] {EffectClass} could not be instantiated; the guest runs silently");
			return;
		}
		AudioServer.AddBusEffect(bus, effect);

		SpeakerPairs = Math.Max(1, AudioServer.GetBusChannels(bus));
		int mixRate = (int)AudioServer.GetMixRate();
		ActiveOutputChannels = SpeakerPairs >= 3 ? 6 : 2;
		HostBridge.AudioOutputConfigure((uint)SpeakerPairs, (uint)mixRate);

		_keepAlive = new AudioStreamPlayer
		{
			Name = "KeepAlive",
			Bus = BusName,
			Stream = SilentLoop(),
			MixTarget = AudioStreamPlayer.MixTargetEnum.Surround,
		};
		AddChild(_keepAlive);
		_keepAlive.Play();

		IsReady = true;
		GD.Print(
			$"[audio] guest output on bus '{BusName}': {AudioServer.GetSpeakerMode()}, " +
			$"{SpeakerPairs} speaker pair(s), {mixRate} Hz, the guest's 5.1 " +
			(ActiveOutputChannels == 6 ? "passed through" : "folded to stereo"));

		if (mixRate != GuestMixRate)
		{
			GD.PushError(
				$"[audio] Godot mixes at {mixRate} Hz but the guest renders at {GuestMixRate}; " +
				"set audio/driver/mix_rate in project.godot, or the game plays at the wrong speed");
		}
	}

	/// <summary>
	/// Follows the device. Godot rebuilds a bus's channels when the output device changes
	/// layout, and the effect's pair count must change with it or the pairs would be read off
	/// by one; the stride is reset on the native side when the channel count moves.
	/// </summary>
	public override void _Process(double delta)
	{
		if (!IsReady || _bus < 0 || _bus >= AudioServer.BusCount || AudioServer.GetBusName(_bus) != BusName)
		{
			return;
		}

		int pairs = Math.Max(1, AudioServer.GetBusChannels(_bus));
		if (pairs == SpeakerPairs)
		{
			return;
		}

		SpeakerPairs = pairs;
		ActiveOutputChannels = pairs >= 3 ? 6 : 2;
		HostBridge.AudioOutputConfigure((uint)pairs, (uint)AudioServer.GetMixRate());
		GD.Print(
			$"[audio] speaker layout changed: {AudioServer.GetSpeakerMode()}, {pairs} speaker pair(s), " +
			$"the guest's 5.1 {(ActiveOutputChannels == 6 ? "passed through" : "folded to stereo")}");
	}

	public override void _ExitTree()
	{
		if (!IsReady)
		{
			return;
		}

		IsReady = false;
		HostBridge.AudioOutputDetach();
		_keepAlive?.Stop();

		int bus = AudioServer.GetBusIndex(BusName);
		if (bus > 0)
		{
			AudioServer.RemoveBus(bus);
		}
	}

	/// <summary>
	/// Loads the bridge as a GDExtension, through a manifest written beside the user's data.
	///
	/// Written rather than shipped: a manifest in the project can only name a path under
	/// res://, and the export does not carry the staging tree — the natives live in the app's
	/// bin directory. The resolver already chose a file; this names that exact file, which is
	/// the only way to be sure Godot and .NET hold one module.
	/// </summary>
	private static bool LoadExtension(string library)
	{
		string os = OperatingSystem.IsWindows() ? "windows"
			: OperatingSystem.IsMacOS() ? "macos"
			: "linux";
		string manifest =
			"[configuration]\n" +
			$"entry_symbol = \"{EntrySymbol}\"\n" +
			"compatibility_minimum = \"4.7\"\n" +
			"reloadable = false\n" +
			"\n" +
			"[libraries]\n" +
			$"{os}.{Engine_NativeLibraries.ArchFolder} = \"{library.Replace('\\', '/')}\"\n";

		using (var file = Godot.FileAccess.Open(ManifestPath, Godot.FileAccess.ModeFlags.Write))
		{
			if (file is null)
			{
				GD.PushError($"[audio] cannot write {ManifestPath}: {Godot.FileAccess.GetOpenError()}");
				return false;
			}
			file.StoreString(manifest);
		}

		var status = GDExtensionManager.Singleton.LoadExtension(ManifestPath);
		if (status is GDExtensionManager.LoadStatus.Ok or GDExtensionManager.LoadStatus.AlreadyLoaded)
		{
			return true;
		}

		GD.PushError($"[audio] loading {library} as a GDExtension: {status}");
		return false;
	}

	/// <summary>
	/// A tenth of a second of silence, looped, so the bus's pairs stay active.
	///
	/// <c>LoopEnd</c> is INCLUSIVE: Godot's WAV mixer decodes the frame at the loop end before
	/// wrapping (the WAV <c>smpl</c> convention). A loop end equal to the frame count reads one
	/// frame past the data — four bytes of whatever the heap holds — and that lands on every
	/// speaker pair as a one-sample click once per loop. The last frame is <c>frames - 1</c>.
	/// </summary>
	private static AudioStreamWav SilentLoop()
	{
		const int frames = GuestMixRate / 10;
		return new AudioStreamWav
		{
			Data = new byte[frames * 2 * sizeof(short)],
			Format = AudioStreamWav.FormatEnum.Format16Bits,
			MixRate = GuestMixRate,
			Stereo = true,
			LoopMode = AudioStreamWav.LoopModeEnum.Forward,
			LoopBegin = 0,
			LoopEnd = frames - 1,
		};
	}
}
