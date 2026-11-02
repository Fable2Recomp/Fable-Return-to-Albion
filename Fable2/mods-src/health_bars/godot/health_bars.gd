extends Node
## Health Bars: a bar of health over every enemy near the heroes.
##
## Each bar is a world meter (world_meter) on the HUD's world-icon channel: drawn through the camera
## of the player it is for, in that player's half on split screen, lifted by the drawn body, sized by
## the game's distance curve, with a pale trail after each drop. On split screen each player has their
## own bars, around their own hero; on one shared screen one set serves every player this machine
## plays.
##
## Who has a bar: a look every tenth of a second around each hero for living creatures the game
## counts as that hero's enemies (RELATION_ENEMY) within range, nearest first, up to MAX. A look asks
## the guest: the hero's position, the creatures near it (query near, a page of 100 at a time, the
## next page only while fewer than MAX enemies are found), and each one's relation to the hero and
## whether it is living — the liveness once per creature (entity_value, forgotten at entity_removed),
## then entity_died. The value: health read once when the bar is made, then restated in place from
## the game's own health_changed; entity_died and entity_removed take the bar away. Bars stand in play
## only (in_play).
##
## Settings (settings.json, the in-game Options > Mods page): show, range in metres, only_hurt to
## leave out enemies still at full health, numbers for "current / most" under the bar, size.

const MAX := 24
const EVERY := 0.1                  # seconds between looks
const STALE := 1.0                  # seconds after which a look still waiting on the guest is dropped
const LIVING := "health_bars.living"

var host: Object
var enemy := 4
var bars := {}                      # view -> { entity -> { id, value, max } }; view 0 is every player here
var near := {}                      # view -> { entity -> true }: the enemies the last look found in range
var split = null
var since := 0.0
var looks := {}                     # view -> the look in progress
var generation := 0                 # steps on every clear; a look of an older generation is dropped


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	enemy = int(host.constants().get("RELATION_ENEMY", enemy))
	host.tick.connect(_on_tick)
	host.health_changed.connect(_on_health_changed)
	host.entity_died.connect(_on_died)
	host.entity_removed.connect(_on_gone)
	host.level_unloading.connect(func(_e: Dictionary) -> void: _clear())
	host.setting_changed.connect(_on_setting_changed)


func _style() -> String:
	var text := "health"
	if host.setting("health_bars.numbers", 0.0) >= 0.5:
		text += " numbers"
	return text + " x%.2f" % host.setting("health_bars.size", 1.0)


func _drop(view: int, e: int) -> void:
	var list: Dictionary = bars.get(view, {})
	if list.has(e):
		host.unmark(list[e].id)
		list.erase(e)


func _clear() -> void:
	for view in bars:
		for e in bars[view]:
			host.unmark(bars[view][e].id)
	bars.clear()
	near.clear()
	looks.clear()
	generation += 1


func _shows(current: float, most: float) -> bool:
	return most > 0.0 and current > 0.0 \
		and (host.setting("health_bars.only_hurt", 0.0) < 0.5 or current < most)


func _show(view: int, e: int, current: float, most: float) -> void:
	if not bars.has(view):
		bars[view] = {}
	var bar = bars[view].get(e)
	if bar == null:
		var id: int = host.world_meter(e, current, most, _style(), view)
		if id != 0:
			bars[view][e] = { "id": id, "value": current, "max": most }
	elif bar.value != current or bar.max != most:
		bar.value = current
		bar.max = most
		host.mark_meter(bar.id, current, most)


# The views and the players whose heroes each looks around.
func _views() -> Array:
	if host.split_screen():
		return [[1, [1]], [2, [2]]]
	return [[0, [1, 2]]]


func _on_tick(e: Dictionary) -> void:
	since += float(e.get("seconds", 0.0))
	if since < EVERY:
		return
	since = 0.0
	_scan()


func _scan() -> void:
	var on: bool = host.split_screen()
	if split != on:
		_clear()
		split = on
	if host.setting("health_bars.show", 1.0) < 0.5:
		_clear()
		return
	var reach: float = host.setting("health_bars.range", 25.0)
	for v in _views():
		var view: int = v[0]
		var look: Dictionary = looks.get(view, {})
		if not look.is_empty() and host.real_now() - look.started < STALE:
			continue
		look = { "started": host.real_now(), "generation": generation, "reach": reach,
			"waiting": 0, "lists": [] }
		looks[view] = look
		for p in v[1]:
			var hero: int = host.hero(p)
			if hero != 0 and host.player_here(p) and host.in_play(p):
				var list := { "hero": hero, "found": [] }
				look.lists.append(list)
				_wait(look, host.ask("position", [hero], func(r: Dictionary) -> void:
					if r.get("ok", false):
						_page(view, look, list, r.x, r.y, r.z, 0)
					_answered(view, look)))
		if look.waiting == 0:
			_finish(view, look)


# A look's question posted (an id) or not (0): a look completes when none is outstanding.
func _wait(look: Dictionary, id: int) -> bool:
	if id != 0:
		look.waiting += 1
	return id != 0


func _answered(view: int, look: Dictionary) -> void:
	look.waiting -= 1
	if look.waiting == 0 and is_same(looks.get(view), look):
		_finish(view, look)


# A page of the creatures near a hero, nearest first, and each one's standing to that hero.
func _page(view: int, look: Dictionary, list: Dictionary, x: float, y: float, z: float, offset: int) -> void:
	_wait(look, host.ask("near", [x, y, z, look.reach, true, offset], func(r: Dictionary) -> void:
		var count := int(r.get("count", 0))
		var total := int(r.get("total", 0))
		var page := []
		for i in range(1, count + 1):
			var e := int(r.get("e%d" % i, 0))
			if e != 0 and e != host.hero(1) and e != host.hero(2):
				page.append({ "entity": e, "relation": 0, "living": host.entity_value(e, LIVING, null) })
		var pending := { "n": 0 }
		var settle := func() -> void:
			for c in page:
				if list.found.size() >= MAX:
					break
				if c.relation == enemy and c.living == true:
					list.found.append(c.entity)
			if list.found.size() < MAX and count > 0 and offset + count < total and is_same(looks.get(view), look):
				_page(view, look, list, x, y, z, offset + count)
		var heard := func() -> void:
			pending.n -= 1
			if pending.n == 0:
				settle.call()
			_answered(view, look)
		for c in page:
			if _wait(look, host.ask("relation", [list.hero, c.entity], func(a: Dictionary) -> void:
				c.relation = int(a.get("relation", 0)) if a.get("ok", false) else 0
				heard.call())):
				pending.n += 1
			if c.living == null and _wait(look, host.ask("living", [c.entity], func(a: Dictionary) -> void:
				c.living = bool(a.get("living", false))
				host.entity_set(c.entity, LIVING, c.living)
				heard.call())):
				pending.n += 1
		if pending.n == 0:
			settle.call()
		_answered(view, look)))


# Every list in: player one's enemies first, then the other hero's not yet found, up to MAX.
func _finish(view: int, look: Dictionary) -> void:
	looks.erase(view)
	if look.generation != generation:
		return
	var found := {}
	for list in look.lists:
		for e in list.found:
			if found.size() >= MAX:
				break
			found[e] = true
	near[view] = found
	for e in bars.get(view, {}).keys():
		if not found.has(e):
			_drop(view, e)
	for e in found:
		if bars.get(view, {}).has(e):
			continue
		host.ask("health", [e], func(r: Dictionary) -> void:
			if look.generation != generation or not near.get(view, {}).has(e) or bars.get(view, {}).has(e):
				return
			var current := float(r.get("current", 0.0))
			var most := float(r.get("max", 0.0))
			if r.get("ok", false) and _shows(current, most):
				_show(view, e, current, most))


func _on_health_changed(e: Dictionary) -> void:
	var entity := int(e.get("entity", 0))
	for view in near:
		if near[view].has(entity):
			var current := float(e.get("health", 0.0))
			var most := float(e.get("max", 0.0))
			if _shows(current, most):
				_show(view, entity, current, most)
			else:
				_drop(view, entity)


# A creature that died is living no more; the host forgets the value at its entity_removed.
func _on_died(e: Dictionary) -> void:
	host.entity_set(int(e.get("entity", 0)), LIVING, false)
	_on_gone(e)


func _on_gone(e: Dictionary) -> void:
	var entity := int(e.get("entity", 0))
	for view in near:
		near[view].erase(entity)
		_drop(view, entity)


# A setting changed: the bars are made again on the next look, in the new style.
func _on_setting_changed(key: String, _value: float) -> void:
	if key.begins_with("health_bars."):
		_clear()
