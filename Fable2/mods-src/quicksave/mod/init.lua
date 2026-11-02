-- Quicksave: the game's own save, asked for by godot/quicksave.gd when F8 is pressed.
--
-- Refii.save_game(p) is the game's AutoSaveGame(true, true) where Player.IsAbleToSaveGame allows a
-- save for p's hero, as the game's own saves are gated. Whether the save was started goes back to
-- the Godot script as the signal quicksave.answer: "saving" or "refused". The save_written event
-- that follows, the copy, the load and the notices are the Godot script's.

Quicksave = Quicksave or {}

function Quicksave.save(p)
	Refii.signal("quicksave.answer", Refii.save_game(p) and "saving" or "refused")
end
