-- Spell Weaving on the title and the front end (mod/frontend.lua).
--
-- There is no game here — no hero, no save, no world — so this file runs in the current front-end
-- presentation's own Lua state, with the front end's Refii: notices, the menu box, message boxes,
-- menus, published values and the menu events (mod_menu_row, mod_menu_box, mod_message_box,
-- mod_notice). The "Spell Weaving" row on the title (ui/templates.json, a patch on frontend-root)
-- calls spell_weaving_frontend.help; the topic chosen in its menu box comes back as mod_menu_box,
-- and the topic's text goes up in a message box. Everything it says is how the in-game surfaces
-- work: the ability on the Hybrid page, and each slot's partner picked on the spell selectors.

spell_weaving_frontend = {}

local topics = {
	{
		"Weaving",
		"Two spells from one release, as Fable III's gauntlets. Of a slot's spell and its partner, the\n" ..
		"higher in the hierarchy is cast and the other rides its hits: Vortex, Blades, Fireball, Shock,\n" ..
		"Force Push, Chaos. Slow Time and Raise Dead are never partners.",
	},
	{
		"Choosing partners",
		"Each spell slot has its own partner. On the quick wheel or the spell page of the abilities\n" ..
		"book, highlight a will and press X to make it the slot's partner; Y removes it. Each player\n" ..
		"picks their own, and the picks are saved with that player's hero.",
	},
	{
		"The ability",
		"Spell Weaving is bought on the Hybrid page of the abilities book with Will and Skill\n" ..
		"experience in equal measure. Level N weaves casts up to power N; Y on the row unlearns a\n" ..
		"level and gives half its price back.",
	},
}

-- The id of the menu box this file raised, so its answer is told apart from another mod's.
local asked = nil

function spell_weaving_frontend.help(arg, e)
	local names = {}
	for i, topic in ipairs(topics) do
		names[i] = topic[1]
	end
	asked = Refii.menu_box("Spell Weaving", "What would you like to know?", names, e and e.player or 0)
end

Refii.on("mod_menu_box", function(e)
	if asked == nil or e.id ~= asked then
		return
	end
	asked = nil
	local topic = topics[(e.option or -1) + 1]
	if topic ~= nil then
		Refii.message_box(topic[1], topic[2], nil, nil, e.player)
	end
end)
