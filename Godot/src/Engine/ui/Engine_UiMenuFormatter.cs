using Godot;

namespace Refii.Ui;

/// <summary>
/// `g_MenuFormatter`'s pure rules — the slot arithmetic, the indent, the rating decode and the
/// auto-collapse walk. Quoted from `expandablemenuformatting.lua` and
/// `expandablemenuemphasisandghosting.lua`; <see cref="Engine_FrontEndMenuView"/> is the surface
/// that applies them, and carries the per-slot style, face and ghosting rules itself because
/// they read the LIVE slot table the per-screen end fade mutates.
///
/// Nothing here touches a node, so the rules can be checked without a screen and can drive a
/// Godot-authored menu that has no `.bgf` behind it.
///
/// Three separate channels:
/// <list type="bullet">
/// <item>Depth is an INDENT and a SCALE, never a face. `GetMenuItemTabOffset` is
/// `-65 - 5*(depth-1)`, plus 24 when the row is not a heading; `GetMenuItemScaleDelta` adds
/// `HEADER_SCALE - 1` or `DEFAULT_SCALE - 1` on top of the slot's own scale.</item>
/// <item>Emphasis is the FACE. `UpdateEmphasisForItem` swaps the group opacities of
/// `Tab.InnerBrown` and `Tab.InnerRed` — a root row with no emphasis is red 100 / brown 0, so
/// brown is the mark.</item>
/// <item>Ghosting is the TEXT. `SetGhosting` writes only `Icon.Title`: a live row is
/// (255, 224, 128) at full, a ghosted heading keeps that colour at 40, a ghosted child goes
/// black at 90. It never touches the tab.</item>
/// </list>
/// </summary>
public static class Engine_UiMenuFormatter
{
	/// <summary>
	/// `forceReset`'s slot arithmetic: `VisibleIndex = DisplayIndex + 3 - CurrentHighlightIdx + 1`,
	/// both 1-based. The highlighted row lands on
	/// <see cref="Engine_UiMenuConstants.HighlightSlot"/> by construction — the cursor does not
	/// move, the list does.
	/// </summary>
	public static int SlotFor(int displayIndex, int highlightIndex) =>
		displayIndex - highlightIndex + Engine_UiMenuConstants.HighlightSlot;

	/// <summary>
	/// `GetMenuItemTabOffset(item)` — `-65 - 5*(depth-1)`, plus 24 for a row that is NOT a
	/// heading.
	///
	/// The bonus is on the CHILD, not the heading. `main.38`:
	/// <code>
	/// 14  TEST A=7 C=1     ; R7 = IsHeading(item)
	/// 15  JMP  -> 17       ; taken when IsHeading is TRUE, skipping the ADD
	/// 16  ADD  R3 = R3 + 24
	/// </code>
	/// Lua 5.1's `TEST A C` runs the following `JMP` when `bool(R(A)) == C`, so a heading jumps
	/// over the `+24` and a plain row falls into it: headings run -65, -70, -75 …, children -41,
	/// -46, -51 … (the equipment screen states -65 for its eight root categories, -46 inside an
	/// opened one).
	///
	/// Recovering headedness BACKWARDS from an observed offset is exact only to depth 3 — past
	/// that the two families interleave (a depth-6 heading is -90, a depth-11 child -91).
	/// Computing forward, as here, has no such limit.
	/// </summary>
	public static float TabOffsetFor(int depth, bool heading) =>
		Engine_UiMenuConstants.TabOffset +
		(Engine_UiMenuConstants.TabOffsetPerDepth * (depth - 1)) +
		(heading ? 0f : Engine_UiMenuConstants.HeadingTabBonus);

	/// <summary>
	/// A row's rating art, as `SetAttributesToVisibleIndex` drives it (main.30). Two displays
	/// behind one number, chosen by magnitude:
	/// <list type="bullet">
	/// <item>Below 100, a STAR slide: `Tab.Stars` goes to `RatingSlide` clamped into
	/// `[ONE_STAR_EMPTY_RATING_SLIDE .. FIVE_STAR_FULL_RATING_SLIDE]` (1..10, five stars in half
	/// steps); outside that range it falls to `EMPTY_RATING_SLIDE`. Every pip is cleared.</item>
	/// <item>100 or above, a PIP string: a divisor walks down from 1,000,000 by ten per pip, five
	/// pips, each base-10 digit read as 0/1/2 (empty/half/full) and subtracted. 2211100 is full,
	/// full, half, half, half.</item>
	/// </list>
	/// </summary>
	public readonly record struct RowRating(int StarSlide, int[] PipSlides)
	{
		/// <summary>The slide an untouched star strip and every cleared pip sits on.</summary>
		public const int PipEmpty = 1;

		public const int PipHalf = 2;

		public const int PipFull = 3;
	}

	/// <summary>How many pips a row's strip carries — `for i = 1, 5`.</summary>
	public const int PipCount = 5;

	/// <summary>The divisor the pip walk starts at, before the first divide by ten.</summary>
	public const int PipFirstDivisor = 1000000;

	/// <summary>`SetAttributesToVisibleIndex`'s rating half, for one row.</summary>
	public static RowRating RatingFor(Engine_UiExpandableMenu.Item item)
	{
		var pips = new int[PipCount];
		for (int i = 0; i < PipCount; ++i)
		{
			pips[i] = RowRating.PipEmpty;
		}

		int rating = item.RatingSlide;

		// The star strip is driven either way — the script falls through to it after both
		// branches, so a pip row still parks its stars on the empty slide.
		int star = rating < Engine_UiMenuConstants.OneStarEmptyRatingSlide ||
			rating > Engine_UiMenuConstants.FiveStarFullRatingSlide
				? Engine_UiMenuConstants.EmptyRatingSlide
				: rating;

		if (rating < 100)
		{
			return new RowRating(star, pips);
		}

		int remaining = rating;
		int divisor = PipFirstDivisor;
		for (int i = 0; i < PipCount; ++i)
		{
			if (divisor <= remaining)
			{
				if (2 * divisor <= remaining)
				{
					remaining -= 2 * divisor;
					pips[i] = RowRating.PipFull;
				}
				else
				{
					remaining -= divisor;
					pips[i] = RowRating.PipHalf;
				}
			}

			divisor /= 10;
		}

		return new RowRating(star, pips);
	}

	/// <summary>
	/// `AutoCollapse` — the item <paramref name="opening"/> must close first, or null.
	///
	/// The scope is the whole VISIBLE list, not the siblings:
	/// `expandablemenuformatting.dis main.6 pc0..4` walks
	/// `g_Menu.VisibleInfo.VisibleElements` at every depth and spares only the row being opened
	/// (`pc9 EQ A=1 B=5 C=1`, loop key against index). The menu is an accordion with exactly one
	/// open folder anywhere in it.
	/// </summary>
	public static Engine_UiExpandableMenu.Item? AutoCollapse(
		Engine_UiExpandableMenu menu, Engine_UiExpandableMenu.Item opening)
	{
		var ancestors = new System.Collections.Generic.HashSet<Engine_UiExpandableMenu.Item>();
		if (opening.HostOnly)
		{
			for (Engine_UiExpandableMenu.Item? parent = opening.Parent;
				parent is not null; parent = parent.Parent)
			{
				ancestors.Add(parent);
			}
		}

		foreach (Engine_UiExpandableMenu.Item row in menu.Visible)
		{
			if (!ReferenceEquals(row, opening) && !ancestors.Contains(row) && row.IsOpen)
			{
				return row;
			}
		}

		return null;
	}
}
