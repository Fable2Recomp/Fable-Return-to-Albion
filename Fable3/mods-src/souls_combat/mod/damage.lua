-- Souls Combat: how hard a hit lands.
--
-- A module of init.lua (require("damage")).

local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer

----------------------------------------------------------------------------------------------------
-- DAMAGE: enemies hit the hero harder, and elite enemies take longer to put down.
--   hit — every hit reaches its victim's receiver here, before delivery, with `damage` writable.
--     The game's own damage for a hit (CECHealth::CalculateDamage, TU3 0x82629D28) is linear in
--     the hit's damage once the health admits the hit, so a scale here scales the health lost by
--     as much.
--   A hit by a hero on an enemy the creature definition states elite or a lieutenant
--   (Refii.creature_tier) is divided by elite_toughness. Hero on hero is left alone.
----------------------------------------------------------------------------------------------------

local function HasBit(bits, bit)
	return math.floor(bits / bit) % 2 == 1
end

local function IsElite(entity)
	local bits = entity ~= 0 and Refii.creature_tier(entity) or nil
	return bits ~= nil and (HasBit(bits, Refii.CREATURE_ELITE) or HasBit(bits, Refii.CREATURE_LIEUTENANT))
end

Refii.on("hit", function(e)
	if not (e.damage > 0) then
		return
	end
	local victim_hero, attacker_hero = IsPlayer(e.victim_player), IsPlayer(e.attacker_player)
	if victim_hero and not attacker_hero then
		e.damage = e.damage * Setting("damage_taken_scale", 1.6)
	elseif attacker_hero and not victim_hero and IsElite(e.victim) then
		local toughness = Setting("elite_toughness", 1.5)
		if toughness > 0 then
			e.damage = e.damage / toughness
		end
	end
end)
