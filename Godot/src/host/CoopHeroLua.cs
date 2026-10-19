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

		-- SAVED COROUTINES CARRY OUR FUNCTIONS. A save persists every running quest and script
		-- thread with Pluto, and a thread parked under one of our wrappers holds that wrapper on its
		-- stack. Only permanents are written by name (_G, the class tables, the shipped functions:
		-- saveloadsystem.lua, postscriptsloaded.lua); anything else is written by value, upvalues
		-- and all — RefiiCoop and the C natives it holds. So every function of ours that can sit
		-- under a yield is registered as a permanent under a fixed name, through the game's own
		-- AddToPermanentsTables, which also maps the name back when a save is loaded.
		function C.Permanent(fn, name)
			if AddToPermanentsTables ~= nil then
				AddToPermanentsTables(fn, "Refii." .. name)
			end
			return fn
		end

		-- The hero a script acts for, by the coroutine it runs in (weak keys: a dead coroutine
		-- takes its entry with it). A function there is a resolver, asked each time (a demon
		-- door's nearer hero).
		C.ActingHero = C.ActingHero or setmetatable({}, { __mode = "k" })

		-- Outside any coroutine (a behaviour's PrepareToRun, a plain call) the acting hero is one
		-- value: the main thread cannot yield, so it is set and restored exactly around the call.
		function C.Acting()
			local co = coroutine.running()
			local acting = co ~= nil and C.ActingHero[co] or (co == nil and C.MainActing or nil)
			if type(acting) == "function" then
				return acting()
			end
			return acting
		end

		if C.ShippedGetPlayerHero == nil and GetPlayerHero ~= nil then
			C.ShippedGetPlayerHero = GetPlayerHero
			GetPlayerHero = C.Permanent(function(...)
				local acting = C.Acting()
				if acting ~= nil then
					return acting
				end
				return C.ShippedGetPlayerHero(...)
			end, "GetPlayerHero")
		end

		-- Runs fn as `entity` when entity is player two: GetPlayerHero() answers player two and
		-- GetPlayerHenchman() player one — each the other's partner, as Fable 3's two heroes are.
		-- In a coroutine there is no pcall (the wrapped scripts yield); on the main thread
		-- nothing can yield, so the call is protected and the value always restored.
		function C.RunAs(entity, fn, ...)
			local partner = C.ShippedGetPlayerHenchman or GetPlayerHenchman
			if entity == nil or partner == nil or entity ~= partner() then
				return fn(...)
			end
			local co = coroutine.running()
			if co == nil then
				local before = C.MainActing
				C.MainActing = entity
				local results = { pcall(fn, ...) }
				C.MainActing = before
				if not results[1] then
					error(results[2], 0)
				end
				return unpack(results, 2)
			end
			local before = C.ActingHero[co]
			C.ActingHero[co] = entity
			local results = { fn(...) }
			C.ActingHero[co] = before
			return unpack(results)
		end
		C.Permanent(C.RunAs, "RunAs")

		-- Runs fn with the acting hero resolved on every ask: player two while player two is
		-- nearer `entity` than player one, else as shipped. A coroutine only (a quest thread).
		function C.NearerPartner(entity)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			local hero = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
			if entity == nil or not entity:IsAlive() or partner == nil or not partner:IsAlive() then
				return nil
			end
			if hero == nil or not hero:IsAlive() then
				return partner
			end
			return GetDistanceBetweenEntities(partner, entity) < GetDistanceBetweenEntities(hero, entity) and partner or nil
		end

		function C.RunNear(entity, fn, ...)
			local co = coroutine.running()
			if co == nil or entity == nil then
				return fn(...)
			end
			local before = C.ActingHero[co]
			C.ActingHero[co] = function() return C.NearerPartner(entity) end
			local results = { fn(...) }
			C.ActingHero[co] = before
			return unpack(results)
		end
		C.Permanent(C.RunNear, "RunNear")

		-- Wraps Class.Method (a class by its global name) so it runs as the entity the script's
		-- own `field` names. The wrapper is a permanent named after the method.
		function C.RunMethodAsUser(name, method, field, class)
			local table_ = class or _G[name]
			if type(table_) ~= "table" or type(table_[method]) ~= "function" then
				return
			end
			local shipped_key = "RefiiShipped" .. method
			if rawget(table_, shipped_key) == nil then
				rawset(table_, shipped_key, table_[method])
			end
			local shipped = rawget(table_, shipped_key)
			table_[method] = C.Permanent(function(self, ...)
				return C.RunAs(self and self[field], shipped, self, ...)
			end, "User." .. name .. "." .. method)
		end

		C.RunMethodAsUser("OnActionUseTakeItems", "Update", "EntityUsingItem")

		-- RINGS AND GIFTS: OnCarriedActionUseRing:Update takes the target and the giver from
		-- GetPlayerHero(); run as the user, player two gives their own gift to their own target.
		C.RunMethodAsUser("OnCarriedActionUseRing", "Update", "EntityUsingItem")

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
			C.IsHeroWithinInteractionDistance = C.Permanent(function(entity, ...)
				if shipped(entity, ...) then
					return true
				end
				return HenchmanTargeting(entity)
			end, "IsHeroWithinInteractionDistance")
			Targeted.IsHeroWithinInteractionDistance = C.IsHeroWithinInteractionDistance
		end

		if ScriptFunction ~= nil and ScriptFunction.GetPlayersTarget ~= C.GetPlayersTarget then
			local shipped = ScriptFunction.GetPlayersTarget
			C.GetPlayersTarget = C.Permanent(function(...)
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
			end, "GetPlayersTarget")
			ScriptFunction.GetPlayersTarget = C.GetPlayersTarget
		end

		-- SHOPS RUN BY A BEHAVIOUR (the barman, the town crier): their Update says
		-- TEXT_AI_HENCHMAN_REJECT when `self.InteractEntity == GetPlayerHenchman()`, then opens the
		-- shop for the LOCAL player (GUI.DisplayShopScreen, the same buyer rule as the native shop).
		-- So player two is served exactly where player two is this machine's local hero.
		C.HiddenHenchman = C.HiddenHenchman or setmetatable({}, { __mode = "k" })
		if C.ShippedGetPlayerHenchman == nil and GetPlayerHenchman ~= nil then
			C.ShippedGetPlayerHenchman = GetPlayerHenchman
			GetPlayerHenchman = C.Permanent(function(...)
				local co = coroutine.running()
				if co ~= nil and C.HiddenHenchman[co] then
					return nil
				end
				if C.Acting() ~= nil then
					return C.ShippedGetPlayerHero()
				end
				return C.ShippedGetPlayerHenchman(...)
			end, "GetPlayerHenchman")
		end

		-- QuestManager.HeroEntity, "the hero" in 8,660 places, is a plain field (set at
		-- gameflow.lua:2027 and QuestManager:LoadFromSave), so the per-coroutine GetPlayerHero()
		-- never reached it. It is served per acting player: the stored value moves to
		-- RefiiHeroEntity, and reading HeroEntity answers the acting player first. QuestManager is
		-- reached only through _G, a Pluto permanent (ctypesserialisefunctions.lua), and nothing
		-- stores the table itself, so the metatable never enters a save; the save table copies
		-- the field (QuestManager.GetSaveTable), which outside any acting player is player one.
		if QuestManager ~= nil and getmetatable(QuestManager) == nil and not rawget(QuestManager, "RefiiHeroServed") then
			rawset(QuestManager, "RefiiHeroEntity", rawget(QuestManager, "HeroEntity"))
			rawset(QuestManager, "HeroEntity", nil)
			rawset(QuestManager, "RefiiHeroServed", true)
			setmetatable(QuestManager, {
				__index = function(t, key)
					if key == "HeroEntity" then
						local acting = C.Acting()
						if acting ~= nil then
							return acting
						end
						return rawget(t, "RefiiHeroEntity")
					end
					return nil
				end,
				__newindex = function(t, key, value)
					if key == "HeroEntity" then
						rawset(t, "RefiiHeroEntity", value)
					else
						rawset(t, key, value)
					end
				end,
			})
		end

		local function PlayerTwoIsLocal(entity)
			if entity == nil or Refii == nil or Refii.handle == nil or Refii.local_hero == nil then
				return false
			end
			local henchman = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			return henchman ~= nil and entity == henchman and Refii.handle(entity) == Refii.local_hero()
		end

		local function ServePlayerTwo(name, method)
			local table_ = _G[name]
			if type(table_) ~= "table" or type(table_[method]) ~= "function" then
				return
			end
			local shipped_key = "RefiiShipped" .. method
			if rawget(table_, shipped_key) == nil then
				rawset(table_, shipped_key, table_[method])
			end
			local shipped = rawget(table_, shipped_key)
			table_[method] = C.Permanent(function(self, ...)
				local co = coroutine.running()
				if co == nil or not PlayerTwoIsLocal(self and self.InteractEntity) then
					return shipped(self, ...)
				end
				C.HiddenHenchman[co] = true
				local results = { shipped(self, ...) }
				C.HiddenHenchman[co] = nil
				return unpack(results)
			end, "Serve." .. name .. "." .. method)
		end

		ServePlayerTwo("BehaviourBarmanOpenShopUI", "Update")
		ServePlayerTwo("BehaviourTownCrierOpenGUI", "Update")

		-- DOGS, ONE PER PLAYER. Shipped, GetDog() is the one global "the dog" slot (hero one's);
		-- player two's dog is the co-op one (coop_dog_hooks.cpp), Refii.player_dog(2). Each dog
		-- serves Dog.GetMaster(dog), so a behaviour that names GetDog() or GetPlayerHero() where
		-- it means "this dog" or "this dog's master" is restated for the dog it runs on.
		function C.PartnerDog()
			local handle = Refii ~= nil and Refii.player_dog ~= nil and Refii.player_dog(2) or nil
			return handle ~= nil and Refii.entity(handle) or nil
		end

		-- ITEMS USED ON "THE DOG" (treat, collar, breed potions and books, the dig's happy dog)
		-- act on the user's own dog. Only GetDog() is redirected: GetPlayerHero() is left alone.
		C.DogUser = C.DogUser or setmetatable({}, { __mode = "k" })
		if C.ShippedGetDog == nil and GetDog ~= nil then
			C.ShippedGetDog = GetDog
			GetDog = C.Permanent(function(...)
				local co = coroutine.running()
				if co ~= nil and C.DogUser[co] then
					return C.PartnerDog()
				end
				return C.ShippedGetDog(...)
			end, "GetDog")
		end

		local function UsesOwnDog(name, method)
			local table_ = _G[name]
			if type(table_) ~= "table" or type(table_[method]) ~= "function" then
				return
			end
			local shipped_key = "RefiiShippedDog" .. method
			if rawget(table_, shipped_key) == nil then
				rawset(table_, shipped_key, table_[method])
			end
			local shipped = rawget(table_, shipped_key)
			table_[method] = C.Permanent(function(self, ...)
				local co = coroutine.running()
				local henchman = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
				if co == nil or henchman == nil or self == nil or self.EntityUsingItem ~= henchman then
					return shipped(self, ...)
				end
				C.DogUser[co] = true
				local results = { shipped(self, ...) }
				C.DogUser[co] = nil
				return unpack(results)
			end, "OwnDog." .. name .. "." .. method)
		end

		UsesOwnDog("OnCarriedActionUseDogTreat", "Update")
		UsesOwnDog("OnCarriedActionUseDogAccessory", "Update")
		UsesOwnDog("OnCarriedActionUseBonusEffects", "Update")
		UsesOwnDog("OnCarriedActionUseDig", "Update")

		-- A BREED CHANGE OF PLAYER TWO'S DOG makes the new dog player two's, not the quest dog:
		-- ApplyDogChange creates it (CreateEntityFromIDAtPosition, named "CreatureDogHero"),
		-- destroys the old one and stores it in QuestManager.DogEntity. Player two's dog is the
		-- one named RefiiPartnerDog on both machines (the name rides the create message), so the
		-- replacement is created under that name, the record is cleared for its first bind to
		-- take, and QuestManager keeps hero one's. ApplyDogChange never yields, so pcall is safe.
		if OnCarriedActionUseBase ~= nil and OnCarriedActionUseBase.ApplyDogChange ~= nil
			and OnCarriedActionUseBase.ApplyDogChange ~= C.ApplyDogChange then
			local shipped = OnCarriedActionUseBase.ApplyDogChange
			C.ApplyDogChange = C.Permanent(function(self, dog, breed, ...)
				local partner = C.PartnerDog()
				if partner == nil or dog ~= partner or Refii._set_partner_dog == nil then
					return shipped(self, dog, breed, ...)
				end
				local quest_dog = QuestManager and QuestManager.DogEntity
				local create = CreateEntityFromIDAtPosition
				CreateEntityFromIDAtPosition = function(id, name, ...)
					return create(id, "RefiiPartnerDog", ...)
				end
				Refii._set_partner_dog()
				local ok, failure = pcall(shipped, self, dog, breed, ...)
				CreateEntityFromIDAtPosition = create
				if QuestManager ~= nil then
					QuestManager.DogEntity = quest_dog
				end
				if not ok then
					error(failure, 0)
				end
			end, "ApplyDogChange")
			OnCarriedActionUseBase.ApplyDogChange = C.ApplyDogChange
		end

		-- THE DOG'S OWN NEEDS. BehaviourDogVariables.Reload (run by every dog's init) sets the
		-- desire rates on GetDog() only; player two's dog gets the same rates.
		if BehaviourDogVariables ~= nil and BehaviourDogVariables.Reload ~= nil
			and BehaviourDogVariables.Reload ~= C.ReloadDogVariables then
			local shipped = BehaviourDogVariables.Reload
			C.ReloadDogVariables = C.Permanent(function(...)
				local result = shipped(...)
				local dog = C.PartnerDog()
				local v = BehaviourDogVariables
				if dog ~= nil and dog:IsAlive() then
					Dog.SetDesireChangePerSecond(dog, EDogDesireType.DOG_DESIRE_TYPE_SNIFF, 1 / v.Desire_TimeToFillMeterInSeconds_Sniff)
					Dog.SetDesireChangePerSecond(dog, EDogDesireType.DOG_DESIRE_TYPE_BARK, 1 / v.Desire_TimeToFillMeterInSeconds_Bark)
					Dog.SetDesireChangePerSecond(dog, EDogDesireType.DOG_DESIRE_TYPE_PEE, 1 / v.Desire_TimeToFillMeterInSeconds_Pee)
					Dog.SetDesireChangePerSecond(dog, EDogDesireType.DOG_DESIRE_TYPE_VILLAGER_INTERACTION, 1 / v.Desire_TimeToFillMeterInSeconds_VillagerInteraction)
					Dog.SetDesireChangePerSecond(dog, EDogDesireType.DOG_DESIRE_TYPE_SLEEP, 1 / v.Desire_TimeToFillMeterInSeconds_Sleep)
				end
				return result
			end, "ReloadDogVariables")
			BehaviourDogVariables.Reload = C.ReloadDogVariables
		end

		-- INJURED is this dog's injury (shipped: GetDog()'s).
		if BehaviourDogInjured ~= nil and BehaviourDogInjured.IsStillRunnable ~= C.DogInjuredRunnable then
			C.DogInjuredRunnable = C.Permanent(function(self)
				return BehaviourDogTrackHeroBase.IsStillRunnable(self) and Dog.IsInjured(self.Entity) and true or false
			end, "DogInjuredRunnable")
			BehaviourDogInjured.IsStillRunnable = C.DogInjuredRunnable
			C.DogInjuredSlowExit = C.Permanent(function(self, mode)
				BehaviourDogTrackHeroBase.SlowExit(self, mode)
				if not Dog.IsInjured(self.Entity) and not AIManager:IsModeMaintained(mode, AIManager.Modes.DOG_INJURED) then
					self:PerformActionEx({ Type = EScriptableAction.DOG_ANIMATION, Anim = "Healed", OverrideLooking = true,
						Priority = EActionPriority.PRIORITY_COMBAT_RESPONSE, BlendInTime = 0.5 })
				end
			end, "DogInjuredSlowExit")
			BehaviourDogInjured.SlowExit = C.DogInjuredSlowExit
		end

		-- TRICKS answer the dog's own master. Shipped, hero one's dog also answered player two's
		-- expressions (the GetPlayerHenchman() branch); player two's dog answers player two.
		if BehaviourDogPerformTrick ~= nil and BehaviourDogPerformTrick.IsStillRunnable ~= C.DogTrickRunnable then
			C.DogTrickRunnable = C.Permanent(function(self)
				if Dog.GetMoodType(self.Entity) == EDogMoodType.DOG_MOOD_TYPE_SCARED then
					return false
				end
				return ExpressionPerformer.IsPerformingExpression(Dog.GetMaster(self.Entity)) or Dog.IsDogCommandAvailable(self.Entity)
			end, "DogTrickRunnable")
			BehaviourDogPerformTrick.IsStillRunnable = C.DogTrickRunnable
			C.DogTrickPrepare = C.Permanent(function(self)
				if not self:IsStillRunnable() then
					return false
				end
				local master = Dog.GetMaster(self.Entity)
				self.Target = Targeting.GetTarget(master)
				if not self.Target:IsAlive() or self.Target == self.Entity
					or (self.Target:IsAlive() and not OpinionReaction.IsAvailable(self.Target)) then
					self.Target = master
				end
				return true
			end, "DogTrickPrepare")
			BehaviourDogPerformTrick.PrepareToRun = C.DogTrickPrepare
		end

		-- ITEMS AND PLACES PLAYER TWO USES AS THEMSELVES. Each script reads "the hero" from
		-- GetPlayerHero(); run as the user, it is the user's.
		--   beds (onactionusebed.lua:39 refused player two outright; sleep, sex, rent, trespass);
		--   potions, food and books (bonus effects; books apply them too);
		--   property: the sale sign's rules, knocking on an owned door, a bedroll's trespass;
		--   the DLC travel items' return point; the photo; the costume suits (QuestManager.HeroEntity
		--   throughout, served per acting player above).
		for _, use in ipairs({ "OnActionUseBed", "OnCarriedActionUseBonusEffects", "OnCarriedActionUseBook",
			"OnActionUseSaleSign", "OnActionUseKnockDoor", "OnCarriedActionUseBedRoll", "OnCarriedActionUseSkull",
			"OnCarriedActionUseSnowglobe", "OnCarriedActionUseSpireStatue", "OnCarriedActionUseArenaMiniature",
			"OnCarriedActionUsePhoto", "OnCarriedActionUseHobbeSuit", "OnCarriedActionUseBalverineSuit",
			"OnCarriedActionUseHollowmanSuit" }) do
			C.RunMethodAsUser(use, "Update", "EntityUsingItem")
		end
		UsesOwnDog("OnCarriedActionUseBook", "Update")

		-- A WILL POTION'S MORPH: ApplyBonusEffects sets the will stat with Debug.SetHeroWill(v),
		-- which has no entity and is hero one's. For player two it is player two's: the same
		-- value through Stats.ModifyWill on them.
		if Debug ~= nil and Debug.SetHeroWill ~= nil and Debug.SetHeroWill ~= C.SetHeroWill then
			local shipped = Debug.SetHeroWill
			C.SetHeroWill = C.Permanent(function(value, ...)
				local acting = C.Acting()
				if acting == nil or Stats == nil or Stats.ModifyWill == nil then
					return shipped(value, ...)
				end
				return Stats.ModifyWill(acting, value - Stats.GetWill(acting))
			end, "SetHeroWill")
			Debug.SetHeroWill = C.SetHeroWill
		end

		-- PROPERTY: the real-estate screen resolves its buyer natively, as hero one; opened from a
		-- sale sign player two used (OnActionUseSaleSign runs as its user, above), it buys for
		-- player two (coop_ui_hooks.cpp, RefiiCoopEstateBuyer).
		if GUI ~= nil and GUI.DisplayRealEstateScreen ~= nil and GUI.DisplayRealEstateScreen ~= C.DisplayRealEstateScreen then
			local shipped = GUI.DisplayRealEstateScreen
			C.DisplayRealEstateScreen = C.Permanent(function(...)
				if Refii ~= nil and Refii._estate_buyer ~= nil then
					Refii._estate_buyer(C.Acting() ~= nil and 2 or 1)
				end
				return shipped(...)
			end, "DisplayRealEstateScreen")
			GUI.DisplayRealEstateScreen = C.DisplayRealEstateScreen
		end

		-- GUARDS fine and arrest the criminal they chase; shipped, the fine and the sleep tidy-up
		-- went to GetPlayerHero() whoever the criminal was.
		if BehaviourGuardChaseCriminal ~= nil then
			for _, method in ipairs({ "PrepareToRun", "Update", "TidyUpJustInCaseHeroWasSleeping", "SlowExit", "FastExit" }) do
				C.RunMethodAsUser("BehaviourGuardChaseCriminal", method, "Criminal")
			end
		end

		-- A SPOUSE'S HERO is the one they married: every BehaviourSpouse* method says
		-- GetPlayerHero() for "my husband/wife". Married to player two, it runs as player two.
		C.SpouseOf = function(villager)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if villager ~= nil and partner ~= nil and partner:IsAlive() and PlayerFamily ~= nil
				and PlayerFamily.IsMarriedTo(partner, villager) then
				return partner
			end
			return nil
		end
		for _, name in ipairs({ "BehaviourSpouseComment", "BehaviourSpouseMissedHero", "BehaviourSpousePostChildBirth",
			"BehaviourSpouseHoneymoonPeriod", "BehaviourSpouseJealous", "BehaviourSpouseLeaveHero",
			"BehaviourSpouseCatchBigamist" }) do
			local class = _G[name]
			if type(class) == "table" and not rawget(class, "RefiiSpouseWrapped") then
				rawset(class, "RefiiSpouseWrapped", true)
				local methods = {}
				for key, value in pairs(class) do
					if type(value) == "function" and key ~= "CreateSubClass" then
						methods[key] = value
					end
				end
				for key, shipped in pairs(methods) do
					class[key] = C.Permanent(function(self, ...)
						return C.RunAs(C.SpouseOf(self and self.Entity), shipped, self, ...)
					end, "Spouse." .. name .. "." .. key)
				end
			end
		end

		-- QUEST PROGRESS IS THE WORLD'S. Code acting for player two still records quests on player
		-- one's tracker (quests belong to the host's world, as in Fable 3): a QuestTracker call
		-- whose hero argument is the acting partner is made for the stored hero instead.
		if type(QuestTracker) == "table" and not rawget(QuestTracker, "RefiiWorldTracker") then
			rawset(QuestTracker, "RefiiWorldTracker", true)
			local shipped = {}
			for key, value in pairs(QuestTracker) do
				if type(value) == "function" then
					shipped[key] = value
				end
			end
			for key, fn in pairs(shipped) do
				QuestTracker[key] = C.Permanent(function(hero, ...)
					local acting = C.Acting()
					if acting ~= nil and hero == acting and QuestManager ~= nil then
						hero = rawget(QuestManager, "RefiiHeroEntity") or (C.ShippedGetPlayerHero and C.ShippedGetPlayerHero())
					end
					return fn(hero, ...)
				end, "QuestTracker." .. key)
			end
		end

		-- QUESTS LOAD ON DEMAND (QuestManager:LoadQuestModule → require), after this chunk runs,
		-- so their classes are patched as each module loads (and those already loaded, now).
		-- A class wrapper is a permanent (see C.Permanent); the module table is one already
		-- (QuestManager:AddQuestToPermanentsTables).
		function C.RunMethodNear(class, method, field, name)
			if type(class) ~= "table" or type(class[method]) ~= "function" then
				return
			end
			local key = "RefiiShippedNear" .. method
			if rawget(class, key) == nil then
				rawset(class, key, class[method])
			end
			local shipped = rawget(class, key)
			class[method] = C.Permanent(function(self, ...)
				return C.RunNear(self and self[field], shipped, self, ...)
			end, "Near." .. name .. "." .. method)
		end

		-- JOBS FOR EITHER PLAYER (the woodsman, blacksmith, bartender and bard signs). While a sign
		-- waits, the worker is player two when player two alone targets it; once the job starts
		-- (CurrentState leaves 0) the worker is whoever it was then. Inside the sign's thread
		-- "the hero" is that worker: the gate, the pay (Money.Modify(QuestManager.HeroEntity)),
		-- the camera (SetCameraOverride is per hero), the stats. The partner is neither ghosted
		-- nor hidden (Fable 3 has no henchman to put away).
		C.JobThread = C.JobThread or setmetatable({}, { __mode = "k" })
		function C.JobWorker(sign)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if sign == nil or partner == nil or not partner:IsAlive() then
				return nil
			end
			if sign.CurrentState == 0 then
				local hero = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
				local theirs = Targeting.GetTarget(partner) == sign.Entity
				local mine = hero ~= nil and Targeting.GetTarget(hero) == sign.Entity
				sign.RefiiWorker = theirs and not mine and partner or nil
				-- The job this sign ran is over: its meter goes back to player one's pad.
				if C.ActiveJobSign == sign then
					C.ActiveJobSign = nil
					if Refii ~= nil and Refii._job_worker ~= nil then
						Refii._job_worker(0)
					end
				end
			elseif Refii ~= nil and Refii._job_worker ~= nil then
				-- The running job's meter takes the worker's pad (coop_ui_hooks.cpp).
				C.ActiveJobSign = sign
				Refii._job_worker(sign.RefiiWorker ~= nil and 2 or 1)
			end
			return sign.RefiiWorker
		end

		function C.RunJob(sign, fn, ...)
			local co = coroutine.running()
			if co == nil or sign == nil then
				return fn(...)
			end
			local before = C.ActingHero[co]
			C.ActingHero[co] = function() return C.JobWorker(sign) end
			C.JobThread[co] = true
			local results = { fn(...) }
			C.JobThread[co] = nil
			C.ActingHero[co] = before
			return unpack(results)
		end
		C.Permanent(C.RunJob, "RunJob")

		if Multiplayer ~= nil and not rawget(Multiplayer, "RefiiJobGhost") then
			rawset(Multiplayer, "RefiiJobGhost", true)
			for _, key in ipairs({ "SetHenchmanGhostMode", "SetHenchmanHiddenMode" }) do
				local shipped = Multiplayer[key]
				if type(shipped) == "function" then
					Multiplayer[key] = C.Permanent(function(...)
						local co = coroutine.running()
						if co ~= nil and C.JobThread[co] then
							return
						end
						return shipped(...)
					end, "Multiplayer." .. key)
				end
			end
		end

		-- DEMON DOORS answer whichever hero stands nearer: each door judges "the hero" — purity,
		-- items, expressions, followers, distance (qo900..qo990) — off QuestManager.HeroEntity,
		-- which inside a door's thread is player two while player two is the nearer.
		function C.PatchQuestModule(module)
			if type(module) ~= "table" or rawget(module, "RefiiPatched") then
				return
			end
			rawset(module, "RefiiPatched", true)
			for key, class in pairs(module) do
				if type(key) == "string" and type(class) == "table" then
					if key:match("^QO9%d%d_DemonDoor$") or key:match("^QO9%d%d_DemonDoorMouthTrigger$") then
						C.RunMethodNear(class, "CustomUpdate", "Entity", key)
					elseif key:match("^QO9%d%d_DemonDoor%w+$") then
						C.RunMethodNear(class, "Update", "Door", key)
					elseif key:match("^QJ00%d_Job%w+Sign$") and type(class.CustomUpdate) == "function"
						and rawget(class, "RefiiShippedJobCustomUpdate") == nil then
						local shipped = class.CustomUpdate
						rawset(class, "RefiiShippedJobCustomUpdate", shipped)
						class.CustomUpdate = C.Permanent(function(self, ...)
							return C.RunJob(self, shipped, self, ...)
						end, "Job." .. key .. ".CustomUpdate")
					end
				end
			end
		end

		if QuestManager ~= nil and QuestManager.LoadQuestModule ~= nil and QuestManager.LoadQuestModule ~= C.LoadQuestModule then
			local shipped = QuestManager.LoadQuestModule
			C.LoadQuestModule = C.Permanent(function(name, ...)
				local module = shipped(name, ...)
				C.PatchQuestModule(module)
				return module
			end, "LoadQuestModule")
			QuestManager.LoadQuestModule = C.LoadQuestModule
			for name in pairs(QuestManager.LoadedQuestModules or {}) do
				C.PatchQuestModule(_G[name] or (package ~= nil and package.loaded[name]) or nil)
			end
		end

		-- THE HENCHMAN TUTORIALS are retired: the teleport hint (free roam replaces the leash) and
		-- the multiplayer introduction (it explains the henchman and the share sliders). The
		-- orb, join, camera-snap and joint-expression tutorials stay.
		if TutorialManager ~= nil and TutorialManager.CompleteTutorial ~= nil and ETutorialType ~= nil then
			TutorialManager.CompleteTutorial(ETutorialType.TUTORIAL_MULTIPLAYER_TELEPORT)
			TutorialManager.CompleteTutorial(ETutorialType.TUTORIAL_MULTIPLAYER)
		end
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

		-- The core chunk's permanent registration, in case this runs first (the disc build).
		C.Permanent = C.Permanent or function(fn, name)
			if AddToPermanentsTables ~= nil then
				AddToPermanentsTables(fn, "Refii." .. name)
			end
			return fn
		end

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
			C.IsMultiplayer = C.Permanent(function(...)
				if C.OwnCameras() then
					return false
				end
				return shipped(...)
			end, "IsMultiplayer")
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
			C.FrameUpdate = C.Permanent(function(self, hero, ...)
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
			end, "FrameUpdate")
			CameraManager.FrameUpdate = C.FrameUpdate
		end

		-- EACH PLAYER TURNS THEIR OWN CAMERA. SACCamera:ProcessGameAction rotates only while the
		-- global GetCanRotateCamera() (the network manager's +222, `sub_8233C280`) is true. Fable 2
		-- clears it for co-op because one camera framed both players: the host's join handler
		-- (`stb r22,0xDE(r31)` at 0x8232BD98) and the local shared-screen toggle (0x8233C350).
		if GetCanRotateCamera ~= nil and GetCanRotateCamera ~= C.GetCanRotateCamera then
			local shipped = GetCanRotateCamera
			C.GetCanRotateCamera = C.Permanent(function(...)
				if C.OwnCameras() then
					return true
				end
				return shipped(...)
			end, "GetCanRotateCamera")
			GetCanRotateCamera = C.GetCanRotateCamera
		end

		if SACCamera.SetMaxCoopDistance ~= C.SetMaxCoopDistance then
			local shipped = SACCamera.SetMaxCoopDistance
			C.SetMaxCoopDistance = C.Permanent(function(self, ...)
				local free = C.FreeRoam()
				if Debug ~= nil and Debug.SetMultiPlayerHeroFree ~= nil then
					Debug.SetMultiPlayerHeroFree(free)
				end
				if free then
					return
				end
				return shipped(self, ...)
			end, "SetMaxCoopDistance")
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
