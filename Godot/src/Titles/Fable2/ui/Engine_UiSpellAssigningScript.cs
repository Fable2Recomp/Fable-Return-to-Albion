using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// `spellassigning.lua`, TRANSLATED — the will-assignment carousel's own state machine: the
/// queued row/column moves, the fade-then-move row change, the scroll-then-commit column
/// change, and the six scroller bubbles' show/hide choreography.
///
/// The constants (paths, 5×8 dimensions, slide numbers) are
/// <see cref="Engine_UiScreenConstants"/> and the running widget is the owned
/// <see cref="Engine_UiSpellAssign"/>/<see cref="Engine_UiSpellAssignSet"/>; this class is the
/// reference translation, with the engine natives modeled as state: `goToSlide` on the four
/// animated components becomes their `*Slide` fields,
/// `setAttribute(..., "active"/"opacity"/"position.y"/"ResourceIDString")` becomes the arrays
/// below, and `doneFadingOut`/`doneFadingIn`/`doneScrolling` become the public `OnDone*` methods
/// a driver calls at completion.
///
/// ALL INDICES ARE 1-BASED, as the script's. `CurrentColPosition` IS THE SPELL TYPE (1..8), not
/// an index into the row's available list — `setCurrentAssignedSpell` copies the assigned
/// spell straight into it, and the left/right walks scan TYPES for the next one whose
/// `maxAvailableSpellLevels` clears the row.
///
/// SHIPPED QUIRKS:
/// <list type="bullet">
/// <item><b>Moving the column COMMITS.</b> `onDoneHorizontalScrolAnim` calls
/// `assignCurrentSpellInRow()` — there is no confirm step, matching the guest's
/// `sub_826B5330` behaviour the owned widget reproduces.</item>
/// <item><b>A row change assigns BEFORE it queues.</b> `onUp`/`onDown`/`onGoToRow` call
/// `assignCurrentSpellInRow()` first, so leaving a row locks in whatever the carousel sat
/// on.</item>
/// <item><b>Arriving on a never-assigned row puts the cursor on spell 0.</b> `setRow` copies
/// `assignedSpells[row]` (initially 0) into `CurrentColPosition`; the refresh then draws no
/// centre icon. Kept.</item>
/// <item><b>`onUpdate`'s second arm is gated by a LIVE local, not a dead constant.</b>
/// `spellassigning.dis main.9` loads `R1 = false` at `pc0`, sets `R1 = true` at `pc12` right
/// after `startFadeOut()`, and tests it at `pc16` — mutual exclusion, so a frame that starts a
/// row fade never also starts a column scroll.</item>
/// <item><b>Scroller bubble 6 is the wrap cell</b>: `GoingLeft` shows it on the LEFT flank,
/// `GoingRight` on the RIGHT, and the static refresh never touches it.</item>
/// <item><b>The demo tables are dead in-game</b> — `setupDemoTable1` is empty,
/// `setupDemoTable2`/`setupDebuggingStuff` are studio scaffolding. Kept as shipped.</item>
/// <item><b>`onUp`/`onDown`/`onLeft`/`onRight` are DEBUG-ONLY.</b> `onInitializeReal`
/// (`spellassigning.dis main.7 pc85..154`) registers fourteen events and not one direction;
/// the four directional handlers are registered only by `setupDebuggingStuff`
/// (`main.2 pc7..34`, which then fires `refreshRow` with `parent` and `currentColPosition`).
/// The shipped widget is driven through `goToRow`/`goToCol`.</item>
/// </list>
///
/// The scroller cannot bind: `main.7 pc74..83` fetches the six bubbles as `'H' .. tostring(i)` —
/// `H1`..`H6` — and no such element exists in `hud.bgf` or `abilitiesscreen.bgf`, which carry
/// `HL2`/`HL1`/`HC`/`HR1`/`HR2`.
///
/// The bytecode states all three of the places a decompiler dropped:
/// <list type="bullet">
/// <item>`onUp` queues `currentRowPosition + 1` (`main.3 pc8`, `ADD R3 = R3 ADD 1`) under
/// `row &lt; maxAssignableRows`; `onDown` queues `currentRowPosition − 1` (`main.4 pc7`,
/// `SUB R3 = R3 SUB 1`) under `1 &lt; row`.</item>
/// <item>`setIconImages_Row`'s loops are legible in full at `main.19 pc40..60` — the left
/// flank's `3 − i` wrap and the right flank's `3 + i` are both in the instructions.</item>
/// <item>`getNumSpellsAtLevel*Of` initialise their counter — `main.29 pc0` and `main.30 pc0`
/// are both `LOADK 0`.</item>
/// </list>
/// </summary>
public sealed class Engine_UiSpellAssigningScript
{
	/// <summary>`self.ICON_TEXTURES` — the chunk's own icon list, kept verbatim. The BINARY's
	/// table (with Babel tags, and `.tex` extensions) is
	/// <see cref="Engine_UiScreenConstants.Wills"/>; the two agree on the eight stems.</summary>
	public static readonly string[] IconTextures =
	{
		@"Art\GUI\WheelIcons\Abilities\will_shock.tga",
		@"Art\GUI\WheelIcons\Abilities\will_blast.tga",
		@"Art\GUI\WheelIcons\Abilities\icon_will_time_control.tga",
		@"Art\GUI\WheelIcons\Abilities\will_sword_of_damocles.tga",
		@"Art\GUI\WheelIcons\Abilities\will_vortex.tga",
		@"Art\GUI\WheelIcons\Abilities\will_chaos.tga",
		@"Art\GUI\WheelIcons\Abilities\will_force_push.tga",
		@"Art\GUI\WheelIcons\Abilities\will_dead_rising.tga",
	};

	private const int MaxSpellLevels = Engine_UiScreenConstants.MaxSpellLevels;

	private const int MaxSpellTypes = Engine_UiScreenConstants.MaxSpellTypes;

	private const int NumScrollerBubbles = Engine_UiScreenConstants.SpellScrollerBubbles;

	/// <summary>`self.rowYPositions` — read off the authored art at initialize
	/// (`getAttribute(mainBubbles[n], "position.y")`); the hud.bgf values, index 1..5.</summary>
	public double[] RowYPositions = { 0, -160, -80, 0, 80, 160 };

	/// <summary>`self.maxAssignableRows` — the highest row any spell reaches; rows above it are
	/// inert and unhighlighted.</summary>
	public int MaxAssignableRows { get; private set; }

	/// <summary>`self.assignedSpells[1..5]` — the spell TYPE on each row, 0 = none.</summary>
	public int[] AssignedSpells { get; } = new int[MaxSpellLevels + 1];

	/// <summary>`self.maxAvailableSpellLevels[1..8]` — the highest row each spell type may be
	/// assigned to, 0 = not offered at all.</summary>
	public int[] MaxAvailableSpellLevels { get; } = new int[MaxSpellTypes + 1];

	/// <summary>`self.currentRowPosition` / `self.currentColPosition` — the cursor. The column
	/// IS a spell type; see the class note.</summary>
	public int CurrentRowPosition { get; private set; } = 1;

	public int CurrentColPosition { get; private set; } = 1;

	/// <summary>`self.waitingForFade` / `self.waitingForScroll` — the two animation locks
	/// `onUpdate` serializes moves behind.</summary>
	public bool WaitingForFade { get; private set; }

	public bool WaitingForScroll { get; private set; }

	/// <summary>`self.queuedRow` / `self.queuedCol` — at most one pending move each.</summary>
	public int? QueuedRow { get; private set; }

	public int? QueuedCol { get; private set; }

	/// <summary>`self.adjusterComponent`'s `position.y` and current slide
	/// (static/left/right).</summary>
	public double AdjusterY { get; private set; }

	public int AdjusterSlide { get; private set; } = Engine_UiScreenConstants.SpellSlideStatic;

	/// <summary>`self.rowFadeComponent`'s slide (no-fade/in/out).</summary>
	public int RowFadeSlide { get; private set; } = Engine_UiScreenConstants.SpellRowNoFadeSlide;

	/// <summary>`self.parent`'s slide — the whole component's fade in/out.</summary>
	public int ComponentSlide { get; private set; }

	/// <summary>`self.mainBubbles[1..5]` — icon (0 = authored placeholder untouched), opacity,
	/// and active/inactive slide per row bubble.</summary>
	public int[] MainBubbleIcon { get; } = new int[MaxSpellLevels + 1];

	public double[] MainBubbleOpacity { get; } =
		new double[] { 0, 100, 100, 100, 100, 100 };

	public int[] MainBubbleSlide { get; } = new int[]
	{
		0,
		Engine_UiScreenConstants.SpellBubbleInactiveSlide,
		Engine_UiScreenConstants.SpellBubbleInactiveSlide,
		Engine_UiScreenConstants.SpellBubbleInactiveSlide,
		Engine_UiScreenConstants.SpellBubbleInactiveSlide,
		Engine_UiScreenConstants.SpellBubbleInactiveSlide,
	};

	/// <summary>`self.scrollerBubbles[1..6]` — `active` and each `Icon`'s texture index into
	/// <see cref="IconTextures"/> (1-based, 0 = untouched).</summary>
	public bool[] ScrollerBubbleActive { get; } =
		new bool[] { false, true, true, true, true, true, true };

	public int[] ScrollerBubbleIcon { get; } = new int[NumScrollerBubbles + 1];

	/// <summary>
	/// `self.onInitializeReal` — paths and element fetches are
	/// <see cref="Engine_UiScreenConstants"/>' constants and the art's job; the behavioural
	/// remainder is: capture `rowYPositions` off the five bubbles, register the fourteen event
	/// handlers (all internal here — `fireEvent` on the parent reaches this same table), and
	/// `onReset()`.
	/// </summary>
	public void Initialize() => OnReset();

	/// <summary>`self.onReset` — everything to zero, cursor to (1,1), adjuster parked on row
	/// 1's authored y.</summary>
	public void OnReset()
	{
		MaxAssignableRows = 0;
		Array.Clear(AssignedSpells);
		Array.Clear(MaxAvailableSpellLevels);
		CurrentRowPosition = 1;
		CurrentColPosition = 1;
		AdjusterY = RowYPositions[CurrentRowPosition];
		WaitingForFade = false;
		WaitingForScroll = false;
		QueuedRow = null;
		QueuedCol = null;
	}

	/// <summary>
	/// `self.onUpdate` — starts at most one queued move per frame, row moves first, each behind
	/// both animation locks. The two arms are mutually exclusive: `spellassigning.dis main.9`
	/// sets a local true in the row arm (`pc12`, after `startFadeOut()`) and tests it in the
	/// column arm (`pc16`), the same condition `WaitingForFade` states.
	/// </summary>
	public void OnUpdate()
	{
		if (QueuedRow is not null && !WaitingForScroll && !WaitingForFade)
		{
			StartFadeOut();
		}

		if (QueuedCol is { } queuedCol && !WaitingForScroll && !WaitingForFade)
		{
			if (queuedCol < CurrentColPosition)
			{
				StartScrollLeft(queuedCol);
			}
			else
			{
				StartScrollRight(queuedCol);
			}

			WaitingForScroll = true;
		}
	}

	/// <summary>`self.onUp` — queue row+1, `spellassigning.dis main.3 pc8`. Assigns first, as
	/// shipped. DEBUG-ONLY entry point; see the class note.</summary>
	public void OnUp()
	{
		if (CurrentRowPosition < MaxAssignableRows)
		{
			AssignCurrentSpellInRow();
			QueueRow(CurrentRowPosition + 1);
		}
	}

	/// <summary>`self.onDown` — queue row−1, `spellassigning.dis main.4 pc7`. DEBUG-ONLY entry
	/// point; see the class note.</summary>
	public void OnDown()
	{
		if (CurrentRowPosition > 1)
		{
			AssignCurrentSpellInRow();
			QueueRow(CurrentRowPosition - 1);
		}
	}

	/// <summary>`self.onLeft` — queue the next offered spell type to the left, if any.
	/// DEBUG-ONLY entry point; see the class note.</summary>
	public void OnLeft()
	{
		int left = GetSpellAtLevelLeftOf(CurrentRowPosition, CurrentColPosition);
		if (left > 0)
		{
			QueueCol(left);
		}
	}

	/// <summary>`self.onRight`. DEBUG-ONLY entry point; see the class note.</summary>
	public void OnRight()
	{
		int right = GetSpellAtLevelRightOf(CurrentRowPosition, CurrentColPosition);
		if (right > 0)
		{
			QueueCol(right);
		}
	}

	/// <summary>`self.onGoToRow` — the external row command: assign, then queue.</summary>
	public void OnGoToRow(int row)
	{
		AssignCurrentSpellInRow();
		QueueRow(row);
	}

	/// <summary>`self.onGoToCol`.</summary>
	public void OnGoToCol(int col) => QueueCol(col);

	private void HideElement(int bubble) => ScrollerBubbleActive[bubble] = false;

	private void ShowElement(int bubble) => ScrollerBubbleActive[bubble] = true;

	/// <summary>
	/// `self.onSetInactiveColBubblesGoingLeft` — the flank visibility for a LEFTWARD move,
	/// centre-to-be at `col`. Bubble 6 wraps onto the left flank here. Branch-for-branch.
	/// </summary>
	public void OnSetInactiveColBubblesGoingLeft(int col)
	{
		int left = GetNumSpellsAtLevelLeftOf(CurrentRowPosition, col);
		if (left == 0)
		{
			HideElement(6);
			HideElement(1);
			ShowElement(2);
		}
		else if (left == 1)
		{
			HideElement(6);
			ShowElement(1);
			ShowElement(2);
		}
		else
		{
			ShowElement(6);
			ShowElement(1);
			ShowElement(2);
		}

		int right = GetNumSpellsAtLevelRightOf(CurrentRowPosition, col);
		if (right <= 1)
		{
			HideElement(4);
			HideElement(5);
		}
		else if (right == 2)
		{
			ShowElement(4);
			HideElement(5);
		}
		else
		{
			ShowElement(4);
			ShowElement(5);
		}

		SetIconImagesRow(CurrentRowPosition, col + 1);
	}

	/// <summary>
	/// `self.onSetInactiveColBubblesGoingRight` — bubble 6 wraps onto the RIGHT flank. Not the
	/// mirror of GoingLeft: the left flank hides both bubbles at `left &lt;= 1` where GoingLeft's
	/// right flank shows one at `right == 2`. Shipped asymmetry, kept.
	/// </summary>
	public void OnSetInactiveColBubblesGoingRight(int col)
	{
		int left = GetNumSpellsAtLevelLeftOf(CurrentRowPosition, col);
		if (left <= 1)
		{
			HideElement(1);
			HideElement(2);
		}
		else if (left == 2)
		{
			HideElement(1);
			ShowElement(2);
		}
		else
		{
			ShowElement(1);
			ShowElement(2);
		}

		int right = GetNumSpellsAtLevelRightOf(CurrentRowPosition, col);
		if (right == 0)
		{
			ShowElement(4);
			HideElement(5);
			HideElement(6);
		}
		else if (right == 1)
		{
			ShowElement(4);
			ShowElement(5);
			HideElement(6);
		}
		else
		{
			ShowElement(4);
			ShowElement(5);
			ShowElement(6);
		}

		SetIconImagesRow(CurrentRowPosition, col - 1);
	}

	/// <summary>
	/// `self.onSetInactiveColBubblesStatic` — the settled refresh (`refreshRow`). Never touches
	/// bubble 6, and repaints the assigned column icons too.
	/// </summary>
	public void OnSetInactiveColBubblesStatic(int col)
	{
		int left = GetNumSpellsAtLevelLeftOf(CurrentRowPosition, col);
		int right = GetNumSpellsAtLevelRightOf(CurrentRowPosition, col);

		if (left == 0)
		{
			HideElement(1);
			HideElement(2);
		}
		else if (left == 1)
		{
			HideElement(1);
			ShowElement(2);
		}
		else
		{
			ShowElement(1);
			ShowElement(2);
		}

		if (right == 0)
		{
			HideElement(4);
			HideElement(5);
		}
		else if (right == 1)
		{
			ShowElement(4);
			HideElement(5);
		}
		else
		{
			ShowElement(4);
			ShowElement(5);
		}

		SetIconImagesAssigned();
		SetIconImagesRow(CurrentRowPosition, col);
	}

	/// <summary>`self.setRowIcon` — a scroller bubble's `Icon.ResourceIDString`.</summary>
	public void SetRowIcon(int bubble, int spell)
	{
		ScrollerBubbleIcon[bubble] = spell;
	}

	/// <summary>`self.setColIcon` — a row bubble's `Icon.ResourceIDString`. Spell 0 indexes
	/// nothing in `ICON_TEXTURES` (Lua nil write); modeled as icon 0 = none.</summary>
	public void SetColIcon(int row, int spell)
	{
		MainBubbleIcon[row] = spell;
	}

	/// <summary>
	/// `self.setIconImages_Row` — paint the scroller bubbles with the offered spells around
	/// `centerCol`, `spellassigning.dis main.19`.
	///
	/// The right-flank bound comes from the RAW LEFT COUNT: `pc7..11` is
	/// `numRight = #available − getNumSpellsAtLevelLeftOf(row, centerCol)`, which with the centre
	/// column in the list is (spells to the right) + 1. Both counts clamp to 3 (`pc23..34`).
	///
	/// The left wrap spends a right slot: at `pc40..44`, when `3 − i` reaches 0 the bubble becomes
	/// 6 and `numRight` is decremented, so the flanks cannot both claim the wrap cell. The right
	/// loop is inclusive, `for i = 0, numRight` (`pc51..54`), so its last step indexes one past
	/// the last offered spell — Lua's nil write, which clears that bubble; modeled as icon 0.
	/// </summary>
	public void SetIconImagesRow(int row, int centerCol)
	{
		List<int> available = GetRowAvailableSpells(row);
		int center = available.IndexOf(centerCol);
		if (center < 0)
		{
			return;
		}

		int rawLeft = GetNumSpellsAtLevelLeftOf(row, centerCol);
		int numLeft = Math.Min(rawLeft, 3);
		int numRight = Math.Min(available.Count - rawLeft, 3);

		for (int step = 1; step <= numLeft; ++step)
		{
			int bubble = 3 - step;
			if (bubble <= 0)
			{
				bubble = 6;
				--numRight;
			}

			SetRowIcon(bubble, SpellAt(available, center - step));
		}

		for (int step = 0; step <= numRight; ++step)
		{
			SetRowIcon(3 + step, SpellAt(available, center + step));
		}
	}

	/// <summary>Indexing past the list is Lua's nil, which clears the bubble — icon 0.</summary>
	private static int SpellAt(List<int> available, int index) =>
		index >= 0 && index < available.Count ? available[index] : 0;

	/// <summary>`self.onRefreshCol` (`refreshCol`).</summary>
	public void OnRefreshCol() => SetIconImagesAssigned();

	/// <summary>`self.setIconImages_Assigned` — each assignable row bubble shows its assigned
	/// spell's icon.</summary>
	public void SetIconImagesAssigned()
	{
		for (int row = 1; row <= MaxAssignableRows; ++row)
		{
			SetColIcon(row, AssignedSpells[row]);
		}
	}

	/// <summary>`self.onFadeComponentIn` — the parent's fade-in slide.</summary>
	public void OnFadeComponentIn()
	{
		ComponentSlide = Engine_UiScreenConstants.SpellComponentFadeInSlide;
	}

	/// <summary>`self.onFadeComponentOut`.</summary>
	public void OnFadeComponentOut()
	{
		ComponentSlide = Engine_UiScreenConstants.SpellComponentFadeOutSlide;
	}

	/// <summary>`self.getRowAvailableSpells` — the spell types whose max level clears `row`,
	/// in type order.</summary>
	public List<int> GetRowAvailableSpells(int row)
	{
		var available = new List<int>();
		for (int spell = 1; spell <= MaxSpellTypes; ++spell)
		{
			if (row <= MaxAvailableSpellLevels[spell])
			{
				available.Add(spell);
			}
		}

		return available;
	}

	/// <summary>`self.startScrollLeft` — hide the row bubble, run the adjuster's RIGHT slide
	/// (the strip moves right so the selection moves left), pre-paint the flanks.</summary>
	public void StartScrollLeft(int col)
	{
		MainBubbleOpacity[CurrentRowPosition] = 0;
		AdjusterSlide = Engine_UiScreenConstants.SpellSlideRight;
		OnSetInactiveColBubblesGoingLeft(col);
	}

	/// <summary>`self.startScrollRight` — the LEFT slide, same inversion.</summary>
	public void StartScrollRight(int col)
	{
		MainBubbleOpacity[CurrentRowPosition] = 0;
		AdjusterSlide = Engine_UiScreenConstants.SpellSlideLeft;
		OnSetInactiveColBubblesGoingRight(col);
	}

	/// <summary>`self.getSpellAtLevelRightOf` — the next offered TYPE above `col`, 0 for
	/// none.</summary>
	public int GetSpellAtLevelRightOf(int row, int col)
	{
		if (MaxSpellTypes <= col)
		{
			return 0;
		}

		for (int spell = col + 1; spell <= MaxSpellTypes; ++spell)
		{
			if (row <= MaxAvailableSpellLevels[spell])
			{
				return spell;
			}
		}

		return 0;
	}

	/// <summary>`self.getSpellAtLevelLeftOf`.</summary>
	public int GetSpellAtLevelLeftOf(int row, int col)
	{
		if (col <= 1)
		{
			return 0;
		}

		for (int spell = col - 1; spell >= 1; --spell)
		{
			if (row <= MaxAvailableSpellLevels[spell])
			{
				return spell;
			}
		}

		return 0;
	}

	/// <summary>`self.getNumSpellsAtLevelLeftOf` — counts from zero, `spellassigning.dis
	/// main.29 pc0` (`LOADK 0`).</summary>
	public int GetNumSpellsAtLevelLeftOf(int row, int col)
	{
		if (col <= 1)
		{
			return 0;
		}

		int count = 0;
		for (int spell = 1; spell <= col - 1; ++spell)
		{
			if (row <= MaxAvailableSpellLevels[spell])
			{
				++count;
			}
		}

		return count;
	}

	/// <summary>`self.getNumSpellsAtLevelRightOf` — counts from zero, `spellassigning.dis
	/// main.30 pc0` (`LOADK 0`).</summary>
	public int GetNumSpellsAtLevelRightOf(int row, int col)
	{
		if (MaxSpellTypes <= col)
		{
			return 0;
		}

		int count = 0;
		for (int spell = col + 1; spell <= MaxSpellTypes; ++spell)
		{
			if (row <= MaxAvailableSpellLevels[spell])
			{
				++count;
			}
		}

		return count;
	}

	/// <summary>`self.highlightAssignableBubbles` — every assignable row bubble to the ACTIVE
	/// slide. Bubbles above `MaxAssignableRows` are never reverted by the script.</summary>
	public void HighlightAssignableBubbles()
	{
		for (int row = 1; row <= MaxAssignableRows; ++row)
		{
			MainBubbleSlide[row] = Engine_UiScreenConstants.SpellBubbleActiveSlide;
		}
	}

	/// <summary>`self.setMaxAvailableSpellLevel` — raising any spell's ceiling can raise
	/// `MaxAssignableRows`, re-highlighting.</summary>
	public void SetMaxAvailableSpellLevel(int spell, int maxLevel)
	{
		MaxAvailableSpellLevels[spell] = maxLevel;
		if (MaxAssignableRows < maxLevel)
		{
			MaxAssignableRows = maxLevel;
			HighlightAssignableBubbles();
		}
	}

	/// <summary>`self.onSetMaxAvailableSpellLevel` — the event form; the extra leading
	/// parameter is the event's element argument, dropped as the Lua drops it.</summary>
	public void OnSetMaxAvailableSpellLevel(int spell, int maxLevel) =>
		SetMaxAvailableSpellLevel(spell, maxLevel);

	/// <summary>`self.setCurrentAssignedSpell` — writes the row's assignment; on the CURRENT
	/// row it also moves the column cursor onto the assigned type.</summary>
	public void SetCurrentAssignedSpell(int row, int spell)
	{
		AssignedSpells[row] = spell;
		if (row == CurrentRowPosition)
		{
			CurrentColPosition = spell;
		}
	}

	/// <summary>`self.onSetCurrentAssignedSpell`.</summary>
	public void OnSetCurrentAssignedSpell(int row, int spell) =>
		SetCurrentAssignedSpell(row, spell);

	/// <summary>`self.assignCurrentSpellInRow` — the commit: cursor position into the
	/// assignment table.</summary>
	public void AssignCurrentSpellInRow() =>
		SetCurrentAssignedSpell(CurrentRowPosition, CurrentColPosition);

	/// <summary>`self.queueRow` — no-op on the current row.</summary>
	public void QueueRow(int row)
	{
		if (row != CurrentRowPosition)
		{
			QueuedRow = row;
		}
	}

	/// <summary>`self.queueCol`.</summary>
	public void QueueCol(int col)
	{
		if (col != CurrentColPosition)
		{
			QueuedCol = col;
		}
	}

	/// <summary>
	/// `self.onDoneHorizontalScrolAnim` [sic] — the scroll animation finished: adjuster back to
	/// static, the row bubble back on, land the cursor, COMMIT, and refresh.
	/// </summary>
	public void OnDoneHorizontalScrollAnim()
	{
		AdjusterSlide = Engine_UiScreenConstants.SpellSlideStatic;
		MainBubbleOpacity[CurrentRowPosition] = 100;
		WaitingForScroll = false;

		// HOST GUARD: `main.39 pc12..16` writes `currentColPosition = queuedCol` with no nil
		// test. Unreachable with a nil queue in practice; the guard is ours.
		if (QueuedCol is { } queuedCol)
		{
			CurrentColPosition = queuedCol;
		}

		AssignCurrentSpellInRow();
		QueuedCol = null;

		// `fireEvent("refreshRow", self.parent, self.currentColPosition)` — self-dispatch; the
		// handler registered for `refreshRow` is onSetInactiveColBubblesStatic.
		OnSetInactiveColBubblesStatic(CurrentColPosition);
	}

	/// <summary>`self.setRow` — land the row cursor mid-fade: column onto the row's assigned
	/// spell (0 on a virgin row — see the class note), refresh, park the adjuster.</summary>
	public void SetRow(int row)
	{
		CurrentRowPosition = row;
		CurrentColPosition = AssignedSpells[row];
		OnSetInactiveColBubblesStatic(CurrentColPosition);
		AdjusterY = RowYPositions[CurrentRowPosition];
	}

	/// <summary>`self.startFadeOut` — lock, and run the row fade-out slide.</summary>
	public void StartFadeOut()
	{
		WaitingForFade = true;
		RowFadeSlide = Engine_UiScreenConstants.SpellRowFadeOutSlide;
	}

	/// <summary>`self.onDoneFadeOut` — faded to nothing: move the row, fade back in.</summary>
	public void OnDoneFadeOut()
	{
		// HOST GUARD: `main.42 pc0..2` calls `setRow(queuedRow)` unguarded. Ours, not the
		// script's.
		if (QueuedRow is { } queuedRow)
		{
			SetRow(queuedRow);
		}

		RowFadeSlide = Engine_UiScreenConstants.SpellRowFadeInSlide;
	}

	/// <summary>`self.onDoneFadeIn` — unlock; clear the queue only if it still names the row we
	/// are on (a newer queued row starts the next cycle from `onUpdate`).</summary>
	public void OnDoneFadeIn()
	{
		WaitingForFade = false;
		if (CurrentRowPosition == QueuedRow)
		{
			QueuedRow = null;
		}

		RowFadeSlide = Engine_UiScreenConstants.SpellRowNoFadeSlide;
	}

	/// <summary>`self.setupDemoTable1` — SHIPPED EMPTY.</summary>
	public void SetupDemoTable1()
	{
	}

	/// <summary>`self.setupDemoTable2` — studio demo data; dead in-game (only
	/// `setupDebuggingStuff` calls it). Kept as shipped.</summary>
	public void SetupDemoTable2()
	{
		OnSetMaxAvailableSpellLevel(1, 3);
		OnSetMaxAvailableSpellLevel(2, 1);
		OnSetMaxAvailableSpellLevel(6, 2);
		OnSetCurrentAssignedSpell(1, 1);
		OnSetCurrentAssignedSpell(2, 1);
		OnSetCurrentAssignedSpell(3, 1);
		OnFadeComponentIn();
	}
}
