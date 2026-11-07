namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The presentation each of the guest's two front-end screens is authored as: the guest ctor
/// hands `"FrontEndStartupScreen.bgf"` to GUI+180 and `"FrontEndMainMenu.bgf"` to GUI+184
/// (static init at 0x8324A2B8 / 0x8324A2F8). `Is` consults the same table.
/// </summary>
public static class Engine_UiFrontEndScreens
{
	public static string? PresentationFor(Engine_UiModel.FrontEndScreen screen) => screen switch
	{
		Engine_UiModel.FrontEndScreen.Title => "frontendstartupscreen",
		Engine_UiModel.FrontEndScreen.MainMenu => "frontendmainmenu",
		_ => null,
	};

	/// <summary>True when a bank name is one of the front end's own screens.</summary>
	public static bool Is(string bank)
	{
		foreach (Engine_UiModel.FrontEndScreen screen in
			System.Enum.GetValues<Engine_UiModel.FrontEndScreen>())
		{
			if (PresentationFor(screen) is { } name &&
				string.Equals(bank, name, System.StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// The file element a dotted path names, as `GuiElement_Resolve_p` (TU1 `sub_824265A0`) finds
	/// it with no start element: `Layer` under the root (0x824265d0..0x824265f0), then each
	/// segment a direct child of the one before (service vf +84, `sub_82BB5658`). Null when the
	/// file has no such element.
	/// </summary>
	public static UiNode? ElementAt(Engine_UiScreen screen, string path)
	{
		UiNode? at = screen.Nodes.Length > 0 ? ChildNamed(screen.Nodes[0], "Layer") : null;
		foreach (string name in path.Split('.'))
		{
			at = ChildNamed(at, name);
		}

		return at;
	}

	private static UiNode? ChildNamed(UiNode? node, string name)
	{
		foreach (UiNode child in node?.Children ?? new System.Collections.Generic.List<UiNode>())
		{
			if (child.Name == name)
			{
				return child;
			}
		}

		return null;
	}

	/// <summary>
	/// The text the guest last told `dialog.Group.{leaf}` of a front-end screen after
	/// <paramref name="since"/>, empty when none. The dialog writer `sub_826E40E0` (disc
	/// `sub_826CB158`) sets `OK`, `yousure` and `cancel` through `lhGuiElement_SetText` on every
	/// raise; the entry's statement is the text (RefiiHeadlessSetText).
	/// </summary>
	public static string DialogText(Engine_UiScreen screen, uint presentation, string leaf, ulong since) =>
		screen.Nodes.Length > 0 && ElementAt(screen, "dialog.Group." + leaf) is { } node
			? Engine_UiSlideStatements.TextOf(presentation, screen.Nodes[0].Element.NameHash, node.Ordinal, since) ??
				string.Empty
			: string.Empty;
}
