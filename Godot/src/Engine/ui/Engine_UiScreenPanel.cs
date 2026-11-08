namespace Refii.Ui;

/// <summary>
/// The info-panel vocabulary of the screen templates (<see cref="Engine_UiScreenTemplate.PanelFields"/>)
/// and the dotted-path lookup into a screen's document.
///
/// NOTHING DRAWS A PANEL FIELD. The panel painted the guest's own text off a built presentation,
/// and every Fable 2 presentation is headless (gui_headless_presentation.cpp): an owned book's
/// texts come from its page provider, the registry statements and the told text writers
/// (<see cref="Engine_UiOwnedBook"/>). The field stays in the format so a mod that states it still
/// loads; the validator warns that it draws nothing (Engine_ModValidate).
/// </summary>
public static class Engine_UiScreenPanel
{
	/// <summary>
	/// One field of a panel: the guest's dotted element path, and how it is drawn.
	/// </summary>
	/// <param name="Path">The guest's own dotted element path.</param>
	/// <param name="Multiline">True to lay the string out as wrapped paragraphs.</param>
	/// <param name="Alternate">
	/// True when another entry names the same field under a different spelling (clothing calls
	/// the base-value label `ItemValue2`, items and gift call it `ItemValueName`).
	/// </param>
	public readonly record struct Field(
		string Path, bool Multiline = false, bool Alternate = false);

	/// <summary>
	/// Finds an element by the guest's dotted path: the first segment is searched for and the
	/// rest walked as children, as the engine's `getElement(path, from)` does. See
	/// <see cref="Engine_UiTheme.FindByPath"/> for the same rule against the built tree.
	/// </summary>
	public static UiNode? Resolve(Engine_UiScreen screen, string path)
	{
		string[] segments = path.Split('.');
		if (segments.Length == 0)
		{
			return null;
		}

		UiNode? at = null;
		foreach (UiNode node in screen.Nodes)
		{
			if (string.Equals(node.Name, segments[0], System.StringComparison.Ordinal))
			{
				at = node;
				break;
			}
		}

		for (int i = 1; at is not null && i < segments.Length; i++)
		{
			at = Engine_UiStatedPanel.Child(at, segments[i]);
		}

		return at;
	}
}
