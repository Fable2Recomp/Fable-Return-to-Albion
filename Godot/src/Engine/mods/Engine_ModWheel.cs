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
/// A controller's: on the keyboard and the touchscreen Back still holds the expression wheel. EVERY
/// PLAYER HAS ONE: player one's pad (on each machine online, the local player) and local co-op player
/// two's own pad, each with its own open state and page, and an entry chosen fires for the player who
/// chose it (<see cref="ModInput.Fire"/>). The
/// south wedge stays the wheel's own Close, and the first wedge stays empty: it is the Dog's, which the
/// game hides with no dog, so an entry there could not be seen. The top wedge is the core's
/// (<see cref="Top"/>: photo mode), on every page, and the mods' entries fill the five around it.
/// </remarks>
public static class ModWheel
{
	/// <summary>The wedges a page offers: the second to the seventh.</summary>
	public const int Slots = 6;

	/// <summary>The first wedge an entry may take, in the guest's sector numbering (0 is the Dog's).</summary>
	public const int FirstSector = 1;

	/// <summary>The wheel's own Close, in the guest's sector numbering.</summary>
	public const int CloseSector = 7;

	/// <summary>
	/// The top wedge, in the guest's sector numbering: sector i stands at −45° + i·45°, counter-clockwise
	/// from the south-east Dog (GameUi's stick decode, measured on the guest), so 3 is north.
	/// </summary>
	public const int TopSector = 3;

	/// <summary>An entry the core puts on the wheel: its wedge title, its picture, and what choosing it does.</summary>
	public sealed record Fixed(string Label, string Icon, System.Action Choose);

	/// <summary>The core's entry on the top wedge of every page; null leaves the wedge to the mods.</summary>
	public static Fixed? Top { get; private set; }

	/// <summary>Puts <paramref name="entry"/> on the top wedge, or gives the wedge back to the mods with null.</summary>
	public static void SetTop(Fixed? entry) => Top = entry;

	/// <summary>Whether the top wedge is the core's.</summary>
	public static bool TopTaken => Top is not null;

	/// <summary>One player's wheel.</summary>
	private sealed class Seat
	{
		public bool BackWasDown;

		/// <summary>Back went down for this wheel, and has not come up yet.</summary>
		public bool OwnsBack;

		public bool Open;
		public int Page;
	}

	private static readonly Seat[] _seats = { new(), new() };

	private static Seat SeatOf(int player) => _seats[player == 2 ? 1 : 0];

	/// <summary>Whether player one's wheel is standing open as the mods' wheel.</summary>
	public static bool Open => _seats[0].Open;

	/// <summary>Player one's page, from 0.</summary>
	public static int Page => _seats[0].Page;

	/// <summary>Whether <paramref name="player"/>'s (1 or 2) wheel is standing open as the mods' wheel.</summary>
	public static bool IsOpen(int player) => SeatOf(player).Open;

	/// <summary><paramref name="player"/>'s page, from 0.</summary>
	public static int PageOf(int player) => SeatOf(player).Page;

	/// <summary>The player whose mods' wheel stands open, 1 before 2; 0 for none.</summary>
	public static int OpenPlayer => _seats[0].Open ? 1 : _seats[1].Open ? 2 : 0;

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

	/// <summary>The wedges a page offers the mods: all of them, or all but the top while the core holds it.</summary>
	public static int FreeSlots(bool topTaken) => topTaken ? Slots - 1 : Slots;

	/// <summary>How many pages <paramref name="entries"/> fill: at least one.</summary>
	public static int PageCount(int entries, bool topTaken = false)
	{
		int perPage = FreeSlots(topTaken);
		return entries <= perPage ? 1 : ((entries - 1) / perPage) + 1;
	}

	/// <summary>
	/// Which mod entry stands in <paramref name="sector"/> of <paramref name="page"/>, or -1 for none;
	/// with <paramref name="topTaken"/> the top wedge is skipped and the entries run on past it.
	/// </summary>
	public static int EntryAt(int page, int sector, int entries, bool topTaken = false)
	{
		if (sector < FirstSector || sector >= FirstSector + Slots || page < 0 || (topTaken && sector == TopSector))
		{
			return -1;
		}

		int position = sector - FirstSector - (topTaken && sector > TopSector ? 1 : 0);
		int index = (page * FreeSlots(topTaken)) + position;
		return index < entries ? index : -1;
	}

	/// <summary><paramref name="page"/> stepped by <paramref name="delta"/>, wrapping round.</summary>
	public static int Stepped(int page, int delta, int pages) => pages <= 0 ? 0 : ((page + delta) % pages + pages) % pages;

	/// <summary>
	/// Follows player one's Back for this frame. True when the mods' wheel has Back, so the expression
	/// wheel must not: <paramref name="held"/> then says whether the wheel should stand open.
	/// </summary>
	public static bool Follow(bool back, bool inWorld, out bool held) => Follow(1, back, inWorld, out held);

	/// <summary>Follows <paramref name="player"/>'s Back for this frame, as <see cref="Follow(bool, bool, out bool)"/>.</summary>
	public static bool Follow(int player, bool back, bool inWorld, out bool held)
	{
		Seat seat = SeatOf(player);
		bool pressed = back && !seat.BackWasDown;
		seat.BackWasDown = back;
		if (!inWorld)
		{
			Close(player);
		}

		if (pressed)
		{
			if (seat.Open)
			{
				Close(player);
				seat.OwnsBack = true;
			}
			else if (inWorld && Available(player))
			{
				seat.Open = true;
				seat.Page = 0;
				seat.OwnsBack = true;
			}
		}
		else if (!back)
		{
			seat.OwnsBack = false;
		}

		held = seat.Open;
		return seat.Open || seat.OwnsBack;
	}

	/// <summary>Player one chooses <paramref name="sector"/>.</summary>
	public static void Choose(int sector) => Choose(1, sector);

	/// <summary>
	/// <paramref name="player"/> chooses <paramref name="sector"/>: an entry fires for that player, the
	/// core's top entry runs, Close closes, an empty wedge does nothing.
	/// </summary>
	public static void Choose(int player, int sector)
	{
		Seat seat = SeatOf(player);
		if (!seat.Open)
		{
			return;
		}

		if (sector == CloseSector)
		{
			Close(player);
			return;
		}

		if (Top is { } top && sector == TopSector)
		{
			Close(player);
			top.Choose();
			return;
		}

		List<ModInput.Action> entries = Entries();
		int entry = EntryAt(seat.Page, sector, entries.Count, TopTaken);
		if (entry >= 0)
		{
			Close(player);
			ModInput.Fire(entries[entry], player);
		}
	}

	/// <summary>Turns player one's page; a wheel of one page stays where it is.</summary>
	public static void StepPage(int delta) => StepPage(1, delta);

	/// <summary>Turns <paramref name="player"/>'s page.</summary>
	public static void StepPage(int player, int delta)
	{
		Seat seat = SeatOf(player);
		seat.Page = Stepped(seat.Page, delta, PageCount(Entries().Count, TopTaken));
	}

	public static void Close() => Close(1);

	public static void Close(int player)
	{
		Seat seat = SeatOf(player);
		seat.Open = false;
		seat.Page = 0;
	}

	/// <summary>
	/// Whether <paramref name="player"/> may open the wheel: something is on it, and for player one a
	/// controller is the device in use (player two's Back is always their own pad's).
	/// </summary>
	private static bool Available(int player) =>
		(TopTaken || Entries().Count != 0) &&
		(player == 2 || (!Engine_UiInputMode.Keycaps && !Engine_UiInputMode.Touch));
}
