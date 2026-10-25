-- Quickslots: the D-pad in play — which slot a press reaches, what it does, and what the HUD's
-- D-pad shows.
--
-- A module of init.lua (require("slots")).
--
-- THE GAME KEEPS A DIRECTION NOBODY ASSIGNED. The mod's input actions (input.json) ride the
-- D-pad beside the game's own quick use of it; a direction is CLAIMED — withheld from the game —
-- only while its slot in the active bank holds something (Refii.input_claim). So an empty D-pad
-- plays exactly as shipped, and assigning one slot takes only that direction.
--
-- THE BANKS. LT held reaches slots 5..8, RT held slots 9..12, through chord actions that fire only
-- with the trigger down. LT is never withheld: it is the game's target lock, and holding it to
-- reach a bank keeps the lock as the console did. RT is the game's spells menu, so it is withheld
-- while any RT slot is assigned (the `rt` action's claim), and the spells menu is then reached
-- from the book; with the RT bank empty the trigger stays the game's.
--
-- THE HUD. The D-pad widget's four icons show the active bank's slots (Refii.hud_icon / hud_slide
-- on the DPad* elements), the widget held visible while anything is assigned (Refii.hud_hold),
-- and with a trigger down the bank it reaches. A direction the game owns keeps the game's icon.

local Q = Quickslots
local C = require("common")
local Setting, IsPlayer, Kind = C.Setting, C.IsPlayer, C.Kind

-- Per player: the slots as last read, the store revision they came from, the triggers held, the
-- bank shown, and the claims and HUD statements last made (restated only on change).
Q.State = Q.State or {}

local function State(p)
	local s = Q.State[p]
	if s == nil then
		s = { slots = {}, revision = -1, lt = false, rt = false, bank = 0, claims = {}, hud = {}, hold = nil, wheel = false }
		Q.State[p] = s
	end
	return s
end

-- The slots, re-read when the store's revision moved (a save, an assignment).
function Q.Slots(p)
	local s = State(p)
	local revision = C.Revision(p)
	if revision ~= s.revision then
		s.revision = revision
		s.slots = C.Read(p)
	end
	return s.slots
end

local function BankEnabled(bank)
	if bank == 1 then
		return Setting("lt_bank", 1) ~= 0
	elseif bank == 2 then
		return Setting("rt_bank", 1) ~= 0
	end
	return true
end

local function BankUsed(p, bank)
	if not BankEnabled(bank) then
		return false
	end
	local slots = Q.Slots(p)
	for d = 1, 4 do
		if slots[bank * 4 + d] ~= nil then
			return true
		end
	end
	return false
end

-- The bank a press reaches now: RT's while RT is down and holds anything, else LT's likewise,
-- else the plain D-pad.
local function ActiveBank(p)
	local s = State(p)
	if s.rt and BankUsed(p, 2) then
		return 2
	elseif s.lt and BankUsed(p, 1) then
		return 1
	end
	return 0
end

----------------------------------------------------------------------------------------------------
-- CLAIMS AND THE HUD, restated when they change
----------------------------------------------------------------------------------------------------

local function Claim(p, s, action, on)
	if s.claims[action] ~= on then
		s.claims[action] = on
		Refii.input_claim(p, "quickslots." .. action, on)
	end
end

local function Icon(p, s, element, texture, slide)
	local last = s.hud[element]
	if last == nil or last.texture ~= texture or last.slide ~= slide then
		s.hud[element] = { texture = texture, slide = slide }
		Refii.hud_icon(p, element, texture)
		Refii.hud_slide(p, element, slide)
	end
end

local function Restate(p)
	local s = State(p)
	local slots = Q.Slots(p)
	local any = false
	for d = 1, 4 do
		local dir = C.Directions[d]
		local base = slots[d] ~= nil
		local lt = BankEnabled(1) and slots[4 + d] ~= nil
		local rt = BankEnabled(2) and slots[8 + d] ~= nil
		any = any or base or lt or rt
		Claim(p, s, dir, base)
		Claim(p, s, "lt_" .. dir, lt)
		Claim(p, s, "rt_" .. dir, rt)
	end
	Claim(p, s, "rt", BankUsed(p, 2))
	-- Y assigns the expression under the wheel's cursor only while a wheel is up for this player.
	local wheel = Refii.selected_expression(p) ~= nil
	Claim(p, s, "assign", wheel)

	local bank = ActiveBank(p)
	s.bank = bank
	local icons = Setting("hud_icons", 1) ~= 0
	for d = 1, 4 do
		local element = C.Elements[d]
		local b = icons and slots[bank * 4 + d] or nil
		if b ~= nil then
			local entry = C.Describe(b)
			local texture = entry.icon ~= "" and entry.icon or "Art\\GUI\\WheelIcons\\expressions.tex"
			Icon(p, s, element, texture, 2)
		elseif icons and bank ~= 0 then
			-- A bank's empty slot: the direction shown dark rather than the game's own icon.
			Icon(p, s, element, "", 1)
		else
			Icon(p, s, element, "", 0)
		end
	end
	local hold = icons and any
	if s.hold ~= hold then
		s.hold = hold
		Refii.hud_hold(p, "dpad", hold)
	end
end

Refii.on("tick", function(e)
	for p = 1, 2 do
		if Refii.hero(p) ~= nil then
			Restate(p)
		end
	end
end)

----------------------------------------------------------------------------------------------------
-- A PRESS
----------------------------------------------------------------------------------------------------

local function Perform(p, b)
	if b.kind == Kind.Item then
		return Refii.use_item(p, C.Hex(b.record))
	elseif b.kind == Kind.Weapon then
		return Refii.equip_weapon(p, C.Hex(b.record))
	elseif b.kind == Kind.Expression then
		return Refii.perform_expression(p, C.Hex(b.record))
	elseif b.kind == Kind.Spell then
		return Refii.assign_spell(p, b.record)
	end
	return false
end

-- The slot an action names: "up".."right" the plain bank, "lt_up" / "rt_up" the others.
local function SlotOf(action)
	local bank, dir = 0, action
	if string.sub(action, 1, 3) == "lt_" then
		bank, dir = 1, string.sub(action, 4)
	elseif string.sub(action, 1, 3) == "rt_" then
		bank, dir = 2, string.sub(action, 4)
	end
	for d = 1, 4 do
		if C.Directions[d] == dir then
			return bank * 4 + d
		end
	end
	return nil
end

Refii.on("input_action", function(e)
	if string.sub(e.name, 1, 11) ~= "quickslots." or not IsPlayer(e.player) then
		return
	end
	local action = string.sub(e.name, 12)
	local s = State(e.player)
	if action == "lt" then
		s.lt = e.pressed
		return
	elseif action == "rt" then
		s.rt = e.pressed
		return
	end
	if not e.pressed then
		return
	end
	if action == "assign" then
		-- The expression under the wheel's cursor goes to a slot (assign.lua opens the picker).
		local record = Refii.selected_expression(e.player)
		if record ~= nil then
			Q.BeginAssign(e.player, { kind = Kind.Expression, record = tonumber(record) })
		end
		return
	end
	local slot = SlotOf(action)
	if slot == nil then
		return
	end
	-- An assignment in progress takes the press as the slot's choice.
	if Q.Pending ~= nil and Q.Pending[e.player] ~= nil then
		Q.AssignPending(e.player, slot)
		return
	end
	-- A plain direction reaches the ACTIVE bank: the chord actions only fire with a trigger down,
	-- and a plain one only without, so the bank is the action's own.
	local b = Q.Slots(e.player)[slot]
	if b ~= nil then
		Perform(e.player, b)
	end
end)

-- The store moved under us (a save loaded): the next tick re-reads and restates.
Refii.on("save_loaded", function()
	for p = 1, 2 do
		State(p).revision = -1
	end
end)
