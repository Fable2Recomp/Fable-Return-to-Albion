-- Hotbar: a Fable 1-style bar of ten slots on the keys 1-0.
--
-- 1, 2 and 3 stay the game's own: they put the attack button on melee, ranged or magic, and the
-- bar only shows them (the weapons in hand and the spell at the first charge level, which Ctrl's
-- spell wheel chooses as ever). 4-0 hold what the player puts there from the Hotbar page of the
-- pause menu, and add to the game's controls without changing them:
--
--   a spell        the key casts it: held, it charges through the power levels exactly as holding
--                  the magic button does, and a tap casts it at once (see THE CAST below);
--   an expression  the key performs it, through the game's own expression performer;
--   an item        the key uses one, as the Items page's Use does (godot/hotbar.gd, through the
--                  host's use_item(definition), which needs no menu open).
--
-- What is on a slot is the hero's, saved with them: the player values hotbar.k<key> (the kind),
-- hotbar.a<key> and hotbar.b<key> (what: a spell type, an expression's index in EXPRESSIONS, or
-- an item's record hash as two 16-bit halves, since a Lua number here is a 32-bit float).
--
-- THE BAR is drawn by godot/hotbar.gd from values this file publishes for the local player, per
-- slot s (1-10, the 0 key is 10): hotbar.kind<s>, .ok<s> (usable now), .on<s> (lit: the spell
-- being cast from it), .count<s> (an item's stack, -1 for none), .hi<s>/.lo<s> (an item's hash) and the icon
-- path, which a published number cannot hold: hotbar.icon<s>.n characters, packed three to a
-- number in hotbar.icon<s>.<j> (24 bits, exact in a float).

Hotbar = Hotbar or {}
local H = Hotbar

local EMPTY, SPELL, EXPRESSION, ITEM, BUILTIN = 0, 1, 2, 3, 4
local KEYS = { 4, 5, 6, 7, 8, 9, 0 }
local PAGE = 15   -- pick rows a page: a Lua menu list crosses the bus in about 1.8 KB

-- ESpellType 0..7, in the game's order.
local SPELLS = {
	[0] = { name = "Shock", icon = "will_shock", tag = "LIGHTNING" },
	[1] = { name = "Inferno", icon = "will_blast", tag = "FIREBALL" },
	[2] = { name = "Time Control", icon = "icon_will_time_control", tag = "SLOWTIME" },
	[3] = { name = "Blades", icon = "will_sword_of_damocles", tag = "SWORDS" },
	[4] = { name = "Vortex", icon = "will_vortex", tag = "VORTEX" },
	[5] = { name = "Chaos", icon = "will_chaos", tag = "CHAOS" },
	[6] = { name = "Force Push", icon = "will_force_push", tag = "FORCE_PUSH" },
	[7] = { name = "Raise Dead", icon = "will_dead_rising", tag = "DEAD_RISING" },
}

-- The hero's expressions, as their GDB records name them (Expression<name>). Append only: a
-- slot keeps its expression by its index here. The pick list offers those the hero has learned.
local EXPRESSIONS = {
	"ThumbsUp", "ThumbsDown", "Dance", "Chicken", "SexyPose", "PointAndLaugh", "Laugh", "Growl",
	"BloodlustRoar", "ScaryLaugh", "BodilyFunction", "Belch", "Fart", "Insult", "VulgarThrust",
	"KissMyAss", "PickUpLine", "BlowKiss", "Kiss", "Seduce", "ComeBackToMyPlace", "Whistle",
	"Worship", "Apologise", "Encourage", "Beg", "Extort", "Threaten", "Slap", "Fakeout",
	"SockPuppet", "Lute", "VictoryArmPump", "Flex", "PlayDead", "HatMoustacheHeadband", "Follow",
	"Wait",
}

-- Inventory categories the pick lists take. Anything else usable from the inventory is "Other
-- items", less what needs a second choice (augmentations and dyes need a target), what is worn
-- (weapons, hair, clothes, outfits) and the dog's kit.
local FOOD, POTIONS = 5, 6
local NOT_OTHER = { [1] = true, [5] = true, [6] = true, [7] = true, [8] = true, [9] = true,
	[10] = true, [11] = true, [12] = true, [13] = true, [14] = true, [17] = true, [18] = true,
	[19] = true, [20] = true, [21] = true, [22] = true, [23] = true, [24] = true, [25] = true }

H.Editing = H.Editing or {}   -- per player: the key whose slot the Hotbar page is filling
H.Pick = H.Pick or {}         -- per player: { kind, list, page } behind the pick page
H.Items = H.Items or {}       -- item record hash -> { tag, icon, category, usable }

local function slot_of(key)
	return key == 0 and 10 or key
end

local function hero_of(p)
	local handle = Refii.player_hero(p)
	local hero = handle and Refii.entity(handle)
	if hero == nil or not hero:IsAlive() then
		return nil, nil
	end
	return hero, handle
end

-- An item's hash as Refii's "0x…" and back to the two halves the player store holds.
local function item_hash(hi, lo)
	return string.format("0x%04x%04x", hi, lo)
end

local function item_halves(hash)
	local hex = string.match(hash, "^0x(%x+)$")
	if hex == nil then
		return nil
	end
	hex = string.rep("0", 8 - #hex) .. hex
	return tonumber(string.sub(hex, 1, 4), 16), tonumber(string.sub(hex, 5, 8), 16)
end

-- A readable English name from a text tag, for the slot list (a menu row is one tag or one
-- literal, so "4  <name>" has to be a literal).
local function tag_words(tag)
	local words = string.gsub(string.gsub(string.gsub(tag or "", "^INV_ITEM_", ""), "^TEXT_", ""), "_NAME$", "")
	words = string.lower(string.gsub(words, "_", " "))
	return (string.gsub(words, "(%a)(%w*)", function(a, b) return string.upper(a) .. b end))
end

local function camel_words(name)
	return (string.gsub(name, "(%l)(%u)", "%1 %2"))
end

-- Every item the hero carries, read once a scan: record hash -> its InventoryItemComponent's
-- name tag, icon, category and whether it can be used from the inventory.
local function scan_items(hero)
	local found = {}
	local n = Inventory.GetNumberOfItems(hero) or 0
	for i = 0, n - 1 do
		local item = Inventory.GetItemByIndex(hero, i)
		local record = item and item:GetRecord()
		if record ~= nil and not record:IsNull() and record:Exists("InventoryItemComponent") then
			local hex = string.match(tostring(record:GetID()), "(%x+)$")
			local hash = "0x" .. string.lower(string.rep("0", 8 - #hex) .. hex)
			local entry = found[hash]
			if entry == nil then
				local c = record:GetRecord("InventoryItemComponent")
				entry = {
					tag = c:Exists("NameTag") and c:GetString("NameTag") or "",
					icon = c:Exists("IconGraphic") and c:GetString("IconGraphic") or "",
					category = c:Exists("Category") and c:GetEnum("Category") or -1,
					usable = c:Exists("UsableFromInventory") and c:GetBoolean("UsableFromInventory"),
					count = 0,
				}
				found[hash] = entry
				H.Items[hash] = entry
			end
			entry.count = entry.count + 1
		end
	end
	return found
end

-- A record the hero does not carry is looked for again at most every few seconds.
H.Missed = H.Missed or {}
local function item_info(hero, hash)
	if H.Items[hash] == nil and hero ~= nil and (H.Missed[hash] or 0) <= 0 then
		scan_items(hero)
		H.Missed[hash] = H.Items[hash] == nil and 20 or nil
	elseif H.Missed[hash] ~= nil then
		H.Missed[hash] = H.Missed[hash] - 1
	end
	return H.Items[hash]
end

local function expression_record(index)
	local name = EXPRESSIONS[index]
	if name == nil or not GDB.RecordExists("Expression" .. name) then
		return nil
	end
	return GDB.GetRecord("Expression" .. name)
end

-- ---- what a slot holds ----------------------------------------------------------------------

local function read_slot(p, key)
	local kind = Refii.player_value(p, "hotbar.k" .. key, EMPTY) or EMPTY
	local a = Refii.player_value(p, "hotbar.a" .. key, 0) or 0
	local b = Refii.player_value(p, "hotbar.b" .. key, 0) or 0
	return kind, a, b
end

local function write_slot(p, key, kind, a, b)
	Refii.player_save(p, "hotbar.k" .. key, kind)
	Refii.player_save(p, "hotbar.a" .. key, a or 0)
	Refii.player_save(p, "hotbar.b" .. key, b or 0)
end

-- What a slot shows: its icon path, a name for the slot list, whether it can be used now, its
-- item count (-1 for none) and whether it is the active spell.
local function describe(p, key, hero)
	local kind, a, b = read_slot(p, key)
	if kind == SPELL and SPELLS[a] ~= nil then
		local spell = SPELLS[a]
		local learned = hero ~= nil and SpellManager.IsEntitySpellTypePowerLevelPurchased(hero, a, 0)
		return kind, "art\\gui\\wheelicons\\abilities\\" .. spell.icon .. ".tex", spell.name,
			learned, -1, H.Cast ~= nil and H.Cast.p == p and H.Cast.key == key
	elseif kind == EXPRESSION then
		local record = expression_record(a)
		if record ~= nil then
			local learned = hero ~= nil and ExpressionPerformer.IsExpressionAvailable(hero, "Expression" .. EXPRESSIONS[a])
			return kind, record:GetString("IconGraphic"), camel_words(EXPRESSIONS[a]), learned, -1, false
		end
	elseif kind == ITEM then
		local hash = item_hash(a, b)
		local handle = Refii.player_hero(p)
		local count = handle and Refii.item_count(handle, hash) or 0
		local info = item_info(hero, hash)
		if info ~= nil then
			return kind, info.icon, tag_words(info.tag), count > 0, count, false
		end
		return kind, "", "?", false, count, false
	end
	return EMPTY, "", nil, false, -1, false
end

-- ---- the bar --------------------------------------------------------------------------------

local published = {}

local function publish(key, value)
	if published[key] ~= value then
		published[key] = value
		Refii.publish(key, value)
	end
end

local function publish_icon(s, path)
	local n = #path
	publish("hotbar.icon" .. s .. ".n", n)
	for j = 0, math.floor((n - 1) / 3) do
		local packed = 0
		for k = 1, 3 do
			packed = packed * 256 + (string.byte(path, j * 3 + k) or 0)
		end
		publish("hotbar.icon" .. s .. "." .. j, packed)
	end
end

local function publish_slot(s, kind, icon, ok, count, on, hi, lo)
	publish("hotbar.kind" .. s, kind)
	publish("hotbar.ok" .. s, ok and 1 or 0)
	publish("hotbar.on" .. s, on and 1 or 0)
	publish("hotbar.count" .. s, count or -1)
	publish("hotbar.hi" .. s, hi or 0)
	publish("hotbar.lo" .. s, lo or 0)
	publish_icon(s, icon or "")
end

local function weapon_icon(hero, hash)
	local info = hash and item_info(hero, hash)
	return info and info.icon or ""
end

-- The local player's bar, every quarter second.
local function refresh_bar()
	local p = Refii.player_of(Refii.local_hero() or 0)
	if p ~= 1 and p ~= 2 then
		p = 1
	end
	local hero, handle = hero_of(p)
	publish("hotbar.ready", hero ~= nil and 1 or 0)
	if hero == nil then
		return
	end

	local melee, ranged = Refii.equipped(handle)
	-- The player's own first-level spell, also while a hotbar cast has the table.
	local first = H.Cast ~= nil and H.Cast.p == p and H.Cast.saved[1] or Refii.spell_in_slot(p, 0)
	publish_slot(1, BUILTIN, weapon_icon(hero, melee), melee ~= nil, -1, false)
	publish_slot(2, BUILTIN, weapon_icon(hero, ranged), ranged ~= nil, -1, false)
	publish_slot(3, BUILTIN, SPELLS[first] and ("art\\gui\\wheelicons\\abilities\\" .. SPELLS[first].icon .. ".tex") or "",
		SPELLS[first] ~= nil, -1, false)

	for _, key in ipairs(KEYS) do
		local kind, icon, _, ok, count, on = describe(p, key, hero)
		local _, a, b = read_slot(p, key)
		publish_slot(slot_of(key), kind, icon, ok, count, on, kind == ITEM and a or 0, kind == ITEM and b or 0)
	end
end

local cast_tick   -- THE CAST, below
local since = 0
Refii.on("tick", function(e)
	cast_tick(e.seconds or 0)
	since = since + (e.seconds or 0)
	if since < 0.25 then
		return
	end
	since = 0
	refresh_bar()
end)

-- ---- the keys -------------------------------------------------------------------------------

-- THE CAST. The game charges and casts from the hero's spell table (one spell per power level,
-- the same in both cast modes: the game's own writer mirrors them) while the magic button is
-- held. A spell key borrows that for one cast: here the table as it stands is kept and the key's
-- spell put on every level the hero has learned it to, the levels above emptied so the charge
-- stops where the spell does; godot/hotbar.gd then holds the magic button for as long as the key
-- is held (hotbar.cast_seq says a cast was taken, hotbar.casting which key). Once the key is up
-- and the charge is spent the table goes back as it was, so Ctrl's choices are never touched.
H.Cast = H.Cast or nil   -- { p, key, saved = {5 levels}, lent = {5 levels}, released, after, age }
H.CastSeq = H.CastSeq or 0

local function write_table(hero, row)
	for level = 0, 4 do
		local spell = row[level + 1]
		if spell == nil or spell < 0 then
			spell = ESpellType.SPELL_NULL
		end
		SpellManager.SetEntitySpellTableItem(hero, ESpellCastDirMode.eSCDM_SURROUND, level, spell)
		SpellManager.SetEntitySpellTableItem(hero, ESpellCastDirMode.eSCDM_TARGETED, level, spell)
	end
end

local function cast_begin(p, hero, key, spell)
	if H.Cast ~= nil or SpellManager.IsCharging(hero) or
			not SpellManager.IsEntitySpellTypePowerLevelPurchased(hero, spell, 0) then
		return
	end
	local saved, row = {}, {}
	for level = 0, 4 do
		saved[level + 1] = Refii.spell_in_slot(p, level)
		row[level + 1] = SpellManager.IsEntitySpellTypePowerLevelPurchased(hero, spell, level) and spell or -1
	end
	write_table(hero, row)
	H.Cast = { p = p, key = key, saved = saved, lent = row, released = false, after = 0, age = 0 }
	H.CastSeq = H.CastSeq + 1
	Refii.publish("hotbar.casting", key == 0 and 10 or key)
	Refii.publish("hotbar.cast_seq", H.CastSeq)
end
H.cast_begin = cast_begin   -- for the live checks (scratchpad), never called by the mod itself

-- The table back once the key is up and the charge has gone out (or the hero has gone).
cast_tick = function(seconds)
	local cast = H.Cast
	if cast == nil then
		return
	end
	cast.age = cast.age + seconds
	if cast.released then
		cast.after = cast.after + seconds
	end
	local hero = hero_of(cast.p)
	local done = hero == nil or cast.age > 30 or
		(cast.released and cast.after > 0.3 and not SpellManager.IsCharging(hero))
	if done then
		-- A level the player set with Ctrl's wheel meanwhile keeps the player's spell.
		if hero ~= nil then
			local row = {}
			for level = 0, 4 do
				local now = Refii.spell_in_slot(cast.p, level)
				row[level + 1] = now == cast.lent[level + 1] and cast.saved[level + 1] or now
			end
			write_table(hero, row)
		end
		H.Cast = nil
		Refii.publish("hotbar.casting", 0)
	end
end

local function perform_expression(hero, index)
	local name = EXPRESSIONS[index]
	if name == nil or ExpressionPerformer.IsPerformingExpression(hero) or
			not ExpressionPerformer.IsExpressionAvailable(hero, "Expression" .. name) then
		return
	end
	local record = expression_record(index)
	if record ~= nil then
		ExpressionPerformer.PerformExpression(hero, { ExpressionRecord = record })
	end
end

Refii.on("input_action", function(e)
	local key = tonumber(string.match(e.name or "", "^hotbar%.slot(%d)$"))
	if key == nil then
		return
	end
	if not e.pressed then
		if H.Cast ~= nil and H.Cast.p == e.player and H.Cast.key == key then
			H.Cast.released = true
		end
		return
	end
	if Refii.in_cutscene(e.player) then
		return
	end
	local hero = hero_of(e.player)
	if hero == nil then
		return
	end
	local kind, a = read_slot(e.player, key)
	if kind == SPELL then
		cast_begin(e.player, hero, key, a)
		refresh_bar()
	elseif kind == EXPRESSION then
		perform_expression(hero, a)
	end
	-- ITEM: godot/hotbar.gd uses it on the same press.
end)

-- ---- the Hotbar page ------------------------------------------------------------------------

local function slot_rows(p)
	local hero = hero_of(p)
	local rows = {}
	for _, key in ipairs(KEYS) do
		local _, _, name = describe(p, key, hero)
		rows[#rows + 1] = { name = "slot" .. key, label = key .. "   " .. (name or "-"), command = "Opens:hotbar.kind" }
	end
	Refii.set_menu_rows("hotbar.slots", rows, p)
end

-- One page of the pick list behind the kind chosen.
local function pick_rows(p)
	local pick = H.Pick[p]
	local rows = {}
	if pick == nil or #pick.list == 0 then
		rows[1] = { name = "none", label = "HOTBAR_NONE", ghosted = true }
	else
		local first = pick.page * PAGE + 1
		for i = first, math.min(#pick.list, first + PAGE - 1) do
			rows[#rows + 1] = { name = "p" .. i, label = pick.list[i].tag, command = "Mod:lua:Hotbar.assign:" .. i }
		end
		if #pick.list > first + PAGE - 1 then
			rows[#rows + 1] = { name = "more", label = "HOTBAR_MORE", command = "Mod:lua:Hotbar.page:" .. (pick.page + 1) }
		elseif pick.page > 0 then
			rows[#rows + 1] = { name = "more", label = "HOTBAR_MORE", command = "Mod:lua:Hotbar.page:0" }
		end
	end
	Refii.set_menu_rows("hotbar.pick", rows, p)
end

local function build_pick(p, kind)
	local hero = hero_of(p)
	local list = {}
	if hero ~= nil and kind == "spells" then
		for spell = 0, 7 do
			if SpellManager.IsEntitySpellTypePowerLevelPurchased(hero, spell, 0) then
				list[#list + 1] = { kind = SPELL, a = spell,
					tag = "GUI_SCREEN_ABILITIES_WILL_" .. SPELLS[spell].tag .. "_NAME_LEVEL_06" }
			end
		end
	elseif hero ~= nil and kind == "expressions" then
		for index, name in ipairs(EXPRESSIONS) do
			local record = expression_record(index)
			if record ~= nil and ExpressionPerformer.IsExpressionAvailable(hero, "Expression" .. name) then
				list[#list + 1] = { kind = EXPRESSION, a = index, tag = record:GetString("NameTag") }
			end
		end
	elseif hero ~= nil then
		for hash, info in pairs(scan_items(hero)) do
			local wanted = (kind == "food" and info.category == FOOD) or
				(kind == "potions" and info.category == POTIONS) or
				(kind == "items" and info.usable and not NOT_OTHER[info.category])
			local hi, lo = item_halves(hash)
			if wanted and hi ~= nil then
				list[#list + 1] = { kind = ITEM, a = hi, b = lo, tag = info.tag }
			end
		end
		table.sort(list, function(x, y) return tag_words(x.tag) < tag_words(y.tag) end)
	end
	H.Pick[p] = { kind = kind, list = list, page = 0 }
	pick_rows(p)
end

Refii.on("mod_menu_row", function(e)
	if e.menu == "hotbar.slots" then
		H.Editing[e.player] = tonumber(string.match(e.row or "", "^slot(%d)$"))
	elseif e.menu == "hotbar.kind" and e.row ~= "clear" then
		build_pick(e.player, e.row)
	elseif e.menu == "pause-root" and e.row == "hotbar.menu" then
		slot_rows(e.player)
	end
end)

-- Mod:lua rows: function(arg, e), e the row's mod_menu_row table.
function Hotbar.assign(arg, e)
	local p = e and e.player or 1
	local key = H.Editing[p]
	local pick = H.Pick[p]
	local choice = pick and pick.list[tonumber(arg) or 0]
	if key == nil or choice == nil then
		return
	end
	write_slot(p, key, choice.kind, choice.a, choice.b)
	slot_rows(p)
	refresh_bar()
	Refii.log("hotbar: slot " .. key .. " now holds " .. tostring(choice.tag))
end

function Hotbar.page(arg, e)
	local p = e and e.player or 1
	if H.Pick[p] ~= nil then
		H.Pick[p].page = tonumber(arg) or 0
		pick_rows(p)
	end
end

function Hotbar.clear(_, e)
	local p = e and e.player or 1
	local key = H.Editing[p]
	if key ~= nil then
		write_slot(p, key, EMPTY, 0, 0)
		slot_rows(p)
		refresh_bar()
	end
end

-- The slot list is stated before the page can open, and again whenever a hero arrives.
Refii.on("hero_ready", function(e)
	if e.player == 1 or e.player == 2 then
		slot_rows(e.player)
	end
end)

Refii.on("level_loaded", function()
	H.Items = {}
	published = {}
	for p = 1, 2 do
		if Refii.player_hero(p) ~= nil then
			slot_rows(p)
		end
	end
end)

for p = 1, 2 do
	if Refii.player_hero(p) ~= nil then
		slot_rows(p)
	end
end
