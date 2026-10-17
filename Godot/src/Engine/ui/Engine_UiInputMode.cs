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
/// </remarks>
public static class Engine_UiInputMode
{
	private static bool _keyboardMouse;

	/// <summary>True while the keyboard and mouse are the active device.</summary>
	public static bool KeyboardMouse
	{
		get => _keyboardMouse;
		set
		{
			if (_keyboardMouse != value)
			{
				_keyboardMouse = value;
				++Generation;
			}
		}
	}

	/// <summary>Bumped on every switch.</summary>
	public static int Generation { get; private set; }
}
