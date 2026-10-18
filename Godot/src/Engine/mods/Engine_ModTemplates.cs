using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Refii.Mods;

/// <summary>
/// One starter mod: the files it writes into a fresh package, each rendered for the mod's name
/// and title. <see cref="Render"/> fills <c>{mod}</c>, <c>{MOD}</c> (upper-cased) and
/// <c>{title}</c>; a template whose text follows the catalog builds it in code instead.
/// </summary>
public sealed record ModTemplate(
	string Id,
	string Title,
	string Description,
	Func<string, string, IReadOnlyList<(string Path, string Text)>> Build)
{
	/// <summary>The files for a mod named <paramref name="mod"/>, by their path inside the package.</summary>
	public IReadOnlyList<(string Path, string Text)> Render(string mod, string title) =>
		Build(mod, title.Length == 0 ? mod : title);
}

/// <summary>
/// THE STARTER MODS: what "New mod from template" writes, each a working example of one
/// content kind on the mod API (<see cref="ModApi"/>). The package metadata is the creator's
/// (<c>RefiiModApi.CreateMod</c>); a template adds only content files. Every template's Lua
/// is held to the game's compiler and every JSON to a parser by a test.
/// </summary>
public static class ModTemplates
{
	public static readonly IReadOnlyList<ModTemplate> All = new[]
	{
		new ModTemplate("combat_tweak", "Combat tweak",
			"Scales the heroes' hit damage by a settings.json value and logs a downed hero.",
			(mod, title) => new[]
			{
				("settings.json", Fill(CombatTweakSettings, mod, title)),
				(ModLua.ModInitPath, Fill(CombatTweakLua, mod, title)),
			}),
		new ModTemplate("hud_piece", "HUD piece",
			"A gold meter under the health widget, fed by a value init.lua publishes every tick.",
			(mod, title) => new[]
			{
				(ModLua.ModInitPath, Fill(HudPieceLua, mod, title)),
				("ui/hud.json", Fill(HudPieceJson, mod, title)),
				("language/en-uk/text/book.babel.csv", Fill(HudPieceBabel, mod, title)),
			}),
		new ModTemplate("custom_spell", "Custom spell",
			"Frost Nova in Chaos's slot: a spells/ record with its own damage and Lua on cast and hit.",
			(mod, title) => new[]
			{
				("spells/frost.json", Fill(CustomSpellJson, mod, title)),
				("settings.json", Fill(CustomSpellSettings, mod, title)),
			}),
		new ModTemplate("quest_hooks", "Quest hooks",
			"Handlers for level loads, items and kills, counting in the save; quest and dialogue events when the API has them.",
			(mod, title) => new[] { (ModLua.ModInitPath, QuestHooksLua(mod, title)) }),
		new ModTemplate("native_handler", "Native handler",
			"A C++ handler for the Hit event under native/, compiled into the game by the launcher.",
			(mod, title) => new[]
			{
				("native/handler.cpp", ModApi.NativeTemplate(ModApi.Find("hit")!, mod)),
				("README.md", Fill(NativeReadme, mod, title)),
			}),
	};

	public static ModTemplate? Find(string id) =>
		All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

	private static string Fill(string text, string mod, string title) =>
		text.Replace("{mod}", mod).Replace("{MOD}", mod.ToUpperInvariant()).Replace("{title}", title);

	// ---------------------------------------------------------------------------------------
	// combat_tweak
	// ---------------------------------------------------------------------------------------

	private const string CombatTweakSettings =
		"{\n" +
		"\t\"{mod}\": {\n" +
		"\t\t\"hero_damage\": 1.5\n" +
		"\t}\n" +
		"}\n";

	private const string CombatTweakLua =
		"-- {title}: a combat tweak.\n" +
		"--\n" +
		"-- Runs once per session, after the game's own scripts. Refii.on subscribes to an event the\n" +
		"-- game raises (docs/mod_api.md lists them all); e is a table of that event's fields.\n" +
		"\n" +
		"-- The multiplier lives in settings.json as \"{mod}.hero_damage\", so a player can tune it.\n" +
		"local multiplier = Refii.setting(\"{mod}.hero_damage\", 1.5)\n" +
		"\n" +
		"-- \"hit\": every hit, before it is delivered. e.damage is writable: assigning it changes what\n" +
		"-- the victim takes. e.attacker_player is 1 or 2 when a player's hero dealt the hit.\n" +
		"Refii.on(\"hit\", function(e)\n" +
		"\tif e.attacker_player ~= 0 then\n" +
		"\t\te.damage = e.damage * multiplier\n" +
		"\tend\n" +
		"\t-- return true  -- drops the hit entirely: no damage, no response\n" +
		"end)\n" +
		"\n" +
		"-- \"hero_downed\": a player's hero reached zero health. Returning true here skips the game's\n" +
		"-- knockdown, scar and revive flow and leaves the hero with 1 health.\n" +
		"Refii.on(\"hero_downed\", function(e)\n" +
		"\tRefii.log(\"{title}: player \" .. tostring(e.player) .. \"'s hero was downed\")\n" +
		"end)\n";

	// ---------------------------------------------------------------------------------------
	// hud_piece
	// ---------------------------------------------------------------------------------------

	private const string HudPieceLua =
		"-- {title}: a HUD piece fed from Lua.\n" +
		"--\n" +
		"-- Refii.publish(key, number) states a mod value; ui/hud.json reads it as \"mod:<key>\".\n" +
		"-- The meter here shows the hero's purse against a cap, under the health widget.\n" +
		"\n" +
		"local cap = 1000  -- the meter is full at this much gold\n" +
		"\n" +
		"-- \"tick\": once per game step; Lua sees it at most ten times a second. Refii.hero(1) is\n" +
		"-- player one's hero, or nil before a session starts.\n" +
		"Refii.on(\"tick\", function(e)\n" +
		"\tlocal hero = Refii.hero(1)\n" +
		"\tif hero == nil then\n" +
		"\t\treturn\n" +
		"\tend\n" +
		"\tlocal gold = Refii.gold(hero) or 0\n" +
		"\tif gold > cap then\n" +
		"\t\tgold = cap\n" +
		"\tend\n" +
		"\tRefii.publish(\"{mod}.gold_share\", gold)\n" +
		"\tRefii.publish(\"{mod}.gold_cap\", cap)\n" +
		"end)\n";

	// A meter hung beneath the health widget's bar, the shape Souls Combat's co-op stamina uses.
	private const string HudPieceJson =
		"{\n" +
		"\t\"pieces\": [\n" +
		"\t\t{\n" +
		"\t\t\t\"id\": \"{mod}.gold_share\",\n" +
		"\t\t\t\"kind\": \"meter\",\n" +
		"\t\t\t\"follow\": \"group.PlayerHealth.PlayerHealth\",\n" +
		"\t\t\t\"followElement\": \"BarMax, Bar\",\n" +
		"\t\t\t\"offset\": [0, 3],\n" +
		"\t\t\t\"size\": [0, 6],\n" +
		"\t\t\t\"value\": \"mod:{mod}.gold_share\",\n" +
		"\t\t\t\"max\": \"mod:{mod}.gold_cap\",\n" +
		"\t\t\t\"fill\": \"#d8b23a\",\n" +
		"\t\t\t\"back\": \"#000000b8\"\n" +
		"\t\t}\n" +
		"\t]\n" +
		"}\n";

	private const string HudPieceBabel =
		"key,value\n" +
		"{MOD}_GOLD_SHARE_NAME,Gold\n" +
		"{MOD}_GOLD_SHARE_DESCRIPTION,\"The hero's purse, shown as a share of a thousand gold.\"\n";

	// ---------------------------------------------------------------------------------------
	// custom_spell
	// ---------------------------------------------------------------------------------------

	private const string CustomSpellJson =
		"{\n" +
		"\t\"replaces\": \"Chaos\",\n" +
		"\t\"name\": \"Frost Nova\",\n" +
		"\t\"description\": \"A ring of frost that chills everything it touches.\",\n" +
		"\t\"damage\": 1.3,\n" +
		"\t\"on_cast\": \"Refii.log('frost cast')\",\n" +
		"\t\"on_hit\": \"Refii.log('frost hit ' .. tostring(e.victim) .. ' chilled for ' .. tostring(Refii.setting('{mod}.chill_seconds', 2)) .. 's')\"\n" +
		"}\n";

	private const string CustomSpellSettings =
		"{\n" +
		"\t\"{mod}\": {\n" +
		"\t\t\"chill_seconds\": 2\n" +
		"\t}\n" +
		"}\n";

	// ---------------------------------------------------------------------------------------
	// quest_hooks — built from the catalog, so a handler lists the fields the API states today
	// ---------------------------------------------------------------------------------------

	private static string QuestHooksLua(string mod, string title)
	{
		var text = new StringBuilder();
		text.Append("-- ").Append(title).Append(": quest-style hooks.\n")
			.Append("--\n")
			.Append("-- Each handler runs when the game reaches that seam; e carries the event's fields, listed\n")
			.Append("-- above each body. What a mod puts in RefiiModSave persists with the save game.\n")
			.Append("\n")
			.Append("RefiiModSave = RefiiModSave or {}\n")
			.Append("local kills_key = \"").Append(mod).Append(".kills\"\n")
			.Append("local items_key = \"").Append(mod).Append(".items\"\n")
			.Append("\n");

		text.Append(Handler("level_loaded",
			$"\tRefii.log(\"{title}: entered \" .. tostring(e.level) .. (e.from_save and \" from a save\" or \"\"))\n"));
		text.Append('\n');
		text.Append(Handler("item_added",
			"\tif e.player ~= 0 then\n" +
			"\t\tRefiiModSave[items_key] = (RefiiModSave[items_key] or 0) + 1\n" +
			$"\t\tRefii.log(\"{title}: player \" .. tostring(e.player) .. \" received item \" .. tostring(e.definition))\n" +
			"\tend\n"));
		text.Append('\n');
		text.Append(Handler("entity_died",
			"\tif e.killer_player ~= 0 then\n" +
			"\t\tRefiiModSave[kills_key] = (RefiiModSave[kills_key] or 0) + 1\n" +
			$"\t\tRefii.log(\"{title}: \" .. tostring(Refii.name(e.entity)) .. \" slain; \" .. tostring(RefiiModSave[kills_key]) .. \" so far\")\n" +
			"\tend\n"));

		// Events the catalog gains later (quest state, dialogue) join as the catalog's own skeletons.
		foreach (string name in new[] { "quest_state", "dialogue" })
		{
			if (ModApi.Find(name) is { } e)
			{
				text.Append('\n').Append("-- ").Append(e.Summary).Append('\n').Append(ModApi.LuaTemplate(e));
			}
		}

		return text.ToString();
	}

	/// <summary>A <c>Refii.on</c> handler with the event's fields commented above <paramref name="body"/>.</summary>
	private static string Handler(string luaName, string body)
	{
		ModApiEvent e = ModApi.Find(luaName) ?? throw new InvalidOperationException($"no event '{luaName}' in the catalog");
		var text = new StringBuilder();
		text.Append("-- ").Append(e.Summary).Append('\n');
		text.Append("Refii.on(\"").Append(e.LuaName).Append("\", function(e)\n");
		// Lua sees every field but the native-only pointers ("const char* / string" is a string).
		foreach (ModApiField f in e.Fields.Where(f => !f.Type.EndsWith('*')))
		{
			text.Append("\t-- e.").Append(f.Name).Append(": ").Append(f.Meaning).Append('\n');
		}

		text.Append(body);
		if (e.Cancellable)
		{
			text.Append("\t-- return true  -- ").Append(e.Cancel).Append('\n');
		}

		text.Append("end)\n");
		return text.ToString();
	}

	// ---------------------------------------------------------------------------------------
	// native_handler
	// ---------------------------------------------------------------------------------------

	private const string NativeReadme =
		"# {title}\n" +
		"\n" +
		"A native mod: C++ under `native/` against `core/refii_mod.h`.\n" +
		"\n" +
		"The launcher compiles every `native/*.cpp` of an enabled mod into the game module when the\n" +
		"game is built (allow the mod's native code when the launcher asks). `native/handler.cpp`\n" +
		"subscribes to the Hit event with `REFII_MOD_ON(Hit, OnHit)`; its fields are listed in the\n" +
		"handler, and `e.damage` is writable. A guest function no event covers is hooked by its config\n" +
		"name with `REX_EXTERN(name) { ... }` and `REFII_MOD_ORIGINAL(name)`.\n" +
		"\n" +
		"The Mod Studio's Guide (Script API & Hard Mods) and `docs/mod_api.md` describe every event and\n" +
		"helper; `RefiiModApi.NewNativeHandler(event, file)` writes a skeleton for any of them.\n";
}
