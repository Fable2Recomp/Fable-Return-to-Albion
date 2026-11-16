-- Souls Combat: what every rule shares. A module (require("common")): the settings reader, the
-- player test, the player-store keys and the curve the pools grow along.
--
-- EACH PLAYER'S STATE LIVES IN THE PLAYER STORE (Refii.player_*). The two pools are the runtime's
-- regenerating pools (Refii.pool, ticked where the hero is played). Keys:
--   souls_combat.stamina / stamina_max   the pool (it may dip below zero by up to stamina_floor:
--                               the swing that empties it still lands) and its maximum
--   souls_combat.mana / mana_max         the same for mana
--
-- THE CURVE. Fable II grew the pools along the health curve. Fable III's hero keeps 250 health
-- (CreatureHero's MaxHealth) in retail: its progression is the Road to Rule's three weapon levels,
-- melee, ranged and magic (EHeroAbilityType 17, 18, 19, 0..5 each), and General Health (16) when a
-- mod or a debug path sets it. So a pool starts at 100 and grows as health would along the ability's
-- own steps (AbilityHealthBase MaxHealthModifier 150 .. 750) up to the health ceiling (GUIValues
-- MaxMaxHealth 1000): 100 at nothing, 400 at the top.
--   stamina — the 750 spread over the ten melee and ranged levels (General Health's step wins when larger)
--   mana    — the 750 spread over the five magic levels

local S = SoulsCombat
local C = {}

function C.Setting(name, default)
	return Refii.setting("souls_combat." .. name, default)
end

C.StaminaKey, C.StaminaMaxKey = "souls_combat.stamina", "souls_combat.stamina_max"
C.ManaKey, C.ManaMaxKey = "souls_combat.mana", "souls_combat.mana_max"

local kUntrained, kBase = 250, 100
local kStep = { [0] = 0, 150, 300, 450, 600, 750 }
local kMelee, kRanged, kMagic, kHealth = 17, 18, 19, 16

-- The health ceiling, read from the game's own record (GUIValues MaxMaxHealth).
C.Ceiling = Refii.record_number("GUIValues", "MaxMaxHealth") or 1000
if not (C.Ceiling > kUntrained) then
	C.Ceiling = 1000
end

-- The fullest a pool gets: what the bars are drawn against.
S.BarCap = kBase * C.Ceiling / kUntrained

function C.IsPlayer(p)
	return p == 1 or p == 2
end

local function Level(hero, kind)
	local level = math.floor(Refii.ability_level(hero, kind) or 0)
	if level < 0 then
		return 0
	end
	return level > 5 and 5 or level
end

local function Curve(bonus)
	return kBase * (kUntrained + bonus) / kUntrained
end

function C.StaminaMax(p)
	local hero = Refii.player_hero(p)
	if hero == nil then
		return kBase
	end
	local weapons = 750 * (Level(hero, kMelee) + Level(hero, kRanged)) / 10
	local health = kStep[Level(hero, kHealth)]
	return Curve(weapons > health and weapons or health) * C.Setting("stamina_scale", 1)
end

function C.ManaMax(p)
	local hero = Refii.player_hero(p)
	if hero == nil then
		return kBase
	end
	return Curve(750 * Level(hero, kMagic) / 5) * C.Setting("mana_scale", 1)
end

-- A player's stamina now (the pool's value; a player with none stated yet is full).
function C.Stamina(p)
	return Refii.pool_value(p, C.StaminaKey) or C.StaminaMax(p)
end

function C.Mana(p)
	return Refii.pool_value(p, C.ManaKey) or C.ManaMax(p)
end

return C
