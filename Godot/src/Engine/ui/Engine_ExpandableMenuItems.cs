using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// Reads the live contents of a Fable 2 <c>ExpandableMenu</c> out of a presentation.
///
/// THE ITEM LIST IS THE GUEST'S, AND IT IS NOT IN THE FILE. The pause menu's
/// <c>ExpandableMenu</c> element carries <c>MenuPopulationScript = "PauseMenu"</c>, and the
/// shipped <c>expandablemenupopulationscript.lua</c> answers that name with an EMPTY table —
/// <c>g_Menu.PopulationTable = { HighlightIdx = 2 }</c>, zero entries — where the same
/// function builds real lists for <c>OptionsMenu</c>, <c>LogbookMenu</c>, <c>CrimeMenu</c> and
/// the rest. So unlike every static menu in the game, what the pause menu CONTAINS is decided
/// at runtime: <c>expandablemenuspawning.lua</c> registers a <c>SpawnButton</c> handler that
/// takes <c>g_Param.String1..String4</c> / <c>Float1..Float4</c> and calls
/// <c>g_Menu:CreateMenuItem</c>, and the entries arrive that way.
///
/// WHICH MAKES THE ELEMENT TREE THE PLACE TO READ THEM. Every one of those spawn and refresh
/// handlers ends the same way — <c>setAttribute(getElement('Icon.Title', ref), 'TextID', tag)</c>
/// — so an item's label is written onto a real element of the presentation, and the live
/// element state we already pull per frame carries it. That is the guest STATING its menu,
/// which is the thing to follow; scraping it back out of the Lua VM is not something the
/// bridge can do, and a host-side list of what a pause menu ought to contain would be exactly
/// the invention this project keeps deleting.
///
/// THE BUTTON POOL IS AUTHORED, THE ENTRIES ARE NOT. <c>pausemainmenu.bgf</c> ships 26
/// <c>MenuItemButtonN</c> components under <c>ExpandableMenu/Buttons</c>, all authored at the
/// SAME position — x[-444..-20], y[176..242] — because <c>expandablemenuformatting.lua</c>
/// moves them onto its twelve position slots at runtime. Built straight from the file they
/// stack into what looks like one gold bar in the top-left corner, which is precisely what a
/// screenshot of our pause menu showed. So a button existing means nothing; a button carrying
/// TEXT is an entry.
/// </summary>
public static class Engine_ExpandableMenuItems
{
	/// <summary>The widget element. One per screen that has a list.</summary>
	public const string MenuElementName = "ExpandableMenu";

	/// <summary>
	/// The group holding the button pool.
	///
	/// Named exactly as the formatter reaches it: <c>g_MenuFormatter:onInitialize</c> does
	/// <c>getChildren(getElement('Buttons', self.parent))</c> and keeps each child whose
	/// element type is <c>component</c>, storing them as <c>MenuItems</c>.
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
	/// <param name="Active">The guest's own draw gate for the button.</param>
	/// <param name="Slide">The button's live slide.</param>
	public readonly record struct Button(
		int Ordinal, string Name, int TitleOrdinal, string? Text, bool Active, int Slide)
	{
		/// <summary>
		/// True when the guest has actually put an entry in this button.
		///
		/// TEXT, NOT THE ACTIVE BIT. The pool's buttons carry their authored active state
		/// before anything is spawned into them, so gating on the draw gate counts 26 entries
		/// on a menu that has none. A label is written only by a spawn or a refresh.
		/// </summary>
		public bool IsFilled => !string.IsNullOrEmpty(Text);
	}

	/// <summary>
	/// Finds the button pool and reads each button's live label.
	///
	/// The live arrays are the ones <c>GameUi</c> already builds per presentation, indexed by
	/// the same ordinal join the slides go through — passed as plain lists rather than the
	/// host's element record so the engine keeps no dependency on the bridge. A null array is
	/// treated as "the guest said nothing", not as an error: this is also called against a
	/// screen built with no guest attached.
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
			int titleOrdinal = FindTitle(child) is { } title ? title.Ordinal : -1;

			buttons.Add(new Button(
				child.Ordinal,
				child.Name,
				titleOrdinal,
				titleOrdinal >= 0 ? At(text, titleOrdinal) : null,
				At(active, child.Ordinal),
				AtInt(slides, child.Ordinal)));
		}

		return buttons;
	}

	/// <summary>
	/// The <c>ExpandableMenu/Buttons</c> group, or null when this screen carries no list.
	///
	/// Found by walking from the named widget rather than by searching for a node called
	/// "Buttons" anywhere: the name is generic enough that a screen could reuse it for
	/// something that is not a menu, and the widget is what the formatter anchors to.
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
	/// One line saying what the guest has put in the menu, for the build report.
	///
	/// Prints the FILLED entries and the pool size separately, because "26 buttons" and "0
	/// entries" is the interesting state and a count alone cannot tell the two apart.
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
			? $"{buttons.Count} button(s) in the pool, NONE carries a label — " +
			  "the guest has spawned no entries"
			: $"{filled.Count} of {buttons.Count} button(s) filled: {string.Join(", ", filled)}";
	}

	/// <summary>
	/// One line saying whether the guest's own formatter has laid the pool out.
	///
	/// THE POOL IS AUTHORED STACKED. Every <c>MenuItemButtonN</c> in <c>pausemainmenu.bgf</c>
	/// sits at the same authored position, and <c>expandablemenuformatting.lua</c> is what
	/// separates them — <c>AnimatePropertiesToIndex</c> writes <c>position.x</c> and
	/// <c>position.y</c> onto each component to move it to one of the twelve slots. Those are
	/// attributes the host already reads per frame, so a pool that is still stacked is a pool
	/// the formatter has not touched, and that distinguishes "the guest never populated this
	/// menu" from "it did and we are drawing it wrong" — which look identical on screen.
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

		return distinct.Count <= 1
			? $"{stated} button(s) state a position but they are ALL THE SAME — the formatter " +
			  "has not spread the pool"
			: $"{stated} button(s) state a position, {distinct.Count} distinct — the guest's " +
			  "formatter has laid the pool out";
	}

	private static string? At(IReadOnlyList<string?>? values, int index) =>
		values is not null && index >= 0 && index < values.Count ? values[index] : null;

	private static bool At(IReadOnlyList<bool>? values, int index) =>
		values is not null && index >= 0 && index < values.Count && values[index];

	private static int AtInt(IReadOnlyList<int>? values, int index) =>
		values is not null && index >= 0 && index < values.Count ? values[index] : 0;
}
