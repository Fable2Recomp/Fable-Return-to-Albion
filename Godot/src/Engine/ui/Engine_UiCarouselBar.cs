namespace Refii.Ui;

/// <summary>
/// The mouse's scroll bar beside a list's carousel, as arithmetic on the HIGHLIGHT. The carousel
/// has no scroll position of its own — the highlight is pinned to its slot and the column moves
/// under it — so the bar is the highlight: its thumb stands at index over count, and a press or a
/// drag on it names the row the highlight walks to, one ordinary step at a time.
/// </summary>
public static class Engine_UiCarouselBar
{
	/// <summary>
	/// Rows the carousel draws at most: the <see cref="Engine_UiMenuConstants.NumVisible"/> slots
	/// between the gutters, less an end slot the screen fades to nothing.
	/// </summary>
	public static int Capacity(bool fadesTopEnd, bool fadesBottomEnd) =>
		Engine_UiMenuConstants.NumVisible - (fadesTopEnd ? 1 : 0) - (fadesBottomEnd ? 1 : 0);

	/// <summary>Whether the list needs the bar: more rows than the carousel can draw at once.</summary>
	public static bool Shown(int count, int capacity) => count > capacity;

	/// <summary>The thumb's 0..1 place for a 1-based highlight: the first row at the top, the last at the bottom.</summary>
	public static float Position(int highlight, int count) =>
		count <= 1 ? 0f : System.Math.Clamp((highlight - 1) / (float)(count - 1), 0f, 1f);

	/// <summary>The 1-based row a 0..1 place on the bar stands for — the nearest one.</summary>
	public static int IndexAt(float position, int count) =>
		count <= 1 ? 1 : 1 + (int)System.MathF.Round(System.Math.Clamp(position, 0f, 1f) * (count - 1));

	/// <summary>
	/// One step of the highlight toward <paramref name="target"/>, through the model's own
	/// <c>ScrollUp</c>/<c>ScrollDown</c> — the step a D-pad press takes, so it clamps, never wraps
	/// and honours <c>UpDownInputDisabled</c> exactly as the pad does. True when it moved.
	/// </summary>
	public static bool StepToward(Engine_UiExpandableMenu model, int target)
	{
		int at = model.CurrentHighlightIdx;
		return target < at ? model.ScrollUp() : target > at && model.ScrollDown();
	}

	/// <summary>
	/// The pad steps still to send for a list whose cursor is the guest's: the bar asks for
	/// <paramref name="target"/>, the grab found the cursor at <paramref name="start"/>, and
	/// <paramref name="sent"/> steps (signed) are already on their way. Counted from the grab, not
	/// from the cursor read back, which lags the steps in flight.
	/// </summary>
	public static int StepsToSend(int start, int target, int sent) => target - start - sent;
}
