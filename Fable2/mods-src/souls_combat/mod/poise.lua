-- Souls Combat: poise.
--
-- A module of init.lua (require("poise")).

local C = require("common")
local Setting = C.Setting

----------------------------------------------------------------------------------------------------
-- POISE: a hit staggers its victim only once enough damage has landed in a short time. Enemies no
-- longer flinch at every tap; neither does the hero.
--   hit_response — the victim's hit-response component is about to play the flinch, with the
--     game's own damage arithmetic and the victim's health; the bus keeps the game's early-outs
--     (a blocked or parried hit, a ranged hit, a knockdown, a killing blow), so this only sees a
--     hit the victim would have flinched at. Cancelling absorbs it: no animation, no state.
--   entity_died — a dead entity's pool goes with it.
-- Each victim's pool is a per-entity value (Refii.entity_value), forgotten when the entity leaves
-- the world; its window runs on the runtime's clock (Refii.now).
-- Online the game runs the response on both machines for a hit on or by player two; the run on
-- the machine not authoritative for the victim is the copy (Refii.event_replica). The host runs
-- every hit, so its pools are whole and decide; the joiner decides player two's own pool and
-- leaves its copy of a hit by player two on anyone else to the game's predicted response. No event
-- field states which machine decides a hit's response, so that is the copy on a joiner (coop_mode 3).
----------------------------------------------------------------------------------------------------

local kTakenKey = "souls_combat.poise_taken"
local kLastHitKey = "souls_combat.poise_last_hit"

Refii.on("entity_died", function(e)
	Refii.entity_set(e.entity, kTakenKey, nil)
	Refii.entity_set(e.entity, kLastHitKey, nil)
end)

Refii.on("hit_response", function(e)
	if e.max_health <= 0 or (Refii.event_replica() and Refii.coop_mode() == 3) then
		return
	end
	local now = Refii.now()
	local taken = Refii.entity_value(e.victim, kTakenKey, 0)
	if now - Refii.entity_value(e.victim, kLastHitKey, now) > Setting("poise_window_seconds", 3) then
		taken = 0
	end
	taken = taken + e.damage
	Refii.entity_set(e.victim, kLastHitKey, now)
	if taken < Setting("poise_fraction", 0.3) * e.max_health then
		Refii.entity_set(e.victim, kTakenKey, taken)
		return true  -- Absorbed: no flinch.
	end
	Refii.entity_set(e.victim, kTakenKey, 0)  -- Staggered; the pool refills from here.
end)
