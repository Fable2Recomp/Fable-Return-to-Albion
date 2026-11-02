-- Quickslots: what every part shares — the slots in the player store, what a chosen row binds,
-- and the labels.
--
-- A module of init.lua (require("common")).
--
-- A SLOT IS A BINDING { kind, record }. kind: 1 an item to use, 2 an expression to perform,
-- 3 a weapon to draw, 4 a spell to ready; record: the GDB record as its "0x%08x" text for 1..3,
-- the spell type 0..7 for 4. Twelve slots per player: 1..4 the D-pad, 5..8 LT + the D-pad, 9..12
-- RT + the D-pad, in the order up, down, left, right.
--
-- EACH PLAYER'S SLOTS LIVE IN THE PLAYER STORE, SAVED with the hero, so they go home with the save
-- and are the same on both machines of an online session: quickslots.<n>.kind the kind (0 empty),
-- quickslots.<n>.record the record as a TYPED hash (Refii.player_save_hash) for 1..3,
-- quickslots.<n>.spell the spell type for 4.
--
-- AN ITEM'S NAME, ICON AND CATEGORY ARE ON ITS InventoryItemComponent. The record an inventory
-- states (and a use or equip takes) is the item ENTITY, which names that component by a reference
-- property and carries none of the item's own properties; an expression's record carries its own.

local Q = Quickslots
local C = {}

C.Count = 12
C.Kind = { Item = 1, Expression = 2, Weapon = 3, Spell = 4 }
C.Directions = { "up", "down", "left", "right" }
C.Elements = { "DPadUp", "DPadDown", "DPadLeft", "DPadRight" }
C.KindTags = { "QUICKSLOTS_ITEM", "QUICKSLOTS_EXPRESSION", "QUICKSLOTS_WEAPON", "QUICKSLOTS_SPELL" }

local kNone = "0x00000000"
local kWeaponCategory = 1

function C.Setting(name, default)
	return Refii.setting("quickslots." .. name, default)
end

function C.IsPlayer(p)
	return p == 1 or p == 2
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

-- The InventoryItemComponent record an item entity names, or nil.
local function ItemComponent(def)
	return Refii.record_ref(def, "InventoryItemComponent")
end

-- The binding a chosen row's typed subject states (mod_menu_row's subject_kind, definition, spell),
-- or nil. An item in the weapon category is drawn, not used.
function C.Binding(kind, definition, spell)
	if kind == "will" then
		if spell ~= nil and spell >= 0 and spell <= 7 then
			return { kind = C.Kind.Spell, record = spell }
		end
		return nil
	end
	if definition == nil or definition == "" or definition == kNone then
		return nil
	end
	if kind == "expression" then
		return { kind = C.Kind.Expression, record = definition }
	elseif kind == "weapon" then
		return { kind = C.Kind.Weapon, record = definition }
	elseif kind == "item" then
		local component = ItemComponent(definition)
		if component ~= nil and Refii.record_int(component, "Category") == kWeaponCategory then
			return { kind = C.Kind.Weapon, record = definition }
		end
		return { kind = C.Kind.Item, record = definition }
	end
	return nil
end

----------------------------------------------------------------------------------------------------
-- THE STORE
----------------------------------------------------------------------------------------------------

local function Key(slot, part)
	return "quickslots." .. slot .. "." .. part
end

-- THE OLD FORM, read once: a record split over two floats, quickslots.<n>.hi (the high 8 bits) and
-- .lo (the low 24), written from the record as a float, so a record past 2^24 lost its low bits
-- before it was stored. The record is the carried item or known expression whose float splits into
-- the same two halves, else the halves joined when the database has that record. nil when neither,
-- and whether that is final: not while the hero states no inventory or expressions yet.
local kLow = 16777216

local function Halves(hex)
	local f = tonumber(hex)
	if f == nil then
		return nil, nil
	end
	local hi = math.floor(f / kLow)
	return hi, f - hi * kLow
end

local function OldRecord(p, hero, kind, hi, lo)
	local candidates = {}
	if kind == C.Kind.Expression then
		for _, e in ipairs(Refii.expressions(p) or {}) do
			candidates[#candidates + 1] = e.record
		end
	else
		for _, stack in ipairs(Refii.inventory(hero) or {}) do
			candidates[#candidates + 1] = stack.definition
		end
	end
	for _, hex in ipairs(candidates) do
		local h, l = Halves(hex)
		if h == hi and l == lo then
			return hex, true
		end
	end
	local joined = string.format("0x%02x%06x", hi, lo)
	if Refii.record_exists(joined) then
		return joined, true
	end
	return nil, #candidates > 0
end

-- Moves slot i of player p from the old form to the typed one, once the hero exists (the
-- candidates are its inventory and expressions). The binding, or nil.
local function Migrate(p, hero, i, kind)
	local hi = Refii.player_value(p, Key(i, "hi"))
	local lo = Refii.player_value(p, Key(i, "lo"))
	if hi == nil or lo == nil or hero == nil then
		return nil
	end
	local binding, final = nil, true
	if kind == C.Kind.Spell then
		local spell = hi * kLow + lo
		if spell >= 0 and spell <= 7 then
			binding = { kind = kind, record = spell }
			Refii.player_save(p, Key(i, "spell"), spell)
		end
	else
		local record
		record, final = OldRecord(p, hero, kind, hi, lo)
		if record ~= nil then
			binding = { kind = kind, record = record }
			Refii.player_save_hash(p, Key(i, "record"), record)
		end
	end
	if binding == nil and final then
		Refii.player_save(p, Key(i, "kind"), 0)
		Refii.log("Quickslots: slot " .. i .. " of player " .. p .. " named no record it can be read back as; cleared")
	end
	return binding
end

-- Every slot of a player, nil where empty. hero: the player's hero (Refii.hero(p) when absent).
function C.Read(p, hero)
	hero = hero or Refii.hero(p)
	local slots = {}
	for i = 1, C.Count do
		local kind = Refii.player_value(p, Key(i, "kind"), 0)
		if kind == C.Kind.Spell then
			local spell = Refii.player_value(p, Key(i, "spell"))
			if spell ~= nil then
				if spell >= 0 and spell <= 7 then
					slots[i] = { kind = kind, record = spell }
				end
			else
				slots[i] = Migrate(p, hero, i, kind)
			end
		elseif kind >= 1 and kind <= 3 then
			local record = Refii.player_value_hash(p, Key(i, "record"))
			if record ~= nil then
				if record ~= kNone then
					slots[i] = { kind = kind, record = record }
				end
			else
				slots[i] = Migrate(p, hero, i, kind)
			end
		end
	end
	return slots
end

-- Writes every slot. Saved: it goes home with the hero.
function C.Write(p, slots)
	for i = 1, C.Count do
		local b = slots[i]
		if b == nil then
			Refii.player_save(p, Key(i, "kind"), 0)
		elseif b.kind == C.Kind.Spell then
			Refii.player_save(p, Key(i, "kind"), b.kind)
			Refii.player_save(p, Key(i, "spell"), b.record)
		else
			Refii.player_save(p, Key(i, "kind"), b.kind)
			Refii.player_save_hash(p, Key(i, "record"), b.record)
		end
	end
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
-- NAMES AND ICONS
----------------------------------------------------------------------------------------------------

-- The name tag and icon of a binding: a spell's from Refii.spell_info, an expression's off its
-- record, an item's or weapon's off its InventoryItemComponent. "" for what the records lack.
function C.Describe(binding)
	if binding == nil then
		return nil
	end
	local tag, icon
	if binding.kind == C.Kind.Spell then
		tag, icon = Refii.spell_info(binding.record)
	else
		local record = binding.record
		if binding.kind ~= C.Kind.Expression then
			record = ItemComponent(record)
		end
		if record ~= nil then
			tag = Refii.record_string(record, "NameTag")
			icon = Refii.record_string(record, "IconGraphic")
		end
	end
	return { kind = binding.kind, record = binding.record, tag = tag or "", icon = icon or "" }
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
