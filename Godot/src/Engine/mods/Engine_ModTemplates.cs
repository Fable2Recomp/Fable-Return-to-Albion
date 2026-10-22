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
		new ModTemplate("fx_effect", "Effect",
			"An effect of the mod's own (fx/effects/), played by init.lua where a hero's hit lands — the same file the FX Lab writes.",
			(mod, title) => new[]
			{
				(Refii.Fx.Engine_FxModEffects.PathOf(mod + "_green_flame"), FxEffectJson(mod)),
				(ModLua.ModInitPath, Fill(FxEffectLua, mod, title)),
			}),
		new ModTemplate("native_handler", "Native handler",
			"A C++ handler for the Hit event under native/, compiled into the game by the launcher.",
			(mod, title) => new[]
			{
				("native/handler.cpp", ModApi.NativeTemplate(ModApi.Find("hit")!, mod)),
				("README.md", Fill(NativeReadme, mod, title)),
			}),
		new ModTemplate("mod_menu", "Mod menu",
			"A menu of the mod's own, opened from a row on the pause list; its rows call the mod's Lua.",
			(mod, title) => new[]
			{
				("ui/templates.json", Fill(ModMenuJson, mod, title).Replace("{id}", LuaIdentifier(mod))),
				(ModLua.ModInitPath, Fill(ModMenuLua, mod, title).Replace("{id}", LuaIdentifier(mod))),
				("language/en-uk/text/book.babel.csv", Fill(ModMenuBabel, mod, title)),
			}),
		new ModTemplate("front_end", "Front end",
			"A row on the title screen whose mod/frontend.lua puts up a menu box and answers it with a notice — mods' Lua where there is no game.",
			(mod, title) => new[]
			{
				("ui/templates.json", Fill(FrontEndJson, mod, title).Replace("{id}", LuaIdentifier(mod))),
				(ModLua.ModFrontendPath, Fill(FrontEndLua, mod, title).Replace("{id}", LuaIdentifier(mod))),
				("language/en-uk/text/book.babel.csv", Fill(FrontEndBabel, mod, title)),
			}),
		new ModTemplate("hud_text", "HUD text",
			"A line of HUD text in the top-right corner showing the hero's gold, a value init.lua publishes.",
			(mod, title) => new[]
			{
				(ModLua.ModInitPath, Fill(HudTextLua, mod, title)),
				("ui/hud.json", Fill(HudTextJson, mod, title)),
			}),
	};

	/// <summary>A mod name as a Lua identifier: every character that cannot be in one becomes '_'.</summary>
	private static string LuaIdentifier(string mod)
	{
		var name = new StringBuilder(mod.Length + 1);
		foreach (char c in mod)
		{
			name.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' ? c : '_');
		}

		return name.Length == 0 || char.IsDigit(name[0]) ? "_" + name : name.ToString();
	}

	public static ModTemplate? Find(string id) =>
		All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

	// ---------------------------------------------------------------------------------------
	// fx_effect
	// ---------------------------------------------------------------------------------------

	/// <summary>
	/// A small green flame in the bank's own model: one standard emitter on a sphere, one sprite
	/// system drawing a shipped flame sheet, built and written by the effect file's own writer.
	/// </summary>
	private static string FxEffectJson(string mod)
	{
		var d = new Refii.Fx.Engine_FxEffectJson.Definition { Name = mod + "_green_flame", UpdateFrequency = 1 };
		var emitter = new Refii.Fx.FxEmitter { TypeId = 0, SubType = 1, MaxActive = 48, InitialNum = 4, SpawnRadius = 0.15f };
		for (int i = 0; i < 28; i++)
		{
			emitter.Generic[i] = Refii.Fx.Engine_FxBank.MakeTimeline(i, i switch
			{
				2 => new[] { (0f, 24f) },
				4 => new[] { (0f, 0.6f) },
				5 => new[] { (0f, 0.2f) },
				11 => new[] { (0f, 1f) },
				_ => Array.Empty<(float, float)>(),
			});
		}

		emitter.Vec0 = Refii.Fx.Engine_FxBank.MakeVecTimeline(0, Array.Empty<(float, float, float, float)>());
		emitter.Vec1 = Refii.Fx.Engine_FxBank.MakeVecTimeline(1, Array.Empty<(float, float, float, float)>());
		emitter.Vec2 = Refii.Fx.Engine_FxBank.MakeVecTimeline(2, Array.Empty<(float, float, float, float)>());
		emitter.LightColour = Refii.Fx.Engine_FxBank.MakeVecTimeline(3, Array.Empty<(float, float, float, float)>());
		Refii.Fx.Engine_FxBank.LinkParameters(emitter);
		var system = new Refii.Fx.FxSystemDef { Mode = 1, LastStartFrames = 30, ChildCount = 1 };
		system.Visuals.Add(new Refii.Fx.FxVisual
		{
			HasTexture = true, Texture = "art\\fx\\pfx_textures\\fxt_flames_double_03.tex", TexCols = 4, TexRows = 4, FrameSpeed = 20f,
			HasColor = true, Color = new[] { 0.4f, 1f, 0.5f }, ColorSq = new[] { 0.16f, 1f, 0.25f }, HasSize = true, SizeX = 0.5f, SizeY = 0.7f,
			HasSelfIllum = true, SelfIllum = 1f, HasAlpha = true, Alpha = 1f,
		});
		system.Visuals.Add(new Refii.Fx.FxVisual
		{
			HasTexture = true, Texture = "art\\fx\\pfx_textures\\fxt_flames_double_03.tex", TexCols = 4, TexRows = 4, FrameSpeed = 20f,
			HasColor = true, Color = new[] { 0.1f, 0.5f, 0.2f }, ColorSq = new[] { 0.01f, 0.25f, 0.04f }, HasSize = true, SizeX = 0.2f, SizeY = 0.3f,
			HasSelfIllum = true, SelfIllum = 1f, HasAlpha = true, Alpha = 0f, Delay = 1f, DelayFrames = 30, InvSpan = 1f / 30f,
		});
		d.Emitters.Add(emitter);
		d.Systems.Add(system);
		d.Parts.Add(new Refii.Fx.FxEffectPart(0, 0, 1, 0));
		return Refii.Fx.Engine_FxEffectJson.Serialize(d);
	}

	private const string FxEffectLua =
		"-- {title}: an effect of the mod's own.\n" +
		"--\n" +
		"-- fx/effects/{mod}_green_flame.json is the particle bank's own model as JSON; open it in the\n" +
		"-- Mod Studio's FX Lab (Effects) to change its emitters with a live preview, or clone any\n" +
		"-- shipped effect there. Refii.play_fx asks Godot to draw it; the game spawns nothing.\n" +
		"\n" +
		"-- \"hit_landed\": a hit was delivered. Where a player's hero lands one, the flame bursts on\n" +
		"-- the victim for a second and a half. Refii.play_fx_on(e.victim, ...) would ride the victim.\n" +
		"Refii.on(\"hit_landed\", function(e)\n" +
		"\tif e.attacker_player == 0 then\n" +
		"\t\treturn\n" +
		"\tend\n" +
		"\tlocal x, y, z = Refii.position(e.victim)\n" +
		"\tif x ~= nil then\n" +
		"\t\tRefii.play_fx(\"{mod}_green_flame\", x, y, z, { duration = 1.5, scale = 0.8 })\n" +
		"\tend\n" +
		"end)\n";

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
		"-- \"tick\": once per game step, e.seconds that step's. Refii.hero(1) is\n" +
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
	// mod_menu
	// ---------------------------------------------------------------------------------------

	// A host-only row on the pause list (the guest's own rows are matched to its list by order, so
	// a row it never spawned must say so) that opens the mod's menu; B returns to the pause list.
	private const string ModMenuJson =
		"{\n" +
		"\t\"menus\": {\n" +
		"\t\t\"pause-root\": {\n" +
		"\t\t\t\"patch\": true,\n" +
		"\t\t\t\"entries\": [\n" +
		"\t\t\t\t{ \"name\": \"{mod}.menu\", \"tag\": \"{MOD}_MENU\", \"opens\": \"{mod}.menu\", \"hostOnly\": true }\n" +
		"\t\t\t]\n" +
		"\t\t},\n" +
		"\t\t\"{mod}.menu\": {\n" +
		"\t\t\t\"title\": \"{MOD}_MENU\",\n" +
		"\t\t\t\"entries\": [\n" +
		"\t\t\t\t{ \"name\": \"greet\", \"tag\": \"{MOD}_MENU_GREET\", \"command\": \"Mod:lua:{id}_menu.greet\" },\n" +
		"\t\t\t\t{ \"name\": \"gold\", \"tag\": \"{MOD}_MENU_GOLD\", \"command\": \"Mod:lua:{id}_menu.give_gold:100\" }\n" +
		"\t\t\t]\n" +
		"\t\t}\n" +
		"\t}\n" +
		"}\n";

	private const string ModMenuLua =
		"-- {title}: a menu of the mod's own.\n" +
		"--\n" +
		"-- ui/templates.json defines the menu \"{mod}.menu\" and a row on the pause list that opens it.\n" +
		"-- A row whose command is Mod:lua:<function>[:<arg>] calls that global function as\n" +
		"-- function(arg, e): arg is the text after the second colon (nil when there is none), e the\n" +
		"-- row's mod_menu_row event (e.menu, e.row, e.player, e.mod).\n" +
		"\n" +
		"{id}_menu = {}\n" +
		"\n" +
		"function {id}_menu.greet(arg, e)\n" +
		"\tRefii.log(\"{title}: hello, player \" .. tostring(e.player))\n" +
		"end\n" +
		"\n" +
		"-- The player who chose the row gets the gold: e.player is 1 or 2.\n" +
		"function {id}_menu.give_gold(arg, e)\n" +
		"\tlocal hero = Refii.hero(e.player)\n" +
		"\tif hero ~= nil then\n" +
		"\t\tRefii.give_gold(hero, tonumber(arg) or 0)\n" +
		"\tend\n" +
		"end\n" +
		"\n" +
		"-- Every row of the menu raises mod_menu_row, whatever its command.\n" +
		"Refii.on(\"mod_menu_row\", function(e)\n" +
		"\tRefii.log(\"{title}: \" .. tostring(e.menu) .. \"/\" .. tostring(e.row) .. \" chosen\")\n" +
		"end)\n";

	private const string ModMenuBabel =
		"key,value\n" +
		"{MOD}_MENU,{title}\n" +
		"{MOD}_MENU_GREET,Say hello\n" +
		"{MOD}_MENU_GOLD,Take 100 gold\n";

	// ---------------------------------------------------------------------------------------
	// front_end
	// ---------------------------------------------------------------------------------------

	private const string FrontEndJson =
		"{\n" +
		"\t\"menus\": {\n" +
		"\t\t\"frontend-root\": {\n" +
		"\t\t\t\"patch\": true,\n" +
		"\t\t\t\"entries\": [\n" +
		"\t\t\t\t{ \"name\": \"{mod}.title\", \"tag\": \"{MOD}_TITLE_ROW\", \"command\": \"Mod:lua:{id}_frontend.ask\" }\n" +
		"\t\t\t]\n" +
		"\t\t}\n" +
		"\t}\n" +
		"}\n";

	private const string FrontEndLua =
		"-- {title} on the title screen (mod/frontend.lua).\n" +
		"--\n" +
		"-- No game runs here: no hero, no save, no world. This file runs in the front end's own Lua,\n" +
		"-- the current presentation's, where Refii offers notice, menu_box, message_box, open_menu,\n" +
		"-- publish/value and log, and the events mod_menu_row, mod_menu_box, mod_message_box and\n" +
		"-- mod_notice (Refii.frontend is true). ui/templates.json adds a row to the title's menu whose\n" +
		"-- command calls {id}_frontend.ask; the box's answer comes back as mod_menu_box.\n" +
		"\n" +
		"{id}_frontend = {}\n" +
		"\n" +
		"local asked = nil\n" +
		"\n" +
		"function {id}_frontend.ask(arg, e)\n" +
		"\tasked = Refii.menu_box(\"{title}\", \"Pick one.\", { \"Hello\", \"Goodbye\" }, e and e.player or 0)\n" +
		"end\n" +
		"\n" +
		"Refii.on(\"mod_menu_box\", function(e)\n" +
		"\tif asked == nil or e.id ~= asked then\n" +
		"\t\treturn\n" +
		"\tend\n" +
		"\tasked = nil\n" +
		"\tif e.option >= 0 then\n" +
		"\t\tRefii.notice(\"{title}\", e.option == 0 and \"Hello from the title screen.\" or \"Goodbye.\", 0, e.player)\n" +
		"\tend\n" +
		"end)\n" +
		"\n" +
		"Refii.on(\"mod_notice\", function(e)\n" +
		"\tRefii.log(\"{title}: notice \" .. tostring(e.id) .. \" gone (\" .. tostring(e.outcome) .. \")\")\n" +
		"end)\n";

	private const string FrontEndBabel =
		"key,value\n" +
		"{MOD}_TITLE_ROW,{title}\n";

	// ---------------------------------------------------------------------------------------
	// hud_text
	// ---------------------------------------------------------------------------------------

	private const string HudTextLua =
		"-- {title}: a line of HUD text fed from Lua.\n" +
		"--\n" +
		"-- A text piece's \"text\" is a literal, a Babel tag, or mod:<key> — a value Refii.publish\n" +
		"-- states — drawn through its \"format\". An unpublished value draws nothing.\n" +
		"\n" +
		"Refii.on(\"tick\", function(e)\n" +
		"\tlocal hero = Refii.hero(1)\n" +
		"\tif hero == nil then\n" +
		"\t\treturn\n" +
		"\tend\n" +
		"\tRefii.publish(\"{mod}.gold\", Refii.gold(hero) or 0)\n" +
		"end)\n";

	// Right-aligned against the top-right corner: the anchor is the corner, the offset +X right, +Y down.
	private const string HudTextJson =
		"{\n" +
		"\t\"pieces\": [\n" +
		"\t\t{\n" +
		"\t\t\t\"id\": \"{mod}.gold_text\",\n" +
		"\t\t\t\"kind\": \"text\",\n" +
		"\t\t\t\"anchor\": [1, 0],\n" +
		"\t\t\t\"offset\": [-48, 40],\n" +
		"\t\t\t\"text\": \"mod:{mod}.gold\",\n" +
		"\t\t\t\"format\": \"{0:0} gold\",\n" +
		"\t\t\t\"size\": 18,\n" +
		"\t\t\t\"colour\": \"#d8b23a\",\n" +
		"\t\t\t\"align\": \"right\"\n" +
		"\t\t}\n" +
		"\t]\n" +
		"}\n";

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
