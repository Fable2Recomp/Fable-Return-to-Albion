using Godot;

namespace Refii.Ui;

/// <summary>
/// `theme/themetext.lua` translated: `g_Theme`'s nine text colours, as literal bytes from the
/// bytecode. Menu rows are a separate channel (`SetGhosting` writes (255, 224, 128) live /
/// black ghosted on `Icon.Title` and never reads the theme).
/// </summary>
public static class Engine_UiThemeText
{
	private static Color Rgba(int r, int g, int b, int a) =>
		new(r / 255f, g / 255f, b / 255f, a / 255f);

	/// <summary>`GetMenuItemDefaultTextColor` — (0, 0, 96).</summary>
	public static readonly Color MenuItemDefault = Rgba(0, 0, 96, 255);

	/// <summary>`GetMenuItemFolderTextColor` — (0, 0, 0).</summary>
	public static readonly Color MenuItemFolder = Rgba(0, 0, 0, 255);

	/// <summary>`GetMenuItemEmphasisedTextColor` — (0, 32, 255), the one bright colour here.</summary>
	public static readonly Color MenuItemEmphasised = Rgba(0, 32, 255, 255);

	/// <summary>`GetMenuItemGhostedTextColor` — (48, 48, 48); the expandable menu's `SetGhosting` ghost is black at opacity 90.</summary>
	public static readonly Color MenuItemGhosted = Rgba(48, 48, 48, 255);

	/// <summary>`GetMenuItemHighlightedTextColor` — (64, 16, 0).</summary>
	public static readonly Color MenuItemHighlighted = Rgba(64, 16, 0, 255);

	/// <summary>`GetPageTitleColor` — (0, 0, 0).</summary>
	public static readonly Color PageTitle = Rgba(0, 0, 0, 255);

	/// <summary>`GetSubheadingColor` — (0, 0, 0).</summary>
	public static readonly Color Subheading = Rgba(0, 0, 0, 255);

	/// <summary>`GetNormalTextColor` — (0, 0, 0). The book's body text.</summary>
	public static readonly Color Normal = Rgba(0, 0, 0, 255);

	/// <summary>`GetNegativeTextColor` — (96, 0, 0); unaffordable prices and other bad numbers.</summary>
	public static readonly Color Negative = Rgba(96, 0, 0, 255);
}
