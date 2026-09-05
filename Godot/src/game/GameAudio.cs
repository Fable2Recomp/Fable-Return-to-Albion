using System;
using Godot;
using Refii.Audio;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Plays the game's audio — through GODOT, paced by Godot.
///
/// The guest still decides what is heard: its own mixer runs, its sound middleware picks
/// the samples, and every voice it starts is its own. What changed is where the audio comes
/// out and, more importantly, what sets the pace.
///
/// The guest's audio clock is not a timer — it is the rate its output is consumed. One
/// credit per frame taken wakes its audio worker, which re-enters its render-driver
/// callback, which mixes the next frame. Under the SDK's SDL driver those credits came from
/// SDL's playback thread, so the guest ran on SDL's clock while Godot drew on another. Every
/// frame this node pulls is a credit, so the guest now runs on Godot's.
///
/// That also explains the shape of <see cref="_Process"/>: it pulls only what the generator
/// reports room for. Draining greedily would buffer the guest ahead of what is audible and
/// reintroduce exactly the drift this replaces.
///
/// Stage 2 — per-voice playback, so each sound can sit in Godot's own 3D field against
/// Godot's camera — layers on top of this and needs it. The guest's mixer keeps running and
/// keeps being credited even once its mixed output is discarded, because that mix is the
/// clock and it does not stop being the clock just because nobody is listening to it.
/// </summary>
public partial class GameAudio : Node
{
	/// <summary>
	/// How many guest frames to move in one poll, at most.
	///
	/// A guest frame is 5.333 ms and Godot polls at frame rate, so a 60 Hz frame owes about
	/// three. This ceiling covers a long hitch without letting one catch-up poll dump the
	/// whole ring into the generator in a single push.
	/// </summary>
	private const int MaxFramesPerPoll = 32;

	/// <summary>
	/// How many voices to ask the bridge for in one snapshot. Grown if the guest reports
	/// more, so a busy scene is not silently truncated.
	/// </summary>
	private const int InitialVoiceCapacity = 64;

	/// <summary>
	/// The project setting that switches per-voice playback on. Editable in the editor's
	/// Project Settings, which is the only one of the three switches that survives pressing
	/// Play there.
	/// </summary>
	private const string VoicePlaybackSetting = "refii/audio/voice_playback";

	private Engine_AudioOutput? _output;
	private uint _samplesPerFrame;
	private uint _floatsPerFrame;

	private Engine_VoicePool? _voices;
	private HostBridge.GuestVoice[] _voiceScratch = Array.Empty<HostBridge.GuestVoice>();
	private byte[] _voicePcm = Array.Empty<byte>();
	private readonly System.Collections.Generic.HashSet<ulong> _aliveVoices = new();
	private bool _voicePlayback;

	/// <summary>How often the health check samples. Long enough that the rate is stable.</summary>
	private const double ReportIntervalSeconds = 5.0;

	/// <summary>
	/// How many reports always print. Enough to record that audio came up and at what rate,
	/// so every run carries its own evidence without the log filling for the rest of it.
	/// </summary>
	private const int BootReports = 3;

	private double _statsTimer;
	private ulong _lastPushed;
	private ulong _lastDropped;
	private int _reports;
	private bool _wasOutsideBand;
	private uint _lastVoiceCount;
	private bool _reportedSilence;

	/// <summary>
	/// Guest frames handed to Godot since start. Counted by the sink, since the sink's own
	/// thread is what moves them.
	/// </summary>
	public long FramesPlayed =>
		_output is null || _samplesPerFrame == 0 ? 0 : _output.FramesPushed / _samplesPerFrame;

	/// <summary>
	/// The guest's production rate over the last sampling window, in frames per second.
	///
	/// The number to check is 187.5 — 48000/256. A rate that is not that means the clock is
	/// wrong however the audio happens to sound, which is why this is measured rather than
	/// assumed.
	/// </summary>
	public double GuestFramesPerSecond { get; private set; }

	public override void _Ready()
	{
		_output = new Engine_AudioOutput { Name = "AudioOutput" };
		AddChild(_output);

		// Ask the bridge for the frame geometry rather than hard-coding what the binary
		// states. If the guest's mastering voice ever reports something else, this follows
		// it instead of quietly writing past the end of a buffer sized from a guess.
		_samplesPerFrame = HostBridge.AudioSamplesPerFrame();
		_floatsPerFrame = HostBridge.AudioFloatsPerFrame();

		if (_samplesPerFrame == 0 || _floatsPerFrame == 0)
		{
			GD.PushWarning(
				"[audio] the bridge reports a zero-sized audio frame; guest audio is off this " +
				"session");
			return;
		}

		// The pull runs on the sink's own thread, NOT here. Pulling from _Process tied the
		// guest's audio clock to Godot's render rate: the generator holds 18 guest frames,
		// so once-per-frame pulling needs Godot above ~10 fps, and building a region drops
		// it to about 7. A run measured exactly that — 187.6 frames/s settled, 123.2 during
		// the level load with an empty ring and no drops.
		_output.StartPump(
			(buffer, frames) => (int)HostBridge.PullAudio(buffer, (uint)frames),
			(int)_samplesPerFrame, (int)_floatsPerFrame, MaxFramesPerPoll);

		GD.Print(
			$"[audio] host sink ready: {HostBridge.AudioSampleRate()} Hz, " +
			$"{HostBridge.AudioChannels()} ch, {_samplesPerFrame} samples/frame");

		// Per-voice playback is OFF by default.
		//
		// Stage 1 — Godot owning the device and the clock — is the part that stands on its
		// own and cannot make the game sound worse. Stage 2 replaces the guest's finished
		// mix with our reconstruction of it, so until it has been heard against the real
		// thing it is the more interesting option, not the safer one.
		//
		// THREE ways to switch it on, because one was not enough: launching from the Godot
		// editor does not inherit the shell's environment, so a run meant to test Stage 2
		// silently tested Stage 1 again. The project setting is the one that works from the
		// editor; the env var and the flag are for the packaged app and the command line.
		_voicePlayback =
			OS.GetEnvironment("REFII_VOICE_AUDIO") == "1" ||
			Array.IndexOf(OS.GetCmdlineArgs(), "--voice-audio") >= 0 ||
			ProjectSettings.GetSetting(VoicePlaybackSetting, false).AsBool();

		// Said either way. "No line" is not a state anyone can read — this run's log had no
		// per-voice line at all and that was indistinguishable from the feature being
		// broken, when in fact it had simply never been switched on.
		GD.Print($"[audio] per-voice playback: {(_voicePlayback ? "ON" : "off")} " +
			$"(set {VoicePlaybackSetting}, REFII_VOICE_AUDIO=1, or --voice-audio)");

		if (_voicePlayback)
		{
			_voices = new Engine_VoicePool { Name = "VoicePool" };
			AddChild(_voices);
			_voiceScratch = new HostBridge.GuestVoice[InitialVoiceCapacity];

			// The mix is SILENCED, not skipped. The sink keeps consuming it at 48 kHz —
			// that consumption is the guest's clock — and pushes zeros instead, so the
			// voices carry the sound without everything playing twice.
			_output.Muted = true;
			HostBridge.SetVoicePlaybackOwned(true);
			GD.Print("[audio] per-voice playback enabled; the guest's mix is muted but still paced");
		}
	}

	public override void _ExitTree()
	{
		// Hand the mix back on the way out. Leaving the guest's output discarded with
		// nothing playing the voices would be silence that looks like a bug in the guest.
		if (_voicePlayback)
		{
			HostBridge.SetVoicePlaybackOwned(false);
		}
	}

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("gameaudio");
		if (_output is null || !_output.IsReady || _floatsPerFrame == 0)
		{
			return;
		}

		// THE VOICE MIRROR ONLY, and only while there is a guest to mirror.
		//
		// This node is added beside the session rather than inside it, so it is up before the
		// boot and survives a failed one — and `PumpVoices` reads the guest's voice table every
		// frame. With no guest that is a per-frame read of a runtime that was never created.
		//
		// The MIXED path is deliberately untouched: it is pulled by the sink's own thread and it
		// owns the guest's clock, so it must keep being called and must SILENCE rather than skip.
		// `PullAudio` already returns nothing when there is no guest, which is that silence.
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		// The mixed output is pumped by the sink's own thread; only the per-voice mirror and
		// the health report belong on the render frame.
		PumpVoices();
		ReportHealth(delta);
	}

	/// <summary>
	/// Moves each guest voice's PCM into its own Godot stream.
	///
	/// Voices are played at their own rate from their own queue, started when the guest
	/// starts them. Both Godot and the guest are advanced by the same device clock, so they
	/// hold alignment; the guest's queue depth is buffering-ahead exactly like ours. What
	/// this deliberately does NOT do is drain everything the moment it appears — that would
	/// run Godot ahead of the simulation by up to the guest's ten-packet cap.
	/// </summary>
	private void PumpVoices()
	{
		if (!_voicePlayback || _voices is null)
		{
			return;
		}

		uint total = HostBridge.GetVoices(_voiceScratch);
		_lastVoiceCount = total;

		// The guest can have more voices than the last snapshot had room for. Grow and take
		// it again next frame rather than losing whichever ones fell off the end.
		if (total > _voiceScratch.Length)
		{
			_voiceScratch = new HostBridge.GuestVoice[total];
			return;
		}

		_aliveVoices.Clear();

		for (uint i = 0; i < total; i++)
		{
			HostBridge.GuestVoice voice = _voiceScratch[i];
			_aliveVoices.Add(Engine_VoicePool.KeyOf(voice.Handle, voice.Generation));

			uint taken = 0;
			if (voice.QueuedBytes != 0)
			{
				if (_voicePcm.Length < voice.QueuedBytes)
				{
					_voicePcm = new byte[voice.QueuedBytes];
				}

				taken = HostBridge.TakeVoicePcm(voice.Handle, voice.Generation, _voicePcm);
			}

			// Fed even with nothing new: a voice whose generator was full last frame is
			// still holding PCM this pool has already taken from the native ring, and only
			// a feed delivers it. Skipping the call would strand that audio forever.
			_voices.Feed(
				voice.Handle, voice.Generation, voice.SampleRate, voice.Channels,
				voice.Volume, voice.PanLeft, voice.PanRight, _voicePcm, (int)taken);
			_voices.SetPitch(voice.Handle, voice.Generation, voice.PitchRatio);
		}

		_voices.RetireAllExcept(_aliveVoices);
	}


	/// <summary>
	/// The rate the guest should produce at: 48000 / 256. A measured rate that is not this
	/// means the clock is wrong however the audio happens to sound, which is the whole
	/// reason this is measured rather than assumed.
	/// </summary>
	private const double ExpectedFramesPerSecond = 48000.0 / 256.0;

	/// <summary>How far off <see cref="ExpectedFramesPerSecond"/> is worth saying so.</summary>
	private const double RateTolerance = 0.05;

	/// <summary>
	/// Reports the guest's production rate.
	///
	/// This used to print only on silence or drops, which meant a WORKING run said nothing
	/// at all — and the rate was the stated acceptance check for the whole path. A gate
	/// that produces no evidence when it passes is not a gate. So the first few reports
	/// always print, giving every run a boot record, and after that it stays quiet unless
	/// something is worth reading.
	/// </summary>
	private void ReportHealth(double delta)
	{
		_statsTimer += delta;
		if (_statsTimer < ReportIntervalSeconds)
		{
			return;
		}

		HostBridge.AudioStats(out uint queued, out ulong pushed, out _, out ulong dropped,
			out bool driverInstalled);

		GuestFramesPerSecond = (pushed - _lastPushed) / _statsTimer;
		_lastPushed = pushed;
		_statsTimer = 0.0;
		_reports++;

		if (pushed == 0 && FramesPlayed == 0)
		{
			// Normal during boot — the guest has not reached its audio path yet. Only
			// worth saying while it is still plausibly starting up.
			if (!_reportedSilence && _reports >= 3)
			{
				_reportedSilence = true;
				GD.Print(
					$"[audio] still no frames from the guest after {_reports * ReportIntervalSeconds:0}s " +
					$"(host driver installed: {driverInstalled})");
			}
			return;
		}

		// The first window spans startup — the sink comes up partway through it, so its
		// average is always low and judging it against the settled rate is a false alarm.
		// The first run reported "174.6 OFF RATE" for exactly this reason and 187.6 from
		// then on.
		//
		// And ONE window off is not a fault. The guest cannot outrun the device that
		// credits it, so the long-run average is pinned to the expected rate; a single high
		// sample is the queue catching up after dipping under target, which is the pump
		// working. A run reported "202.0 OFF RATE" for exactly that. What a real problem
		// looked like was different in kind — sustained and downward, 167.0 then 123.2 —
		// so it takes two consecutive windows to call it.
		bool outsideBand = _reports > 1 &&
			Math.Abs(GuestFramesPerSecond - ExpectedFramesPerSecond) >
			ExpectedFramesPerSecond * RateTolerance;
		bool offRate = outsideBand && _wasOutsideBand;
		_wasOutsideBand = outsideBand;
		bool newDrops = dropped != _lastDropped;
		_lastDropped = dropped;

		if (_reports <= BootReports || offRate || newDrops)
		{
			string verdict = offRate ? "OFF RATE" : "ok";
			GD.Print(
				$"[audio] {GuestFramesPerSecond:F1} frames/s (expect {ExpectedFramesPerSecond:F1}) " +
				$"{verdict}, queued={queued}, played={FramesPlayed}, dropped={dropped}" +
				(_output!.Muted ? " (mix muted, voices carry the sound)" : string.Empty));
		}

		// Stage 2 needs its own evidence. The mixed-frame numbers above say the CLOCK is
		// healthy and nothing at all about whether any voice reached the speakers.
		if (_voicePlayback && _voices is not null && _reports <= BootReports)
		{
			GD.Print(
				$"[audio] voices: {_voices.LiveVoices} live, {_voices.VoicesRefused} refused, " +
				$"{_lastVoiceCount} reported by the guest");

			// The guest reporting voices while the pool creates none means Feed is never
			// reached, and the only two gates before it are QueuedBytes and the PCM take.
			// The guest log says the per-voice rings are FULL, so one of those two readings
			// disagrees with the native side — print what the host actually receives rather
			// than reasoning about which.
			for (uint i = 0; i < _lastVoiceCount && i < 3 && i < _voiceScratch.Length; i++)
			{
				HostBridge.GuestVoice v = _voiceScratch[i];
				GD.Print(
					$"[audio]   voice[{i}] handle=0x{v.Handle:X8} gen={v.Generation} " +
					$"{v.SampleRate}Hz x{v.Channels} started={v.Started} retired={v.Retired} " +
					$"queued={v.QueuedBytes} seq={v.SubmittedSeq} vol={v.Volume:F2}");
			}
		}
	}
}
