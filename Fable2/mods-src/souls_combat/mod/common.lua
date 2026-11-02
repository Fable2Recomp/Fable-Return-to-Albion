-- Souls Combat: what every rule shares. A module (require("common")): the settings reader, the
-- player test, the player-store keys and the health curve the pools grow along.
--
----------------------------------------------------------------------------------------------------
-- THE COMBAT RULES (stamina.lua, roll.lua, guard.lua, mana.lua, poise.lua, commitment.lua,
-- cast_recovery.lua, lockon.lua): stamina, the roll, the guard, mana, poise, commitment, cast
-- recovery and the lock-on. Every seam is a mod API event (Refii.on) and every guest read or write a Refii.* call;
-- nothing here reads guest memory or hooks a function by name.
--
-- EACH PLAYER'S STATE LIVES IN THE PLAYER STORE (Refii.player_*), so it is the same on both
-- machines of an online session: a spend on either reaches the hero's owner. The two pools are the
-- runtime's regenerating pools (Refii.pool: ticked where the hero is played). Keys:
--   souls_combat.stamina / stamina_max   the pool (it may dip below zero by up to stamina_floor:
--                               the swing that empties it still lands) and the maximum the HUD's
--                               bar reads; the pool keeps its seconds since the last spend under
--                               souls_combat.stamina.rest
--   souls_combat.mana / mana_max         the same for mana (souls_combat.mana.rest)
--   souls_combat.stamina_ability   the Stamina ability's level, SAVED with the hero: the ENGINE
--                               buys and unlearns it from ui/abilities.json; this mod only reads it
-- The HUD (ui/hud.json) reads them as player:souls_combat.<name>, and the bars' common length as
-- mod:souls_combat.bar_cap.
----------------------------------------------------------------------------------------------------

local S = SoulsCombat
local C = {}

function C.Setting(name, default)
	return Refii.setting("souls_combat." .. name, default)
end

local kStaminaKey = "souls_combat.stamina"
local kStaminaMaxKey = "souls_combat.stamina_max"
local kStaminaAbilityKey = "souls_combat.stamina_ability"
local kManaKey = "souls_combat.mana"
local kManaMaxKey = "souls_combat.mana_max"

local kStaminaMaxLevel = 5
local kBaseStamina = 100

-- THE HEALTH CURVE, so stamina and mana grow exactly as health does. From globals.gdb: the
-- length a maxed health bar is drawn against is GUIValues (#a8f8dac8) MaxMaxHealth, 950, read from
-- the record; Toughness levels 1..5 (AbilityToughnessBase -> AbilityGroupRecords #24876832,
-- Record1..5) state MaxHealthModifier 100, 300, 400, 500, 600. Those five are copied here: the
-- RecordN properties are links, not the reference properties Refii.record_ref reads. An untrained
-- hero's 350 is the ceiling less level 5's 600.
local kToughnessBonus = { [0] = 0, 100, 300, 400, 500, 600 }
local kHealthCeiling = Refii.record_number("0xa8f8dac8", "MaxMaxHealth")
if kHealthCeiling == nil or kHealthCeiling <= kToughnessBonus[5] then
	kHealthCeiling = 950
end
local kUntrainedHealth = kHealthCeiling - kToughnessBonus[5]

-- A pool that starts at kBaseStamina and grows as health would: `bonus` health points' worth.
local function AlongHealthCurve(bonus)
	return kBaseStamina * (kUntrainedHealth + bonus) / kUntrainedHealth
end

-- The fullest any such pool gets (271.4): what the HUD draws both bars against, so a full pool is
-- exactly as long as a maxed health bar.
S.BarCap = kBaseStamina * kHealthCeiling / kUntrainedHealth

local function IsPlayer(p)
	return p == 1 or p == 2
end

-- A player's Stamina ability level, 0..5, from the saved player value the engine's purchase writes.
local function StaminaLevel(p)
	local level = math.floor(Refii.player_value(p, kStaminaAbilityKey, 0))
	if level < 0 then
		return 0
	end
	return level > kStaminaMaxLevel and kStaminaMaxLevel or level
end

-- Maximum stamina at a Stamina level: Toughness's own steps along the health curve.
local function StaminaMax(p)
	return AlongHealthCurve(kToughnessBonus[StaminaLevel(p)])
end

-- A player's stamina now (the pool's value; a player with none stated yet is full).
local function Stamina(p)
	return Refii.pool_value(p, kStaminaKey) or StaminaMax(p)
end

C.StaminaKey, C.StaminaMaxKey, C.StaminaAbilityKey = kStaminaKey, kStaminaMaxKey, kStaminaAbilityKey
C.ManaKey, C.ManaMaxKey = kManaKey, kManaMaxKey
C.AlongHealthCurve = AlongHealthCurve
C.IsPlayer = IsPlayer
C.StaminaMax = StaminaMax
C.Stamina = Stamina

return C
