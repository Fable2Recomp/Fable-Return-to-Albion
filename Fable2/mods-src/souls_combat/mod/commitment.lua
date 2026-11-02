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
-- the parry at 8 need no permission).
--   melee_swing — the hero's committed strike: its `action`, recorded where the hero is played
--     (a relayed copy carries none).
--   anim_event — that strike's ALLOW_ACTION_INTERRUPT, matched by the owner's player AND the
--     event's own `action` (the same handle on both events; a freed action's address is reused by
--     the next action anyone plays). The first is cancelled and the permission handed to
--     Refii.allow_action_interrupt_at at commit_extra_seconds past the action's clock then; the
--     engine grants it when the strike's clock gets there, or drops it if the strike ends first.
--     A repeat of the event before that time is cancelled too, so nothing grants it early.
----------------------------------------------------------------------------------------------------

-- Each player's committed strike: { action, at } (at: the action-clock time it is granted at).
S.Commit = {}

Refii.on("melee_swing", function(e)
	local p = e.player
	if not IsPlayer(p) then
		return
	end
	if Refii.event_replica() or e.action == nil or e.action == 0 then
		S.Commit[p] = nil
	else
		S.Commit[p] = { action = e.action }
	end
end)

Refii.on("anim_event", function(e)
	if e.name ~= "ALLOW_ACTION_INTERRUPT" or not IsPlayer(e.player) then
		return
	end
	local commit = S.Commit[e.player]
	if commit == nil or e.action ~= commit.action then
		return
	end
	local elapsed = Refii.action_elapsed(commit.action) or 0
	if commit.at == nil then
		commit.at = elapsed + Setting("commit_extra_seconds", 0.25)
		Refii.allow_action_interrupt_at(commit.action, commit.at)
	end
	if elapsed < commit.at then
		return true
	end
end)
