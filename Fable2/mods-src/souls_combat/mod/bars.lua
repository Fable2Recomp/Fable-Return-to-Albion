-- Souls Combat: the three bars shown together while any of them is short.
--
-- A module of init.lua (require("bars")), required after every rule that spends.

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer
local kStaminaKey, kManaKey = C.StaminaKey, C.ManaKey

----------------------------------------------------------------------------------------------------
-- THE BARS: while any of a player's health, mana or stamina is below its maximum, that player's
-- health readout — and with it the mana and stamina copies (ui/hud.json), which follow its state —
-- is held on screen; once all three are full the game's own show and fade decide again.
--   Refii.hud_hold — the piece is the player's own readout in hud.bgf (PlayerHealth for player
--     one, CoopHealth for player two), held on both players' HUDs (player 0): each view draws the
--     piece of the player it shows, so a shared screen, either half of a split screen and either
--     machine of an online session show the right one. The hold draws the readout over the
--     guest's timed fade on its Health element.
--   Health from Refii.health(hero); mana and stamina from their pools (Refii.pool_value).
--   melee_swing, roll_started, block, spell_launched — the spends, heard after the rules that pay
--     them; health_changed — damage and heals; hit_landed — a hit on a hero, whose blocked cost the
--     host may take; hero_ready, hero_gone; ability_level and mod_ability — a maximum that grows
--     (Toughness, a spell rank, the Stamina ability), checked once the level is set;
--     setting_changed — bars_always_when_not_full turned on or off.
--   While a bar is short it is checked again every kRecheckSeconds on the runtime's clock: the
--     pools refill on the tick with no event of their own, so the refill that tops them up is
--     found there. Nothing runs while all three are full.
----------------------------------------------------------------------------------------------------

local kRecheckSeconds = 0.25
local kPieces = { "PlayerHealth", "CoopHealth" }
local kTimerKey = "souls_combat.bars."

-- Whether each player's readout is held now. Fresh with the chunk: a Lua reload clears every hold.
S.BarsHeld = { false, false }

local function PoolShort(p, key)
	local value, max = Refii.pool_value(p, key)
	return value ~= nil and max ~= nil and value < max
end

-- Whether any of player p's three bars is below its maximum.
local function Short(p)
	local hero = Refii.player_hero(p)
	if hero == nil then
		return false
	end
	local health, max = Refii.health(hero)
	if health ~= nil and max ~= nil and health < max then
		return true
	end
	return PoolShort(p, kStaminaKey) or PoolShort(p, kManaKey)
end

local function Hold(p, on)
	if S.BarsHeld[p] ~= on then
		S.BarsHeld[p] = on
		Refii.hud_hold(0, kPieces[p], on)
	end
end

local function Recheck(p)
	local short = Setting("bars_always_when_not_full", 1) ~= 0 and Short(p)
	Hold(p, short)
	if short then
		Refii.after(kRecheckSeconds, function() Recheck(p) end, kTimerKey .. p)
	else
		Refii.cancel(kTimerKey .. p)
	end
end

-- A maximum that changes after its event is set: checked on the next recheck.
local function RecheckSoon(p)
	Refii.after(kRecheckSeconds, function() Recheck(p) end, kTimerKey .. p)
end

for p = 1, 2 do
	Recheck(p)
end

local function RecheckPlayer(e)
	if IsPlayer(e.player) then
		Recheck(e.player)
	end
end

local function RecheckPlayerSoon(e)
	if IsPlayer(e.player) then
		RecheckSoon(e.player)
	end
end

Refii.on("melee_swing", RecheckPlayer)
Refii.on("roll_started", RecheckPlayer)
Refii.on("block", RecheckPlayer)
Refii.on("spell_launched", RecheckPlayer)
Refii.on("health_changed", RecheckPlayer)
Refii.on("hero_ready", RecheckPlayer)
Refii.on("ability_level", RecheckPlayerSoon)
Refii.on("mod_ability", RecheckPlayerSoon)

-- A hit on a hero, relayed to the joiner: the host pays a blocked hit from the joiner's pool, and
-- the spend reaches this machine through the player store, after the hit or before it.
Refii.on("hit_landed", function(e)
	if IsPlayer(e.victim_player) then
		RecheckSoon(e.victim_player)
	end
end)

Refii.on("hero_gone", function(e)
	if IsPlayer(e.player) then
		Hold(e.player, false)
		Refii.cancel(kTimerKey .. e.player)
	end
end)

Refii.on("setting_changed", function(e)
	if e.key == "souls_combat.bars_always_when_not_full" then
		for p = 1, 2 do
			Recheck(p)
		end
	end
end)
