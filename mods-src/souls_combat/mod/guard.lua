-- Souls Combat: the guard's stamina cost.
--
-- A module of init.lua (require("guard")).

local C = require("common")
local Setting, IsPlayer, Stamina, Spend = C.Setting, C.IsPlayer, C.Stamina, C.Spend

----------------------------------------------------------------------------------------------------
-- THE GUARD costs stamina and breaks when the pool cannot pay. block fires once per hit a block is
-- taking (the bus gives the game's second block listener the first's verdict, so the cost is
-- taken once); cancelling BREAKS the guard and the hit lands in full. An unblockable hit bypasses
-- the block in the game already and is never charged.
----------------------------------------------------------------------------------------------------

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
	Spend(p, cost)
end)
