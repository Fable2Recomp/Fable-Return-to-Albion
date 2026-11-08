-- Minimap: what godot/minimap.gd needs that only the guest's Lua can answer.
--
-- The map itself, the hero and the quests come from the host (RefiiHost.minimap). This publishes
-- the clock and player one's nearby enemies:
--   minimap.hour             the game clock, 0..24
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

local function scan()
	publish("minimap.hour", Refii.time_of_day())
	local n = 0
	local hero = Refii.player_hero(1)
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
end)
