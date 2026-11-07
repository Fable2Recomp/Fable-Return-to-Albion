using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// THE GUEST'S DISPLAY BOXES, as each class's own Open and Close state them — not read out of its
/// queues. Every box on the owner's two queues is shown through its class's Open (vf1) and taken
/// down through its Close (vf2); those are hooked (Fable2/src/vfs/src/hooks/display_box_hooks.cpp)
/// and post <c>@DisplayBox.Open</c> / <c>.Close</c> (the MessageBox <c>@MessageBox.Open</c> /
/// <c>.Close</c>) with the box's address, its identity, its body and its kind, then the class's
/// own fields. The owned surfaces and the box pages stand on these statements and answer the
/// stated address.
/// </summary>
/// <remarks>
/// <para>The kind is the class's own type — vtable slot 7, <c>li r3,N</c>, which <c>sub_823D0B90</c>
/// maps to a screen: 0 message, 2 query, 3 numeric adjuster, 4 quest box, 5 intermission, 6
/// acceptance, 7 button, 8 the hold-to-accept card.</para>
/// <para>Two queues, each showing one box: the owner's +4 queue takes the message, query, adjuster
/// and quest boxes (`sub_826B99D8(owner + 4, box)`: `GUI.DisplayYesNoQuestion` 0x82304520,
/// `sub_823A22B0`, `CQuestInfoBox_ctor`), the +0x20 queue the intermission, acceptance, button and
/// hold-to-accept boxes (`sub_823A2228` 0x823a2294). A queue's tick opens its head only when its
/// current box is down, so the newest box still open in a queue is the one it shows. The box the
/// router hands input to is the +4 queue's current, else the +0x20 queue's (`owner+24`, else
/// `owner+52` — `ShowingDisplayBox`, gui_display_box.h).</para>
/// <para>The menu box is a different object — one controller at <c>*(guiOwner+56)</c> — stated by
/// its populate and close (<c>@MenuBox.Open</c> / <c>.Close</c>): see <see cref="Menu"/>.</para>
/// <para>Pure .NET: held by tests without a Godot type in the host.</para>
/// </remarks>
public static class Engine_UiDisplayBoxes
{
	public const int Message = 0;
	public const int Query = 2;
	public const int Adjuster = 3;
	public const int Quest = 4;
	public const int Intermission = 5;
	public const int Acceptance = 6;
	public const int Button = 7;
	public const int AcceptHold = 8;

	/// <summary>
	/// The TU1 vtable of each kind's class, for re-checking a stated box: Message 0x820AE668,
	/// Query 0x820AE688, NumericAdjuster 0x820AE6A8, QuestInfoBox 0x820AE6C8, AcceptHold
	/// 0x820EBF1C. Each of these Opens sets the open byte `+12` and its Close clears it
	/// (0x823cf2e4 / 0x823cf38c, 0x823cf7e0 / 0x823cf880, 0x823cfff4 / 0x823d006c, 0x8299e620 /
	/// 0x8299e748); 0 for a kind that is not re-checked.
	/// </summary>
	public static uint VtableOf(int kind) => kind switch
	{
		Message => 0x820AE668u,
		Query => 0x820AE688u,
		Adjuster => 0x820AE6A8u,
		Quest => 0x820AE6C8u,
		AcceptHold => 0x820EBF1Cu,
		_ => 0u,
	};

	/// <summary>One open box as its Open stated it.</summary>
	/// <param name="Address">The box object — what an answer is addressed to.</param>
	/// <param name="Kind">The class's type; see the class note.</param>
	/// <param name="Name">The identity at `box+4`, the name `RemoveDisplayBox` matches.</param>
	/// <param name="Body">The resolved body at `box+8`, what the Open writes into the presentation.</param>
	/// <param name="Fields">The class's own fields, in the order its hook states them.</param>
	public sealed record Box(uint Address, int Kind, string Name, string Body, IReadOnlyList<string> Fields)
	{
		/// <summary>The adjuster's widget, for kind 3: the host steps it by the widget's rule on the
		/// same codes it sends the guest.</summary>
		public Engine_UiGuiBoxes.AdjusterState? Adjuster { get; init; }

		public string Field(int index) => index >= 0 && index < Fields.Count ? Fields[index] : string.Empty;

		public int Number(int index) =>
			int.TryParse(Field(index), System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : 0;

		public bool Flag(int index) => Field(index) == "1";
	}

	private static readonly List<Box> _open = new();

	/// <summary>Moves whenever a box opens or closes.</summary>
	public static ulong Generation { get; private set; }

	/// <summary>Every box stated open, oldest first.</summary>
	public static IReadOnlyList<Box> Open => _open;

	/// <summary>Which queue a kind's boxes go on: 0 the +4 queue, 1 the +0x20 queue.</summary>
	public static int QueueOf(int kind) => kind is Intermission or Acceptance or Button or AcceptHold ? 1 : 0;

	/// <summary>The box one queue shows: the newest of its boxes still open, null when none is.</summary>
	public static Box? ShowingIn(int queue)
	{
		for (int i = _open.Count - 1; i >= 0; --i)
		{
			if (QueueOf(_open[i].Kind) == queue)
			{
				return _open[i];
			}
		}

		return null;
	}

	/// <summary>The box the router hands input to: the +4 queue's, else the +0x20 queue's.</summary>
	public static Box? Showing => ShowingIn(0) ?? ShowingIn(1);

	/// <summary>The box its kind's queue shows, when it is of <paramref name="kind"/>; else null.</summary>
	public static Box? ShowingOf(int kind) => ShowingIn(QueueOf(kind)) is { } box && box.Kind == kind ? box : null;

	/// <summary>A class's Open stated a box.</summary>
	public static Box Opened(uint address, int kind, string name, string body, IReadOnlyList<string> fields)
	{
		_open.RemoveAll(b => b.Address == address);
		var box = new Box(address, kind, name, body, fields)
		{
			Adjuster = kind == Adjuster ? AdjusterFrom(fields) : null,
		};
		_open.Add(box);
		++Generation;
		return box;
	}

	/// <summary>The adjuster widget as the box holds it: value, min, max, step, flags (1 money,
	/// 2 percent, 4 sign) — the ctor's `+68`, `+56`, `+60`, `+64`, `+52`.</summary>
	private static Engine_UiGuiBoxes.AdjusterState AdjusterFrom(IReadOnlyList<string> fields)
	{
		int At(int index) =>
			index < fields.Count && int.TryParse(fields[index], System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : 0;
		int flags = At(4);
		return new Engine_UiGuiBoxes.AdjusterState(At(1), At(2), At(3), flags & 3, (flags & 4) != 0)
		{
			Value = At(0),
		};
	}

	/// <summary>A class's Close stated the box down, or the box is no longer one: it goes.</summary>
	public static bool Closed(uint address)
	{
		if (_open.RemoveAll(b => b.Address == address) == 0)
		{
			return false;
		}

		++Generation;
		return true;
	}

	/// <summary>
	/// Drops every stated box that the probe says is no longer an open box of its class — freed
	/// without its Close being seen. The probe reads the ONE address the class stated (its vtable,
	/// mapped to TU1, and its open byte `+12`), or null when it cannot. Kinds with no re-check
	/// (<see cref="VtableOf"/> 0) are kept. Returns how many went.
	/// </summary>
	public static int Sweep(System.Func<uint, (uint Vtable, bool Open)?> probe)
	{
		int dropped = 0;
		for (int i = _open.Count - 1; i >= 0; --i)
		{
			Box box = _open[i];
			uint vtable = VtableOf(box.Kind);
			if (vtable == 0u || probe(box.Address) is not { } seen)
			{
				continue;
			}

			if (seen.Vtable != vtable || !seen.Open)
			{
				_open.RemoveAt(i);
				++dropped;
			}
		}

		if (dropped != 0)
		{
			++Generation;
		}

		return dropped;
	}

	/// <summary>One menu box as its populate stated it: the title, cancelable, the Orb's player card,
	/// and each row's label (resolved by the receiver) and enabled byte.</summary>
	public sealed record MenuBox(uint Address, Engine_UiGuiBoxes.MenuBoxModel Model, bool Card);

	/// <summary>The menu box stated open, null once its close is stated.</summary>
	public static MenuBox? Menu { get; private set; }

	/// <summary>The populate stated the box — again on a row's enable, so a repeat replaces it.</summary>
	public static void MenuOpened(uint address, Engine_UiGuiBoxes.MenuBoxModel model, bool card)
	{
		Menu = new MenuBox(address, model, card);
		++Generation;
	}

	/// <summary>The close stated the box down.</summary>
	public static void MenuClosed(uint address)
	{
		if (Menu is { } menu && (address == 0u || menu.Address == address))
		{
			Menu = null;
			++Generation;
		}
	}

	/// <summary>
	/// Parses one stated menu row — its tag prefixed <c>+</c> enabled or <c>-</c> disabled — into
	/// the tag and the flag; null for an empty slot.
	/// </summary>
	public static (string Tag, bool Enabled)? Row(string row) =>
		row.Length == 0 ? null : row[0] switch
		{
			'+' => (row[1..], true),
			'-' => (row[1..], false),
			_ => (row, true),
		};

	/// <summary>Forgets everything — the guest left the game.</summary>
	public static void Clear()
	{
		_open.Clear();
		Menu = null;
		++Generation;
	}
}
