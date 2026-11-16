-- Souls Combat: cast recovery.
--
-- CAST RECOVERY: a launched spell holds its caster in the cast for a moment, so a step back
-- between casts is not free.
--   spell_launched — the cast action is the caster's current action as its spell is created; it
--     is held for cast_recovery_seconds from this launch (0 turns the rule off). Every slot of a
--     charged release's cascade fires spell_launched; each restarts the hold.
--   action_move_interrupt — every frame the game would let the stick end that action (its own
--     test answered yes); cancelling holds it: the animation plays on and the stick neither
--     cancels it nor moves the body. The action is matched by handle, so another action reusing
--     the handle after the cast is never held past the hold's end. Once the cast animation ends
--     the hero is free, whatever is left of the hold.

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer

-- Each player's held cast: { action, ends } (ends on the runtime's clock).
S.CastHold = {}

Refii.on("spell_launched", function(e)
	local p = e.player
	if not IsPlayer(p) then
		return
	end
	local seconds = Setting("cast_recovery_seconds", 0.6)
	local action = seconds > 0 and Refii.current_action(e.caster) or nil
	S.CastHold[p] = action ~= nil and { action = action, ends = Refii.now() + seconds } or nil
end)

Refii.on("action_move_interrupt", function(e)
	local p = e.player
	local hold = IsPlayer(p) and S.CastHold[p] or nil
	if hold == nil then
		return
	end
	if Refii.now() >= hold.ends then
		S.CastHold[p] = nil
		return
	end
	if e.action == hold.action then
		return true
	end
end)
