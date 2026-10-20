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
		-- takes its entry with it). Outside any coroutine (a behaviour's PrepareToRun, a plain
		-- call) it is C.MainActing: the main thread cannot yield, so it is set and restored
		-- exactly around the call, and a coroutine resumed inside that call is inside it too.
		-- Last comes C.AIActing, the hero the AI is running for (below).
		--
		-- AN ENTRY is the hero itself, C.PinOne (player one, stated: the AI's nearer hero does not
		-- override it), or a resolver record { RefiiResolve = <permanent function>, RefiiArg = v }
		-- asked on every read (a demon door's nearer hero, a cutscene's held hero). An entry is
		-- never a fresh closure: a saved coroutine parked under a wrapper holds the entry on its
		-- stack, and only permanents and plain data persist by reference-free value.
		-- C.Acting() resolves it to player two or nil (player one) — never player one itself.
		C.ActingHero = C.ActingHero or setmetatable({}, { __mode = "k" })
		C.PinOne = C.PinOne or C.Permanent(function() return nil end, "PinOne")

		function C.Entry()
			local co = coroutine.running()
			local entry = co ~= nil and C.ActingHero[co] or nil
			if entry == nil then
				entry = C.MainActing
			end
			if entry == nil then
				entry = C.AIActing
			end
			return entry
		end

		function C.Resolve(entry)
			if entry == nil then
				return nil
			end
			local hero = entry
			local kind = type(entry)
			if kind == "function" then
				hero = entry()
			elseif kind == "table" and rawget(entry, "RefiiResolve") ~= nil then
				hero = entry.RefiiResolve(entry.RefiiArg)
			end
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if hero ~= nil and two ~= nil and hero == two then
				return two
			end
			return nil
		end

		function C.Acting()
			return C.Resolve(C.Entry())
		end

		-- An acting scope that names player one: GetPlayerHero() is player one by statement, not
		-- by default (the opinion natives then skip their own nearer-hero choice).
		function C.IsPinnedOne()
			local entry = C.Entry()
			return entry ~= nil and C.Resolve(entry) == nil
		end

		function C.IsHero(entity)
			if entity == nil then
				return false
			end
			local one = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			return (one ~= nil and entity == one) or (two ~= nil and entity == two)
		end

		-- The other hero: player one for player two, else player two (nil without a partner).
		function C.Other(hero)
			local one = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if two ~= nil and hero ~= nil and hero == two then
				return one
			end
			return two
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

		-- Runs fn under an acting entry. In a coroutine there is no pcall (the wrapped scripts
		-- yield); on the main thread nothing can yield, so the call is protected and the value
		-- always restored.
		function C.RunScoped(entry, fn, ...)
			local co = coroutine.running()
			if co == nil then
				local before = C.MainActing
				C.MainActing = entry
				local results = { pcall(fn, ...) }
				C.MainActing = before
				if not results[1] then
					error(results[2], 0)
				end
				return unpack(results, 2, table.maxn(results))
			end
			local before = C.ActingHero[co]
			C.ActingHero[co] = entry
			local results = { fn(...) }
			C.ActingHero[co] = before
			return unpack(results, 1, table.maxn(results))
		end
		C.Permanent(C.RunScoped, "RunScoped")

		-- Runs fn as `entity` when it is either hero: GetPlayerHero() answers that hero and
		-- GetPlayerHenchman() the other — each the other's partner, as Fable 3's two heroes are.
		-- Player one is pinned (C.PinOne), so the AI's nearer hero does not replace them. Any other
		-- entity, or no partner in the game, runs fn as it stands.
		function C.RunAs(entity, fn, ...)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if entity == nil or two == nil then
				return fn(...)
			end
			if entity == two then
				return C.RunScoped(two, fn, ...)
			end
			if entity == C.ShippedGetPlayerHero() then
				return C.RunScoped(C.PinOne, fn, ...)
			end
			return fn(...)
		end
		C.Permanent(C.RunAs, "RunAs")

		-- Runs fn with the acting hero asked of `resolve(arg)` on every read: a hero, or nil for
		-- player one (pinned). `resolve` must be a permanent (see the entry note above).
		function C.RunResolved(resolve, arg, fn, ...)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if two == nil then
				return fn(...)
			end
			return C.RunScoped({ RefiiResolve = resolve, RefiiArg = arg }, fn, ...)
		end
		C.Permanent(C.RunResolved, "RunResolved")

		-- Player two while player two is nearer `entity` than player one, else nil (player one):
		-- the native Refii.player_for's answer (see C.AIHeroFor) where the payload has it.
		C.NearerPartner = C.Permanent(function(entity)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			local hero = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
			if entity == nil or not entity:IsAlive() or partner == nil or not partner:IsAlive() then
				return nil
			end
			if Refii ~= nil and Refii.player_for ~= nil then
				return Refii.player_for(entity) == 2 and partner or nil
			end
			if hero == nil or not hero:IsAlive() then
				return partner
			end
			return GetDistanceBetweenEntities(partner, entity) < GetDistanceBetweenEntities(hero, entity) and partner or nil
		end, "NearerPartner")

		-- Runs fn with the acting hero the one nearer `entity`, asked on every read (a quest thread).
		function C.RunNear(entity, fn, ...)
			if entity == nil then
				return fn(...)
			end
			return C.RunResolved(C.NearerPartner, entity, fn, ...)
		end
		C.Permanent(C.RunNear, "RunNear")

		-- A message's sender, or nil (a message is a native object; anything else answers nil).
		local function SenderOf(message)
			return message:GetEntitySentBy()
		end
		function C.SenderOf(message)
			if message == nil or type(message) == "boolean" or type(message) == "number" then
				return nil
			end
			local ok, sender = pcall(SenderOf, message)
			return ok and sender or nil
		end

		-- The sender of the newest message of `kind` sent to `entity` after `last` when it is a
		-- hero, else nil. A peek: nothing is consumed.
		function C.HeroSentTo(kind, entity, last)
			if entity == nil or MessageEvents == nil or kind == nil then
				return nil
			end
			local posted, message
			if last ~= nil then
				posted, message = MessageEvents.IsMessageSentTo(kind, entity, last)
			else
				posted, message = MessageEvents.IsMessageSentTo(kind, entity)
			end
			if not posted then
				return nil
			end
			local sender = C.SenderOf(message)
			return C.IsHero(sender) and sender or nil
		end

		-- Wraps Class.Method (a class by its global name; `class` overrides) once: the shipped
		-- function is kept under rawget(class, "RefiiShipped<tag><method>") so a re-run wraps
		-- the same function, an inherited method is looked up and set on the class itself, and
		-- the wrapper `make(shipped)` returns is a permanent named "<tag>.<name>.<method>".
		function C.Wrap(name, method, tag, make, class)
			local class_ = class or _G[name]
			if type(class_) ~= "table" then
				return
			end
			local key = "RefiiShipped" .. tag .. method
			local shipped = rawget(class_, key)
			if shipped == nil then
				shipped = class_[method]
				if type(shipped) ~= "function" then
					return
				end
				rawset(class_, key, shipped)
			end
			class_[method] = C.Permanent(make(shipped), tag .. "." .. name .. "." .. method)
		end

		-- A class's own methods, by name (CreateSubClass and New excluded).
		function C.OwnMethods(class)
			local names = {}
			if type(class) ~= "table" then
				return names
			end
			for key, value in pairs(class) do
				if type(key) == "string" and type(value) == "function" and key ~= "CreateSubClass" and key ~= "New"
					and key:sub(1, 5) ~= "Refii" then
					names[#names + 1] = key
				end
			end
			return names
		end

		-- Wraps methods of a class (all its own when `methods` is nil) to run as the hero
		-- `resolver(self, method, ...)` names (C.RunAs: either hero, else as it stands).
		function C.WrapAs(name, methods, tag, resolver)
			local class = _G[name]
			if type(class) ~= "table" then
				return
			end
			for _, method in ipairs(methods or C.OwnMethods(class)) do
				C.Wrap(name, method, tag, function(shipped)
					return function(self, ...)
						return C.RunAs(resolver(self, method, ...), shipped, self, ...)
					end
				end)
			end
		end

		-- Resolver for the hero a behaviour holds in `field` (permanent, one per field).
		C.FieldResolvers = C.FieldResolvers or {}
		function C.FieldHero(field)
			local resolver = C.FieldResolvers[field]
			if resolver == nil then
				resolver = C.Permanent(function(self)
					local hero = type(self) == "table" and self[field] or nil
					return C.IsHero(hero) and hero or nil
				end, "Field." .. field)
				C.FieldResolvers[field] = resolver
			end
			return resolver
		end

		-- A behaviour that keeps its hero in `field`: PrepareToRun runs as `latch(self, ...)`'s
		-- hero (nil: as it stands) and stores it when `store` is set, and every other listed
		-- method runs as the hero the field holds, read on every ask (it may change mid-run).
		function C.LatchAs(name, methods, field, latch, store)
			-- Registered now, not on first use: a save being loaded maps it by name.
			local resolver = C.FieldHero(field)
			for _, method in ipairs(methods) do
				C.Wrap(name, method, "Latch", function(shipped)
					if method == "PrepareToRun" then
						return function(self, ...)
							local hero = latch(self, ...)
							if store and self ~= nil then
								self[field] = hero
							end
							return C.RunAs(hero, shipped, self, ...)
						end
					end
					return function(self, ...)
						local hero = self ~= nil and self[field] or nil
						if not C.IsHero(hero) then
							return shipped(self, ...)
						end
						return C.RunResolved(resolver, self, shipped, self, ...)
					end
				end)
			end
		end

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

		-- A GIFT'S ANSWER IS FOR ITS GIVER. BehaviourReceiveGift takes "the hero" from
		-- GetPlayerHero() throughout: who to face and answer, whose inventory the gift leaves
		-- (RemoveGiftFromHero), who a ring's AgreeToMarryHero goes to. The gift is a message
		-- (GetGift: MessageEvents' gift event) whose sender is the giver; PrepareToRun reads it, and
		-- the behaviour runs as them from then on (self.RefiiGiver, kept with the behaviour).
		function C.GiftGiver(behaviour)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if behaviour == nil or partner == nil or type(behaviour.GetGift) ~= "function" then
				return nil
			end
			-- GetGift answers (given, message): the first is a boolean, never the message. Indexing
			-- it threw inside AIManager:Update's best-behaviour pass and stopped every brain.
			local given, message = behaviour:GetGift()
			if not given or message == nil then
				return nil
			end
			local from = C.SenderOf(message)
			return C.IsHero(from) and from or nil
		end
		if type(BehaviourReceiveGift) == "table" and not rawget(BehaviourReceiveGift, "RefiiGiverWrapped") then
			rawset(BehaviourReceiveGift, "RefiiGiverWrapped", true)
			local methods = {}
			for key, value in pairs(BehaviourReceiveGift) do
				if type(value) == "function" and key ~= "CreateSubClass" and key ~= "GetGift" then
					methods[key] = value
				end
			end
			for key, shipped in pairs(methods) do
				BehaviourReceiveGift[key] = C.Permanent(function(self, ...)
					if key == "PrepareToRun" and self ~= nil then
						self.RefiiGiver = C.GiftGiver(self)
					end
					return C.RunAs(self and self.RefiiGiver, shipped, self, ...)
				end, "Gift.BehaviourReceiveGift." .. key)
			end
		end

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
		-- GetPlayerHenchman() is the other hero of the acting one; hidden (nil) inside a coroutine
		-- marked in C.HiddenHenchman or a main-thread call under C.HideHenchman.
		C.HiddenHenchman = C.HiddenHenchman or setmetatable({}, { __mode = "k" })
		C.HiddenDepth = C.HiddenDepth or 0
		if C.ShippedGetPlayerHenchman == nil and GetPlayerHenchman ~= nil then
			C.ShippedGetPlayerHenchman = GetPlayerHenchman
			GetPlayerHenchman = C.Permanent(function(...)
				local co = coroutine.running()
				if (co ~= nil and C.HiddenHenchman[co]) or C.HiddenDepth > 0 then
					return nil
				end
				if C.Acting() ~= nil then
					return C.ShippedGetPlayerHero()
				end
				return C.ShippedGetPlayerHenchman(...)
			end, "GetPlayerHenchman")
		end

		-- Runs fn (which never yields) with GetPlayerHenchman() answering nil.
		function C.HideHenchman(fn, ...)
			C.HiddenDepth = C.HiddenDepth + 1
			local results = { pcall(fn, ...) }
			C.HiddenDepth = C.HiddenDepth - 1
			if not results[1] then
				error(results[2], 0)
			end
			return unpack(results, 2, table.maxn(results))
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
				local one = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
				if co == nil or not PlayerTwoIsLocal(self and self.InteractEntity) then
					-- The shipped pair (player one the hero, player two the henchman), whichever
					-- hero the AI is running for: a remote player two is turned away as shipped.
					return C.RunAs(one, shipped, self, ...)
				end
				C.HiddenHenchman[co] = true
				local results = { C.RunAs(self.InteractEntity, shipped, self, ...) }
				C.HiddenHenchman[co] = nil
				return unpack(results, 1, table.maxn(results))
			end, "Serve." .. name .. "." .. method)
		end

		ServePlayerTwo("BehaviourBarmanOpenShopUI", "Update")
		ServePlayerTwo("BehaviourTownCrierOpenGUI", "Update")

		-- Their PrepareToRun's ghost test names the henchman of the shipped pair too. The crier's
		-- customer is kept by the crier: a title it then announces is the customer's (below).
		C.CrierCustomer = C.CrierCustomer or {}
		C.Wrap("BehaviourBarmanOpenShopUI", "PrepareToRun", "Serve", function(shipped)
			return function(self, ...)
				return C.RunAs(C.ShippedGetPlayerHero(), shipped, self, ...)
			end
		end)
		C.Wrap("BehaviourTownCrierOpenGUI", "PrepareToRun", "Serve", function(shipped)
			return function(self, ...)
				local results = { C.RunAs(C.ShippedGetPlayerHero(), shipped, self, ...) }
				if results[1] and self ~= nil and self.Entity ~= nil and C.IsHero(self.InteractEntity) and Refii ~= nil then
					C.CrierCustomer[Refii.handle(self.Entity)] = self.InteractEntity
				end
				return unpack(results, 1, table.maxn(results))
			end
		end)

		-- BED CONSENT (Fable 3): sleeping moves the clock for both players and sex fades both
		-- screens, so the other player is asked first, on their own screen (GameUiCoopConsent).
		-- The script waits for the answer; a refusal (or no answer) is the bed not used.
		function C.AskPartner(user, kind, detail)
			local henchman = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if henchman == nil or not henchman:IsAlive() or Refii == nil or Refii._consent_ask == nil then
				return true
			end
			local hero = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero() or nil
			local asker = (user ~= nil and user == henchman) and 2 or 1
			if user ~= nil and user ~= henchman and user ~= hero then
				return true
			end
			local ticket = Refii._consent_ask(kind, asker, detail or 0)
			while true do
				local answer = Refii._consent_answer(ticket)
				if answer ~= nil and answer >= 0 then
					return answer == 1
				end
				coroutine.yield()
			end
		end

		-- Sleep: the type the sleep menu chose is read right after it closes, before the fade
		-- (SleepFunction.Sleep); the partner is asked there, and a refusal reads as no sleep.
		C.SleepUser = C.SleepUser or setmetatable({}, { __mode = "k" })
		if SleepFunction ~= nil and SleepFunction.Sleep ~= nil and SleepFunction.Sleep ~= C.Sleep then
			local shipped = SleepFunction.Sleep
			C.Sleep = C.Permanent(function(entity, bed, ...)
				local co = coroutine.running()
				if co ~= nil then
					C.SleepUser[co] = entity or false
				end
				local results = { shipped(entity, bed, ...) }
				if co ~= nil then
					C.SleepUser[co] = nil
				end
				return unpack(results)
			end, "SleepFunction.Sleep")
			SleepFunction.Sleep = C.Sleep
		end
		if Bed ~= nil and Bed.GetSleepType ~= nil and Bed.GetSleepType ~= C.GetSleepType then
			local shipped = Bed.GetSleepType
			C.GetSleepType = C.Permanent(function(bed, ...)
				local sleep_type = shipped(bed, ...)
				local co = coroutine.running()
				local user = co ~= nil and C.SleepUser[co] or nil
				if user ~= nil and sleep_type ~= nil and ESleepType ~= nil and sleep_type ~= ESleepType.SLEEP_NONE then
					C.SleepUser[co] = nil
					if not C.AskPartner(user or nil, 1, sleep_type) then
						Bed.SetSleepType(bed, ESleepType.SLEEP_NONE)
						return ESleepType.SLEEP_NONE
					end
				end
				return sleep_type
			end, "Bed.GetSleepType")
			Bed.GetSleepType = C.GetSleepType
		end

		-- CRIME IS THE CRIMINAL'S. The crime manager records player two's crimes as player two's
		-- (coop_hero_hooks.cpp RefiiCoopCrimeIsTheirs; shipped AddCrime made every player criminal
		-- hero one), and VillageCrimeManager.IsResistingArrest / IsCriminal answer for the hero
		-- they are given (`sub_827CCED8` compares each crime's criminal with its argument). The
		-- group minds that ask about "the hero" are restated for both heroes (GUARDS AND VILLAGES,
		-- below); the native itself is left as shipped.
		if VillageCrimeManager ~= nil and VillageCrimeManager.IsResistingArrest ~= nil then
			if rawget(VillageCrimeManager, "RefiiShippedIsResistingArrest") == nil then
				rawset(VillageCrimeManager, "RefiiShippedIsResistingArrest",
					C.ShippedIsResistingArrest or VillageCrimeManager.IsResistingArrest)
			end
			C.ShippedIsResistingArrest = rawget(VillageCrimeManager, "RefiiShippedIsResistingArrest")
			C.IsResistingArrest = C.ShippedIsResistingArrest
			VillageCrimeManager.IsResistingArrest = C.ShippedIsResistingArrest
		end

		-- ONE-TIME COLLECTABLES PER PLAYER (Fable 3): a silver key one player has taken stays for the
		-- other (coop_world_hooks.cpp keeps it). The player who has it cannot take it again;
		-- with no partner in the game, a key its only player already has is simply cleared away.
		if OnActionUsePickUp ~= nil and OnActionUsePickUp.Update ~= nil and OnActionUsePickUp.Update ~= C.PickUp then
			local shipped = OnActionUsePickUp.Update
			C.PickUp = C.Permanent(function(self, ...)
				local item = self and self.ItemBeingUsed
				local user = self and self.EntityUsingItem
				if item ~= nil and user ~= nil and item:IsAlive() and Refii ~= nil and Refii._key_taken ~= nil then
					local henchman = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
					local player = (henchman ~= nil and user == henchman) and 2 or 1
					if Refii._key_taken(Refii.handle(item), player) == 1 then
						if henchman == nil or not henchman:IsAlive() then
							item:Destroy()
						end
						return
					end
				end
				return shipped(self, ...)
			end, "OnActionUsePickUp.Update")
			OnActionUsePickUp.Update = C.PickUp
		end

		-- SILVER-KEY CHESTS PER PLAYER (Fable 3's one-time chests: each hero opens one for themselves).
		-- A chest's loot lives in its own inventory and the shipped script moves it to the opener;
		-- an empty chest stops being targetable (the chest's tick, `sub_82295D60`). With a partner
		-- who has not opened it, the chest is closed again after the first opening
		-- (Chest.SnapToClosed, which the shipped script itself uses), its counted byte restored
		-- (Refii._chest_counted) and its loot and gold put back, so the partner opens it through the
		-- same script. Who has opened which chest is a SAVED player value (refii.chest.<UniqueID>),
		-- so it goes home with each hero; a player cannot open a chest twice.
		if OnActionUseTakeItems ~= nil and OnActionUseTakeItems.Update ~= nil and OnActionUseTakeItems.Update ~= C.TakeItems then
			local shipped = OnActionUseTakeItems.Update
			C.TakeItems = C.Permanent(function(self, ...)
				local chest = self and self.ItemBeingUsed
				local user = self and self.EntityUsingItem
				if chest == nil or user == nil or not chest:IsAlive() or not Chest.IsAvailable(chest) or Refii == nil
					or Refii.uid == nil or Chest.GetNumberOfSilverKeysNeeded(chest) <= 0 then
					return shipped(self, ...)
				end
				local one = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
				local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
				local player = (two ~= nil and user == two) and 2 or 1
				local partner = player == 2 and one or two
				local handle = Refii.handle(chest)
				local key = "refii.chest." .. tostring(Refii.uid(handle))
				if Refii.player_value(player, key, 0) ~= 0 then
					return
				end
				local loot = Refii.inventory(handle) or {}
				local money = Money.IsAvailable(chest) and Money.Get(chest) or 0
				local results = { shipped(self, ...) }
				local after = chest:IsAlive() and Refii.inventory(handle) or {}
				local opened = Chest.IsOpen(chest) or (#loot > 0 and #after == 0)
				if not opened then
					return unpack(results)
				end
				Refii.player_save(player, key, 1)
				local other = player == 1 and 2 or 1
				if partner ~= nil and partner:IsAlive() and Refii.player_value(other, key, 0) == 0 then
					Chest.SnapToClosed(chest)
					Refii._chest_counted(handle)
					for _, stack in ipairs(loot) do
						for i = 1, (stack.count or 1) do
							Refii.add_item(handle, stack.definition)
						end
					end
					if money > 0 and Money.IsAvailable(chest) then
						Money.Set(chest, money)
					end
				end
				return unpack(results)
			end, "OnActionUseTakeItems.Update")
			OnActionUseTakeItems.Update = C.TakeItems
		end

		-- A SILVER-KEY CHEST OPENED BEFORE THE PARTNER WAS THERE: open, emptied, and no longer
		-- targetable (the chest's tick `sub_82295D60` keys targeting on the inventory), so the wrapper
		-- above never sees player two use it. Its loot is its definition's InitialItems, put in once at
		-- creation (`sub_824EEF48` → `sub_824EFFC8`, Inventory.AddInitialItems) and saved empty after,
		-- and its gold the definition's "Money". Near player two, such a chest they have not opened is
		-- given back to them: player one is recorded as having opened it, it is closed again, its
		-- counted byte kept, and refilled the game's own way — a randomly generated entry rolls again,
		-- as it would for a fresh chest. The world is the host's, so the host does it.
		function C.RestoreChestsNear(hero)
			local x, y, z = Refii.position(Refii.handle(hero))
			if x == nil or Inventory == nil or Inventory.AddInitialItems == nil then
				return
			end
			for _, handle in ipairs(Refii.near(x, y, z, 20, false) or {}) do
				local chest = Refii.entity(handle)
				if chest ~= nil and chest:IsAlive() and Chest.IsAvailable(chest) and Chest.IsOpen(chest)
					and Chest.GetNumberOfSilverKeysNeeded(chest) > 0 then
					local key = "refii.chest." .. tostring(Refii.uid(handle))
					local items = Refii.inventory(handle)
					if items ~= nil and #items == 0 and Refii.player_value(2, key, 0) == 0 then
						if Refii.player_value(1, key, 0) == 0 then
							Refii.player_save(1, key, 1)
						end
						Chest.SnapToClosed(chest)
						Refii._chest_counted(handle)
						Inventory.AddInitialItems(chest)
						Refii._chest_money(handle)
					end
				end
			end
		end

		function C.ChestWatch()
			C.ChestTicks = (C.ChestTicks or 0) + 1
			if C.ChestTicks % 90 ~= 0 or Refii == nil or Refii.coop_mode == nil or Refii._chest_money == nil then
				return
			end
			local mode = Refii.coop_mode()
			if mode ~= 1 and mode ~= 2 then
				return
			end
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if two ~= nil and two:IsAlive() then
				C.RestoreChestsNear(two)
			end
		end

		-- KNOCKED DOWN, PLAYER TWO TAKES THE HERO'S PENALTY (Fable 3: one death tracker per hero).
		-- Shipped, Henchman_DeathTracker (gameflow.lua) revives player two with
		-- MESSAGE_EVENT_REVIVING_HENCHMAN and no scars, where Hero_DeathTracker gives hero one a
		-- scar (+1 with the scar augment, -1 with stoneskin). Player two is a hero now: the same
		-- scars land when their tracker revives them. The trackers are running coroutines, so the
		-- revive message is where the rule goes in.
		if MessageEvents ~= nil and MessageEvents.PostMessage ~= nil and MessageEvents.PostMessage ~= C.PostMessage then
			local shipped = MessageEvents.PostMessage
			C.PostMessage = C.Permanent(function(message, ...)
				local results = { shipped(message, ...) }
				if type(message) == "table" and EMessageEventType ~= nil and
					message.type == EMessageEventType.MESSAGE_EVENT_REVIVING_HENCHMAN then
					local two = message.to
					local henchman = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
					if two ~= nil and two == henchman and two:IsAlive() and Morph ~= nil and Morph.AddRandomScar ~= nil then
						local scars = 1
						if Creature.IsAugmentedWithType(two, EAugmentationType.AUGMENTATION_SCAR) then
							scars = scars + 1
						end
						if Creature.IsAugmentedWithType(two, EAugmentationType.AUGMENTATION_STONESKIN) then
							scars = scars - 1
						end
						for i = 1, scars do
							Morph.AddRandomScar(two)
						end
					end
				end
				return unpack(results)
			end, "MessageEvents.PostMessage")
			MessageEvents.PostMessage = C.PostMessage
		end

		-- Sex: asked before it starts (OnActionUseBed:Sex); a refusal uses neither bed nor partner.
		if OnActionUseBed ~= nil and OnActionUseBed.Sex ~= nil and OnActionUseBed.Sex ~= C.BedSex then
			local shipped = OnActionUseBed.Sex
			C.BedSex = C.Permanent(function(self, ...)
				if not C.AskPartner(self and self.EntityUsingItem, 2, 0) then
					return false, false
				end
				return shipped(self, ...)
			end, "OnActionUseBed.Sex")
			OnActionUseBed.Sex = C.BedSex
		end

		-- EACH VILLAGER'S OPINION OF EACH HERO (Fable 3 keeps a record per hero). The villagers'
		-- brains ask OpinionReaction.* with the villager alone and the game answers for hero one;
		-- each native now runs for the hero it concerns — a hero passed to it, else the one the
		-- villager notices (the nearer, the game's own GetEntityToNoticeExpressionsOf rule) — with
		-- that hero's opinion swapped in (mod_events.cpp OpinionHeroScope). Natives never yield.
		if OpinionReaction ~= nil and Refii ~= nil and Refii._opinion_enter ~= nil and not C.OpinionWrapped then
			C.OpinionWrapped = true
			local skip = { GetECType = true, IsAvailable = true, IsValidAxis = true }
			local function finish(ok, ...)
				Refii._opinion_leave()
				if not ok then
					error((...), 0)
				end
				return ...
			end
			for name, shipped in pairs(OpinionReaction) do
				if type(shipped) == "function" and not skip[name] then
					OpinionReaction[name] = C.Permanent(function(villager, ...)
						local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
						if two == nil or villager == nil then
							return shipped(villager, ...)
						end
						local one = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
						-- A hero named in the arguments wins; else code acting for a hero asks about
						-- that hero — player two's record in their scope, player one's (the shipped
						-- record, no nearer-hero choice) when player one is pinned.
						local hero = nil
						for i = 1, select("#", ...) do
							local v = select(i, ...)
							if v ~= nil and v == two then
								hero = two
								break
							elseif v ~= nil and v == one then
								return shipped(villager, ...)
							end
						end
						if hero == nil then
							hero = C.Acting()
							if hero == nil and C.IsPinnedOne() then
								return shipped(villager, ...)
							end
						end
						local entered = Refii._opinion_enter(Refii.handle(villager), hero ~= nil and Refii.handle(hero) or nil)
						if entered ~= 1 then
							return shipped(villager, ...)
						end
						return finish(pcall(shipped, villager, ...))
					end, "Opinion." .. name)
				end
			end
		end

		-- DOGS, ONE PER PLAYER. Shipped, GetDog() is the one global "the dog" slot (hero one's);
		-- player two's dog is the co-op one (coop_dog_hooks.cpp), Refii.player_dog(2). Each dog
		-- serves Dog.GetMaster(dog), so a behaviour that names GetDog() or GetPlayerHero() where
		-- it means "this dog" or "this dog's master" is restated for the dog it runs on.
		function C.PartnerDog()
			local handle = Refii ~= nil and Refii.player_dog ~= nil and Refii.player_dog(2) or nil
			return handle ~= nil and Refii.entity(handle) or nil
		end

		-- BOTH DOGS ALWAYS THINK. AIManager:IsEntityToUpdate updates GetDog() every time and any
		-- other creature only inside its update zone, so player two's dog stopped following and
		-- answering once it left that zone (seen on the host: no TrackHero, its navigation target
		-- frozen at player two's old spot). Fable 3's names both heroes' dogs (GetLocalHeroDog,
		-- GetRemoteHeroDog); player two's dog is the second.
		if AIManager ~= nil and AIManager.IsEntityToUpdate ~= nil and AIManager.IsEntityToUpdate ~= C.IsEntityToUpdate then
			local shipped = AIManager.IsEntityToUpdate
			C.IsEntityToUpdate = C.Permanent(function(self, entity, ...)
				if entity ~= nil then
					local partner = C.PartnerDog()
					if partner ~= nil and entity == partner then
						return true
					end
				end
				return shipped(self, entity, ...)
			end, "AIManager.IsEntityToUpdate")
			AIManager.IsEntityToUpdate = C.IsEntityToUpdate
		end

		-- ITEMS USED ON "THE DOG" (treat, collar, breed potions and books, the dig's happy dog)
		-- act on the user's own dog. Only GetDog() is redirected: GetPlayerHero() is left alone.
		-- Otherwise "the dog" is the acting hero's: a villager reacting to the dog (aibase.lua
		-- LookWhileSittingOrCarrying(EOT_DOG), PerformReaction(..., GetDog())) while the AI runs
		-- for player two names player two's dog. C.DogPlayer (1 or 2) states it outright.
		C.DogUser = C.DogUser or setmetatable({}, { __mode = "k" })
		C.DogSelf = C.DogSelf or 0
		if C.ShippedGetDog == nil and GetDog ~= nil then
			C.ShippedGetDog = GetDog
			GetDog = C.Permanent(function(...)
				if C.DogPlayer == 1 then
					return C.ShippedGetDog(...)
				end
				local co = coroutine.running()
				if (co ~= nil and C.DogUser[co]) or C.DogSelf > 0 or C.DogPlayer == 2 then
					return C.PartnerDog()
				end
				if C.Acting() ~= nil then
					local dog = C.PartnerDog()
					if dog ~= nil then
						return dog
					end
				end
				return C.ShippedGetDog(...)
			end, "GetDog")
		end

		-- Runs fn (which never yields) with GetDog() answering `hero`'s own dog.
		function C.WithDogOf(hero, fn, ...)
			local before = C.DogPlayer
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			C.DogPlayer = (two ~= nil and hero == two) and 2 or 1
			local results = { pcall(fn, ...) }
			C.DogPlayer = before
			if not results[1] then
				error(results[2], 0)
			end
			return unpack(results, 2, table.maxn(results))
		end

		-- AN NPC DEALS WITH ITS OWN HERO. The shipped AI names GetPlayerHero() (590 places in
		-- scripts/ai) and QuestManager.HeroEntity (80) for "the hero" — one shared screen. With two
		-- heroes roaming apart every brain answered player one: a travelling trader near player
		-- two walked off to player one, patrols and wanders centred on player one. While the AI
		-- runs for an entity — choosing a behaviour, resuming its update and exit coroutines — the
		-- acting hero (C.Acting, so GetPlayerHero/GetPlayerHenchman/QuestManager.HeroEntity swap as
		-- a pair, as C.RunAs does) is the one nearer that entity, Fable 3's rule; a player's dog
		-- acts for its own master.
		--
		-- PLAYER TWO'S DOG IS "THE DOG" IN ITS OWN BRAIN. Shipped AI code names GetDog() where it
		-- means the dog it is running for: every BehaviourBase mover sets the dog's speed only
		-- `if self.Entity == GetDog()` (aibase.lua MoveToPosition..MoveToEntityTimeLimited), so
		-- player two's dog chose its dig-spot lead and pathed there at no speed, standing still;
		-- `BehaviourDogInjured` asks `Dog.IsInjured(GetDog())`. While the AI runs for that dog,
		-- GetDog() answers it.
		--
		-- These entry points never yield (they resume), so setting and restoring around them is
		-- exact; a coroutine with its own acting hero (C.RunAs, C.RunNear) keeps it. For an
		-- entity nearer player one the scope is C.PinOne, so the opinion natives answer the same
		-- hero the AI chose rather than making their own nearer-hero choice.
		--
		-- WHICH HERO AN ENTITY DEALS WITH is the native Refii.player_for (mod_api.cpp
		-- refii_mod_player_for): a hero itself, a player's dog its master, anything else the nearer
		-- living hero, kept per entity until the other hero is more than 3 m nearer (an NPC between
		-- the two does not flip heroes every frame); one hero standing, that hero. Player two, or
		-- nil for player one.
		function C.AIHeroFor(entity)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if entity == nil or partner == nil then
				return nil
			end
			if Refii ~= nil and Refii.player_for ~= nil then
				return Refii.player_for(entity) == 2 and partner or nil
			end
			-- A payload without the native: the nearer living hero, no hysteresis.
			if not partner:IsAlive() then
				return nil
			end
			if entity == partner then
				return partner
			end
			local dog = C.PartnerDog()
			if dog ~= nil and entity == dog then
				return partner
			end
			if C.ShippedGetDog ~= nil and entity == C.ShippedGetDog() then
				return nil
			end
			return C.NearerPartner(entity)
		end

		local function AsItsEntity(table_, method, entity_of)
			if type(table_) ~= "table" or type(table_[method]) ~= "function" then
				return
			end
			local shipped_key = "RefiiShippedItsAI" .. method
			if rawget(table_, shipped_key) == nil then
				rawset(table_, shipped_key, table_[method])
			end
			local shipped = rawget(table_, shipped_key)
			table_[method] = C.Permanent(function(self, ...)
				local entity = entity_of(self, ...)
				local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
				if entity == nil or partner == nil then
					return shipped(self, ...)
				end
				local own_dog = C.PartnerDog()
				local is_dog = own_dog ~= nil and entity == own_dog
				local acting = C.AIHeroFor(entity) or C.PinOne
				local before = C.AIActing
				C.AIActing = acting
				if is_dog then
					C.DogSelf = C.DogSelf + 1
				end
				local results = { pcall(shipped, self, ...) }
				if is_dog then
					C.DogSelf = C.DogSelf - 1
				end
				C.AIActing = before
				if not results[1] then
					error(results[2], 0)
				end
				return unpack(results, 2, table.maxn(results))
			end, "ItsAI." .. method)
		end

		-- EACH HERO THEIR OWN FIGHT. CombatRegister:Update (combatregister.lua) zeroes ONE
		-- MidstCount / CloseToCombat / InAnyCombat, adds every attacker of a player or a player's
		-- ally to it (AddToMidstCountScore, from each CombatInfo update) and sets the three on
		-- QuestManager.HeroEntity — player one — so a fight anywhere put player one in combat (the
		-- camera's alert, Player.GetMidstOfCombatCounter) and player two's own fights never set
		-- theirs. Each score now counts for the hero it is about: the target when it is a hero,
		-- else the hero nearer the attacker (a player's ally under attack), run as that hero for the
		-- ally test. The pass's three setters then give each hero their own; an online host states
		-- player two's in the player store (coop.combat.*), and the joiner, which plays player two,
		-- applies them to its hero each frame (C.CombatApply, from the camera's FrameUpdate).
		if CombatRegister ~= nil and Player ~= nil and type(CombatRegister.AddToMidstCountScore) == "function"
			and type(CombatRegister.UpdateFormations) == "function" then
			local function Fresh() return { midst = 0, close = false, any = false } end
			C.CombatAcc = C.CombatAcc or { Fresh(), Fresh() }
			C.CombatSent = C.CombatSent or {}
			-- The last published state per hero (Refii.combat_state).
			C.CombatLast = C.CombatLast or { Fresh(), Fresh() }

			if rawget(CombatRegister, "RefiiShippedUpdateFormations") == nil then
				rawset(CombatRegister, "RefiiShippedUpdateFormations", CombatRegister.UpdateFormations)
			end
			local shipped_formations = rawget(CombatRegister, "RefiiShippedUpdateFormations")
			CombatRegister.UpdateFormations = C.Permanent(function(self, ...)
				C.CombatAcc = { Fresh(), Fresh() }
				local results = { shipped_formations(self, ...) }
				C.CombatPass = true
				return unpack(results, 1, table.maxn(results))
			end, "CombatRegister.UpdateFormations")

			if rawget(CombatRegister, "RefiiShippedAddToMidstCountScore") == nil then
				rawset(CombatRegister, "RefiiShippedAddToMidstCountScore", CombatRegister.AddToMidstCountScore)
			end
			local shipped_score = rawget(CombatRegister, "RefiiShippedAddToMidstCountScore")
			CombatRegister.AddToMidstCountScore = C.Permanent(function(self, info, ...)
				local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
				if partner == nil or not partner:IsAlive() or info == nil or info.Target == nil then
					return shipped_score(self, info, ...)
				end
				local player = 1
				if info.Target == partner then
					player = 2
				elseif info.Target ~= C.ShippedGetPlayerHero() and info.Combatant ~= nil
					and C.NearerPartner(info.Combatant) ~= nil then
					player = 2
				end
				local acc = C.CombatAcc[player]
				self.MidstCount, self.CloseToCombat, self.InAnyCombat = acc.midst, acc.close, acc.any
				C.RunAs(player == 2 and partner or C.ShippedGetPlayerHero(), shipped_score, self, info, ...)
				acc.midst, acc.close, acc.any = self.MidstCount, self.CloseToCombat, self.InAnyCombat
			end, "CombatRegister.AddToMidstCountScore")

			local function PerHero(name, field, last)
				if rawget(Player, "RefiiShipped" .. name) == nil then
					rawset(Player, "RefiiShipped" .. name, Player[name])
				end
				local shipped = rawget(Player, "RefiiShipped" .. name)
				Player[name] = C.Permanent(function(entity, value, ...)
					local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
					local one = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
					if not C.CombatPass or entity == nil or entity ~= one
						or partner == nil or not partner:IsAlive() or Refii == nil or Refii.coop_mode == nil then
						if entity ~= nil and entity == one then
							C.CombatLast[1][field] = value
						end
						return shipped(entity, value, ...)
					end
					if last then
						C.CombatPass = false
					end
					C.CombatLast[1][field] = C.CombatAcc[1][field]
					C.CombatLast[2][field] = C.CombatAcc[2][field]
					local mode = Refii.coop_mode()
					if mode == 3 then
						return
					end
					shipped(entity, C.CombatAcc[1][field], ...)
					local two = C.CombatAcc[2][field]
					if mode == 1 then
						return shipped(partner, two, ...)
					end
					local stated = type(two) == "boolean" and (two and 1 or 0) or two
					local key = "coop.combat." .. field
					if C.CombatSent[key] ~= stated then
						C.CombatSent[key] = stated
						Refii.player_set(2, key, stated)
					end
				end, "Combat." .. name)
			end
			PerHero("SetMidstOfCombatCounter", "midst", false)
			PerHero("SetCloseToCombat", "close", false)
			PerHero("SetInAnyCombat", "any", true)

			function C.CombatApply()
				if Refii == nil or Refii.coop_mode == nil or Refii.coop_mode() ~= 3 then
					return
				end
				local hero = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
				if hero == nil or not hero:IsAlive() or not Player.IsAvailable(hero) then
					return
				end
				rawget(Player, "RefiiShippedSetMidstOfCombatCounter")(hero, Refii.player_value(2, "coop.combat.midst", 0))
				rawget(Player, "RefiiShippedSetCloseToCombat")(hero, Refii.player_value(2, "coop.combat.close", 0) ~= 0)
				rawget(Player, "RefiiShippedSetInAnyCombat")(hero, Refii.player_value(2, "coop.combat.any", 0) ~= 0)
			end
		end

		local function OwnEntity(self) return self ~= nil and self.Entity or nil end
		AsItsEntity(BehaviourBase, "RunUpdateFunction", OwnEntity)
		AsItsEntity(BehaviourBase, "RunExitFunction", OwnEntity)
		AsItsEntity(BehaviourBase, "RunFastExitFunctions", OwnEntity)
		AsItsEntity(AIManager, "FindBestBehaviourAndGroup", function(_, _, entity) return entity end)
		-- The running behaviour's own IsStillRunnable and IsFinished (aimanager.lua
		-- UpdateCurrentBehaviour, around its RunUpdateFunction) ask for the same hero its Update does.
		AsItsEntity(AIManager, "UpdateCurrentBehaviour", function(_, groups)
			local current = type(groups) == "table" and groups.CurrentBehaviour or nil
			local instance = type(current) == "table" and current.BehaviourInstance or nil
			return type(instance) == "table" and instance.Entity or nil
		end)

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

		-- LEADING ITS OWN PLAYER (dig spots, chests, quest objectives). The lead behaviour waits,
		-- barking, until "the player can see the dog/destination": IsEntityVisibleOnScreen /
		-- IsPositionVisibleOnScreen, which ask player one's camera. For player two's dog they ask
		-- player two's view (Refii._partner_screen; the host's copy of player two's camera).
		for _, method in ipairs({ "GetPlayerCanSeeDestination", "GetPlayerCanSeeDog" }) do
			local class = BehaviourDogLeadHeroBase
			if type(class) == "table" and type(class[method]) == "function"
				and rawget(class, "RefiiShippedScreen" .. method) == nil then
				local shipped = class[method]
				rawset(class, "RefiiShippedScreen" .. method, shipped)
				class[method] = C.Permanent(function(self, ...)
					local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
					if partner == nil or self == nil or Refii == nil or Refii._partner_screen == nil
						or Dog.GetMaster(self.Entity) ~= partner then
						return shipped(self, ...)
					end
					Refii._partner_screen(true)
					local ok, result = pcall(shipped, self, ...)
					Refii._partner_screen(false)
					if not ok then
						error(result, 0)
					end
					return result
				end, "DogLead." .. method)
			end
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
					Refii._estate_buyer(Refii.acting_player ~= nil and Refii.acting_player() or (C.Acting() ~= nil and 2 or 1))
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

		-- A HERO'S FAMILY ANSWERS THAT HERO. The spouse, child and family behaviours say
		-- GetPlayerHero() for "my husband/wife", "my mum/dad". The family natives answer for the
		-- hero they are given: PlayerFamily.IsMarriedTo / IsParentOf / IsFamilyMember / GetChildren
		-- take the family component (136) of their first argument (`sub_827BC100`) and walk its
		-- lists (`sub_821C6DB0`, `sub_82285B08`, `sub_827AFC90`, `sub_827B3E98`), and both heroes'
		-- bodies carry one. A villager in exactly one hero's family runs as that hero; in both or
		-- neither, as the hero the AI runs for.
		C.FamilyHeroOf = C.Permanent(function(entity)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if entity == nil or two == nil or PlayerFamily == nil or not entity:IsAlive() then
				return nil
			end
			local found = nil
			for _, hero in ipairs({ C.ShippedGetPlayerHero(), two }) do
				local children = hero ~= nil and hero:IsAlive() and PlayerFamily.GetChildren(hero, entity) or nil
				if hero ~= nil and hero:IsAlive() and (PlayerFamily.IsMarriedTo(hero, entity)
					or PlayerFamily.IsParentOf(hero, entity) or PlayerFamily.IsFamilyMember(hero, entity)
					or (type(children) == "table" and #children > 0)) then
					if found ~= nil then
						return nil
					end
					found = hero
				end
			end
			return found
		end, "FamilyHeroOf")

		-- The hero a villager is married to (player two first), or nil.
		function C.SpouseOf(villager)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if villager == nil or two == nil or PlayerFamily == nil then
				return nil
			end
			for _, hero in ipairs({ two, C.ShippedGetPlayerHero() }) do
				if hero ~= nil and hero:IsAlive() and PlayerFamily.IsMarriedTo(hero, villager) then
					return hero
				end
			end
			return nil
		end

		local function FamilyOfSelf(self)
			return C.FamilyHeroOf(type(self) == "table" and self.Entity or nil)
		end
		for _, name in ipairs({ "BehaviourSpouseComment", "BehaviourSpouseMissedHero", "BehaviourSpousePostChildBirth",
			"BehaviourSpouseHoneymoonPeriod", "BehaviourSpouseJealous", "BehaviourSpouseLeaveHero",
			"BehaviourSpouseCatchBigamist", "BehaviourFamilyIdleUntilSeeHero", "BehaviourThreatenDivorce",
			"BehaviourInteractWithCot", "BehaviourChildLeaveHero", "BehaviourGreetHeroParent",
			"BehaviourWitnessParentDeath", "BehaviourHerosChildComment", "BehaviourHerosChildCommentWhenPrompted" }) do
			C.WrapAs(name, nil, "Spouse", FamilyOfSelf)
		end
		-- "Is this a hero's child / spouse / family" asked by one method of a wider behaviour.
		C.WrapAs("BehaviourChildTalkToParent", { "PickLine" }, "Family", FamilyOfSelf)
		C.WrapAs("BehaviourParentTalkToChild", { "PickLine" }, "Family", FamilyOfSelf)
		C.WrapAs("BehaviourSleepAtHome", { "PrepareToRun" }, "Family", FamilyOfSelf)
		-- A divorcing parent's group mind is matched by the child's own PrepareToRun
		-- (GroupMindManager:GetValidGroupMind): the children are the parent's hero's.
		C.WrapAs("FamilyDivorceGroupMind", { "GetMetricForComparison" }, "Family", function(self)
			local parent = type(self) == "table" and type(self.GetParent) == "function" and self:GetParent() or nil
			return C.FamilyHeroOf(parent)
		end)
		-- A child following its parent: chosen as its family's hero, then that parent throughout.
		C.LatchAs("BehaviourFollowHeroParent", { "PrepareToRun", "IsStillRunnable", "Update", "MakeSuggestionToParent" },
			"Parent", FamilyOfSelf, false)

		-- A FIANCE'S HERO. OpinionReaction.IsEngagedToHero reads the villager's engaged byte
		-- (`sub_82427750`: comp+771), part of the per-hero opinion block the opinion scope swaps
		-- (mod_events.cpp kOpinionRegions {770, 3}), so asked as each hero it answers for each.
		C.FianceOf = C.Permanent(function(villager)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if villager == nil or two == nil or OpinionReaction == nil or not OpinionReaction.IsAvailable(villager) then
				return nil
			end
			if two:IsAlive() and C.RunAs(two, OpinionReaction.IsEngagedToHero, villager) then
				return two
			end
			local one = C.ShippedGetPlayerHero()
			if one ~= nil and C.RunAs(one, OpinionReaction.IsEngagedToHero, villager) then
				return one
			end
			return nil
		end, "FianceOf")
		local function FianceOfSelf(self)
			return C.FianceOf(type(self) == "table" and self.Entity or nil)
		end
		for _, name in ipairs({ "BehaviourBreakEngagement", "BehaviourFianceJealous" }) do
			C.LatchAs(name, { "PrepareToRun", "Update", "FastExit" }, "RefiiHero", FianceOfSelf, true)
		end

		-- A FOLLOWER'S COMMENTS ON A HOUSE are about the house the hero they follow targets
		-- (BehaviourHouseOpinion.WantToCommentAboutHouse / MakeCommentAboutHouse, called with the
		-- running behaviour as self from the micro-reactions).
		function C.FollowedHero(entity)
			if entity == nil or Follow == nil or not Follow.IsAvailable(entity) or not Follow.IsFollowing(entity) then
				return nil
			end
			local target = Follow.GetFollowTarget(entity)
			return C.IsHero(target) and target or nil
		end
		C.WrapAs("BehaviourHouseOpinion", { "WantToCommentAboutHouse", "MakeCommentAboutHouse" }, "House", function(self)
			return C.FollowedHero(type(self) == "table" and self.Entity or nil)
		end)

		-- FLIRTING, COURTING, LOVE: the hero chosen when the behaviour was picked stays its hero
		-- (it walks to them, reads their opinion, puts suggestions on their HUD). MakeSuggestion and
		-- DoReaction are left to their callers' hero (micro-reactions call them too).
		local function ChosenHero()
			return C.Acting() or C.ShippedGetPlayerHero()
		end
		for _, name in ipairs({ "BehaviourFlirt", "BehaviourCourtHero", "BehaviourLoveHero" }) do
			C.LatchAs(name, { "PrepareToRun", "IsStillRunnable", "Update", "SlowExit", "FastExit" }, "RefiiHero", ChosenHero, true)
		end

		-- WHO A VILLAGER FOLLOWS AND ANSWERS. The expression behaviour answers the hero who
		-- performed the expression it is handling (the message's sender), else the hero the AI runs
		-- for; the follow behaviours the hero they follow; shipped, both say GetPlayerHero(), so
		-- player two's "follow me" or seduction made the villager follow player one
		-- (FollowEntityFromExpression with GetPlayerHero()). A follower invited for sex follows the
		-- hero whose seduction it accepted (OpinionReaction.SetFollowForSex, below) before its
		-- follow target is set.
		function C.WrapClassAs(name, resolver)
			C.WrapAs(name, nil, "Hero", resolver)
		end
		C.SexInviter = C.SexInviter or {}
		local MessageArg = { FollowHero = 1, ReactToSeductionAttempt = 1, DoDeedReaction = 1, RespondToBegging = 1,
			IsFollowExpression = 2 }
		C.NoticedPartner = C.Permanent(function(behaviour, method, ...)
			local index = method ~= nil and MessageArg[method] or nil
			if index ~= nil and select("#", ...) >= index then
				local sender = C.SenderOf((select(index, ...)))
				if C.IsHero(sender) then
					return sender
				end
			end
			local entity = type(behaviour) == "table" and behaviour.Entity or nil
			return C.AIHeroFor(entity) or C.ShippedGetPlayerHero()
		end, "NoticedPartner")
		C.FollowLeader = C.Permanent(function(behaviour)
			local entity = type(behaviour) == "table" and behaviour.Entity or nil
			if entity == nil then
				return nil
			end
			if Follow ~= nil and type(Follow.GetFollowTarget) == "function" then
				local target = Follow.GetFollowTarget(entity)
				if target ~= nil and target:IsAlive() and C.IsHero(target) then
					return target
				end
			end
			local inviter = Refii ~= nil and Refii.handle ~= nil and C.SexInviter[Refii.handle(entity)] or nil
			if inviter ~= nil and inviter:IsAlive() and C.IsHero(inviter) then
				return inviter
			end
			return C.AIHeroFor(entity) or C.ShippedGetPlayerHero()
		end, "FollowLeader")
		C.WrapClassAs("BehaviourNoticeExpression", C.NoticedPartner)
		-- The deed's doer is the hero noticed (its title credit, its amused/scared stats).
		C.Wrap("BehaviourNoticeExpression", "DoDeedReaction", "Deed", function(shipped)
			return function(self, message, just_talk, entity_to_notice, ...)
				local sender = C.SenderOf(message)
				if C.IsHero(sender) then
					entity_to_notice = sender
				end
				return shipped(self, message, just_talk, entity_to_notice, ...)
			end
		end)
		for _, name in ipairs({ "BehaviourFollow", "BehaviourFollowForSex", "BehaviourFollowCatchUp", "BehaviourFollowWaitOutside" }) do
			C.WrapClassAs(name, C.FollowLeader)
		end

		-- ONLINE, PLAYER TWO'S FOLLOWERS ARE THE HOST'S. The guest never sends who follows whom and
		-- the villagers think on the host, so the joiner's hero two lists nobody: its bed never
		-- found a partner. The host publishes them (household_persist.cpp, Refii._followers_two:
		-- handle and flags, 1 engaged, 2 following for sex, 4 married), and on the joiner the follow
		-- queries for its own hero two answer from that list.
		function C.JoinerTwo(hero)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			return hero ~= nil and partner ~= nil and hero == partner and Refii ~= nil and Refii.coop_mode ~= nil
				and Refii._followers_two ~= nil and Refii.coop_mode() == 3
		end
		function C.PublishedFollowers()
			local list = {}
			for i = 1, 32 do
				local handle, flags = Refii._followers_two(i)
				local entity = handle ~= nil and Refii.entity(handle) or nil
				if entity ~= nil then
					list[#list + 1] = { entity = entity, flags = flags or 0 }
				end
			end
			return list
		end
		if Follow ~= nil and Follow.GetListOfFollowers ~= nil and not rawget(Follow, "RefiiJoinerFollowers") then
			rawset(Follow, "RefiiJoinerFollowers", true)
			local shipped_list = Follow.GetListOfFollowers
			Follow.GetListOfFollowers = C.Permanent(function(hero, ...)
				if not C.JoinerTwo(hero) then
					return shipped_list(hero, ...)
				end
				local list = {}
				for _, follower in ipairs(C.PublishedFollowers()) do
					list[#list + 1] = follower.entity
				end
				return list
			end, "Follow.GetListOfFollowers")
			local shipped_sex = Follow.IsBeingFollowedBySexPartner
			if shipped_sex ~= nil then
				Follow.IsBeingFollowedBySexPartner = C.Permanent(function(hero, ...)
					if not C.JoinerTwo(hero) then
						return shipped_sex(hero, ...)
					end
					for _, follower in ipairs(C.PublishedFollowers()) do
						if follower.flags % 4 >= 2 then
							return true
						end
					end
					return false
				end, "Follow.IsBeingFollowedBySexPartner")
			end
		end

		-- ONLINE, PLAYER TWO'S BED IS PARTLY THE HOST'S. On the joiner the bed runs as shipped for its
		-- player (the menus, the fade, their own stats), but the partners, the pregnancy and the baby,
		-- the partner's opinion, the deeds and the gold are the host's world: SexFunction.Begin there
		-- would find no partner (SexFunction.SexPartners is filled by the host's follow behaviours) and
		-- a client's money changes nothing. So the joiner's Begin for its hero two also goes to the
		-- host (`sex:<bed uid>:<condom>`), which runs the bed's own payment (the prostitutes' and the
		-- inn's, as OnActionUseBed:Update/Sex reckon them) and Begin for its hero two in a thread of
		-- its own, silent: no fade, movie, sound, tutorial, message box or achievement on the host.
		C.Silent = C.Silent or setmetatable({}, { __mode = "k" })
		C.HostThreads = C.HostThreads or {}
		function C.Silence(class, name, answer)
			if type(class) ~= "table" or type(class[name]) ~= "function" or rawget(class, "RefiiSilent" .. name) then
				return
			end
			local shipped = class[name]
			rawset(class, "RefiiSilent" .. name, shipped)
			class[name] = C.Permanent(function(...)
				local co = coroutine.running()
				if co ~= nil and C.Silent[co] then
					return answer
				end
				return shipped(...)
			end, "Silent." .. name)
		end
		C.Silence(GUI, "FadeScreenOut")
		C.Silence(GUI, "FadeScreenIn")
		C.Silence(GUI, "IsScreenFading", false)
		C.Silence(GUI, "SetDPadEnabled")
		C.Silence(GUI, "PlayLocalisedMovie")
		C.Silence(GUI, "DisplayMessageBox")
		C.Silence(Debug, "SetShouldDrawWorldIcons")
		C.Silence(Sound, "SetSoundEffectVolume")
		C.Silence(TutorialManager, "DisplayTutorial")

		-- THE JOINER'S OWN BED SCENE IS ITS PRESENTATION. Its Begin finds no partner (they are the
		-- host's), so what it would count is wrong and what it would show is incomplete: its tracked
		-- stats and achievements are left to the host's (sent here, mod_events.cpp `stat`/`ach`), and
		-- its fade-in waits for the host's verdict (RefiiCoop.SexVerdict: the partner, a child born,
		-- the first) to play what a birth plays — the PREGNANT line with the sound down for five
		-- seconds, Childbirth.bik for the first child, the baby tutorial after the fade.
		C.Presenting = C.Presenting or setmetatable({}, { __mode = "k" })
		for _, name in ipairs({ "ModifyTrackedStat", "UnlockAchievement" }) do
			if Stats ~= nil and type(Stats[name]) == "function" and not rawget(Stats, "RefiiPresenting" .. name) then
				local shipped = Stats[name]
				rawset(Stats, "RefiiPresenting" .. name, shipped)
				Stats[name] = C.Permanent(function(...)
					local co = coroutine.running()
					if co ~= nil and C.Presenting[co] then
						return nil
					end
					return shipped(...)
				end, "Presenting." .. name)
			end
		end
		function C.WaitSeconds(seconds)
			local until_frame = Timing.GetWorldFrame() + Timing.GetTickRate() * seconds
			while Timing.GetWorldFrame() < until_frame do
				coroutine.yield()
			end
		end
		-- The PREGNANT line itself is the host's partner speaking: it arrives with the scene's chatter.
		function C.PlayBirth(verdict)
			Sound.SetSoundEffectVolume(0.0)
			C.WaitSeconds(5.0)
			Sound.SetSoundEffectVolume(1.0)
			if verdict.first then
				GUI.PlayLocalisedMovie("Childbirth.bik")
			end
		end
		if GUI ~= nil and GUI.FadeScreenIn ~= nil and not rawget(GUI, "RefiiAwaitVerdict") then
			local shipped = GUI.FadeScreenIn
			rawset(GUI, "RefiiAwaitVerdict", shipped)
			GUI.FadeScreenIn = C.Permanent(function(...)
				local co = coroutine.running()
				local verdict = nil
				if co ~= nil and C.Presenting[co] then
					C.Presenting[co] = nil
					local limit = Timing.GetWorldFrame() + Timing.GetTickRate() * 45.0
					while C.SexVerdict == nil and Timing.GetWorldFrame() < limit do
						coroutine.yield()
					end
					verdict = C.SexVerdict
					C.SexVerdict = nil
					if verdict ~= nil and verdict.born then
						C.PlayBirth(verdict)
					end
				end
				local results = { shipped(...) }
				if verdict ~= nil and verdict.born and TutorialManager ~= nil
					and not TutorialManager.HasDisplayedTutorial(ETutorialType.TUTORIAL_BABY) then
					TutorialManager.DisplayTutorial(ETutorialType.TUTORIAL_BABY)
				end
				return unpack(results)
			end, "AwaitVerdict.FadeScreenIn")
		end

		if SexFunction ~= nil and SexFunction.Begin ~= nil and SexFunction.Begin ~= C.SexBegin then
			local shipped = SexFunction.Begin
			-- C.SexHero is the hero having sex while Begin runs (BehaviourAvoidSex's hero).
			C.SexBegin = C.Permanent(function(hero, bed, ...)
				if bed ~= nil and C.JoinerTwo(hero) and Refii._household ~= nil and Refii.uid(bed) ~= nil then
					local condom = Player ~= nil and Player.IsWearingCondom ~= nil and Player.IsWearingCondom(hero)
					Refii._household("sex", Refii.uid(bed), condom and 1 or 0)
					local co = coroutine.running()
					if co ~= nil then
						C.SexVerdict = nil
						C.Presenting[co] = true
						local results = { shipped(hero, bed, ...) }
						C.Presenting[co] = nil
						return unpack(results)
					end
				end
				local before = C.SexHero
				C.SexHero = hero
				local results = { shipped(hero, bed, ...) }
				C.SexHero = before
				return unpack(results, 1, table.maxn(results))
			end, "SexFunction.Begin")
			SexFunction.Begin = C.SexBegin
		end

		C.HostBedSexThread = C.Permanent(function(hero, bed, condom)
			return C.RunAs(hero, function()
				local owner = OwnedEntity.IsAvailable(bed) and OwnedEntity.GetOwner(bed) or nil
				local trespass, cost = false, 0
				if owner ~= nil and owner:IsAlive() then
					local bar = Building.IsBuildingOfType(owner, EBuildingType.BUILDING_BAR)
					trespass = not PlayerProperties.IsOwnedByPlayer(hero, owner) and not bar
					if bar then
						cost = Building.GetCostToSleepHere(owner) or 0
					end
				end
				local whores = 0
				for _, follower in ipairs(Follow.GetListOfFollowers(hero) or {}) do
					if Villager.GetOccupation(follower) == EJobType.JOB_WHORE and not PlayerFamily.IsMarriedTo(hero, follower) then
						whores = whores + 1
					end
				end
				local total = cost + whores * GDB.GetRecord("WhoreMultipliers"):GetS32("Cost")
				if total > 0 then
					Money.Modify(hero, -total)
				end
				if condom == 1 then
					Player.SetWearingCondom(hero, true)
				end
				-- What the bed's own Sex charges a hero after Begin (onactionusebed main.3 pc 303–362):
				-- the prostitutes' morality and purity, +10 morality for protection. Morality is the
				-- host's (a client's change applies only when received), so it is charged here.
				local multipliers = GDB.GetRecord("WhoreMultipliers")
				SexFunction.HenchmanHavingSex = false
				local before = SexFunction.FrameLastHadChild
				C.BirthSpouse = nil
				SexFunction.Begin(hero, bed, trespass, owner)
				if Stats.IsAvailable(hero) then
					Stats.ModifyMorality(hero, multipliers:GetS32("MoralityModifier") * whores)
					Stats.ModifyPurity(hero, multipliers:GetFloat("PurityModifier") * whores)
					if condom == 1 then
						Stats.ModifyMorality(hero, 10.0)
					end
				end
				-- The verdict for the joiner's own presentation (mod_events.cpp `sexdone`).
				local born = SexFunction.FrameLastHadChild ~= before
				local first = born and Refii._children_two ~= nil and Refii._children_two() == 1
				local partner = born and C.BirthSpouse ~= nil and Refii.uid(C.BirthSpouse) or "0x1"
				Refii._household("sexdone", partner, (born and 1 or 0) + (first and 2 or 0))
			end)
		end, "HostBedSexThread")

		-- THE CHATTER GOES TO THE JOINER. Every line a partner is told to say in the silent thread
		-- (the comments, the PREGNANT line) is played later by the villager's AI; its speaker is marked
		-- so those lines reach the joiner's screen too (mod_events.cpp ForwardSpeech).
		if ScriptFunction ~= nil and ScriptFunction.SaySimLine ~= nil and not rawget(ScriptFunction, "RefiiForwardSpeech") then
			local shipped = ScriptFunction.SaySimLine
			rawset(ScriptFunction, "RefiiForwardSpeech", shipped)
			ScriptFunction.SaySimLine = C.Permanent(function(speaker, ...)
				local co = coroutine.running()
				if co ~= nil and C.Silent[co] and speaker ~= nil and Refii._forward_speech ~= nil then
					Refii._forward_speech(Refii.handle(speaker), 15.0)
				end
				return shipped(speaker, ...)
			end, "Forward.SaySimLine")
		end

		-- Which spouse a birth in the silent thread was with (the joiner's verdict).
		if PlayerFamily ~= nil and PlayerFamily.AdoptWithSpouse ~= nil and not rawget(PlayerFamily, "RefiiBirthSpouse") then
			local shipped = PlayerFamily.AdoptWithSpouse
			rawset(PlayerFamily, "RefiiBirthSpouse", shipped)
			PlayerFamily.AdoptWithSpouse = C.Permanent(function(hero, spouse, ...)
				local co = coroutine.running()
				if co ~= nil and C.Silent[co] then
					C.BirthSpouse = spouse
				end
				return shipped(hero, spouse, ...)
			end, "BirthSpouse.AdoptWithSpouse")
		end

		function C.HostBedSex(bed, condom)
			local hero = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if bed == nil or hero == nil or not hero:IsAlive() then
				return
			end
			local co = coroutine.create(C.HostBedSexThread)
			C.Silent[co] = true
			C.HostThreads[#C.HostThreads + 1] = { co = co, args = { hero, bed, condom } }
		end

		-- The host's threads run once a world frame (from the per-hero FrameUpdate, below).
		function C.HouseholdTick()
			local frame = GetWorldFrame ~= nil and GetWorldFrame() or 0
			if C.HouseholdFrame == frame or #C.HostThreads == 0 then
				return
			end
			C.HouseholdFrame = frame
			for i = #C.HostThreads, 1, -1 do
				local thread = C.HostThreads[i]
				if coroutine.status(thread.co) == "dead" then
					table.remove(C.HostThreads, i)
				else
					-- Player two's stats counted while it runs go to the joiner (mod_events.cpp).
					if Refii._household_context ~= nil then
						Refii._household_context(true)
					end
					local ok, err = coroutine.resume(thread.co, unpack(thread.args))
					if Refii._household_context ~= nil then
						Refii._household_context(false)
					end
					thread.args = {}
					if not ok then
						print("[refii] player two's bed on the host: " .. tostring(err))
						table.remove(C.HostThreads, i)
					end
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
		C.JobWorker = C.Permanent(function(sign)
			local partner = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			local hero = C.ShippedGetPlayerHero and C.ShippedGetPlayerHero()
			if sign == nil or partner == nil or not partner:IsAlive() then
				return hero
			end
			if sign.CurrentState == 0 then
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
			return sign.RefiiWorker or hero
		end, "JobWorker")

		function C.RunJob(sign, fn, ...)
			local co = coroutine.running()
			if co == nil or sign == nil then
				return fn(...)
			end
			C.JobThread[co] = true
			local results = { C.RunResolved(C.JobWorker, sign, fn, ...) }
			C.JobThread[co] = nil
			return unpack(results, 1, table.maxn(results))
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

		-- ============================================================================
		-- EACH NPC'S HERO, CASE BY CASE. The AI rule above runs an NPC for the hero nearer it; the
		-- cases below are the ones where the shipped script means a particular hero instead — the
		-- one who sent the message being answered, the one a behaviour latched when it started,
		-- the one a family, a crime, a cutscene or a building belongs to — or both heroes at once.
		-- ============================================================================
		local MSG = EMessageEventType or {}

		-- The two heroes, the one the AI runs `entity` for first (player one alone without a
		-- partner standing).
		function C.HeroesFor(entity)
			local one = C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman()
			if two == nil or not two:IsAlive() then
				return { one }
			end
			if C.AIHeroFor(entity) == two then
				return { two, one }
			end
			return { one, two }
		end

		-- A behaviour whose PrepareToRun tests "the hero" with no message naming one: it is tried
		-- as each hero, the AI's first, and runs as the one it accepted (stored in `field` when
		-- `store`). Only for a PrepareToRun whose refusals change nothing — each one listed here
		-- returns false before any write that matters.
		function C.PrepareAsEither(name, field, store)
			C.Wrap(name, "PrepareToRun", "Either", function(shipped)
				return function(self, ...)
					local heroes = C.HeroesFor(type(self) == "table" and self.Entity or nil)
					for i, hero in ipairs(heroes) do
						local results = { C.RunAs(hero, shipped, self, ...) }
						if results[1] or i == #heroes then
							if store and type(self) == "table" then
								self[field] = results[1] and hero or nil
							end
							return unpack(results, 1, table.maxn(results))
						end
					end
					return shipped(self, ...)
				end
			end)
		end

		-- Runs fn (which never yields) once as each standing hero, player one first; answers
		-- player one's results.
		function C.RunAsBoth(fn, ...)
			local results = { C.RunAs(C.ShippedGetPlayerHero(), fn, ...) }
			local two = C.ShippedGetPlayerHenchman()
			if two ~= nil and two:IsAlive() then
				C.RunAs(two, fn, ...)
			end
			return unpack(results, 1, table.maxn(results))
		end

		-- "NOBODY NEAR". While C.AllHeroesFar runs fn (which never yields), the distance tests
		-- IsDistanceBetweenThingAndPositionOver / IsDistanceBetweenThingsOver asked about a hero
		-- answer for both heroes: "over" only when every standing hero is over.
		C.FarDepth = C.FarDepth or 0
		function C.AllHeroesFar(fn, ...)
			C.FarDepth = C.FarDepth + 1
			local results = { pcall(fn, ...) }
			C.FarDepth = C.FarDepth - 1
			if not results[1] then
				error(results[2], 0)
			end
			return unpack(results, 2, table.maxn(results))
		end
		for _, name in ipairs({ "IsDistanceBetweenThingAndPositionOver", "IsDistanceBetweenThingsOver" }) do
			if type(_G[name]) == "function" and C["Shipped" .. name] == nil then
				local shipped = _G[name]
				C["Shipped" .. name] = shipped
				_G[name] = C.Permanent(function(a, b, ...)
					if C.FarDepth == 0 then
						return shipped(a, b, ...)
					end
					local one = C.ShippedGetPlayerHero()
					local two = C.ShippedGetPlayerHenchman()
					if two == nil or not two:IsAlive() then
						return shipped(a, b, ...)
					end
					if C.IsHero(a) then
						return shipped(one, b, ...) and shipped(two, b, ...)
					end
					if C.IsHero(b) then
						return shipped(a, one, ...) and shipped(a, two, ...)
					end
					return shipped(a, b, ...)
				end, name)
			end
		end

		-- NATIVES THAT READ "THE HERO" FROM PLAYER ONE'S SLOT. Villager.HasHeroCommittedCrimeIHaveDiscovered
		-- (`sub_827DDD40`: each discovered crime's criminal against players+108) and
		-- Villager.GetPerceivedRenownOfHero (`sub_827DEBE8`: the renown component of players+108)
		-- name no hero. The opinion scope for player two lends player two that slot for its length
		-- (mod_events.cpp OpinionHeroScope), so code acting for player two asks them inside it.
		for _, method in ipairs({ "HasHeroCommittedCrimeIHaveDiscovered", "GetPerceivedRenownOfHero" }) do
			if Villager ~= nil then
				C.Wrap("Villager", method, "Slot", function(shipped)
					return function(villager, ...)
						local two = C.ShippedGetPlayerHenchman()
						if villager == nil or two == nil or C.Acting() ~= two or Refii == nil or Refii._opinion_enter == nil
							or Refii._opinion_enter(Refii.handle(villager), Refii.handle(two)) ~= 1 then
							return shipped(villager, ...)
						end
						local results = { pcall(shipped, villager, ...) }
						Refii._opinion_leave()
						if not results[1] then
							error(results[2], 0)
						end
						return unpack(results, 2, table.maxn(results))
					end
				end, Villager)
			end
		end

		-- ---------------------------------------------------------------- interactive cutscenes
		-- ONE HERO PER INTERACTIVE CUTSCENE. Each actor's Update reaches the group mind
		-- (GetMyAction, SetAsActive, MoveToNextElement) under the AI rule — the hero nearer that
		-- actor — while the group mind's own Update runs from GroupMindManager:Update for player one,
		-- so one scene could grab one hero (Player.StartScriptControlMode, the cutscene rules, speed
		-- caps, cameras, microphone, HUD choices) and release or check the other. The scene holds
		-- one hero from SetAsActive (RefiiHero, saved with the group mind) until SetAsInactive has
		-- released everything it gave them; before that it is the hero in range (the scene's own
		-- IsPlayerInRange, asked as each hero), else the one nearer its main character. Every
		-- method of the group mind and of the actors' BehaviourCutscenePerformAction runs as it.
		local ICS = "InteractiveCutsceneGroupMind"
		C.CutsceneHero = C.Permanent(function(gm)
			local one = C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman()
			if type(gm) ~= "table" or two == nil then
				return one
			end
			if gm.RefiiHeroLatched then
				local held = gm.RefiiHero
				return (held ~= nil and held == two and two:IsAlive()) and two or one
			end
			local frame = Timing.GetWorldFrame()
			if gm.RefiiGuessFrame == frame and gm.RefiiGuess ~= nil then
				return gm.RefiiGuess
			end
			if C.CutsceneGuessing then
				return one
			end
			local class = _G[ICS]
			local in_range = class ~= nil and rawget(class, "RefiiShippedCutsceneIsPlayerInRange") or nil
			local main = class ~= nil and rawget(class, "RefiiShippedCutsceneGetMainCharacter") or nil
			C.CutsceneGuessing = true
			local ok, guess = pcall(function()
				if in_range ~= nil and C.RunAs(one, in_range, gm) then
					return one
				end
				if in_range ~= nil and two:IsAlive() and C.RunAs(two, in_range, gm) then
					return two
				end
				return main ~= nil and C.NearerPartner(main(gm)) or one
			end)
			C.CutsceneGuessing = false
			guess = (ok and guess ~= nil) and guess or one
			gm.RefiiGuess = guess
			gm.RefiiGuessFrame = frame
			return guess
		end, "CutsceneHero")
		C.CutsceneHeroOfAction = C.Permanent(function(behaviour)
			local gm = type(behaviour) == "table" and behaviour.MyGroupMind or nil
			return C.CutsceneHero(gm)
		end, "CutsceneHeroOfAction")

		if type(_G[ICS]) == "table" then
			local special = { SetAsActive = true, SetAsInactive = true, PauseAnyCrimeAI = true, RestartAnyCrimeAI = true }
			for _, method in ipairs(C.OwnMethods(_G[ICS])) do
				if not special[method] then
					C.Wrap(ICS, method, "Cutscene", function(shipped)
						return function(self, ...)
							return C.RunResolved(C.CutsceneHero, self, shipped, self, ...)
						end
					end)
				end
			end
			C.Wrap(ICS, "SetAsActive", "Cutscene", function(shipped)
				return function(self, ...)
					if type(self) == "table" and not self.Active and not self.RefiiHeroLatched then
						self.RefiiHero = C.CutsceneHero(self)
						self.RefiiHeroLatched = true
					end
					return C.RunResolved(C.CutsceneHero, self, shipped, self, ...)
				end
			end)
			-- Released once nothing given to the held hero is still out (script control, a custom
			-- pose, a point of interest): a looping background scene, or one paused because its hero
			-- walked off, passes to the hero who walks up next.
			C.Wrap(ICS, "SetAsInactive", "Cutscene", function(shipped)
				return function(self, ...)
					local results = { C.RunResolved(C.CutsceneHero, self, shipped, self, ...) }
					if type(self) == "table" and not self.Active and not self.GrabbedHero and not self.HeroInMode
						and not self.POITargetSet then
						self.RefiiHeroLatched = nil
						self.RefiiHero = nil
					end
					return unpack(results, 1, table.maxn(results))
				end
			end)
			-- A cutscene pauses and restarts the crime pursuit of both heroes.
			for _, method in ipairs({ "PauseAnyCrimeAI", "RestartAnyCrimeAI" }) do
				C.Wrap(ICS, method, "Cutscene", function(shipped)
					return function(self, ...)
						return C.RunAsBoth(shipped, self, ...)
					end
				end)
			end
		end
		if type(BehaviourCutscenePerformAction) == "table" then
			for _, method in ipairs(C.OwnMethods(BehaviourCutscenePerformAction)) do
				C.Wrap("BehaviourCutscenePerformAction", method, "Cutscene", function(shipped)
					return function(self, ...)
						if type(self) ~= "table" or type(self.MyGroupMind) ~= "table" then
							return shipped(self, ...)
						end
						return C.RunResolved(C.CutsceneHeroOfAction, self, shipped, self, ...)
					end
				end)
			end
		end

		-- A CUTSCENE SPEAKER answers the hero who hit it or performed at it
		-- (BehaviourCutsceneSpeakerIdle:Update compares each message's sender with the hero).
		C.SpeakerHero = C.Permanent(function(self)
			if type(self) ~= "table" or self.Entity == nil then
				return nil
			end
			return C.HeroSentTo(MSG.MESSAGE_EVENT_HIT, self.Entity, self.LastReactionMessage)
				or C.HeroSentTo(MSG.MESSAGE_EVENT_EXPRESSION_PERFORMED, self.Entity, self.LastReactionMessage)
				or C.AIHeroFor(self.Entity) or C.ShippedGetPlayerHero()
		end, "SpeakerHero")
		C.Wrap("BehaviourCutsceneSpeakerIdle", "Update", "Cutscene", function(shipped)
			return function(self, ...)
				return C.RunResolved(C.SpeakerHero, self, shipped, self, ...)
			end
		end)

		-- ---------------------------------------------------------------- allies and fights
		-- An ally follows the hero whose follow group it joined (PrepareToRun attached it to that
		-- hero's FollowGroupMind; Update asks the hero again for its formation slot).
		C.AllyLeader = C.Permanent(function(self)
			local gm = type(self) == "table" and self.MyGroupMind or nil
			local leader = type(gm) == "table" and gm.EntityAttachedTo or nil
			return C.IsHero(leader) and leader or nil
		end, "AllyLeader")
		C.Wrap("BehaviourAllyFollowHero", "Update", "Ally", function(shipped)
			return function(self, ...)
				if C.AllyLeader(self) == nil then
					return shipped(self, ...)
				end
				return C.RunResolved(C.AllyLeader, self, shipped, self, ...)
			end
		end)
		-- Whether a villager keeps fighting is about the hero it fights.
		C.WrapAs("BehaviourOpinionBasedAttack", { "IsStillRunnable" }, "Target", function(self)
			local target = type(self) == "table" and self.Target or nil
			return C.IsHero(target) and target or nil
		end)

		-- ---------------------------------------------------------------- homes
		-- ASKED TO LEAVE: either hero inside counts (the trespasser), and the vigilante, the fine
		-- and the crime are that hero's (self.Hero, which the shipped PrepareToRun stores).
		C.PrepareAsEither("BehaviourHomeAskHeroToLeave", "Hero", false)
		C.PrepareAsEither("BehaviourShopKeeperAskHeroToLeave", "Hero", false)
		C.LatchAs("BehaviourHomeAskHeroToLeave", { "Update", "IsStillRunnable", "FastExit" }, "Hero", nil, false)
		C.LatchAs("BehaviourShopKeeperAskHeroToLeave", { "IsStillRunnable" }, "Hero", nil, false)

		-- A HOUSE'S HERO is its owner (PlayerProperties.IsOwnedByPlayer reads the properties
		-- component of the hero it is given, `sub_82197CE0`), else the hero the AI runs the house
		-- for: its sims do not lock a hero out of their own home.
		function C.HouseHero(gm)
			local house = type(gm) == "table" and gm.EntityAttachedTo or nil
			local one = C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman()
			if house == nil or two == nil or PlayerProperties == nil then
				return nil
			end
			if two:IsAlive() and PlayerProperties.IsOwnedByPlayer(two, house) then
				return two
			end
			if one ~= nil and PlayerProperties.IsOwnedByPlayer(one, house) then
				return one
			end
			return C.AIHeroFor(house) or one
		end
		C.WrapAs("HomeGroupMind", { "Update", "IncrementNumberOfPeopleToCloseAndLockDoor", "DecrementNumberOfPeopleToCloseAndLockDoor" },
			"House", C.HouseHero)

		-- ---------------------------------------------------------------- reactions
		-- AN INTERROGATION IS ANSWERED TO THE INTERROGATOR: the micro-reaction comment
		-- (BehaviourBase:CommentBasedOnOpinionCheck) and a baby's reaction checks face, greet and
		-- judge the hero whose interaction they are handling, else the hero the AI runs for — one
		-- hero for the whole comment, which turns to them over several seconds.
		if MSG.MESSAGE_EVENT_INTERACTED_WITH ~= nil then
			C.Wrap("BehaviourBase", "CommentBasedOnOpinionCheck", "Micro", function(shipped)
				return function(self, ...)
					if type(self) ~= "table" or self.Entity == nil or C.ShippedGetPlayerHenchman() == nil then
						return shipped(self, ...)
					end
					local hero = C.HeroSentTo(MSG.MESSAGE_EVENT_INTERACTED_WITH, self.Entity, self.LastInterrogationMessage)
						or C.AIHeroFor(self.Entity) or C.ShippedGetPlayerHero()
					return C.RunAs(hero, shipped, self, ...)
				end
			end)
			C.Wrap("BehaviourBaby", "BasicReactionChecks", "Micro", function(shipped)
				return function(self, ...)
					local hero = type(self) == "table"
						and C.HeroSentTo(MSG.MESSAGE_EVENT_INTERACTED_WITH, self.Entity, self.BabyLastInterrogationMessage) or nil
					return C.RunAs(hero, shipped, self, ...)
				end
			end)
			-- Interacting with a villager: the whole reaction is for the interactor (who courts,
			-- gossips, is told about the rent), latched when it is picked.
			C.LatchAs("BehaviourReactOnInteraction", { "PrepareToRun", "Update", "FastExit" }, "RefiiActor", function(self)
				return type(self) == "table" and C.HeroSentTo(MSG.MESSAGE_EVENT_INTERACTED_WITH, self.Entity, nil) or nil
			end, true)
			-- A barman's job subgame is served by the hero who interacted (the jug, the pour, the tip).
			C.Wrap("BehaviourBarmanJobBase", "CheckForInteractionMessages", "Server", function(shipped)
				return function(self, ...)
					local server = type(self) == "table"
						and C.HeroSentTo(MSG.MESSAGE_EVENT_INTERACTED_WITH, self.Entity, self.LastMessageID_InteractedWith) or nil
					return C.RunAs(server, shipped, self, ...)
				end
			end)
		end

		-- The crowd an expression drew is the performer's: it is counted down for the hero it was
		-- counted up for (ExpressionPerformer.IncrementCrowdSize / DecrementCrowdSize).
		C.Wrap("BehaviourBase", "SetMicroReactionLabelAndPriorities", "Crowd", function(shipped)
			return function(self, label, ...)
				if type(self) ~= "table" or C.ShippedGetPlayerHenchman() == nil then
					return shipped(self, label, ...)
				end
				if label == "Notice Expression" then
					self.RefiiCrowdHero = GetPlayerHero()
					return shipped(self, label, ...)
				end
				if self.MicroReactLabel == "Notice Expression" and C.IsHero(self.RefiiCrowdHero) then
					local hero = self.RefiiCrowdHero
					self.RefiiCrowdHero = nil
					return C.RunAs(hero, shipped, self, label, ...)
				end
				return shipped(self, label, ...)
			end
		end)
		C.Wrap("BehaviourBase", "GenericFastExit", "Crowd", function(shipped)
			return function(self, ...)
				if type(self) == "table" and self.MicroReactLabel == "Notice Expression" and C.IsHero(self.RefiiCrowdHero) then
					local hero = self.RefiiCrowdHero
					self.RefiiCrowdHero = nil
					return C.RunAs(hero, shipped, self, ...)
				end
				return shipped(self, ...)
			end
		end)

		-- Extortion is the extorter's: their money, their crime.
		C.WrapAs("BehaviourRespondToExtortion", nil, "Extort", function(self, method)
			if type(self) ~= "table" then
				return nil
			end
			if method == "PrepareToRun" then
				return C.HeroSentTo(MSG.MESSAGE_EVENT_EXPRESSION_PERFORMED, self.Entity, nil)
			end
			local sender = C.SenderOf(self.Message)
			return C.IsHero(sender) and sender or nil
		end)

		-- A hit is the hitter's: from either hero it is noticed (and consumed) as theirs, and the
		-- reaction that follows is to them.
		C.Wrap("BehaviourReactToAbuse", "IsHitByPlayer", "Abuse", function(shipped)
			return function(self, ...)
				local hero = type(self) == "table" and C.HeroSentTo(MSG.MESSAGE_EVENT_HIT, self.Entity, self.LastMessageID_Hit) or nil
				if hero ~= nil then
					self.RefiiHitter = hero
				end
				return C.RunAs(hero, shipped, self, ...)
			end
		end)
		C.LatchAs("BehaviourReactToAbuse", { "Update", "ReactToAbuse" }, "RefiiHitter", nil, false)

		-- A deed is judged as its doer's.
		C.WrapAs("BehaviourReactToDeed", { "PerformDeedReaction" }, "Deed", function(self, method, message)
			local sender = C.SenderOf(message)
			return C.IsHero(sender) and sender or nil
		end)
		-- A reaction faces the hero it is to (InteractedWithHero is that hero's record).
		C.WrapAs("ReactionHelpers", { "PerformReactionNoWait" }, "Face", function(self, method, behaviour, record, can_speak,
			just_talk, entity_to_face)
			return C.IsHero(entity_to_face) and entity_to_face or nil
		end)
		-- A reaction to "the henchman" is a reaction to the other hero, judged by that hero's opinion.
		C.Wrap("BehaviourOpinionReaction", "GetReaction", "Henchman", function(shipped)
			return function(self, entity, target, ...)
				if EOpinionTarget ~= nil and target == EOpinionTarget.EOT_HENCHMAN
					and not (Multiplayer ~= nil and Multiplayer.IsHenchmanInGhostMode()) then
					local other = GetPlayerHenchman()
					if other ~= nil and other:IsAlive() then
						return C.RunAs(other, shipped, self, entity, EOpinionTarget.EOT_PLAYER, ...)
					end
				end
				return shipped(self, entity, target, ...)
			end
		end)
		-- A dog's trick is noticed as its master's dog's (GetDog() is the acting hero's dog).
		C.LatchAs("BehaviourNoticeDogTricks", { "PrepareToRun", "Update", "SlowExit" }, "RefiiHero", function(self)
			if type(self) ~= "table" or self.Entity == nil then
				return nil
			end
			local posted, message = MessageEvents.IsMessageSentTo(MSG.MESSAGE_EVENT_EXPRESSION_PERFORMED, self.Entity,
				self.LastMessageID_ExpressionPerformed)
			local dog = posted and C.SenderOf(message) or nil
			if dog == nil or not IsDog(dog) then
				return nil
			end
			local master = Dog.GetMaster(dog)
			return C.IsHero(master) and master or nil
		end, true)

		-- Tried as each hero: a known person's reaction to what a hero did nearby, an apology,
		-- running away from a frightening hero.
		for _, name in ipairs({ "BehaviourReactToKnownPersonInteraction", "BehaviourAcceptApology", "BehaviourAcceptApologyVigilante" }) do
			C.PrepareAsEither(name, "RefiiHero", true)
			C.LatchAs(name, { "Update" }, "RefiiHero", nil, false)
		end
		C.PrepareAsEither("BehaviourRunAway", "FleeFrom", false)
		C.LatchAs("BehaviourRunAway", { "IsStillRunnable", "Update" }, "FleeFrom", nil, false)
		-- A villager killed by a hero is not a reason to flee "from an enemy" for either hero.
		C.WrapAs("BehaviourScriptControlFlee", { "PrepareToRun" }, "Flee", function(self)
			local village = type(self) == "table" and Villager.IsAvailable(self.Entity) and Villager.GetVillage(self.Entity) or nil
			return village ~= nil and C.HeroSentTo(MSG.MESSAGE_EVENT_VILLAGER_KILLED_BY_ENEMY, village, nil) or nil
		end)
		-- Sex is avoided around the hero having it.
		C.WrapAs("BehaviourAvoidSex", nil, "Sex", function()
			if C.SexHero ~= nil and C.SexHero:IsAlive() then
				return C.SexHero
			end
			local two = C.ShippedGetPlayerHenchman()
			if two ~= nil and two:IsAlive() and Follow ~= nil and Follow.IsBeingFollowedBySexPartner(two) then
				return two
			end
			return C.ShippedGetPlayerHero()
		end)

		-- A title bought from the town crier is announced for its buyer (the crier's last
		-- customer, kept by BehaviourTownCrierOpenGUI above).
		C.WrapAs("BehaviourAnnounceHeroTitle", { "PrepareToRun", "Update" }, "Title", function(self)
			local customer = type(self) == "table" and self.Entity ~= nil and Refii ~= nil and C.CrierCustomer[Refii.handle(self.Entity)] or nil
			return C.IsHero(customer) and customer or nil
		end)

		-- ---------------------------------------------------------------- crime
		-- THE CRIMINAL. A victim, a witness or a vigilante responds to the hero who is the
		-- village's criminal (VillageCrimeManager.IsCriminal answers for the hero it is given); both
		-- criminals, the one the AI runs for.
		function C.CriminalFor(village, entity)
			local one = C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman()
			if village == nil or two == nil or VillageCrimeManager == nil then
				return nil
			end
			local first = one ~= nil and VillageCrimeManager.IsCriminal(village, one)
			local second = two:IsAlive() and VillageCrimeManager.IsCriminal(village, two)
			if first and second then
				return C.AIHeroFor(entity) or one
			end
			return (first and one) or (second and two) or nil
		end
		for _, name in ipairs({ "BehaviourRespondToCrime", "BehaviourRespondToCrimeVictim", "BehaviourRespondToCrimeWitness",
			"BehaviourRespondToCrimeVigilante" }) do
			C.LatchAs(name, C.OwnMethods(_G[name]), "RefiiCriminal", function(self, ptr_data)
				if type(self) ~= "table" or self.Entity == nil then
					return nil
				end
				local village = type(ptr_data) == "table" and ptr_data.Village or nil
				if village == nil and Villager.IsAvailable(self.Entity) then
					village = Villager.GetVillage(self.Entity)
				end
				return C.CriminalFor(village, self.Entity)
			end, true)
		end

		-- GUARDS AND VILLAGES. GuardGroupMind:Update attacks "the hero" when resisting arrest and
		-- nobody is being attacked yet: each resisting hero is attacked. Attacking or letting off a
		-- hero takes that hero's own dog and leaves the other hero alone (the crime is the
		-- criminal's; shipped, the henchman and the one dog were accomplices).
		C.Wrap("GuardGroupMind", "Update", "Crime", function(shipped)
			return function(self, ...)
				local results = { C.RunAs(C.ShippedGetPlayerHero(), shipped, self, ...) }
				local village = type(self) == "table" and self.EntityAttachedTo or nil
				local two = C.ShippedGetPlayerHenchman()
				if village ~= nil and two ~= nil and type(self.CriminalsAttacking) == "table" and C.ShippedIsResistingArrest ~= nil then
					for _, hero in ipairs({ C.ShippedGetPlayerHero(), two }) do
						local listed = false
						for _, criminal in pairs(self.CriminalsAttacking) do
							if criminal == hero then
								listed = true
							end
						end
						if not listed and hero:IsAlive() and C.ShippedIsResistingArrest(village, hero) then
							self:AttackCriminal(hero)
						end
					end
				end
				return unpack(results, 1, table.maxn(results))
			end
		end)
		for _, method in ipairs({ "AttackCriminal", "StopAttackingCriminal" }) do
			C.Wrap("GuardGroupMind", method, "Crime", function(shipped)
				return function(self, criminal, ...)
					if C.ShippedGetPlayerHenchman() == nil or not C.IsHero(criminal) then
						return shipped(self, criminal, ...)
					end
					return C.HideHenchman(C.WithDogOf, criminal, shipped, self, criminal, ...)
				end
			end)
		end
		-- A village spawns its transients and guards out of sight of both heroes, and a guard
		-- spawned while a hero resists arrest goes for that hero.
		C.Wrap("VillageGroupMind", "Update", "Crime", function(shipped)
			return function(self, ...)
				local two = C.ShippedGetPlayerHenchman()
				local village = type(self) == "table" and self.EntityAttachedTo or nil
				if two == nil or village == nil or C.ShippedIsResistingArrest == nil then
					return shipped(self, ...)
				end
				local one = C.ShippedGetPlayerHero()
				local first = one ~= nil and C.ShippedIsResistingArrest(village, one)
				local second = two:IsAlive() and C.ShippedIsResistingArrest(village, two)
				local hero = (second and not first) and two or one
				if first and second then
					return C.RunAs(hero, C.AllHeroesFar, shipped, self, ...)
				end
				return C.RunAs(hero, C.HideHenchman, C.AllHeroesFar, shipped, self, ...)
			end
		end)
		-- A farmer's crate goes home unseen only when both heroes are far from it.
		C.Wrap("FarmGroupMind", "TidyCrates", "Far", function(shipped)
			return function(self, ...)
				return C.AllHeroesFar(shipped, self, ...)
			end
		end)

		-- ---------------------------------------------------------------- despawning
		-- AN EVICTED VILLAGER OR A SPOUSE LEAVING is removed only where neither hero sees it
		-- (`not IsEntityVisibleOnScreen(e) and IsDistanceBetweenThingsOver(e, hero, 30)`): under
		-- C.DespawnWatch, IsEntityVisibleOnScreen also answers for player two's view
		-- (Refii._partner_screen) and for either hero within 30 m.
		C.DespawnWatch = C.DespawnWatch or setmetatable({}, { __mode = "k" })
		if IsEntityVisibleOnScreen ~= nil and C.ShippedIsEntityVisibleOnScreen == nil then
			C.ShippedIsEntityVisibleOnScreen = IsEntityVisibleOnScreen
			IsEntityVisibleOnScreen = C.Permanent(function(entity, ...)
				local shipped = C.ShippedIsEntityVisibleOnScreen
				local co = coroutine.running()
				local two = C.ShippedGetPlayerHenchman()
				if co == nil or not C.DespawnWatch[co] or entity == nil or two == nil then
					return shipped(entity, ...)
				end
				if shipped(entity, ...) then
					return true
				end
				if Refii ~= nil and Refii._partner_screen ~= nil then
					Refii._partner_screen(true)
					local ok, seen = pcall(shipped, entity, ...)
					Refii._partner_screen(false)
					if ok and seen then
						return true
					end
				end
				for _, hero in ipairs({ C.ShippedGetPlayerHero(), two }) do
					if hero:IsAlive() and GetDistanceBetweenEntities(hero, entity) < 30 then
						return true
					end
				end
				return false
			end, "IsEntityVisibleOnScreen")
		end
		for _, name in ipairs({ "BehaviourEvicted", "BehaviourSpouseLeaveHero" }) do
			C.Wrap(name, "Update", "Despawn", function(shipped)
				return function(self, ...)
					local co = coroutine.running()
					if co == nil then
						return shipped(self, ...)
					end
					local before = C.DespawnWatch[co]
					C.DespawnWatch[co] = true
					local results = { shipped(self, ...) }
					C.DespawnWatch[co] = before
					return unpack(results, 1, table.maxn(results))
				end
			end)
		end

		-- A PROSTITUTE'S THUMBS-UP/DOWN SUGGESTIONS went to whichever hero was nearer when added;
		-- they are taken off both heroes' HUDs.
		C.HudBoth = C.HudBoth or setmetatable({}, { __mode = "k" })
		if Player ~= nil then
			C.Wrap("Player", "RemoveHUDSuggestionOfType", "Both", function(shipped)
				return function(hero, ...)
					local results = { shipped(hero, ...) }
					local co = coroutine.running()
					if co ~= nil and C.HudBoth[co] and C.IsHero(hero) then
						local other = C.Other(hero)
						if other ~= nil and other:IsAlive() then
							shipped(other, ...)
						end
					end
					return unpack(results, 1, table.maxn(results))
				end
			end, Player)
		end
		C.Wrap("BehaviourWhoreIdle", "Update", "Both", function(shipped)
			return function(self, ...)
				local co = coroutine.running()
				if co == nil then
					return shipped(self, ...)
				end
				C.HudBoth[co] = true
				local results = { shipped(self, ...) }
				C.HudBoth[co] = nil
				return unpack(results, 1, table.maxn(results))
			end
		end)
		C.Wrap("BehaviourWhoreIdle", "FastExit", "Both", function(shipped)
			return function(self, ...)
				return C.RunAsBoth(shipped, self, ...)
			end
		end)

		-- ---------------------------------------------------------------- jobs, shops, followers
		-- A follower invited for sex follows the hero whose seduction it accepted.
		if OpinionReaction ~= nil and type(OpinionReaction.SetFollowForSex) == "function" then
			C.Wrap("OpinionReaction", "SetFollowForSex", "Inviter", function(shipped)
				return function(villager, on, ...)
					if villager ~= nil and Refii ~= nil and Refii.handle ~= nil then
						C.SexInviter[Refii.handle(villager)] = on and (C.Acting() or C.ShippedGetPlayerHero()) or nil
					end
					return shipped(villager, on, ...)
				end
			end, OpinionReaction)
		end

		-- Behaviour groups only a hero's spouse or child runs: either hero's. The home emergency
		-- (calling the guards on an intruder) spares either hero's family.
		local groups = AIManager ~= nil and AIManager.BehaviourGroupPrepareToRuns or nil
		if type(groups) == "table" then
			for _, name in ipairs({ "SpouseBehaviourGroup", "SpouseMiscBehaviourGroup", "HerosChildBehaviourGroup" }) do
				C.Wrap("BehaviourGroupPrepareToRuns", name, "Either", function(shipped)
					return function(entity, ...)
						local two = C.ShippedGetPlayerHenchman()
						local results = { C.RunAs(C.ShippedGetPlayerHero(), shipped, entity, ...) }
						if results[1] or two == nil or not two:IsAlive() then
							return unpack(results, 1, table.maxn(results))
						end
						return C.RunAs(two, shipped, entity, ...)
					end
				end, groups)
			end
			C.Wrap("BehaviourGroupPrepareToRuns", "HomeEmergencyBehaviourGroup", "Both", function(shipped)
				return function(entity, ...)
					local two = C.ShippedGetPlayerHenchman()
					if two == nil or not two:IsAlive() then
						return shipped(entity, ...)
					end
					local results = { C.RunAs(C.ShippedGetPlayerHero(), shipped, entity, ...) }
					if not results[1] or not C.RunAs(two, shipped, entity, ...) then
						return false
					end
					return unpack(results, 1, table.maxn(results))
				end
			end, groups)
		end

		-- A bigamy chat: both chatters are asked about the same hero, each hero in turn.
		C.Wrap("BehaviourVillagerChat", "IsThisADivorceConversation", "Either", function(shipped)
			return function(self, partner, ...)
				local two = C.ShippedGetPlayerHenchman()
				local results = { C.RunAs(C.ShippedGetPlayerHero(), shipped, self, partner, ...) }
				if results[1] or two == nil or not two:IsAlive() then
					return unpack(results, 1, table.maxn(results))
				end
				return C.RunAs(two, shipped, self, partner, ...)
			end
		end)

		-- A hero-owned business: its keeper's and patrons' lines about the owner are for its owner.
		function C.HeroOwner(building)
			local owner = building ~= nil and Building ~= nil and Building.GetBuildingOwner(building) or nil
			return C.IsHero(owner) and owner or nil
		end
		local function OwnerOfGroup(self)
			local gm = type(self) == "table" and self.MyGroupMind or nil
			return C.HeroOwner(type(gm) == "table" and gm.EntityAttachedTo or nil)
		end
		for _, name in ipairs({ "BehaviourShopKeeperHawk", "BehaviourStallVendorAttractMode", "BehaviourStallPatronGotoStall" }) do
			C.WrapAs(name, { "Update" }, "Owner", OwnerOfGroup)
		end
		C.WrapAs("BehaviourShopPatronCommentOnPrice", { "PrepareToRun" }, "Owner", function(self)
			if type(self) ~= "table" or GroupMind == nil or GroupMind.Types == nil then
				return nil
			end
			local gm = GroupMindManager:GetGroupMind(GroupMind.Types.ShopGroupMind, self.Entity)
			return C.HeroOwner(type(gm) == "table" and gm.EntityAttachedTo or nil)
		end)

		-- A villager leading a hero watches the hero it leads (self.Player, latched at PrepareToRun).
		C.LatchAs("BehaviourLeadHero", { "Update" }, "Player", nil, false)

		-- A TRAVELLING TRADER answers either hero targeting it. Targeted.IsTargetLockedByHero
		-- tests player one's targeting only (`sub_822950B0`: the hero at players+108); player two's
		-- target is their Targeting.GetTarget.
		function C.TraderTargeted(entity)
			if entity == nil or Targeted == nil then
				return false
			end
			if Targeted.IsTargetLockedByHero(entity) or Targeted.IsTargetedByHero(entity) then
				return true
			end
			local two = C.ShippedGetPlayerHenchman()
			return two ~= nil and two:IsAlive() and Targeting.GetTarget(two) == entity
		end
		C.TraderTargeter = C.Permanent(function(self)
			local entity = type(self) == "table" and self.Entity or nil
			local one = C.ShippedGetPlayerHero()
			local two = C.ShippedGetPlayerHenchman()
			if entity ~= nil and two ~= nil and two:IsAlive() and Targeting.GetTarget(two) == entity
				and Targeting.GetTarget(one) ~= entity then
				return two
			end
			return one
		end, "TraderTargeter")
		C.TraderUpdate = C.Permanent(function(self)
			self:LookAtEntity(GetPlayerHero(), ELookAtPriority.NORMAL_PRIORITY, ELookAt.WITH_ALL)
			while C.TraderTargeted(self.Entity) do
				self:CommentBasedOnOpinionCheck()
				coroutine.yield()
			end
		end, "TraderUpdate")
		C.Wrap("BehaviourTravellingTraderTargeted", "PrepareToRun", "Trader", function()
			return function(self)
				return C.TraderTargeted(type(self) == "table" and self.Entity or nil)
			end
		end)
		C.Wrap("BehaviourTravellingTraderTargeted", "IsStillRunnable", "Trader", function()
			return function(self)
				return C.TraderTargeted(type(self) == "table" and self.Entity or nil)
			end
		end)
		C.Wrap("BehaviourTravellingTraderTargeted", "Update", "Trader", function()
			return function(self)
				return C.RunResolved(C.TraderTargeter, self, C.TraderUpdate, self)
			end
		end)

		-- A closed shop's doors: the hero inside is the one asked whether they are in or out.
		C.Wrap("ShopGroupMind", "Update", "Inside", function(shipped)
			return function(self, ...)
				local shop = type(self) == "table" and self.EntityAttachedTo or nil
				local one = C.ShippedGetPlayerHero()
				local two = C.ShippedGetPlayerHenchman()
				local inside = shop ~= nil and two ~= nil and two:IsAlive() and Whereabouts.GetBuildingWeAreInsideOf(two) == shop
					and Whereabouts.GetBuildingWeAreInsideOf(one) ~= shop
				return C.RunAs(inside and two or one, shipped, self, ...)
			end
		end)

		-- ---------------------------------------------------------------- player two's player state
		-- PLAYER TWO IS A PLAYER ON THE HOST. Online, the host's copy of player two's body has no
		-- Player component (Player.IsAvailable false, IsPlayer true), so shipped fights treated
		-- player two as a non-player: a beetle kept attacking a hero in interaction mode
		-- (CombatSequences BeetleCloseAttack/BeetleAttack, CombatStates:BeetleSpit), a beetle's hug
		-- could not be shaken off (CombatStates:BeetleHug), a hobbe caster in chaos would not heal
		-- them (CombatStates:SpellCasterHeal), and the child rule applied to them
		-- (CombatRegister:IsValidTarget). The joiner, which plays player two, states that hero's
		-- player modes in the player store (coop.mode.aim_ranged, coop.mode.interaction) and its
		-- button presses while in interaction mode (coop.mode.bash); the host commands its
		-- interaction mode there (coop.cmd.interaction, odd on, even off, acknowledged in
		-- coop.mode.ack). Those functions read Player and MessageEvents through views that answer
		-- for player two from the store (their environment, setfenv: the rest of the game sees the
		-- real tables).
		function C.StatedPlayer(entity)
			local two = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			return entity ~= nil and two ~= nil and entity == two and Refii ~= nil and Refii.player_value ~= nil
				and not Player.IsAvailable(entity) and entity:IsAlive()
		end
		function C.InteractionCommand()
			return C.InteractionCmd or Refii.player_value(2, "coop.cmd.interaction", 0)
		end
		function C.StatedMode(entity, mode)
			if EPlayerMode == nil then
				return false
			end
			if mode == EPlayerMode.PLAYER_MODE_INTERACTION then
				local command = C.InteractionCommand()
				if Refii.player_value(2, "coop.mode.ack", 0) ~= command then
					return command % 2 == 1
				end
				return Refii.player_value(2, "coop.mode.interaction", 0) ~= 0
			end
			if mode == EPlayerMode.PLAYER_MODE_AIM_RANGED then
				return Refii.player_value(2, "coop.mode.aim_ranged", 0) ~= 0
			end
			return false
		end
		function C.CommandInteraction(on)
			local command = C.InteractionCommand()
			if (command % 2 == 1) ~= on then
				command = command + 1
				C.InteractionCmd = command
				Refii.player_set(2, "coop.cmd.interaction", command)
			end
			if on then
				C.BashSeen = Refii.player_value(2, "coop.mode.bash", 0)
			end
		end

		C.BashKinds = { "MESSAGE_EVENT_INTERACTED_A_BUTTON", "MESSAGE_EVENT_INTERACTED_B_BUTTON",
			"MESSAGE_EVENT_INTERACTED_X_BUTTON", "MESSAGE_EVENT_INTERACTED_Y_BUTTON" }
		C.BashKind = {}
		for _, kind in ipairs(C.BashKinds) do
			if MSG[kind] ~= nil then
				C.BashKind[MSG[kind]] = true
			end
		end
		C.BashMessage = C.BashMessage or { GetID = C.Permanent(function() return 0 end, "BashMessage.GetID") }

		C.PlayerView = C.PlayerView or setmetatable({}, { __index = Player })
		C.PlayerView.IsAvailable = C.Permanent(function(entity, ...)
			return Player.IsAvailable(entity, ...) or C.StatedPlayer(entity)
		end, "PlayerView.IsAvailable")
		C.PlayerView.HasPlayerMode = C.Permanent(function(entity, mode, ...)
			if C.StatedPlayer(entity) then
				return C.StatedMode(entity, mode)
			end
			return Player.HasPlayerMode(entity, mode, ...)
		end, "PlayerView.HasPlayerMode")
		C.PlayerView.StartInteractionMode = C.Permanent(function(entity, ...)
			if C.StatedPlayer(entity) then
				return C.CommandInteraction(true)
			end
			return Player.StartInteractionMode(entity, ...)
		end, "PlayerView.StartInteractionMode")
		C.PlayerView.StopInteractionMode = C.Permanent(function(entity, ...)
			if C.StatedPlayer(entity) then
				return C.CommandInteraction(false)
			end
			return Player.StopInteractionMode(entity, ...)
		end, "PlayerView.StopInteractionMode")
		C.MessageView = C.MessageView or setmetatable({}, { __index = MessageEvents })
		C.MessageView.IsMessageSentBy = C.Permanent(function(kind, entity, ...)
			if kind ~= nil and C.BashKind[kind] and C.StatedPlayer(entity) then
				local bashes = Refii.player_value(2, "coop.mode.bash", 0)
				C.BashSeen = C.BashSeen or bashes
				if bashes > C.BashSeen then
					C.BashSeen = C.BashSeen + 1
					return true, C.BashMessage
				end
				return false
			end
			return MessageEvents.IsMessageSentBy(kind, entity, ...)
		end, "MessageView.IsMessageSentBy")
		C.PlayerEnv = C.PlayerEnv or setmetatable({}, { __index = _G, __newindex = _G })
		rawset(C.PlayerEnv, "Player", C.PlayerView)
		rawset(C.PlayerEnv, "MessageEvents", C.MessageView)
		for _, owner in ipairs({ { CombatStates, "BeetleSpit" }, { CombatStates, "BeetleHug" }, { CombatStates, "SpellCasterHeal" },
			{ CombatRegister, "IsValidTarget" } }) do
			local fn = type(owner[1]) == "table" and owner[1][owner[2]] or nil
			if type(fn) == "function" and setfenv ~= nil then
				pcall(setfenv, fn, C.PlayerEnv)
			end
		end
		-- The beetles' melee sequences hold their "target in interaction mode" test inside their
		-- IsValid (a file-local predicate): a stated player in interaction mode refuses them too.
		if type(CombatSequences) == "table" and EPlayerMode ~= nil then
			for _, name in ipairs({ "BeetleCloseAttack", "BeetleAttack" }) do
				local sequence = CombatSequences[name]
				if type(sequence) == "table" and type(sequence.IsValid) == "function" then
					if rawget(sequence, "RefiiShippedIsValid") == nil then
						rawset(sequence, "RefiiShippedIsValid", sequence.IsValid)
					end
					local shipped = rawget(sequence, "RefiiShippedIsValid")
					sequence.IsValid = C.Permanent(function(combat_info)
						if not shipped(combat_info) then
							return false
						end
						local target = type(combat_info) == "table" and combat_info.Target or nil
						return not (C.StatedPlayer(target) and C.StatedMode(target, EPlayerMode.PLAYER_MODE_INTERACTION))
					end, "Sequence." .. name .. ".IsValid")
				end
			end
		end

		-- This machine plays player two online and the host runs the world (Refii.world_here).
		function C.IsJoiner()
			if Refii == nil then
				return false
			end
			if Refii.world_here ~= nil then
				return not Refii.world_here()
			end
			return Refii.coop_mode ~= nil and Refii.coop_mode() == 3
		end

		-- The joiner's side, once a world frame (from the camera's FrameUpdate).
		C.ModeSent = C.ModeSent or {}
		C.ButtonLast = C.ButtonLast or {}
		function C.StatePlayerModes()
			if not C.IsJoiner() or Refii.player_set == nil or EPlayerMode == nil then
				return
			end
			local frame = Timing.GetWorldFrame()
			if C.ModeFrame == frame then
				return
			end
			C.ModeFrame = frame
			local hero = C.ShippedGetPlayerHenchman and C.ShippedGetPlayerHenchman()
			if hero == nil or not hero:IsAlive() or not Player.IsAvailable(hero) then
				return
			end
			local function State(key, value)
				if C.ModeSent[key] ~= value then
					C.ModeSent[key] = value
					Refii.player_set(2, key, value)
				end
			end
			-- A command from before this machine joined is acknowledged, not replayed.
			local command = Refii.player_value(2, "coop.cmd.interaction", 0)
			if C.CommandApplied == nil then
				C.CommandApplied = command
			elseif command ~= C.CommandApplied then
				C.CommandApplied = command
				if command % 2 == 1 then
					Player.StartInteractionMode(hero)
				else
					Player.StopInteractionMode(hero)
				end
			end
			local interacting = Player.HasPlayerMode(hero, EPlayerMode.PLAYER_MODE_INTERACTION) == true
			State("coop.mode.aim_ranged", Player.HasPlayerMode(hero, EPlayerMode.PLAYER_MODE_AIM_RANGED) and 1 or 0)
			State("coop.mode.interaction", interacting and 1 or 0)
			if interacting then
				if not C.WasInteracting then
					local newest = MessageEvents.GetMostRecentMessageID()
					for _, kind in ipairs(C.BashKinds) do
						C.ButtonLast[kind] = newest
					end
				end
				for _, kind in ipairs(C.BashKinds) do
					if MSG[kind] ~= nil then
						local posted, message = MessageEvents.IsMessageSentBy(MSG[kind], hero, C.ButtonLast[kind])
						if posted then
							C.ButtonLast[kind] = message:GetID()
							C.Bashes = (C.Bashes or 0) + 1
						end
					end
				end
				State("coop.mode.bash", C.Bashes or 0)
			end
			C.WasInteracting = interacting
			State("coop.mode.ack", command)
		end

		-- ============================================================================
		-- THE PLAYER-AGNOSTIC PRIMITIVES, the mod API's core: the co-op layer above and the mods
		-- use the same calls (the Godot and native API state the same names). Refii.player_for is
		-- native; these are defined here only where no native of the name exists.
		--   Refii.acting_player()            1 or 2: who GetPlayerHero() means right now.
		--   Refii.run_as_player(p, fn, ...)  fn with GetPlayerHero()/GetPlayerHenchman()/
		--                                    QuestManager.HeroEntity as player p.
		--   Refii.combat_state(p)            midst, close_to_combat, in_any_combat for player p.
		-- ============================================================================
		if Refii ~= nil then
			if Refii.acting_player == nil then
				Refii.acting_player = C.Permanent(function()
					return C.Acting() ~= nil and 2 or 1
				end, "Api.acting_player")
			end
			if Refii.run_as_player == nil then
				Refii.run_as_player = C.Permanent(function(player, fn, ...)
					local hero = player == 2 and C.ShippedGetPlayerHenchman() or C.ShippedGetPlayerHero()
					return C.RunAs(hero, fn, ...)
				end, "Api.run_as_player")
			end
			if Refii.combat_state == nil then
				Refii.combat_state = C.Permanent(function(player)
					local p = player == 2 and 2 or 1
					if p == 2 and C.IsJoiner() then
						return Refii.player_value(2, "coop.combat.midst", 0), Refii.player_value(2, "coop.combat.close", 0) ~= 0,
							Refii.player_value(2, "coop.combat.any", 0) ~= 0
					end
					local last = C.CombatLast ~= nil and C.CombatLast[p] or nil
					if last == nil then
						return 0, false, false
					end
					return last.midst or 0, last.close == true, last.any == true
				end, "Api.combat_state")
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

		-- A CAMERA'S LOOK-AT IS ITS OWN PLAYER'S. CameraFunctions.GetSafeLookAtButtonDown(player)
		-- ignores its player: it answers from hero one (gated on Player.IsAvailable(GetPlayerHero()))
		-- or'd with the henchman — one shared camera. On a joiner hero one is the host's copy, with
		-- no Player component, so player two's held trigger never reached their camera (no target
		-- lock, no look-at). With a camera each, it is the camera's own player's button.
		if CameraFunctions.GetSafeLookAtButtonDown ~= C.SafeLookAtButtonDown then
			local shipped = CameraFunctions.GetSafeLookAtButtonDown
			C.SafeLookAtButtonDown = C.Permanent(function(player, ...)
				if C.OwnCameras() then
					return player ~= nil and Player.IsAvailable(player) and Player.GetLookAtButtonDown(player) == true
				end
				return shipped(player, ...)
			end, "SafeLookAtButtonDown")
			CameraFunctions.GetSafeLookAtButtonDown = C.SafeLookAtButtonDown
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

		-- EACH CAMERA FRAMES ITS OWN PLAYER'S FIGHT. With UseNewNearbyFoeSearch (set on both
		-- machines) the cage takes its foes from CameraManager.GetFoes (`sub_82484428`), which names
		-- no player: with hero two alive it searches around the MIDPOINT of the two heroes, widened by
		-- half their separation, for hero one's foes — so either player's fight pulled both cameras
		-- into combat framing. The script's other branch searches around the camera's own player
		-- (SearchTools.FilterFoesForCombatCamera(..., self.Player, ...)); with a camera each, that
		-- is the one taken.
		if SACCamera.CalculateNearbyFoes ~= nil and SACCamera.CalculateNearbyFoes ~= C.CalculateNearbyFoes then
			local shipped = SACCamera.CalculateNearbyFoes
			C.CalculateNearbyFoes = C.Permanent(function(self, ...)
				if not C.OwnCameras() or not SACCamera.UseNewNearbyFoeSearch then
					return shipped(self, ...)
				end
				SACCamera.UseNewNearbyFoeSearch = false
				local results = { pcall(shipped, self, ...) }
				SACCamera.UseNewNearbyFoeSearch = true
				if not results[1] then
					error(results[2], 0)
				end
				return unpack(results, 2, table.maxn(results))
			end, "CalculateNearbyFoes")
			SACCamera.CalculateNearbyFoes = C.CalculateNearbyFoes
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
				-- Called for each hero every frame: the chest watch rides it (throttled inside).
				if C.ChestWatch ~= nil then
					pcall(C.ChestWatch)
				end
				if C.HouseholdTick ~= nil then
					pcall(C.HouseholdTick)
				end
				if C.CombatApply ~= nil then
					pcall(C.CombatApply)
				end
				if C.StatePlayerModes ~= nil then
					pcall(C.StatePlayerModes)
				end
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
