-- Portal Gun: while portal mode holds, the camera stays where the mouse puts it.
--
-- Runs after the camera scripts each time they run, so everything here is idempotent. Two cameras can be
-- up while the gun is in hand, and each has its own pull:
--
-- The gun's aim (camera/rangedcombatcamera.lua) chases the aim point: outside a cone of FOV * 0.5 *
-- Tightness round its direction it is pulled toward that point, turned OffsetDegrees to the side, and its
-- elevation stops at AngleLimit (30 degrees). In portal mode the cone is narrow, so a controller's stick
-- turns the camera with the aim point; the offset is nothing, so the aim is the middle of the screen,
-- where the portal goes; the elevation reaches further.
--
-- The everyday camera (camera/stringandcagecamera.lua, SACCamera) hangs on a string from a focal point
-- that slides about and takes in whoever is near, blends into a high combat camera near a foe, and decays
-- its pitch back to level on every update. In portal mode, and only for a camera framing one hero, the
-- distance is fixed, foes and passers-by are hidden from the frame, the pitch stays where it was put over
-- a wider range, and the focus slides over the hero's shoulder.
--
-- A held click stays on the everyday camera (six seconds of it never reached the gun's aim), so its pitch
-- is the pull a player fights. Every override wraps a method the camera's own update calls by name, so it
-- holds from the next update.

local function PortalMode()
	return PortalGun ~= nil and PortalGun.On == true
end

if RangedCombatCamera ~= nil then
	local NARROW_CONE = 0.1
	local ELEVATION = math.rad(70)

	-- The camera scripts build RangedCombatCamera afresh each run, so a table without the mark is a new one.
	if not RangedCombatCamera.PortalGunWrapped then
		RangedCombatCamera.PortalGunWrapped = true

		local shipped_elevation = RangedCombatCamera.AngleLimit
		local tightness = RangedCombatCamera.GetTightness
		RangedCombatCamera.GetTightness = function(self)
			-- Asked just before the elevation is bounded.
			self.AngleLimit = PortalMode() and ELEVATION or shipped_elevation
			if PortalMode() then
				return NARROW_CONE
			end
			return tightness(self)
		end

		local offset = RangedCombatCamera.GetOffsetDegrees
		RangedCombatCamera.GetOffsetDegrees = function(self)
			if PortalMode() then
				return 0
			end
			return offset(self)
		end
	end
end

if SACCamera == nil or CameraFunctions == nil then
	return
end

local DISTANCE = 3.2
local SHOULDER = 0.95       -- metres the focus slides to the camera's right
local LIFT = 0.25           -- and up
local EASE = 0.12           -- of the way there each update
local PITCH_UP, PITCH_DOWN = 3.0, 1.2

local function Aiming(camera)
	return PortalMode() and not CameraFunctions.IsMultiplayer(camera.Player)
end

local Shaped = { "StringLength", "CageRadius", "MaxCageRadius", "SlotWidth", "SlotDepth", "TrackAmount" }

-- Fixes one zoom level while aiming and puts back what it found afterwards. Souls Combat, when it
-- is enabled, has already fixed them for good.
local function Shape(level, aiming, distance)
	if type(level) ~= "table" or SoulsCamera ~= nil or (level.PortalShipped ~= nil) == aiming then
		return
	end
	if aiming then
		level.PortalShipped = {}
		for _, key in ipairs(Shaped) do
			level.PortalShipped[key] = level[key]
		end
		level.StringLength = distance
		level.CageRadius = distance
		level.MaxCageRadius = distance
		level.SlotWidth = 0
		level.SlotDepth = 0
		level.TrackAmount = 0
	else
		for _, key in ipairs(Shaped) do
			level[key] = level.PortalShipped[key]
		end
		level.PortalShipped = nil
	end
end

-- The camera scripts build SACCamera afresh each run, so a table without the mark is a new one.
if not SACCamera.PortalGunWrapped then
	SACCamera.PortalGunWrapped = true

	local calculate_active = SACCamera.CalculateActiveZoomLevel
	SACCamera.CalculateActiveZoomLevel = function(self)
		local aiming = Aiming(self)
		Shape(self.MyExteriorZoomLevels and self.MyExteriorZoomLevels[1], aiming, DISTANCE)
		Shape(self.MyInteriorZoomLevels and self.MyInteriorZoomLevels[1], aiming, DISTANCE * 0.85)
		return calculate_active(self)
	end

	local combat_weight = SACCamera.GetDesiredCombatCamWeight
	SACCamera.GetDesiredCombatCamWeight = function(self)
		if Aiming(self) then
			return 0
		end
		return combat_weight(self)
	end

	local empty = {}
	local centre_and_cage = SACCamera.CalculateCentreAndCage
	-- The focus, moved over the shoulder by how far the ease has got.
	local function Shouldered(self, centre)
		local forward = self.Camera and Physics.GetFacingVector(self.Camera)
		local share = self.PortalShoulder or 0
		if forward == nil or centre == nil or share <= 0 then
			return centre
		end
		return centre + CVector3(forward:GetY() * SHOULDER * share, -forward:GetX() * SHOULDER * share, LIFT * share)
	end

	SACCamera.CalculateCentreAndCage = function(self)
		local aiming = Aiming(self)
		self.PortalShoulder = (self.PortalShoulder or 0) + ((aiming and 1 or 0) - (self.PortalShoulder or 0)) * EASE
		if not aiming then
			if self.PortalShoulder < 0.01 then
				self.PortalShoulder = 0
				return centre_and_cage(self)
			end
			local centre, cage = centre_and_cage(self)
			return Shouldered(self, centre), cage
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
		return Shouldered(self, centre), cage
	end

	-- GetHeight clamps ManualHeightOffset to the class's bounds and then decays it toward 0; the
	-- clamped value is put back after it, and the bounds are wider for the call.
	local get_height = SACCamera.GetHeight
	SACCamera.GetHeight = function(self, current_pos, centre, current_cage_radius)
		if not Aiming(self) then
			return get_height(self, current_pos, centre, current_cage_radius)
		end
		local upper, lower = SACCamera.HeightOffsetUpperBound, SACCamera.HeightOffsetLowerBound
		SACCamera.HeightOffsetUpperBound, SACCamera.HeightOffsetLowerBound = PITCH_UP, PITCH_DOWN
		local held = self.ManualHeightOffset
		local ok, height = pcall(get_height, self, current_pos, centre, current_cage_radius)
		SACCamera.HeightOffsetUpperBound, SACCamera.HeightOffsetLowerBound = upper, lower
		if not ok then
			error(height, 0)
		end
		if held ~= nil then
			self.ManualHeightOffset = math.min(math.max(held, -PITCH_DOWN), PITCH_UP)
		end
		return height
	end
end
