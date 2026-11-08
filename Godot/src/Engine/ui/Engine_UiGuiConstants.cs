namespace Refii.Ui;

/// <summary>
/// `guistateandconstants.lua` and `guiinput.lua`, translated — the GUI layer's timing,
/// thresholds and button vocabulary, quoted rather than invented now that navigation is owned.
/// <see cref="Engine_UiNavigationClock"/> is the machine that uses them.
/// </summary>
public static class Engine_UiGuiConstants
{
	/// <summary>`GUI.Timing.FIRST_NAVIGATE_INTERVAL` — delay before a held direction repeats.</summary>
	public const double FirstNavigateInterval = 0.25;

	/// <summary>
	/// `SUBSEQUENT_NAVIGATE_INTERVAL` — every repeat after the first; `DoNavigate` swaps to it
	/// once `NavigationCount` passes 1.
	/// </summary>
	public const double SubsequentNavigateInterval = 0.14;

	/// <summary>`MIN_NAVIGATE_INTERVAL` / `MIN_ADJUST_INTERVAL` — floors the interval may not go
	/// below, and `MIN_GUI_WHEEL_GRAPH_NAV_INTERVAL`, which the wheel graph uses instead.</summary>
	public const double MinNavigateInterval = 0.2;

	public const double MinAdjustInterval = 0.2;

	public const double MinWheelGraphNavigateInterval = 0.6;

	/// <summary>`TRANSITION_TIME` — a screen change.</summary>
	public const double TransitionTime = 0.5;

	/// <summary>`INPUT_INTERVAL_CLAMP` — ceiling on an accumulated "time since" value.
	/// `TimeSinceNavigate` and `TimeSinceAdjust` both start here, so a screen's first press is
	/// never swallowed.</summary>
	public const double InputIntervalClamp = 10.0;

	/// <summary>
	/// `THUMBSTICK_PUSH_THRESHOLD` / `THUMBSTICK_RELEASE_THRESHOLD` — 0.7 to engage, 0.65 to
	/// release. The hysteresis keeps a stick resting near the edge from chattering.
	/// </summary>
	public const float ThumbstickPushThreshold = 0.7f;

	public const float ThumbstickReleaseThreshold = 0.65f;

	/// <summary>`GUI.Const.BUTTON_TYPE_*`.</summary>
	public enum Button
	{
		Up = 1,
		Down = 2,
		Left = 3,
		Right = 4,
		A = 11,
		B = 12,
		X = 13,
		Y = 14,
	}
}
