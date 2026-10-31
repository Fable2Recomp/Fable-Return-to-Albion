namespace Refii.Titles.Fable2;

/// <summary>
/// The augment the player picked, while they choose the weapon to put it on.
///
/// The guest keeps this on its items screen: a press on an augment row (event 83,
/// <c>sub_826D1800</c>) looks the pushed id up into <c>screen15+380</c>, sets the mode word
/// <c>screen15+420</c> to 1, titles the screen <c>GUI_SCREEN_TITLE_AUGMENT</c> and has its Lua
/// list the weapons; the press on a weapon (event 85) attaches through <c>sub_826CA298</c>. The
/// list and the screens are Godot's, so the choice is held here, the items screen shows
/// <see cref="MenuScript"/> while one is held, and the attach is the pump's
/// <c>Inventory:augment:&lt;augment&gt;:&lt;target&gt;</c> — the same function, called with the
/// same two refs — whose verdict comes back as <c>@Augment.Result</c>.
/// </summary>
public static class Engine_UiAugmentFlow
{
	/// <summary>The held list's template: every carried weapon or accessory with a free slot.</summary>
	public const string MenuScript = "AugmentMenu";

	/// <summary>The items screen, which hosts the list.</summary>
	public const uint ItemsScreen = 15;

	/// <summary>An augment row's action: <c>Inventory:augmentselect:&lt;record&gt;</c>.</summary>
	public const string SelectAction = Engine_UiInventorySource.InventoryAction + "augmentselect:";

	/// <summary>A target row's action: <c>Inventory:augment:&lt;augment&gt;:&lt;target&gt;</c>.</summary>
	public const string ApplyAction = Engine_UiInventorySource.InventoryAction + Engine_UiInventorySource.AugmentOperation + ":";

	/// <summary>The chosen augment's GDB record, or 0 when none is held.</summary>
	public static uint Augment { get; private set; }

	public static bool Active => Augment != 0u;

	/// <summary>
	/// The names of the pair an apply was posted for, kept for the verdict's message: the augment
	/// is consumed on success, so its name cannot be read back afterwards.
	/// </summary>
	public static (string Augment, string Target) Names { get; set; } = (string.Empty, string.Empty);

	public static void Begin(uint augment)
	{
		Augment = augment;
		Godot.GD.Print($"[augment] chose augment 0x{augment:X8} — the items screen shows '{MenuScript}'");
	}

	public static void End()
	{
		if (Augment != 0u)
		{
			Godot.GD.Print($"[augment] augment 0x{Augment:X8} put away");
		}

		Augment = 0u;
	}
}
