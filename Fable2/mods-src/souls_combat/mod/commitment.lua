-- Souls Combat: the committed strike.
--
-- A module of init.lua (require("commitment")).

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer

----------------------------------------------------------------------------------------------------
-- COMMITMENT: once the hero's strike has started, nothing interrupts it until later in the
-- animation than the game allows. Every action's animation-event handler, for
-- "ALLOW_ACTION_INTERRUPT", grants the next same-priority action its way in and nothing else
-- (0x82709cac; strikes, rolls and the plain blocks play at priority 6, the preemptive block and
-- the parry at 8 need no permission). For the hero's committed strike — the action recorded at
-- melee_swing, matched by the owner's player AND the event's own `action` (the same handle on both
-- events), since a freed action's address is reused by the next action anyone plays — the event is
-- cancelled and the permission granted (Refii.allow_action_interrupt) commit_extra_seconds of the
-- action's clock later, only while the same strike is still current. The record is cleared once
-- granted or once the strike is gone.
----------------------------------------------------------------------------------------------------

S.Deferred = { { action = nil, allow_at = 0 }, { action = nil, allow_at = 0 } }

Refii.on("anim_event", function(e)
	if e.name ~= "ALLOW_ACTION_INTERRUPT" then
		return
	end
	local p = e.player
	if not IsPlayer(p) then
		return
	end
	local strike = S.CommittedStrike[p]
	if strike == nil or e.action ~= strike then
		return
	end
	local deferred = S.Deferred[p]
	deferred.action = strike
	deferred.allow_at = (Refii.action_elapsed(strike) or 0) + Setting("commit_extra_seconds", 0.25)
	return true
end)

Refii.on("tick", function(e)
	for p = 1, 2 do
		local deferred = S.Deferred[p]
		local action = deferred.action
		if action ~= nil then
			local hero = Refii.player_hero(p)
			if hero == nil or Refii.current_action(hero) ~= action then
				deferred.action = nil
				S.CommittedStrike[p] = nil
			elseif (Refii.action_elapsed(action) or 0) >= deferred.allow_at then
				Refii.allow_action_interrupt(action)
				deferred.action = nil
				S.CommittedStrike[p] = nil
			end
		end
	end
end)
