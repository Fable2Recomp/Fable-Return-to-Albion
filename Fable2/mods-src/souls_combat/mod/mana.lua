-- Souls Combat: the mana pool.
--
-- A module of init.lua (require("mana")).

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer, AlongHealthCurve = C.Setting, C.IsPlayer, C.AlongHealthCurve
local kManaKey, kManaMaxKey = C.ManaKey, C.ManaMaxKey

----------------------------------------------------------------------------------------------------
-- MANA (FP) for spells. Its maximum grows with the spells the hero has learned, along the health
-- curve: from the untrained length to a maxed health bar's, evenly over all forty ranks of the
-- eight spells (ABILITY_LIGHTNING .. ABILITY_RAISE_DEAD). EVERY SPELL THAT LAUNCHES COSTS
-- mana_per_level per power level: a release at level L is a cascade of slots 1..L each at its own
-- power (sub_8253ED78), so a full release costs the sum. An empty pool refuses the charge; a short
-- one caps the charge at the top slot it can pay for; a lower slot of the cascade is skipped when
-- launching it would leave the top slot unpaid — the charged spell always fires. Refills slowly
-- after a pause.
--   Refii.pool — each player's pool under souls_combat.mana, its maximum under
--     souls_combat.mana_max; refilled mana_regen_per_second once mana_regen_delay has passed since
--     a cast, where the hero is played.
--   hero_ready / ability_level — the maximum, from the hero's spell ranks: worked out when the hero
--     arrives and again on the tick after any of its spell abilities is set (a purchase, an
--     unlearn, a script, a save load), from the level the game then holds.
--   spell_charge — the B press; cancelling refuses the charge, with no side effects.
--   spell_charge_level — the charge time became the top slot (0..4), every frame of the held
--     charge; writing `level` caps the cascade (it launches 0..level), so the cap follows the pool
--     as it refills mid-hold (after mana_regen_delay).
--   spell_launch — one slot about to launch, `cascading` at the lower ones; cancel skips it.
--   spell_launched — a spell was really created for a slot; the cost is taken here.
----------------------------------------------------------------------------------------------------

local kRanksPerSpell = 5
local kAllRanks = (Refii.ABILITY_RAISE_DEAD - Refii.ABILITY_LIGHTNING + 1) * kRanksPerSpell
local kMaxChargeLevel = 5

-- Each player's maximum, from their hero's learned spells; 0 without a hero.
S.ManaMax = { 0, 0 }
-- The top slot each player last charged to: what a cascade's lower slot must leave paid for.
S.ChargeTop = { -1, -1 }

local function ManaOf(p)
	return Refii.pool_value(p, kManaKey) or S.ManaMax[p]
end

local function ManaCost(level)
	return Setting("mana_per_level", 12) * level
end

local function IsSpell(ability)
	return ability >= Refii.ABILITY_LIGHTNING and ability <= Refii.ABILITY_RAISE_DEAD
end

-- Player p's maximum from the spell ranks their hero holds now.
local function RestateManaMax(p)
	local hero = Refii.player_hero(p)
	if hero == nil then
		S.ManaMax[p] = 0
		return
	end
	local ranks = 0
	for id = Refii.ABILITY_LIGHTNING, Refii.ABILITY_RAISE_DEAD do
		local rank = Refii.ability_level(hero, id)
		if rank ~= nil and rank > 0 and rank <= kRanksPerSpell then
			ranks = ranks + rank
		end
	end
	-- Forty ranks are worth what five Toughness levels are: the whole 600 health points.
	S.ManaMax[p] = AlongHealthCurve(600 * ranks / kAllRanks)
end

-- The charge levels a player's pool can pay for, 0..5.
local function AffordableLevels(p)
	local levels = math.floor(ManaOf(p) / Setting("mana_per_level", 12))
	if levels < 0 then
		return 0
	end
	return levels > kMaxChargeLevel and kMaxChargeLevel or levels
end

-- One length both bars are drawn against. The same for everyone, so a mod value, not a player's.
local function PublishBarCap()
	Refii.publish("souls_combat.bar_cap", S.BarCap)
end

for p = 1, 2 do
	Refii.pool(p, kManaKey, {
		max = function(p) return S.ManaMax[p] end,
		floor = 0,
		regen = function() return Setting("mana_regen_per_second", 6) end,
		delay = function() return Setting("mana_regen_delay", 2.5) end,
		max_key = kManaMaxKey,
	})
	RestateManaMax(p)
end
PublishBarCap()

Refii.on("hero_ready", function(e)
	if IsPlayer(e.player) then
		RestateManaMax(e.player)
		PublishBarCap()
	end
end)

Refii.on("hero_gone", function(e)
	if IsPlayer(e.player) then
		S.ManaMax[e.player] = 0
	end
end)

-- The event comes before the level is set and another handler may refuse it, so the maximum is
-- read back on the next tick, from what the game then holds.
Refii.on("ability_level", function(e)
	local p = e.player
	if IsPlayer(p) and IsSpell(e.type) then
		Refii.after(0, function() RestateManaMax(p) end, "souls_combat.mana_max." .. p)
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
	if IsPlayer(p) and level >= 1 and level <= kMaxChargeLevel then
		Refii.pool_spend(p, kManaKey, ManaCost(level))
	end
end)
