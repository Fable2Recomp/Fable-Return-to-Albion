-- Souls Combat: healing in a fight.
--
-- A module of init.lua (require("healing")).

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer

----------------------------------------------------------------------------------------------------
-- HEALING IN A FIGHT has a cooldown. Fable 2 heals with no wind-up: a health potion is one
-- instant Modify (200 to 700), food the same amount spread over 5 seconds, from the d-pad or the
-- pause menu alike (OnCarriedActionUseBase:ApplyBonusEffects). In a fight, a second healing item
-- within heal_cooldown_seconds of the last is refused.
--   item_used — every use of an item reaches lhEntity_UseInventoryItem (the d-pad, the menu, a
--     co-op partner's); a healing item is one whose InventoryItemComponent's BonusEffects state a
--     HealthModifier above 0 (the field the use script applies). Refused: the item stays.
--   hit_landed — a hit taken or dealt by a hero: that hero is in a fight for heal_combat_seconds.
-- The cooldown runs on the game clock, which stands still in the pause menu, so drinking from
-- the menu does not run it down. Uses are decided where the hero is played.
----------------------------------------------------------------------------------------------------

S.LastFight = S.LastFight or {}
S.LastHeal = S.LastHeal or {}

local function Heals(definition)
	local component = definition and Refii.record_ref(definition, "InventoryItemComponent")
	local bonus = component and Refii.record_ref(component, "BonusEffects")
	local amount = bonus and Refii.record_number(bonus, "HealthModifier")
	return amount ~= nil and amount > 0
end

Refii.on("hit_landed", function(e)
	local now = Refii.now()
	if IsPlayer(e.victim_player) then
		S.LastFight[e.victim_player] = now
	end
	if IsPlayer(e.attacker_player) then
		S.LastFight[e.attacker_player] = now
	end
end)

Refii.on("item_used", function(e)
	local p = e.player
	if not IsPlayer(p) or Refii.event_replica() or not Heals(e.definition) then
		return
	end
	local now = Refii.now()
	local fight, last = S.LastFight[p], S.LastHeal[p]
	if fight ~= nil and now - fight < Setting("heal_combat_seconds", 8)
		and last ~= nil and now - last < Setting("heal_cooldown_seconds", 5) then
		return true
	end
	S.LastHeal[p] = now
end)

Refii.on("hero_gone", function(e)
	if IsPlayer(e.player) then
		S.LastFight[e.player], S.LastHeal[e.player] = nil, nil
	end
end)
