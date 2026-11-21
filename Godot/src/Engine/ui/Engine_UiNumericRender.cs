using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// THE NUMERIC ADJUSTER'S RENDER, as its widget states it. The widget's render `sub_826B8BB0(widget)`
/// (disc `sub_8240CA98`) is the one writer of the adjuster's value text — run by both openers and
/// after every step — and it is hooked (Fable2/src/vfs/src/hooks/display_box_hooks.cpp) to post
/// `@Adjuster.Render widget value min max step flags`: the fields it formats and gates the arrows
/// by. Keyed by the widget the box's open states (`@Modal.Open`'s last argument, the display box's
/// field 5). The latest render of a widget is what the box shows.
/// </summary>
/// <remarks>Pure .NET: held by tests without a Godot type in the host.</remarks>
public static class Engine_UiNumericRender
{
	private static readonly Dictionary<uint, Engine_UiGuiBoxes.AdjusterState> _rendered = new();

	/// <summary>Records one render: flags 1 money, 2 percent, 4 sign.</summary>
	public static void Stated(uint widget, int value, int min, int max, int step, int flags)
	{
		_rendered[widget] = new Engine_UiGuiBoxes.AdjusterState(min, max, step, flags & 3, (flags & 4) != 0)
		{
			Value = value,
		};
	}

	/// <summary>The widget's latest render, null when it has stated none.</summary>
	public static Engine_UiGuiBoxes.AdjusterState? Of(uint widget) =>
		widget != 0u && _rendered.TryGetValue(widget, out Engine_UiGuiBoxes.AdjusterState? state) ? state : null;

	/// <summary>Forgets every render — the guest has left the game.</summary>
	public static void Clear() => _rendered.Clear();
}
