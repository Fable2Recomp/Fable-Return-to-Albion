-- Souls Combat: the hero's movement and parry.
--
-- A module of init.lua (require("movement")).

local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer

----------------------------------------------------------------------------------------------------
-- MOVEMENT (miscellaneous/herolocomotionstates.lua)
--
-- Turning: the facing turns toward the stick at a rate blended from MaxTurnSpeedStatic (standing)
-- to MaxTurnSpeedRunning (at full speed), degrees per second (sub_822B1F78). Shipped: 350 and
-- 115, so a running hero swings round in a wide arc. Speed follows the stick through a first-order
-- lag, FrictionCoefficient per second (4: about a quarter of the gap per update). All three are
-- each hero's OWN locomotion values (Refii.set_locomotion): every hero's locomotion component
-- copies a HeroLocomotionData* table when it loads one (sub_8283E408: at creation,
-- indoors/outdoors, a movement-mode change), and the call writes that hero's copy, live, and holds
-- it across the next copy — so it is stated per player on hero_ready and again when a setting
-- changes.
--
-- Turn-in-place: standing still, the Idle state's own MovementParams cap turning at 1 deg/s and
-- a stick behind the hero plays a Turn90/Turn180 animation; running, a reversal plays
-- Turn180IntoRun/RunTurn180. The state picker takes the first valid entry of the current state's
-- `States` list and skips a name it does not find, so those turns are taken out of every list,
-- LocomoteForwards accepts any stick direction (shipped +-120 deg) and Idle may turn: the hero
-- pivots on the spot. These are the STATES' content, shared by every hero, and a state's own
-- MovementParams win over the hero's values while it is active, so they stay patches of the
-- state tables: parsed once, on the first locomotion activation (sub_827F7A78 -> sub_827F3598),
-- after this runs. Idle's turn cap follows turn_speed_static until then.
----------------------------------------------------------------------------------------------------

-- Player p's hero's own turn rates and friction, from the live settings.
local function StateLocomotion(p)
	if Refii.player_hero(p) == nil then
		return
	end
	local static, running = Setting("turn_speed_static", 720), Setting("turn_speed_running", 540)
	local friction = Setting("move_friction", 9)
	Refii.set_locomotion(p, { MaxTurnSpeedStatic = static, MaxTurnSpeedRunning = running, FrictionCoefficient = friction })
end

-- The Idle state's own cap: the standing turn rate, for every hero.
local function StateIdleTurn()
	if HeroLocomotionStateIdle and HeroLocomotionStateIdle.MovementParams then
		local static = Setting("turn_speed_static", 720)
		HeroLocomotionStateIdle.MovementParams.MaxTurnSpeedStatic = static
		HeroLocomotionStateIdle.MovementParams.MaxTurnSpeedRunning = static
	end
end

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
-- THE HERO'S PARRY (miscellaneous/combatbalance.lua): a quarter-second window instead of a full
-- second, in a 90-degree wedge. Each hero's player component copies HeroCombatBalanceParams when
-- it is made (sub_823E9BF0) and the parry reads that copy (sub_82986B28 -> sub_829897B0);
-- Refii.set_parry writes that hero's copy, live, and holds it for the player's next hero. The
-- window is a setting; the wedge is fixed.
----------------------------------------------------------------------------------------------------
local kParryWedge = 90

local function StateParry(p)
	Refii.set_parry(p, Setting("parry_window_seconds", 0.25), kParryWedge)
end

StateIdleTurn()
for p = 1, 2 do
	StateLocomotion(p)
	StateParry(p)
end

Refii.on("hero_ready", function(e)
	if IsPlayer(e.player) then
		StateLocomotion(e.player)
		StateParry(e.player)
	end
end)

local kLocomotionSettings = {
	["souls_combat.turn_speed_static"] = true,
	["souls_combat.turn_speed_running"] = true,
	["souls_combat.move_friction"] = true,
}

Refii.on("setting_changed", function(e)
	if kLocomotionSettings[e.key] then
		StateIdleTurn()
		for p = 1, 2 do
			StateLocomotion(p)
		end
	elseif e.key == "souls_combat.parry_window_seconds" then
		for p = 1, 2 do
			StateParry(p)
		end
	end
end)
