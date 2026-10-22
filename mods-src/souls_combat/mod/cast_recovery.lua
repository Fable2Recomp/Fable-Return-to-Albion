-- Souls Combat: cast recovery.
--
-- A module of init.lua (require("cast_recovery")).

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer

----------------------------------------------------------------------------------------------------
-- CAST RECOVERY: a launched spell plants its caster for a moment. The game holds a quick cast's
-- movement only until the spell launches; from then any stick input cancels the rest of the
-- animation (sub_82737C38, 0x82737e14..e24), which made a step back between casts free. The plant
-- is the player component's own movement-speed override (Refii.set_speed_multiplier: the pair the
-- game's scripts set through Player.SetPlayerMovementSpeedOverridden / SetOverriddenMovement-
-- SpeedMultiplier, which the locomotion update multiplies the velocity by, 0x822b2c68): 0 holds
-- the hero in place; turning, rolls and blocks still work. Every slot of a charged release's
-- cascade fires spell_launched; each refreshes the timer. The override is taken only when no
-- script holds it, and given back only while it still holds the values written here — a script
-- that took it meanwhile keeps it. In co-op only the machine that plays the hero plants it.
----------------------------------------------------------------------------------------------------

S.Plants = {}

local function ReleasePlant(p, plant)
	if Refii.player_hero(p) == plant.hero then
		local overridden, factor = Refii.speed_multiplier(p)
		if overridden == true and factor == 0 then
			Refii.set_speed_multiplier(p, plant.multiplier)
			Refii.clear_speed_multiplier(p)
		end
	end
	S.Plants[p] = nil
end

Refii.on("spell_launched", function(e)
	local p = e.player
	-- The plant holds the body still, so it is made where the hero is played; the other co-op
	-- machine runs a copy of the cast and leaves the body to the replication.
	if not IsPlayer(p) or Refii.event_replica() then
		return
	end
	local seconds = Setting("cast_recovery_seconds", 0.6)
	if seconds <= 0 then
		return
	end
	local plant = S.Plants[p]
	if plant ~= nil and plant.hero == e.caster then
		plant.left = seconds
		return
	end
	local overridden, factor = Refii.speed_multiplier(p)
	if overridden == nil or overridden then
		return  -- no player component, or a script holds the override
	end
	S.Plants[p] = { hero = e.caster, multiplier = factor, left = seconds }
	Refii.set_speed_multiplier(p, 0)
end)

Refii.on("tick", function(e)
	for p = 1, 2 do
		local plant = S.Plants[p]
		if plant ~= nil then
			plant.left = plant.left - e.seconds
			if plant.left <= 0 or Refii.player_hero(p) ~= plant.hero then
				ReleasePlant(p, plant)
			end
		end
	end
end)
