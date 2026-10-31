namespace Refii.Titles.Fable2;

/// <summary>
/// The augment, or the augment remover, the player picked, while they choose the weapon.
///
/// The guest keeps this on its items screen (<c>sub_826D1800</c>): a press on an Augmentations
/// row raises event 83 for an augment or 310 for an item whose <c>InventoryItemComponent</c>
/// states <c>IsAugmentRemover</c> (<c>sub_826CEEF0</c> picks the id). Both look the pushed id up
/// into <c>screen15+380</c>, set the mode word <c>screen15+420</c> (1 apply, 2 remove), retitle
/// the screen (<c>GUI_SCREEN_TITLE_AUGMENT</c> / <c>GUI_SCREEN_AUGMENT_REMOVAL_TITLE</c>) and
/// have its Lua list the weapons; the press on a weapon (85 / 311) attaches through
/// <c>sub_826CA298</c> or strips through <c>sub_826CA840</c>. The list and the screens are
/// Godot's, so the choice is held here, the items screen shows <see cref="HeldScript"/> while
/// one is held, and the attach or strip is the pump's <c>Inventory:augment:…</c> /
/// <c>Inventory:augmentremove:…</c> — the same functions, called with the same two refs —
/// whose verdicts come back as <c>@Augment.Result</c> / <c>@Augment.Removed</c>.
/// </summary>
public static class Engine_UiAugmentFlow
{
	/// <summary>The apply list's template: every carried item with a free slot.</summary>
	public const string MenuScript = "AugmentMenu";

	/// <summary>The remove list's template: every carried item with an augment to strip.</summary>
	public const string RemoveMenuScript = "AugmentRemoveMenu";

	/// <summary>The items screen, which hosts both lists.</summary>
	public const uint ItemsScreen = 15;

	/// <summary>An augment row's action: <c>Inventory:augmentselect:&lt;record&gt;</c>.</summary>
	public const string SelectAction = Engine_UiInventorySource.InventoryAction + Engine_UiInventorySource.AugmentSelectOperation + ":";

	/// <summary>A remover row's action: <c>Inventory:augmentremoverselect:&lt;record&gt;</c>.</summary>
	public const string SelectRemoverAction = Engine_UiInventorySource.InventoryAction + Engine_UiInventorySource.RemoverSelectOperation + ":";

	/// <summary>A target row's action: <c>Inventory:augment:&lt;augment&gt;:&lt;target&gt;</c>.</summary>
	public const string ApplyAction = Engine_UiInventorySource.InventoryAction + Engine_UiInventorySource.AugmentOperation + ":";

	/// <summary>A strip row's action: <c>Inventory:augmentremove:&lt;remover&gt;:&lt;target&gt;</c>.</summary>
	public const string RemoveAction = Engine_UiInventorySource.InventoryAction + Engine_UiInventorySource.AugmentRemoveOperation + ":";

	/// <summary>The chosen augment's GDB record, or 0 when none is held.</summary>
	public static uint Augment { get; private set; }

	/// <summary>The chosen remover's GDB record, or 0 when none is held.</summary>
	public static uint Remover { get; private set; }

	public static bool Active => Augment != 0u || Remover != 0u;

	/// <summary>The list the items screen shows now: the apply list, the remove list, or none.</summary>
	public static string? HeldScript =>
		Augment != 0u ? MenuScript : Remover != 0u ? RemoveMenuScript : null;

	/// <summary>
	/// The names of the pair an apply or a strip was posted for, kept for the verdict's message:
	/// the augment is consumed on success, so its name cannot be read back afterwards.
	/// </summary>
	public static (string Augment, string Target) Names { get; set; } = (string.Empty, string.Empty);

	public static void Begin(uint augment)
	{
		Remover = 0u;
		Augment = augment;
		Godot.GD.Print($"[augment] chose augment 0x{augment:X8} — the items screen shows '{MenuScript}'");
	}

	public static void BeginRemove(uint remover)
	{
		Augment = 0u;
		Remover = remover;
		Godot.GD.Print($"[augment] chose remover 0x{remover:X8} — the items screen shows '{RemoveMenuScript}'");
	}

	public static void End()
	{
		if (Active)
		{
			Godot.GD.Print($"[augment] augment 0x{Augment:X8} / remover 0x{Remover:X8} put away");
		}

		Augment = 0u;
		Remover = 0u;
	}
}
