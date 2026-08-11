using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Audio;

/// <summary>
/// One Godot stream per guest XAudio voice.
///
/// The guest creates a source voice for every sound it plays — it is not a software mixer
/// with one output, it is dozens of independent voices — so mirroring it means one player
/// each. Players are pooled rather than created per sound: Fable 2 starts and stops voices
/// constantly, and churning scene nodes at that rate is the kind of cost that shows up as
/// stutter rather than as an audio problem.
///
/// Sample values arrive as the guest's own PCM. Volume and pan are baked into them here
/// rather than set on the player, for two reasons: the guest states both as linear
/// amplitude (its engine has no decibels anywhere), and its own router applies them by
/// multiplying the samples. Going through Godot's VolumeDb would mean converting to
/// decibels and back through Godot's smoothing to reproduce something the guest already
/// expressed exactly.
/// </summary>
public partial class Engine_VoicePool : Node
{
	/// <summary>
	/// How many voices can sound at once.
	///
	/// A ceiling rather than a guess at the real number: past this, further voices are
	/// dropped and said so, which is a bounded, visible failure. Growing without limit
	/// would turn a leak in the mirror into an out-of-memory much later and somewhere else.
	/// </summary>
	public const int MaxConcurrentVoices = 64;

	/// <summary>Per voice, how much audio Godot holds ahead of the speaker.</summary>
	private const float VoiceBufferSeconds = 0.25f;

	/// <summary>
	/// How much undelivered PCM a voice may hold before we start dropping it.
	///
	/// PCM taken from the native ring is GONE from there, so anything this pool cannot
	/// hand to the generator immediately has to be kept until it can. Dropping it instead
	/// is silent, permanent data loss — which is exactly what "quiet in places" sounded
	/// like. Half a second is far more than a voice can legitimately run ahead by.
	/// </summary>
	private const int MaxPendingBytesPerVoice = 48000 * 2 * 2 / 2;

	private readonly Dictionary<ulong, VoiceStream> _live = new();
	private readonly List<ulong> _reap = new();
	private Vector2[] _scratch = Array.Empty<Vector2>();
	private bool _reportedFull;

	/// <summary>Voices currently sounding.</summary>
	public int LiveVoices => _live.Count;

	/// <summary>Voices refused because the pool was full.</summary>
	public long VoicesRefused { get; private set; }

	/// <summary>
	/// A stable key for a voice. The guest's allocator reuses handles, so the generation is
	/// what stops a new sound inheriting the previous one's stream.
	/// </summary>
	public static ulong KeyOf(uint handle, uint generation) =>
		((ulong)generation << 32) | handle;

	/// <summary>
	/// Feeds one voice, creating its stream on first use.
	/// </summary>
	/// <param name="pcm">Interleaved 16-bit samples, as the guest submitted them.</param>
	/// <param name="byteCount">How much of <paramref name="pcm"/> is valid.</param>
	/// <returns>False when the pool was full and this voice was refused.</returns>
	public bool Feed(
		uint handle, uint generation, uint sampleRate, uint channels,
		float volume, float panLeft, float panRight,
		byte[] pcm, int byteCount)
	{
		ulong key = KeyOf(handle, generation);

		if (!_live.TryGetValue(key, out VoiceStream? stream))
		{
			if (_live.Count >= MaxConcurrentVoices)
			{
				VoicesRefused++;
				if (!_reportedFull)
				{
					_reportedFull = true;
					GD.PushWarning(
						$"[audio] more than {MaxConcurrentVoices} guest voices at once; " +
						"further sounds are being dropped");
				}
				return false;
			}

			stream = new VoiceStream { Name = $"Voice{handle:X8}_{generation}" };
			AddChild(stream);
			stream.Configure(sampleRate, channels, VoiceBufferSeconds);
			_live[key] = stream;
		}

		// Pushed even with nothing new: the stream may still hold undelivered PCM from a
		// frame where the generator was full, and only a push drains it.
		stream.Push(pcm, byteCount, volume, panLeft, panRight, ref _scratch);
		return true;
	}

	/// <summary>
	/// Sets a voice's resample ratio. The guest states pitch in octaves; the native side has
	/// already converted, so this is directly Godot's PitchScale.
	/// </summary>
	public void SetPitch(uint handle, uint generation, float pitchRatio)
	{
		if (_live.TryGetValue(KeyOf(handle, generation), out VoiceStream? stream))
		{
			stream.PitchScale = pitchRatio > 0.0f ? pitchRatio : 1.0f;
		}
	}

	/// <summary>
	/// Drops every voice not in <paramref name="alive"/>.
	///
	/// A retired voice keeps its stream until what it already holds has played out. The
	/// guest destroys a voice as soon as it has submitted the last of a sound, not when the
	/// sound finishes, so tearing down immediately would clip the tail off every effect.
	/// </summary>
	public void RetireAllExcept(HashSet<ulong> alive)
	{
		_reap.Clear();

		foreach ((ulong key, VoiceStream stream) in _live)
		{
			if (alive.Contains(key))
			{
				continue;
			}

			if (stream.HasAudiblePending)
			{
				continue;
			}

			_reap.Add(key);
		}

		foreach (ulong key in _reap)
		{
			_live[key].QueueFree();
			_live.Remove(key);
		}

		if (_reap.Count != 0 && _live.Count < MaxConcurrentVoices)
		{
			_reportedFull = false;
		}
	}

	/// <summary>One guest voice, playing through its own generator.</summary>
	private partial class VoiceStream : AudioStreamPlayer
	{
		private AudioStreamGeneratorPlayback? _playback;
		private uint _channels = 1;

		/// <summary>True while Godot still has samples left to play for this voice.</summary>
		public bool HasAudiblePending
		{
			get
			{
				if (_playback is null)
				{
					return false;
				}

				// Buffer length minus what is free is what is still to be heard. Godot
				// exposes no "pending" directly, so this is the honest way to ask.
				var generator = Stream as AudioStreamGenerator;
				if (generator is null)
				{
					return false;
				}

				if (_pendingCount > 0)
				{
					return true;
				}

				int capacity = (int)(generator.BufferLength * generator.MixRate);
				return _playback.GetFramesAvailable() < capacity - 1;
			}
		}

		public void Configure(uint sampleRate, uint channels, float bufferSeconds)
		{
			// The guest states the rate; playing at it and letting Godot resample is both
			// simpler and better than resampling ourselves.
			_channels = channels == 0 ? 1 : channels;

			Stream = new AudioStreamGenerator
			{
				MixRate = sampleRate == 0 ? Engine_AudioOutput.GuestMixRate : (float)sampleRate,
				BufferLength = bufferSeconds,
			};

			Play();
			_playback = GetStreamPlayback() as AudioStreamGeneratorPlayback;
		}

		/// <summary>
		/// Takes PCM and delivers as much as the generator will accept, KEEPING the rest.
		///
		/// The remainder must be kept, not dropped: by the time it gets here the native
		/// ring has already discarded it, so anything this refuses is gone for good. An
		/// earlier version truncated to the generator's free space and lost the overflow on
		/// every push, which is silent and sounds like a quiet, patchy mix rather than like
		/// a fault.
		/// </summary>
		public void Push(
			byte[] pcm, int byteCount, float volume, float panLeft, float panRight,
			ref Vector2[] scratch)
		{
			if (_playback is null)
			{
				return;
			}

			Append(pcm, byteCount);

			int bytesPerFrame = 2 * (int)_channels;
			int frames = Math.Min(_pendingCount / bytesPerFrame, _playback.GetFramesAvailable());
			if (frames <= 0)
			{
				return;
			}

			pcm = _pending;

			if (scratch.Length != frames)
			{
				scratch = new Vector2[frames];
			}

			const float ToUnit = 1.0f / 32768.0f;
			for (int i = 0; i < frames; i++)
			{
				int at = i * bytesPerFrame;

				// BIG-ENDIAN. The guest is a big-endian target and its decoder says so
				// outright — rexglue's xma_context.cpp ends with "Convert the sample and
				// output it in big endian" / `out[o++] = rex::byte_swap(sample)`. Reading
				// these little-endian swaps every sample's halves, which is not a subtle
				// artefact but full-scale noise.
				short left = (short)((pcm[at] << 8) | pcm[at + 1]);
				short right = _channels >= 2
					? (short)((pcm[at + 2] << 8) | pcm[at + 3])
					: left;

				scratch[i] = new Vector2(
					left * ToUnit * volume * panLeft,
					right * ToUnit * volume * panRight);
			}

			_playback.PushBuffer(scratch);
			Consume(frames * bytesPerFrame);
		}

		/// <summary>Adds to the undelivered tail, growing to fit.</summary>
		private void Append(byte[] pcm, int byteCount)
		{
			if (byteCount <= 0)
			{
				return;
			}

			if (_pendingCount + byteCount > MaxPendingBytesPerVoice)
			{
				// Half a second behind means this voice is not being drained at all, so the
				// oldest audio is already stale. Said once per voice rather than per push.
				if (!_reportedBacklog)
				{
					_reportedBacklog = true;
					GD.PushWarning(
						$"[audio] voice {Name} is more than {MaxPendingBytesPerVoice} bytes " +
						"behind; dropping the backlog");
				}
				_pendingCount = 0;
			}

			if (_pending.Length < _pendingCount + byteCount)
			{
				Array.Resize(ref _pending, Math.Max(_pendingCount + byteCount, _pending.Length * 2));
			}

			Buffer.BlockCopy(pcm, 0, _pending, _pendingCount, byteCount);
			_pendingCount += byteCount;
		}

		/// <summary>Drops the bytes just delivered, keeping the tail contiguous at the front.</summary>
		private void Consume(int byteCount)
		{
			_pendingCount -= byteCount;
			if (_pendingCount > 0)
			{
				Buffer.BlockCopy(_pending, byteCount, _pending, 0, _pendingCount);
			}
		}

		private byte[] _pending = Array.Empty<byte>();
		private int _pendingCount;
		private bool _reportedBacklog;
	}
}
