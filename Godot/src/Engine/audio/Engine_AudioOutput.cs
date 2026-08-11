using System;
using Godot;

namespace Refii.Audio;

/// <summary>
/// A generator-backed audio sink: PCM in, Godot's audio bus out.
///
/// This is the seam that makes Godot the owner of the guest's audio rather than a bystander
/// to it. The guest's audio clock is its output device's CONSUMPTION rate — it is credited
/// for each frame something takes, and that credit is what lets its mixer produce the next
/// one. Feeding it through Godot's own device therefore hands Godot the guest's audio
/// timeline, not merely its speakers.
///
/// Which is why <see cref="FramesAvailable"/> matters more than it looks: it is the
/// back-pressure. Pulling from the guest faster than Godot can play lets the guest run ahead
/// of what is audible, which is exactly the drift this exists to remove. Only ever pull what
/// this reports room for.
///
/// THE PUMP IS ITS OWN THREAD, NOT `_Process`. It was `_Process` first, and a run showed why
/// that cannot work: the generator holds 18 guest frames, so pulling once per rendered frame
/// needs Godot above ~10 fps just to keep audio fed. Building a region drops it to about 7,
/// and the measured production rate fell with it — 187.6/s settled, 123.2/s during the level
/// load, with an empty ring and no drops, which is the guest waiting for credit rather than
/// being blocked. Pulling on the render thread makes every frame hitch an audio hitch, and
/// the guest's audio clock is not the render clock.
/// </summary>
public partial class Engine_AudioOutput : AudioStreamPlayer
{
	/// <summary>
	/// The guest's rate, stated by its mastering voice rather than assumed. Godot resamples
	/// if its own mix rate differs, so matching the project to this avoids a resample of
	/// every sound in the game.
	/// </summary>
	public const int GuestMixRate = 48000;

	/// <summary>
	/// Capacity of the generator's buffer. Headroom, not the working depth — see
	/// <see cref="TargetQueuedSeconds"/>.
	/// </summary>
	public const float BufferSeconds = 0.25f;

	/// <summary>
	/// How much audio we keep queued ahead of the speaker.
	///
	/// This is the latency, so it is deliberately much smaller than the buffer. Filling to
	/// capacity would make the buffer size and the latency the same number, which is why
	/// simply enlarging the buffer is not a fix for starvation — it only trades a dropout
	/// for a delay.
	/// </summary>
	public const float TargetQueuedSeconds = 0.04f;

	/// <summary>
	/// How often the pump thread tops the queue up. Far faster than a render frame, which
	/// is the entire point of it being a thread.
	/// </summary>
	private const int PumpIntervalMs = 2;

	private AudioStreamGeneratorPlayback? _playback;
	private Vector2[] _scratch = Array.Empty<Vector2>();

	private System.Threading.Thread? _pump;
	private volatile bool _pumping;
	private Func<float[], int, int>? _pull;
	private float[] _pullBuffer = Array.Empty<float>();
	private int _floatsPerGuestFrame;
	private int _samplesPerGuestFrame;
	private int _capacityFrames;
	private int _targetFrames;

	/// <summary>Frames Godot can accept right now. 0 until the stream is playing.</summary>
	public int FramesAvailable => _playback?.GetFramesAvailable() ?? 0;

	/// <summary>
	/// True once the generator is live and will accept pushes.
	///
	/// Not called <c>Ready</c>: that name is already <see cref="Node"/>'s, and shadowing it
	/// reads as the lifecycle callback rather than a state query.
	/// </summary>
	public bool IsReady => _playback is not null;

	/// <summary>Frames pushed since this sink started, for rate checks.</summary>
	public long FramesPushed { get; private set; }

	/// <summary>
	/// Pushes silence instead of the guest's mix, while still consuming it.
	///
	/// Used when the host plays individual voices: those carry the sound, and playing the
	/// guest's mix as well would double everything. Muting HERE rather than declining to
	/// queue the frame is deliberate — the guest is paced by this sink consuming at 48 kHz,
	/// so a mode that stops consuming stops the clock. Silence still takes real time to
	/// play; a skipped frame does not.
	/// </summary>
	public bool Muted { get; set; }

	/// <summary>
	/// Pushes that were refused because the buffer was full. A climbing count means the
	/// caller is not honouring <see cref="FramesAvailable"/>.
	/// </summary>
	public long PushesRefused { get; private set; }

	public override void _Ready()
	{
		Stream = new AudioStreamGenerator
		{
			MixRate = GuestMixRate,
			BufferLength = BufferSeconds,
		};

		// The playback object only exists once the stream is playing, so this cannot be
		// fetched in advance. Nothing is audible until something is pushed.
		Play();
		_playback = GetStreamPlayback() as AudioStreamGeneratorPlayback;

		if (_playback is null)
		{
			GD.PushWarning(
				"[audio] AudioStreamGenerator produced no playback; guest audio has nowhere " +
				"to go. The guest will keep running silently.");
		}

		_capacityFrames = (int)(BufferSeconds * GuestMixRate);
		_targetFrames = (int)(TargetQueuedSeconds * GuestMixRate);
	}

	/// <summary>
	/// Starts the pump.
	/// </summary>
	/// <param name="pull">
	/// Takes up to N guest frames into the buffer, returning how many it wrote. This is the
	/// call that credits the guest, so it runs on the pump thread and nowhere else.
	/// </param>
	/// <param name="samplesPerGuestFrame">256, as the guest's renderer states.</param>
	/// <param name="floatsPerGuestFrame">Samples times channels.</param>
	/// <param name="maxFramesPerPull">Ceiling on one pull, to bound the scratch buffer.</param>
	public void StartPump(
		Func<float[], int, int> pull, int samplesPerGuestFrame, int floatsPerGuestFrame,
		int maxFramesPerPull)
	{
		if (_playback is null || _pumping)
		{
			return;
		}

		_pull = pull;
		_samplesPerGuestFrame = samplesPerGuestFrame;
		_floatsPerGuestFrame = floatsPerGuestFrame;
		_pullBuffer = new float[floatsPerGuestFrame * maxFramesPerPull];

		_pumping = true;
		_pump = new System.Threading.Thread(PumpLoop)
		{
			IsBackground = true,
			Name = "refii-audio-pump",
		};
		_pump.Start();
	}

	/// <summary>Stops the pump and waits for it, so nothing pushes into a freed playback.</summary>
	public void StopPump()
	{
		_pumping = false;
		_pump?.Join(500);
		_pump = null;
	}

	public override void _ExitTree() => StopPump();

	/// <summary>
	/// Keeps the queue near <see cref="TargetQueuedSeconds"/>, forever, independent of how
	/// long a rendered frame takes.
	///
	/// Single producer, single consumer: this thread is the only one that pushes, and
	/// Godot's audio thread is the only one that drains. That is the contract the
	/// generator's ring is built for.
	/// </summary>
	private void PumpLoop()
	{
		while (_pumping)
		{
			try
			{
				TopUp();
			}
			catch (Exception error)
			{
				// A throw here would kill the pump silently and the guest would stall
				// behind a ring nobody drains. Say so and keep going.
				GD.PushError($"[audio] pump: {error.Message}");
			}

			System.Threading.Thread.Sleep(PumpIntervalMs);
		}
	}

	private void TopUp()
	{
		if (_playback is null || _pull is null)
		{
			return;
		}

		int free = _playback.GetFramesAvailable();
		int queued = _capacityFrames - free;
		int wantSamples = _targetFrames - queued;
		if (wantSamples <= 0)
		{
			return;
		}

		int wantFrames = Math.Min(wantSamples / _samplesPerGuestFrame,
			_pullBuffer.Length / _floatsPerGuestFrame);
		if (wantFrames <= 0)
		{
			return;
		}

		int taken = _pull(_pullBuffer, wantFrames);
		if (taken > 0)
		{
			PushInterleavedStereo(_pullBuffer, taken * _samplesPerGuestFrame);
		}
	}

	/// <summary>
	/// Pushes interleaved stereo float samples.
	/// </summary>
	/// <param name="interleaved">
	/// L,R pairs. Must hold at least <paramref name="frames"/> * 2 entries.
	/// </param>
	/// <param name="frames">How many stereo frames to take from the front of the buffer.</param>
	/// <returns>True when Godot accepted them.</returns>
	public bool PushInterleavedStereo(float[] interleaved, int frames)
	{
		if (_playback is null || frames <= 0)
		{
			return false;
		}

		if (interleaved.Length < frames * 2)
		{
			GD.PushError(
				$"[audio] asked to push {frames} frames from a buffer of {interleaved.Length} " +
				"floats; refusing rather than reading past the end");
			return false;
		}

		// Sized to EXACTLY the request and reused across calls. PushBuffer takes an array
		// rather than a span, so a larger scratch would have to be copied down to size on
		// every push — which is the allocation this exists to avoid. The pull is a whole
		// number of 256-sample guest frames, so in practice this reallocates almost never.
		if (_scratch.Length != frames)
		{
			_scratch = new Vector2[frames];
		}

		if (Muted)
		{
			// Zeroed rather than turned down with VolumeDb: exact, and free of Godot's own
			// volume smoothing. The frames still occupy real time in the generator, which
			// is what keeps the guest paced.
			Array.Clear(_scratch, 0, frames);
		}
		else
		{
			for (int i = 0; i < frames; i++)
			{
				_scratch[i] = new Vector2(interleaved[i * 2], interleaved[(i * 2) + 1]);
			}
		}

		// PushBuffer takes the whole buffer or none of it, so the caller must have gated on
		// FramesAvailable already. A refusal here is a bug upstream, not a normal outcome.
		bool accepted = _playback.PushBuffer(_scratch);
		if (accepted)
		{
			FramesPushed += frames;
		}
		else
		{
			PushesRefused++;
		}

		return accepted;
	}
}
