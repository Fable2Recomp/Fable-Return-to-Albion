-- First Person: the game's first-person camera, adjusted for a player who holds it.
--
-- Runs after the camera scripts each time they run (a session's first level, a save load, a
-- co-op join), so everything here is idempotent.
--
-- godot/first_person.gd holds the game's own first-person camera (camera/firstpersoncamera.lua,
-- FirstPersonCamera) per player through camera_override, with the field of view, the pitch limit
-- and the gun-aim rule as the hold's options, and states each hold as the player value
-- first_person.held. The game's camera takes no data for the rest, so these wraps state it, on a
-- camera whose player holds first person (Refii.player_value(p, "first_person.held")) and that the
-- game's own look mode (the left bumper) did not open:
--   * ProcessGameAction: only the look input turns the view; the shipped look mode also turned it
--     with the left stick, which here walks the hero.
--   * FrameUpdate: the eye is lifted and pushed forward of the head (settings), the hero is turned
--     to the view only as face_camera says, and the shipped body fade is left out (the core hides
--     the head while the camera is first person).
--   * Scenes: a scene's look-at and framed shots (SIMPLE and GAME in the override set) become the
--     first-person camera for a player who holds it, facing what they frame until the player looks
--     away (the scenes setting; never in a full cutscene).
-- Each camera is its player's instance (self.Player is that player's hero), so both players of a
-- split screen or an online game are served alike.

if FirstPersonCamera == nil or CameraFunctions == nil or Refii == nil then
	return
end

local function Setting(name, default)
	return Refii.setting("first_person." .. name, default)
end

-- The player (1 or 2) whose hero `hero` is; 0 for anything else.
local function PlayerOf(hero)
	local handle = hero ~= nil and Refii.handle(hero) or nil
	return handle ~= nil and Refii.player_of(handle) or 0
end

-- Whether hero's player holds first person through this mod now: the hold godot/first_person.gd
-- states, as player p's value first_person.held.
local function Holds(hero)
	local p = PlayerOf(hero)
	return (p == 1 or p == 2) and (Refii.player_value(p, "first_person.held", 0) or 0) ~= 0
end

-- The camera scripts build FirstPersonCamera afresh each run, so the shipped methods are kept on
-- the table the first time this sees it. The wrappers are installed again on every run, so a live
-- reload ("Reload mods + Lua") puts the edited camera in.
local shipped = FirstPersonCamera.RefiiFirstPersonShipped
if shipped == nil then
	shipped = {
		Activate = FirstPersonCamera.Activate,
		ProcessGameAction = FirstPersonCamera.ProcessGameAction,
		FrameUpdate = FirstPersonCamera.FrameUpdate,
	}
	FirstPersonCamera.RefiiFirstPersonShipped = shipped
end

-- The overrides a scene or quest sets (camera/cameramanager.lua's override set, ahead of the normal
-- set): SIMPLE, a camera at PositionEntity looking at FocusEntity (a scene's look-at shot), and
-- GAME, the third-person camera framed on an InterestingObject (the talk camera a held LT gives, a
-- quest's hero shot). Such an override replaces the hold; for a player who holds first person it
-- becomes the first-person camera, which turns toward what it frames (FrameUpdate below).
for _, set in ipairs(CameraManager.CameraSetList or {}) do
	if set.OverridesCameraValidity then
		set.RefiiShippedBest = set.RefiiShippedBest or set.BestCameraFunction
		local shipped_best = set.RefiiShippedBest
		set.BestCameraFunction = function(camera_set, hero)
			local camera = shipped_best(camera_set, hero)
			if (camera == SimpleCamera or camera == SACCamera) and Setting("scenes", 1) ~= 0 and Holds(hero) then
				local p = PlayerOf(hero)
				if not Refii.in_cutscene(p, Refii.CUTSCENE_FULL) then
					return FirstPersonCamera
				end
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
	-- Ours for a player who holds first person, unless the game's own look mode opened it: that
	-- stays as shipped.
	local shipped_activate = shipped.Activate
	FirstPersonCamera.Activate = function(self, hero, camera, previous_camera)
		shipped_activate(self, hero, camera, previous_camera)
		self.ModDriven = Holds(hero) and not CameraFunctions.IsInFirstPerson(hero)
		if self.ModDriven then
			-- The hold states this; a scene's camera turned first person carries the scene's data.
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

		-- UpdateRotations bounds the pitch by the hold's BoundAngle; a scene's data that states no
		-- bound falls back to the class's, which is the pitch limit only for this call.
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

		-- No fade: the body stays in view, and the core takes the head away.
		self:SetDesiredDOF(CameraFunctions.AssembleDOFData(50, 50, 50, 50, 0, 0.6))

		CameraBase.FrameUpdate(self)
	end
end
