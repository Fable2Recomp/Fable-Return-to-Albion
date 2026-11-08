using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The authored structure of a Fable 2 <c>ExpandableMenu</c>: the widget, its button pool and each
/// button's label element.
///
/// The item list is not in the file: the pause menu's widget carries
/// <c>MenuPopulationScript = "PauseMenu"</c>, which <c>expandablemenupopulationscript.lua</c>
/// answers with an empty table, and the rows are the C++'s `g_Menu:SpawnButton` statements
/// (<see cref="Engine_UiMenuModel"/>). The pool is authored: <c>pausemainmenu.bgf</c> ships 26
/// <c>MenuItemButtonN</c> components under <c>ExpandableMenu/Buttons</c>, all at the same authored
/// position (x[-444..-20], y[176..242]) until <c>expandablemenuformatting.lua</c> moves them onto
/// its twelve slots.
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

	/// <summary>One button of the pool, as the file authors it.</summary>
	/// <param name="Ordinal">Index of the button component itself.</param>
	/// <param name="Name">Its authored name, e.g. <c>MenuItemButton3</c>.</param>
	/// <param name="TitleOrdinal">Index of its <c>Icon/Title</c> label, or -1 if it has none.</param>
	/// <param name="Authored">The tag the FILE puts in this button's label.</param>
	public readonly record struct Button(int Ordinal, string Name, int TitleOrdinal, string Authored);

	/// <summary>The button pool of <paramref name="screen"/>, in file order; empty when it has none.</summary>
	public static IReadOnlyList<Button> Read(Engine_UiScreen? screen)
	{
		var buttons = new List<Button>();
		if (screen is null || FindGroup(screen) is not { } group)
		{
			return buttons;
		}

		foreach (UiNode child in group.Children)
		{
			UiNode? title = FindTitle(child);
			buttons.Add(new Button(child.Ordinal, child.Name, title?.Ordinal ?? -1,
				title?.Text?.Tag ?? string.Empty));
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

	/// <summary>One line describing the pool.</summary>
	public static string Describe(IReadOnlyList<Button> buttons) =>
		buttons.Count == 0
			? "no ExpandableMenu on this screen"
			: $"{buttons.Count} button(s) in the pool";
}
