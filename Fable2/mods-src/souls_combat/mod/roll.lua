-- Souls Combat: the roll's stamina cost.
--
-- A module of init.lua (require("roll")).

local C = require("common")
local Setting, IsPlayer, Stamina, kStaminaKey = C.Setting, C.IsPlayer, C.Stamina, C.StaminaKey

----------------------------------------------------------------------------------------------------
-- THE ROLL costs stamina and is refused on an empty pool. roll_can_start asks whether the roll may
-- start (once per matching roll record, so it gates without spending); roll_started says one was
-- queued, so the cost is taken there. Invulnerability is the game's own: the hit dispatch drops
-- every hit while the roll holds the victim's receiver flag clear (TU1 0x825c5254..78, the roll's
-- 0x8270087c / 0x82700b50), so a roll is already invulnerable for its whole length.
----------------------------------------------------------------------------------------------------

Refii.on("roll_can_start", function(e)
	if IsPlayer(e.player) and Stamina(e.player) <= 0 then
		return true
	end
end)

Refii.on("roll_started", function(e)
	if IsPlayer(e.player) then
		Refii.pool_spend(e.player, kStaminaKey, Setting("roll_cost", 22))
	end
end)
