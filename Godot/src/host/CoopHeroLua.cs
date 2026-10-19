using System;
using System.Text;
using Godot;
using Refii.Bnk;

namespace Refii.Host;

/// <summary>
/// Refii's own Lua for Fable 3-style co-op, run in the GAME VM after the shipped scripts and before
/// any mod's (<c>refii/core_init.lua</c>, the payload's LoadStartupScripts hook). Every session
/// builds a fresh VM, so it runs once per session.
/// </summary>
/// <remarks>
/// <para>Like a mod's Lua it is OUR code patching what the shipped scripts defined, never an edited
/// copy of a shipped script.</para>
/// <para>THE HERO, PER COROUTINE. The shipped scripts take "the hero" from <c>GetPlayerHero()</c>
/// (1,281 calls in 166 files), which is always player one. A usable object's script runs in its
/// own coroutine — it yields while the user's animation plays — and the native use routine now
/// hands it the real user (coop_hero_hooks.cpp). Inside a coroutine a script started for player
/// two, <c>GetPlayerHero()</c> answers with player two; everywhere else it is unchanged. A
/// wrapper cannot use <c>pcall</c>: Lua 5.1 cannot yield across it. A coroutine that dies
/// mid-script leaves only a weak key behind.</para>
/// <para>CHESTS: <c>OnActionUseTakeItems:Update</c> gives the chest to the hero whenever player two
/// is alive ("entity_receiving_items = hero"); run as the user, it gives it to whoever opened
/// it.</para>
/// </remarks>
public static class CoopHeroLua
{
	public const string Key = "refii/core_init.lua";

	private const string Source = """
		-- Refii core: co-op heroes. Patches the shipped scripts' tables; nothing replaces a file.
		RefiiCoop = RefiiCoop or {}
		local C = RefiiCoop

		-- The hero a script acts for, by the coroutine it runs in (weak keys: a dead coroutine
		-- takes its entry with it).
		C.ActingHero = C.ActingHero or setmetatable({}, { __mode = "k" })

		if C.ShippedGetPlayerHero == nil and GetPlayerHero ~= nil then
			C.ShippedGetPlayerHero = GetPlayerHero
			GetPlayerHero = function(...)
				local co = coroutine.running()
				local acting = co and C.ActingHero[co]
				if acting ~= nil then
					return acting
				end
				return C.ShippedGetPlayerHero(...)
			end
		end

		-- Runs fn as `entity` when entity is player two and fn is running in a coroutine; plain
		-- otherwise. No pcall: the wrapped scripts yield.
		function C.RunAs(entity, fn, ...)
			local co = coroutine.running()
			if co == nil or entity == nil or GetPlayerHenchman == nil or entity ~= GetPlayerHenchman() then
				return fn(...)
			end
			C.ActingHero[co] = entity
			local results = { fn(...) }
			C.ActingHero[co] = nil
			return unpack(results)
		end

		-- Wraps Table.Method so it runs as the entity the script's own `field` names.
		function C.RunMethodAsUser(table_, method, field)
			if type(table_) ~= "table" or type(table_[method]) ~= "function" then
				return
			end
			local shipped_key = "RefiiShipped" .. method
			if table_[shipped_key] == nil then
				table_[shipped_key] = table_[method]
			end
			local shipped = table_[shipped_key]
			table_[method] = function(self, ...)
				return C.RunAs(self and self[field], shipped, self, ...)
			end
		end

		C.RunMethodAsUser(OnActionUseTakeItems, "Update", "EntityUsingItem")

		-- RINGS AND GIFTS: OnCarriedActionUseRing:Update takes the target and the giver from
		-- GetPlayerHero(); run as the user, player two gives their own gift to their own target.
		C.RunMethodAsUser(OnCarriedActionUseRing, "Update", "EntityUsingItem")

		-- QUEST GIVERS: either player. The shipped prompts show a quest's box when
		-- `ScriptFunction.GetPlayersTarget() == giver and Targeted.IsHeroWithinInteractionDistance(giver)`,
		-- both player one only (QuestManager.HeroEntity). The game's own two-player rule is
		-- QuestEntityThreadBase:ShowAcceptanceBoxWithHelp's: player one targeting the giver from
		-- interaction range, or player two (alive) doing the same. GetPlayersTarget answers with the
		-- target of the player who is engaging — player one's when they are in range of it, else
		-- player two's — and the distance test accepts player two targeting the giver from range.
		local function HenchmanTargeting(entity)
			local henchman = GetPlayerHenchman and GetPlayerHenchman()
			if henchman == nil or not henchman:IsAlive() then
				return false
			end
			local target = Targeting.GetTarget(henchman)
			if target == nil or (entity ~= nil and target ~= entity) then
				return false
			end
			return Targeted.IsHenchmanWithinInteractionDistance(target) == true
		end

		if Targeted ~= nil and Targeted.IsHeroWithinInteractionDistance ~= C.IsHeroWithinInteractionDistance then
			local shipped = Targeted.IsHeroWithinInteractionDistance
			C.ShippedIsHeroWithinInteractionDistance = shipped
			C.IsHeroWithinInteractionDistance = function(entity, ...)
				if shipped(entity, ...) then
					return true
				end
				return HenchmanTargeting(entity)
			end
			Targeted.IsHeroWithinInteractionDistance = C.IsHeroWithinInteractionDistance
		end

		if ScriptFunction ~= nil and ScriptFunction.GetPlayersTarget ~= C.GetPlayersTarget then
			local shipped = ScriptFunction.GetPlayersTarget
			C.GetPlayersTarget = function(...)
				local target = shipped(...)
				if target ~= nil and target:IsAlive() and C.ShippedIsHeroWithinInteractionDistance ~= nil
					and C.ShippedIsHeroWithinInteractionDistance(target) then
					return target
				end
				local henchman = GetPlayerHenchman and GetPlayerHenchman()
				if HenchmanTargeting(nil) then
					return Targeting.GetTarget(henchman)
				end
				return target
			end
			ScriptFunction.GetPlayersTarget = C.GetPlayersTarget
		end

		-- SHOPS RUN BY A BEHAVIOUR (the barman, the town crier): their Update says
		-- TEXT_AI_HENCHMAN_REJECT when `self.InteractEntity == GetPlayerHenchman()`, then opens the
		-- shop for the LOCAL player (GUI.DisplayShopScreen, the same buyer rule as the native shop).
		-- So player two is served exactly where player two is this machine's local hero.
		C.HiddenHenchman = C.HiddenHenchman or setmetatable({}, { __mode = "k" })
		if C.ShippedGetPlayerHenchman == nil and GetPlayerHenchman ~= nil then
			C.ShippedGetPlayerHenchman = GetPlayerHenchman
			GetPlayerHenchman = function(...)
				local co = coroutine.running()
				if co ~= nil and C.HiddenHenchman[co] then
					return nil
				end
				return C.ShippedGetPlayerHenchman(...)
			end
		end

		local function PlayerTwoIsLocal(entity)
			if entity == nil or Refii == nil or Refii.handle == nil or Refii.local_hero == nil then
				return false
			end
			local henchman = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			return henchman ~= nil and entity == henchman and Refii.handle(entity) == Refii.local_hero()
		end

		local function ServePlayerTwo(table_, method)
			if type(table_) ~= "table" or type(table_[method]) ~= "function" then
				return
			end
			local shipped_key = "RefiiShipped" .. method
			if table_[shipped_key] == nil then
				table_[shipped_key] = table_[method]
			end
			local shipped = table_[shipped_key]
			table_[method] = function(self, ...)
				local co = coroutine.running()
				if co == nil or not PlayerTwoIsLocal(self and self.InteractEntity) then
					return shipped(self, ...)
				end
				C.HiddenHenchman[co] = true
				local results = { shipped(self, ...) }
				C.HiddenHenchman[co] = nil
				return unpack(results)
			end
		end

		ServePlayerTwo(BehaviourBarmanOpenShopUI, "Update")
		ServePlayerTwo(BehaviourTownCrierOpenGUI, "Update")
		""";

	/// <summary>
	/// The camera half, run after the camera scripts each time they run (a session's first level,
	/// a save load, a co-op join: each rebuilds SACCamera and CameraFunctions, so every wrap is
	/// re-applied when the table no longer holds it).
	/// </summary>
	/// <remarks>
	/// <para>EACH PLAYER THEIR OWN CAMERA, ONLINE. Shipped, the joiner's camera ran no Lua and
	/// followed the host's (net message 0xBD); now each machine runs its own (coop_hero_hooks.cpp)
	/// and frames only its own hero: <c>CameraFunctions.IsMultiplayer</c> — "henchman alive", the
	/// test behind the two-player cage (SACCamera:CalculatePrimaryAndSecondary) and the
	/// multiplayer camera set (CameraManager.CameraSetList, which captured the function, so its
	/// entry is repointed) — answers false in an online game.</para>
	/// <para>FREE ROAM. The leash is the movement filter sub_821A0298, skipped while the game's own
	/// <c>Debug.SetMultiPlayerHeroFree</c> byte is set (0x821a0334); the camera re-imposes its
	/// distance every frame in <c>SACCamera:SetMaxCoopDistance</c>. That method now states the
	/// switch every frame: free (and no distance) where players roam freely — online — else the
	/// shipped leash, which a shared screen needs. Local co-op on split screen counts as "own
	/// cameras" too: player two's manager runs the same scripts with player two as its player.</para>
	/// </remarks>
	public const string CameraKey = "refii/core_camera.lua";

	private const string CameraSource = """
		-- The payload also runs this right after the startup scripts (the disc build loads its
		-- camera files there); on the Title Update the camera scripts have not run yet.
		if SACCamera == nil or CameraFunctions == nil then
			return
		end

		RefiiCoop = RefiiCoop or {}
		local C = RefiiCoop

		-- Every player follows their own hero: an online game, or local co-op on split screen
		-- (player two's camera manager is built by coop_hero_hooks.cpp; Refii.split_screen() is
		-- the host's coop_split_screen switch).
		-- Online is "a partner and not local co-op" (game+284 1 vs 2, `sub_82340300`) on BOTH machines:
		-- the shipped IsMultiplayerGame (`sub_8245B410`) answers only on the session HOST
		-- (CM+52 && CM+53), so a joiner read it false and framed both heroes in one camera.
		function C.OwnCameras()
			if Multiplayer ~= nil and Multiplayer.IsLocalCoopGame ~= nil and Multiplayer.IsLocalCoopGame() == true then
				return Refii ~= nil and Refii.split_screen ~= nil and Refii.split_screen() == true
			end
			local partner = GetPlayerHenchman ~= nil and GetPlayerHenchman() or nil
			return partner ~= nil and partner:IsAlive() == true
		end

		function C.FreeRoam()
			return C.OwnCameras()
		end

		if CameraFunctions.IsMultiplayer ~= C.IsMultiplayer then
			local shipped = CameraFunctions.IsMultiplayer
			C.IsMultiplayer = function(...)
				if C.OwnCameras() then
					return false
				end
				return shipped(...)
			end
			CameraFunctions.IsMultiplayer = C.IsMultiplayer
			if CameraManager ~= nil and type(CameraManager.CameraSetList) == "table" then
				for _, set in ipairs(CameraManager.CameraSetList) do
					if type(set) == "table" and set.IsValid == shipped then
						set.IsValid = C.IsMultiplayer
					end
				end
			end
		end

		-- EACH CAMERA MANAGER ITS OWN CURRENT CAMERA. Every native manager binds the ONE global
		-- `CameraManager` table as its self (`sub_82481328`: the global named at dword_83498450),
		-- and FrameUpdate activates a camera only when the best one differs from
		-- self.CurrentCamera. A second manager — an online joiner's own, local player two's on
		-- split screen — was therefore told "already active" and never activated: no mode
		-- wrapper, so `sub_82265F90` wrote the default pose (origin, 70 degrees). The current
		-- set, camera and priority are kept per hero here, keyed as the shipped script keys its
		-- per-player overrides (GetIDFromEntity).
		if CameraManager ~= nil and CameraManager.FrameUpdate ~= nil and CameraManager.FrameUpdate ~= C.FrameUpdate then
			local shipped = CameraManager.FrameUpdate
			C.CameraState = C.CameraState or {}
			C.FrameUpdate = function(self, hero, ...)
				if hero == nil or not C.OwnCameras() then
					return shipped(self, hero, ...)
				end
				local key = GetIDFromEntity(hero)
				if key == nil then
					return shipped(self, hero, ...)
				end
				local state = C.CameraState[key]
				if state == nil then
					state = {}
					C.CameraState[key] = state
				end
				local set, camera, priority = self.CurrentSet, self.CurrentCamera, self.CurrentSetPriority
				self.CurrentSet, self.CurrentCamera, self.CurrentSetPriority = state.Set, state.Camera, state.Priority
				local result = shipped(self, hero, ...)
				state.Set, state.Camera, state.Priority = self.CurrentSet, self.CurrentCamera, self.CurrentSetPriority
				self.CurrentSet, self.CurrentCamera, self.CurrentSetPriority = set, camera, priority
				return result
			end
			CameraManager.FrameUpdate = C.FrameUpdate
		end

		-- EACH PLAYER TURNS THEIR OWN CAMERA. SACCamera:ProcessGameAction rotates only while the
		-- global GetCanRotateCamera() (the network manager's +222, `sub_8233C280`) is true. Fable 2
		-- clears it for co-op because one camera framed both players: the host's join handler
		-- (`stb r22,0xDE(r31)` at 0x8232BD98) and the local shared-screen toggle (0x8233C350).
		if GetCanRotateCamera ~= nil and GetCanRotateCamera ~= C.GetCanRotateCamera then
			local shipped = GetCanRotateCamera
			C.GetCanRotateCamera = function(...)
				if C.OwnCameras() then
					return true
				end
				return shipped(...)
			end
			GetCanRotateCamera = C.GetCanRotateCamera
		end

		if SACCamera.SetMaxCoopDistance ~= C.SetMaxCoopDistance then
			local shipped = SACCamera.SetMaxCoopDistance
			C.SetMaxCoopDistance = function(self, ...)
				local free = C.FreeRoam()
				if Debug ~= nil and Debug.SetMultiPlayerHeroFree ~= nil then
					Debug.SetMultiPlayerHeroFree(free)
				end
				if free then
					return
				end
				return shipped(self, ...)
			end
			SACCamera.SetMaxCoopDistance = C.SetMaxCoopDistance
		end
		""";

	/// <summary>Mounts both chunks the payload runs. Called on every mount.</summary>
	public static void Attach(BnkFileSystem mounted)
	{
		Mount(mounted, Key, Source);
		Mount(mounted, CameraKey, CameraSource);
	}

	private static void Mount(BnkFileSystem mounted, string key, string source)
	{
		if (Refii.Lua.Engine_LuaDecompiler.Available &&
			!Refii.Lua.Engine_LuaDecompiler.TryCheckSource(source, "@" + key, out string error))
		{
			GD.PushError($"[coop] {key} does not compile and is NOT run: {error}");
			return;
		}

		byte[] bytes = Encoding.UTF8.GetBytes(source);
		mounted.AddVirtualFile(key, key, bytes.Length, () => bytes, "Refii");
	}
}
