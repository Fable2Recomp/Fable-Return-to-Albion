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
--   quickslots.choose   what a slot may hold: the host's own lists of what the hero can use from
--                       the inventory (inventory:usable — weapons among them, drawn rather than
--                       used), the expressions it knows and the wills it can assign, and Clear
--
-- The slot rows are stated by Refii.set_menu_rows for the player whose menu it is; a label is a
-- text tag, a literal, or a literal with {TAG} parts the UI localises. A chosen row names its
-- subject TYPED (mod_menu_row's subject_kind, definition and spell) — a book's row and a host
-- list's alike — so no row's command is parsed here.

local Q = Quickslots
local C = require("common")
local IsPlayer = C.IsPlayer

-- The global the rows call (Mod:lua:quickslots.<function>).
quickslots = quickslots or {}

-- Per player: the binding waiting for a slot, the slot waiting for a choice.
Q.Pending = Q.Pending or {}
Q.Choosing = Q.Choosing or {}

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
	Q.Restate(p)
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
	Refii.log("Quickslots: slot " .. slot .. " <- kind " .. binding.kind .. " " .. tostring(binding.record) .. " (player " .. p .. ")")
end

-- A book's row action: the highlighted row's typed subject — an item, a weapon, an expression or a will.
function quickslots.assign_row(arg, e)
	local binding = C.Binding(e.subject_kind, e.definition, e.spell)
	if binding == nil then
		Refii.log("Quickslots: nothing to assign from row " .. tostring(e.row) .. " (" .. tostring(e.subject_kind) .. ")")
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
	Q.Choosing[e.player] = slot
	Refii.open_menu("quickslots.choose", e.player)
end

-- A host list's row on quickslots.choose (its rowCommand): the row's typed subject.
function quickslots.choose(arg, e)
	local p = e.player
	local slot = Q.Choosing[p]
	local binding = C.Binding(e.subject_kind, e.definition, e.spell)
	if slot == nil or binding == nil then
		return
	end
	local slots = Q.Slots(p)
	slots[slot] = binding
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
