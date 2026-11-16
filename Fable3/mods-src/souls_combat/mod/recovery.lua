-- Souls Combat: what restores stamina and mana.
--
-- Fable III heals the hero on its own (15 s without a hit, then a twentieth of the maximum a second)
-- and with potions and food (the item's BonusEffects HealthModifier, through
-- OnCarriedActionUseBonusEffects). The pools follow the potions, not the food or the auto-heal, so a
-- fight is won by what the hero carries in:
--   item_used — a HEALTH potion restores stamina by its HealthModifier as a share of the health ceiling
--     (the standard potion 50%, Health_5 70%, Life 100%); a MANA or WISDOM potion restores mana by its
--     ManaModifier as a share of MaxMaxMana (300) — data the game ships and never reads. Matched by the
--     item's record ID (item_used's definition): Fable III names records by ID, not by a hash of the name.
--   hero_downed — a hero brought back fills both pools.
-- The restore lands on the next tick (after the use), through the pool so its maximum clamps it.

local C = require("common")
local IsPlayer = C.IsPlayer

local kMaxMana = 300

-- HealthModifier of each health potion record (globals.gdb), by record ID.
local kHealthPotions = {
	["0x68dc87bd"] = 500, ["0xce6b8e0f"] = 200, ["0xc8d92d8b"] = 400, ["0xd9876f47"] = 500,
	["0x3ab98f43"] = 600, ["0x28b9097f"] = 700, ["0xcc0dafa2"] = 1000,
}

-- ManaModifier of each mana potion record, and the Potion of Wisdom's.
local kManaPotions = {
	["0xb4cc9e9e"] = 200, ["0xe31b4628"] = 100, ["0x50757194"] = 200, ["0xbcf6e840"] = 300,
	["0xc8ddd62c"] = 400, ["0xe4a7a758"] = 500, ["0x0dfa8a6e"] = 1000,
}

local function Restore(p, key, amount)
	if amount > 0 then
		Refii.after(0, function() Refii.player_add(p, key, amount) end)
	end
end

Refii.on("item_used", function(e)
	local p = e.player
	if not IsPlayer(p) then
		return
	end
	local health = kHealthPotions[e.definition]
	if health ~= nil then
		Restore(p, C.StaminaKey, C.StaminaMax(p) * health / C.Ceiling * C.Setting("potion_restore_scale", 1))
	end
	local mana = kManaPotions[e.definition]
	if mana ~= nil then
		Restore(p, C.ManaKey, C.ManaMax(p) * mana / kMaxMana * C.Setting("potion_restore_scale", 1))
	end
end)

Refii.on("hero_downed", function(e)
	local p = e.player
	if IsPlayer(p) then
		Restore(p, C.StaminaKey, C.StaminaMax(p))
		Restore(p, C.ManaKey, C.ManaMax(p))
	end
end)
