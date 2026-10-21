namespace Refii.Core;

/// <summary>
/// The mouse buttons as pad presses: a button is held for play from a press made while the cursor
/// was the camera's (captured) until it is released.
/// </summary>
/// <remarks>
/// A PRESS BELONGS TO WHERE IT WENT DOWN. Read per frame as "down and captured", a held button
/// lost its hold whenever an overlay freed the cursor — the spell blade (right trigger) and the
/// expression wheel free it while held — so the left trigger it stood for (the target lock) let
/// go under them, and came back as a fresh press when they closed: a held lock released and
/// re-picked its target, and a toggled lock (Souls Combat flips on each press) switched off. A
/// button already down when the cursor is captured, from a menu click, likewise reappeared as a
/// press. Now a press counts only if it began while captured, and it holds through the cursor being
/// freed until the button comes up; one that began over a free cursor is the menus' and never
/// becomes a pad press.
/// </remarks>
public sealed class Engine_PlayButtons
{
	private readonly bool[] _down;
	private readonly bool[] _held;

	/// <param name="count">One past the highest button index used.</param>
	public Engine_PlayButtons(int count)
	{
		_down = new bool[count];
		_held = new bool[count];
	}

	/// <summary>One frame of <paramref name="button"/>: whether it is down, and whether the cursor is captured.</summary>
	public void Update(int button, bool down, bool captured)
	{
		if ((uint)button >= (uint)_down.Length)
		{
			return;
		}

		if (!down)
		{
			_held[button] = false;
		}
		else if (!_down[button])
		{
			_held[button] = captured;
		}

		_down[button] = down;
	}

	/// <summary>Whether <paramref name="button"/> is held for play.</summary>
	public bool Held(int button) => (uint)button < (uint)_held.Length && _held[button];

	/// <summary>
	/// Every hold ends (the devices are muted: the window lost focus). A button still down stays
	/// unheld until it is pressed again.
	/// </summary>
	public void ReleaseAll()
	{
		for (int i = 0; i < _held.Length; ++i)
		{
			_held[i] = false;
			_down[i] = true;
		}
	}
}
