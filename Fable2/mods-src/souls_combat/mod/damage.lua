-- Souls Combat: how hard a hit lands.
--
-- A module of init.lua (require("damage")).

local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer

----------------------------------------------------------------------------------------------------
-- DAMAGE: enemies hit the hero harder, and elite enemies take longer to put down.
--   hit — every hit reaches its victim's receiver here, before delivery, and `damage` is the
--     hit's own +0x38. Everything after it multiplies (the victim's DamageModifiers, the armour
--     divide by its Toughness, Barkskin and Berserk, the attack zone; TU1 CECHealth_CalculateDamage
--     sub_824CDDC8) and only the clamp to 0..max follows, so a scale here scales the health lost
--     by exactly as much. The game has no hero damage cap; its difficulty multipliers apply only
--     above level 1, and the shipped game runs at 1.
--   A hit by a hero on an enemy the creature definition states elite or a lieutenant
--   (Refii.creature_tier) is divided by elite_toughness. Hero on hero is left alone.
-- The hit is decided where the world runs, so online the host scales every hit, the joiner's
-- hero's included.
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
