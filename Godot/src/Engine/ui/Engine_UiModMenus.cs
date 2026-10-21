using Godot;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// A MOD'S OWN MENUS and what their rows do, for all three of the game's lists — the front end,
/// the pause book and the screen lists — on the one list view they share
/// (<see cref="Engine_FrontEndMenuView"/>), and on a list of their own when nothing is on screen.
/// </summary>
/// <remarks>
/// <para>A menu a mod defines in <c>ui/templates.json</c> opens as a list over the one it was
/// chosen from — by an entry's <c>opens</c> field or by the command <c>Opens:&lt;menu&gt;</c> — and
/// B returns to that list (<see cref="Engine_FrontEndMenuView.CloseMenu"/>). Opened by a script
/// (<see cref="OpenFor"/>: Godot's <c>open_menu</c>, native <c>refii_mod_open_menu</c>, Lua
/// <c>Refii.open_menu</c>) it opens over whichever list has the input, or — with none up, in play
/// or on the title — as a list of its own the host stands (<see cref="TakeStandalone"/>), which
/// takes the whole pad while it stands and closes on B at its root. The list is built by the
/// template's own <see cref="Engine_UiMenuTemplate.Build"/>, the construction every shipped list
/// uses, so a mod menu folds, ghosts and sources rows exactly as they do.</para>
/// <para>A row's <c>Mod:lua:&lt;function&gt;[:&lt;arg&gt;]</c> calls that global Lua function in the
/// game VM on the next guest tick (<see cref="Engine_UiModMenuCommand"/>). Every row of a mod's
/// menu, every row a mod patched into a shipped menu and every <c>Opens:</c> / <c>Mod:lua:</c> row
/// raises the <c>ModMenuRow</c> event on all three surfaces: the Godot signal as it happens, the
/// native and Lua handlers on the next guest tick — on the title too, where the guest ticks with
/// no game VM, so only native handlers and Godot scripts hear it there. Any other command on a mod
/// menu's row runs as a host action (<see cref="Engine_UiScreenActions.Run"/>) and never reaches
/// the guest, which knows nothing of the menu.</para>
/// </remarks>
public static class Engine_UiModMenus
{
	/// <summary>
	/// The player whose menu it is — the one rule every host menu uses
	/// (<see cref="Engine_UiPropertySource.MenuPlayer"/>): the guest's stated menu owner in local
	/// co-op, else this machine's player (player two on a joined client).
	/// </summary>
	public static int MenuPlayer => Engine_UiPropertySource.MenuPlayer();

	private static Engine_FrontEndMenuView? _focus;
	private static string _focusRoot = string.Empty;
	private static ulong _focusFrame;

	/// <summary>
	/// Names the list that has the input this frame and the template its root list is, for a
	/// script's <c>RefiiHost.open_menu</c>. Called every frame by whichever list is standing.
	/// </summary>
	public static void Focus(Engine_FrontEndMenuView view, string rootMenu)
	{
		_focus = view;
		_focusRoot = rootMenu;
		_focusFrame = Engine.GetProcessFrames();
	}

	/// <summary>The list with the input, when one stated it this frame or the last.</summary>
	private static Engine_FrontEndMenuView? Focused =>
		_focus is not null && GodotObject.IsInstanceValid(_focus) && _focus.IsVisibleInTree() &&
		Engine.GetProcessFrames() - _focusFrame <= 1
			? _focus
			: null;

	/// <summary>
	/// Opens a mod menu for <paramref name="player"/> (0: whoever owns the menus here): over
	/// whichever list has the input, else as a list of its own (<see cref="TakeStandalone"/>).
	/// False when no template has that name or the player is played on the other machine.
	/// </summary>
	public static bool OpenFor(string menu, int player)
	{
		if (!Engine_UiModBoxes.PlayedHere(player))
		{
			GD.Print($"[mod-menus] '{menu}' is for player {player}, who is played on the other machine — not opened here");
			return false;
		}

		if (Engine_UiTemplates.Menu(menu) is null)
		{
			GD.PushWarning($"[mod-menus] no menu '{menu}' in any ui/templates.json; nothing opens");
			return false;
		}

		if (Focused is { } view)
		{
			return Open(view, menu);
		}

		_requested = new Standalone(menu, Engine_UiModBoxes.Resolve(player));
		GD.Print($"[mod-menus] '{menu}' asked for with no list up — it opens on its own");
		return true;
	}

	/// <summary>A menu asked for with no list on screen: which, and whose.</summary>
	public readonly record struct Standalone(string Menu, int Player);

	private static Standalone? _requested;

	/// <summary>The view the host stands a <see cref="Standalone"/> menu on, while it stands.</summary>
	private static Engine_FrontEndMenuView? _standaloneView;

	private static int _standalonePlayer;

	/// <summary>
	/// The menu asked for with no list up, once: the owner stands it on a list of its own, roots it
	/// with <see cref="StandaloneOpened"/>, drives it as any list (<see cref="Activated"/> with
	/// <c>rootIsModMenu</c>) and calls <see cref="StandaloneClosed"/> when B leaves its root.
	/// </summary>
	public static Standalone? TakeStandalone()
	{
		Standalone? requested = _requested;
		_requested = null;
		return requested;
	}

	/// <summary>The owner stood <paramref name="menu"/> on <paramref name="view"/> as its root, for <paramref name="player"/>.</summary>
	public static void StandaloneOpened(Engine_FrontEndMenuView view, string menu, int player)
	{
		_standaloneView = view;
		_standalonePlayer = player;
		GD.Print($"[mod-menus] '{menu}' opened on its own for player {player} — " +
			$"{view.Menu?.Visible.Count ?? 0} row(s)");
	}

	/// <summary>The standalone list went: B at its root, or the owner took it down.</summary>
	public static void StandaloneClosed(string menu)
	{
		_standaloneView = null;
		GD.Print($"[mod-menus] '{menu}' closed");
	}

	/// <summary>Whose menu a row on <paramref name="view"/> is: a standalone list's player, else the menu owner here.</summary>
	private static int PlayerFor(Engine_FrontEndMenuView view) =>
		ReferenceEquals(view, _standaloneView) ? _standalonePlayer : MenuPlayer;

	/// <summary>
	/// Opens a mod menu over whichever list has the input, else on its own, for whoever owns the
	/// menus here. False when no template has that name.
	/// </summary>
	public static bool OpenOnFocus(string menu) => OpenFor(menu, 0);

	/// <summary>Opens the menu a template names over the list on <paramref name="view"/>.</summary>
	public static bool Open(Engine_FrontEndMenuView view, string menu)
	{
		if (Engine_UiTemplates.Menu(menu) is not { } template)
		{
			GD.PushWarning($"[mod-menus] no menu '{menu}' in any ui/templates.json; nothing opens");
			return false;
		}

		view.OpenMenu(template.Build(), menu);
		GD.Print($"[mod-menus] '{menu}' opened ({view.OpenedDepth} deep) — " +
			$"{view.Menu?.Visible.Count ?? 0} row(s)");
		return true;
	}

	/// <summary>
	/// B on a list: closes the innermost mod menu. False when none is open, so B is the caller's.
	/// </summary>
	public static bool Back(Engine_FrontEndMenuView view)
	{
		string? name = view.OpenedMenuName;
		if (!view.CloseMenu())
		{
			return false;
		}

		GD.Print($"[mod-menus] '{name}' closed — back to {view.OpenedMenuName ?? "the list it was opened from"}");
		return true;
	}

	/// <summary>
	/// The player pressed A on <paramref name="item"/> and the view answered
	/// <paramref name="action"/> (null for a fold, an <c>opens</c> entry or nothing). True when the
	/// press is fully answered here and the caller must do nothing more with it.
	/// </summary>
	/// <param name="rootMenu">The template the view's ROOT list is ("frontend-root", "pause-root",
	/// a screen's population script) — the menu a row belongs to when no mod menu is open.</param>
	/// <param name="rootIsModMenu">The root list is itself a mod's menu (a standalone list).</param>
	public static bool Activated(
		Engine_FrontEndMenuView view, string rootMenu, Engine_UiExpandableMenu.Item? item, string? action,
		bool rootIsModMenu = false)
	{
		if (item is null)
		{
			return false;
		}

		string menu = view.OpenedMenuName ?? rootMenu;
		bool inModMenu = view.OpenedDepth > 0 || rootIsModMenu;
		int player = PlayerFor(view);

		// AN ENTRY'S `opens` naming a menu template (a fold's children come first, as the model
		// expands them). The front end's view latched it as a screen to open, which it is not, so
		// the latch is taken here.
		if (item.Opens is { } opens && item.Children.Count == 0 && Engine_UiTemplates.Menu(opens) is not null)
		{
			view.TakeOpenedScreen();
			Raise(menu, item.Name, player);
			Open(view, opens);
			return true;
		}

		if (action is null)
		{
			return false;
		}

		Engine_UiModMenuCommand.Parsed command = Engine_UiModMenuCommand.Parse(action);
		string mod = Engine_UiTemplates.MenuOwner(menu, item.Name);
		bool ours = command.Verb != Engine_UiModMenuCommand.Verb.None || inModMenu;
		if (!ours && mod.Length == 0)
		{
			return false;
		}

		Raise(menu, item.Name, player, mod);
		switch (command.Verb)
		{
			case Engine_UiModMenuCommand.Verb.Opens:
				Open(view, command.Menu);
				return true;

			case Engine_UiModMenuCommand.Verb.Lua:
				CallLua(command, menu, item.Name, player, mod);
				return true;
		}

		if (!inModMenu)
		{
			// A mod's row patched into a shipped list, with a command of the list's own: the
			// event is raised and the list answers it as it answers its own rows.
			return false;
		}

		// A mod menu's row with any other command: a host action, or nothing. The guest has no row
		// of this name to commit.
		if (item.Command is { Length: > 0 } && !Engine_UiScreenActions.Run(action))
		{
			GD.Print($"[mod-menus] '{menu}/{item.Name}' names '{action}', which nothing runs");
		}

		return true;
	}

	/// <summary>
	/// The <c>ModMenuRow</c> event: the Godot signal now, the native and Lua handlers on the next
	/// guest tick (the guest command <c>menu_row</c>). The guest ticks on the title and the front end
	/// too, so native handlers hear a front-end row; Lua handlers live in the game VM, which exists
	/// only in a game, so on the front end the row is Godot's and native code's.
	/// </summary>
	private static void Raise(string menu, string row, int player, string? mod = null)
	{
		mod ??= Engine_UiTemplates.MenuOwner(menu, row);
		Refii.Mods.ModGodot.Host?.RaiseModMenuRow(menu, row, player, mod);
		if (HostBridge.IsGuestUp)
		{
			HostBridge.PostModCommand(Engine_UiModMenuCommand.RowCommandLine(menu, row, player, mod));
		}

		GD.Print($"[mod-menus] '{menu}/{row}' chosen by player {player}" + (mod.Length == 0 ? string.Empty : $" ({mod})"));
	}

	/// <summary>
	/// A row's <c>Mod:lua:</c> call, run in the game VM on the next guest tick. The game VM is built
	/// by <c>Game_InitDatabases</c> when a game starts and closed when it ends
	/// (src/vfs/src/hooks/mod_lua_hooks.cpp); the front end's only Lua is Gameface's, one state per
	/// presentation, a different Lua build the mods' code is never run in. So on the front end the
	/// call is not made, and says so; the row's <c>ModMenuRow</c> still reaches Godot and native.
	/// </summary>
	private static void CallLua(Engine_UiModMenuCommand.Parsed command, string menu, string row, int player, string mod)
	{
		if (!HostBridge.IsInGame)
		{
			GD.PushWarning($"[mod-menus] '{menu}/{row}' calls {command.Function}, but no game is running, so there is " +
				"no game Lua to call it in; the row's mod_menu_row reached Godot scripts and native handlers");
			return;
		}

		string line = "run_lua " + Engine_UiModMenuCommand.LuaCall(command, menu, row, player, mod);
		if (System.Text.Encoding.UTF8.GetByteCount(line) >= Refii.Mods.RefiiHost.CommandLimit)
		{
			GD.PushWarning($"[mod-menus] '{menu}/{row}': the call to {command.Function} is over the " +
				$"{Refii.Mods.RefiiHost.CommandLimit}-byte command limit; not run");
			return;
		}

		HostBridge.PostModCommand(line);
	}
}
