namespace Refii.Titles.Fable3;

/// <summary>
/// What a press on Fable 3's boxes, menu box and Options screen means, as the guest tick action
/// that carries Godot's answer to the box's own answer function (Fable3/src/hooks/src/
/// display_box_hooks.cpp, options_hooks.cpp). Pure logic, so the tests can run it.
/// </summary>
/// <remarks>
/// THE ANSWERS ARE THE GUEST'S OWN CALLS. A question: sub_8281BD88(box, 1 / 0), which posts
/// message event 68 with it (0x8281BE08); the natal query its handler with 41 / 42. The numeric
/// adjuster: its value stored where its answer reads it (+112, 0x8281C688), then
/// sub_8281C638(box, accept), posting event 69 or 70. A message: its handler with 73
/// (GUI_CANCEL: the cue and the close, 0x8281B3D0..0x8281B404), or 76 on the download form (+33,
/// 0x8281B418). A quest box: its handler with 73 (event 67, 0x8281CBF0). A tutorial notice: its
/// handler with 41 (the close, 0x824B29F0). The pause screen's message box: the screen input
/// sub_825CE380 with 72 (GUI_SELECT, 0x825CE3B4), or 76 on its download form (0x825CE3D8). The
/// menu box: its highlight sub_825F9F38(MB, row) then its select sub_825FA850(MB), or its cancel
/// (`+13 = 1`, the close; 0x825FA0F0). The Options screen: one game action into its handler
/// sub_825CA410 — 29/30 up/down, 31/32 left/right, 74 A, 75 B, 77 Y.
/// </remarks>
public static class Fable3BoxAnswers
{
	/// <summary>The presses a box reads this frame: each true on the frame its button went down.</summary>
	public readonly record struct Press(bool A, bool B, bool X, bool Y, bool Up, bool Down, bool Left, bool Right)
	{
		public bool Any => A || B || X || Y || Up || Down || Left || Right;
	}

	/// <summary>The box kinds (display_box_hooks.cpp's `KindOf`) whose pad Godot owns.</summary>
	public static bool Owns(string kind) => kind is "message" or "question" or "adjuster" or "quest" or "notice" or "modal";

	public const int ActionUp = 29;
	public const int ActionDown = 30;
	public const int ActionLeft = 31;
	public const int ActionRight = 32;
	public const int ActionA = 74;
	public const int ActionB = 75;
	public const int ActionY = 77;

	/// <summary>
	/// The tick action that answers the box at <paramref name="address"/> of <paramref name="kind"/>
	/// for <paramref name="press"/>, or null when the press is not an answer.
	/// <paramref name="adjusterValue"/> is the value Godot's adjuster holds; <paramref name="download"/>
	/// whether the message is the download form, whose X is its own action.
	/// </summary>
	public static string? ForBox(string kind, uint address, Press press, int adjusterValue = 0, bool download = false)
	{
		string box = "box:" + address.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ":";
		switch (kind)
		{
			case "question":
				return press.A ? box + "yes" : press.B ? box + "no" : null;
			case "adjuster":
				return press.A ? box + "accept:" + adjusterValue.ToString(System.Globalization.CultureInfo.InvariantCulture) :
					press.B ? box + "cancel" : null;
			case "message":
				if (press.X && download)
				{
					return box + "action";
				}

				return press.A || press.B ? box + "dismiss" : null;
			case "modal":
				// sub_825CE380 takes only 72 (GUI_SELECT) and, on the download form, 76.
				if (press.X && download)
				{
					return box + "action";
				}

				return press.A ? box + "dismiss" : null;
			case "quest":
				return press.A || press.B ? box + "dismiss" : null;
			case "notice":
				return press.A ? box + "dismiss" : null;
			default:
				return null;
		}
	}

	/// <summary>Picks row <paramref name="row"/> (0-based) of the open menu box.</summary>
	public static string MenuPick(int row) => "menubox:" + row.ToString(System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>Cancels the open menu box (the guest takes it only when the box is cancelable).</summary>
	public const string MenuCancel = "menubox-cancel";

	/// <summary>The menu box's highlight after an up or down, wrapping as the guest's handler does (0x825FA138..0x825FA14C).</summary>
	public static int MenuStep(int highlight, int rows, bool down)
	{
		if (rows <= 0)
		{
			return 0;
		}

		int next = highlight + (down ? 1 : -1);
		return next >= rows ? 0 : next < 0 ? rows - 1 : next;
	}

	/// <summary>The Options screen's game action for a press, or null; A wins over a direction pressed with it.</summary>
	public static int? OptionsAction(Press press) =>
		press.A ? ActionA :
		press.B ? ActionB :
		press.Y ? ActionY :
		press.Up ? ActionUp :
		press.Down ? ActionDown :
		press.Left ? ActionLeft :
		press.Right ? ActionRight :
		null;

	/// <summary>The tick action for an Options game action.</summary>
	public static string Options(int action) => "options:" + action.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
