using System.Collections.Generic;
using Refii.Ui;

namespace Refii.Mods;

/// <summary>
/// THE CONTROLLER'S MOD WHEEL: every input action a mod declares with <c>"wheel"</c>, in load order,
/// laid out over the game's own expression wheel. Back opens it and closes it, the stick points, A
/// chooses (the action fires as though its button had been pressed), B closes, and LB and RB turn the
/// page when there are more entries than wedges. Each mod takes the next free wedge, so none needs a
/// button of its own.
/// </summary>
/// <remarks>
/// A controller's only: on the keyboard and the touchscreen Back still holds the expression wheel, and
/// with no mod declaring an entry Back does there too. Player one's, like the wheel's other levers. The
/// south wedge stays the wheel's own Close, and the first wedge stays empty: it is the Dog's, which the
/// game hides with no dog, so an entry there could not be seen.
/// </remarks>
public static class ModWheel
{
	/// <summary>The wedges a page offers: the second to the seventh.</summary>
	public const int Slots = 6;

	/// <summary>The first wedge an entry may take, in the guest's sector numbering (0 is the Dog's).</summary>
	public const int FirstSector = 1;

	/// <summary>The wheel's own Close, in the guest's sector numbering.</summary>
	public const int CloseSector = 7;

	private static bool _backWasDown;

	/// <summary>Back went down for this wheel, and has not come up yet.</summary>
	private static bool _ownsBack;

	/// <summary>Whether the wheel is standing open as the mods' wheel.</summary>
	public static bool Open { get; private set; }

	/// <summary>The page showing, from 0.</summary>
	public static int Page { get; private set; }

	/// <summary>The entries on the wheel, in load order.</summary>
	public static List<ModInput.Action> Entries()
	{
		var entries = new List<ModInput.Action>();
		foreach (ModInput.Action action in ModInput.All)
		{
			if (action.Wheel.Length != 0)
			{
				entries.Add(action);
			}
		}

		return entries;
	}

	/// <summary>How many pages <paramref name="entries"/> fill: at least one.</summary>
	public static int PageCount(int entries) => entries <= Slots ? 1 : ((entries - 1) / Slots) + 1;

	/// <summary>Which entry stands in <paramref name="sector"/> of <paramref name="page"/>, or -1 for none.</summary>
	public static int EntryAt(int page, int sector, int entries)
	{
		if (sector < FirstSector || sector >= FirstSector + Slots || page < 0)
		{
			return -1;
		}

		int index = (page * Slots) + (sector - FirstSector);
		return index < entries ? index : -1;
	}

	/// <summary><paramref name="page"/> stepped by <paramref name="delta"/>, wrapping round.</summary>
	public static int Stepped(int page, int delta, int pages) => pages <= 0 ? 0 : ((page + delta) % pages + pages) % pages;

	/// <summary>
	/// Follows the pad's Back for this frame. True when the mods' wheel has Back, so the expression
	/// wheel must not: <paramref name="held"/> then says whether the wheel should stand open.
	/// </summary>
	public static bool Follow(bool back, bool inWorld, out bool held)
	{
		bool pressed = back && !_backWasDown;
		_backWasDown = back;
		if (!inWorld)
		{
			Close();
		}

		if (pressed)
		{
			if (Open)
			{
				Close();
				_ownsBack = true;
			}
			else if (inWorld && Available())
			{
				Open = true;
				Page = 0;
				_ownsBack = true;
			}
		}
		else if (!back)
		{
			_ownsBack = false;
		}

		held = Open;
		return Open || _ownsBack;
	}

	/// <summary>Chooses <paramref name="sector"/>: an entry fires, Close closes, an empty wedge does nothing.</summary>
	public static void Choose(int sector)
	{
		if (!Open)
		{
			return;
		}

		if (sector == CloseSector)
		{
			Close();
			return;
		}

		List<ModInput.Action> entries = Entries();
		int entry = EntryAt(Page, sector, entries.Count);
		if (entry >= 0)
		{
			Close();
			ModInput.Fire(entries[entry]);
		}
	}

	/// <summary>Turns the page; a wheel of one page stays where it is.</summary>
	public static void StepPage(int delta) => Page = Stepped(Page, delta, PageCount(Entries().Count));

	public static void Close()
	{
		Open = false;
		Page = 0;
	}

	/// <summary>Whether a controller is in use and some mod has put something on the wheel.</summary>
	private static bool Available() =>
		!Engine_UiInputMode.Keycaps && !Engine_UiInputMode.Touch && Entries().Count != 0;
}
