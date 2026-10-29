-- Souls Combat: the mana pool.
--
-- A module of init.lua (require("mana")).

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer, AlongHealthCurve = C.Setting, C.IsPlayer, C.AlongHealthCurve
local kManaKey, kManaMaxKey, kManaRestKey, kRestCap = C.ManaKey, C.ManaMaxKey, C.ManaRestKey, C.RestCap

----------------------------------------------------------------------------------------------------
-- MANA (FP) for spells. Its maximum grows with the spells the hero has learned, along the health
-- curve: from the untrained length to a maxed health bar's, evenly over all forty ranks of the
-- eight spells (ability ids 7..14). EVERY SPELL THAT LAUNCHES COSTS mana_per_level per power
-- level: a release at level L is a cascade of slots 1..L each at its own power (sub_8253ED78), so
-- a full release costs the sum. An empty pool refuses the charge; a short one caps the charge at
-- the top slot it can pay for; a lower slot of the cascade is skipped when launching it would
-- leave the top slot unpaid — the charged spell always fires. Refills slowly after a pause.
--   spell_charge — the B press; cancelling refuses the charge, with no side effects.
--   spell_charge_level — the charge time became the top slot (0..4), every frame of the held
--     charge; writing `level` caps the cascade (it launches 0..level), so the cap follows the pool
--     as it refills mid-hold (after mana_regen_delay).
--   spell_launch — one slot about to launch, `cascading` at the lower ones; cancel skips it.
--   spell_launched — a spell was really created for a slot; the cost is taken here.
----------------------------------------------------------------------------------------------------

local kAllRanks = 8 * 5
local kFirstSpell = 7
local kLastSpell = 14
local kMaxChargeLevel = 5

-- Each player's maximum, as the last tick found it from their learned spells.
S.ManaMax = { 0, 0 }
-- The top slot each player last charged to: what a cascade's lower slot must leave paid for.
S.ChargeTop = { -1, -1 }

local function ManaOf(p)
	return Refii.player_value(p, kManaKey, S.ManaMax[p])
end

local function ManaCost(level)
	return Setting("mana_per_level", 12) * level
end

local function MaxMana(hero)
	local ranks = 0
	for id = kFirstSpell, kLastSpell do
		local rank = Refii.ability_level(hero, id)
		if rank ~= nil and rank > 0 and rank <= kMaxChargeLevel then
			ranks = ranks + rank
		end
	end
	-- Forty ranks are worth what five Toughness levels are: the whole 600 health points.
	return AlongHealthCurve(600 * ranks / kAllRanks)
end

-- The charge levels a player's pool can pay for, 0..5.
local function AffordableLevels(p)
	local levels = math.floor(ManaOf(p) / Setting("mana_per_level", 12))
	if levels < 0 then
		return 0
	end
	return levels > kMaxChargeLevel and kMaxChargeLevel or levels
end

Refii.on("tick", function(e)
	-- One length both bars are drawn against. The same for everyone, so a mod value, not a player's.
	Refii.publish("souls_combat.bar_cap", S.BarCap)
	for p = 1, 2 do
		local hero = Refii.player_hero(p)
		if hero == nil then
			S.ManaMax[p] = 0
		else
			S.ManaMax[p] = MaxMana(hero)
			local value = ManaOf(p)
			if Refii.player_here(p) then
				Refii.player_set(p, kManaMaxKey, S.ManaMax[p])
				-- A hero arriving (a load, a join) starts full: ManaOf is the maximum until stated.
				local rest = Refii.player_value(p, kManaRestKey, kRestCap) + e.seconds
				if rest >= Setting("mana_regen_delay", 2.5) then
					value = value + Setting("mana_regen_per_second", 6) * e.seconds
				end
				if value > S.ManaMax[p] then
					value = S.ManaMax[p]
				elseif value < 0 then
					value = 0
				end
				Refii.player_set(p, kManaKey, value)
				Refii.player_set(p, kManaRestKey, rest > kRestCap and kRestCap or rest)
			end
		end
	end
end)

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
	local mana = ManaOf(p)
	local cost = ManaCost(e.slot + 1)
	if e.cascading then
		-- A lower slot of the cascade: skipped when paying it would leave the top slot unpaid.
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
	if not IsPlayer(p) or level < 1 or level > kMaxChargeLevel then
		return
	end
	-- A pool nobody has stated yet is full, and the first statement comes from the tick where the
	-- hero is played, so a launch before it has nothing to come off.
	if Refii.player_value(p, kManaKey) == nil then
		return
	end
	Refii.player_add(p, kManaKey, -ManaCost(level))
	Refii.player_set(p, kManaRestKey, 0)
end)
