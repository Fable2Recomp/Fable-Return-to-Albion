-- Minimap: what godot/minimap.gd needs that only the guest's Lua can answer.
--
-- The map itself, the hero and the other quests come from the host (RefiiHost.minimap). This
-- publishes the clock and, around the hero of the player this machine plays (the first of 1 and 2
-- that Refii.player_here holds for: player two on an online joiner), that player's dog and the
-- enemies near:
--   minimap.hour             the game clock, 0..24
--   minimap.dog              1 when the dog's place is published, else 0
--   minimap.dog_x, _y, _z    its world position
--   minimap.n                how many enemies are listed
--   minimap.x<i>, y<i>, z<i> each one's world position

local MAX = 24
local EVERY = 0.25   -- seconds between looks

local published = {}
local waited = 0

local function publish(key, value)
	if published[key] ~= value then
		published[key] = value
		Refii.publish(key, value)
	end
end

-- The player this machine plays: the map is theirs.
local function here()
	for p = 1, 2 do
		if Refii.player_here(p) then
			return p
		end
	end
	return 1
end

local function scan_dog(p)
	local dog = Refii.player_dog(p)
	local x, y, z
	if dog ~= nil then
		x, y, z = Refii.position(dog)
	end
	if x == nil then
		publish("minimap.dog", 0)
		return
	end
	publish("minimap.dog_x", x)
	publish("minimap.dog_y", y)
	publish("minimap.dog_z", z)
	publish("minimap.dog", 1)
end

local function scan()
	publish("minimap.hour", Refii.time_of_day())
	local p = here()
	scan_dog(p)
	local n = 0
	local hero = Refii.player_hero(p)
	if hero ~= nil and Refii.setting("minimap.enemies", 1) >= 0.5 and Refii.living(hero) then
		local x, y, z = Refii.position(hero)
		for _, e in ipairs(x and Refii.near(x, y, z, Refii.setting("minimap.range", 50), true) or {}) do
			if n >= MAX then
				break
			end
			if Refii.player_of(e) == 0 and Refii.relation(hero, e) == 4 and Refii.living(e) then
				local ex, ey, ez = Refii.position(e)
				if ex ~= nil then
					n = n + 1
					publish("minimap.x" .. n, ex)
					publish("minimap.y" .. n, ey)
					publish("minimap.z" .. n, ez)
				end
			end
		end
	end
	publish("minimap.n", n)
end

Refii.on("tick", function(e)
	waited = waited + e.seconds
	if waited >= EVERY then
		waited = 0
		scan()
	end
end)

Refii.on("level_unloading", function()
	publish("minimap.n", 0)
	publish("minimap.dog", 0)
end)
