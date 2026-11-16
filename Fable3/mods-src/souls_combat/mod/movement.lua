-- Souls Combat: the hero's movement.
--
-- A module of init.lua (require("movement")).

local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer

----------------------------------------------------------------------------------------------------
-- MOVEMENT (miscellaneous/herolocomotionstates.lua)
--
-- Turning: the facing turns toward the stick at a rate blended from MaxTurnSpeedStatic (standing)
-- to MaxTurnSpeedRunning (at full speed), degrees per second, and speed follows the stick through a
-- first-order lag of FrictionCoefficient per second. The hero's control mode reads all three from
-- its locomotion component (134) each update: CPlayerModeControlEntitySimpleBase vf15 (TU3
-- 0x8224A0A0 / disc 0x8224AE58), run by the Simple, SimpleStrafe, HandHoldingFollower and
-- ChargeMeleeAttack modes; the Simple mode is the one hero creation installs. Shipped: 800 and 1
-- (indoors 800 and 7), friction 7, so a running hero barely turns by this path. Refii.set_locomotion
-- holds each value for that player's hero at the component's one reader, over whichever
-- HeroLocomotionData* table is loaded — so it is stated per player on hero_ready and again when a
-- setting changes.
--
-- Turn-in-place: standing still, a stick behind the hero plays Turn90CW/Turn90ACW/Turn180; running,
-- a reversal plays RunTurn180. The turn states are taken out of every state's `States` list, and
-- LocomoteWalk and LocomoteRun (shipped +-60 deg) accept any stick direction, so the hero pivots at
-- its own turn rate instead. A state's MovementParams win over the held values while it is active
-- (the reader asks state-first); in Fable III only IdleIdle (MaxSpeed) and RunTurn180 (the turn
-- rates) carry any, and RunTurn180 is no longer reachable. These are the STATES' content, shared by
-- every hero: the game parses HeroLocomotionStateRegister's tables after the mods have run (TU3
-- 0x8234CAE4, disc 0x8234576C, after the startup scripts at 0x8234C970 / 0x823455F8), and every
-- movement mode shares the parsed states.
--
-- No parry window: Fable III's combatbalance.lua still carries MeleeParryInputWindowSeconds and
-- MeleeParryWedgeAngle, but nothing in the game reads them, so Refii.set_parry has nothing to set.
----------------------------------------------------------------------------------------------------

-- Player p's hero's own turn rates and friction, from the live settings.
local function StateLocomotion(p)
	if Refii.player_hero(p) == nil then
		return
	end
	local static, running = Setting("turn_speed_static", 800), Setting("turn_speed_running", 540)
	local friction = Setting("move_friction", 9)
	Refii.set_locomotion(p, { MaxTurnSpeedStatic = static, MaxTurnSpeedRunning = running, FrictionCoefficient = friction })
end

local turn_states = {
	HeroLocomotionStateTurn90CW = true,
	HeroLocomotionStateTurn90ACW = true,
	HeroLocomotionStateTurn180 = true,
	HeroLocomotionStateTurn180IntoRun = true,
	HeroLocomotionStateRunTurn180 = true,
}

-- The game parses exactly the states its register names.
if type(HeroLocomotionStateRegister) == "table" then
	for _, name in ipairs(HeroLocomotionStateRegister) do
		local state = _G[name]
		if not turn_states[name] and type(state) == "table" and type(state.States) == "table" then
			local kept = {}
			for _, next_state in ipairs(state.States) do
				-- An entry is a name or {name, weight}.
				local next_name = type(next_state) == "table" and next_state[1] or next_state
				if not turn_states[next_name] then
					kept[#kept + 1] = next_state
				end
			end
			-- Never strand a state: one with no way out keeps its list.
			if #kept > 0 then
				state.States = kept
			end
		end
	end
end

if HeroLocomotionData then
	local min_control = HeroLocomotionData.MinControlMagForMovement
	for _, state in ipairs({ HeroLocomotionStateLocomoteWalk, HeroLocomotionStateLocomoteRun }) do
		if type(state) == "table" then
			state.ControlArea = { CVector3(min_control, -180.1, 0), CVector3(1.05, 180.1, 0) }
		end
	end
end

for p = 1, 2 do
	StateLocomotion(p)
end

Refii.on("hero_ready", function(e)
	if IsPlayer(e.player) then
		StateLocomotion(e.player)
	end
end)

local kLocomotionSettings = {
	["souls_combat.turn_speed_static"] = true,
	["souls_combat.turn_speed_running"] = true,
	["souls_combat.move_friction"] = true,
}

Refii.on("setting_changed", function(e)
	if kLocomotionSettings[e.key] then
		for p = 1, 2 do
			StateLocomotion(p)
		end
	end
end)
