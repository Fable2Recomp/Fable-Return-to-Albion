-- Souls Combat: the committed strike.
--
-- COMMITMENT: once the hero's strike has started, nothing interrupts it until later in the
-- animation than the game allows. The action's animation-event handler, for
-- "ALLOW_ACTION_INTERRUPT", lets the next action in (CAction::OnAnimEvent sets +0x49 unless the
-- action vetoes it).
--   melee_swing — the hero's committed strike: its `action`.
--   anim_event — that strike's ALLOW_ACTION_INTERRUPT, matched by the owner's player AND the
--     event's `action` (the same handle on both events; a freed action's address is reused by the
--     next action anyone plays). The first is cancelled and the permission handed to
--     Refii.allow_action_interrupt_at at commit_extra_seconds past the action's clock; the runtime
--     grants it when the strike's clock gets there, or drops it if the strike ends first. A repeat
--     of the event before that time is cancelled too, so nothing grants it early.

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer

-- Each player's committed strike: { action, at } (at: the action-clock time it is granted at).
S.Commit = {}

Refii.on("melee_swing", function(e)
	local p = e.player
	if not IsPlayer(p) then
		return
	end
	S.Commit[p] = (e.action ~= nil and e.action ~= 0) and { action = e.action } or nil
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
