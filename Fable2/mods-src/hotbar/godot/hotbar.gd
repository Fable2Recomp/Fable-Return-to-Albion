extends Node
## Hotbar: a Fable 1-style bar of ten slots on the keys 1-0, for each player.
##
## 1, 2 and 3 stay the game's own: the keyboard's attack mode (melee, ranged, magic), lit on the bar
## with the weapons in hand and the spell on the first charge level, which the spell wheel chooses as
## ever. 4-0 hold what the player puts there from the Hotbar page of the pause menu, and add to the
## game's controls without changing them:
##
##   a spell        the key charges it through the player's own magic charge (charge_spell): held,
##                  it charges through the power levels the hero has; let go, it casts; a tap casts
##                  at once. The player's own spell slots come back afterwards;
##   an expression  the key performs it (perform_expression, held, ungated): a tap plays its basic
##                  form, held it extends as holding the wheel's button does, until the key is let go
##                  (release_expression);
##   an item        the key uses one, as the Items page's Use does (use_item, ungated).
##
## The charge and the expression are asked through their answering forms, so a slot lights and a
## release is sent only for what the game took; a key let go before the answer is let go after it.
##
## A slot is the hero's, saved with them: hotbar.k<key> (the kind), hotbar.a<key> (a spell type) and
## hotbar.h<key> (an expression's or an item's record, a typed hash). A slot saved by 0.1 (an item's
## hash as two 16-bit halves in hotbar.a/b<key>, an expression as its index in LEGACY_EXPRESSIONS) is
## read once and written in the new form.
##
## THE BAR is ui/hud.json, each player's, so the HUD's own visibility, scale and split-screen halves
## come with it. Per slot s (1-10, the 0 key is 10) hud_icon fills hotbar.icon<s> (usable now) or
## hotbar.dim<s> (not), and the player store keys the rest: hotbar.frame (0 drawn), hotbar.ring<s> and
## hotbar.lit<s> (0 drawn plain / lit), hotbar.key<s> (1 plain, -1 lit, 0 hidden), hotbar.badge<s>
## (0 drawn) and hotbar.count<s> (an item's stack, -1 for none). Restated on the events that change
## them, never polled; what the hero holds is asked of the guest each time (query spells_known,
## expressions, inventory, equipped, spell_in_slot), a record's icon and name once (record_string,
## record_ref).
##
## THE HOTBAR PAGE: the slot list is stated with set_menu_rows; the pick lists are host lists
## (ui/templates.json), whose rows run the verbs Hotbar:assign and Hotbar:clear registered here with
## the row (register_command with_row), so a pick arrives with its typed subject.

const EMPTY := 0
const SPELL := 1
const EXPRESSION := 2
const ITEM := 3
const KEYS := [4, 5, 6, 7, 8, 9, 0]
const MIN_HOLD := 0.08   # seconds the magic button stays down for a tap, so the game reads the press
const NONE := "0x00000000"

# 0.1's expression slots, by index from 1: read only to migrate them to records.
const LEGACY_EXPRESSIONS := [
	"ThumbsUp", "ThumbsDown", "Dance", "Chicken", "SexyPose", "PointAndLaugh", "Laugh", "Growl",
	"BloodlustRoar", "ScaryLaugh", "BodilyFunction", "Belch", "Fart", "Insult", "VulgarThrust",
	"KissMyAss", "PickUpLine", "BlowKiss", "Kiss", "Seduce", "ComeBackToMyPlace", "Whistle",
	"Worship", "Apologise", "Encourage", "Beg", "Extort", "Threaten", "Slap", "Fakeout",
	"SockPuppet", "Lute", "VictoryArmPump", "Flex", "PlayDead", "HatMoustacheHeadband", "Follow",
	"Wait",
]


## Calls `done` once every question held on it is answered (hold before asking, release on answer).
class Join:
	var pending := 1
	var done: Callable

	func _init(then: Callable) -> void:
		done = then

	func hold() -> void:
		pending += 1

	func release() -> void:
		pending -= 1
		if pending == 0:
			done.call()


var host: Object
var melee_mode := 1
var ranged_mode := 2
var magic_mode := 3

# Per player, indexed 1 and 2.
var editing := [-1, -1, -1]          # the key whose slot the Hotbar page is filling
var charge := [null, null, null]     # { key, spell, at, taken, up, released } while a slot's charge is out
var held := [null, null, null]       # { key, performed, up }: the key holding an expression
var third := ["", "", ""]            # slot 3's icon, kept while a hotbar charge has the slots lent
var stated := [{}, {}, {}]           # what the HUD was last told, by key
var refreshing := [0, 0, 0]          # steps on every refresh; an older one's answers are dropped

var strings := {}                    # "def|field|item" -> the record's string, or null for none
var asking := {}                     # "def|field|item" -> the joins waiting on its answer


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	var named: Dictionary = host.constants()
	melee_mode = int(named.get("ATTACK_MODE_MELEE", melee_mode))
	ranged_mode = int(named.get("ATTACK_MODE_RANGED", ranged_mode))
	magic_mode = int(named.get("ATTACK_MODE_MAGIC", magic_mode))
	host.register_command("Hotbar:assign", _assign, true)
	host.register_command("Hotbar:clear", _clear_slot, true)
	host.input_action.connect(_on_input_action)
	host.spell_charge_ended.connect(_on_spell_charge_ended)
	host.spell_ended.connect(_on_spell_ended)
	for changed in [host.item_added, host.item_removed, host.item_used, host.weapon_equipped,
			host.ability_level, host.spell_pick]:
		changed.connect(func(e: Dictionary) -> void: _soon(int(e.get("player", 0))))
	host.attack_mode_changed.connect(_on_attack_mode_changed)
	host.setting_changed.connect(_on_setting_changed)
	host.hero_gone.connect(_on_hero_gone)
	host.hero_ready.connect(_on_hero_ready)
	host.level_loaded.connect(_on_level_loaded)
	host.mod_menu_row.connect(_on_mod_menu_row)
	for p in [1, 2]:
		if host.hero(p) != 0:
			_slot_rows(p)
		_refresh(p)


# The script going lets a held expression go.
func _exit_tree() -> void:
	if not is_instance_valid(host):
		return
	for p in [1, 2]:
		if held[p] != null and held[p].performed:
			host.release_expression(p)


func _slot_of(key: int) -> int:
	return 10 if key == 0 else key


# A record hash as "0x%08x", the one spelling every list and store answers in; "" for none.
func _norm(hash) -> String:
	var text := str(hash if hash != null else "").to_lower()
	if not text.begins_with("0x"):
		return ""
	var hex := text.substr(2)
	if hex.is_empty() or hex.length() > 8 or not hex.is_valid_hex_number():
		return ""
	return "0x" + hex.lpad(8, "0")


# ---- what a slot holds ---------------------------------------------------------------------

func _legacy_hash(p: int, key: int, kind: int) -> String:
	var a: float = host.player_value(p, "hotbar.a%d" % key)
	if is_nan(a):
		return ""
	if kind == ITEM:
		var b: float = host.player_value(p, "hotbar.b%d" % key)
		if is_nan(b) or (a == 0.0 and b == 0.0):
			return ""
		return "0x%04x%04x" % [int(a), int(b)]
	var index := int(a)
	if float(index) != a or index < 1 or index > LEGACY_EXPRESSIONS.size():
		return ""
	return _norm(host.hash("Expression" + LEGACY_EXPRESSIONS[index - 1]))


# The kind and what: a spell type, or a record hash for an expression or an item.
func _read_slot(p: int, key: int) -> Array:
	var kind := int(host.player_value(p, "hotbar.k%d" % key, EMPTY))
	if kind == SPELL:
		return [kind, int(host.player_value(p, "hotbar.a%d" % key, -1))]
	if kind == EXPRESSION or kind == ITEM:
		var hash: String = host.player_value_hash(p, "hotbar.h%d" % key)
		if hash.is_empty():
			hash = _legacy_hash(p, key, kind)
			if hash.is_empty():
				return [EMPTY, null]
			host.player_save_hash(p, "hotbar.h%d" % key, hash)
			host.player_save(p, "hotbar.a%d" % key, 0.0)
			host.player_save(p, "hotbar.b%d" % key, 0.0)
			print("hotbar: player %d slot %d migrated to %s" % [p, key, hash])
		return [kind, hash]
	return [EMPTY, null]


func _write_slot(p: int, key: int, kind: int, spell: int, hash: String) -> void:
	host.player_save(p, "hotbar.k%d" % key, float(kind))
	host.player_save(p, "hotbar.a%d" % key, float(spell))
	if not hash.is_empty():
		host.player_save_hash(p, "hotbar.h%d" % key, hash)


# ---- the records' strings: asked once, kept --------------------------------------------------

func _string_key(def: String, field: String, item: bool) -> String:
	return "%s|%s|%s" % [def, field, item]


# A record's string, as far as it is known (null for none or not yet asked).
func _string(def: String, field: String, item: bool):
	return strings.get(_string_key(def, field, item))


# Asks a record's string on `join` unless it is known: an item's name tag or icon is on its record,
# else on the InventoryItemComponent it names. A string already being asked holds `join` too.
func _need(join: Join, def: String, field: String, item: bool) -> void:
	var key := _string_key(def, field, item)
	if def.is_empty() or strings.has(key):
		return
	join.hold()
	if asking.has(key):
		asking[key].append(join)
		return
	asking[key] = [join]
	var settle := func(value) -> void:
		strings[key] = value
		var waiting: Array = asking.get(key, [])
		asking.erase(key)
		for j in waiting:
			j.release()
	var fallback := func() -> void:
		var refd: int = host.ask("record_ref", [def, "InventoryItemComponent"], func(c: Dictionary) -> void:
			var component := _norm(c.get("value", "")) if c.get("ok", false) else ""
			if component.is_empty() or component == NONE:
				settle.call(null)
				return
			var got: int = host.ask("record_string", [component, field], func(r: Dictionary) -> void:
				settle.call(str(r.get("value", "")) if r.get("ok", false) else null))
			if got == 0:
				_unasked(key))
		if refd == 0:
			_unasked(key)
	var asked: int = host.ask("record_string", [def, field], func(r: Dictionary) -> void:
		if r.get("ok", false):
			settle.call(str(r.get("value", "")))
		elif item:
			fallback.call()
		else:
			settle.call(null))
	if asked == 0:
		_unasked(key)


# A string whose question could not be posted: asked again next time.
func _unasked(key: String) -> void:
	var waiting: Array = asking.get(key, [])
	asking.erase(key)
	for j in waiting:
		j.release()


# The strings every slot of player p needs for `field`.
func _need_slots(join: Join, p: int, field: String) -> void:
	for key in KEYS:
		var slot := _read_slot(p, key)
		if slot[0] == EXPRESSION:
			_need(join, slot[1], field, false)
		elif slot[0] == ITEM:
			_need(join, slot[1], field, true)


# What a slot shows: [kind, icon, name tag, usable now, an item's stack (-1 for none)].
func _describe(p: int, key: int, known: Dictionary) -> Array:
	var slot := _read_slot(p, key)
	var kind: int = slot[0]
	if kind == SPELL:
		var info: Dictionary = host.spell_info(slot[1])
		if info.has("name_tag"):
			return [kind, str(info.get("icon", "")), info.name_tag, known.spells.has(slot[1]), -1]
	elif kind == EXPRESSION:
		var icon = _string(slot[1], "IconGraphic", false)
		return [kind, icon if icon != null else "", _string(slot[1], "NameTag", false),
			known.expressions.has(slot[1]), -1]
	elif kind == ITEM:
		var stack: Dictionary = known.items.get(slot[1], {})
		var count: int = stack.get("count", 0)
		var icon = _string(slot[1], "IconGraphic", true)
		return [kind, icon if icon != null else "", _string(slot[1], "NameTag", true),
			not stack.is_empty() and stack.usable and count > 0, count]
	return [EMPTY, "", null, false, -1]


# ---- the bar --------------------------------------------------------------------------------

func _state_value(p: int, key: String, value: float) -> void:
	if not stated[p].has(key) or stated[p][key] != value:
		stated[p][key] = value
		host.player_set(p, key, value)


func _state_icon(p: int, piece: String, texture: String) -> void:
	if not stated[p].has(piece) or stated[p][piece] != texture:
		stated[p][piece] = texture
		host.hud_icon(p, piece, texture)


func _show_slot(p: int, s: int, icon: String, ok: bool, lit: bool, count: int) -> void:
	_state_icon(p, "hotbar.icon%d" % s, icon if ok else "")
	_state_icon(p, "hotbar.dim%d" % s, "" if ok else icon)
	_state_value(p, "hotbar.ring%d" % s, -1.0 if lit else 0.0)
	_state_value(p, "hotbar.lit%d" % s, 0.0 if lit else -1.0)
	_state_value(p, "hotbar.key%d" % s, -1.0 if lit else 1.0)
	if s >= 4:
		_state_value(p, "hotbar.badge%d" % s, 0.0 if count >= 0 else -1.0)
		_state_value(p, "hotbar.count%d" % s, float(count))


func _hide_bar(p: int) -> void:
	_state_value(p, "hotbar.frame", -1.0)
	for s in range(1, 11):
		_state_icon(p, "hotbar.icon%d" % s, "")
		_state_icon(p, "hotbar.dim%d" % s, "")
		_state_value(p, "hotbar.ring%d" % s, -1.0)
		_state_value(p, "hotbar.lit%d" % s, -1.0)
		_state_value(p, "hotbar.key%d" % s, 0.0)
		if s >= 4:
			_state_value(p, "hotbar.badge%d" % s, -1.0)
			_state_value(p, "hotbar.count%d" % s, -1.0)


# Player p's bar, where p is played: what the hero can use now is asked of the guest — the spells
# it can assign, the expressions it knows, its stacks by record with the game's own usable test, the
# weapons in hand and the spell on the first charge level — then the icons those name.
func _refresh(p: int) -> void:
	if not host.player_here(p):
		return
	var hero: int = host.hero(p)
	refreshing[p] += 1
	var token: int = refreshing[p]
	if hero == 0 or host.setting("hotbar.show", 1.0) < 0.5:
		_hide_bar(p)
		return
	var known := { "spells": {}, "expressions": {}, "items": {}, "melee": "", "ranged": "", "third": null }
	var strings_in := func() -> void:
		if refreshing[p] == token:
			_show_bar(p, known)
	var answers := Join.new(func() -> void:
		if refreshing[p] != token:
			return
		var join := Join.new(strings_in)
		_need(join, known.melee, "IconGraphic", true)
		_need(join, known.ranged, "IconGraphic", true)
		_need_slots(join, p, "IconGraphic")
		join.release())
	_ask(answers, "spells_known", [p], func(r: Dictionary) -> void:
		var mask := int(r.get("mask", 0)) if r.get("ok", false) else 0
		for spell in range(8):
			if mask & (1 << spell):
				known.spells[spell] = true)
	_ask(answers, "expressions", [p], func(r: Dictionary) -> void:
		for i in range(1, int(r.get("count", 0)) + 1):
			var id := _norm(r.get("e%d" % i, ""))
			if not id.is_empty():
				known.expressions[id] = true)
	_inventory(answers, p, 0, known)
	_ask(answers, "equipped", [hero], func(r: Dictionary) -> void:
		if r.get("ok", false):
			var melee := _norm(r.get("melee", ""))
			var ranged := _norm(r.get("ranged", ""))
			known.melee = "" if melee == NONE else melee
			known.ranged = "" if ranged == NONE else ranged)
	if charge[p] == null or not charge[p].taken:
		_ask(answers, "spell_in_slot", [p, 0], func(r: Dictionary) -> void:
			known.third = str(host.spell_info(int(r.get("type", -1))).get("icon", "")))
	answers.release()


# A question held on `join`: `then` runs with the answer, then the join is released.
func _ask(join: Join, what: String, args: Array, then: Callable) -> void:
	join.hold()
	var id: int = host.ask(what, args, func(r: Dictionary) -> void:
		then.call(r)
		join.release())
	if id == 0:
		join.release()


# Player p's hero's stacks, page by page, summed by record.
func _inventory(join: Join, p: int, offset: int, known: Dictionary) -> void:
	_ask(join, "inventory", [p, offset], func(r: Dictionary) -> void:
		if not r.get("ok", false):
			return
		var count := int(r.get("count", 0))
		for i in range(1, count + 1):
			var def := _norm(r.get("d%d" % i, ""))
			if def.is_empty():
				continue
			var stack: Dictionary = known.items.get(def, { "count": 0, "usable": false })
			stack.count += int(r.get("n%d" % i, 0))
			stack.usable = stack.usable or int(r.get("u%d" % i, 0)) != 0
			known.items[def] = stack
		var next := int(r.get("next", 0))
		if count > 0 and next < int(r.get("total", 0)):
			_inventory(join, p, next, known))


func _show_bar(p: int, known: Dictionary) -> void:
	var mode: int = host.attack_mode(p)
	_state_value(p, "hotbar.frame", 0.0)
	var melee: String = known.melee
	var ranged: String = known.ranged
	_show_slot(p, 1, _icon(melee), not melee.is_empty(), mode == melee_mode, -1)
	_show_slot(p, 2, _icon(ranged), not ranged.is_empty(), mode == ranged_mode, -1)
	if known.third != null and (charge[p] == null or not charge[p].taken):
		third[p] = known.third
	_show_slot(p, 3, third[p], third[p] != "", mode == magic_mode, -1)
	for key in KEYS:
		var shown := _describe(p, key, known)
		var charging: bool = charge[p] != null and charge[p].taken and charge[p].key == key
		_show_slot(p, _slot_of(key), shown[1], shown[3], charging, shown[4] if shown[0] == ITEM else -1)


func _icon(def: String) -> String:
	var icon = _string(def, "IconGraphic", true) if not def.is_empty() else null
	return icon if icon != null else ""


# Several changes in one moment restate the bar once.
func _soon(p: int) -> void:
	if p == 1 or p == 2:
		host.after(0.1, func() -> void: _refresh(p), "hotbar.refresh%d" % p)


func _restate(p: int) -> void:
	stated[p] = {}
	_refresh(p)


# ---- the keys -------------------------------------------------------------------------------

# A charge's slot stays lit until the cast is out and the player's own slots are back.
func _charge_over(p: int, c: Dictionary) -> void:
	if is_same(charge[p], c):
		charge[p] = null
		_refresh(p)


func _release_charge(p: int, c: Dictionary) -> void:
	if c.released:
		return
	if not c.taken:
		c.up = true                          # let go once the game has taken it
		return
	c.released = true
	var up := func() -> void:
		host.charge_spell(p, c.spell, false)
		host.after(1.2, func() -> void: _charge_over(p, c), "hotbar.charge%d" % p)
	var wait: float = MIN_HOLD - (host.now() - c.at)
	if wait > 0.0:
		host.after(wait, up, "hotbar.release%d" % p)
	else:
		up.call()


func _on_input_action(e: Dictionary) -> void:
	var name: String = e.get("name", "")
	var p := int(e.get("player", 0))
	if not name.begins_with("hotbar.slot") or name.length() != 12 or (p != 1 and p != 2):
		return
	var digit := name.substr(11)
	if not digit.is_valid_int():
		return
	var key := int(digit)
	if not e.get("pressed", false):
		var c = charge[p]
		if c != null and c.key == key:
			_release_charge(p, c)
		var h = held[p]
		if h != null and h.key == key:
			held[p] = null
			if h.performed:
				host.release_expression(p)
			else:
				h.up = true
		return
	if not host.in_play(p) or host.in_cutscene(p):
		return
	var slot := _read_slot(p, key)
	if slot[0] == SPELL:
		if charge[p] == null:
			_charge(p, key, slot[1])
	elif slot[0] == EXPRESSION:
		if held[p] == null:
			_perform(p, key, slot[1])
	elif slot[0] == ITEM:
		var hero: int = host.hero(p)
		if hero != 0:
			var what: String = slot[1]
			host.ask("item_count", [hero, what], func(r: Dictionary) -> void:
				if int(r.get("count", 0)) > 0:
					host.use_item(p, what, { "ungated": true }))


func _charge(p: int, key: int, spell: int) -> void:
	var c := { "key": key, "spell": spell, "at": 0.0, "taken": false, "up": false, "released": false }
	charge[p] = c
	var posted: bool = host.charge_spell(p, spell, true, func(taken) -> void:
		if not is_same(charge[p], c):
			return
		if taken != true:
			charge[p] = null
			return
		c.taken = true
		c.at = host.now()
		host.after(31.0, func() -> void: _charge_over(p, c), "hotbar.cap%d" % p)
		_refresh(p)
		if c.up:
			_release_charge(p, c))
	if not posted:
		charge[p] = null


func _perform(p: int, key: int, expression: String) -> void:
	var h := { "key": key, "performed": false, "up": false }
	held[p] = h
	var posted: bool = host.perform_expression(p, expression, true, { "ungated": true }, func(performed) -> void:
		if performed != true:
			if is_same(held[p], h):
				held[p] = null
			return
		h.performed = true
		if h.up:
			host.release_expression(p))
	if not posted:
		held[p] = null


# A charge the game ended without a release (a hit, a roll) lets the button go; a released one
# lights the slot until the cast is out.
func _on_spell_charge_ended(e: Dictionary) -> void:
	var p := int(e.get("player", 0))
	if (p == 1 or p == 2) and charge[p] != null and not e.get("released", false):
		_release_charge(p, charge[p])


func _on_spell_ended(e: Dictionary) -> void:
	var p := int(e.get("caster_player", 0))
	if p != 1 and p != 2:
		return
	var c = charge[p]
	if c != null and c.released:
		host.after(0.1, func() -> void: _charge_over(p, c), "hotbar.charge%d" % p)


# ---- what changes the bar -------------------------------------------------------------------

func _on_attack_mode_changed(e: Dictionary) -> void:
	var p := int(e.get("player", 0))
	if p == 1 or p == 2:
		_refresh(p)


func _on_setting_changed(key: String, _value: float) -> void:
	if key == "hotbar.show":
		_refresh(1)
		_refresh(2)


func _on_hero_gone(e: Dictionary) -> void:
	var p := int(e.get("player", 0))
	if p == 1 or p == 2:
		charge[p] = null
		held[p] = null
		_refresh(p)


# ---- the Hotbar page ------------------------------------------------------------------------

# The slot list: "4   {NAME_TAG}", drawn in the player's language.
func _slot_rows(p: int) -> void:
	var join := Join.new(func() -> void:
		var nothing := { "spells": {}, "expressions": {}, "items": {} }
		var rows := []
		for key in KEYS:
			var tag = _describe(p, key, nothing)[2]
			rows.append({ "name": "slot%d" % key, "label": "%d   %s" % [key, "{%s}" % tag if tag != null else "-"],
				"command": "Opens:hotbar.kind" })
		host.set_menu_rows("hotbar.slots", rows, p))
	_need_slots(join, p, "NameTag")
	join.release()


func _on_mod_menu_row(e: Dictionary) -> void:
	var p := int(e.get("player", 0))
	if p != 1 and p != 2:
		return
	var row: String = e.get("row", "")
	if e.get("menu", "") == "hotbar.slots":
		var digit := row.substr(4)
		editing[p] = int(digit) if row.begins_with("slot") and row.length() == 5 and digit.is_valid_int() else -1
	elif e.get("menu", "") == "pause-root" and row == "hotbar.menu":
		_slot_rows(p)


# Hotbar:assign, the pick lists' row verb: e is the row, its typed subject from the host list
# (spells_known, expressions, inventory:usable).
func _assign(_argument: String, e: Dictionary) -> void:
	var p := int(e.get("player", 1))
	var key: int = editing[p] if p == 1 or p == 2 else -1
	if key < 0:
		return
	var kind: String = e.get("subject_kind", "")
	var definition := _norm(e.get("definition", ""))
	var spell := int(e.get("spell", -1))
	if kind == "will" and spell >= 0:
		_write_slot(p, key, SPELL, spell, "")
	elif kind == "expression" and not definition.is_empty():
		_write_slot(p, key, EXPRESSION, 0, definition)
	elif kind == "item" and not definition.is_empty():
		_write_slot(p, key, ITEM, 0, definition)
	else:
		return
	_slot_rows(p)
	_refresh(p)
	print("hotbar: player %d slot %d now holds %s %s" % [p, key, kind, definition if not definition.is_empty() else str(spell)])


# Hotbar:clear: the slot being filled emptied.
func _clear_slot(_argument: String, e: Dictionary) -> void:
	var p := int(e.get("player", 1))
	var key: int = editing[p] if p == 1 or p == 2 else -1
	if key >= 0:
		_write_slot(p, key, EMPTY, 0, "")
		_slot_rows(p)
		_refresh(p)


# The slot list is stated before the page can open, and the bar again, whenever a hero arrives.
func _on_hero_ready(e: Dictionary) -> void:
	var p := int(e.get("player", 0))
	if p == 1 or p == 2:
		_slot_rows(p)
		_restate(p)


func _on_level_loaded(_e: Dictionary) -> void:
	for p in [1, 2]:
		if host.hero(p) != 0:
			_slot_rows(p)
		_restate(p)
