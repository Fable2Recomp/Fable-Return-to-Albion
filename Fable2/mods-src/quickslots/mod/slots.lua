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
--
-- STATED ON CHANGE. Claims and HUD overrides hold until the mods' Lua reloads or the game goes
-- down, so they are stated when what they follow changes: the hero arriving (hero_ready, after its
-- saved store is read), a save loading, a slot written, a quickslots setting changing, a trigger
-- going down or up, and the expression wheel opening, moving or closing (expression_wheel). A hero
-- leaving takes its player's claims and overrides with it.
--
-- AN EXPRESSION IS HELD WHILE ITS DIRECTION IS: started unreleased (perform_expression's `held`),
-- so an extendable one extends after the game's own second, and released (release_expression) when
-- the direction comes up — the expression wheel's own tap-or-hold.

local Q = Quickslots
local C = require("common")
local Setting, IsPlayer, Kind = C.Setting, C.IsPlayer, C.Kind

local kNone = "0x00000000"

-- Per player: the triggers held, the expression the wheel highlights while it is up, the action
-- holding an expression, and the claims and HUD statements last made (restated only on change).
Q.State = Q.State or {}

local function State(p)
	local s = Q.State[p]
	if s == nil then
		s = { lt = false, rt = false, wheel = nil, extending = nil, claims = {}, hud = {}, hold = nil }
		Q.State[p] = s
	end
	return s
end

function Q.Slots(p)
	return C.Read(p)
end

local function BankEnabled(bank)
	if bank == 1 then
		return Setting("lt_bank", 1) ~= 0
	elseif bank == 2 then
		return Setting("rt_bank", 1) ~= 0
	end
	return true
end

local function BankUsed(slots, bank)
	if not BankEnabled(bank) then
		return false
	end
	for d = 1, 4 do
		if slots[bank * 4 + d] ~= nil then
			return true
		end
	end
	return false
end

-- The bank a press reaches now: RT's while RT is down and holds anything, else LT's likewise,
-- else the plain D-pad.
local function ActiveBank(s, slots)
	if s.rt and BankUsed(slots, 2) then
		return 2
	elseif s.lt and BankUsed(slots, 1) then
		return 1
	end
	return 0
end

----------------------------------------------------------------------------------------------------
-- CLAIMS AND THE HUD, stated when they change
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

-- hero: the player's hero, as hero_ready states it before Refii.hero answers it.
function Q.Restate(p, hero)
	hero = IsPlayer(p) and (hero or Refii.hero(p)) or nil
	if hero == nil then
		return
	end
	local s = State(p)
	local slots = C.Read(p, hero)
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
	Claim(p, s, "rt", BankUsed(slots, 2))
	-- Y assigns the expression under the wheel's cursor only while a wheel is up for this player.
	Claim(p, s, "assign", s.wheel ~= nil)

	local bank = ActiveBank(s, slots)
	local icons = Setting("hud_icons", 1) ~= 0
	for d = 1, 4 do
		local element = C.Elements[d]
		local b = icons and slots[bank * 4 + d] or nil
		if b ~= nil then
			Icon(p, s, element, C.Describe(b).icon, 2)
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

-- Everything this player's state stated, given back (the hero left).
local function Release(p)
	local s = Q.State[p]
	if s == nil then
		return
	end
	for action, on in pairs(s.claims) do
		if on then
			Refii.input_claim(p, "quickslots." .. action, false)
		end
	end
	for element, last in pairs(s.hud) do
		if last.texture ~= "" or last.slide ~= 0 then
			Refii.hud_icon(p, element, "")
			Refii.hud_slide(p, element, 0)
		end
	end
	if s.hold then
		Refii.hud_hold(p, "dpad", false)
	end
	Q.State[p] = nil
end

local function RestateAll()
	for p = 1, 2 do
		Q.Restate(p)
	end
end

Refii.on("lua_ready", RestateAll)
Refii.on("save_loaded", RestateAll)

Refii.on("hero_ready", function(e)
	Q.Restate(e.player, e.hero)
end)

Refii.on("hero_gone", function(e)
	Release(e.player)
end)

Refii.on("setting_changed", function(e)
	if string.sub(e.key, 1, 11) == "quickslots." then
		RestateAll()
	end
end)

Refii.on("expression_wheel", function(e)
	if not IsPlayer(e.player) then
		return
	end
	local highlighted = e.open and e.highlighted ~= kNone and e.highlighted or nil
	local s = State(e.player)
	if s.wheel ~= highlighted then
		s.wheel = highlighted
		Q.Restate(e.player)
	end
end)

----------------------------------------------------------------------------------------------------
-- A PRESS
----------------------------------------------------------------------------------------------------

local function Perform(p, b)
	if b.kind == Kind.Item then
		return Refii.use_item(p, b.record)
	elseif b.kind == Kind.Weapon then
		return Refii.equip_weapon(p, b.record)
	elseif b.kind == Kind.Expression then
		return Refii.perform_expression(p, b.record, true)
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
	local p = e.player
	local action = string.sub(e.name, 12)
	local s = State(p)
	if action == "lt" or action == "rt" then
		s[action] = e.pressed
		Q.Restate(p)
		return
	end
	if not e.pressed then
		-- The direction holding an expression came up: the expression's own end.
		if s.extending == action then
			s.extending = nil
			Refii.release_expression(p)
		end
		return
	end
	if action == "assign" then
		-- The expression under the wheel's cursor goes to a slot (assign.lua opens the picker).
		if s.wheel ~= nil then
			Q.BeginAssign(p, { kind = Kind.Expression, record = s.wheel })
		end
		return
	end
	local slot = SlotOf(action)
	if slot == nil then
		return
	end
	-- An assignment in progress takes the press as the slot's choice.
	if Q.Pending ~= nil and Q.Pending[p] ~= nil then
		Q.AssignPending(p, slot)
		return
	end
	-- A plain direction reaches the ACTIVE bank: the chord actions only fire with a trigger down,
	-- and a plain one only without, so the bank is the action's own.
	local b = Q.Slots(p)[slot]
	if b ~= nil and Perform(p, b) and b.kind == Kind.Expression then
		s.extending = action
	end
end)
