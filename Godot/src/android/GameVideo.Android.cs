using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Game;

/// <summary>
/// The phone's startup videos played ahead: started by the host before the guest asks for them,
/// while the game data still mounts (AndroidStartup).
/// </summary>
/// <remarks>
/// The guest's own request for a video played ahead, or still waiting its turn, does not start
/// it again: AndroidNativeSession retires that request after a moment, as a tap would, and the
/// guest carries on with its startup behind the video the host is showing.
/// </remarks>
public partial class GameVideo
{
	private readonly List<byte[]> _ahead = new();
	private bool _startingAhead;
	private bool _playingAhead;

	/// <summary>Every movie the guest asks for, with the bytes it would play; the host learns its startup videos from these.</summary>
	public Action<byte[]>? GuestRequested { get; set; }

	/// <summary>Whether the movie on screen is one the host started ahead of the guest.</summary>
	public bool PlayingAhead => _playingAhead && _decoder is not null;

	/// <summary>The videos the host will play ahead, so a request of the guest's for one is not shown twice.</summary>
	public void ExpectAhead(byte[] bik) => _ahead.Add(bik);

	/// <summary>
	/// Plays <paramref name="bik"/> now, with its first sound track: before the guest runs, nothing
	/// states the tracks (the startup logos' is 0).
	/// </summary>
	public void PlayAhead(byte[] bik)
	{
		_startingAhead = true;
		StartVideo(bik);
		_startingAhead = false;
		_playingAhead = _decoder is not null;
		if (_decoder is null || _soundPlayback is not null || _decoder.AudioTrackCount == 0) return;
		_trackSelect[0] = 0;
		if (_decoder.SelectAudio(_trackSelect, 1) == 0 || _decoder.AudioRate == 0) return;
		_sound.Stream = new AudioStreamGenerator { MixRate = _decoder.AudioRate, BufferLength = SoundBufferSeconds };
		_sound.Play();
		_soundPlayback = _sound.GetStreamPlayback() as AudioStreamGeneratorPlayback;
	}

	/// <summary>The hook in <see cref="StartVideo"/>: whether this request of the guest's is a video played ahead.</summary>
	private bool TakenAhead(byte[]? requested)
	{
		if (_startingAhead || requested is null) return false;
		GuestRequested?.Invoke(requested);
		foreach (byte[] bik in _ahead)
		{
			if (bik.AsSpan().SequenceEqual(requested))
			{
				GD.Print($"[game-video] the guest asked for a video played ahead ({requested.Length} bytes); not shown again");
				return true;
			}
		}

		_playingAhead = false;
		return false;
	}
}
