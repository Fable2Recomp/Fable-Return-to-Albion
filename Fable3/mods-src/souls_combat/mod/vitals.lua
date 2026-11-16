-- Souls Combat: each hero's health in the player store, for the health bar (ui/hud.json).
--
-- Fable III draws no health bar of its own. The bar is the mod's: a meter piece reading
-- player:souls_combat.health / health_max, written here each tick from Refii.health (the game's own
-- Health.Get / GetMax) when it moved, for both players, so player two's half draws player two's.

local C = require("common")

local kHealthKey, kHealthMaxKey = "souls_combat.health", "souls_combat.health_max"
local stated = { {}, {} }

Refii.on("tick", function()
	for p = 1, 2 do
		local hero = Refii.player_hero(p)
		if hero ~= nil then
			local health, max = Refii.health(hero)
			local last = stated[p]
			if health ~= nil and max ~= nil and (health ~= last.health or max ~= last.max) then
				Refii.player_set(p, kHealthKey, health)
				Refii.player_set(p, kHealthMaxKey, max)
				last.health, last.max = health, max
			end
		end
	end
end)

Refii.on("hero_gone", function(e)
	if C.IsPlayer(e.player) then
		stated[e.player] = {}
	end
end)
