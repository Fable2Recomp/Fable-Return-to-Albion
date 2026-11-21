namespace Refii.Ui;

/// <summary>
/// THE TEXT A GUEST MODAL BOX WAS OPENED WITH, as its opener stated it.
///
/// The modal router's openers — `sub_8236FA68` (message and query boxes) and `sub_8236FCA8`
/// (the numeric adjuster) — take the box's text as an argument and write it into the box's
/// presentation. They are hooked (Fable2/src/vfs/src/hooks/display_box_hooks.cpp) and post
/// `@Modal.Open kind id text` on the GUI call feed, so the owned surfaces draw the text from the
/// call rather than from the presentation it fills. Keyed by the modal kind and id the latch
/// states. An opener that passed an empty string stated an empty text; a box whose open was not
/// stated has none, and its surface does not stand.
/// </summary>
public static class Engine_UiModalText
{
	private static uint _kind;

	private static uint _id;

	private static string? _text;

	/// <summary>Records one stated open.</summary>
	public static void Stated(uint kind, uint id, string text)
	{
		_kind = kind;
		_id = id;
		_text = text;
	}

	/// <summary>The stated text of the box the latch names (empty when the opener passed none), or
	/// null when its open was not stated.</summary>
	public static string? For(uint kind, uint id) => _text is not null && _kind == kind && _id == id ? _text : null;

	/// <summary>Forgets the stated box — the guest has left the game.</summary>
	public static void Clear()
	{
		_text = null;
		_adjuster = null;
	}

	private static uint _adjusterId;

	private static uint _adjusterWidget;

	private static Engine_UiGuiBoxes.AdjusterState? _adjuster;

	/// <summary>
	/// The router adjuster's numbers as `sub_8236FCA8` was handed them — value, min, max, step,
	/// flags (1 money, 2 percent, 4 sign) — and its widget `gui28[16]`, whose renders state the value
	/// from then on (<see cref="Engine_UiNumericRender"/>).
	/// </summary>
	public static void StatedAdjuster(uint id, int value, int min, int max, int step, int flags, uint widget)
	{
		_adjusterId = id;
		_adjusterWidget = widget;
		_adjuster = new Engine_UiGuiBoxes.AdjusterState(min, max, step, flags & 3, (flags & 4) != 0)
		{
			Value = value,
		};
	}

	/// <summary>The router adjuster the latch names, or null when its open was not stated.</summary>
	public static Engine_UiGuiBoxes.AdjusterState? Adjuster(uint id) =>
		_adjuster is not null && _adjusterId == id ? _adjuster : null;

	/// <summary>The widget of the router adjuster the latch names, 0 when its open was not stated.</summary>
	public static uint AdjusterWidget(uint id) => _adjuster is not null && _adjusterId == id ? _adjusterWidget : 0u;
}
