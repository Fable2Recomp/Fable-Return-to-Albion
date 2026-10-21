using System;
using System.Collections.Generic;
using System.Linq;

namespace Refii.Ui;

/// <summary>The list a row was chosen on — the ROOT list, whatever mod menu stands over it.</summary>
public enum Engine_UiCommandList
{
	/// <summary>The title's front-end list.</summary>
	FrontEnd,

	/// <summary>The in-game pause book.</summary>
	Pause,

	/// <summary>A guest screen's owned list (inventory, abilities, the world map...).</summary>
	Screen,

	/// <summary>A list the host stands on its own: a host screen's, a mod menu opened with none up.</summary>
	HostList,

	/// <summary>A screen's own button while it is revealed (<c>revealedAccept</c>, <c>flipAction</c>).</summary>
	Revealed,
}

/// <summary>
/// Where a command was chosen: which list, which menu and row, whose menu (player 1 or 2), the mod
/// that stated the row (empty for the game's), and the list's view when a verb needs one
/// (<c>Opens:</c> opens over it). <see cref="Command"/> is the whole command as the row states it.
/// </summary>
public readonly record struct Engine_UiCommandContext(
	Engine_UiCommandList List, string Menu = "", string Row = "", int Player = 0, string Mod = "")
{
	public string Command { get; init; } = string.Empty;

	public Engine_FrontEndMenuView? View { get; init; }
}

public enum Engine_UiCommandOutcome
{
	/// <summary>No verb claims the command; the list does what it does with its own commands.</summary>
	NotOurs,

	/// <summary>The verb ran.</summary>
	Done,

	/// <summary>The verb exists but does not run here, or the command names nothing it can do.</summary>
	Refused,
}

public readonly record struct Engine_UiCommandResult(Engine_UiCommandOutcome Outcome, string Message = "")
{
	public static readonly Engine_UiCommandResult Done = new(Engine_UiCommandOutcome.Done);

	public static readonly Engine_UiCommandResult NotOurs = new(Engine_UiCommandOutcome.NotOurs);

	public static Engine_UiCommandResult Refused(string why) => new(Engine_UiCommandOutcome.Refused, why);

	/// <summary>Done when <paramref name="ran"/>, else refused with <paramref name="why"/>.</summary>
	public static Engine_UiCommandResult From(bool ran, string why) => ran ? Done : Refused(why);
}

/// <summary>Runs one verb: <paramref name="argument"/> is the command past the verb.</summary>
public delegate Engine_UiCommandResult Engine_UiCommandHandler(string argument, Engine_UiCommandContext context);

/// <summary>
/// A verb as a row names it: <see cref="Name"/> is the whole command (<c>QuitToDesktop</c>) or, ending
/// in <c>:</c>, its prefix (<c>PauseTab:</c>); <see cref="Lists"/> are the lists it runs on (empty:
/// every one); <see cref="Owner"/> is <c>game</c> or the mod that registered it.
/// </summary>
public sealed record Engine_UiVerb(
	string Name, string Usage, string Description, IReadOnlyList<Engine_UiCommandList> Lists, string Owner = "game")
{
	public bool IsPrefix => Name.EndsWith(':');

	public bool RunsOn(Engine_UiCommandList list) => Lists.Count == 0 || Lists.Contains(list);
}

/// <summary>
/// THE ONE COMMAND DISPATCHER every list goes through — the front end, the pause book, the screen
/// lists, mod menus, host-owned lists — and a screen record's <c>revealedAccept</c> /
/// <c>revealedBack</c> / <c>flipAction</c>. The game registers its verbs here and a mod's row can
/// name any of them; a mod's Godot script registers verbs of its own (<c>RefiiHost.register_command</c>).
/// </summary>
/// <remarks>
/// <para>A command is matched to the LONGEST registered verb it is or begins with, so
/// <c>Inventory:dyeselect:</c> can sit beside <c>Inventory:</c>. A verb that does not run on the
/// list the row is on REFUSES with the reason (a <c>PauseTab:</c> row on a screen list); one that
/// cannot do what the argument asks refuses too. A command no verb claims is
/// <see cref="Engine_UiCommandOutcome.NotOurs"/>: the front end posts it to the guest by name, every
/// other list says nothing runs it.</para>
/// <para>Pure .NET: handlers are delegates, so the matching and the refusals are held by tests.</para>
/// </remarks>
public sealed class Engine_UiCommandSet
{
	private readonly Dictionary<string, (Engine_UiVerb Verb, Engine_UiCommandHandler Handler)> _verbs =
		new(StringComparer.Ordinal);

	/// <summary>Registers (or replaces) a verb. A mod's verb may not replace the game's.</summary>
	public bool Register(Engine_UiVerb verb, Engine_UiCommandHandler handler)
	{
		if (verb.Name.Length == 0)
		{
			return false;
		}

		lock (_verbs)
		{
			if (_verbs.TryGetValue(verb.Name, out var standing) && standing.Verb.Owner == "game" && verb.Owner != "game")
			{
				return false;
			}

			_verbs[verb.Name] = (verb, handler);
			return true;
		}
	}

	/// <summary>Removes a mod's verb (the game's stay).</summary>
	public bool Unregister(string name, string owner)
	{
		lock (_verbs)
		{
			return _verbs.TryGetValue(name, out var standing) && standing.Verb.Owner == owner && owner != "game" &&
				_verbs.Remove(name);
		}
	}

	/// <summary>Every verb, by name — the Studio's and the guide's listing.</summary>
	public IReadOnlyList<Engine_UiVerb> Verbs
	{
		get
		{
			lock (_verbs)
			{
				return _verbs.Values.Select(v => v.Verb).OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
			}
		}
	}

	/// <summary>The verb a command names and the argument past it, or null for none.</summary>
	public (Engine_UiVerb Verb, string Argument)? Match(string? command)
	{
		if (string.IsNullOrEmpty(command))
		{
			return null;
		}

		(Engine_UiVerb Verb, Engine_UiCommandHandler Handler)? best = Find(command);
		return best is { } found ? (found.Verb, command[(found.Verb.IsPrefix ? found.Verb.Name.Length : command.Length)..]) : null;
	}

	private (Engine_UiVerb Verb, Engine_UiCommandHandler Handler)? Find(string command)
	{
		lock (_verbs)
		{
			if (_verbs.TryGetValue(command, out var exact) && !exact.Verb.IsPrefix)
			{
				return exact;
			}

			(Engine_UiVerb Verb, Engine_UiCommandHandler Handler)? best = null;
			foreach ((string name, var entry) in _verbs)
			{
				if (entry.Verb.IsPrefix && command.StartsWith(name, StringComparison.Ordinal) &&
					(best is null || name.Length > best.Value.Verb.Name.Length))
				{
					best = entry;
				}
			}

			return best;
		}
	}

	/// <summary>Runs a command chosen in <paramref name="context"/>.</summary>
	public Engine_UiCommandResult Run(string? command, Engine_UiCommandContext context)
	{
		if (string.IsNullOrEmpty(command) || Find(command) is not { } found)
		{
			return Engine_UiCommandResult.NotOurs;
		}

		if (!found.Verb.RunsOn(context.List))
		{
			return Engine_UiCommandResult.Refused(
				$"'{found.Verb.Name}' runs on the {string.Join(" or ", found.Verb.Lists.Select(ListName))} only; " +
				$"this row is on the {ListName(context.List)}");
		}

		string argument = command[(found.Verb.IsPrefix ? found.Verb.Name.Length : command.Length)..];
		try
		{
			return found.Handler(argument, context with { Command = command });
		}
		catch (Exception error) when (found.Verb.Owner != "game")
		{
			return Engine_UiCommandResult.Refused($"'{found.Verb.Name}' ({found.Verb.Owner}) failed: {error.Message}");
		}
	}

	/// <summary>How a list is named in a refusal.</summary>
	public static string ListName(Engine_UiCommandList list) => list switch
	{
		Engine_UiCommandList.FrontEnd => "front-end list",
		Engine_UiCommandList.Pause => "pause list",
		Engine_UiCommandList.Screen => "screen list",
		Engine_UiCommandList.HostList => "host list",
		_ => "screen's own buttons",
	};
}

/// <summary>
/// THE GAME'S VERBS, described once: what registers them binds a handler to each
/// (<see cref="Engine_UiCommands"/>, GameUi), and the validator, the Studio and the guide read the
/// same records.
/// </summary>
public static class Engine_UiVerbs
{
	private static readonly Engine_UiCommandList[] Anywhere = Array.Empty<Engine_UiCommandList>();
	private static readonly Engine_UiCommandList[] PauseOnly = { Engine_UiCommandList.Pause };
	private static readonly Engine_UiCommandList[] FrontEndOnly = { Engine_UiCommandList.FrontEnd };

	private static readonly Engine_UiCommandList[] GuestScreen =
		{ Engine_UiCommandList.Screen, Engine_UiCommandList.Revealed };

	public static readonly Engine_UiVerb Opens = new(Engine_UiModMenuCommand.OpensPrefix, "Opens:<menu>",
		"opens a menu ui/templates.json defines as a list over this one; B returns", Anywhere);

	public static readonly Engine_UiVerb Lua = new(Engine_UiModMenuCommand.LuaPrefix, "Mod:lua:<function>[:<arg>]",
		"calls a global Lua function (or table.function) in the game VM with the argument and the mod_menu_row table", Anywhere);

	public static readonly Engine_UiVerb OpenScreen = new(Engine_UiHostScreens.OpenPrefix, "Screen:open:<id>",
		"opens a host-owned screen: the game's (achievements 1013, multiplayer 1014) or a screens record a mod states (id 1000 and up) with a menu and a presentation, filled by its provider", Anywhere);

	public static readonly Engine_UiVerb Options = new("options:", "options:<page>",
		"opens a host options page (options:weather, options:mod:<mod>)", Anywhere);

	public static readonly Engine_UiVerb SettingToggle = new(Engine_UiScreenActions.SettingToggle, "setting:toggle:<key>",
		"flips a config switch between true and false", Anywhere);

	public static readonly Engine_UiVerb SettingCycle = new(Engine_UiScreenActions.SettingCycle, "setting:cycle:<key>:<a>,<b>,...",
		"steps a config switch through its values", Anywhere);

	public static readonly Engine_UiVerb SettingSet = new(Engine_UiScreenActions.SettingSet, "setting:set:<key>=<value>",
		"writes a config switch", Anywhere);

	public static readonly Engine_UiVerb Inventory = new(Engine_UiInventorySource.InventoryAction, "Inventory:<op>:<record>",
		"an inventory operation on the simulation (use, equip, gift...), run on the guest's tick", Anywhere);

	public static readonly Engine_UiVerb Quest = new(Engine_UiQuestSource.QuestAction, "Quest:select:<record>",
		"makes a quest primary through the world map's own answer", Anywhere);

	public static readonly Engine_UiVerb Sale = new(Engine_UiQuestSource.SaleAction, "Sale:<...>",
		"a world-map sale row's answer", Anywhere);

	public static readonly Engine_UiVerb Ability = new(Engine_UiAbilitySource.AbilityAction, "Ability:learn:<1..14>",
		"buys an ability through the guest's own purchase", Anywhere);

	public static readonly Engine_UiVerb PropertyRepair = new(Engine_UiPropertySource.RepairAction, "PropertyRepair:<uid>",
		"confirms and repairs a property for the menu's player", Anywhere);

	public static readonly Engine_UiVerb PropertyRepairAll = new(Engine_UiPropertySource.RepairAllAction, "PropertyRepairAll",
		"repairs every property", Anywhere);

	public static readonly Engine_UiVerb Partner = new(Engine_UiPropertySource.PartnerAction, "Partner:<...>",
		"a property partnership row's answer", Anywhere);

	public static readonly Engine_UiVerb ModBuy = new(Engine_UiModAbilities.BuyAction, "Mod:buy:<ability>",
		"buys a level of a mod ability (ui/abilities.json) for the row's player: the engine's purchase", Anywhere);

	public static readonly Engine_UiVerb ScreenBack = new(Engine_UiScreenActions.ScreenBack, "screen:back",
		"closes a guest screen through its own back lever", GuestScreen);

	public static readonly Engine_UiVerb ScreenDismiss = new(Engine_UiScreenActions.ScreenDismiss, "screen:dismiss",
		"dismisses a one-off guest panel", GuestScreen);

	public static readonly Engine_UiVerb ScreenUnlearn = new(Engine_UiScreenActions.ScreenUnlearn, "screen:unlearn",
		"the abilities screen's unlearn", GuestScreen);

	public static readonly Engine_UiVerb ScreenSelect = new(Engine_UiScreenActions.ScreenSelect, "screen:select",
		"A on a guest screen whose rows it does not answer by identity", GuestScreen);

	public static readonly Engine_UiVerb BoxDismiss = new(Engine_UiScreenActions.BoxDismiss, "box:dismiss",
		"dismisses the display box standing over a screen", GuestScreen);

	public static readonly Engine_UiVerb PauseTab = new(Engine_PauseMenu.TabCommandPrefix, "PauseTab:<tab>",
		"opens a pause tab through the guest's tab lever (2 map .. 8 options)", PauseOnly);

	public static readonly Engine_UiVerb PauseSaving = new(Engine_PauseMenu.SavingCommandPrefix, "PauseSaving:<0|1|2>",
		"the guest's saving answer: 0 save and continue, 1 save and quit, 2 quit without saving", PauseOnly);

	public static readonly Engine_UiVerb PauseQuit = new(Engine_PauseMenu.QuitCommandPrefix, "PauseQuit:<1|2>",
		"the saving answer, then leaves the app when the guest reaches its title", PauseOnly);

	public static readonly Engine_UiVerb PauseCoop = new(Engine_PauseMenu.CoopCommandPrefix, "PauseCoop:<leave|kick>",
		"player two leaves the co-op game, or the online host kicks them", PauseOnly);

	public static readonly Engine_UiVerb PauseOwned = new(Engine_PauseMenu.OwnedCommandPrefix, "PauseOwned:<id>",
		"the old spelling of Screen:open:<id>", Anywhere);

	public static readonly Engine_UiVerb QuitToDesktop = new(Engine_FrontEndMenu.QuitToDesktopCommand, "QuitToDesktop",
		"leaves the app from the title (in a game, PauseQuit: saves first)", FrontEndOnly);

	public static readonly Engine_UiVerb Network = new(Engine_UiNetworkSource.CommandPrefix, "Net:<op>[:<arg>]",
		"the multiplayer rows' answers (Net:book opens the multiplayer book)", Anywhere);

	/// <summary>Every verb the game registers, for the validator, the Studio and the guide.</summary>
	public static readonly IReadOnlyList<Engine_UiVerb> Game = new[]
	{
		Opens, Lua, OpenScreen, Options, SettingToggle, SettingCycle, SettingSet, Inventory, Quest, Sale, Ability,
		PropertyRepair, PropertyRepairAll, Partner, ModBuy, ScreenBack, ScreenDismiss, ScreenUnlearn, ScreenSelect,
		BoxDismiss, PauseTab, PauseSaving, PauseQuit, PauseCoop, PauseOwned, QuitToDesktop, Network,
	};

	/// <summary>The game verb a command names, matched as the dispatcher matches (longest prefix), or null.</summary>
	public static Engine_UiVerb? Of(string command)
	{
		Engine_UiVerb? best = null;
		foreach (Engine_UiVerb verb in Game)
		{
			bool hit = verb.IsPrefix ? command.StartsWith(verb.Name, StringComparison.Ordinal) : command == verb.Name;
			if (hit && (best is null || verb.Name.Length > best.Name.Length))
			{
				best = verb;
			}
		}

		return best;
	}
}
