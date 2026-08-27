using System;
using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// `scripts/gameface/listlib.lua`, ported from the shipped bytecode.
///
/// Not the expandable menu: this is the flat slot list — an item array, a window of visible slot
/// ELEMENTS, a cursor and two arrows. <see cref="Engine_UiExpandableMenu"/> is the row list every
/// screen uses.
///
/// Nothing in Fable 2 calls it: the eight `List*` globals are set by `listlib` and referenced by
/// no other script in `guiscripts.bnk`, no `.bgf` presentation and no string in `default.xex`.
/// Stock Gameface library code the game ships and never invokes, so this is a record of the
/// contract rather than a live widget.
///
/// The model is four numbers — `CurrItemIndex`, `FirstVisibleItemIndex`, `#ListItem`,
/// `#VisibleItem`. Everything else derives per frame.
/// </summary>
public sealed class Engine_UiListLib
{
	// The eight upvalues `ListUpdateGraphics` closes over, bound in `listlib.dis` main pc
	// 28..35 to main registers holding 6, 3, 40, 100-40, 0, 10-0, 20, 100-20.

	/// <summary>Highlight rise per second — up[0], and BOTH directions for the arrows.</summary>
	public const float RiseRate = 6f;

	/// <summary>Highlight fall per second for a SLOT — up[1]. The arrows do not use it.</summary>
	public const float FallRate = 3f;

	/// <summary>Slot opacity, `up[2] + up[3] * HighlightAmount` (pc 77..82).</summary>
	public const float SlotOpacityRest = 40f;

	public const float SlotOpacityGain = 60f;

	/// <summary>Slot `position.x`, `up[4] + up[5] * HighlightAmount` (pc 83..89).</summary>
	public const float SlotXRest = 0f;

	public const float SlotXTravel = 10f;

	/// <summary>Arrow opacity, `up[6] + up[7] * HighlightAmount` (pc 112..117).</summary>
	public const float ArrowOpacityRest = 20f;

	public const float ArrowOpacityGain = 80f;

	/// <summary>
	/// One visible slot's derived presentation. Two objects, two opacities: `Opacity`/`XOffset`
	/// are the SLOT element's, `TextString`/`TextOpacity` its `name == "Text"` child's. The slot
	/// fades with the highlight; the text is 100 for a real item and 50 for overflow "Empty".
	/// </summary>
	public readonly record struct Slot(
		string TextString,
		float TextOpacity,
		float Opacity,
		float XOffset,
		float HighlightAmount,
		bool IsEmpty);

	/// <summary>The whole list, replaced by the owner when the guest restates it.</summary>
	public List<string> Items { get; } = new();

	/// <summary>`#State.VisibleItem` — how many slot ELEMENTS the widget has.</summary>
	public int VisibleSlots { get; set; } = 8;

	/// <summary>`CurrItemIndex` — 1-based, like the Lua's.</summary>
	public int CurrentIndex { get; private set; } = 1;

	/// <summary>`FirstVisibleItemIndex` — 1-based.</summary>
	public int FirstVisible { get; private set; } = 1;

	/// <summary>`GUI:GetText(item)` — every slot's string goes through it (pc 23..33).</summary>
	public Func<string, string>? GetText { get; set; }

	/// <summary>`GUI:Navigate(list)`, fired by BOTH increment and decrement before the window
	/// term (pc 15..18 / pc 10..13).</summary>
	public Action? OnNavigate { get; set; }

	/// <summary>
	/// `State.Book:SetCurrentPage(CurrItemIndex)` — the tail of both increment and decrement,
	/// guarded on `State.Book` being set (`main.2` pc 32..41, `main.3` pc 25..34). A list bound
	/// to a book turns its page as the cursor moves.
	/// </summary>
	public Action<int>? OnPageChanged { get; set; }

	private readonly List<float> _highlight = new();
	private float _toStartArrow;
	private float _toEndArrow;

	/// <summary>`ListGetCurrentItemIndex`.</summary>
	public int GetCurrentItemIndex() => CurrentIndex;

	/// <summary>`ListCanIncrementCurrentIndex` — `CurrItemIndex &lt; #ListItem`.</summary>
	public bool CanIncrement() => CurrentIndex < Items.Count;

	/// <summary>`ListCanDecrementCurrentIndex` — `1 &lt; CurrItemIndex`.</summary>
	public bool CanDecrement() => CurrentIndex > 1;

	/// <summary>`ListCanScrollTowardsStart` — `1 &lt; FirstVisibleItemIndex`.</summary>
	public bool CanScrollTowardsStart() => FirstVisible > 1;

	/// <summary>`ListCanScrollTowardsEnd` — `First - 1 &lt; #ListItem - #VisibleItem`.</summary>
	public bool CanScrollTowardsEnd() => FirstVisible - 1 < Items.Count - VisibleSlots;

	/// <summary>
	/// `ListUpdateCurrentIndex` — four arms: down and RIGHT increment, up and LEFT decrement
	/// (`main.1` pc 9..43). The `GUI:CanNavigate()` gate and the stick states are the caller's.
	/// </summary>
	public bool UpdateCurrentIndex(
		bool canNavigate, bool pushingDown, bool pushingUp, bool pushingRight, bool pushingLeft)
	{
		if (!canNavigate)
		{
			return false;
		}

		// The Lua's else-if order: down, up, right, left. First one held wins.
		if (pushingDown)
		{
			return Increment();
		}

		if (pushingUp)
		{
			return Decrement();
		}

		if (pushingRight)
		{
			return Increment();
		}

		return pushingLeft && Decrement();
	}

	/// <summary>
	/// `ListIncrementCurrentIndex` — cursor down one, then the window follows by ONE when the
	/// cursor leaves it (`if First - 1 &lt; Curr - #VisibleItem then First = First + 1`), then the
	/// bound book turns its page. Never jumps.
	/// </summary>
	public bool Increment()
	{
		if (!CanIncrement())
		{
			return false;
		}

		++CurrentIndex;
		OnNavigate?.Invoke();
		if (FirstVisible - 1 < CurrentIndex - VisibleSlots)
		{
			++FirstVisible;
		}

		OnPageChanged?.Invoke(CurrentIndex);
		return true;
	}

	/// <summary>`ListDecrementCurrentIndex` — the mirror image, `if Curr &lt; First then First--`.</summary>
	public bool Decrement()
	{
		if (!CanDecrement())
		{
			return false;
		}

		--CurrentIndex;
		OnNavigate?.Invoke();
		if (CurrentIndex < FirstVisible)
		{
			--FirstVisible;
		}

		OnPageChanged?.Invoke(CurrentIndex);
		return true;
	}

	/// <summary>
	/// `ListUpdateGraphics` — one frame of every slot, plus the arrows. The Lua writes straight
	/// onto elements; this returns the derived values and lets the owner write, because which
	/// node is slot n differs per widget.
	///
	/// The in-range test is the SLOT index, not the item index: `if r6 &lt;= #ListItem` (pc 21)
	/// while the item fetched is `ListItem[First - 1 + r6]`. On a scrolled window those disagree
	/// and the shipped behaviour reads past the end; reproduced.
	///
	/// An out-of-range slot keeps its highlight: the overflow branch jumps over the highlight
	/// block, so the value freezes rather than decaying and still feeds `40 + 60*h` opacity and
	/// `0 + 10*h` offset.
	/// </summary>
	public IReadOnlyList<Slot> UpdateGraphics(double delta)
	{
		while (_highlight.Count < VisibleSlots)
		{
			_highlight.Add(0f);
		}

		var slots = new List<Slot>(VisibleSlots);
		float rise = RiseRate * (float)delta;
		float fall = FallRate * (float)delta;
		for (int i = 0; i < VisibleSlots; ++i)
		{
			string text;
			float textOpacity;
			bool empty = i >= Items.Count;
			if (empty)
			{
				text = Engine_UiBoxConstants.ListEmptyText;
				textOpacity = Engine_UiBoxConstants.ListEmptyOpacity;
			}
			else
			{
				int item = FirstVisible - 1 + i;
				string raw = item >= 0 && item < Items.Count ? Items[item] : string.Empty;
				text = GetText is null ? raw : GetText(raw);
				textOpacity = 100f;

				// `if First - 1 + r6 == CurrItemIndex` — rise, else fall, each guarded and
				// clamped (pc 34..72).
				if (item + 1 == CurrentIndex)
				{
					if (_highlight[i] < 1f)
					{
						_highlight[i] = Math.Min(1f, _highlight[i] + rise);
					}
				}
				else if (_highlight[i] > 0f)
				{
					_highlight[i] = Math.Max(0f, _highlight[i] - fall);
				}
			}

			slots.Add(new Slot(
				text,
				textOpacity,
				SlotOpacityRest + (SlotOpacityGain * _highlight[i]),
				SlotXRest + (SlotXTravel * _highlight[i]),
				_highlight[i],
				empty));
		}

		// Both arrow directions slew at the RISE rate — the arrows never read up[1] (pc 101,
		// 120, 143, 162 all fetch up[0]).
		_toStartArrow = Slew(_toStartArrow, CanScrollTowardsStart(), rise);
		_toEndArrow = Slew(_toEndArrow, CanScrollTowardsEnd(), rise);
		return slots;
	}

	/// <summary>The arrows' derived opacity, after <see cref="UpdateGraphics"/>.</summary>
	public float ToStartArrowOpacity => ArrowOpacityRest + (ArrowOpacityGain * _toStartArrow);

	public float ToEndArrowOpacity => ArrowOpacityRest + (ArrowOpacityGain * _toEndArrow);

	private static float Slew(float value, bool toward, float step)
	{
		value += toward ? step : -step;
		return value < 0f ? 0f : value > 1f ? 1f : value;
	}

	public string Describe() =>
		$"{Items.Count} item(s), cursor {CurrentIndex}, window {FirstVisible}.." +
		$"{FirstVisible + VisibleSlots - 1}";
}
