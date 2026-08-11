using Godot;

namespace Refii.Audio;

/// <summary>
/// Plays one asset sound, wherever we are.
///
/// A sound has to be audible in three places — inside the Godot editor, inside the editor
/// we package outside Godot, and in the running game — and this is the one implementation
/// all three use. That is the same rule the textures follow: a <c>.tex</c> reaches the
/// screen through the <c>game://</c> resource loader whether the bank browser opened it or
/// the game drew it, and a sound should not get its own private path just because it is
/// heard rather than seen.
///
/// This is deliberately NOT the live guest audio path. <see cref="Engine_AudioOutput"/> and
/// <see cref="Engine_VoicePool"/> are generator-fed, because what the running guest hands
/// over is a stream of PCM with no end. This plays a FILE that is fully known before it
/// starts. Those are different mechanisms for the same reason a video texture and a still
/// image are, and collapsing them would make both worse.
///
/// It attaches itself to whatever tree it is added to and cleans up after itself, so a
/// caller with no scene of its own — the editor plugin — can still use it.
/// </summary>
public partial class Engine_AudioPreview : AudioStreamPlayer
{
	/// <summary>
	/// Loads and plays a sound by its mounted path.
	/// </summary>
	/// <param name="parent">
	/// The node to attach the player to. In the editor this is the plugin itself; in the
	/// game it is whatever owns the sound.
	/// </param>
	/// <param name="gamePath">A mounted path, e.g. <c>game://…/foo.wav</c>.</param>
	/// <returns>The player, or null when the sound could not be loaded.</returns>
	public static Engine_AudioPreview? Play(Node parent, string gamePath)
	{
		var stream = ResourceLoader.Load<AudioStreamWav>(gamePath, "AudioStreamWAV");
		if (stream is null)
		{
			GD.PushWarning($"[audio] {gamePath} did not load as an AudioStreamWAV");
			return null;
		}

		return Play(parent, stream);
	}

	/// <summary>Plays an already-loaded stream.</summary>
	public static Engine_AudioPreview? Play(Node parent, AudioStream stream)
	{
		var player = new Engine_AudioPreview
		{
			Name = "AudioPreview",
			Stream = stream,
		};

		parent.AddChild(player);

		// Freed when it finishes rather than left resident. Previewing a whole bank one
		// sound at a time would otherwise leave a player per click parented to the plugin,
		// which outlives the selection that made it.
		player.Finished += player.QueueFree;
		player.Play();
		return player;
	}

	/// <summary>
	/// Describes a stream the way the bank browser reports it — length, rate, channels.
	/// Shared so the Godot editor and the packaged editor print the same thing.
	/// </summary>
	public static string Describe(AudioStreamWav stream) =>
		$"{stream.GetLength():0.00}s, {stream.MixRate} Hz, " +
		$"{(stream.Stereo ? "stereo" : "mono")}";
}
