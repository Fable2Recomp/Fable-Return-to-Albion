-- Souls Combat: the roll's stamina cost.
--
-- roll_can_start asks whether the roll may start (pure tests, so it gates without spending);
-- roll_started says one was pushed, so the cost is taken there.

local C = require("common")
local Setting, IsPlayer, Stamina = C.Setting, C.IsPlayer, C.Stamina

Refii.on("roll_can_start", function(e)
	if IsPlayer(e.player) and Stamina(e.player) <= 0 then
		return true
	end
end)

Refii.on("roll_started", function(e)
	if IsPlayer(e.player) then
		Refii.pool_spend(e.player, C.StaminaKey, Setting("roll_cost", 22))
	end
end)
