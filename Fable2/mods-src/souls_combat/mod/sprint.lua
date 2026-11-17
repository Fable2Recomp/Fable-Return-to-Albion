-- Souls Combat: the sprint's stamina.
--
-- A module of init.lua (require("sprint")).

local C = require("common")
local Setting, IsPlayer, Stamina, kStaminaKey = C.Setting, C.IsPlayer, C.Stamina, C.StaminaKey

----------------------------------------------------------------------------------------------------
-- THE SPRINT drains stamina while it lasts and ends when the pool is empty.
--   game_action — the sprint button's press (Refii.ACTION_SPRINT_SET) reaches the hero's
--     receiver; refused on an empty pool, so the hero does not start a sprint it cannot pay for.
--   While the sprint component's own flag holds (Refii.sprinting), sprint_cost_per_second is
--     taken for the game time that passed (Refii.now stands still in the pause and menus), every
--     tenth of a second; the spend holds the refill off as any spend does. At an empty pool the
--     sprint ends as the button's release ends it (Refii.end_sprint): a held button sprints again
--     only on its next press. The check runs only while that hero sprints.
-- The sprint is played, and paid for, where the hero is played.
----------------------------------------------------------------------------------------------------

local kStep = 0.1

local function TimerKey(p)
	return "souls_combat.sprint." .. p
end

local function Drain(p, last)
	if not Refii.sprinting(p) then
		return
	end
	if Stamina(p) <= 0 then
		Refii.end_sprint(p)
		return
	end
	local now = Refii.now()
	local cost = (now - last) * Setting("sprint_cost_per_second", 12)
	if cost > 0 then
		Refii.pool_spend(p, kStaminaKey, cost)
	end
	Refii.after(kStep, function() Drain(p, now) end, TimerKey(p))
end

Refii.on("game_action", function(e)
	local p = e.player
	if not IsPlayer(p) or e.action ~= Refii.ACTION_SPRINT_SET then
		return
	end
	if Stamina(p) <= 0 then
		return true
	end
	local now = Refii.now()
	Refii.after(kStep, function() Drain(p, now) end, TimerKey(p))
end)

Refii.on("hero_gone", function(e)
	if IsPlayer(e.player) then
		Refii.cancel(TimerKey(e.player))
	end
end)
