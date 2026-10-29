namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The dye the player picked, while they choose what to put it on.
///
/// The guest keeps this on its clothing screen: a press on a dye row (event 80,
/// <c>sub_826D1800</c>) stores the dye's item ref at <c>screen16+228</c>, sets the mode word
/// <c>screen16+220</c> to 5 and shows the dye list (<c>sub_829B2168</c>). The list and the
/// screens are Godot's, so the choice is held here and the items screen shows
/// <see cref="MenuScript"/> while one is held.
/// </summary>
public static class Engine_UiDyeFlow
{
	/// <summary>The population table the guest's own dye list is built from.</summary>
	public const string MenuScript = "DyeMenu";

	/// <summary>The items screen, which hosts the dye list.</summary>
	public const uint ItemsScreen = 15;

	/// <summary>A dye row's action: <c>Inventory:dyeselect:&lt;record&gt;</c>.</summary>
	public const string SelectAction = Engine_UiInventorySource.InventoryAction + "dyeselect:";

	/// <summary>The chosen dye's GDB record, or 0 when none is held.</summary>
	public static uint Dye { get; private set; }

	public static bool Active => Dye != 0u;

	public static void Begin(uint dye)
	{
		Dye = dye;
		Godot.GD.Print($"[dye] chose dye 0x{dye:X8} — the items screen shows '{MenuScript}'");
	}

	public static void End()
	{
		if (Dye != 0u)
		{
			Godot.GD.Print($"[dye] dye 0x{Dye:X8} put away");
		}

		Dye = 0u;
	}
}
