using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// Reads the live contents of a Fable 2 <c>ExpandableMenu</c> out of a presentation.
///
/// The item list is the guest's and is not in the file: the pause menu's widget carries
/// <c>MenuPopulationScript = "PauseMenu"</c>, which
/// <c>expandablemenupopulationscript.lua</c> answers with an empty table, and entries arrive at
/// runtime through <c>expandablemenuspawning.lua</c>'s <c>SpawnButton</c> handler
/// (<c>g_Param.String1..String4</c>/<c>Float1..Float4</c> into <c>g_Menu:CreateMenuItem</c>).
///
/// Every spawn and refresh handler ends with
/// <c>setAttribute(getElement('Icon.Title', ref), 'TextID', tag)</c>, so the label lands on a
/// real element and the per-frame live element state carries it — read the menu there.
///
/// The button pool is authored, the entries are not: <c>pausemainmenu.bgf</c> ships 26
/// <c>MenuItemButtonN</c> components under <c>ExpandableMenu/Buttons</c>, all at the same
/// authored position (x[-444..-20], y[176..242]) until
/// <c>expandablemenuformatting.lua</c> moves them onto its twelve slots. A button existing
/// means nothing; a button carrying TEXT is an entry.
/// </summary>
public static class Engine_ExpandableMenuItems
{
	/// <summary>The widget element. One per screen that has a list.</summary>
	public const string MenuElementName = "ExpandableMenu";

	/// <summary>
	/// The group holding the button pool, as the formatter reaches it:
	/// <c>g_MenuFormatter:onInitialize</c> does <c>getChildren(getElement('Buttons', self.parent))</c>
	/// and keeps each <c>component</c> child as <c>MenuItems</c>.
	/// </summary>
	public const string ButtonGroupName = "Buttons";

	/// <summary>
	/// The label's parent and the label, the two halves of the <c>Icon.Title</c> path every
	/// spawn and refresh handler writes through.
	/// </summary>
	public const string IconName = "Icon";

	public const string TitleName = "Title";

	/// <summary>One button of the pool, with whatever the guest has put in it.</summary>
	/// <param name="Ordinal">Index of the button component itself.</param>
	/// <param name="Name">Its authored name, e.g. <c>MenuItemButton3</c>.</param>
	/// <param name="TitleOrdinal">Index of its <c>Icon/Title</c> label, or -1 if it has none.</param>
	/// <param name="Text">The label the guest wrote, null when it never wrote one.</param>
	/// <param name="Authored">The tag the FILE puts in this button's label, before any spawn.</param>
	/// <param name="Active">The guest's own draw gate for the button.</param>
	/// <param name="Slide">The button's live slide.</param>
	public readonly record struct Button(
		int Ordinal, string Name, int TitleOrdinal, string? Text, string Authored,
		bool Active, int Slide)
	{
		/// <summary>
		/// True when the guest has put an entry in this button. Gated on TEXT, not the active
		/// bit — the pool carries its authored active state before anything is spawned. Text
		/// alone is not enough either: every button is authored carrying <c>DEFAULT_TEXT_ID</c>
		/// and the guest hands that tag back for the ones it never spawned into, so the label is
		/// compared against the file's own placeholder rather than a hardcoded string.
		/// </summary>
		public bool IsFilled =>
			!string.IsNullOrEmpty(Text) &&
			!string.Equals(Text, Authored, System.StringComparison.Ordinal);
	}

	/// <summary>
	/// Finds the button pool and reads each button's live label. The live arrays are indexed by
	/// the same ordinal join the slides use, passed as plain lists so the engine keeps no
	/// dependency on the bridge; a null array means "the guest said nothing", not an error.
	/// </summary>
	public static IReadOnlyList<Button> Read(Engine_UiScreen? screen,
		IReadOnlyList<string?>? text, IReadOnlyList<bool>? active, IReadOnlyList<int>? slides)
	{
		var buttons = new List<Button>();
		if (screen is null)
		{
			return buttons;
		}

		if (FindGroup(screen) is not { } group)
		{
			return buttons;
		}

		foreach (UiNode child in group.Children)
		{
			UiNode? title = FindTitle(child);
			int titleOrdinal = title?.Ordinal ?? -1;

			buttons.Add(new Button(
				child.Ordinal,
				child.Name,
				titleOrdinal,
				titleOrdinal >= 0 ? At(text, titleOrdinal) : null,
				// The authored tag, not `Display` — the guest's live text has already been
				// folded into that, so the comparison would be against itself.
				title?.Text?.Tag ?? string.Empty,
				At(active, child.Ordinal),
				AtInt(slides, child.Ordinal)));
		}

		return buttons;
	}

	/// <summary>
	/// The `MenuPopulationScript` this screen's `ExpandableMenu` names, or empty. The attribute
	/// is authored on the widget element and `expandablemenupopulationscript.lua` is one switch
	/// on it (`LOADK 'MenuPopulationScript'` then `SETTABLE 'PopulationTable'`), so it is the
	/// join from a running screen to <see cref="Engine_UiMenuPopulation"/>.
	/// </summary>
	public static string PopulationScriptOf(Engine_UiScreen? screen)
	{
		if (screen?.Document is not { } document)
		{
			return string.Empty;
		}

		foreach (UiNode node in screen.Nodes)
		{
			if (!string.Equals(node.Name, MenuElementName, System.StringComparison.Ordinal) ||
				node.Ordinal < 0 || node.Ordinal >= document.Elements.Length)
			{
				continue;
			}

			if (document.Elements[node.Ordinal].TryGetAttribute(
					PopulationScriptAttribute, out BgfAttribute attr) &&
				attr.Type == BgfValueType.String)
			{
				return document.GetString(attr.AsStringIndex());
			}
		}

		return string.Empty;
	}

	/// <summary>The attribute the widget carries — see <see cref="PopulationScriptOf"/>.</summary>
	public const string PopulationScriptAttribute = "MenuPopulationScript";

	/// <summary>
	/// The <c>ExpandableMenu/Buttons</c> group, or null when this screen carries no list. Walked
	/// from the named widget the formatter anchors to, not searched for by name anywhere.
	/// </summary>
	public static UiNode? FindGroup(Engine_UiScreen screen)
	{
		foreach (UiNode node in screen.Nodes)
		{
			if (!string.Equals(node.Name, MenuElementName, System.StringComparison.Ordinal))
			{
				continue;
			}

			foreach (UiNode child in node.Children)
			{
				if (string.Equals(child.Name, ButtonGroupName, System.StringComparison.Ordinal))
				{
					return child;
				}
			}
		}

		return null;
	}

	/// <summary>The button's <c>Icon/Title</c> label element, or null if it has none.</summary>
	private static UiNode? FindTitle(UiNode button)
	{
		foreach (UiNode icon in button.Children)
		{
			if (!string.Equals(icon.Name, IconName, System.StringComparison.Ordinal))
			{
				continue;
			}

			foreach (UiNode title in icon.Children)
			{
				if (string.Equals(title.Name, TitleName, System.StringComparison.Ordinal))
				{
					return title;
				}
			}
		}

		return null;
	}

	/// <summary>
	/// One line saying what the guest has put in the menu. Prints filled entries and pool size
	/// separately — "26 buttons, 0 entries" is a state a single count cannot express.
	/// </summary>
	public static string Describe(IReadOnlyList<Button> buttons)
	{
		if (buttons.Count == 0)
		{
			return "no ExpandableMenu on this screen";
		}

		var filled = new List<string>();
		foreach (Button button in buttons)
		{
			if (button.IsFilled)
			{
				filled.Add($"{button.Name}={button.Text}{(button.Active ? string.Empty : " (not drawn)")}");
			}
		}

		return filled.Count == 0
			? $"{buttons.Count} button(s) in the pool, every one still carrying its authored " +
			  "placeholder — the guest has spawned no entries"
			: $"{filled.Count} of {buttons.Count} button(s) filled: {string.Join(", ", filled)}";
	}

	/// <summary>
	/// One line saying whether the guest's formatter has laid the pool out. The pool is authored
	/// stacked; <c>expandablemenuformatting.lua</c>'s <c>AnimatePropertiesToIndex</c> writes
	/// <c>position.x</c>/<c>position.y</c> onto each component to move it to one of the twelve
	/// slots, so a still-stacked pool is one the formatter has not touched.
	/// </summary>
	/// <param name="transform">
	/// Per-element live transform, six entries per row in the order position x/y/z then scale
	/// x/y/z, as the host reads them. A null row means the guest states none of them.
	/// </param>
	public static string DescribeLayout(IReadOnlyList<Button> buttons,
		IReadOnlyList<float?[]?>? transform)
	{
		if (transform is null)
		{
			return "no live transform read";
		}

		int stated = 0;
		var distinct = new HashSet<(float, float)>();
		foreach (Button button in buttons)
		{
			if (button.Ordinal < 0 || button.Ordinal >= transform.Count ||
				transform[button.Ordinal] is not { } row)
			{
				continue;
			}

			// Indices 0 and 1 are position.x and position.y. A component can state one without
			// the other, so an unstated axis counts as "unchanged" rather than as zero.
			++stated;
			distinct.Add((row[0] ?? float.NaN, row[1] ?? float.NaN));
		}

		if (stated == 0)
		{
			return "the guest states no position for any button — its formatter has NOT run, " +
				"so the pool is still authored stacked";
		}

		if (distinct.Count <= 1)
		{
			return $"{stated} button(s) state a position but they are ALL THE SAME — the " +
				"formatter has not spread the pool";
		}

		// Where each filled one landed. `onAddChild` sets
		// `VisibleIndex = HIGHLIGHT_INDEX + DisplayIndex - CurrentHighlightIdx`, so the y the
		// guest writes IS its slot, and the slots say which entry the guest believes is
		// highlighted — the value its own A press commits against.
		var placed = new List<string>();
		int display = 0;
		var highlights = new HashSet<int>();
		foreach (Button button in buttons)
		{
			if (!button.IsFilled)
			{
				continue;
			}

			++display;
			if (button.Ordinal < 0 || button.Ordinal >= transform.Count ||
				transform[button.Ordinal] is not { } row)
			{
				continue;
			}

			int slot = SlotAt(row[1]);
			placed.Add($"{button.Text}@slot{(slot > 0 ? slot.ToString() : "?")}" +
				$"({row[0]?.ToString("F0") ?? "-"},{row[1]?.ToString("F0") ?? "-"})");

			// Every row solves for the same highlight. Collected as a SET, not taken from the
			// first row: a mid-slide sample has rows in transit and they will disagree.
			if (slot > 0)
			{
				highlights.Add(display - slot + Engine_FrontEndMenuView.HighlightSlot);
			}
		}

		string solved = highlights.Count == 1
			? $"; the guest's CurrentHighlightIdx is {System.Linq.Enumerable.First(highlights)}"
			: highlights.Count == 0
				? "; no row landed on a known slot"
				: "; rows disagree on the highlight — sampled mid-slide";

		return $"{stated} button(s) state a position, {distinct.Count} distinct — the guest's " +
			$"formatter has laid the pool out: {string.Join(' ', placed)}{solved}";
	}

	/// <summary>
	/// Which of the twelve authored slots a stated y lands on, or 0. The y identifies it alone —
	/// <c>g_MenuPositionSlots</c> gives each slot a distinct one, written verbatim. The tolerance
	/// is half the tightest gap, so a mid-slide row reads as "not on a slot".
	/// </summary>
	private static int SlotAt(float? y)
	{
		if (y is not { } value)
		{
			return 0;
		}

		for (int i = 0; i < Engine_FrontEndMenuView.Slots.Length; ++i)
		{
			if (System.Math.Abs(Engine_FrontEndMenuView.Slots[i].Y - value) < 8f)
			{
				return i + 1;
			}
		}

		return 0;
	}

	private static string? At(IReadOnlyList<string?>? values, int index) =>
		values is not null && index >= 0 && index < values.Count ? values[index] : null;

	private static bool At(IReadOnlyList<bool>? values, int index) =>
		values is not null && index >= 0 && index < values.Count && values[index];

	private static int AtInt(IReadOnlyList<int>? values, int index) =>
		values is not null && index >= 0 && index < values.Count ? values[index] : 0;
}
