-- Toggle Sprint: one press starts the sprint, the next press ends it, and standing still for a
-- while ends it too.
--
-- The game sprints while A is held (Shift on the keyboard holds A): the hero's receiver gets
-- GAME_ACTION_SPRINT_SET (66) when it goes down and GAME_ACTION_SPRINT_RELEASE (67) when it comes
-- up. A mod cannot send an action, only cancel one (the game_action event), so the toggle is made
-- of cancels:
--   press one   SET passes (the sprint starts), RELEASE is cancelled (the game still holds A);
--   press two   SET is cancelled (already sprinting), RELEASE passes (the sprint ends).
--
-- STANDING STILL. After stop_after seconds without moving, the game still holds A and would sprint
-- again at the first step, and no RELEASE can be sent from here. So player one's hero asks
-- godot/end_sprint.gd for a Shift tap (the published counter toggle_sprint.park_request): the game
-- reads the keyboard's sprint from Godot's key state, the tap arrives as SET and RELEASE, and the
-- rules above treat it as press two. Player two's pad has no key to tap, so their sprint stays
-- held until they press again.
--
-- One latch per player, so each co-op hero toggles on their own.

ToggleSprint = ToggleSprint or {}
local T = ToggleSprint

local kSprintSet = 66
local kSprintRelease = 67
local kStillSpeed = 1.0   -- metres a second: slower than a walk, so the slide to a stop counts

-- Per player: nil idle; "held" while the game holds the sprint for us; "ending" once the second
-- press is down and its release should reach the game.
T.State = T.State or {}
T.Still = T.Still or {}         -- per player: seconds stood still while held
T.Last = T.Last or {}           -- per player: the hero's last x, y
T.Asked = T.Asked or {}         -- per player: seconds since the tap was asked for, nil if not asked
T.Requests = T.Requests or 0    -- the counter end_sprint.gd watches

Refii.on("game_action", function(e)
	if e.action ~= kSprintSet and e.action ~= kSprintRelease then
		return
	end
	local p = Refii.player_of(e.hero)
	if p ~= 1 and p ~= 2 then
		return
	end
	local state = T.State[p]
	if e.action == kSprintSet then
		if state == "held" then
			T.State[p] = "ending"
			return true                     -- already sprinting: the game keeps its held A
		end
		T.State[p] = nil                    -- a fresh press: the game starts the sprint
		T.Still[p] = 0
		T.Asked[p] = nil
	else
		if state == "ending" then
			T.State[p] = nil                -- the second release ends the sprint
			return
		end
		T.State[p] = "held"
		T.Still[p] = 0
		T.Asked[p] = nil
		return true                         -- keep the sprint going after the button comes up
	end
end)

-- Standing still while held asks for the tap that ends the sprint (player one only: see above).
Refii.on("tick", function(e)
	local seconds = e.seconds
	if seconds <= 0 then
		return
	end
	local limit = Refii.setting("toggle_sprint.stop_after", 0.5)
	local p = 1
	local h = Refii.player_hero(p)
	if h == nil or T.State[p] ~= "held" or limit <= 0 then
		T.Last[p] = nil
		return
	end
	local x, y = Refii.position(h)
	local last = T.Last[p]
	T.Last[p] = x ~= nil and { x, y } or nil
	if x == nil or last == nil then
		return
	end
	local dx, dy = x - last[1], y - last[2]
	if math.sqrt(dx * dx + dy * dy) / seconds < kStillSpeed then
		T.Still[p] = (T.Still[p] or 0) + seconds
	else
		T.Still[p] = 0
	end
	if T.Asked[p] ~= nil then
		T.Asked[p] = T.Asked[p] + seconds
	end
	-- Asked once; again a second later if the tap never arrived (the window lost focus, say).
	if T.Still[p] >= limit and (T.Asked[p] == nil or T.Asked[p] > 1) then
		T.Requests = T.Requests + 1
		Refii.publish("toggle_sprint.park_request", T.Requests)
		T.Asked[p] = 0
	end
end)

-- A level change or a save load starts without anything held.
Refii.on("level_loaded", function()
	T.State = {}
	T.Still = {}
	T.Asked = {}
end)
