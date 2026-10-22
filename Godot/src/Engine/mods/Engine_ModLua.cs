using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Refii.Bnk;

namespace Refii.Mods;

/// <summary>
/// A mod's own Lua, run in the GAME VM after the shipped scripts: <c>mod/init.lua</c> once the
/// startup scripts have defined their tables, <c>mod/camera.lua</c> each time the camera scripts
/// run.
/// </summary>
/// <remarks>
/// <para>A MOD'S LUA IS ITS OWN CODE, never an edited copy of a shipped script — that is derived
/// game text and cannot be redistributed. It patches what the shipped scripts defined
/// (<c>CombatWheel.ActiveAttackerTimeOut = 6</c>, a field on <c>CombatStyles.X</c>, a wrapped
/// <c>WheelRing.Create</c>).</para>
/// <para>WHEN, from the binary (TU1): every game session builds a fresh VM and
/// <c>rex_lhLuaScriptSystem_LoadStartupScripts</c> (0x82459280) runs every shipped script except
/// the cameras'; <c>rex_lhLuaScriptSystem_RunCameraScripts</c> (0x8245A610) runs those when a
/// player's CameraManager is built — the first level of a session, a save load, a co-op join.
/// The payload hook runs the composed text right after each (src/vfs/src/hooks/mod_lua_hooks.cpp),
/// so init.lua runs once per session and camera.lua may run several times: it must be
/// idempotent.</para>
/// <para>Every enabled mod's file is composed, lowest priority first, into one chunk served at
/// <see cref="InitKey"/> / <see cref="CameraKey"/>, each mod inside its own <c>pcall</c> so one
/// mod's runtime error cannot stop another's; the error lands in the global
/// <c>RefiiModErrors</c>. Each file is compiled first with the game's own compiler, and one that
/// does not compile is left out and named — a syntax error would otherwise fail the whole
/// chunk.</para>
/// <para>A MOD'S LUA MAY BE SEVERAL FILES: every other <c>mod/*.lua</c> is a MODULE, reached from
/// the mod's own chunks with <c>require("name")</c> — the path under <c>mod/</c> without
/// <c>.lua</c>, <c>.</c> or <c>/</c> between folders. The game VM has no <c>require</c> and no
/// file system to resolve one from, so each module is composed INTO the init chunk as a function
/// registered under the mod's name (<see cref="RequireRuntime"/>), and <c>require</c> is a local
/// of the mod's block that runs the function once and caches its result, as Lua's does: a module
/// that returns nothing yields <c>true</c>, a module required while it is running (a cycle) sees
/// <c>true</c>. Modules are lazy — only what a chunk requires runs — and each mod sees only its
/// own. The front-end chunk registers them again in its own state. A reload that runs the init
/// chunk again (Live iteration) registers fresh module functions, so the next <c>require</c>
/// runs the new file. Each module is compiled with the game's compiler like the entry points.</para>
/// </remarks>
public static class ModLua
{
	public const string ModInitPath = "mod/init.lua";
	public const string ModCameraPath = "mod/camera.lua";

	/// <summary>The folder every file of a mod's Lua lives in; any other <c>.lua</c> under it is a module.</summary>
	public const string ModLuaFolder = "mod/";

	/// <summary>
	/// The module registry and <c>require</c>, in the preamble: <c>Refii._mod_scope(mod)</c> opens a
	/// fresh registry for a mod (the composed chunk fills <c>modules[name]</c> with each file as a
	/// function), <c>Refii._require(mod)</c> is that mod's <c>require</c>. A name is lower-cased with
	/// <c>.</c> read as <c>/</c>; a missing module is an error naming the file; the first result is
	/// cached, nil as <c>true</c>, and a cycle sees <c>true</c>.
	/// </summary>
	public const string RequireRuntime =
		"Refii._modules = Refii._modules or {}\n" +
		"function Refii._mod_scope(mod)\n" +
		"\tlocal scope = { modules = {}, loaded = {} }\n" +
		"\tRefii._modules[mod] = scope\n" +
		"\treturn scope\n" +
		"end\n" +
		"function Refii._require(mod)\n" +
		"\treturn function(name)\n" +
		"\t\tname = string.lower((string.gsub(tostring(name), '%.', '/')))\n" +
		"\t\tlocal scope = Refii._modules[mod]\n" +
		"\t\tlocal module = scope and scope.modules[name]\n" +
		"\t\tif module == nil then error('require: no mod/' .. name .. '.lua in ' .. tostring(mod), 2) end\n" +
		"\t\tlocal loaded = scope.loaded[name]\n" +
		"\t\tif loaded ~= nil then return loaded end\n" +
		"\t\tscope.loaded[name] = true\n" +
		"\t\tlocal result = module(name)\n" +
		"\t\tif result == nil then result = true end\n" +
		"\t\tscope.loaded[name] = result\n" +
		"\t\treturn result\n" +
		"\tend\n" +
		"end\n";

	/// <summary>
	/// A mod's Lua for the TITLE and the FRONT END, where there is no game VM: run in the current
	/// Gameface presentation's own Lua state (src/vfs/src/hooks/mod_frontend_lua.cpp), which is a
	/// different Lua build with no world, no hero and no save. Its own entry point, as a client and
	/// a server entry point are separate: init.lua patches the game's scripts, which do not exist
	/// there. What works in it is <see cref="FrontendPreamble"/>'s list.
	/// </summary>
	public const string ModFrontendPath = "mod/frontend.lua";

	/// <summary>
	/// What the init chunk starts with whenever a mod ships one: <c>RefiiModSave</c>, a table
	/// that persists WITH THE SAVE. The game builds its save through the global <c>GetSaveTable</c>
	/// and restores through <c>LoadScriptSave</c> (saveloadsystem.lua; the engine looks both up by
	/// name every time — TU1 0x8245C7B8 / 0x8245CC90), so wrapping them carries one more key. Plain
	/// data only (numbers, strings, tables of them): Pluto copies those by value. The shipped
	/// LoadScriptSave ignores keys it does not know, so a save made with mods loads without them.
	/// </summary>
	private const string SavePreamble =
		"RefiiModSave = RefiiModSave or {}\n" +
		"if GetSaveTable and LoadScriptSave and not RefiiModSaveWrapped then\n" +
		"\tRefiiModSaveWrapped = true\n" +
		"\tlocal get_save, load_save = GetSaveTable, LoadScriptSave\n" +
		"\tGetSaveTable = function() local t = get_save() t.RefiiMods = RefiiModSave return t end\n" +
		"\tLoadScriptSave = function(t)\n" +
		"\t\tif t and type(t.RefiiMods) == 'table' then RefiiModSave = t.RefiiMods end\n" +
		"\t\treturn load_save(t)\n" +
		"\tend\n" +
		"end\n";

	/// <summary>
	/// THE LUA SIDE OF THE MOD API, ahead of every mod's init: <c>Refii.on(event, fn)</c>
	/// subscribes a handler to a named event (the names and fields are
	/// <c>core/refii_mod_events.h</c>'s, lower-case: "hit", "spell_launch", "tick" ...), run by
	/// the payload's event bus as <c>Refii._fire(kind, fields)</c>; a handler that returns
	/// <c>true</c> cancels where the event allows it. <c>Refii.setting(key, default)</c> reads the
	/// mods' settings.json values composed in as <c>RefiiSettings</c>; <c>Refii.hero(player)</c>
	/// is the player's hero id as the bus last stated it. A handler's error lands in
	/// <c>RefiiModErrors</c> and never stops the others. The kinds table mirrors
	/// <see cref="ModApi.Events"/>, which is checked by a test.
	/// </summary>
	private static string RuntimePreamble()
	{
		var text = new StringBuilder();
		text.Append("Refii = Refii or {}\n");
		text.Append("Refii.api_version = ").Append(ModApi.Version).Append('\n');
		text.Append("Refii._handlers = Refii._handlers or {}\n");
		// THE SUBSCRIPTION MASKS ARE GROUPS OF ModApi.LuaMaskBits (20) BITS: this Lua's number is a
		// 32-bit float, exact only to 24 bits, so a 31-bit sum loses its low bits once a high one is
		// set. Group g holds kinds 20*(g-1) .. 20*g-1; the payload reads ModApi.LuaMaskGroups of
		// them (mod_events.cpp ReadLuaMask), so the capacity is ModApi.LuaMaskCapacity kinds.
		text.Append("Refii._masks = Refii._masks or {}\n");
		text.Append("Refii._dirty = false\n");
		text.Append("Refii._hero = Refii._hero or {}\n");
		text.Append("Refii._kinds = {");
		foreach (ModApiEvent e in ModApi.Events)
		{
			text.Append(e.LuaName).Append('=').Append(e.Kind).Append(',');
		}

		text.Append("}\n");
		// The writable numbers per kind: a handler's change to one goes back through the native
		// Refii._write while the chunk runs (mod_events.cpp WriteLuaField).
		text.Append("Refii._writable = {");
		foreach (IGrouping<int, (int Kind, string Field)> kind in ModApi.WritableFields.GroupBy(w => w.Kind))
		{
			text.Append('[').Append(kind.Key).Append("]={");
			foreach ((_, string field) in kind)
			{
				text.Append('\'').Append(field).Append("',");
			}

			text.Append("},");
		}

		text.Append("}\n");
		text.Append(
			"function Refii.on(name, fn)\n" +
			"\tlocal kind = Refii._kinds[name]\n" +
			"\tif kind == nil then error('Refii.on: no event \"' .. tostring(name) .. '\"') end\n" +
			"\tif type(fn) ~= 'function' then error('Refii.on: a function is needed for \"' .. name .. '\"') end\n" +
			"\tlocal list = Refii._handlers[kind]\n" +
			"\tif list == nil then list = {} Refii._handlers[kind] = list end\n" +
			"\tlist[#list + 1] = fn\n" +
			"\tlocal group = math.floor(kind / " + ModApi.LuaMaskBits + ") + 1\n" +
			"\tlocal bit = 2 ^ (kind % " + ModApi.LuaMaskBits + ")\n" +
			"\tlocal mask = Refii._masks[group] or 0\n" +
			"\tif math.floor(mask / bit) % 2 == 0 then Refii._masks[group] = mask + bit end\n" +
			"\tRefii._dirty = true\n" +
			"end\n" +
			"function Refii._fire(kind, e)\n" +
			"\tlocal list = Refii._handlers[kind]\n" +
			"\tif list == nil then return true end\n" +
			"\tlocal writable = Refii._writable[kind]\n" +
			"\tlocal before = nil\n" +
			"\tif writable then before = {} for i = 1, #writable do before[i] = e[writable[i]] end end\n" +
			"\tlocal keep = true\n" +
			"\tfor i = 1, #list do\n" +
			"\t\tlocal ok, cancel = pcall(list[i], e)\n" +
			"\t\tif not ok then Refii._report('event ' .. tostring(kind), cancel)\n" +
			"\t\telseif cancel == true then keep = false end\n" +
			"\tend\n" +
			"\tif writable and Refii._write then\n" +
			"\t\tfor i = 1, #writable do\n" +
			"\t\t\tlocal v = e[writable[i]]\n" +
			"\t\t\tif v ~= before[i] then\n" +
			"\t\t\t\tif type(v) == 'number' then Refii._write(kind, writable[i], v)\n" +
			"\t\t\t\telseif type(v) == 'boolean' then Refii._write(kind, writable[i], v and 1 or 0) end\n" +
			"\t\t\tend\n" +
			"\t\tend\n" +
			"\tend\n" +
			"\treturn keep\n" +
			"end\n" +
			"function Refii._report(where, err)\n" +
			"\tlocal text = tostring(where) .. ': ' .. tostring(err)\n" +
			"\tRefiiModErrors = (RefiiModErrors or '') .. text .. '\\n'\n" +
			"\tif #RefiiModErrors > 4096 then RefiiModErrors = string.sub(RefiiModErrors, -4096) end\n" +
			"\tif Refii._error then Refii._error(text) end\n" +
			"end\n" +
			"function Refii.setting(key, default)\n" +
			"\tlocal v = RefiiSettings and RefiiSettings[key]\n" +
			"\tif v == nil then return default end\n" +
			"\treturn v\n" +
			"end\n" +
			"function Refii.hero(player)\n" +
			"\tlocal h = Refii._hero[player or 1]\n" +
			"\tif h == nil or h == 0 then return nil end\n" +
			"\treturn h\n" +
			"end\n" +
			MenuRowRuntime +
			MenuRowsRuntime +
			// play_fx / play_fx_on unpack the options table for the native Refii._play_fx (the
			// game's Lua C API here has no array reads): duration, scale, tint {r, g, b},
			// intensity, loop (true unless false), offset {x, y, z}, at (a model helper name).
			"function Refii._fx_options(o)\n" +
			"\to = o or {}\n" +
			"\tlocal t = o.tint or {}\n" +
			"\tlocal f = o.offset or {}\n" +
			"\treturn o.duration or 0, o.scale or 1, t[1] or t.r or 1, t[2] or t.g or 1, t[3] or t.b or 1,\n" +
			"\t\to.intensity or 1, (o.loop == false) and 0 or 1, f[1] or f.x or 0, f[2] or f.y or 0, f[3] or f.z or 0, o.at or ''\n" +
			"end\n" +
			"function Refii.play_fx(name, x, y, z, o)\n" +
			"\tif Refii._play_fx == nil then return nil end\n" +
			"\treturn Refii._play_fx(name, x or 0, y or 0, z or 0, 0, Refii._fx_options(o))\n" +
			"end\n" +
			"function Refii.play_fx_on(e, name, o)\n" +
			"\tif Refii._play_fx == nil or e == nil or e == 0 then return nil end\n" +
			"\treturn Refii._play_fx(name, 0, 0, 0, e, Refii._fx_options(o))\n" +
			"end\n" +
			// cast_spell(caster, type, targeted, target, level [, pos [, dir]]) -> spell, serial: the
			// native Refii._cast_spell with the start position and facing unpacked from {x, y, z}
			// tables (the caster's own when absent); targeted is a boolean or 0/1.
			"function Refii.cast_spell(caster, spell_type, targeted, target, level, pos, dir)\n" +
			"\tif Refii._cast_spell == nil or caster == nil or caster == 0 then return nil end\n" +
			"\tlocal p = pos or {}\n" +
			"\tlocal d = dir or {}\n" +
			"\tlocal t = (targeted == true or (type(targeted) == 'number' and targeted ~= 0)) and 1 or 0\n" +
			"\treturn Refii._cast_spell(caster, spell_type, t, target or 0, level or 0, pos ~= nil and 1 or 0,\n" +
			"\t\tp[1] or p.x or 0, p[2] or p.y or 0, p[3] or p.z or 0, dir ~= nil and 1 or 0, d[1] or d.x or 0, d[2] or d.y or 0, d[3] or d.z or 0)\n" +
			"end\n" +
			// mark(target, icon, text [, p]): a HUD world marker over an entity handle or at a point
			// {x, y, z} for player p (nil: every player played here); the id unmark takes, or nil.
			"function Refii.mark(target, icon, text, p)\n" +
			"\tif Refii._mark == nil or target == nil then return nil end\n" +
			"\tif type(target) == 'table' then\n" +
			"\t\treturn Refii._mark(0, target[1] or target.x or 0, target[2] or target.y or 0, target[3] or target.z or 0, icon or '', text or '', p or 0)\n" +
			"\tend\n" +
			"\tif target == 0 then return nil end\n" +
			"\treturn Refii._mark(target, 0, 0, 0, icon or '', text or '', p or 0)\n" +
			"end\n" +
			// light(target, color, energy, range [, o]): a mod light on an entity handle or at a
			// point {x, y, z}; color {r, g, b}; o = { offset = {x, y, z}, at = "helper", flicker = n,
			// direction = {x, y, z}, cone = degrees, attenuation = n } — a cone above 0 is a spot
			// along the direction. The id light_remove takes, or nil.
			"function Refii.light(target, color, energy, range, o)\n" +
			"\tif Refii._light == nil or target == nil then return nil end\n" +
			"\to = o or {}\n" +
			"\tlocal c = color or {}\n" +
			"\tlocal f = o.offset or {}\n" +
			"\tlocal d = o.direction or {}\n" +
			"\tlocal r, g, b = c[1] or c.r or 1, c[2] or c.g or 1, c[3] or c.b or 1\n" +
			"\tif type(target) == 'table' then\n" +
			"\t\treturn Refii._light(0, target[1] or target.x or 0, target[2] or target.y or 0, target[3] or target.z or 0, r, g, b,\n" +
			"\t\t\tenergy or 1, range or 0, f[1] or f.x or 0, f[2] or f.y or 0, f[3] or f.z or 0, o.at or '', o.flicker or 0,\n" +
			"\t\t\td[1] or d.x or 0, d[2] or d.y or 0, d[3] or d.z or 0, o.cone or 0, o.attenuation or 0)\n" +
			"\tend\n" +
			"\tif target == 0 then return nil end\n" +
			"\treturn Refii._light(target, 0, 0, 0, r, g, b, energy or 1, range or 0, f[1] or f.x or 0, f[2] or f.y or 0, f[3] or f.z or 0, o.at or '', o.flicker or 0,\n" +
			"\t\td[1] or d.x or 0, d[2] or d.y or 0, d[3] or d.z or 0, o.cone or 0, o.attenuation or 0)\n" +
			"end\n" +
			// message_box(title, text [, accept [, cancel [, p]]]): a question when cancel is given
			// (nil captions keep the box's own), else a notice; the id mod_message_box carries.
			"function Refii.message_box(title, text, accept, cancel, p)\n" +
			"\tif Refii._message_box == nil then return nil end\n" +
			"\treturn Refii._message_box(title or '', text or '', accept or '', cancel or '', cancel ~= nil and 1 or 0, p or 0)\n" +
			"end\n" +
			// menu_box(title, text, options [, p [, cancelable]]): up to 12 options from the table
			// (cancelable unless false); the id mod_menu_box carries.
			"function Refii.menu_box(title, text, options, p, cancelable)\n" +
			"\tif Refii._menu_box == nil or type(options) ~= 'table' then return nil end\n" +
			"\tlocal o = {}\n" +
			"\tfor i = 1, 12 do o[i] = options[i] ~= nil and tostring(options[i]) or '' end\n" +
			"\tlocal n = 0\n" +
			"\twhile n < 12 and options[n + 1] ~= nil do n = n + 1 end\n" +
			"\treturn Refii._menu_box(title or '', text or '', p or 0, cancelable == false and 0 or 1, n,\n" +
			"\t\to[1], o[2], o[3], o[4], o[5], o[6], o[7], o[8], o[9], o[10], o[11], o[12])\n" +
			"end\n");

		text.Append(RequireRuntime);
		// The values IN FORCE, the live overrides over the files': what the native table holds, so
		// a session or a reload composed after a live change starts from that change.
		text.Append("RefiiSettings = {");
		foreach ((string key, float value) in ModSettings.InForce)
		{
			text.Append("[\"").Append(key.Replace("\\", "\\\\").Replace("\"", "\\\""))
				.Append("\"]=").Append(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
		}

		text.Append("}\n");
		return text.ToString();
	}

	/// <summary>
	/// Lua that OTHER composers contribute to the init chunk, after every mod's own init.lua:
	/// the spells kind's generated handlers (Engine_ModSpells), a template's glue. Each entry is
	/// (a label for errors, the source); it is wrapped like a mod's file and runs in the game VM.
	/// Cleared and refilled on every mount by whoever contributes (register before
	/// <see cref="Apply"/> runs, i.e. during the overlay pass).
	/// </summary>
	public static readonly List<(string Label, string Source)> ExtraInitSources = new();

	/// <summary>
	/// <c>Refii._menu_row(path, arg, e)</c>, which a mod menu row's <c>Mod:lua:&lt;function&gt;[:&lt;arg&gt;]</c>
	/// calls (Engine_UiModMenuCommand.LuaCall): the function is a global or a dotted path through
	/// tables, LOOKED UP, never compiled, and run protected as <c>function(arg, e)</c>.
	/// </summary>
	public const string MenuRowRuntime =
		"function Refii._menu_row(path, arg, e)\n" +
		"\tlocal f = _G\n" +
		"\tlocal at = 1\n" +
		"\twhile f ~= nil do\n" +
		"\t\tlocal dot = string.find(path, '.', at, true)\n" +
		"\t\tlocal part = string.sub(path, at, (dot or 0) - 1)\n" +
		"\t\tif type(f) ~= 'table' then f = nil break end\n" +
		"\t\tf = f[part]\n" +
		"\t\tif dot == nil then break end\n" +
		"\t\tat = dot + 1\n" +
		"\tend\n" +
		"\tlocal where = 'menu row ' .. tostring(e.menu) .. '/' .. tostring(e.row)\n" +
		"\tif type(f) ~= 'function' then Refii._report(where, 'no Lua function ' .. tostring(path)) return end\n" +
		"\tlocal ok, err = pcall(f, arg, e)\n" +
		"\tif not ok then Refii._report(where, err) end\n" +
		"end\n";

	/// <summary>
	/// <c>Refii.set_menu_rows(menu, rows [, p])</c>: writes the rows table — strings, or
	/// <c>{ name, label, command, ghosted }</c> — as the bus text Engine_UiModRows reads
	/// (<c>name|label|command|ghosted</c> by <c>;</c>, <c>\</c> escaping) for the native
	/// <c>Refii._menu_rows</c>, which the game's Lua C API needs: it has no table reads.
	/// </summary>
	public const string MenuRowsRuntime = """
		function Refii.set_menu_rows(menu, rows, p)
			if Refii._menu_rows == nil or type(rows) ~= 'table' then return false end
			local function esc(v)
				v = string.gsub(tostring(v or ''), '\\', '\\\\')
				v = string.gsub(v, '|', '\\|')
				return (string.gsub(v, ';', '\\;'))
			end
			local text = ''
			for i = 1, #rows do
				local r = rows[i]
				local row
				if type(r) == 'table' then
					row = esc(r.name or r.label or i) .. '|' .. esc(r.label or '') .. '|' .. esc(r.command or '') .. '|' .. (r.ghosted and '1' or '0')
				else
					row = esc(r) .. '|' .. esc(r) .. '||0'
				end
				if i > 1 then text = text .. ';' end
				text = text .. row
			end
			return Refii._menu_rows(menu or '', text, p or 0)
		end

		""";

	/// <summary>
	/// A mod's Lua text as it runs in the init chunk: the body of a protected function, its error
	/// appended to <c>RefiiModErrors</c>. The Studio's live run sends this through the debug
	/// channel so a chunk run live behaves exactly as it does at session start.
	/// </summary>
	public static string Wrap(string mod, string source)
	{
		var text = new StringBuilder();
		AppendWrapped(text, mod, source);
		return text.ToString();
	}

	/// <summary>The keys the payload hook reads the composed chunks from.</summary>
	public const string InitKey = "refii/mod_init.lua";
	public const string CameraKey = "refii/mod_camera.lua";

	/// <summary>The front end's composed chunk (<see cref="FrontendPreamble"/> and every mod's
	/// <see cref="ModFrontendPath"/>), read by the payload when a presentation state lacks it.</summary>
	public const string FrontendKey = "refii/mod_frontend.lua";

	/// <summary>
	/// Every <c>.lua</c> under <c>mod/</c>: the three entry points and the modules. All are composed
	/// (never served as assets); the entry points by their role, the rest by name through <c>require</c>.
	/// </summary>
	public static bool IsModLuaPath(string path)
	{
		string normalized = path.Replace('\\', '/').TrimStart('/');
		return normalized.StartsWith(ModLuaFolder, StringComparison.OrdinalIgnoreCase) &&
			normalized.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) &&
			normalized.Length > ModLuaFolder.Length + ".lua".Length;
	}

	/// <summary>Whether a package path is one of the three entry points.</summary>
	public static bool IsEntryPath(string path)
	{
		string normalized = path.Replace('\\', '/').TrimStart('/');
		return normalized.Equals(ModInitPath, StringComparison.OrdinalIgnoreCase) ||
			normalized.Equals(ModCameraPath, StringComparison.OrdinalIgnoreCase) ||
			normalized.Equals(ModFrontendPath, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// A module's name from its package path: <c>mod/ai/wheel.lua</c> is <c>ai/wheel</c>, what
	/// <c>require("ai/wheel")</c> or <c>require("ai.wheel")</c> resolves. Lower case, as the registry
	/// keys it. False for an entry point or anything outside <c>mod/</c>.
	/// </summary>
	public static bool TryModuleName(string path, out string name)
	{
		name = string.Empty;
		if (!IsModLuaPath(path) || IsEntryPath(path))
		{
			return false;
		}

		string normalized = path.Replace('\\', '/').TrimStart('/');
		name = normalized[ModLuaFolder.Length..^".lua".Length].ToLowerInvariant();
		return name.Length != 0 && !name.Contains('"') && !name.Contains('\\');
	}

	public static bool IsFrontendKey(string path) => path.Equals(FrontendKey, StringComparison.OrdinalIgnoreCase);

	/// <summary>Composes the chunks from <paramref name="overlays"/> (mount order) into the mount.</summary>
	public static void Apply(BnkFileSystem mounted, IReadOnlyList<ModOverlay> overlays)
	{
		List<ModSources> sources = Read(overlays);
		Compose(mounted, overlays, sources, ModInitPath, InitKey);
		Compose(mounted, overlays, sources, ModCameraPath, CameraKey);
		Compose(mounted, overlays, sources, ModFrontendPath, FrontendKey);
	}

	/// <summary>One mod's Lua as the package ships it: the entry points by path, the modules by name.</summary>
	public sealed class ModSources
	{
		public string Mod { get; init; } = string.Empty;

		/// <summary>Entry point text by its package path (<see cref="ModInitPath"/> ...).</summary>
		public Dictionary<string, string> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Module text by module name, in name order.</summary>
		public SortedDictionary<string, string> Modules { get; } = new(StringComparer.Ordinal);
	}

	/// <summary>
	/// Every enabled mod's Lua files, each checked with the game's compiler: a file that does not
	/// compile is left out and named, so a syntax error cannot fail a whole chunk.
	/// </summary>
	private static List<ModSources> Read(IReadOnlyList<ModOverlay> overlays)
	{
		var sources = new List<ModSources>();
		foreach (ModOverlay overlay in overlays)
		{
			if (!F2ModPackage.TryOpen(overlay.PackagePath, out F2ModPackage? package, out _) || package is null)
			{
				continue;
			}

			var mod = new ModSources { Mod = overlay.Name };
			using (package)
			{
				foreach (F2ModEntry entry in package.Entries)
				{
					string path = entry.Path.Replace('\\', '/').TrimStart('/');
					if (path.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
					{
						path = path["files/".Length..];
					}

					if (!IsModLuaPath(path))
					{
						continue;
					}

					string source = Encoding.UTF8.GetString(package.ReadEntryBytes(entry));
					if (IsEntryPath(path))
					{
						if (Checked(overlay.Name, path, source, source))
						{
							mod.Entries[path.ToLowerInvariant()] = source;
						}
					}
					else if (TryModuleName(path, out string name) &&
						Checked(overlay.Name, path, source, WrapModule(overlay.Name, name, source)))
					{
						mod.Modules[name] = source;
					}
				}
			}

			sources.Add(mod);
		}

		return sources;
	}

	/// <summary>The compile check, on the text as it will be composed; true to include the file.</summary>
	private static bool Checked(string mod, string path, string source, string composed)
	{
		string chunkName = $"@{mod}/{path}";
		if (Refii.Lua.Engine_LuaDecompiler.TryCheckSource(composed, chunkName, out string error))
		{
			return true;
		}

		if (error.Contains("not available", StringComparison.Ordinal))
		{
			GD.PushWarning($"[mods] lua: {mod}/{path} could not be checked ({error}); included unchecked");
			return true;
		}

		GD.PushWarning($"[mods] lua: {mod}/{path} does not compile and is NOT run: {error}");
		return false;
	}

	/// <summary>
	/// THE FRONT END'S RUNTIME, ahead of every mod's mod/frontend.lua: a fresh <c>Refii</c> (so a
	/// state that runs it again — a remount — does not subscribe twice) with the game preamble's pure
	/// Lua (<c>Refii.on</c>, <c>_fire</c>, <c>_report</c>, <c>setting</c> over <c>RefiiSettings</c>,
	/// <c>_menu_row</c>, <c>message_box</c>), and the natives the payload registers in that state as
	/// globals (<c>__refii_*</c>, mod_frontend_lua.cpp), moved under <c>Refii</c>:
	/// <list type="bullet">
	/// <item><c>publish(key, n)</c>, <c>value(key)</c>: the shared value store, which every surface reads.</item>
	/// <item><c>log(text)</c>; a handler's error goes to the Mods error list as in a game.</item>
	/// <item><c>open_menu(name [, p])</c>, <c>message_box(...)</c>, <c>notice(title, text [, seconds [, p]])</c>,
	/// <c>menu_box(title, text, options [, p [, cancelable]])</c>: the host's list, dialog, notices and
	/// menu box, which the front end has (G.14, G.18 #6).</item>
	/// <item><c>menu_player()</c>.</item>
	/// </list>
	/// The events heard there are <c>mod_menu_row</c>, <c>mod_message_box</c>, <c>mod_notice</c> and
	/// <c>mod_menu_box</c> — the only ones raised without a world. Everything that needs a world, a hero, an entity or the save (health,
	/// gold, spawn, play_fx, player values, <c>RefiiModSave</c>) is absent: <c>Refii.frontend</c> is
	/// true. Lua globals live in the presentation's state, so another presentation starts fresh;
	/// published values persist.
	/// </summary>
	public static string FrontendPreamble()
	{
		var text = new StringBuilder("Refii = {}\n");
		text.Append(RuntimePreamble());
		text.Append(FrontendNatives);
		return text.ToString();
	}

	/// <summary>
	/// The front-end natives (registered as globals by mod_frontend_lua.cpp's table) under
	/// <c>Refii</c>, and the marks the payload's probe reads (<c>Refii._frontend</c>).
	/// </summary>
	public const string FrontendNatives =
		"Refii._frontend = true\n" +
		"Refii.frontend = true\n" +
		"Refii.publish = __refii_publish\n" +
		"Refii.value = __refii_value\n" +
		"Refii.log = __refii_log\n" +
		"Refii._error = __refii_error\n" +
		"Refii.open_menu = __refii_open_menu\n" +
		"Refii._message_box = __refii_message_box\n" +
		"Refii.notice = __refii_notice\n" +
		"Refii._menu_box = __refii_menu_box\n" +
		"Refii.menu_player = __refii_menu_player\n" +
		"Refii.hash = __refii_hash\n" +
		"Refii.signed_in = __refii_signed_in\n" +
		"Refii.save_count = __refii_save_count\n" +
		"Refii.screen = __refii_screen\n";

	/// <summary>
	/// A remount that changed <see cref="FrontendKey"/> while the front end is up: the current
	/// presentation's state is marked stale, so the next front-end chunk runs the new composition
	/// first. True when posted.
	/// </summary>
	public static bool RefreshFrontend(IReadOnlyList<string> changed)
	{
		if (!changed.Any(IsFrontendKey) || !Refii.Host.HostBridge.IsGuestUp || Refii.Host.HostBridge.IsInGame)
		{
			return false;
		}

		Refii.Host.HostBridge.PostModCommand(FrontendCommand + "if Refii then Refii._frontend = false end");
		return true;
	}

	/// <summary>The guest tick's command running a chunk in the front end's mod Lua (mod_commands.cpp).</summary>
	public const string FrontendCommand = "frontend_lua ";

	private static void Compose(
		BnkFileSystem mounted, IReadOnlyList<ModOverlay> overlays, IReadOnlyList<ModSources> sources,
		string modPath, string key)
	{
		var text = new StringBuilder();
		var included = new List<string>();
		foreach (ModSources mod in sources)
		{
			bool entry = mod.Entries.TryGetValue(modPath, out string? source);
			// THE MODULES RIDE THE INIT CHUNK (the game VM's registry, which the camera chunk shares)
			// AND THE FRONT-END CHUNK (its own state). A mod with modules and no entry point still
			// registers them: another entry point of the same mod may require one.
			bool modules = mod.Modules.Count != 0 && key != CameraKey;
			if (!entry && !modules)
			{
				continue;
			}

			text.Append("do\n");
			AppendRequire(text, mod.Mod);
			if (modules)
			{
				AppendModules(text, mod.Mod, mod.Modules);
			}

			if (entry)
			{
				AppendProtected(text, mod.Mod, source!);
				included.Add(mod.Mod);
			}

			text.Append("end\n");
		}

		if (key == InitKey)
		{
			foreach ((string label, string source) in ExtraInitSources)
			{
				if (!Refii.Lua.Engine_LuaDecompiler.TryCheckSource(source, "@" + label, out string extraError) &&
					!extraError.Contains("not available", StringComparison.Ordinal))
				{
					GD.PushWarning($"[mods] lua: {label} does not compile and is NOT run: {extraError}");
					continue;
				}

				AppendWrapped(text, label, source);
				included.Add(label);
			}
		}

		// THE PREAMBLES STAND WHENEVER A MOD IS ENABLED, init.lua or not: a native-only mod keeps
		// its state in RefiiModSave and subscribes nothing in Lua, and without the preamble that
		// table was never carried through the save — Spell Weaving alone lost its level and its
		// partners on every load.
		if (key == InitKey && overlays.Count != 0)
		{
			text.Insert(0, SavePreamble + RuntimePreamble());
		}
		else if (key == FrontendKey && overlays.Count != 0)
		{
			text.Insert(0, FrontendPreamble());
		}
		else if (included.Count == 0)
		{
			return;
		}

		byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
		mounted.AddVirtualFile(key, key, bytes.Length, () => bytes, string.Join(",", included));
		GD.Print($"[mods] lua: {modPath} from {(included.Count == 0 ? "the preamble only" : string.Join(", ", included))} " +
			$"composed into {key} ({bytes.Length} bytes)");
	}

	/// <summary>The mod's own <c>require</c>, a local of the block its chunks and modules close over.</summary>
	private static void AppendRequire(StringBuilder text, string mod) =>
		text.Append("local require = Refii._require(\"").Append(Escape(mod)).Append("\")\n");

	/// <summary>
	/// The mod's modules registered under a fresh scope, each as <c>function(...)</c> over its text —
	/// so a module's top-level <c>return</c> and <c>local</c>s are its own, and it runs only when
	/// required.
	/// </summary>
	private static void AppendModules(StringBuilder text, string mod, IReadOnlyDictionary<string, string> modules)
	{
		text.Append("local __scope = Refii._mod_scope(\"").Append(Escape(mod)).Append("\")\n");
		foreach ((string name, string source) in modules)
		{
			text.Append("__scope.modules[\"").Append(Escape(name)).Append("\"] = function(...)\n");
			text.Append(source);
			text.Append("\nend\n");
		}
	}

	/// <summary>
	/// One module's text as the composer registers it, standing on its own for a compile check: the
	/// scope line and the function the module becomes.
	/// </summary>
	public static string WrapModule(string mod, string name, string source)
	{
		var text = new StringBuilder();
		AppendModules(text, mod, new Dictionary<string, string> { [name] = source });
		return text.ToString();
	}

	private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

	/// <summary>
	/// One mod's text as the body of a protected function. A function, so a top-level
	/// <c>return</c> or a <c>local</c> in the mod's file stays the mod's own.
	/// </summary>
	private static void AppendWrapped(StringBuilder text, string mod, string source)
	{
		text.Append("do\n");
		AppendRequire(text, mod);
		AppendProtected(text, mod, source);
		text.Append("end\n");
	}

	/// <summary>The protected call alone, inside a block that already holds the mod's <c>require</c>.</summary>
	private static void AppendProtected(StringBuilder text, string mod, string source)
	{
		string label = Escape(mod);
		text.Append("local ok, err = pcall(function()\n");
		text.Append(source);
		text.Append("\nend) if not ok then if Refii and Refii._report then Refii._report(\"")
			.Append(label)
			.Append("\", err) else RefiiModErrors = (RefiiModErrors or \"\") .. \"")
			.Append(label)
			.Append(": \" .. tostring(err) .. \"\\n\" end end\n");
	}
}
