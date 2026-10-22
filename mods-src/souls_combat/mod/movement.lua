-- Souls Combat: the hero's movement and parry.
--
-- A module of init.lua (require("movement")).

local S = SoulsCombat
local C = require("common")
local Setting = C.Setting

----------------------------------------------------------------------------------------------------
-- MOVEMENT (miscellaneous/herolocomotionstates.lua)
--
-- The player-control component copies a HeroLocomotionData* table by name each time it loads one
-- (sub_8283E408: at creation, indoors/outdoors, a movement-mode change), and the state tables are
-- parsed once, on the first locomotion activation (sub_827F7A78 -> sub_827F3598). Both happen
-- after this runs.
--
-- Turning: the facing turns toward the stick at a rate blended from MaxTurnSpeedStatic (standing)
-- to MaxTurnSpeedRunning (at full speed), degrees per second (sub_822B1F78). Shipped: 350 and
-- 115, so a running hero swings round in a wide arc. Speed follows the stick through a first-order
-- lag, FrictionCoefficient per second (4: about a quarter of the gap per update).
--
-- Turn-in-place: standing still, the Idle state's own MovementParams cap turning at 1 deg/s and
-- a stick behind the hero plays a Turn90/Turn180 animation; running, a reversal plays
-- Turn180IntoRun/RunTurn180. The state picker takes the first valid entry of the current state's
-- `States` list and skips a name it does not find, so those turns are taken out of every list,
-- LocomoteForwards accepts any stick direction (shipped +-120 deg) and Idle may turn: the hero
-- pivots on the spot.
----------------------------------------------------------------------------------------------------

-- THE SETTINGS ARE READ AT USE. These are table patches the game copies by name, so "at use"
-- is: applied now from the live values, and applied again on the tick a value changes (a live
-- tune from the options page or the Studio). The shipped number is kept on the table the first
-- time (SoulsShipped*), so a lower setting after a higher one still computes from the shipped
-- base. A copy the player control already took stands until it takes the next one (a movement
-- mode change, indoors/outdoors), and the Idle state's params are parsed once per session.
local function ApplyLocomotion(static, running, friction)
	for _, name in ipairs({ "HeroLocomotionData", "HeroLocomotionDataIndoorsDefault", "HeroLocomotionDataChild", "HeroLocomotionDataChildIndoorsDefault" }) do
		local data = _G[name]
		if type(data) == "table" then
			data.SoulsShippedTurnStatic = data.SoulsShippedTurnStatic or data.MaxTurnSpeedStatic or 0
			data.SoulsShippedTurnRunning = data.SoulsShippedTurnRunning or data.MaxTurnSpeedRunning or 0
			data.SoulsShippedFriction = data.SoulsShippedFriction or data.FrictionCoefficient or 0
			data.MaxTurnSpeedStatic = math.max(data.SoulsShippedTurnStatic, static)
			data.MaxTurnSpeedRunning = math.max(data.SoulsShippedTurnRunning, running)
			data.FrictionCoefficient = math.max(data.SoulsShippedFriction, friction)
		end
	end
	if HeroLocomotionStateIdle and HeroLocomotionStateIdle.MovementParams then
		HeroLocomotionStateIdle.MovementParams.MaxTurnSpeedStatic = static
		HeroLocomotionStateIdle.MovementParams.MaxTurnSpeedRunning = static
	end
	if HeroCombatBalanceParams then
		HeroCombatBalanceParams.InputWindowSecondsForParry = Setting("parry_window_seconds", 0.25)
	end
end

local applied = { static = nil, running = nil, friction = nil, parry = nil }

local function ApplyIfChanged()
	local static = Setting("turn_speed_static", 720)
	local running = Setting("turn_speed_running", 540)
	local friction = Setting("move_friction", 9)
	local parry = Setting("parry_window_seconds", 0.25)
	if static ~= applied.static or running ~= applied.running or friction ~= applied.friction or parry ~= applied.parry then
		applied.static, applied.running, applied.friction, applied.parry = static, running, friction, parry
		ApplyLocomotion(static, running, friction)
	end
end

ApplyIfChanged()
Refii.on("tick", ApplyIfChanged)

local turn_states = {
	HeroLocomotionStateTurn90CW = true,
	HeroLocomotionStateTurn90ACW = true,
	HeroLocomotionStateTurn180 = true,
	HeroLocomotionStateTurn90IntoRunCW = true,
	HeroLocomotionStateTurn90IntoRunACW = true,
	HeroLocomotionStateTurn180IntoRun = true,
	HeroLocomotionStateRunTurn180 = true,
	HeroLocomotionStateCircleTurn360 = true,
	HeroLocomotionStateOutOfRunTurn90CW = true,
	HeroLocomotionStateOutOfRunTurn90ACW = true,
	HeroLocomotionStateOutOfRunTurn180CW = true,
	HeroLocomotionStateOutOfRunTurn180ACW = true,
}

for name, state in pairs(_G) do
	if type(name) == "string" and type(state) == "table" and string.sub(name, 1, 19) == "HeroLocomotionState"
		and not turn_states[name] and type(state.States) == "table" then
		local kept = {}
		for _, next_state in ipairs(state.States) do
			if not turn_states[next_state] then
				kept[#kept + 1] = next_state
			end
		end
		-- Never strand a state: one with no way out keeps its list.
		if #kept > 0 then
			state.States = kept
		end
	end
end

if HeroLocomotionStateLocomoteForwards and HeroLocomotionData then
	local min_control = HeroLocomotionData.MinControlMagForMovement
	HeroLocomotionStateLocomoteForwards.ControlArea = { CVector3(min_control, -180.1, 0), CVector3(1.05, 180.1, 0) }
end


----------------------------------------------------------------------------------------------------
-- THE HERO'S PARRY (miscellaneous/combatbalance.lua). The player copies the table when it is
-- created, after this runs (sub_823E9BF0), and the parry reads the copy by name (sub_82986B28 ->
-- sub_829897B0): a quarter-second window instead of a full second, in a 90-degree wedge. The
-- window is a setting, written with the locomotion patch above (ApplyLocomotion) so a live
-- change reaches the table; the wedge is fixed.
----------------------------------------------------------------------------------------------------
if HeroCombatBalanceParams then
	HeroCombatBalanceParams.MeleeParryWedgeAngle = 90
end
