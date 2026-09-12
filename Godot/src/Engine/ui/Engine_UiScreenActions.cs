namespace Refii.Ui;

/// <summary>
/// The actions a screen template can name for a button, and the one place that runs them.
///
/// THE ONE-OFF PANELS ANSWER A DIFFERENT VOCABULARY, and that used to be a chain of screen-id
/// comparisons in `GameUi`: the query panel closes on Back, the repeatable-quest card dismisses
/// its box on Accept, minigame-completed and quest-completion dismiss the screen. Each was a
/// branch, so each could be wrong on its own. Naming the action in the template puts them on one
/// path — a screen states which button does what, and this runs it.
/// </summary>
public static class Engine_UiScreenActions
{
	/// <summary>Closes the screen through the guest's own back lever.</summary>
	public const string ScreenBack = "screen:back";

	/// <summary>Dismisses the screen — the one-off panels' own close.</summary>
	public const string ScreenDismiss = "screen:dismiss";

	/// <summary>Unlearns the highlighted ability, then commits the row.</summary>
	public const string ScreenUnlearn = "screen:unlearn";

	/// <summary>
	/// A on the screen itself — for a list whose rows the screen does NOT answer by identity.
	///
	/// The furniture screens are the case: their rows carry event type 0, and the press handler
	/// at `vtable+20` (`sub_826B65F0`) has cases for 140 and 141 and falls out of everything
	/// else, so an identity push does nothing at all. The commit lives in the INPUT handler on
	/// code 36, which reads the highlighted row out of Lua
	/// (`sub_826C1B50` -&gt; `g_Menu:GetHighlightedMenuItemData()`) rather than off the event —
	/// so the press has to be the press.
	/// </summary>
	public const string ScreenSelect = "screen:select";

	/// <summary>
	/// Dismisses the display box rather than the screen. The repeatable-quest card has no screen
	/// class at all: it is the quest box (`sub_823D0090`), which answers 36 through the box
	/// queue's own lever.
	/// </summary>
	public const string BoxDismiss = "box:dismiss";

	/// <summary>Runs a named action. An empty or unknown name does nothing.</summary>
	public static bool Run(string action)
	{
		// `Inventory:<op>:<record hash>` — an operation on the SIMULATION, posted to the guest's
		// tick pump because guest code must run on the guest's thread. It reaches
		// `AddAndReplaceItemOfRecordID` / `Player:GiveGift` directly: no GUI event queue, no
		// screen class, no populated Gameface menu. See `DispatchInventoryAction`.
		if (action.StartsWith(Engine_UiInventorySource.InventoryAction,
				System.StringComparison.Ordinal))
		{
			// A joiner's weapon pick changes the record; the host equips it. The id is decimal.
			const string equip = Engine_UiInventorySource.InventoryAction + "equip:";
			if (Refii.Host.CoopSession.IsClient && action.StartsWith(equip, System.StringComparison.Ordinal) &&
				uint.TryParse(action[equip.Length..], out uint record) &&
				Refii.Host.CoopHeroKit.SetLocalWeapon(record, Engine_UiInventorySource.WeaponClassOf(record)))
			{
				return true;
			}

			Refii.Host.HostBridge.PostFrontEndAction(action);
			return true;
		}

		// `Ability:learn:<1..14>` — the same arrangement for the abilities screen. The guest's
		// own answer to one of those rows is a single two-argument call that reads nothing from
		// the screen or the menu (`sub_8246C970` / `sub_8246CF48`), so the row performs it
		// directly rather than borrowing an identity and hoping the guest's cursor agrees.
		if (action.StartsWith(Engine_UiAbilitySource.AbilityAction,
				System.StringComparison.Ordinal))
		{
			Refii.Host.HostBridge.PostFrontEndAction(action);
			return true;
		}

		switch (action)
		{
			case ScreenBack:
				Refii.Host.HostBridge.ScreenAction(Engine_UiScreenInput.Back);
				return true;

			case ScreenDismiss:
				Refii.Host.HostBridge.ScreenAction(Engine_UiScreenInput.Dismiss);
				return true;

			case ScreenUnlearn:
				Refii.Host.HostBridge.ScreenAction(Engine_UiScreenInput.Unlearn);
				return true;

			case ScreenSelect:
				Refii.Host.HostBridge.ScreenAction(Engine_UiScreenInput.Select);
				return true;

			case BoxDismiss:
				Engine_UiSource.Current.DisplayBoxAction(Engine_UiSource.BoxAction.Dismiss);
				return true;

			default:
				return false;
		}
	}

	/// <summary>Whether this screen takes input directly once revealed.</summary>
	public static bool OwnsRevealedInput(uint screenId) =>
		Engine_UiTemplates.Screen(screenId)?.OwnsRevealedInput ?? false;

	/// <summary>
	/// Runs whichever of this screen's revealed-input actions the pressed button names.
	///
	/// Accept is checked before Back because no shipped screen states both, and a screen that
	/// stated both would want the affirmative to win.
	/// </summary>
	public static void RunRevealed(uint screenId, bool accept, bool back)
	{
		if (Engine_UiTemplates.Screen(screenId) is not { } screen)
		{
			return;
		}

		if (accept && screen.RevealedAccept.Length != 0)
		{
			Run(screen.RevealedAccept);
			return;
		}

		if (back && screen.RevealedBack.Length != 0)
		{
			Run(screen.RevealedBack);
		}
	}
}
