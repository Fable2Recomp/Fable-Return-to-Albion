-- Souls Combat: the guard's stamina cost.
--
-- block fires once per hit a block is taking; cancelling BREAKS the guard and the hit lands in full.
-- A block never goes into debt: the pool must hold the whole cost. An unblockable hit bypasses the
-- block in the game already and is never charged.

local C = require("common")
local Setting, IsPlayer, Stamina = C.Setting, C.IsPlayer, C.Stamina

Refii.on("block", function(e)
	local p = e.player
	if not IsPlayer(p) or e.unblockable then
		return
	end
	local cost = (e.damage > 0 and e.damage or 0) * Setting("block_cost_per_damage", 1.2)
	local stamina = Stamina(p)
	if not (stamina >= cost and stamina > 0) then
		return true  -- The guard breaks and the hit lands in full: that is the whole price.
	end
	Refii.pool_spend(p, C.StaminaKey, cost)
end)
