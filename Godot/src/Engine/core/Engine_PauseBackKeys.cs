namespace Refii.Core;

/// <summary>
/// The keyboard's pause and back keys as the pad's Start and B. Escape is bound to both: in play it
/// pauses (Start), in a menu it backs out (B); Tab pauses in both, which on the pause book is the
/// guest's own Start-to-resume.
/// </summary>
/// <remarks>
/// A PRESS MEANS WHAT IT MEANT WHEN IT WENT DOWN. The mode is taken when the keys are up, and a mode
/// change under a held key leaves it meaning nothing until it is released. Read per frame instead,
/// Escape held past the pause book opening turns from Start into a fresh B — the guest and the
/// host's edge-detected back both see a press and shut the book the moment it opened — and Escape
/// held past a menu closing turns into Start in play and opens the book again.
/// </remarks>
public sealed class Engine_PauseBackKeys
{
	private bool _pressInPlay;

	/// <summary>One frame: the Start and B the held keys stand for.</summary>
	public (bool Start, bool Back) Map(bool playing, bool pauseDown, bool backDown)
	{
		if (!pauseDown && !backDown)
		{
			_pressInPlay = playing;
			return (false, false);
		}

		if (_pressInPlay != playing)
		{
			return (false, false);
		}

		return playing
			? (pauseDown, false)
			: (pauseDown && !backDown, backDown);
	}
}
