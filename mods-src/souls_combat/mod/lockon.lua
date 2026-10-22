-- Souls Combat: the toggled lock-on.
--
-- A module of init.lua (require("lockon")).

local S = SoulsCombat
local C = require("common")
local Setting, IsPlayer = C.Setting, C.IsPlayer

----------------------------------------------------------------------------------------------------
-- LOCK-ON: LT toggles the lock instead of being held.
--
-- THE LOCK IS OURS: one state per hero, flipped by the LT PRESS itself, and the game's lock is set
-- to match it through Refii.lock_target / release_lock (the targeting component's force byte and
-- the player's look-at flag, what a held LT states). Keying off the game's own look-at flag stuck
-- locked: it is set only by a camera event on LT down, not delivered again while the lock holds.
--
--   game_action — LT is 49, TARGET_LOCK_SET, on press and 50, TARGET_LOCK_RELEASE, on release. The
--     receiver has no case for 49, and its case 50 already skips the lock removal while the force
--     byte is set (0x826e5db4): neither is cancelled; the press flips our lock.
--   look_at_button — every write of the player's look-at flag (the camera's press event, and the
--     API's own lock and release) arrives with `down` writable, so the flag always equals our
--     lock, whoever asks.
--   tick — THE LOCK ENDS WHEN THERE IS NOTHING TO LOCK. The game never clears the force byte: the
--     targeting update re-selects within 8 ticks of the old target dying, and with none found only
--     drops player mode 4, leaving the camera locked on nothing. So the lock drops after
--     lock_drop_seconds without a valid target: the one Refii.locked_target reports, living
--     (Refii.living: in the world and, for a creature, not killed — the selector's own test).
--     Each tick the lock holds, Refii.wheel_switches_target states that on the keyboard and mouse
--     the wheel switches target (up the next left, down the next right) and moving the mouse never
--     does; a pad's right-stick flick is untouched. The statement holds for the guest tick it is
--     made on and the two after, and this tick runs every guest tick, so it never lapses.
----------------------------------------------------------------------------------------------------

local kTargetLockSet = 49

-- The heroes currently locked, by handle: how long each has been locked on nothing.
S.Locked = {}

local function IsLocked(hero)
	return hero ~= nil and S.Locked[hero] ~= nil
end

-- The game's own test for "a target worth locking": held by the lock, in the world, not killed.
local function HasValidTarget(p)
	local target = Refii.locked_target(p)
	return target ~= nil and Refii.living(target)
end

-- Puts the game's lock where ours says, through the API: the force byte and the look-at flag.
local function ApplyLock(hero, on)
	local p = Refii.player_of(hero)
	if not IsPlayer(p) then
		return
	end
	if on then
		Refii.lock_target(p)  -- the game's own pick
	else
		Refii.release_lock(p)
	end
end

Refii.on("tick", function(e)
	for hero, without in pairs(S.Locked) do
		local p = Refii.player_of(hero)
		if not IsPlayer(p) then
			-- The hero itself is gone (a level change); nothing to release on.
			S.Locked[hero] = nil
		else
			Refii.wheel_switches_target(hero)
			if HasValidTarget(p) then
				S.Locked[hero] = 0
			else
				without = without + e.seconds
				if without >= Setting("lock_drop_seconds", 0.5) then
					S.Locked[hero] = nil
					ApplyLock(hero, false)
					Refii.log("Souls Combat: lock-on OFF (no target)")
				else
					S.Locked[hero] = without
				end
			end
		end
	end
end)

Refii.on("game_action", function(e)
	if e.action ~= kTargetLockSet then
		return
	end
	local on = not IsLocked(e.hero)
	if on then
		S.Locked[e.hero] = 0
	else
		S.Locked[e.hero] = nil
	end
	ApplyLock(e.hero, on)
	if on then
		Refii.wheel_switches_target(e.hero)
	end
	Refii.log("Souls Combat: lock-on " .. (on and "ON" or "OFF"))
	-- The release (50) ends nothing while locked: the game's own case honours the force byte, and
	-- the look-at flag is held by the handler below.
end)

-- The flag is the lock, whoever asks — the camera's press event included.
Refii.on("look_at_button", function(e)
	e.down = IsLocked(e.hero)
end)
