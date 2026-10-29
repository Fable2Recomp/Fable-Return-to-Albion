-- Souls Combat: the stamina pool.
--
-- A module of init.lua (require("stamina")).

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer
local Stamina, StaminaMax, Spend = C.Stamina, C.StaminaMax, C.Spend
local kStaminaKey, kStaminaMaxKey, kStaminaRestKey, kRestCap = C.StaminaKey, C.StaminaMaxKey, C.StaminaRestKey, C.RestCap

----------------------------------------------------------------------------------------------------
-- STAMINA: swings spend it, it refills after a short pause, at zero the next swing is refused.
--   ability_request — every ability request any creature makes, with its AbilityType (16 melee
--     pressed, 17 melee released). Cancelling refuses it. A refused PRESS also refuses the RELEASE
--     that follows it, so the pair stays together. Holding X is also the block, so an empty pool
--     cannot raise a guard either.
--   melee_swing — once per strike, after the swing's animation has started; its `action`, the live
--     strike action, is recorded for the commitment below (nil in a relayed copy, where the
--     deciding machine's action is not this one's).
--   tick — the refill, where the hero is played.
----------------------------------------------------------------------------------------------------

local kMeleePressed = 16
local kMeleeReleased = 17

S.PressRefused = {}
S.CommittedStrike = {}

Refii.on("tick", function(e)
	for p = 1, 2 do
		if Refii.player_hero(p) ~= nil then
			-- Each player's maximum grows with their own Stamina ability (ui/abilities.json).
			local max = StaminaMax(p)
			local value = Stamina(p)
			if Refii.player_here(p) then
				Refii.player_set(p, kStaminaMaxKey, max)
				local rest = Refii.player_value(p, kStaminaRestKey, kRestCap) + e.seconds
				if rest >= Setting("stamina_regen_delay", 0.7) and value < max then
					value = value + Setting("stamina_regen_per_second", 55) * e.seconds
				end
				local floor = Setting("stamina_floor", -25)
				if value > max then
					value = max
				elseif value < floor then
					value = floor
				end
				Refii.player_set(p, kStaminaKey, value)
				Refii.player_set(p, kStaminaRestKey, rest > kRestCap and kRestCap or rest)
			end
		end
	end
end)

Refii.on("ability_request", function(e)
	local p = e.player
	if not IsPlayer(p) then
		return
	end
	if e.type == kMeleePressed then
		S.PressRefused[p] = Stamina(p) <= 0
		if S.PressRefused[p] then
			return true
		end
	elseif e.type == kMeleeReleased and S.PressRefused[p] then
		S.PressRefused[p] = false
		return true
	end
end)

Refii.on("melee_swing", function(e)
	local p = e.player
	if not IsPlayer(p) then
		return
	end
	Spend(p, Setting("swing_cost", 18))
	if Refii.event_replica() or e.action == nil or e.action == 0 then
		S.CommittedStrike[p] = nil
	else
		S.CommittedStrike[p] = e.action
	end
end)
