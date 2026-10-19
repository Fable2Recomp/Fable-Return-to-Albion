-- Souls Combat: the gameplay camera, Elden Ring style.
--
-- Runs after the camera scripts each time they run (a session's first level, a save load, a
-- co-op join), so everything here is idempotent.
--
-- What the shipped camera does (camera/stringandcagecamera.lua, SACCamera — all Lua; the native
-- side only calls FrameUpdate and ProcessGameAction):
--   * the camera hangs on a "string" between CageRadius and StringLength from a focal point, so
--     its distance drifts between 3.4 and 5 m as the hero moves; the focal point is a slot the
--     hero may wander in, merged with circles round nearby foes and villagers, so the frame
--     slides toward whoever is close;
--   * with a foe within 10 m it blends into a high combat camera (HeightOffset 2.8, FocalOffset 1);
--   * right stick X orbits at RotateSpeed 120 deg/s; stick Y pushes ManualHeightOffset, which
--     GetHeight decays back to 0 every update — the pitch springs back to level;
--   * a lock frames the target through AlignToDirection, inside a cone of cos 0.9 at 250 deg/s.
--
-- The souls camera, single player only: a fixed orbit distance round a hero-centred focus, no
-- combat camera, no foe or villager pull, a pitch that stays where the stick puts it, and a
-- tighter lock. Co-op keeps the shipped camera, which has to frame two heroes.
--
-- Every override is a wrap of a method FrameUpdate calls by name, so it holds from the next
-- update whether or not a camera is already active.

-- The payload also runs this chunk straight after the startup scripts (the disc build loads its
-- camera files there); on the Title Update the camera scripts have not run yet at that point.
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

-- Raised by a live retune (refii_lua: RefiiSettings[...] = v; SoulsCamera.Generation = ...)
-- so the zoom levels are reshaped on the next update.
C.Generation = (C.Generation or 0) + 1

-- The class fields every update reads through `self`. The orbit speed and damping are not among
-- them: the host sets RotateSpeed/RotateDamp/HeightDamp for the input device straight after this
-- chunk (ReapplyCameraFeel), and the player's camera sensitivity scales them.
SACCamera.HeightAdjustSpeed = Setting("camera_pitch_speed", 0.09)
SACCamera.HeightOffsetUpperBound = 1.2
SACCamera.HeightOffsetLowerBound = 0.45
SACCamera.CameraMoveDamping = 1
SACCamera.DirectionStrengthHard = Setting("camera_lock_tightness", 0.97)
SACCamera.InwardsRotateSpeed = Setting("camera_lock_turn_speed", 420)

local function Solo(camera)
	return not CameraFunctions.IsMultiplayer(camera.Player)
end

-- The shipped values of each zoom level the souls shape writes, kept per table so co-op can put
-- them back. Weak keys: a re-run of the camera scripts makes new tables.
C.Shipped = C.Shipped or setmetatable({}, { __mode = "k" })
local Shaped = { "StringLength", "CageRadius", "MaxCageRadius", "SlotWidth", "SlotDepth", "TrackAmount", "HeightOffset", "FocalOffset" }

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
		level.SlotDepth = 0
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

-- The camera scripts build SACCamera afresh each run (`SACCamera = CameraBase:Create(...)`), so a
-- table without the mark is a new one to wrap.
if not SACCamera.SoulsWrapped then
	SACCamera.SoulsWrapped = true

	-- The zoom levels this camera explores with (EstablishMyZoomLevels: the shipped tables, or a
	-- region's own when it has one) are reshaped before the active level is worked out from them.
	local calculate_active = SACCamera.CalculateActiveZoomLevel
	SACCamera.CalculateActiveZoomLevel = function(self)
		local solo = Solo(self)
		local distance = Setting("camera_distance", 4)
		Shape(self.MyExteriorZoomLevels and self.MyExteriorZoomLevels[1], solo, distance)
		Shape(self.MyInteriorZoomLevels and self.MyInteriorZoomLevels[1], solo, distance * 0.85)
		return calculate_active(self)
	end

	-- No combat camera: the blend weight toward it is nothing in single player.
	local combat_weight = SACCamera.GetDesiredCombatCamWeight
	SACCamera.GetDesiredCombatCamWeight = function(self)
		if Solo(self) then
			return 0
		end
		return combat_weight(self)
	end

	-- A hero-centred frame: the foes and passers-by the cage would take in are hidden from it for
	-- the call. A quest's own interesting entities still count.
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

	-- The pitch holds: GetHeight clamps ManualHeightOffset and then decays it toward 0; the
	-- clamped value is put back after it.
	local get_height = SACCamera.GetHeight
	SACCamera.GetHeight = function(self, current_pos, centre, current_cage_radius)
		local held = self.ManualHeightOffset
		local height = get_height(self, current_pos, centre, current_cage_radius)
		if held ~= nil then
			self.ManualHeightOffset = math.min(math.max(held, -self.HeightOffsetLowerBound), self.HeightOffsetUpperBound)
		end
		return height
	end
end
