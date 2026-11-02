-- Health Bars: a bar of health over every enemy near the heroes.
--
-- Every tenth of a second this looks around each hero this machine plays for living creatures
-- the game counts as that hero's enemies (Refii.relation 4), nearest first, and publishes up to
-- MAX of them for godot/health_bars.gd, which draws the bars over their bodies:
--
--   health_bars.n          how many are published
--   health_bars.e<i>       the creature's handle (what the host's node_for takes)
--   health_bars.c<i>       its current health
--   health_bars.m<i>       its most health
--
-- Settings (settings.json, the in-game Options > Mods page): range in metres, and only_hurt to
-- leave out enemies still at full health.

HealthBars = HealthBars or {}

local MAX = 24
local EVERY = 0.1   -- seconds between looks

local published = {}

local function publish(key, value)
	if published[key] ~= value then
		published[key] = value
		Refii.publish(key, value)
	end
end

local function scan()
	if Refii.setting("health_bars.show", 1) < 0.5 then
		publish("health_bars.n", 0)
		return
	end

	local range = Refii.setting("health_bars.range", 25)
	local only_hurt = Refii.setting("health_bars.only_hurt", 0) >= 0.5
	local seen = {}
	local n = 0

	for p = 1, 2 do
		local hero = Refii.player_hero(p)
		if hero ~= nil and Refii.player_here(p) and Refii.living(hero) then
			local x, y, z = Refii.position(hero)
			for _, e in ipairs(x and Refii.near(x, y, z, range, true) or {}) do
				if n >= MAX then
					break
				end
				if not seen[e] and Refii.player_of(e) == 0 and Refii.relation(hero, e) == 4 and Refii.living(e) then
					local current, most = Refii.health(e)
					if current ~= nil and most ~= nil and most > 0 and current > 0
						and (not only_hurt or current < most) then
						seen[e] = true
						n = n + 1
						publish("health_bars.e" .. n, e)
						publish("health_bars.c" .. n, current)
						publish("health_bars.m" .. n, most)
					end
				end
			end
		end
	end

	publish("health_bars.n", n)
end

local since = 0
Refii.on("tick", function(e)
	since = since + (e.seconds or 0)
	if since < EVERY then
		return
	end
	since = 0
	scan()
end)

Refii.on("level_unloading", function()
	publish("health_bars.n", 0)
end)
