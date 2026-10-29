-- Souls Combat: poise.
--
-- A module of init.lua (require("poise")).

local S = SoulsCombat
local C = require("common")
local Setting = C.Setting

----------------------------------------------------------------------------------------------------
-- POISE: a hit staggers its victim only once enough damage has landed in a short time. Enemies no
-- longer flinch at every tap; neither does the hero.
--   hit_response — the victim's hit-response component is about to play the flinch, with the
--     game's own damage arithmetic and the victim's health; the bus keeps the game's early-outs
--     (a blocked or parried hit, a ranged hit, a knockdown, a killing blow), so this only sees a
--     hit the victim would have flinched at. Cancelling absorbs it: no animation, no state.
--   entity_died — a dead entity's pool goes with it.
-- Online the game runs the response on both machines for a hit on or by player two; the run on
-- the machine not authoritative for the victim is the copy (Refii.event_replica). The host runs
-- every hit, so its pools are whole and decide; the joiner decides player two's own pool and
-- leaves its copy of a hit by player two on anyone else to the game's predicted response.
----------------------------------------------------------------------------------------------------

local kPoiseSlots = 32

S.Poise = {}
for i = 1, kPoiseSlots do
	S.Poise[i] = { entity = nil, taken = 0, last_hit = -1000 }
end
S.PoiseNow = 0

Refii.on("tick", function(e)
	S.PoiseNow = S.PoiseNow + e.seconds
end)

Refii.on("entity_died", function(e)
	for i = 1, kPoiseSlots do
		local poise = S.Poise[i]
		if poise.entity == e.entity then
			poise.entity = nil
			poise.taken = 0
			poise.last_hit = -1000
		end
	end
end)

-- The victim's pool, or the longest-idle slot made its own.
local function PoiseOf(entity)
	local oldest = S.Poise[1]
	for i = 1, kPoiseSlots do
		local poise = S.Poise[i]
		if poise.entity == entity then
			return poise
		end
		if poise.last_hit < oldest.last_hit then
			oldest = poise
		end
	end
	oldest.entity = entity
	oldest.taken = 0
	oldest.last_hit = -1000
	return oldest
end

Refii.on("hit_response", function(e)
	if e.max_health <= 0 or (Refii.event_replica() and Refii.coop_mode() == 3) then
		return
	end
	local poise = PoiseOf(e.victim)
	if S.PoiseNow - poise.last_hit > Setting("poise_window_seconds", 3) then
		poise.taken = 0
	end
	poise.taken = poise.taken + e.damage
	poise.last_hit = S.PoiseNow
	if poise.taken < Setting("poise_fraction", 0.3) * e.max_health then
		return true  -- Absorbed: no flinch.
	end
	poise.taken = 0  -- Staggered; the pool refills from here.
end)
