-- Quickslots: what every part shares — the slots in the player store, the catalog of what a slot
-- may hold, and the labels.
--
-- A module of init.lua (require("common")).
--
-- A SLOT IS A BINDING { kind, record }. kind: 1 an item to use, 2 an expression to perform,
-- 3 a weapon to draw, 4 a spell to ready; record: the GDB record hash for 1..3, the spell type
-- 0..7 for 4. Twelve slots per player: 1..4 the D-pad, 5..8 LT + the D-pad, 9..12 RT + the
-- D-pad, in the order up, down, left, right.
--
-- EACH PLAYER'S SLOTS LIVE IN THE PLAYER STORE, SAVED with the hero (Refii.player_save), so they
-- go home with the save and are the same on both machines of an online session. A Lua number is
-- a float32 in this VM, so a 32-bit record is kept as two values: quickslots.<n>.lo (the low 24
-- bits) and quickslots.<n>.hi (the high 8); quickslots.<n>.kind holds the kind, 0 for an empty
-- slot. quickslots.revision counts the writes, so the input and HUD parts re-read only when a
-- save changed the slots under them.

local Q = Quickslots
local C = {}

C.Count = 12
C.Kind = { Item = 1, Expression = 2, Weapon = 3, Spell = 4 }
C.Directions = { "up", "down", "left", "right" }
C.Elements = { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" }
C.KindTags = { "QUICKSLOTS_ITEM", "QUICKSLOTS_EXPRESSION", "QUICKSLOTS_WEAPON", "QUICKSLOTS_SPELL" }

-- The wills by spell type 0..7: the abilities book's own name tags and wheel icons.
C.Wills = {
	[0] = { tag = "GUI_SCREEN_ABILITIES_WILL_LIGHTNING_NAME_LEVEL_06", icon = "will_shock" },
	[1] = { tag = "GUI_SCREEN_ABILITIES_WILL_FIREBALL_NAME_LEVEL_06", icon = "will_blast" },
	[2] = { tag = "GUI_SCREEN_ABILITIES_WILL_SLOWTIME_NAME_LEVEL_06", icon = "icon_will_time_control" },
	[3] = { tag = "GUI_SCREEN_ABILITIES_WILL_SWORDS_NAME_LEVEL_06", icon = "will_sword_of_damocles" },
	[4] = { tag = "GUI_SCREEN_ABILITIES_WILL_VORTEX_NAME_LEVEL_06", icon = "will_vortex" },
	[5] = { tag = "GUI_SCREEN_ABILITIES_WILL_CHAOS_NAME_LEVEL_06", icon = "will_chaos" },
	[6] = { tag = "GUI_SCREEN_ABILITIES_WILL_FORCE_PUSH_NAME_LEVEL_06", icon = "will_force_push" },
	[7] = { tag = "GUI_SCREEN_ABILITIES_WILL_DEAD_RISING_NAME_LEVEL_06", icon = "will_dead_rising" },
}
local kWillIconFolder = "art\\gui\\wheelicons\\abilities\\"

-- The inventory categories the game's own quick menu offers for use (the inventory book's
-- "use" pages: potions, food, drink, books, gifts, dyes, tattoos and the rest). Category 1 is a
-- weapon; its WeaponType 1..4 is melee, 5..8 ranged.
C.Usable = {}
for _, category in ipairs({ 2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 14, 16, 24, 26 }) do
	C.Usable[category] = true
end

function C.Setting(name, default)
	return Refii.setting("quickslots." .. name, default)
end

function C.IsPlayer(p)
	return p == 1 or p == 2
end

-- A record as the "0x…" string the Refii.* calls take. Split in two so no 32-bit value meets
-- string.format's integer cast.
local kLow = 16777216
function C.Hex(record)
	local hi = math.floor(record / kLow)
	return string.format("0x%02x%06x", hi, record - hi * kLow)
end

function C.Bank(slot)
	return math.floor((slot - 1) / 4)
end

function C.Direction(slot)
	return ((slot - 1) % 4) + 1
end

function C.SlotTag(slot)
	return "QUICKSLOTS_SLOT_" .. slot
end

function C.Same(a, b)
	return a ~= nil and b ~= nil and a.kind == b.kind and a.record == b.record
end

----------------------------------------------------------------------------------------------------
-- THE STORE
----------------------------------------------------------------------------------------------------

local function Key(slot, part)
	return "quickslots." .. slot .. "." .. part
end

-- Every slot of a player, nil where empty.
function C.Read(p)
	local slots = {}
	for i = 1, C.Count do
		local kind = Refii.player_value(p, Key(i, "kind"), 0)
		if kind >= 1 and kind <= 4 then
			local record = Refii.player_value(p, Key(i, "hi"), 0) * kLow + Refii.player_value(p, Key(i, "lo"), 0)
			if kind == C.Kind.Spell then
				if record >= 0 and record <= 7 then
					slots[i] = { kind = kind, record = record }
				end
			elseif record > 0 then
				slots[i] = { kind = kind, record = record }
			end
		end
	end
	return slots
end

function C.Revision(p)
	return Refii.player_value(p, "quickslots.revision", 0)
end

-- Writes every slot and bumps the revision. Saved: it goes home with the hero.
function C.Write(p, slots)
	for i = 1, C.Count do
		local b = slots[i]
		if b ~= nil then
			local hi = math.floor(b.record / kLow)
			Refii.player_save(p, Key(i, "kind"), b.kind)
			Refii.player_save(p, Key(i, "hi"), hi)
			Refii.player_save(p, Key(i, "lo"), b.record - hi * kLow)
		else
			Refii.player_save(p, Key(i, "kind"), 0)
			Refii.player_save(p, Key(i, "hi"), 0)
			Refii.player_save(p, Key(i, "lo"), 0)
		end
	end
	Refii.player_save(p, "quickslots.revision", C.Revision(p) + 1)
end

-- The slots a binding sits in, as a list of slot numbers.
function C.Holding(slots, binding)
	local held = {}
	for i = 1, C.Count do
		if C.Same(slots[i], binding) then
			held[#held + 1] = i
		end
	end
	return held
end

----------------------------------------------------------------------------------------------------
-- THE CATALOG: what a player may put in a slot right now, each with its name tag and icon.
----------------------------------------------------------------------------------------------------

local function Entry(kind, record, tag, icon)
	return { kind = kind, record = record, tag = tag or "", icon = icon or "" }
end

-- The name tag and icon of a binding, from the record (items, weapons, expressions) or the
-- will table (spells).
function C.Describe(binding)
	if binding == nil then
		return nil
	end
	if binding.kind == C.Kind.Spell then
		local will = C.Wills[binding.record]
		return Entry(binding.kind, binding.record, will and will.tag, will and (kWillIconFolder .. will.icon .. ".tex"))
	end
	local def = C.Hex(binding.record)
	return Entry(binding.kind, binding.record, Refii.record_string(def, "NameTag"), Refii.record_string(def, "IconGraphic"))
end

function C.Catalog(p)
	local list = {}
	local hero = Refii.hero(p)
	if hero == nil then
		return list
	end
	for _, stack in ipairs(Refii.inventory(hero) or {}) do
		local def = stack.definition
		local record = tonumber(def)
		if record ~= nil and record > 0 then
			local category = Refii.record_int(def, "Category")
			if category ~= nil and C.Usable[category] then
				list[#list + 1] = C.Describe({ kind = C.Kind.Item, record = record })
			elseif category == 1 then
				local weapon = Refii.record_int(def, "WeaponType")
				if weapon ~= nil and weapon >= 1 and weapon <= 8 then
					list[#list + 1] = C.Describe({ kind = C.Kind.Weapon, record = record })
				end
			end
		end
	end
	for _, e in ipairs(Refii.expressions(p) or {}) do
		local record = tonumber(e.record)
		if record ~= nil and record > 0 then
			list[#list + 1] = C.Describe({ kind = C.Kind.Expression, record = record })
		end
	end
	for _, spell in ipairs(Refii.spells_known(p) or {}) do
		if C.Wills[spell] ~= nil then
			list[#list + 1] = C.Describe({ kind = C.Kind.Spell, record = spell })
		end
	end
	return list
end

-- A row label: the entry's own name tag, or its kind when the record names nothing.
function C.Label(entry)
	if entry == nil then
		return "{QUICKSLOTS_EMPTY}"
	end
	if entry.tag ~= nil and entry.tag ~= "" then
		return "{" .. entry.tag .. "}"
	end
	return "{" .. C.KindTags[entry.kind] .. "}"
end

return C
