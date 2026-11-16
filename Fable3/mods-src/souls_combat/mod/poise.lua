-- Souls Combat: poise.
--
-- POISE: a hit staggers its victim only once enough damage has landed in a short time. Enemies no
-- longer flinch at every tap; neither does the hero.
--   hit_response — the victim is about to flinch, with the game's own damage arithmetic and the
--     victim's health; a killing blow is never offered, so a death's fall always plays.
--     Cancelling absorbs it: no animation, no state.
--   entity_died — a dead entity's pool goes with it.
-- Each victim's pool is a per-entity value (Refii.entity_value), forgotten when the entity leaves
-- the world; its window runs on the runtime's clock (Refii.now).

local C = require("common")
local Setting = C.Setting

local kTakenKey = "souls_combat.poise_taken"
local kLastHitKey = "souls_combat.poise_last_hit"

Refii.on("entity_died", function(e)
	Refii.entity_set(e.entity, kTakenKey, nil)
	Refii.entity_set(e.entity, kLastHitKey, nil)
end)

Refii.on("hit_response", function(e)
	if e.max_health <= 0 then
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
