-- Souls Combat: the hero's movement and the enemies' fighting.
--
-- Runs once per game session, after the shipped scripts have defined their tables and before the
-- hero or any combatant exists. Everything here patches those tables, wraps their methods or
-- answers the mod API's events; nothing replaces a shipped file. Numbers come from settings.json
-- through Refii.setting.
--
-- ONE FILE PER RULE: each is a module under mod/, reached with require("<name>") (the loader
-- composes every mod/*.lua into the init chunk; require runs a module once and caches its
-- result). The order here is the order their handlers subscribe, which is the order the bus runs
-- them in. common.lua holds what the rules share and is required by each of them.

SoulsCombat = SoulsCombat or {}

require("movement")       -- each hero's turning and parry window, turn-in-place
require("ai")             -- the enemies: tokens, tiers, punishes, blocks, the riposte, circling, enrage
require("stamina")        -- the pool, its refill, the refused swing
require("roll")           -- the roll's cost
require("sprint")         -- the sprint drains stamina
require("damage")         -- enemies hit harder, elites take longer
require("healing")        -- a healing item's cooldown in a fight
require("guard")          -- the guard's cost and break
require("mana")           -- the mana pool and the charge cap
require("poise")          -- flinch only past a damage threshold
require("commitment")     -- the hero's strike plays out
require("cast_recovery")  -- a launched spell holds the caster in the cast
require("lockon")         -- LT toggles the lock
require("bars")           -- health, mana and stamina shown together while any is short
