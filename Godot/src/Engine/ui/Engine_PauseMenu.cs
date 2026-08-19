namespace Refii.Ui;

/// <summary>
/// The pause menu's item list, built the way the guest builds its own.
/// </summary>
/// <remarks>
/// THE LIST IS THE GUEST'S; the layout, highlight and navigation are ours — the same split as
/// <see cref="Engine_FrontEndMenu"/>, because the end state is menus we can author in an
/// editor, including ones the guest never had.
///
/// Source of truth: `sub_8298DF98`, the hero variant of the builder `sub_826A2C88` runs when
/// it pushes screen id 0. Items spawn through the same GUI-var stager as the front end's
/// (`sub_826CE8D0` -> `g_Menu:SpawnButton()`); names, tags, order and conditions below are
/// that function's, decompiled 2026-08-20. The co-op variants are NOT here yet: the henchman
/// list (`sub_8298EDE0`) and the network-only `Kick` entry belong to sessions this build
/// cannot reach today, and inventing their conditions would be worse than omitting them.
///
/// DISPATCH IS THE TAB LEVER, not the pump: each screen entry names the tab
/// `sub_826B89E0`'s switch maps to its screen, and the host writes the guest's own
/// pending-tab fields (see HostBridge.PauseTabSelect). One known quirk carried over from the
/// guest itself: the tab path to Abilities omits the mode call the ITEM path makes
/// (`vfn+32(screen14, 0)`), so it inherits whatever mode the screen last had — the guest's
/// own LB/RB has the identical behaviour, so following it is faithful, not lazy.
///
/// The Saving group's children dispatch by EVENT (type 2, param 0/1/2 -> query boxes
/// 23/24/25), which the tab lever cannot express — they stay unwired until the pause event
/// path exists, and are marked unavailable so they cannot appear to work.
/// </remarks>
public static class Engine_PauseMenu
{
	/// <summary>Tab ids as `sub_826B89E0`'s switch numbers them.</summary>
	public const uint TabMap = 2;
	public const uint TabAbilities = 3;
	public const uint TabItems = 4;
	public const uint TabWeapons = 5;
	public const uint TabClothing = 6;
	public const uint TabLogbook = 7;
	public const uint TabOptions = 8;

	/// <summary>Builds the hero pause list in the guest's own order.</summary>
	public static Engine_FrontEndMenu BuildHero()
	{
		var menu = new Engine_FrontEndMenu();

		// Float3=301: the parent expands in Lua only — no C++ handler exists for "Saving"
		// itself. Its children are event-dispatched (params 0/1/2), which the tab lever
		// cannot carry; Available=false keeps them visibly ghosted rather than silently dead.
		var saving = new Engine_FrontEndMenu.Item("Saving", "GUI_SCREEN_PAUSE_SAVING");
		saving.Children.Add(new Engine_FrontEndMenu.Item(
			"SaveAndContinue", "GUI_SCREEN_PAUSE_SAVE_AND_CONTINUE", "Saving")
		{ Available = false });
		saving.Children.Add(new Engine_FrontEndMenu.Item(
			"SaveAndQuit", "GUI_SCREEN_PAUSE_SAVE_AND_QUIT", "Saving")
		{ Available = false });
		saving.Children.Add(new Engine_FrontEndMenu.Item(
			"QuitDontSave", "GUI_SCREEN_PAUSE_QUIT_DONT_SAVE", "Saving")
		{ Available = false });
		menu.Add(saving);

		menu.Add(Tab("Map", "GUI_SCREEN_PAUSE_WORLD_MAP", TabMap));
		menu.Add(Tab("Abilities", "GUI_SCREEN_PAUSE_ABILITIES", TabAbilities));
		menu.Add(Tab("Items", "GUI_SCREEN_PAUSE_ITEMS", TabItems));
		menu.Add(Tab("Weapons", "GUI_SCREEN_PAUSE_WEAPONS", TabWeapons));
		menu.Add(Tab("Clothing", "GUI_SCREEN_PAUSE_CLOTHING", TabClothing));
		menu.Add(Tab("Logbook", "GUI_SCREEN_PAUSE_LOGBOOK", TabLogbook));
		menu.Add(Tab("Options", "GUI_SCREEN_PAUSE_OPTIONS", TabOptions));

		return menu;
	}

	private static Engine_FrontEndMenu.Item Tab(string name, string tag, uint tab) =>
		new(name, tag) { Command = $"PauseTab:{tab}" };
}
