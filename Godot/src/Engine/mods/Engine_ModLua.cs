using System;
using System.Collections.Generic;
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
		text.Append("Refii._mask = Refii._mask or 0\n");
		text.Append("Refii._mask2 = Refii._mask2 or 0\n");
		text.Append("Refii._dirty = false\n");
		text.Append("Refii._hero = Refii._hero or {}\n");
		text.Append("Refii._kinds = {");
		foreach (ModApiEvent e in ModApi.Events)
		{
			text.Append(e.LuaName).Append('=').Append(e.Kind).Append(',');
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
			"\tif kind < 31 then\n" +
			"\t\tlocal bit = 2 ^ kind\n" +
			"\t\tif math.floor(Refii._mask / bit) % 2 == 0 then Refii._mask = Refii._mask + bit end\n" +
			"\telse\n" +
			"\t\tlocal bit = 2 ^ (kind - 31)\n" +
			"\t\tif math.floor(Refii._mask2 / bit) % 2 == 0 then Refii._mask2 = Refii._mask2 + bit end\n" +
			"\tend\n" +
			"\tRefii._dirty = true\n" +
			"end\n" +
			"function Refii._fire(kind, e)\n" +
			"\tlocal list = Refii._handlers[kind]\n" +
			"\tif list == nil then return true end\n" +
			"\tlocal keep = true\n" +
			"\tfor i = 1, #list do\n" +
			"\t\tlocal ok, cancel = pcall(list[i], e)\n" +
			"\t\tif not ok then RefiiModErrors = (RefiiModErrors or '') .. 'event ' .. tostring(kind) .. ': ' .. tostring(cancel) .. '\\n'\n" +
			"\t\telseif cancel == true then keep = false end\n" +
			"\tend\n" +
			"\treturn keep\n" +
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
		text.Append("\nend) if not ok then RefiiModErrors = (RefiiModErrors or \"\") .. \"")
			.Append(label)
			.Append(": \" .. tostring(err) .. \"\\n\" end end\n");
	}
}
