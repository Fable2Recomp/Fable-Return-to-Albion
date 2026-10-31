-- First Person: the gameplay camera, from the hero's eyes.
--
-- Runs after the camera scripts each time they run (a session's first level, a save load, a
-- co-op join), so everything here is idempotent.
--
-- The game already has a first-person camera (camera/firstpersoncamera.lua, FirstPersonCamera):
-- the "look around" view the first-person button gives, where the hero stands still and both
-- sticks turn the head. It sits at Creature.GetCollisionHeadPosition, turns the hero to face the
-- way it looks, and fades the hero's body out (here the body stays and godot/hide_head.gd hides
-- only the head). Refii's mouse look already drives it.
--
-- This mod makes that camera the everyday one while FirstPerson.On holds (init.lua's H key):
--   * IsValid: the camera manager picks the first valid camera of its normal set
--     {CutScene, CannedAction, CombatCannedAction, FirstPerson, ICS, RangedCombat, MagicSAC, SAC},
--     so cutscenes and canned actions (chests, doors, finishers) still take over, and the
--     override set (conversations, quest cameras) and the co-op set come before it anyway.
--     It also steps aside for a full cutscene (and an interactive one when the scenes setting is
--     off), and for the gun aim by default.
--   * ProcessGameAction: only the look input turns the view; the shipped look mode also turned
--     it with the left stick, which here walks the hero.
--   * FrameUpdate: the eye is lifted and pushed forward of the head (settings), and the hero
--     is turned to the view only as face_camera says.
-- The game's own first-person button keeps its shipped behaviour: the patches only act on a
-- camera this mod activated.

if FirstPersonCamera == nil or CameraFunctions == nil then
	return
end

FirstPerson = FirstPerson or {}
local F = FirstPerson

local function Setting(name, default)
	if Refii and Refii.setting then
		return Refii.setting("first_person." .. name, default)
	end
	return default
end

if F.On == nil then
	F.On = Setting("start_on", 1) ~= 0
end

local kFull = 1           -- Refii.CUTSCENE_FULL
local kFullOrScene = 3    -- Refii.CUTSCENE_FULL + Refii.CUTSCENE_INTERACTIVE

-- Whether conversations and interactive scenes stay in first person (the "scenes" setting).
local function Scenes()
	return Setting("scenes", 1) ~= 0
end

-- Whether this mod's first person should hold for the player now.
function F.Wanted(player)
	if not F.On or not Player.IsAvailable(player) then
		return false
	end
	if CameraFunctions.IsMultiplayer(player) then
		return false
	end
	if Setting("third_person_aim", 1) ~= 0 and CameraFunctions.IsRangedCombat(player) then
		return false
	end
	-- A full cutscene is the game's cinematic. An interactive one (a conversation: the hero stays
	-- in the world under the vignette) stays in first person unless the scenes setting is off.
	local kinds = Scenes() and kFull or kFullOrScene
	if Refii and Refii.in_cutscene and Refii.in_cutscene(0, kinds) then
		return false
	end
	return true
end

-- The camera scripts build FirstPersonCamera afresh each run, so the shipped methods are kept on
-- the table the first time this sees it. The wrappers are installed again on every run, so a live
-- reload ("Reload mods + Lua") puts the edited camera in.
local shipped = FirstPersonCamera.RefiiFirstPersonShipped
if shipped == nil then
	shipped = {
		IsValid = FirstPersonCamera.IsValid,
		Activate = FirstPersonCamera.Activate,
		ProcessGameAction = FirstPersonCamera.ProcessGameAction,
		FrameUpdate = FirstPersonCamera.FrameUpdate,
	}
	FirstPersonCamera.RefiiFirstPersonShipped = shipped
end

-- The overrides a scene or quest sets (camera/cameramanager.lua's override set, ahead of the
-- normal set): SIMPLE, a camera at PositionEntity looking at FocusEntity (a scene's look-at shot),
-- and GAME, the third-person camera framed on an InterestingObject (the talk camera a held LT
-- gives, a quest's hero shot). While first person holds those become the first-person camera,
-- which turns toward what they frame (FirstPersonCamera.FrameUpdate below). Everything else in the
-- set (a scene's own first-person camera, the gun aim, the cutscene cameras) stays as shipped.
for _, set in ipairs(CameraManager.CameraSetList or {}) do
	if set.OverridesCameraValidity then
		set.RefiiShippedBest = set.RefiiShippedBest or set.BestCameraFunction
		local shipped_best = set.RefiiShippedBest
		set.BestCameraFunction = function(camera_set, player)
			local camera = shipped_best(camera_set, player)
			if (camera == SimpleCamera or camera == SACCamera) and Scenes() and F.Wanted(player) then
				return FirstPersonCamera
			end
			return camera
		end
	end
end

-- What a scene's override asks the camera to look at: a point, or nil.
local function OverrideFocus(data)
	if data.FocusFunction then
		return data.FocusFunction()
	end
	local entity = data.FocusEntity or data.InterestingObject
	-- An entity answers IsAlive; a position vector does not. Natives fault on a dead one.
	if entity ~= nil and type(entity) == "userdata" and entity.IsAlive and entity:IsAlive() then
		return CameraFunctions.GetObjectPosition(entity)
	end
	return nil
end

do
	local shipped_valid = shipped.IsValid
	FirstPersonCamera.IsValid = function(player)
		return shipped_valid(player) or F.Wanted(player)
	end

	-- Ours when the game's own first-person state does not hold: the shipped look mode stays as
	-- shipped.
	local shipped_activate = shipped.Activate
	FirstPersonCamera.Activate = function(self, player_entity, camera, previous_camera)
		shipped_activate(self, player_entity, camera, previous_camera)
		self.ModDriven = not CameraFunctions.IsInFirstPerson(player_entity)
		if self.ModDriven then
			self:SetFOV(Setting("fov", 80))
		end
	end

	local shipped_action = shipped.ProcessGameAction
	FirstPersonCamera.ProcessGameAction = function(self, action, control_x, control_y)
		if not self.ModDriven then
			return shipped_action(self, action, control_x, control_y)
		end
		if action == EGameAction.GAME_ACTION_ROTATE_CAMERA then
			self.RotateX = self.RotateX + control_x
			self.RotateY = self.RotateY + control_y
		end
	end

	local shipped_update = shipped.FrameUpdate
	FirstPersonCamera.FrameUpdate = function(self)
		if not self.ModDriven then
			return shipped_update(self)
		end

		-- UpdateRotations reads the class's bound, so it is ours only for this call.
		local bound = FirstPersonCamera.VerticalAngularBound
		FirstPersonCamera.VerticalAngularBound = Setting("pitch_limit", 75)
		self:UpdateRotations()
		FirstPersonCamera.VerticalAngularBound = bound

		local forward = Physics.GetFacingVector(self.Camera)
		forward:SetZ(0)
		if forward:GetLengthXY() > 0.001 then
			forward:Normalise()
		end

		local position = FirstPersonCamera.GetDesiredPosFromPlayer(self.Player)
			+ forward * Setting("eye_forward", 0.12)
			+ CVector3(0, 0, Setting("eye_height", 0.05))
		if Camera.IsOverWaterAtPosition(self.Camera, position) then
			local water_height = Camera.GetWaterPositionAtPosition(self.Camera, position):GetZ() + 0.25
			position:SetZ(math.max(position:GetZ(), water_height))
		end
		self:SetDesiredPosition(position)

		-- A scene's look-at: face what it frames (the speaker's mouth) until the player looks away.
		if not self.HasTurned then
			local data = self:GetOverriddenData()
			local focus = OverrideFocus(data)
			if focus ~= nil then
				self:SetDesiredTarget(focus + CVector3(0, 0, data.FocusVerticalOffset or 0))
			end
		end

		-- 0 never, 1 while standing (walking keeps the game's own turn toward the stick), 2 always.
		local face = Setting("face_camera", 1)
		if face >= 2 or (face == 1 and Physics.GetVelocity(self.Player):GetLengthXY() < 0.5) then
			Physics.SetFacingVector(self.Player, forward)
		end

		-- Read every update, so the Options slider applies live; CameraBase eases toward it.
		self:SetDesiredFOV(Setting("fov", 80))

		-- No fade: the body stays in view, and godot/hide_head.gd takes the head away.
		self:SetDesiredDOF(CameraFunctions.AssembleDOFData(50, 50, 50, 50, 0, 0.6))

		CameraBase.FrameUpdate(self)
	end
end
