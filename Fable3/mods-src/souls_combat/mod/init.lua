-- Souls Combat for Fable III: the hero's stamina, mana, poise, committed strikes, cast recovery and
-- the toggled lock-on, the enemies' fighting and the camera (camera.lua runs with the camera scripts).
--
-- Runs once per game VM, after the shipped scripts. Everything here answers the mod API's events
-- and calls Refii.*; nothing replaces a shipped file. Numbers come from settings.json through
-- Refii.setting.
--
-- ONE FILE PER RULE: each is a module under mod/, reached with require("<name>"). The order here is
-- the order their handlers subscribe, which is the order the bus runs them in. common.lua holds
-- what the rules share.

SoulsCombat = SoulsCombat or {}

require("vitals")         -- each hero's health in the player store, for the health bar
require("movement")       -- each hero's turning and friction, turn-in-place taken out
require("ai")             -- the enemies: tokens, tiers, punishes, blocks, the riposte, circling, enrage
require("stamina")        -- the pool, its refill, the refused swing
require("roll")           -- the roll's cost
require("guard")          -- the guard's cost and break
require("mana")           -- the mana pool and the charge cap
require("recovery")       -- potions restore the pools, being downed fills them
require("poise")          -- flinch only past a damage threshold
require("commitment")     -- the hero's strike plays out
require("cast_recovery")  -- a launched spell holds the caster in the cast
require("lockon")         -- R3 / G toggles the lock and marks the target
-- The bars are ui/hud.json: the pools as the HUD's own meter pieces, read from the player store.
