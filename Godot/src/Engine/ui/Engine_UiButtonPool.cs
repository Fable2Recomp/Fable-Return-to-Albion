using Godot;

namespace Refii.Ui;

/// <summary>
/// The authored row pool: a fixed set of `MenuItemButtonN` components under a group named
/// `Buttons` (nothing is cloned at runtime). `Buttons` is not unique (six in
/// `abilitiesscreen.bgf`), so the pool is identified by what it holds, not by walk order.
/// </summary>
public static class Engine_UiButtonPool
{
	/// <summary>True when a `Buttons` group holds `MenuItemButtonN` components.</summary>
	public static bool IsButtonPool(Node group)
	{
		foreach (Node child in group.GetChildren())
		{
			if (child is Node3D && child.HasMeta("element_type") &&
				(string)child.GetMeta("element_type") == "Component" &&
				child.Name.ToString().StartsWith("MenuItemButton", System.StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// The canvas-space band a screen's list may draw in: the pool's `MaskName` rect, or null
	/// when unmasked.
	/// </summary>
	public static Rect2? ListClip(Engine_UiScreen? screen)
	{
		if (screen is null ||
			Engine_ExpandableMenuItems.FindGroup(screen) is not { } pool ||
			pool.Ordinal < 0 || pool.Ordinal >= screen.MaskByOrdinal.Length)
		{
			return null;
		}

		int mask = screen.MaskByOrdinal[pool.Ordinal];
		if (mask < 0 || mask >= screen.Nodes.Length)
		{
			return null;
		}

		UiNode node = screen.Nodes[mask];
		float x = 0f;
		float y = 0f;
		for (UiNode? at = node; at is not null; at = at.Parent)
		{
			x += at.PositionX;
			y += at.PositionY;
		}

		// A mask is a quad: 100-unit base times its node scale, centred on its position.
		float width = 100f * node.ScaleX;
		float height = 100f * node.ScaleY;
		return new Rect2(x - (width / 2f), y - (height / 2f), width, height);
	}

	/// <summary>The pattern book's button pool (first `Buttons` group that holds rows).</summary>
	public static Node3D? FindButtonPool(Engine_UiTheme book)
	{
		foreach (Node3D candidate in book.ArtSourceNodes(Engine_FrontEndMenuView.RowGroupName))
		{
			if (IsButtonPool(candidate))
			{
				return candidate;
			}
		}

		return null;
	}
}
