namespace Refii.Ui;

/// <summary>
/// Which device the player last touched: the controller, or the keyboard and mouse. The UI
/// draws a PC menu (a still list with a scroll bar, keycap prompts) for the second and the
/// original console menu for the first, and swaps the instant the other device is used.
/// </summary>
/// <remarks>
/// Set by the game's input reader; read by every view. <see cref="Generation"/> counts the
/// switches so a view that caches what it drew (a prompt glyph, a laid-out label) can tell its
/// cache is from the other mode.
///
/// A TOUCHSCREEN IS A POINTER THAT PRESSES PAD BUTTONS. While it is the device the menus are the
/// PC menus — rows are tapped where they stand, through <see cref="Engine_UiPointer"/> — but the
/// prompts keep the pad's art, because the on-screen buttons the player presses are the pad's
/// A/B/X/Y: touch is <see cref="KeyboardMouse"/> with <see cref="Touch"/> set, and
/// <see cref="Keycaps"/> is false.
/// </remarks>
public static class Engine_UiInputMode
{
	private static bool _keyboardMouse;
	private static bool _touch;

	/// <summary>True while the keyboard and mouse, or the touchscreen, are the active device.</summary>
	public static bool KeyboardMouse
	{
		get => _keyboardMouse;
		set
		{
			if (_keyboardMouse != value || _touch)
			{
				_keyboardMouse = value;
				_touch = false;
				++Generation;
			}
		}
	}

	/// <summary>
	/// True while the touchscreen is the active device. Setting it makes the pointer the menus'
	/// (<see cref="KeyboardMouse"/> true); setting <see cref="KeyboardMouse"/> clears it.
	/// </summary>
	public static bool Touch
	{
		get => _touch;
		set
		{
			if (_touch == value && (!value || _keyboardMouse))
			{
				return;
			}

			_touch = value;
			_keyboardMouse = value || _keyboardMouse;
			++Generation;
		}
	}

	/// <summary>Whether prompts are drawn as keycaps: the keyboard and mouse, not touch.</summary>
	public static bool Keycaps => _keyboardMouse && !_touch;

	/// <summary>Bumped on every switch.</summary>
	public static int Generation { get; private set; }
}
