namespace Refii.Ui;

/// <summary>
/// The in-game screen's input vocabulary — the codes its vtable+24 handler answers and the
/// cadence they may be sent at. Shared by the list, the options page level and the blade.
/// </summary>
public static class Engine_UiScreenInput
{
	/// <summary>
	/// Activation and back, answered by every book screen — <c>sub_826D58F0</c> ("OnPressA" on
	/// the screen's UIContract) and <c>sub_826D5AF0</c>.
	/// </summary>
	public const uint Select = 36;

	public const uint Back = 37;

	/// <summary>
	/// The one-off panels' dismiss — `sub_823CDC08` (minigame completed) and `sub_823D3F98`
	/// (quest completion) answer code 6 and nothing else.
	/// </summary>
	public const uint Dismiss = 6;

	/// <summary>
	/// Y on the abilities screen — `sub_826C2958` code 39 clears `+198` (the learn/unlearn arm
	/// `sub_826C2D40` reads) and raises `OnPressA`; the row identity then has to follow.
	/// </summary>
	public const uint Unlearn = 39;

	/// <summary>
	/// The tab cycle — LB/RB. Handlers call the tab controller
	/// (`sub_826B8780`/`sub_826B8878` on `*(GUI+76)`), gated on no open submenu (`screen+420`)
	/// and the availability mask. Forwarded from the host; the GDB action-consumer chain is dead here.
	/// </summary>
	public const uint TabPrev = 72;

	public const uint TabNext = 73;

	/// <summary>
	/// The directional presses. List screens swallow 28..35 (their scrolling is Lua's `onUpdate`
	/// polling `GUI:IsPushingUp()`, hence <c>Engine_UiSource.Current.MenuScroll</c>); only the
	/// options page answers natively — page object on 28/29, row object on 30/31.
	/// </summary>
	public const uint Up = 28;

	public const uint Down = 29;

	public const uint Left = 30;

	public const uint Right = 31;

	/// <summary>
	/// The page level's repeat, matched to the widget's gate, not the menu's. `sub_82881B28`
	/// swallows a directional unless `gate+16/20` (0.3s) has passed, dropping to `gate+24/28`
	/// (0.01s) once repeating — so sit just past the first delay and above the floor.
	/// Separately, `sub_829907F0` dispatches nothing while the widget timer `w+0` is under 0.5s.
	/// </summary>
	public const double PageFirstRepeat = 0.34;

	public const double PageRepeat = 0.12;

	/// <summary>The abilities screen — the one screen with an assignment blade behind it.</summary>
	public const uint AbilitiesScreenId = 14;

	/// <summary>The presentation the assignment blade's art is authored in.</summary>
	public const string AbilitiesPresentation = "abilitiesscreen";
}
