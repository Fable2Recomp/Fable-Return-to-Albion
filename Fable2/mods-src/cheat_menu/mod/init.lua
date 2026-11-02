-- Cheat Menu: a page of the pause menu that hands out rewards.
--
-- ui/templates.json puts "Cheat Menu" on the pause list and lays out its pages; every row runs one
-- of the functions below as Mod:lua:CheatMenu.<name>:<arg>, for the player whose menu it is
-- (e.player), so in co-op each player cheats for their own hero. Everything goes through the
-- game's own setters: the purse's and the experience pools' Modify (Refii.give_gold,
-- Refii.give_experience), Stats.ModifyRenown / ModifyMorality / ModifyPurity (Refii.modify_renown /
-- modify_morality / modify_purity), the inventory's add (Refii.add_item), the clock's own setter.
-- A notice says what was given.
--
-- The Items page is a row list stated here (set_menu_rows) from ITEMS when the Cheat Menu opens,
-- each row named as the game names the item: its record's NameTag, drawn through the string book.

CheatMenu = CheatMenu or {}
local C = CheatMenu

local POOLS = { [0] = "general", [1] = "strength", [2] = "skill", [3] = "will" }

-- The Items page: what a row gives, by GDB record name (the names the shipped scripts give
-- Inventory.AddItemOfType), how many, and the label the notice says (and the row shows when the
-- record states no name). A record the database lacks is left off the page and logged.
local ITEMS = {
	{ record = "ObjectInventoryPotionHealth_5", count = 10, label = "10 Live Forever Health Potions" },
	{ record = "ObjectInventoryPotionResurrection", count = 5, label = "5 Resurrection Phials" },
	{ record = "ObjectInventoryPotionExperienceGeneral_5", count = 5, label = "5 Pure Experience Extracts" },
	{ record = "ObjectInventoryPotionExperienceStrength_5", count = 5, label = "5 Thunder's Strength Potions" },
	{ record = "ObjectInventoryPotionExperienceSkill_5", count = 5, label = "5 Expert Skill Potions" },
	{ record = "ObjectInventoryPotionExperienceWill_5", count = 5, label = "5 Undiluted Will Potions" },
	{ record = "ObjectInventoryPotionYouth", count = 1, label = "A Youth Potion" },
	{ record = "ObjectInventoryPotionScarReduction_5", count = 1, label = "A Miracle Scar Vanishing Potion" },
	{ record = "ObjectInventoryPotionFatReduction_5", count = 1, label = "A Stick-thin Potion" },
	{ record = "ObjectInventoryPotionAttractiveness", count = 1, label = "A Deep Skin Beauty Treatment" },
	{ record = "ObjectInventoryFoodAndDrinkCrunchyChick", count = 10, label = "10 Crunchy Chicks" },
}

for _, item in ipairs(ITEMS) do
	item.def = Refii.hash(item.record)
end

local function thousands(n)
	local text = tostring(math.floor(math.abs(n) + 0.5))
	while true do
		local done
		text, done = string.gsub(text, "^(%d+)(%d%d%d)", "%1,%2")
		if done == 0 then
			break
		end
	end
	return (n < 0 and "-" or "") .. text
end

-- The living hero of the player whose menu ran the row, and that player.
local function hero_of(e)
	local p = e and e.player or 1
	local hero = Refii.player_hero(p)
	if hero == nil or not Refii.living(hero) then
		return nil, p
	end
	return hero, p
end

local function tell(p, text)
	Refii.notice("Cheat Menu", text, 0, p)
	Refii.log("cheat_menu: player " .. p .. ": " .. text)
end

-- arg "<pool>:<amount>", pool 0..3 or "all".
function C.experience(arg, e)
	local handle, p = hero_of(e)
	local pool, amount = string.match(arg or "", "^(%w+):(%d+)$")
	amount = tonumber(amount)
	if handle == nil or amount == nil then
		return
	end
	if pool == "all" then
		for n = 0, 3 do
			Refii.give_experience(handle, n, amount)
		end
		tell(p, "+" .. thousands(amount) .. " of each experience")
	elseif POOLS[tonumber(pool)] ~= nil then
		Refii.give_experience(handle, tonumber(pool), amount)
		tell(p, "+" .. thousands(amount) .. " " .. POOLS[tonumber(pool)] .. " experience")
	end
end

function C.gold(arg, e)
	local handle, p = hero_of(e)
	local amount = tonumber(arg)
	if handle ~= nil and amount ~= nil then
		Refii.give_gold(handle, amount)
		tell(p, "+" .. thousands(amount) .. " gold")
	end
end

function C.renown(arg, e)
	local hero, p = hero_of(e)
	local amount = tonumber(arg)
	if hero ~= nil and amount ~= nil and Refii.modify_renown(hero, amount) then
		tell(p, "+" .. thousands(amount) .. " renown")
	end
end

function C.morality(arg, e)
	local hero, p = hero_of(e)
	local amount = tonumber(arg)
	if hero ~= nil and amount ~= nil and Refii.modify_morality(hero, amount) then
		tell(p, (amount >= 0 and "+" .. thousands(amount) .. " good" or "+" .. thousands(-amount) .. " evil"))
	end
end

function C.purity(arg, e)
	local hero, p = hero_of(e)
	local amount = tonumber(arg)
	if hero ~= nil and amount ~= nil and Refii.modify_purity(hero, amount) then
		tell(p, (amount >= 0 and "+" .. thousands(amount) .. " purity" or "+" .. thousands(-amount) .. " corruption"))
	end
end

function C.heal(_, e)
	local handle, p = hero_of(e)
	local _, most = Refii.health(handle or 0)
	if handle ~= nil and most ~= nil then
		Refii.set_health(handle, most)
		tell(p, "Healed to " .. thousands(most))
	end
end

-- arg the hour, 0..24.
function C.time(arg, e)
	local hours = tonumber(arg)
	if hours ~= nil then
		Refii.set_time_of_day(hours)
		tell(e and e.player or 1, string.format("The time is now %02d:00", hours))
	end
end

-- arg the row's index in ITEMS.
function C.item(arg, e)
	local hero, p = hero_of(e)
	local item = ITEMS[tonumber(arg) or 0]
	if hero == nil or item == nil or not Refii.record_exists(item.def) then
		return
	end
	local _, added = Refii.add_item(hero, item.def, item.count)
	if added == item.count then
		tell(p, item.label .. " added to the inventory")
	elseif (added or 0) > 0 then
		tell(p, added .. " of " .. item.label .. " added to the inventory")
	end
end

-- The row's label: the record's NameTag — on the item's record, else on its InventoryItemComponent —
-- as a {TAG} the menu draws through the string book, with the count as the game's own lists show a
-- stack ("Health Potion x10"); ITEMS' label when the record states no name.
local function item_label(item)
	local tag = Refii.record_string(item.def, "NameTag")
	if tag == nil and Refii.record_ref ~= nil then
		local component = Refii.record_ref(item.def, "InventoryItemComponent")
		tag = component and Refii.record_string(component, "NameTag")
	end
	if tag == nil or not string.find(tag, "^[%w_]+$") then
		return item.label
	end
	return "{" .. tag .. "}" .. (item.count > 1 and (" x" .. item.count) or "")
end

local checked = false

local function item_rows(p)
	local rows = {}
	for i, item in ipairs(ITEMS) do
		if Refii.record_exists(item.def) then
			rows[#rows + 1] = { name = "item" .. i, label = item_label(item), command = "Mod:lua:CheatMenu.item:" .. i }
		elseif not checked then
			Refii.log("cheat_menu: no record " .. item.record .. ", left off the Items page")
		end
	end
	checked = true
	Refii.set_menu_rows("cheat_menu.items", rows, p)
end

Refii.on("mod_menu_row", function(e)
	if e.menu == "pause-root" and e.row == "cheat_menu.menu" then
		item_rows(e.player)
	end
end)

Refii.on("hero_ready", function(e)
	item_rows(e.player)
end)
