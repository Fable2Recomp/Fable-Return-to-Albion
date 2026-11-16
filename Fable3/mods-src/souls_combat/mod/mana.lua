-- Souls Combat: the mana pool.
--
-- MANA for spells. Its maximum grows with the hero's magic level on the Road to Rule (common.lua); it
-- refills in mana_regen_seconds (Fable III's own health refill takes 20) after a pause. EVERY SPELL THAT LAUNCHES COSTS mana_per_level per power level: a release at
-- level L is a cascade of slots each at its own power, so a full release costs the sum. An empty
-- pool refuses the charge; a short one caps the charge at the top slot it can pay for; a lower slot
-- of the cascade is skipped when launching it would leave the top slot unpaid — the charged spell
-- always fires. Refills slowly after a pause.
--   spell_charge — the charge's start; cancelling refuses it.
--   spell_charge_level — the charge time became the top slot (0..4), every frame of the held
--     charge; writing `level` caps the cascade, so the cap follows the pool as it refills.
--   spell_launch — one slot about to launch, `cascading` at the lower ones; cancel skips it.
--   spell_launched — a spell was really created for a slot; the cost is taken here.

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer, Mana = C.Setting, C.IsPlayer, C.Mana

local kMaxChargeLevel = 5

-- The top slot each player last charged to: what a cascade's lower slot must leave paid for.
S.ChargeTop = { -1, -1 }

local function ManaCost(level)
	return Setting("mana_per_level", 12) * level
end

-- The charge levels a player's pool can pay for, 0..5.
local function AffordableLevels(p)
	local levels = math.floor(Mana(p) / Setting("mana_per_level", 12))
	if levels < 0 then
		return 0
	end
	return levels > kMaxChargeLevel and kMaxChargeLevel or levels
end

for p = 1, 2 do
	Refii.pool(p, C.ManaKey, {
		max = C.ManaMax,
		floor = 0,
		regen = function(p) return C.ManaMax(p) / Setting("mana_regen_seconds", 20) end,
		delay = function() return Setting("mana_regen_delay", 2.5) end,
		max_key = C.ManaMaxKey,
	})
end

Refii.on("spell_charge", function(e)
	if IsPlayer(e.player) and AffordableLevels(e.player) == 0 then
		return true
	end
end)

Refii.on("spell_charge_level", function(e)
	local p = e.player
	if not IsPlayer(p) then
		return
	end
	local affordable = AffordableLevels(p)
	if affordable > 0 and e.level + 1 > affordable then
		e.level = affordable - 1
	end
	S.ChargeTop[p] = e.level
end)

Refii.on("spell_launch", function(e)
	local p = e.player
	if not IsPlayer(p) or e.slot < 0 or e.slot >= kMaxChargeLevel then
		return
	end
	local mana = Mana(p)
	local cost = ManaCost(e.slot + 1)
	if e.cascading then
		local top = S.ChargeTop[p]
		if top < e.slot then
			top = e.slot
		end
		if mana - cost < ManaCost(top + 1) then
			return true
		end
	elseif mana < cost then
		return true
	end
end)

Refii.on("spell_launched", function(e)
	local p = e.player
	local level = e.slot + 1
	if IsPlayer(p) and level >= 1 and level <= kMaxChargeLevel then
		Refii.pool_spend(p, C.ManaKey, ManaCost(level))
	end
end)
