-- Quickslots: putting something in a slot.
--
-- A module of init.lua (require("assign")).
--
-- THREE WAYS IN, ONE PICKER. The inventory, expressions and abilities books carry a row action
-- (ui/templates.json "rowActions": Y, or X on the abilities book) that names the highlighted row;
-- the expression wheel's Y names the expression under the cursor (slots.lua); and the Quickslots
-- entry on the pause list opens the slot list itself. Each ends in the same mod menus:
--
--   quickslots.assign   twelve rows, one per slot, each "<slot>: <what it holds>", chosen to
--                       put the pending thing there; while it is open a D-pad press (with a
--                       trigger for the other banks) chooses the slot directly
--   quickslots.menu     the same twelve rows from the pause list, chosen to change a slot
--   quickslots.choose   what a slot may hold: everything in the catalog, and Clear
--
-- The rows are stated by Refii.set_menu_rows for the player whose menu it is; a label is a text
-- tag, a literal, or a literal with {TAG} parts the UI localises.

local Q = Quickslots
local C = require("common")
local IsPlayer, Kind = C.IsPlayer, C.Kind

-- The global the rows call (Mod:lua:quickslots.<function>).
quickslots = quickslots or {}

-- Per player: the binding waiting for a slot, the slot waiting for a choice, the catalog shown.
Q.Pending = Q.Pending or {}
Q.Choosing = Q.Choosing or {}
Q.Shown = Q.Shown or {}

local function Row(name, label, command, ghosted)
	return { name = name, label = label, command = command, ghosted = ghosted or false }
end

-- The twelve slot rows: "<slot>: <holds>", each row's command carrying the slot number.
local function SlotRows(p, fn)
	local rows = {}
	local slots = Q.Slots(p)
	for i = 1, C.Count do
		local holds = slots[i] ~= nil and C.Label(C.Describe(slots[i]))
			or (C.Bank(i) == 0 and "{QUICKSLOTS_AUTOMATIC}" or "{QUICKSLOTS_EMPTY}")
		rows[#rows + 1] = Row("slot" .. i, "{" .. C.SlotTag(i) .. "}: " .. holds, "Mod:lua:quickslots." .. fn .. ":" .. i)
	end
	return rows
end

local function Save(p, slots)
	C.Write(p, slots)
	-- The next tick re-reads the store and restates the claims and the HUD.
	Q.State[p].revision = -1
end

----------------------------------------------------------------------------------------------------
-- A THING LOOKING FOR A SLOT (the books and the wheel)
----------------------------------------------------------------------------------------------------

function Q.BeginAssign(p, binding)
	if not IsPlayer(p) or binding == nil then
		return
	end
	Q.Pending[p] = binding
	local rows = SlotRows(p, "pick")
	if #C.Holding(Q.Slots(p), binding) > 0 then
		rows[#rows + 1] = Row("remove", "{QUICKSLOTS_REMOVE}", "Mod:lua:quickslots.remove")
	end
	Refii.set_menu_rows("quickslots.assign", rows, p)
	Refii.open_menu("quickslots.assign", p)
end

function Q.AssignPending(p, slot)
	local binding = Q.Pending[p]
	if binding == nil or slot < 1 or slot > C.Count then
		return
	end
	local slots = Q.Slots(p)
	slots[slot] = binding
	Save(p, slots)
	Q.Pending[p] = nil
	Refii.log("Quickslots: slot " .. slot .. " <- kind " .. binding.kind .. " " .. C.Hex(binding.record) .. " (player " .. p .. ")")
end

-- The row's own command names what the book has highlighted: "Inventory:use:<id>" an item,
-- "Inventory:equip:<id>" a weapon, "Ability:learn:<n>" a will (7..14), or an expression's record.
local function BindingOfCommand(command)
	if command == nil then
		return nil
	end
	local id = string.match(command, "^Inventory:use:(%d+)$")
	if id ~= nil then
		return { kind = Kind.Item, record = tonumber(id) }
	end
	id = string.match(command, "^Inventory:equip:(%d+)$")
	if id ~= nil then
		return { kind = Kind.Weapon, record = tonumber(id) }
	end
	id = string.match(command, "^Ability:learn:(%d+)$")
	if id ~= nil then
		local ability = tonumber(id)
		if ability >= 7 and ability <= 14 then
			return { kind = Kind.Spell, record = ability - 7 }
		end
		return nil
	end
	id = string.match(command, "^Expression:(%w+)$") or string.match(command, "^(0x%x+)$") or string.match(command, "^(%d+)$")
	if id ~= nil then
		local record = tonumber(id)
		if record ~= nil and record > 0 then
			return { kind = Kind.Expression, record = record }
		end
	end
	return nil
end

function quickslots.assign_row(arg, e)
	local binding = BindingOfCommand(e.command)
	if binding == nil then
		Refii.log("Quickslots: nothing to assign from row " .. tostring(e.row) .. " (" .. tostring(e.command) .. ")")
		return
	end
	Q.BeginAssign(e.player, binding)
end

function quickslots.pick(arg, e)
	Q.AssignPending(e.player, tonumber(arg) or 0)
end

function quickslots.remove(arg, e)
	local binding = Q.Pending[e.player]
	if binding == nil then
		return
	end
	local slots = Q.Slots(e.player)
	for _, i in ipairs(C.Holding(slots, binding)) do
		slots[i] = nil
	end
	Save(e.player, slots)
	Q.Pending[e.player] = nil
end

----------------------------------------------------------------------------------------------------
-- A SLOT LOOKING FOR A THING (the pause list)
----------------------------------------------------------------------------------------------------

local function ShowSlots(p)
	Refii.set_menu_rows("quickslots.menu", SlotRows(p, "slot"), p)
end

-- The pause list's row opened the menu: state its rows for whoever opened it.
Refii.on("mod_menu_row", function(e)
	if e.row == "quickslots.menu" or e.menu == "quickslots.menu" then
		ShowSlots(e.player)
	end
end)

function quickslots.slot(arg, e)
	local slot = tonumber(arg) or 0
	if slot < 1 or slot > C.Count then
		return
	end
	local p = e.player
	Q.Choosing[p] = slot
	local shown = C.Catalog(p)
	Q.Shown[p] = shown
	local rows = {}
	for i, entry in ipairs(shown) do
		rows[#rows + 1] = Row("choice" .. i, "{" .. C.KindTags[entry.kind] .. "}: " .. C.Label(entry), "Mod:lua:quickslots.choose:" .. i)
	end
	rows[#rows + 1] = Row("clear", "{QUICKSLOTS_CLEAR}", "Mod:lua:quickslots.clear")
	Refii.set_menu_rows("quickslots.choose", rows, p)
	Refii.open_menu("quickslots.choose", p)
end

function quickslots.choose(arg, e)
	local p = e.player
	local slot = Q.Choosing[p]
	local entry = Q.Shown[p] and Q.Shown[p][tonumber(arg) or 0]
	if slot == nil or entry == nil then
		return
	end
	local slots = Q.Slots(p)
	slots[slot] = { kind = entry.kind, record = entry.record }
	Save(p, slots)
	Q.Choosing[p] = nil
	ShowSlots(p)
end

function quickslots.clear(arg, e)
	local p = e.player
	local slot = Q.Choosing[p]
	if slot == nil then
		return
	end
	local slots = Q.Slots(p)
	slots[slot] = nil
	Save(p, slots)
	Q.Choosing[p] = nil
	ShowSlots(p)
end
