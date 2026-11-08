-- Skateboard: riding, tricks and the score, as the mod's own physics over the game's placement.
--
-- While riding, the hero's feet are planted by the movement-speed override and gravity is off, and each
-- tick this works out a heading, a speed and a height and states them. The game's physics answers one
-- question, whether a line is clear, so the ground under the board is found by halving a ray down, and a
-- wall by rays ahead. godot/skate.gd reads the stick as Skate.stick(x, y), draws the board under the hero
-- and shows the score.
--
-- The hero stands side-on to the board, the way the board is going on their left, so the plain idle's
-- feet, apart from left to right, are both on the deck. The idle is held as the shipped scripts hold a
-- looped animation, a LOOP action, or the game's own idling would soon move them off it. The game refuses
-- it while the body is busy, as it is getting up, and anything may end it, so it is put back when it's gone.
--
-- Speed is what the rest hangs on: it carries, slopes add to it and take from it, turning at speed is wider
-- and bleeds a little, and a wall met at an angle turns the board along it. The two trucks ride the ground
-- separately, which smooths the height over bumps and gives the slope.
--
-- In the air the way of travel stays put and the stick spins the hero and board. A landing within
-- LAND_TOLERANCE of either end of the board rolls on, backward on a half turn (switch), and banks the
-- combo: its points times its count of tricks. Up to LAND_SKETCHY, or with a grab still held or a flip nearly
-- done, it is sketchy: half the points and much of the speed go, but the run goes on. Anything worse is a
-- bail: a Force Push's knockdown, then the hero steps back on, and the combo is lost.
--
-- A manual, held on the ground, or a landing with it held, keeps the combo going instead of banking it, for
-- as long as the balance holds. A grind has no rails to read, so it finds its own: the heights of a grid of
-- points around the hero, a step between two of them being an edge, whose line is then found by halving
-- across it at either side. Each tick the same halving follows it, until it runs out and the hero pops off.
-- Both are the same balance, a lean that grows by itself and which the stick pushes back.
--
-- The board is one player's at a time: the one whose K (or LB + Back) stepped on, S.Player, published as
-- skate.player. Only that player's actions ride it, and the other player's toggle does nothing meanwhile.
--
-- Published for godot/skate.gd: skate.on, skate.player, skate.heading, skate.air, skate.bailing, skate.flip
-- and skate.flip_seq (a flip's kind, 1 to 4, counted so a repeat shows), skate.grab (0, or a grab's kind),
-- skate.trick and skate.trick_seq (the latest trick's id, a spin's being its degrees), skate.combo,
-- skate.count, skate.total, skate.landed and skate.landed_seq (what a landing banked, -1 for a bail),
-- skate.sketchy (the landing was), skate.balance (0, 1 for a manual, 2 for a grind), skate.lean (-1 to 1),
-- skate.pitch (the board's nose up, in radians), skate.roll (-1 to 1, into a turn), skate.speed, skate.pushing
-- (1 while the stick is pushing), and for the
-- sounds skate.hop_seq and skate.land_seq, counting each hop and landing.

Skate = Skate or {}
local S = Skate
S.Player = S.Player or 1

local TURN_SLOW, TURN_FAST = 2.7, 1.5 -- radians a second at full stick, at a standstill and at TOP
local SPIN = 7.5            -- the same, in the air
local STEER_EASE, SPIN_EASE = 10.0, 16.0 -- how fast the stick's turn comes in, on the ground and in the air
local CARVE_LOSS = 0.1      -- of the speed lost each second of a full turn at TOP
local PUSH = 7.0            -- metres a second gained each second pushing
local TOP = 9.0             -- metres a second that pushing reaches, on the flat
local ROLL_MOST = 14.0      -- metres a second a hill can take it to
local HILL = 6.0            -- metres a second gained each second, by slope
local ROLL_DRAG = 0.2       -- of the speed lost each second, rolling
local WHEELBASE = 0.3       -- each truck from the middle, where the ground is looked for
local RIDE_EASE = 18.0      -- how fast the deck follows the ground
local WALL_ANGLES = { 0.35, -0.35, 0.7, -0.7 } -- radians to try turning along a wall
local WALL_TURN = 0.7       -- of that angle the board takes
local WALL_LOSS = 0.25      -- of the speed lost per WALL_ANGLES[1] of it
local WALL_BAIL = 6.0       -- metres a second at which a wall met square on knocks the hero down
local BRAKE = 7.0           -- metres a second lost each second braking
local GRAVITY = 16.0
local OLLIE = 5.2           -- metres a second upward
local RIDE_HEIGHT = 0.11    -- the deck above the ground
local REACH_UP, REACH_DOWN = 0.7, 1.4   -- how far the ground is looked for, from the deck
local STEP = 0.28           -- the most the ground may rise at once before it counts as a wall
local WALL_RAYS = { 0.45, 0.9 } -- heights above the deck that a wall is looked for at
local STANCE = "Idle"

local FLIP_TIME = 0.36      -- seconds a flip takes, as godot/skate.gd draws it
local LAND_TOLERANCE = math.rad(40)
local LAND_SKETCHY = math.rad(70)
local FLIP_GRACE = 0.12     -- seconds of a flip that may be left for a landing still to be sketchy
local LAND_KEEP, SKETCHY_KEEP = 0.93, 0.6 -- of the speed kept
local BAIL_TIME = 2.6       -- seconds on the ground before stepping back on, at least
local BAIL_MOST = 6.0       -- and at most, should the hero's getting up never seem to end
local STANCE_EVERY = 0.25   -- seconds between looks at whether the stance still holds
local GRAB_RATE = 250       -- points a second a grab is held
local MANUAL_SPEED = 1.5    -- metres a second it takes to get the nose up
local MANUAL_ID, MANUAL_POINTS, MANUAL_RATE = 9, 100, 200
local GRIND_ID, GRIND_POINTS, GRIND_RATE = 10, 150, 300
local GUILD_ID, GUILD_POINTS, GUILD_AFTER = 11, 1000, 2.5 -- a grind held this many seconds on one ledge
local GRIND_SPEED = 4.0     -- metres a second a grind holds at the least
local GRIND_HEIGHT = 0.05   -- the feet above the edge
local GRIND_ARM = 0.6       -- seconds a press goes on looking for an edge
local EDGE_DROP = 0.18      -- the step in the ground that makes an edge
local REACH_ON = 0.35       -- how far above the deck an edge can be and still be landed on
local REACH_DOWN_ON = 1.3   -- and below it
local LEAN_START = 0.15
local LEAN_GROW = 1.6       -- of the lean added to itself each second, in a manual
local LEAN_GROW_GRIND = 0.8 -- and in a grind
local LEAN_HARDER = 0.12    -- that, more by this each second held
local LEAN_STICK = 3.2      -- lean a second at full stick
local LEAN_NOISE = 0.5
local KICK = 1              -- the flips and grabs by the stick: none or left, right, back, forward
local FLIP_POINTS = { 100, 100, 150, 200 }
local GRAB_POINTS = { 150, 150, 200, 200 }
local GRAB_IDS = 4          -- a grab's trick id is its kind plus this

S.StickX, S.StickY = 0, 0
S.Total = S.Total or 0
S.Queue = S.Queue or {}
S.FlipSeq = S.FlipSeq or 0
S.LandedSeq = S.LandedSeq or 0
S.TrickSeq = S.TrickSeq or 0
S.HopSeq = S.HopSeq or 0
S.LandSeq = S.LandSeq or 0

function S.stick(x, y)
	S.StickX, S.StickY = x, y
end

local function wrap(angle)
	return (angle + math.pi) % (2 * math.pi) - math.pi
end

local function clear_ahead(x, y, z, nx, ny, dx, dy)
	for _, height in ipairs(WALL_RAYS) do
		if not Refii.line_of_sight(x, y, z + height, nx + dx * 0.3, ny + dy * 0.3, z + height) then
			return false
		end
	end
	return true
end

-- The ground's height under a point, or nil when there is none within reach.
local function ground(x, y, z, steps)
	local top, bottom = z + REACH_UP, z - REACH_DOWN
	if Refii.line_of_sight(x, y, top, x, y, bottom) then
		return nil
	end
	for _ = 1, steps or 9 do
		local middle = (top + bottom) / 2
		if Refii.line_of_sight(x, y, top, x, y, middle) then
			top = middle
		else
			bottom = middle
		end
	end
	return (top + bottom) / 2
end

local function hold_stance(body)
	Action.FinishScriptedActions(body)
	local pose = { Type = EScriptableAction.PLAY_ANIMATION, Anim = STANCE, OverrideLooking = true, BlendInTime = 0.2 }
	Action.SetCurrentAction(body, { Type = EScriptableAction.LOOP, IntoAction = pose, LoopAction = pose,
		OutOfAction = pose, NumLoops = 100000, KillFXOnFinish = true })
end

local function publish_combo()
	Refii.publish("skate.combo", S.Combo or 0)
	Refii.publish("skate.count", S.Count or 0)
	Refii.publish("skate.total", S.Total)
end

local function clear_combo()
	S.Combo, S.Count = 0, 0
	S.Queue = {}
	publish_combo()
end

local function add_trick(id, points)
	S.Combo = (S.Combo or 0) + points
	S.Count = (S.Count or 0) + 1
	S.Queue[#S.Queue + 1] = id
	publish_combo()
end

local function landed(amount, sketchy)
	S.LandedSeq = S.LandedSeq + 1
	Refii.publish("skate.sketchy", sketchy and 1 or 0)
	Refii.publish("skate.landed", amount)
	Refii.publish("skate.landed_seq", S.LandedSeq)
end

local function set_balance(kind)
	S.Balance = kind
	S.Lean, S.LeanFor = 0, 0
	if kind ~= 0 then
		S.Lean = math.random() < 0.5 and -LEAN_START or LEAN_START
	end
	Refii.publish("skate.lean", S.Lean)
	Refii.publish("skate.balance", kind)
end

-- The balance for one tick. True when it is lost.
local function lean(dt, stick)
	S.LeanFor = S.LeanFor + dt
	local grow = (S.Balance == 2 and LEAN_GROW_GRIND or LEAN_GROW) * (1 + S.LeanFor * LEAN_HARDER)
	S.Lean = S.Lean + (S.Lean * grow + stick * LEAN_STICK + (math.random() - 0.5) * LEAN_NOISE) * dt
	Refii.publish("skate.lean", S.Lean)
	return math.abs(S.Lean) > 1
end

local function reset_air()
	S.Air, S.Rise, S.Spun, S.FlipLeft, S.Grab = false, 0, 0, 0, 0
	S.Rail, S.Armed = nil, 0
	set_balance(0)
	Refii.publish("skate.grab", 0)
	Refii.publish("skate.air", 0)
end

-- Plants the hero on the board where they stand. The board keeps its way after a bail.
local function mount(hero, heading)
	local body = Refii.entity(hero)
	local facing = Physics.GetFacingVector(body)
	S.Heading = heading or math.atan2(facing:GetY(), facing:GetX())
	S.Speed, S.Switch, S.Steer, S.Slope = 0, false, 0, 0
	Refii.publish("skate.pitch", 0)
	Refii.publish("skate.roll", 0)
	S.Travel = S.Heading
	S.StanceLooked = 0
	reset_air()
	Refii.set_speed_multiplier(S.Player, 0)
	Physics.SetIgnoresGravity(body, true)
	hold_stance(body)
end

local function unmount(hero)
	Refii.clear_speed_multiplier(S.Player)
	if hero ~= nil then
		local body = Refii.entity(hero)
		Physics.SetIgnoresGravity(body, false)
		Action.FinishScriptedActions(body)
	end
end

local function step_on(hero)
	mount(hero)
	S.On, S.Bailing = true, nil
	Refii.input_claim(S.Player, "skate.ollie", true)
	Refii.input_claim(S.Player, "skate.flip", true)
	Refii.input_claim(S.Player, "skate.grab", true)
	Refii.input_claim(S.Player, "skate.manual", true)
	Refii.input_claim(S.Player, "skate.grind", true)
	Refii.input_claim(S.Player, "skate.no_lock", true)
	clear_combo()
	Refii.publish("skate.bailing", 0)
	Refii.publish("skate.on", 1)
end

local function step_off(hero)
	S.On, S.Bailing = false, nil
	unmount(hero)
	Refii.input_claim(S.Player, "skate.ollie", false)
	Refii.input_claim(S.Player, "skate.flip", false)
	Refii.input_claim(S.Player, "skate.grab", false)
	Refii.input_claim(S.Player, "skate.manual", false)
	Refii.input_claim(S.Player, "skate.grind", false)
	Refii.input_claim(S.Player, "skate.no_lock", false)
	S.ManualHeld = false
	clear_combo()
	Refii.publish("skate.bailing", 0)
	Refii.publish("skate.on", 0)
end

function S.toggle(p)
	if S.On and p ~= S.Player then
		return
	end
	local hero = Refii.player_hero(p)
	if hero == nil then
		return
	end
	if S.On then
		step_off(hero)
	else
		S.Player = p
		Refii.publish("skate.player", p)
		step_on(hero)
	end
end

local function can_trick()
	return S.On and not S.Bailing
end

function S.ollie()
	if can_trick() and not S.Air then
		S.Air, S.Rise, S.Spun, S.Rail = true, OLLIE, 0, nil
		Refii.publish("skate.pushing", 0)
		S.HopSeq = S.HopSeq + 1
		Refii.publish("skate.hop_seq", S.HopSeq)
		set_balance(0)
		Refii.publish("skate.roll", 0)
		Refii.publish("skate.air", 1)
	end
end

-- Which of the four the stick is asking for.
local function stick_kind()
	if S.StickX > 0.5 then
		return 2
	elseif S.StickY < -0.5 then
		return 3
	elseif S.StickY > 0.5 then
		return 4
	end
	return KICK
end

function S.flip()
	if not can_trick() or (S.FlipLeft or 0) > 0 then
		return
	end
	S.ollie()
	local kind = stick_kind()
	S.FlipLeft = FLIP_TIME
	S.FlipSeq = S.FlipSeq + 1
	Refii.publish("skate.flip", kind)
	Refii.publish("skate.flip_seq", S.FlipSeq)
	add_trick(kind, FLIP_POINTS[kind])
end

function S.grab(on)
	if not can_trick() then
		return
	end
	if on then
		S.ollie()
		local kind = stick_kind()
		S.Grab = kind
		Refii.publish("skate.grab", kind)
		add_trick(kind + GRAB_IDS, GRAB_POINTS[kind])
	else
		S.Grab = 0
		Refii.publish("skate.grab", 0)
	end
end

function S.grind()
	if can_trick() and not S.Rail then
		S.ollie()
		S.Armed = GRIND_ARM
	end
end

local function start_manual()
	set_balance(1)
	add_trick(MANUAL_ID, MANUAL_POINTS)
end

local function bank(share, sketchy)
	if (S.Count or 0) == 0 then
		if sketchy then
			landed(0, true)
		end
	else
		local gained = math.floor(S.Combo * S.Count * (share or 1))
		S.Total = S.Total + gained
		landed(gained, sketchy)
		-- The names still queued have to reach the display, so they are left to drain.
		S.Combo, S.Count = 0, 0
		publish_combo()
	end
end

local function bail(hero, dx, dy)
	S.Bailing, S.BailedFor = BAIL_TIME, 0
	S.Speed = 0
	reset_air()
	clear_combo()
	landed(-1)
	unmount(hero)
	Refii.publish("skate.bailing", 1)
	Refii.knockback(hero, dx, dy, 0.3)
end

-- The hero and board have come down. True when it was a bail.
local function land(hero, dx, dy)
	local off = math.abs(wrap(S.Heading - S.Travel))
	local switch = off > math.pi / 2
	if switch then
		off = math.pi - off
	end
	if off > LAND_SKETCHY or S.FlipLeft > FLIP_GRACE then
		bail(hero, dx, dy)
		return true
	end
	local sketchy = off > LAND_TOLERANCE or S.FlipLeft > 0 or S.Grab ~= 0
	S.LandSeq = S.LandSeq + 1
	Refii.publish("skate.land_seq", S.LandSeq)
	S.Heading = wrap(S.Travel + (switch and math.pi or 0))
	S.Switch = switch
	local degrees = math.floor(math.abs(S.Spun) / math.pi + 0.5) * 180
	if degrees > 0 then
		add_trick(degrees, degrees)
	end
	reset_air()
	S.Speed = S.Speed * (sketchy and SKETCHY_KEEP or LAND_KEEP)
	if not sketchy and S.ManualHeld and S.Speed >= MANUAL_SPEED then
		start_manual()
	else
		bank(sketchy and 0.5 or 1, sketchy)
	end
	return false
end

-- Where the edge is on a line from a point on top of it to one past it, at height h.
local function edge_between(ax, ay, bx, by, h)
	for _ = 1, 6 do
		local mx, my = (ax + bx) / 2, (ay + by) / 2
		if Refii.line_of_sight(mx, my, h + 0.3, mx, my, h - EDGE_DROP * 0.6) then
			bx, by = mx, my
		else
			ax, ay = mx, my
		end
	end
	return ax, ay
end

local function on_top(x, y, h)
	return not Refii.line_of_sight(x, y, h + 0.3, x, y, h - EDGE_DROP * 0.6)
end

-- An edge near the hero, running near enough the way they're going, or nil.
local function find_rail(x, y, z, dx, dy)
	local base = z - RIDE_HEIGHT
	local rx, ry = -dy, dx
	local heights = {}
	for f = 0, 2 do
		heights[f] = {}
		for s = -2, 2 do
			local px, py = x + dx * f * 0.7 + rx * s * 0.7, y + dy * f * 0.7 + ry * s * 0.7
			heights[f][s] = { px, py, ground(px, py, base, 6) }
		end
	end
	local best, best_far
	local function consider(a, b)
		local ha, hb = a[3] or -1000, b[3] or -1000
		local hi = math.max(ha, hb)
		if math.abs(ha - hb) < EDGE_DROP or hi > base + REACH_ON or hi < base - REACH_DOWN_ON then
			return
		end
		local mx, my = (a[1] + b[1]) / 2 - x, (a[2] + b[2]) / 2 - y
		local far = mx * mx + my * my
		if best == nil or far < best_far then
			best, best_far = ha > hb and { a, b, hi } or { b, a, hi }, far
		end
	end
	for f = 0, 2 do
		for s = -2, 2 do
			if s < 2 then
				consider(heights[f][s], heights[f][s + 1])
			end
			if f < 2 then
				consider(heights[f][s], heights[f + 1][s])
			end
		end
	end
	if best == nil then
		return nil
	end
	local top, low, h = best[1], best[2], best[3]
	local ox, oy = low[1] - top[1], low[2] - top[2]
	local length = math.sqrt(ox * ox + oy * oy)
	ox, oy = ox / length, oy / length
	-- The edge's line, from where it crosses two lines either side.
	local function across(along)
		local cx, cy = (top[1] + low[1]) / 2 + -oy * along, (top[2] + low[2]) / 2 + ox * along
		local ax, ay = cx - ox * 0.5, cy - oy * 0.5
		if not on_top(ax, ay, h) then
			return nil
		end
		return edge_between(ax, ay, cx + ox * 0.5, cy + oy * 0.5, h)
	end
	local x1, y1 = across(-0.3)
	local x2, y2 = across(0.3)
	if x1 == nil or x2 == nil then
		return nil
	end
	local ex, ey = x2 - x1, y2 - y1
	length = math.sqrt(ex * ex + ey * ey)
	if length < 0.1 then
		return nil
	end
	ex, ey = ex / length, ey / length
	if ex * dx + ey * dy < 0 then
		ex, ey = -ex, -ey
	end
	if ex * dx + ey * dy < 0.35 then
		return nil
	end
	-- Which side is the drop.
	local nx, ny = -ey, ex
	if nx * ox + ny * oy < 0 then
		nx, ny = -nx, -ny
	end
	local px, py = (x1 + x2) / 2, (y1 + y2) / 2
	return { x = px - nx * 0.04, y = py - ny * 0.04, h = h, dx = ex, dy = ey, nx = nx, ny = ny }
end

local function start_grind(hero, rail)
	local degrees = math.floor(math.abs(S.Spun) / math.pi + 0.5) * 180
	if degrees > 0 then
		add_trick(degrees, degrees)
	end
	local way = math.atan2(rail.dy, rail.dx)
	local switch = math.abs(wrap(S.Heading - way)) > math.pi / 2
	S.Air, S.Rise, S.Spun, S.FlipLeft, S.Grab, S.Armed = false, 0, 0, 0, 0, 0
	Refii.publish("skate.grab", 0)
	Refii.publish("skate.air", 0)
	Refii.publish("skate.pitch", 0)
	Refii.publish("skate.roll", 0)
	S.Slope = 0
	S.Rail, S.Travel, S.Switch = rail, way, switch
	S.Heading = wrap(way + (switch and math.pi or 0))
	S.Speed = math.max(S.Speed, GRIND_SPEED)
	set_balance(2)
	add_trick(GRIND_ID, GRIND_POINTS)
	Refii.set_position(hero, rail.x, rail.y, rail.h + GRIND_HEIGHT)
end

local function leave_grind(pop)
	S.Rail = nil
	S.Air, S.Rise, S.Spun, S.Armed = true, pop, 0, 0
	set_balance(0)
	Refii.publish("skate.air", 1)
end

local function grind_tick(hero, dt)
	local rail = S.Rail
	S.Combo = S.Combo + GRIND_RATE * dt
	publish_combo()
	if lean(dt, S.StickX) then
		bail(hero, rail.dx, rail.dy)
		return
	end
	if not rail.Guild and S.LeanFor >= GUILD_AFTER then
		rail.Guild = true
		add_trick(GUILD_ID, GUILD_POINTS)
	end
	local step = S.Speed * dt
	local qx, qy = rail.x + rail.dx * step, rail.y + rail.dy * step
	local h = rail.h
	if not clear_ahead(rail.x, rail.y, h + GRIND_HEIGHT, qx, qy, rail.dx, rail.dy) then
		bail(hero, rail.dx, rail.dy)
		return
	end
	local under = ground(qx - rail.nx * 0.08, qy - rail.ny * 0.08, h, 6)
	-- It has run out when there is no top ahead, or the top no longer stands above what's beside it.
	local drops = under ~= nil and Refii.line_of_sight(qx + rail.nx * 0.35, qy + rail.ny * 0.35, under + 0.3,
		qx + rail.nx * 0.35, qy + rail.ny * 0.35, under - EDGE_DROP * 0.6)
	if under == nil or math.abs(under - h) > 0.22 or not drops then
		rail.Lost = (rail.Lost or 0) + 1
		if rail.Lost >= 2 then
			leave_grind(2.2)
			return
		end
		under = h
	else
		rail.Lost = 0
	end
	local ex, ey = edge_between(qx - rail.nx * 0.35, qy - rail.ny * 0.35, qx + rail.nx * 0.35, qy + rail.ny * 0.35, under)
	local nx, ny = ex - rail.nx * 0.04, ey - rail.ny * 0.04
	local mx, my = nx - rail.x, ny - rail.y
	local moved = math.sqrt(mx * mx + my * my)
	if rail.Lost == 0 and moved > 0.02 then
		-- Follow its bends, a little at a time, so a rough collision shape doesn't shake the hero.
		local dx, dy = rail.dx * 0.7 + mx / moved * 0.3, rail.dy * 0.7 + my / moved * 0.3
		local length = math.sqrt(dx * dx + dy * dy)
		rail.dx, rail.dy = dx / length, dy / length
		local side = rail.nx * -rail.dy + rail.ny * rail.dx
		rail.nx, rail.ny = -rail.dy * (side < 0 and -1 or 1), rail.dx * (side < 0 and -1 or 1)
		rail.x, rail.y = nx, ny
	else
		rail.x, rail.y = qx, qy
	end
	rail.h = under
	Refii.set_position(hero, rail.x, rail.y, rail.h + GRIND_HEIGHT)
	S.Travel = math.atan2(rail.dy, rail.dx)
	S.Heading = wrap(S.Travel + (S.Switch and math.pi or 0))
	local hx, hy = math.cos(S.Heading), math.sin(S.Heading)
	Physics.SetFacingVector(Refii.entity(hero), CVector3(hy, -hx, 0))
	Refii.publish("skate.heading", S.Heading)
end

local function ride(hero, dt)
	local x, y, z = Refii.position(hero)
	if x == nil then
		return
	end

	S.StanceLooked = (S.StanceLooked or 0) + dt
	if S.StanceLooked >= STANCE_EVERY then
		S.StanceLooked = 0
		local body = Refii.entity(hero)
		if not Action.IsPerformingAnyAction(body) then
			hold_stance(body)
		end
	end

	if S.Rail then
		grind_tick(hero, dt)
		return
	end

	if S.Air and S.Armed > 0 then
		S.Armed = S.Armed - dt
		local rail = find_rail(x, y, z, math.cos(S.Travel), math.sin(S.Travel))
		if rail ~= nil then
			start_grind(hero, rail)
			return
		end
	end

	S.Steer = S.Steer + (S.StickX - S.Steer) * math.min(1, dt * (S.Air and SPIN_EASE or STEER_EASE))
	if S.Air then
		local turned = -S.Steer * SPIN * dt
		S.Heading = S.Heading + turned
		S.Spun = S.Spun + turned
		S.FlipLeft = math.max(0, S.FlipLeft - dt)
		if S.Grab ~= 0 then
			S.Combo = S.Combo + GRAB_RATE * dt
			publish_combo()
		end
	else
		local pace = math.min(1, S.Speed / TOP)
		local pushing = 0
		S.Heading = S.Heading - S.Steer * (TURN_SLOW + (TURN_FAST - TURN_SLOW) * pace) * dt
		S.Travel = S.Heading + (S.Switch and math.pi or 0)
		if S.Balance == 1 and (not S.ManualHeld or S.Speed < MANUAL_SPEED) then
			set_balance(0)
			bank()
		elseif S.Balance == 0 and S.ManualHeld and S.Speed >= MANUAL_SPEED then
			start_manual()
		end
		if S.Balance == 1 then
			S.Combo = S.Combo + MANUAL_RATE * dt
			publish_combo()
			if lean(dt, S.StickY) then
				bail(hero, math.cos(S.Travel), math.sin(S.Travel))
				return
			end
		elseif S.StickY > 0.2 then
			if S.Speed < TOP then
				S.Speed = math.min(TOP, S.Speed + PUSH * S.StickY * dt)
				pushing = 1
			end
		elseif S.StickY < -0.2 then
			S.Speed = math.max(0, S.Speed + BRAKE * S.StickY * dt)
		end
		S.Speed = S.Speed * (1 - (ROLL_DRAG + CARVE_LOSS * math.abs(S.Steer) * pace) * dt)
		S.Speed = math.max(0, math.min(ROLL_MOST, S.Speed - HILL * S.Slope * dt))
		Refii.publish("skate.roll", S.Steer * pace)
		Refii.publish("skate.pushing", pushing)
	end
	local dx, dy = math.cos(S.Travel), math.sin(S.Travel)

	local travel = S.Speed * dt
	local nx, ny = x + dx * travel, y + dy * travel
	if travel > 0 and not clear_ahead(x, y, z, nx, ny, dx, dy) then
		-- Along the wall, if an angle clears it.
		local turn
		if not S.Air then
			for _, angle in ipairs(WALL_ANGLES) do
				local c, a = math.cos(angle), math.sin(angle)
				local rx, ry = dx * c - dy * a, dx * a + dy * c
				if clear_ahead(x, y, z, x + rx * travel, y + ry * travel, rx, ry) then
					turn = angle
					break
				end
			end
		end
		if turn ~= nil then
			S.Heading = S.Heading + turn * WALL_TURN
			S.Travel = S.Travel + turn * WALL_TURN
			S.Speed = S.Speed * (1 - WALL_LOSS * math.abs(turn) / math.abs(WALL_ANGLES[1]))
			dx, dy = math.cos(S.Travel), math.sin(S.Travel)
			travel = S.Speed * dt
			nx, ny = x + dx * travel, y + dy * travel
		elseif not S.Air and S.Speed >= WALL_BAIL then
			bail(hero, -dx, -dy)
			return
		else
			nx, ny, S.Speed = x, y, 0
		end
	end

	local base = z - RIDE_HEIGHT
	local front = ground(nx + dx * WHEELBASE, ny + dy * WHEELBASE, base, 7)
	if front ~= nil and front - base > STEP then
		nx, ny, S.Speed = x, y, 0
		front = ground(nx + dx * WHEELBASE, ny + dy * WHEELBASE, base, 7)
	end
	local back = ground(nx - dx * WHEELBASE, ny - dy * WHEELBASE, base, 7)
	local under = front and back and (front + back) / 2 or front or back

	local deck = under and under + RIDE_HEIGHT
	local came_down = false
	if S.Air or deck == nil or z - deck > 0.12 then
		if not S.Air then
			S.Air, S.Spun = true, 0
			Refii.publish("skate.air", 1)
		end
		S.Slope = 0
		S.Rise = S.Rise - GRAVITY * dt
		z = z + S.Rise * dt
		if deck ~= nil and z <= deck and S.Rise <= 0 then
			z, came_down = deck, true
		end
		Refii.publish("skate.pitch", 0)
	else
		z = z + (deck - z) * math.min(1, dt * RIDE_EASE)
		local slope = front and back and math.max(-0.7, math.min(0.7, (front - back) / (2 * WHEELBASE))) or 0
		S.Slope = S.Slope + (slope - S.Slope) * math.min(1, dt * 5)
		Refii.publish("skate.pitch", math.atan(S.Switch and -slope or slope))
	end

	Refii.set_position(hero, nx, ny, z)
	if came_down and land(hero, dx, dy) then
		return
	end
	local hx, hy = math.cos(S.Heading), math.sin(S.Heading)
	Physics.SetFacingVector(Refii.entity(hero), CVector3(hy, -hx, 0))
	Refii.publish("skate.heading", S.Heading)
	Refii.publish("skate.speed", S.Speed)
end

Refii.on("input_action", function(e)
	if e.name == "skate.toggle" or e.name == "skate.toggle_pad" then
		if e.pressed then
			S.toggle(e.player)
		end
		return
	end
	if e.player ~= S.Player then
		return
	end
	if e.name == "skate.grab" then
		S.grab(e.pressed)
	elseif e.name == "skate.manual" then
		S.ManualHeld = e.pressed
	elseif not e.pressed then
		return
	elseif e.name == "skate.ollie" then
		S.ollie()
	elseif e.name == "skate.flip" then
		S.flip()
	elseif e.name == "skate.grind" then
		S.grind()
	end
end)

Refii.on("tick", function(e)
	if not S.On then
		return
	end
	local hero = Refii.player_hero(S.Player)
	if hero == nil or not Refii.living(hero) or Refii.in_cutscene(S.Player, Refii.CUTSCENE_FULL + Refii.CUTSCENE_INTERACTIVE) then
		step_off(hero)
		return
	end
	if not Refii.in_play(S.Player) then
		return
	end
	-- One name a tick, so each reaches the display by itself.
	if #S.Queue > 0 then
		S.TrickSeq = S.TrickSeq + 1
		Refii.publish("skate.trick", table.remove(S.Queue, 1))
		Refii.publish("skate.trick_seq", S.TrickSeq)
	end
	if S.Bailing then
		S.Bailing = S.Bailing - e.seconds
		S.BailedFor = S.BailedFor + e.seconds
		if S.Bailing <= 0 and (S.BailedFor >= BAIL_MOST or not Action.IsPerformingAnyAction(Refii.entity(hero))) then
			S.Bailing = nil
			mount(hero, S.Heading)
			Refii.publish("skate.bailing", 0)
		end
		return
	end
	ride(hero, e.seconds)
end)

Refii.on("level_unloading", function()
	if S.On then
		step_off(Refii.player_hero(S.Player))
	end
end)
