-- Souls Combat: the gameplay camera, Elden Ring style.
--
-- WHEN THIS RUNS. The startup body (lhLuaScriptSystem_LoadStartupScripts) runs only the camera SETUP
-- script, whose AddCameraScriptFile calls register each camera file in a list without running it. The
-- files run when a camera manager is built (lhCameraManager_Create -> Debug.ReloadCameras, the runtime's
-- rex_lhLuaScriptSystem_RunCameraScripts), every file again each time, so SACCamera, MagicSACCamera
-- and the zoom tables are new tables after every run. The runtime runs this chunk after the startup
-- scripts (SACCamera is still nil then) and again after every camera-script run, straight after the
-- core chunk re-wraps CameraManager.FrameUpdate (Fable3/src/hooks/src/mod_lua_hooks.cpp). Everything
-- here is idempotent and keyed on the table it changed.
--
-- WHAT THE SHIPPED CAMERA DOES (camera/stringandcagecamera.lua and camera/targetlockcamera.lua, both
-- SACCamera, all Lua):
--   * the camera hangs on a "string" from a focal point: CalculateStringLength is the zoom level's
--     StringLength plus how far the cage CalculateCentreAndCage returns exceeds its CageRadius; the
--     cage takes in circles round WeightedNearbyFoes, WeightedNearbyInterestingEntities(High) and
--     the script's InterestingEntities, and ApplySlot lets the focus drift inside SlotWidth,
--     SlotDepthNear and SlotDepthFar;
--   * GetDesiredCombatCamWeight rises from 0 at CombatValues.FullHeroCamDistance (10 m) to 1 at
--     FullCombatCamDistance (4 m) from the nearest foe, and CalculateActiveZoomLevel blends toward
--     CombatExteriorZoomLevel (StringLength 6.5, HeightOffset 2) by it;
--   * stick Y adds to ManualHeightOffset in ProcessGameAction (HeightAdjustSpeed 0.05), and GetHeight
--     clamps it to [-HeightOffsetLowerBound, HeightOffsetUpperBound] (0.7 each) and then decays it
--     toward 0 (GetRollingAverage with 20): the pitch springs back;
--   * a lock frames its target only while the look-at button is down (CalculateDesiredDirection:
--     DirectionStrengthHard 0.9, through AlignToDirection at InwardsRotateSpeed 250 deg/s);
--   * MagicSACCamera (camera/magiccombatcamera.lua) takes over while a spell fully charges or casts
--     (CameraFunctions.IsMagicCombat): a 6.5 m high view of the area or of the cast's target.
--
-- THE SOULS CAMERA, for every camera that frames one hero (single player and online co-op): a fixed
-- orbit distance round a hero-centred focus, no combat camera and no spell camera, no foe or villager
-- pull, a pitch that stays where the stick or mouse puts it, and the mod's lock framed. A shared
-- screen (CameraFunctions.IsMultiplayerSharedScreen) keeps the shipped camera, which frames two
-- heroes.
--
-- Every override is a wrap of a method SACCamera.FrameUpdate calls by name through self, so it holds
-- from the next update whether or not a camera is already active, and MagicSACCamera and
-- TrackingCamera (SACCamera:Create, an __index chain) see the same wraps. The orbit speed, its damping,
-- ProcessGameAction and ApplyRotateVector are not touched: the stick and the mouse
-- (Fable3/src/hooks/src/mouse_look.cpp: RotateRight / SmoothRotateRight through the mode's own
-- receiver) turn the camera as shipped.

if SACCamera == nil then
	return
end

SoulsCamera = SoulsCamera or {}
local C = SoulsCamera

local function Setting(name, default)
	if Refii and Refii.setting then
		return Refii.setting("souls_combat." .. name, default)
	end
	return default
end

-- Raised on every run and on every live change of a camera setting, so the zoom levels are reshaped
-- on the next update.
C.Generation = (C.Generation or 0) + 1

-- The class fields the updates read through self (stringandcagecamera.lua: HeightAdjustSpeed in
-- ProcessGameAction, the bounds in GetHeight, CameraMoveDamping in FrameUpdate's rolling height and
-- focus, InwardsRotateSpeed in AlignToDirection; targetlockcamera.lua: DirectionStrengthHard in
-- CalculateDesiredDirection).
function C.ApplyClassFields()
	if SACCamera == nil then
		return
	end
	SACCamera.HeightAdjustSpeed = Setting("camera_pitch_speed", 0.09)
	SACCamera.HeightOffsetUpperBound = 1.2
	SACCamera.HeightOffsetLowerBound = 0.45
	SACCamera.CameraMoveDamping = 1
	SACCamera.DirectionStrengthHard = Setting("camera_lock_tightness", 0.97)
	SACCamera.InwardsRotateSpeed = Setting("camera_lock_turn_speed", 420)
end
C.ApplyClassFields()

-- A live change of a camera setting (setting_changed) reaches the camera at once. The handler is
-- taken off and put back on each run, so a run never stacks a second one and a reload that dropped
-- the handlers gets it back.
if Refii and Refii.on then
	if C.OnSetting ~= nil and Refii.off then
		Refii.off("setting_changed", C.OnSetting)
	end
	C.OnSetting = function(e)
		if type(e.key) == "string" and string.sub(e.key, 1, 20) == "souls_combat.camera_" then
			C.Generation = C.Generation + 1
			C.ApplyClassFields()
		end
	end
	Refii.on("setting_changed", C.OnSetting)
end

-- One hero framed: anything but a shared screen. CameraFunctions.IsMultiplayerSharedScreen reads only
-- the multiplayer mode, the remote hero and CameraManager.IgnoreMultiplayerCameras.
local function Solo(camera)
	return not CameraFunctions.IsMultiplayerSharedScreen(camera.Player)
end

-- The shipped values of each zoom level the souls shape writes, kept per table so a shared screen
-- puts them back. Weak keys: a camera-script run and a region's values make new tables.
C.Shipped = C.Shipped or setmetatable({}, { __mode = "k" })
local Shaped = { "StringLength", "CageRadius", "MaxCageRadius", "SlotWidth", "SlotDepthNear", "SlotDepthFar", "TrackAmount", "HeightOffset", "FocalOffset" }

local function Shape(level, solo, distance)
	if type(level) ~= "table" then
		return
	end
	local want = solo and C.Generation or false
	if level.SoulsShape == want then
		return
	end
	local shipped = C.Shipped[level]
	if shipped == nil then
		shipped = {}
		for _, key in ipairs(Shaped) do
			shipped[key] = level[key]
		end
		C.Shipped[level] = shipped
	end
	if solo then
		level.StringLength = distance
		level.CageRadius = distance
		level.MaxCageRadius = distance
		level.SlotWidth = 0
		level.SlotDepthNear = 0
		level.SlotDepthFar = 0
		level.TrackAmount = 0
		level.HeightOffset = Setting("camera_height", 0.9)
		level.FocalOffset = Setting("camera_focus_height", 1.6)
	else
		for _, key in ipairs(Shaped) do
			level[key] = shipped[key]
		end
	end
	level.SoulsShape = want
end

local function ShapeSet(set, solo, distance)
	if type(set) ~= "table" then
		return
	end
	for _, level in ipairs(set) do
		Shape(level, solo, distance)
	end
end

-- The mod's lock (lockon.lua) on the hero this camera follows: SoulsCombat.Locked, keyed by the hero's
-- handle.
local function SoulsLocked(camera)
	local locked = SoulsCombat and SoulsCombat.Locked
	if locked == nil or camera.Player == nil or Refii == nil or Refii.handle == nil then
		return false
	end
	local hero = Refii.handle(camera.Player)
	return hero ~= nil and locked[hero] ~= nil
end

-- The camera scripts build SACCamera afresh each run (`SACCamera = CameraBase:Create(...)`), so a table
-- other than the one last wrapped is a new one to wrap.
if C.WrappedSAC ~= SACCamera then
	C.WrappedSAC = SACCamera

	-- The explore zoom levels (EstablishMyZoomLevels: MyExteriorZoomLevels holds CameraExploreClose and
	-- CameraExploreFar, the shipped ExteriorZoomLevels or a region's own; MyInteriorZoomLevels holds
	-- CameraInternal or InteriorZoomLevels) are reshaped before the active level is picked from them.
	-- Sprint, drop, vault, fall, hand-holding, Assassin's Rush, conversation and troll levels stay
	-- shipped.
	local calculate_active = SACCamera.CalculateActiveZoomLevel
	SACCamera.CalculateActiveZoomLevel = function(self)
		local solo = Solo(self)
		local distance = Setting("camera_distance", 4)
		ShapeSet(self.MyExteriorZoomLevels, solo, distance)
		ShapeSet(self.MyInteriorZoomLevels, solo, distance * 0.85)
		return calculate_active(self)
	end

	-- No combat camera: the blend weight toward CombatZoomLevel is nothing for one hero.
	local combat_weight = SACCamera.GetDesiredCombatCamWeight
	SACCamera.GetDesiredCombatCamWeight = function(self)
		if Solo(self) then
			return 0
		end
		return combat_weight(self)
	end

	-- A hero-centred frame: the foes and passers-by the cage would take in are hidden from it for the
	-- call. The script's own InterestingEntities (a quest's, a conversation villager's) still count.
	local empty = {}
	local centre_and_cage = SACCamera.CalculateCentreAndCage
	SACCamera.CalculateCentreAndCage = function(self)
		if not Solo(self) then
			return centre_and_cage(self)
		end
		local foes = self.WeightedNearbyFoes
		local near = self.WeightedNearbyInterestingEntities
		local high = self.WeightedNearbyInterestingEntitiesHigh
		self.WeightedNearbyFoes = empty
		self.WeightedNearbyInterestingEntities = empty
		self.WeightedNearbyInterestingEntitiesHigh = empty
		local ok, centre, cage = pcall(centre_and_cage, self)
		self.WeightedNearbyFoes = foes
		self.WeightedNearbyInterestingEntities = near
		self.WeightedNearbyInterestingEntitiesHigh = high
		if not ok then
			error(centre, 0)
		end
		return centre, cage
	end

	-- The pitch holds: GetHeight clamps ManualHeightOffset and then decays it toward 0; the clamped
	-- value is put back after it.
	local get_height = SACCamera.GetHeight
	SACCamera.GetHeight = function(self, current_pos, centre, cage_radius)
		local held = self.ManualHeightOffset
		local height = get_height(self, current_pos, centre, cage_radius)
		if held ~= nil then
			self.ManualHeightOffset = math.min(math.max(held, -self.HeightOffsetLowerBound), self.HeightOffsetUpperBound)
		end
		return height
	end

	-- The mod's lock is framed. CalculateDesiredDirection frames a TARGET_LOCK only while the look-at
	-- button is down, and the mod's lock holds the targeting's force byte without it; otherwise it
	-- clears TargetLockTarget, which UpdateTarget (ValidateTarget) set this update from
	-- Targeting.GetTarget while the hero holds PLAYER_MODE_TARGET_LOCK. When the shipped pass frames
	-- nothing and the mod's lock holds that target, the shipped lock branch's own statements are made:
	-- the target's direction (CalculateTargetLockDesiredVector) at DirectionStrengthHard, aligning
	-- avoided, the target as the vertical tilt (CalculateTargetLockTargetPosition). A quest's look-at
	-- point and the game's held lock frame as shipped.
	local desired_direction = SACCamera.CalculateDesiredDirection
	SACCamera.CalculateDesiredDirection = function(self)
		local target = self.TargetLockTarget
		desired_direction(self)
		if self.DesiredTargetDirection ~= nil or target == nil or not target:IsAlive() then
			return
		end
		if not Solo(self) or not self:IsTargetLocking() or not SoulsLocked(self) then
			return
		end
		self.TargetLockTarget = target
		local direction = self:CalculateTargetLockDesiredVector()
		if direction == nil then
			self.TargetLockTarget = nil
			return
		end
		self.DesiredTargetDirection = direction
		self.AvoidAligning = true
		self.DesiredDirectionStrength = SACCamera.DirectionStrengthHard
		self.TiltVerticalTarget = self:CalculateTargetLockTargetPosition()
	end
end

-- No spell camera for one hero: MagicSACCamera's validity, which only the normal camera set asks
-- (cameramanager.lua GetHighestPriorityCamera), is false, so charging and casting keep the orbit. The
-- override set picks it by CAMERA_MODE_MAGIC_AIM without asking (GetOverrideCamera), so a quest's or a
-- mod's stated magic camera still runs.
if MagicSACCamera ~= nil and C.WrappedMagic ~= MagicSACCamera then
	C.WrappedMagic = MagicSACCamera
	local magic_valid = MagicSACCamera.IsValid
	MagicSACCamera.IsValid = function(player)
		if not CameraFunctions.IsMultiplayerSharedScreen(player) then
			return false
		end
		return magic_valid(player)
	end
end
