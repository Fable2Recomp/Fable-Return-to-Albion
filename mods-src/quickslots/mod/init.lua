-- Quickslots: twelve assignable slots on the D-pad.
--
-- Runs once per game session, after the shipped scripts and before the hero exists. Everything
-- here is the mod API: the slots live in the player store, the D-pad arrives as input actions,
-- the HUD's D-pad is told what to show, and the books' rows reach us through row actions. No
-- guest memory is read and no function is hooked by name.
--
-- ONE FILE PER CONCERN: common.lua the store, the catalog and the labels; slots.lua the press,
-- the claims and the HUD; assign.lua the menus that put something in a slot.

Quickslots = Quickslots or {}

require("common")
require("slots")
require("assign")
