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
/// no game VM, so only native handlers and Godot scripts hear it there. Every command on a mod
/// menu's row, and <c>Opens:</c> / <c>Mod:lua:</c> on any list's, runs through the one dispatcher
/// (<see cref="Engine_UiCommands"/>) and never reaches the guest, which knows nothing of the menu.</para>
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
	private static Engine_UiCommandList _focusList;
	private static ulong _focusFrame;

	/// <summary>
	/// Names the list that has the input this frame, the template its root list is and which list
	/// it is, for a script's <c>RefiiHost.open_menu</c> / <c>open_screen</c>. Called every frame by
	/// whichever list is standing; a mod menu open on it re-sources its rows here (a script's
	/// <c>set_menu_rows</c>, a <c>values:</c> source), so a list on screen follows what was stated.
	/// </summary>
	public static void Focus(Engine_FrontEndMenuView view, string rootMenu, Engine_UiCommandList list)
	{
		_focus = view;
		_focusRoot = rootMenu;
		_focusList = list;
		_focusFrame = Engine.GetProcessFrames();

		string? opened = view.OpenedMenuName ?? (ReferenceEquals(view, _standaloneView) ? _standaloneMenu : null);
		if (opened is not null && view.Menu is { } model && Engine_UiTemplates.Menu(opened) is { } template &&
			template.RefreshSources(model))
		{
			view.Refresh();
		}
	}

	/// <summary>Which list has the input, when one stated it this frame or the last.</summary>
	public static Engine_UiCommandList? FocusedList => Focused is null ? null : _focusList;

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

	private static string? _standaloneMenu;

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
		_standaloneMenu = menu;
		GD.Print($"[mod-menus] '{menu}' opened on its own for player {player} — " +
			$"{view.Menu?.Visible.Count ?? 0} row(s)");
	}

	/// <summary>The standalone list went: B at its root, or the owner took it down.</summary>
	public static void StandaloneClosed(string menu)
	{
		_standaloneView = null;
		_standaloneMenu = null;
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
	/// <param name="list">The list the view is — what the dispatcher checks a verb against, so a
	/// <c>PauseTab:</c> row in a mod menu opened over the pause book runs and one over a screen's
	/// list refuses.</param>
	/// <param name="rootIsModMenu">The root list is itself a mod's menu (a standalone list).</param>
	public static bool Activated(
		Engine_FrontEndMenuView view, string rootMenu, Engine_UiExpandableMenu.Item? item, string? action,
		Engine_UiCommandList list, bool rootIsModMenu = false)
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

		bool modVerb = Engine_UiModMenuCommand.Parse(action).Verb != Engine_UiModMenuCommand.Verb.None;
		string mod = Engine_UiTemplates.MenuOwner(menu, item.Name);
		if (!modVerb && !inModMenu && mod.Length == 0)
		{
			return false;
		}

		Raise(menu, item.Name, player, mod);
		if (!modVerb && !inModMenu)
		{
			// A mod's row patched into a shipped list, with a command of the list's own: the
			// event is raised and the list answers it as it answers its own rows.
			return false;
		}

		// `Opens:`, `Mod:lua:`, or any command on a mod menu's row: the one dispatcher. The guest has
		// no row of this name to commit, so nothing reaches it.
		if (item.Command is { Length: > 0 } &&
			Engine_UiCommands.Run(action, new Engine_UiCommandContext(list, menu, item.Name, player, mod) { View = view })
				.Outcome == Engine_UiCommandOutcome.NotOurs)
		{
			GD.Print($"[mod-menus] '{menu}/{item.Name}' names '{action}', which no verb runs");
		}

		return true;
	}

	/// <summary>
	/// The mod menu verbs on the one dispatcher: <c>Opens:&lt;menu&gt;</c> opens over the list the row
	/// is on (or on its own with none), <c>Mod:lua:&lt;function&gt;[:&lt;arg&gt;]</c> calls the mod's Lua.
	/// Any list's row may name either.
	/// </summary>
	public static void RegisterVerbs(Engine_UiCommandSet set)
	{
		set.Register(Engine_UiVerbs.Opens, (_, context) =>
		{
			Engine_UiModMenuCommand.Parsed command = Engine_UiModMenuCommand.Parse(context.Command);
			if (command.Verb != Engine_UiModMenuCommand.Verb.Opens)
			{
				return Engine_UiCommandResult.Refused("'Opens:' names no menu");
			}

			bool opened = context.View is { } view && GodotObject.IsInstanceValid(view)
				? Open(view, command.Menu)
				: OpenFor(command.Menu, context.Player);
			return Engine_UiCommandResult.From(opened, $"no menu '{command.Menu}' in any ui/templates.json");
		});

		set.Register(Engine_UiVerbs.Lua, (_, context) =>
		{
			Engine_UiModMenuCommand.Parsed command = Engine_UiModMenuCommand.Parse(context.Command);
			if (command.Verb != Engine_UiModMenuCommand.Verb.Lua)
			{
				return Engine_UiCommandResult.Refused(
					$"'{context.Command}' does not name a Lua function: Mod:lua:<name or table.name>[:<arg>]");
			}

			return CallLua(command, context.Menu, context.Row, context.Player is 1 or 2 ? context.Player : MenuPlayer,
				context.Mod);
		});
	}

	/// <summary>
	/// The <c>ModMenuRow</c> event: the Godot signal now, the native and Lua handlers on the next
	/// guest tick (the guest command <c>menu_row</c>). The guest ticks on the title and the front end
	/// too, so native handlers hear a front-end row, and the payload fires it into the mods'
	/// front-end Lua (mod/frontend.lua) when no game VM is up.
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
	/// A row's <c>Mod:lua:</c> call on the next guest tick: in a game, in the game VM (built by
	/// <c>Game_InitDatabases</c>, src/vfs/src/hooks/mod_lua_hooks.cpp); on the title and the front end,
	/// in the mods' front-end Lua, the current Gameface presentation's own state running every mod's
	/// <c>mod/frontend.lua</c> (Refii.Mods.ModLua.FrontendPreamble).
	/// </summary>
	private static Engine_UiCommandResult CallLua(
		Engine_UiModMenuCommand.Parsed command, string menu, string row, int player, string mod)
	{
		// In a game, the game VM; on the title and the front end, the mods' front-end Lua (the current
		// Gameface presentation's state with every mod's mod/frontend.lua, mod_frontend_lua.cpp).
		string line = (HostBridge.IsInGame ? "run_lua " : Refii.Mods.ModLua.FrontendCommand) +
			Engine_UiModMenuCommand.LuaCall(command, menu, row, player, mod);
		if (System.Text.Encoding.UTF8.GetByteCount(line) >= Refii.Mods.RefiiHost.CommandLimit)
		{
			return Engine_UiCommandResult.Refused($"the call to {command.Function} is over the " +
				$"{Refii.Mods.RefiiHost.CommandLimit}-byte command limit; not run");
		}

		HostBridge.PostModCommand(line);
		return Engine_UiCommandResult.Done;
	}
}
