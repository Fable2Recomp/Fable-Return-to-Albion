-- Quicksave: the game's own save, asked for by godot/quicksave.gd when F8 is pressed.
--
-- The save is the game's: AutoSaveGame, where Player.IsAbleToSaveGame allows one, as the game's
-- own saves are gated. The answer goes back as two
-- published values the Godot script polls: quicksave.answer (1 saving, 2 refused), then
-- quicksave.seq, the ask's own number. The copy, the load and the notices are the Godot script's.

Quicksave = Quicksave or {}
local Q = Quicksave

local SAVING, REFUSED = 1, 2

function Q.save(ask)
	local ok, able = pcall(function()
		local hero = GetPlayerHero()
		return hero ~= nil and Player.IsAbleToSaveGame(hero)
	end)
	able = ok and able == true
	if able then
		AutoSaveGame(true, true)
	end
	Refii.publish("quicksave.answer", able and SAVING or REFUSED)
	Refii.publish("quicksave.seq", ask)
end
