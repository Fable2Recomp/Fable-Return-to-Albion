-- Souls Combat: the stamina pool.
--
-- A module of init.lua (require("stamina")).

local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer
local Stamina, StaminaMax = C.Stamina, C.StaminaMax
local kStaminaKey, kStaminaMaxKey = C.StaminaKey, C.StaminaMaxKey

----------------------------------------------------------------------------------------------------
-- STAMINA: swings spend it, it refills after a short pause, at zero the next swing is refused.
--   Refii.pool — each player's pool under souls_combat.stamina, its maximum (the player's own
--     Stamina level, ui/abilities.json) under souls_combat.stamina_max; the runtime refills it
--     stamina_regen_per_second once stamina_regen_delay has passed since a spend, where the hero
--     is played, and keeps it within stamina_floor..max. Every field is read each tick, so a live
--     tune and a level bought mid-session reach it at once.
--   ability_request — every ability request any creature makes; REQUEST_MELEE_PRESSED refused
--     on an empty pool. The bus refuses that press's release itself. Holding X is also the block,
--     so an empty pool cannot raise a guard either.
--   melee_swing — once per strike, after the swing's animation has started: the cost. A pool
--     holding anything pays in full, into debt down to stamina_floor, so the emptying swing lands.
----------------------------------------------------------------------------------------------------

for p = 1, 2 do
	Refii.pool(p, kStaminaKey, {
		max = StaminaMax,
		floor = function() return Setting("stamina_floor", -25) end,
		regen = function() return Setting("stamina_regen_per_second", 55) end,
		delay = function() return Setting("stamina_regen_delay", 0.7) end,
		max_key = kStaminaMaxKey,
	})
end

Refii.on("ability_request", function(e)
	if IsPlayer(e.player) and e.type == Refii.REQUEST_MELEE_PRESSED and Stamina(e.player) <= 0 then
		return true
	end
end)

Refii.on("melee_swing", function(e)
	if IsPlayer(e.player) then
		Refii.pool_spend(e.player, kStaminaKey, Setting("swing_cost", 18))
	end
end)
