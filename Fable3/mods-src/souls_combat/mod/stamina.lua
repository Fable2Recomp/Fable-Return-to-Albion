-- Souls Combat: the stamina pool.
--
-- STAMINA: swings spend it, it refills after a short pause, at zero the next swing is refused.
--   Refii.pool — each player's pool under souls_combat.stamina; the runtime refills it by
--     stamina_regen_fraction of its maximum a second (Fable II's 55 on 100) once stamina_regen_delay
--     has passed since a spend, and keeps it within stamina_floor..max. The maximum grows with the
--     hero's melee and ranged levels (common.lua). Every field is read each tick, so a live tune and a
--     level bought on the Road to Rule reach it at once.
--   ability_request — REQUEST_MELEE_PRESSED refused on an empty pool; the runtime refuses that
--     press's release with it.
--   melee_swing — once per strike, after its animation started: the cost, swing_cost, or
--     flourish_cost for the charged flourish (the event's `flourish`: the ability's own GDB
--     Flourish property). A pool holding anything
--     pays in full, into debt down to stamina_floor, so the emptying swing lands.

local C = require("common")
local Setting, IsPlayer, Stamina = C.Setting, C.IsPlayer, C.Stamina

for p = 1, 2 do
	Refii.pool(p, C.StaminaKey, {
		max = C.StaminaMax,
		floor = function() return Setting("stamina_floor", -25) end,
		regen = function(p) return Setting("stamina_regen_fraction", 0.55) * C.StaminaMax(p) end,
		delay = function() return Setting("stamina_regen_delay", 0.7) end,
		max_key = C.StaminaMaxKey,
	})
end

Refii.on("ability_request", function(e)
	if IsPlayer(e.player) and e.type == Refii.REQUEST_MELEE_PRESSED and Stamina(e.player) <= 0 then
		return true
	end
end)

Refii.on("melee_swing", function(e)
	if IsPlayer(e.player) then
		local cost = e.flourish and Setting("flourish_cost", 32) or Setting("swing_cost", 18)
		Refii.pool_spend(e.player, C.StaminaKey, cost)
	end
end)
