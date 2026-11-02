-- Souls Combat: cast recovery.
--
-- A module of init.lua (require("cast_recovery")).

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer

----------------------------------------------------------------------------------------------------
-- CAST RECOVERY: a launched spell holds its caster in the cast for a moment. The game holds a
-- quick cast's movement only until the spell launches; from then any stick input cancels the rest
-- of the animation, which made a step back between casts free.
--   spell_launched — the cast action is the caster's current action as its spell is created; it
--     is held for cast_recovery_seconds from this launch (0 turns the rule off). Every slot of a
--     charged release's cascade fires spell_launched; each restarts the hold.
--   action_move_interrupt — every frame the game would let the stick end that action (its own
--     test answered yes: the cast's PERFORM has passed); cancelling holds it: the animation plays
--     on and the stick neither cancels it nor moves the body. The action is matched by handle and
--     type, so another action reusing the handle is never held. Once the cast animation ends the
--     hero is free, whatever is left of the hold.
-- The hold is made where the hero is played; the other co-op machine runs a copy of the cast
-- (Refii.event_replica) and leaves the body to the replication.
----------------------------------------------------------------------------------------------------

-- Each player's held cast: { action, type, ends } (ends on the runtime's clock).
S.CastHold = {}

Refii.on("spell_launched", function(e)
	local p = e.player
	if not IsPlayer(p) or Refii.event_replica() then
		return
	end
	local seconds = Setting("cast_recovery_seconds", 0.6)
	local action = seconds > 0 and Refii.current_action(e.caster) or nil
	if action == nil then
		S.CastHold[p] = nil
		return
	end
	S.CastHold[p] = { action = action, type = Refii.action_type(action), ends = Refii.now() + seconds }
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
	if e.action == hold.action and e.action_type == (hold.type or -1) then
		return true
	end
end)
