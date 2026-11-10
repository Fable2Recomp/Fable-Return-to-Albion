using System.Text;
using Godot;
using Refii.Bnk;

namespace Refii.Titles.Fable2;

using Refii.Save;

/// <summary>
/// Keeps a save's Lua section whole: written without a crash, loaded without a lost manager.
/// </summary>
/// <remarks>
/// <para>THE CONTRACT. The save's Lua section is the table <c>GetSaveTable</c> returns, written by
/// Pluto (TU1 <c>sub_8245C7B8</c>, the last section <c>lhSaveManager_WriteSave</c> 0x82445DB8 writes, in
/// place). A value in <c>PlutoPermanentsSaveTable</c> is written by NAME, the FNV hash of its path
/// (<c>saveloadsystem.lua</c> <c>AddToPermanentsTables</c>); anything else by value, with its upvalues,
/// environment and metatable. A C function cannot be written by value: Pluto raises (TU1
/// <c>pluto_persistfunction</c> 0x82BFA4D0), the persist runs under no pcall, and the game VM's panic
/// (TU1 0x824517D8, logged by <c>rex_lhLua_GamePanic</c>) is followed by <c>exit(1)</c>: the file is
/// left without its Lua section. The game names its own functions once, after its startup scripts
/// (<c>postscriptsloaded.lua</c>: <c>Fable2Scripts</c> as "Lua_F2S", <c>BaseObjects</c> as "Lua_BO");
/// ours come later and are named by <see cref="TailSource"/>.</para>
/// <para>THE STUB. A save writes every running coroutine, and a frame's function goes out by name.
/// On load <c>pluto_unpersist</c> (TU1 0x82BFBF90, type 0x65) looks the hash up in
/// <c>PlutoPermanentsLoadTable</c> with <c>lua_gettable</c> and registers whatever comes back, nil
/// included; <c>pluto_unpersistthread</c> (0x82BFBA60) then rebuilds the frame's pc from that
/// function's code and reads through the nil (0x82BFBC10). A console title update's WatchDogs in a
/// save brought over from the Xbox 360 are the case seen (2026-10-24, beta 6). The lookup honours
/// <c>__index</c>, so an unknown hash resolves to a stub: a LUA function whose body is
/// <see cref="StubMoves"/> MOVE no-ops and a return, so a frame resumed at any saved pc runs off the
/// end, returns nothing, and its coroutine ends. Only a permanent's key is stubbed: <c>FNVHash</c>
/// pushes a light userdata (TU1 <c>LuaAPI_FNVHash</c> 0x82465680 tags the value 2, 0x824656E4). A
/// save whose Lua section is missing unpersists nothing and <c>LoadScriptSave</c> is handed the
/// permanents table itself, reading <c>QuestSave</c>, <c>AISave</c>, ... by NAME: those read nil.</para>
/// <para><see cref="Source"/> is appended to the core chunk (<c>LoadStartupScripts</c> runs
/// <c>InitialisePermanentsLoadTable</c> first, so the load table exists there exactly when a save is
/// being loaded); <see cref="TailSource"/> is the chunk the host runs after every mod's
/// (<c>mod_lua_hooks.cpp</c> <c>kCoreTailKey</c>).</para>
/// </remarks>
public static class Engine_SaveCompatLua
{
	/// <summary>The stub's no-ops. A saved pc past this many instructions would read off the end
	/// of its code; the game's own coroutine bodies are hundreds of instructions at most.</summary>
	public const int StubMoves = 8192;

	/// <summary>The chunk the host runs after the core and mod chunks.</summary>
	public const string TailKey = "refii/core_tail.lua";

	public static readonly string Source = Build();

	/// <summary>
	/// Appends <see cref="Source"/> to the title's core chunk <paramref name="coreChunk"/> (Fable 2's is
	/// `refii/core_init.lua`), which must already be in the mount, and mounts <see cref="TailSource"/>.
	/// </summary>
	public static void Attach(BnkFileSystem mounted, string coreChunk)
	{
		foreach ((string name, string text) in new[] { ("@refii/save-compat", Source), ("@refii/core_tail", TailSource) })
		{
			if (Refii.Lua.Engine_LuaDecompiler.Available &&
				!Refii.Lua.Engine_LuaDecompiler.TryCheckSource(text, name, out string error))
			{
				GD.PushError($"[save-compat] {name} does not compile and is NOT run: {error}");
				return;
			}
		}

		string previous = mounted.TryGetFile(coreChunk, out var core)
			? Encoding.UTF8.GetString(core.ReadAllBytes())
			: string.Empty;
		byte[] bytes = Encoding.UTF8.GetBytes(previous + "\n" + Source);
		mounted.AddVirtualFile(coreChunk, coreChunk, bytes.Length, () => bytes, "Refii");
		byte[] tail = Encoding.UTF8.GetBytes(TailSource);
		mounted.AddVirtualFile(TailKey, TailKey, tail.Length, () => tail, "Refii");
	}

	/// <summary>
	/// The core level of the save chain, marked: <c>RefiiSaveCore.get</c>/<c>.load</c> are the game's own
	/// <c>GetSaveTable</c>/<c>LoadScriptSave</c> with the core's wrappers only, and <c>.reached</c> says a
	/// load got that far. Every wrapper installed after the core chunk (Decorate, the mods) sits
	/// outside it; <see cref="TailSource"/> falls back to it when one of them throws.
	/// </summary>
	private const string CoreMarker = """
		if GetSaveTable ~= nil and LoadScriptSave ~= nil and RefiiSaveCore == nil then
			RefiiSaveCore = { get = GetSaveTable }
			local core_load = LoadScriptSave
			RefiiSaveCore.load = function(loaded_table)
				RefiiSaveCore.reached = true
				return core_load(loaded_table)
			end
			LoadScriptSave = RefiiSaveCore.load
		end

		""";

	/// <summary>
	/// THE SAVE CHAIN'S OUTERMOST LEVEL, run after every mod.
	/// <list type="bullet">
	/// <item>NAMES FOR OUR FUNCTIONS. Every function under <c>Fable2Scripts</c>/<c>BaseObjects</c> that is
	/// not yet a permanent — a wrapper the core, Decorate or a mod put on a shipped table, a mod's own
	/// global — is named "RefiiPatch" + its path, the way <c>postscriptsloaded.lua</c> names the game's
	/// (keys in sorted order, so the same scripts give the same names). A mod wrapper the game keeps by
	/// value (<c>combatinfo.lua</c> stores <c>Func = CombatStates[name]</c>) is then written by name and
	/// loads as the running scripts' function, not a frozen copy of the saving session's. On a load,
	/// each path's current function also answers that path's name, so a save made with a mod that is
	/// gone loads the game's own function there. Once, at startup, like the game's: a closure made
	/// later carries per-object state and stays written by value.</item>
	/// <item>GetSaveTable. A wrapper that throws (it runs under <c>lua_cpcall</c>, TU1
	/// <c>sub_82458420</c>) gave a save with no Lua section; the table comes from the core level instead,
	/// then from the game's own function, and the failure is logged. Before it is written, the table is
	/// walked the way Pluto walks it, and any C function that is not a permanent is named
	/// "RefiiCFn" + its path: written by name, it loads as the stub, where by value it ended the
	/// game part-way through the write.</item>
	/// <item>LoadScriptSave. A wrapper that throws skipped <c>SetQuestUpdateFunction</c>,
	/// <c>SetGeneralScriptManager</c>, <c>SetAIManager</c>, <c>SetCombatRegister</c> and
	/// <c>SetXPBonusTable</c> and every NPC stood still. The chain is protected; on a failure the core
	/// level runs if it was not reached, and the five setters and the two globals the game's own
	/// function ends with are applied whatever happened (each setter rebinds its refs: TU1
	/// <c>Lua_Global_SetCombatRegister</c> 0x82460050).</item>
	/// <item>A SAVE WITHOUT AI DATA STILL GETS ITS AI. A brain registers itself with <c>AIManager</c> once,
	/// from its component slot 12 (TU1 <c>CECAIBrain_vf12</c> 0x8288F918, the <c>AddEntity</c> ref at
	/// script-system +176), and a combat component with <c>CombatRegister</c> (TU1 <c>CECCombat_vf12</c>
	/// 0x8277A430 → <c>sub_8277C068</c>, the <c>RegisterCombatant</c> ref at +232); each sets its flag
	/// whether or not the ref was set. On a load the refs are zeroed first (<c>sub_8245D498</c>), so
	/// both calls are no-ops by design and the save's <c>AISave</c> restores <c>AIManager.Entities</c> and
	/// <c>AISave.CombatReg</c> (<c>aimanager.lua</c> 251/258). A save without them (its Lua section lost)
	/// left the AI loop empty, and an NPC added back without a <c>CombatInfo</c> throws in
	/// <c>FindBestBehaviourAndGroup</c> and stands brainless; a save taken then wrote the same
	/// emptiness. A load restoring NO entity, or NO combatant, is that state (a real one holds every NPC
	/// of the region), so each creature carrying the component is registered as the game's own
	/// <c>Lua_Debug_ReloadAI</c> (0x82459BF0) does it — combatants with is_reload true (0x82459f10), no
	/// weapons made — and only where the shared world runs, the guard both share
	/// (<c>*(game+140)</c> +52/+54; <c>Refii.world_here</c>). GROUP MINDS go the same way: a group-mind
	/// component restored from a save skips registering its minds (TU1 <c>CECGroupMind_vf11</c>
	/// 0x82896500 tests +0x1C, which <c>CECGroupMind_vf8</c> sets for a restore at 0x828964f4) and
	/// <c>AISave.GroupMindMan</c> replaces the manager. Without it no guard has a
	/// <c>GuardGroupMind</c>, <c>GuardBehaviourGroup</c> (patrol, stand guard, chat) cannot run, and the
	/// guard stands in its last-resort <c>CutsceneSpeakerIdleBehaviourGroup</c>; monks and quest NPCs the
	/// same. A load with no group mind attached to an entity (a real one holds every village's, home's
	/// and guard post's) has every component register its minds (<c>Refii._restore_group_minds</c>, the
	/// step <c>Lua_Debug_ReloadAI</c> runs for component 42).</item>
	/// </list>
	/// </summary>
	public const string TailSource = """
		-- Refii save integrity (Engine_SaveCompatLua.cs): the outermost GetSaveTable / LoadScriptSave.
		if RefiiSaveCore ~= nil and not RefiiSaveCore.guarded then
			local core = RefiiSaveCore
			core.guarded = true

			local function log(text)
				if Refii ~= nil and Refii.log ~= nil then
					Refii.log("[save-compat] " .. text)
				end
			end

			local function loading()
				return PlutoPermanentsLoadTable ~= nil and IsLoadedFromSaveGame ~= nil and IsLoadedFromSaveGame()
			end

			local function permanent(value, name)
				local hash = FNVHash(name)
				PlutoPermanentsSaveTable[value] = hash
				if loading() and rawget(PlutoPermanentsLoadTable, hash) == nil then
					rawset(PlutoPermanentsLoadTable, hash, value)
				end
			end

			local function name_functions()
				local save = PlutoPermanentsSaveTable
				if save == nil or FNVHash == nil then
					return 0, 0
				end
				local alias = loading()
				local seen, named, aliased = {}, 0, 0
				local function visit(t, path)
					seen[t] = true
					local keys = {}
					for k, v in pairs(t) do
						local kind = type(v)
						if type(k) == "string" and (kind == "function" or (kind == "table" and not seen[v])) then
							keys[#keys + 1] = k
						end
					end
					table.sort(keys)
					for _, k in ipairs(keys) do
						local v = rawget(t, k)
						local name = path .. "." .. k
						if type(v) == "function" then
							if save[v] == nil then
								permanent(v, "RefiiPatch" .. name)
								named = named + 1
							elseif alias then
								local hash = FNVHash("RefiiPatch" .. name)
								if rawget(PlutoPermanentsLoadTable, hash) == nil then
									rawset(PlutoPermanentsLoadTable, hash, v)
									aliased = aliased + 1
								end
							end
						elseif type(v) == "table" and not seen[v] then
							visit(v, name)
						end
					end
				end
				if type(Fable2Scripts) == "table" then
					visit(Fable2Scripts, "")
				end
				if type(BaseObjects) == "table" then
					visit(BaseObjects, ".BO")
				end
				return named, aliased
			end

			local ok, named, aliased = pcall(name_functions)
			if ok then
				log(named .. " of our functions named for the save" .. (aliased > 0 and (", " .. aliased .. " names answered for a load") or ""))
			else
				log("naming our functions for the save failed: " .. tostring(named))
			end

			local shipped_get
			if PlutoPermanentsSaveTable ~= nil and FNVHash ~= nil then
				local get_hash = FNVHash("Lua_F2S.GetSaveTable")
				for value, hash in pairs(PlutoPermanentsSaveTable) do
					if hash == get_hash then
						shipped_get = value
					end
				end
			end

			-- Walks the table to be written as Pluto does; a C function that is not a permanent is named.
			local function name_c_functions(root)
				local save = PlutoPermanentsSaveTable
				if save == nil or FNVHash == nil or debug == nil or debug.getinfo == nil then
					return 0
				end
				local seen, values, parents, keys, count, fixed = {}, { root }, { 0 }, { "save" }, 1, 0
				local function path_of(i)
					local parts = {}
					while i > 0 do
						table.insert(parts, 1, keys[i])
						i = parents[i]
					end
					return table.concat(parts, ".")
				end
				local function push(value, parent, key)
					local kind = type(value)
					if (kind == "table" or kind == "function" or kind == "thread" or kind == "userdata")
						and not seen[value] and save[value] == nil then
						count = count + 1
						values[count], parents[count], keys[count] = value, parent, key
					end
				end
				local top = 1
				while top <= count do
					local i = top
					top = top + 1
					local v = values[i]
					values[i] = false
					if not seen[v] and save[v] == nil then
						seen[v] = true
						local kind = type(v)
						if kind == "table" then
							for a, b in next, v do
								push(a, i, "{}")
								push(b, i, tostring(a))
							end
							push(debug.getmetatable(v), i, "<mt>")
						elseif kind == "userdata" then
							push(debug.getmetatable(v), i, "<mt>")
						elseif kind == "function" then
							local info = debug.getinfo(v, "S")
							if info ~= nil and info.what == "C" then
								local path = path_of(i)
								permanent(v, "RefiiCFn." .. path)
								fixed = fixed + 1
								if fixed <= 8 then
									log("a C function in the save at " .. path .. " was not a permanent; it is written by name")
								end
							else
								push(getfenv(v), i, "<env>")
								local u = 1
								while true do
									local name, value = debug.getupvalue(v, u)
									if name == nil then
										break
									end
									push(value, i, "^" .. name)
									u = u + 1
								end
							end
						elseif kind == "thread" then
							local level = 0
							while true do
								local info = debug.getinfo(v, level, "f")
								if info == nil then
									break
								end
								push(info.func, i, "@" .. level)
								local l = 1
								while true do
									local name, value = debug.getlocal(v, level, l)
									if name == nil then
										break
									end
									push(value, i, "@" .. level .. "/" .. name)
									l = l + 1
								end
								level = level + 1
							end
						end
					end
				end
				return fixed
			end

			local outer_get = GetSaveTable
			GetSaveTable = function(...)
				local ok, t = pcall(outer_get, ...)
				if not ok or type(t) ~= "table" then
					log("GetSaveTable failed (" .. tostring(t) .. "); the save is the game's own table without the add-ons'")
					ok, t = pcall(core.get, ...)
					if (not ok or type(t) ~= "table") and shipped_get ~= nil then
						log("the core GetSaveTable failed too (" .. tostring(t) .. "); the shipped one is used")
						ok, t = pcall(shipped_get, ...)
					end
					if not ok then
						error(t, 0)
					end
				end
				local checked, fixed = pcall(name_c_functions, t)
				if not checked then
					log("checking the save before it is written failed: " .. tostring(fixed))
				elseif fixed > 8 then
					log(fixed .. " C functions in the save were named")
				end
				return t
			end

			local function register_missing(what, ec, registered, register)
				local ok, err = pcall(function()
					local found = SearchTools.GetSearchResults(SearchTools.FilterWithEC(SearchTools.StartNewSearch("creature"), ec))
					local added = 0
					for _, entity in pairs(found) do
						if registered[GetIDFromEntity(entity)] == nil then
							register(entity)
							added = added + 1
						end
					end
					log("the save loaded no " .. what .. "; " .. added .. " of " .. #found .. " registered")
				end)
				if not ok then
					log("registering " .. what .. " failed: " .. tostring(err))
				end
			end

			local function bind(name, fn, value)
				if fn == nil then
					return
				end
				local ok, err = pcall(fn, value)
				if not ok then
					log(name .. " failed: " .. tostring(err))
				end
			end

			local outer_load = LoadScriptSave
			LoadScriptSave = function(loaded_table)
				core.reached = false
				local ok, err = pcall(outer_load, loaded_table)
				if not ok then
					log("LoadScriptSave failed: " .. tostring(err))
					if not core.reached then
						local core_ok, core_err = pcall(core.load, loaded_table)
						if not core_ok then
							log("the core LoadScriptSave failed too: " .. tostring(core_err))
						end
					end
					bind("SetQuestUpdateFunction", SetQuestUpdateFunction, QuestManager ~= nil and QuestManager.Update or nil)
					bind("SetGeneralScriptManager", SetGeneralScriptManager, GeneralScriptManager)
					bind("SetAIManager", SetAIManager, AIManager)
					bind("SetCombatRegister", SetCombatRegister, CombatRegister)
					bind("SetXPBonusTable", SetXPBonusTable, XPBonusBalance)
					ReadOnlyMetatablesActive = true
					PlutoPermanentsLoadTable = nil
				end
				local world_here = Refii ~= nil and Refii.world_here or nil
				if world_here ~= nil and not world_here() then
					return
				end
				if CombatRegister ~= nil and CombatRegister.PotentialCombatants ~= nil and Combat ~= nil
					and next(CombatRegister.PotentialCombatants) == nil then
					register_missing("combatants", Combat.GetECType(), CombatRegister.PotentialCombatants,
						function(entity) CombatRegister:RegisterCombatant(entity, true) end)
				end
				if AIManager ~= nil and AIManager.Entities ~= nil and Brain ~= nil and next(AIManager.Entities) == nil then
					register_missing("AI entities", Brain.GetECType(), AIManager.Entities,
						function(entity) AIManager:AddEntity(entity) end)
				end
				if GroupMindManager ~= nil and type(GroupMindManager.GroupMinds) == "table"
					and Refii ~= nil and Refii._restore_group_minds ~= nil then
					local attached = 0
					for _, minds in pairs(GroupMindManager.GroupMinds) do
						for _, mind in pairs(minds) do
							if mind.EntityAttachedTo ~= nil then
								attached = attached + 1
							end
						end
					end
					if attached == 0 then
						local restored_ok, restored = pcall(Refii._restore_group_minds)
						log("the save loaded no group minds; " .. tostring(restored) .. " group-mind components registered theirs"
							.. (restored_ok and "" or " (failed)"))
					end
				end
			end

			if PlutoPermanentsSaveTable ~= nil and FNVHash ~= nil then
				permanent(GetSaveTable, "RefiiSaveGuard.GetSaveTable")
				permanent(LoadScriptSave, "RefiiSaveGuard.LoadScriptSave")
			end
		end
		""";

	private static string Build()
	{
		var text = new StringBuilder(StubMoves * 6 + 1024);
		text.Append("-- Refii save compatibility: a permanent the save names and these scripts do not define\n");
		text.Append("-- (Engine_SaveCompatLua.cs). Unpersisting it as nil faults in pluto_unpersistthread; it\n");
		text.Append("-- resolves to RefiiSaveStub instead, whose frame runs off its no-ops and ends the thread.\n");
		text.Append("if PlutoPermanentsLoadTable ~= nil and getmetatable(PlutoPermanentsLoadTable) == nil then\n");
		text.Append("\tlocal function RefiiSaveStub()\n");
		text.Append("\t\tlocal a, b\n");
		for (int pair = 0; pair < StubMoves / 2; pair++)
		{
			text.Append(pair % 16 == 0 ? "\t\t" : " ");
			text.Append("a = b b = a");
			if (pair % 16 == 15)
			{
				text.Append('\n');
			}
		}

		text.Append("\n\tend\n");
		text.Append("\tlocal seen = {}\n");
		text.Append("\tsetmetatable(PlutoPermanentsLoadTable, { __index = function(_, key)\n");
		text.Append("\t\tif type(key) ~= \"userdata\" then\n");
		text.Append("\t\t\treturn nil\n");
		text.Append("\t\tend\n");
		text.Append("\t\tif not seen[key] then\n");
		text.Append("\t\t\tseen[key] = true\n");
		text.Append("\t\t\tif Refii ~= nil and Refii.log ~= nil then\n");
		text.Append("\t\t\t\tRefii.log(\"[save-compat] permanent \" .. (tostring(key):gsub(\"^userdata: \", \"\"))\n");
		text.Append("\t\t\t\t\t.. \" is not defined by these scripts; its saved function is stubbed and its thread ends\")\n");
		text.Append("\t\t\tend\n");
		text.Append("\t\tend\n");
		text.Append("\t\treturn RefiiSaveStub\n");
		text.Append("\tend })\n");
		text.Append("end\n");
		text.Append(CoreMarker);
		return text.ToString();
	}
}
