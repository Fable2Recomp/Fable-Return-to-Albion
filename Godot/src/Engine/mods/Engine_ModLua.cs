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
/// </remarks>
public static class ModLua
{
	public const string ModInitPath = "mod/init.lua";
	public const string ModCameraPath = "mod/camera.lua";

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
		// THE SUBSCRIPTION MASKS ARE GROUPS OF 20 BITS: this Lua's number is a 32-bit float, exact
		// only to 24 bits, so a 31-bit sum loses its low bits once a high one is set. Group g holds
		// kinds 20*(g-1) .. 20*g-1; the payload reads each (mod_events.cpp ReadLuaMask).
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
			"\tlocal group = math.floor(kind / 20) + 1\n" +
			"\tlocal bit = 2 ^ (kind % 20)\n" +
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
			"end\n");

		text.Append("RefiiSettings = {");
		foreach ((string key, float value) in ModSettings.All)
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

	public static bool IsModLuaPath(string path)
	{
		string normalized = path.Replace('\\', '/').TrimStart('/');
		return normalized.Equals(ModInitPath, StringComparison.OrdinalIgnoreCase) ||
			normalized.Equals(ModCameraPath, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Composes both chunks from <paramref name="overlays"/> (mount order) into the mount.</summary>
	public static void Apply(BnkFileSystem mounted, IReadOnlyList<ModOverlay> overlays)
	{
		Compose(mounted, overlays, ModInitPath, InitKey);
		Compose(mounted, overlays, ModCameraPath, CameraKey);
	}

	private static void Compose(
		BnkFileSystem mounted, IReadOnlyList<ModOverlay> overlays, string modPath, string key)
	{
		var text = new StringBuilder();
		var included = new List<string>();
		foreach (ModOverlay overlay in overlays)
		{
			if (!F2ModPackage.TryOpen(overlay.PackagePath, out F2ModPackage? package, out _) || package is null)
			{
				continue;
			}

			string source;
			using (package)
			{
				if (!package.TryGetEntry(modPath, out F2ModEntry? entry) || entry is null)
				{
					continue;
				}

				source = Encoding.UTF8.GetString(package.ReadEntryBytes(entry));
			}

			string chunkName = $"@{overlay.Name}/{modPath}";
			if (!Refii.Lua.Engine_LuaDecompiler.TryCheckSource(source, chunkName, out string error))
			{
				if (error.Contains("not available", StringComparison.Ordinal))
				{
					GD.PushWarning($"[mods] lua: {overlay.Name}/{modPath} could not be checked ({error}); " +
						"included unchecked");
				}
				else
				{
					GD.PushWarning($"[mods] lua: {overlay.Name}/{modPath} does not compile and is NOT run: {error}");
					continue;
				}
			}

			AppendWrapped(text, overlay.Name, source);
			included.Add(overlay.Name);
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
		else if (included.Count == 0)
		{
			return;
		}

		byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
		mounted.AddVirtualFile(key, key, bytes.Length, () => bytes, string.Join(",", included));
		GD.Print($"[mods] lua: {modPath} from {(included.Count == 0 ? "the preamble only" : string.Join(", ", included))} " +
			$"composed into {key} ({bytes.Length} bytes)");
	}

	/// <summary>
	/// One mod's text as the body of a protected function. A function, so a top-level
	/// <c>return</c> or a <c>local</c> in the mod's file stays the mod's own.
	/// </summary>
	private static void AppendWrapped(StringBuilder text, string mod, string source)
	{
		string label = mod.Replace("\\", "\\\\").Replace("\"", "\\\"");
		text.Append("do local ok, err = pcall(function()\n");
		text.Append(source);
		text.Append("\nend) if not ok then if Refii and Refii._report then Refii._report(\"")
			.Append(label)
			.Append("\", err) else RefiiModErrors = (RefiiModErrors or \"\") .. \"")
			.Append(label)
			.Append(": \" .. tostring(err) .. \"\\n\" end end end\n");
	}
}
