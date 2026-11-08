extends Node
## Hero's Satchel: Tab opens the inventory as one see-through screen over the held world, laid out after
## SkyUI and dressed as the pause book is: a row of category icons stepped left and right, a sortable
## list stepped up and down, and the chosen item's model over a parchment card.
##
## Tab is claimed from the game, which leaves Escape on its pause. Tab holds Start for the pause, and
## own_screen takes the pause book's place, so the world holds and the mouse is free. A pad's Start still
## ends the pause, so everything closes on the paused signal.
##
## The items are inventory_items(), the game's own pages' facts, and inventory_action does what their
## rows do. A list's stat columns are whichever stat lines its items share, in the game's own words.

const FONT := "res://assets/launcher/fable2.ttf"
const REFRESH := [0.25, 1.0]          # seconds after an action until the items are read again
const SETTLE := 1.6                   # seconds a garment may take to show as on before it counts as refused
const NOTICE_TIME := 3.5
const FLASH_TIME := 0.7

const SHADE := Color(0.07, 0.045, 0.03)
const LEATHER := Color(0.11, 0.07, 0.045, 0.88)
const COPPER := Color(0.66, 0.38, 0.2)
const COPPER_LIT := Color(0.96, 0.66, 0.34)
const LINE := Color(0.66, 0.38, 0.2, 0.75)
const CREAM := Color(0.93, 0.87, 0.72)
const DIM := Color(0.68, 0.58, 0.42)
const GOLD := Color(0.96, 0.76, 0.34)
const COIN := Color(1.0, 0.9, 0.2)
const ROW_INSIDE := Color(0.05, 0.03, 0.02, 0.8)
const PARCHMENT := Color(0.84, 0.75, 0.57, 0.97)
const PARCHMENT_DARK := Color(0.68, 0.58, 0.41, 0.97)
const INK := Color(0.17, 0.1, 0.05)
const INK_DIM := Color(0.38, 0.27, 0.16)
const BETTER := Color(0.16, 0.42, 0.12)
const WORSE := Color(0.6, 0.13, 0.08)
const REFUSED := Color(0.95, 0.45, 0.32)
const STAR := "★"
const WORN := "◆"

const PANEL_X := 0.03                 # of the height, like every length here
# The layout, as fractions of the screen's height: the desktop's, and a touchscreen's, where the same
# screen is a phone's few centimetres and a fingertip taps it — every size there is TOUCH_SCALE times
# larger (tu), the list shows fewer, taller rows and the panel and the card take more of the wide screen.
const DESK := { "panel_w": 0.84, "title_y": 0.058, "icons_y": 0.098, "labels_y": 0.139, "icon_step": 0.058,
	"rule_y": 0.149, "head_y": 0.177, "rows_y": 0.19, "row_h": 0.0335, "bar_y": 0.915, "card_w": 0.62 }
const TOUCH := { "panel_w": 1.25, "title_y": 0.095, "icons_y": 0.16, "labels_y": 0.226, "icon_step": 0.085,
	"rule_y": 0.242, "head_y": 0.287, "rows_y": 0.3, "row_h": 0.054, "bar_y": 0.895, "card_w": 0.8 }
const TOUCH_SCALE := 1.6
const TOUCH_LEFT := 0.27              # where the bar's buttons start on a touchscreen, clear of its Options
const MODEL_SIZE := 0.36
const MODEL_TURN := 0.5                # radians a second

# The categories, stepped left and right: a name, the icon drawn for it and what belongs in it.
const CATEGORIES := [
	{ "name": "Favourites", "icon": "star", "favourites": true },
	{ "name": "All", "icon": "bag" },
	{ "name": "Melee", "icon": "sword", "weapon": 1 },
	{ "name": "Ranged", "icon": "gun", "weapon": 2 },
	{ "name": "Clothing", "icon": "shirt", "ids": [25, 17, 18, 19, 20, 21, 22, 23], "undress": true },
	{ "name": "Potions", "icon": "flask", "ids": [6] },
	{ "name": "Food", "icon": "apple", "ids": [5] },
	{ "name": "Gifts", "icon": "gift", "ids": [3] },
	{ "name": "Books", "icon": "book", "ids": [16] },
	{ "name": "Dyes", "icon": "drop", "ids": [8] },
	{ "name": "Augments", "icon": "gem", "ids": [7] },
	{ "name": "Trophies", "icon": "cup", "ids": [4] },
	{ "name": "Furniture", "icon": "chair", "ids": [15] },
	{ "name": "Keys and Tools", "label": "Keys", "icon": "key", "ids": [2, 26, 0, 24] },
]
const KIND_NAMES := { 1: "Weapon", 2: "Tool", 3: "Gift", 4: "Trophy", 5: "Food", 6: "Potion", 7: "Augment", 8: "Dye",
	15: "Furniture", 16: "Book", 17: "Hat", 18: "Coat", 19: "Shirt", 20: "Gloves", 21: "Trousers", 22: "Shoes",
	23: "Accessory", 25: "Outfit", 26: "Key item", 0: "Treasure" }
const SLOT_IDS := [17, 18, 19, 20, 21, 22, 23, 25]

var host: Object
var layer: CanvasLayer
var canvas: Control
var face: Font
var plain: Font
var numerals: Font                    # the face's superscript figures: its ordinary 1 and 2 have decorative cuts

var open := false
var opening := false                  # Start is held for the pause
var opening_for := 0.0
var closing := false
var items: Array = []
var shown: Array = []                 # the items the list shows, sorted
var pinned := {}                      # the definitions sorted to the top: what was held when the list was last ordered
var order_held := false               # an action was just taken here: the rows stay where they are
var melee := ""                       # the definitions in hand
var ranged := ""
var chosen_melee := ""                # what this opening equipped: the game only applies it as the pause ends
var chosen_ranged := ""
var waiting := {}                     # a garment asked for that has yet to show: { definition, on, done, refused }
var waiting_for := 0.0
var notice := ""                      # what the last action did, in the bar
var notice_for := 0.0
var notice_refused := false
var flash := ""                       # the definition whose row is lit for that
var flash_for := 0.0
var gold := -1
var category := 1
var row := 0
var scroll := 0
var sort_column := "name"
var sort_down := false
var search := ""
var searching := false
var on_touch := false                  # laid out for a touchscreen: as the screen was when it opened, then the last hand used
var columns: Array = []               # { key, title, width } of the list as shown
var refresh_in: Array = []
var icons := {}                       # path -> Texture2D, or null when it would not load
var icon_budget := 0
var hits: Array = []                  # { rect, kind, value }: what a click on the screen means
var unit := 1.0
var tu := 1.0                          # a size's unit: the screen's height, times TOUCH_SCALE on a touchscreen
var panel_w: float = DESK.panel_w
var title_y: float = DESK.title_y
var icons_y: float = DESK.icons_y
var labels_y: float = DESK.labels_y
var icon_step: float = DESK.icon_step
var rule_y: float = DESK.rule_y
var head_y: float = DESK.head_y
var rows_y: float = DESK.rows_y
var row_h: float = DESK.row_h
var bar_y: float = DESK.bar_y
var card_w: float = DESK.card_w
var last_click := -1.0
var last_row := -1
var bar_top := 0.0                    # the list's scrollbar as last drawn, and whether it is held
var bar_h := 0.0
var thumb_h := 0.0
var bar_held := false
var stage: SubViewport                # the chosen item's model, drawn over the world
var stage_model: Node3D               # one mesh, or an outfit's several
var stage_item := {}                  # what is on it
var stage_centre := Vector3.ZERO
var stage_turn := 0.0
var stage_camera: Camera3D
var meshes := {}                      # path -> Mesh, or null when it would not load
var staged := ""


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	face = load(FONT) as Font
	if face == null:
		face = ThemeDB.fallback_font
	plain = face
	var figures := FontVariation.new()
	figures.base_font = face
	figures.opentype_features = { TextServerManager.get_primary_interface().name_to_tag("sups"): 1 }
	numerals = figures
	layer = CanvasLayer.new()
	layer.layer = 6                     # over the UI (5), under the loading screen (8)
	add_child(layer)
	canvas = Control.new()
	canvas.mouse_filter = Control.MOUSE_FILTER_IGNORE
	canvas.set_anchors_preset(Control.PRESET_FULL_RECT)
	canvas.visible = false
	canvas.draw.connect(_draw_screen)
	layer.add_child(canvas)
	_build_stage()
	host.input_action.connect(_on_input_action)
	host.paused.connect(_on_paused)
	host.hero_ready.connect(func(_e): _claim())
	host.lua_ready.connect(func(_e): _claim())   # a reload clears the claims
	_claim()


## A world of its own holding the chosen item, with the viewport's clear colour see-through.
func _build_stage() -> void:
	stage = SubViewport.new()
	stage.own_world_3d = true
	stage.transparent_bg = true
	stage.size = Vector2i(768, 768)
	stage.render_target_update_mode = SubViewport.UPDATE_DISABLED
	add_child(stage)
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0, 0, 0, 0)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.72, 0.7, 0.66)
	environment.ambient_light_energy = 0.75
	var world := WorldEnvironment.new()
	world.environment = environment
	stage.add_child(world)
	for angle in [Vector3(-40, 35, 0), Vector3(-15, -130, 0)]:
		var light := DirectionalLight3D.new()
		light.rotation_degrees = angle
		light.light_energy = 1.1 if angle.y > 0 else 0.5
		stage.add_child(light)
	stage_model = Node3D.new()
	stage.add_child(stage_model)
	stage_camera = Camera3D.new()
	stage_camera.fov = 26.0
	stage.add_child(stage_camera)


func _mesh(path: String) -> Mesh:
	if path.is_empty():
		return null
	if meshes.has(path):
		return meshes[path]
	var game := "game://" + path.replace("\\", "/").to_lower()
	var loaded: Mesh = load(game) as Mesh if ResourceLoader.exists(game) else null
	meshes[path] = loaded
	return loaded


## Puts the chosen item on the stage, framed to fill the picture whatever its size, held as the item
## page holds it: its front first, then turning the way that item turns.
func _stage_item(item: Dictionary) -> void:
	if str(item.definition) == staged:
		return
	staged = str(item.definition)
	for old in stage_model.get_children():
		old.free()
	var paths: Array = Array(item.pieces) if not item.pieces.is_empty() else [str(item.model)]
	var bounds := AABB()
	for path: String in paths:
		var mesh := _mesh(path)
		if mesh == null:
			continue
		var piece := MeshInstance3D.new()
		stage_model.add_child(piece)
		host.dress_model(piece, mesh)
		bounds = mesh.get_aabb() if stage_model.get_child_count() == 1 else bounds.merge(mesh.get_aabb())
	if stage_model.get_child_count() == 0:
		return
	var extent := maxf(bounds.get_longest_axis_size(), 0.01)
	stage_item = item
	stage_centre = bounds.get_center()
	stage_turn = 0.0
	_pose_stage()
	stage_camera.position = Vector3(0, extent * 0.12, extent * 2.5)
	stage_camera.look_at(Vector3.ZERO)
	stage_camera.near = extent * 0.05
	stage_camera.far = extent * 8.0


func _pose_stage() -> void:
	stage_model.basis = host.item_pose(stage_item, stage_turn)
	stage_model.position = stage_model.basis * -stage_centre


func _claim() -> void:
	host.claim_input(1, "satchel.open", true)


func _exit_tree() -> void:
	if open or opening:
		host.own_screen(false)
	host.claim_input(1, "satchel.open", false)


func _on_input_action(e: Dictionary) -> void:
	if not e.get("pressed", false) or int(e.get("player", 1)) != 1 or e.get("name", "") != "satchel.open":
		return
	if not open and not opening and host.in_play(1) and not host.in_cutscene(1):
		opening = true
		opening_for = 0.0
		host.own_screen(true)
		host.press(1, "Start", true)


func _on_paused(e: Dictionary) -> void:
	if int(e.get("mode", 1)) != 1:
		return
	bar_held = false
	if e.get("paused", false):
		if opening:
			opening = false
			host.press(1, "Start", false)
			_show()
	else:
		var ended := open or opening or closing
		if ended:
			host.press(1, "Start", false)
			host.own_screen(false)
		open = false
		opening = false
		closing = false
		_search(false)
		canvas.visible = false
		stage.render_target_update_mode = SubViewport.UPDATE_DISABLED


func _close() -> void:
	if open and not closing:
		closing = true
		host.press(1, "Start", true)


func _show() -> void:
	open = true
	on_touch = host.touch_mode()
	canvas.visible = true
	stage.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	_search(false)
	order_held = false
	chosen_melee = ""
	chosen_ranged = ""
	waiting = {}
	notice_for = 0.0
	flash_for = 0.0
	_read()


func _read() -> void:
	items = host.inventory_items()
	for item: Dictionary in items:
		item["favourite"] = host.player_value(1, "satchel.f" + item.definition, 0.0) > 0.5
	var hero: int = host.hero(1)
	if hero != 0:
		host.ask("equipped", [hero], func(r: Dictionary) -> void:
			melee = chosen_melee if not chosen_melee.is_empty() else _def(r.get("melee", ""))
			ranged = chosen_ranged if not chosen_ranged.is_empty() else _def(r.get("ranged", ""))
			_list(true))
		host.ask("gold", [hero], func(r: Dictionary) -> void:
			gold = int(r.get("gold", -1))
			canvas.queue_redraw())
	_list(true)


## A definition as inventory_items() spells it, however it arrives.
func _def(value) -> String:
	var text := str(value)
	if text.begins_with("0x") or text.begins_with("0X"):
		return "0x" + text.substr(2).to_upper()
	return text


func _held(item: Dictionary) -> bool:
	return item.get("worn", false) or item.definition == melee or item.definition == ranged


func _in_category(item: Dictionary, chosen: Dictionary) -> bool:
	if chosen.get("favourites", false) and not item.favourite:
		return false
	if chosen.has("weapon") and int(item.weapon_class) != int(chosen.weapon):
		return false
	return not chosen.has("ids") or chosen.ids.has(int(item.category))


func _belongs(item: Dictionary) -> bool:
	if not _in_category(item, CATEGORIES[category]):
		return false
	return search.is_empty() or str(item.name).to_lower().contains(search.to_lower())


## The list as asked for, its columns chosen from what its items state, sorted, with what is held on top.
## With follow, the cursor stays on its item wherever that item lands. Otherwise the list is being asked
## for anew; until then, what an action changes keeps its row, so the action does not move the list.
func _list(follow := false) -> void:
	_settle()
	if not follow:
		order_held = false
	if not order_held:
		pinned.clear()
		for item: Dictionary in items:
			if _held(item):
				pinned[item.definition] = true
	var chosen: String = _current().get("definition", "") if follow else ""
	shown = items.filter(_belongs)
	_columns()
	shown.sort_custom(_before)
	if not chosen.is_empty():
		for i in shown.size():
			if shown[i].definition == chosen:
				row = i
				break
	row = clampi(row, 0, maxi(0, shown.size() - 1))
	_scroll_to_row()
	canvas.queue_redraw()


func _columns() -> void:
	var counts := {}
	var order := []
	for item: Dictionary in shown:
		for line: Dictionary in item.lines:
			if not counts.has(line.label):
				counts[line.label] = 0
				order.append(line.label)
			counts[line.label] += 1
	order.sort_custom(func(a, b): return counts[a] > counts[b])
	columns = [{ "key": "name", "title": "NAME", "width": 0.0 }]
	var chosen: Dictionary = CATEGORIES[category]
	if not chosen.has("weapon") and chosen.get("ids", [0, 0]).size() > 1 or not chosen.has("ids") and not chosen.has("weapon"):
		columns.append({ "key": "kind", "title": "TYPE", "width": 0.09 })
	for label: String in order.slice(0, 2):
		columns.append({ "key": "line:" + label, "title": _plain_label(label).to_upper(), "width": 0.15 })
	columns.append({ "key": "value", "title": "VALUE", "width": 0.075 })
	if not columns.any(func(c): return c.key == sort_column):
		sort_column = "name"
		sort_down = false


## A stat line's label without the game's colon, for a column's head or the card.
func _plain_label(label: String) -> String:
	return label.strip_edges().trim_suffix(":")


## What is pinned comes first, whatever the sort; then the column asked for.
func _before(a: Dictionary, b: Dictionary) -> bool:
	if pinned.has(a.definition) != pinned.has(b.definition):
		return pinned.has(a.definition)
	var left = _sort_value(a)
	var right = _sort_value(b)
	if left == right:
		return str(a.name).naturalnocasecmp_to(str(b.name)) < 0
	if left is String:
		return (left.naturalnocasecmp_to(right) < 0) != sort_down
	return (left < right) != sort_down


func _sort_value(item: Dictionary):
	var text := _cell(item, sort_column)
	if sort_column == "name" or sort_column == "kind":
		return text
	return _number(text)


func _number(text: String) -> float:
	var digits := ""
	for character in text:
		if character.is_valid_int() or character == "." or (character == "-" and digits.is_empty()):
			digits += character
		elif not digits.is_empty():
			break
	return digits.to_float()


func _cell(item: Dictionary, key: String) -> String:
	match key:
		"name":
			return str(item.name)
		"kind":
			return KIND_NAMES.get(int(item.category), "")
		"count":
			return str(int(item.count))
		"value":
			return str(int(item.value)) if int(item.value) > 0 else ""
	if key.begins_with("line:"):
		var label := key.substr(5)
		for line: Dictionary in item.lines:
			if line.label == label:
				return str(line.value)
	return ""


func _scroll_to_row() -> void:
	var fit := _rows_fit()
	if row < scroll:
		scroll = row
	elif row >= scroll + fit:
		scroll = row - fit + 1
	scroll = clampi(scroll, 0, maxi(0, shown.size() - fit))


func _rows_fit() -> int:
	return maxi(1, int((bar_y - rows_y - 0.01) / row_h))


func _current() -> Dictionary:
	return shown[row] if row >= 0 and row < shown.size() else {}


# ---------------------------------------------------------------- what a press does


func _input(event: InputEvent) -> void:
	if not open or closing:
		return
	# The layout follows the hand: a finger (the touch pointer) or the mouse, a controller's button. The
	# phone's own keyboard is a finger's too, so a key never changes it.
	if event is InputEventMouseButton and event.pressed:
		on_touch = event.device == InputEvent.DEVICE_ID_EMULATION
	elif event is InputEventJoypadButton and event.pressed:
		on_touch = false
	if event is InputEventKey and event.pressed:
		_key(event)
		get_viewport().set_input_as_handled()
	elif event is InputEventMouseButton and event.pressed:
		_click(event)
		get_viewport().set_input_as_handled()
	elif event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		bar_held = false
	elif event is InputEventMouseMotion and bar_held:
		_drag_bar(event.position.y)
		get_viewport().set_input_as_handled()
	elif event is InputEventJoypadButton and event.pressed:
		_pad(event.button_index)
		get_viewport().set_input_as_handled()


func _key(event: InputEventKey) -> void:
	if searching:
		match event.keycode:
			KEY_ENTER, KEY_KP_ENTER:
				_search(false)
			KEY_BACKSPACE:
				search = search.substr(0, search.length() - 1)
				row = 0
				_list()
			KEY_ESCAPE:
				_search(false)
			KEY_TAB:
				_close()
			_:
				if event.unicode >= 32:
					search += String.chr(event.unicode)
					row = 0
					_list()
		canvas.queue_redraw()
		return
	match event.keycode:
		KEY_TAB, KEY_ESCAPE:
			_close()
		KEY_UP, KEY_W:
			_move(-1)
		KEY_DOWN, KEY_S:
			_move(1)
		KEY_PAGEUP:
			_move(-_rows_fit())
		KEY_PAGEDOWN:
			_move(_rows_fit())
		KEY_LEFT, KEY_A:
			_step_category(-1)
		KEY_RIGHT, KEY_D:
			_step_category(1)
		KEY_E, KEY_ENTER, KEY_KP_ENTER, KEY_SPACE:
			_act()
		KEY_F:
			_favourite()
		KEY_R:
			if CATEGORIES[category].has("undress"):
				_remove_all()
		KEY_C:
			_step_sort()
		KEY_SLASH:
			_search(true)
			canvas.queue_redraw()


func _pad(button: int) -> void:
	match button:
		JOY_BUTTON_DPAD_UP:
			_move(-1)
		JOY_BUTTON_DPAD_DOWN:
			_move(1)
		JOY_BUTTON_DPAD_LEFT:
			_step_category(-1)
		JOY_BUTTON_DPAD_RIGHT:
			_step_category(1)
		JOY_BUTTON_LEFT_SHOULDER:
			_step_category(-1)
		JOY_BUTTON_RIGHT_SHOULDER:
			_step_category(1)
		JOY_BUTTON_A:
			_act()
		JOY_BUTTON_B:
			_close()
		JOY_BUTTON_X:
			_step_sort()
		JOY_BUTTON_Y:
			_favourite()
		JOY_BUTTON_BACK:
			if CATEGORIES[category].has("undress"):
				_remove_all()


func _click(event: InputEventMouseButton) -> void:
	match event.button_index:
		MOUSE_BUTTON_WHEEL_UP:
			_move(-1)
			return
		MOUSE_BUTTON_WHEEL_DOWN:
			_move(1)
			return
		MOUSE_BUTTON_RIGHT:
			_close()
			return
		MOUSE_BUTTON_LEFT:
			pass
		_:
			return
	for hit: Dictionary in hits:
		if not hit.rect.has_point(event.position):
			continue
		match hit.kind:
			"category":
				category = hit.value
				row = 0
				scroll = 0
				_list()
			"sort":
				_sort_by(hit.value)
			"scrollbar":
				bar_held = true
				_drag_bar(event.position.y)
			"remove_all":
				_remove_all()
			"act":
				_act()
			"favourite":
				_favourite()
			"sort":
				_step_sort()
			"close":
				_close()
			"search":
				_search(true)
				canvas.queue_redraw()
			"row":
				var now := Time.get_ticks_msec() * 0.001
				if hit.value == last_row and now - last_click < 0.35:
					_act()
				row = hit.value
				last_row = hit.value
				last_click = now
				canvas.queue_redraw()
		return
	_search(false)


## Filtering by name on and off. On a touchscreen the phone's own keyboard comes up for it, its keys
## arriving as the keyboard's do; a desktop has no on-screen keyboard, and nothing changes there.
func _search(on: bool) -> void:
	if on == searching:
		return
	searching = on
	if not DisplayServer.has_feature(DisplayServer.FEATURE_VIRTUAL_KEYBOARD):
		return
	if on and on_touch:
		DisplayServer.virtual_keyboard_show(search)
	elif not on:
		DisplayServer.virtual_keyboard_hide()


## The list scrolled so the bar's thumb sits under the pointer, the cursor kept in view.
func _drag_bar(y: float) -> void:
	var fit := _rows_fit()
	var travel := maxf(1.0, bar_h - thumb_h)
	var most := maxi(0, shown.size() - fit)
	scroll = clampi(roundi((y - bar_top - thumb_h * 0.5) / travel * most), 0, most)
	row = clampi(row, scroll, mini(scroll + fit, shown.size()) - 1)
	canvas.queue_redraw()


func _move(by: int) -> void:
	if shown.is_empty():
		return
	row = clampi(row + by, 0, shown.size() - 1)
	_scroll_to_row()
	canvas.queue_redraw()


func _step_category(by: int) -> void:
	category = (category + by + CATEGORIES.size()) % CATEGORIES.size()
	row = 0
	scroll = 0
	_list()


func _sort_by(key: String) -> void:
	sort_down = not sort_down if sort_column == key else key != "name" and key != "kind"
	sort_column = key
	_list()


func _step_sort() -> void:
	var at := 0
	for i in columns.size():
		if columns[i].key == sort_column:
			at = i
	_sort_by(columns[(at + 1) % columns.size()].key)


func _favourite() -> void:
	var item := _current()
	if item.is_empty():
		return
	item.favourite = not item.favourite
	host.player_save(1, "satchel.f" + item.definition, 1.0 if item.favourite else 0.0)
	_list(true)


## What the item's own page would do on A.
func _act() -> void:
	var item := _current()
	if item.is_empty():
		return
	order_held = true
	var id := int(item.category)
	var label := str(item.name)
	if int(item.weapon_class) != 0:
		if _held(item):
			return
		host.inventory_action("equip", item.definition)
		if int(item.weapon_class) == 1:
			chosen_melee = item.definition
			melee = chosen_melee
		else:
			chosen_ranged = item.definition
			ranged = chosen_ranged
		_tell("Equipped " + label, item.definition)
		_list(true)
	elif SLOT_IDS.has(id):
		var taking_off: bool = item.get("worn", false)
		if taking_off and id == 25:
			# An outfit is not a slot: what comes off is each worn item that is one of its pieces.
			for piece: Dictionary in items:
				if piece.get("worn", false) and int(piece.category) != 25 and item.pieces.has(str(piece.model)):
					host.inventory_action("remove", piece.definition)
		else:
			host.inventory_action("remove" if taking_off else ("outfit" if id == 25 else "wear"), item.definition)
		waiting = { "definition": item.definition, "on": not taking_off,
			"done": ("Took off " if taking_off else "Now wearing ") + label,
			"refused": ("Could not take off " if taking_off else "Could not put on ") + label }
		waiting_for = 0.0
	elif item.get("usable", false):
		host.inventory_action("use", item.definition)
		_tell("Used " + label, item.definition)
	else:
		return
	refresh_in = REFRESH.duplicate()


## What just happened: the sentence in the bar, and the row it happened to lit for a moment.
func _tell(text: String, definition := "", refused := false) -> void:
	notice = text
	notice_for = NOTICE_TIME
	notice_refused = refused
	flash = definition
	flash_for = FLASH_TIME if not definition.is_empty() else 0.0
	canvas.queue_redraw()


## Empties every clothing slot with something in it, each the way the game's own "None" row does.
func _remove_all() -> void:
	var slots := _worn_slots()
	if slots.is_empty():
		return
	order_held = true
	for slot: int in slots:
		host.inventory_action("remove", slots[slot])
	waiting = { "all_off": true, "done": "Removed all clothing", "refused": "Could not remove all clothing" }
	waiting_for = 0.0
	refresh_in = REFRESH.duplicate()


## A worn item's definition for each clothing slot that has one, by category.
func _worn_slots() -> Dictionary:
	var slots := {}
	for item: Dictionary in items:
		var id := int(item.category)
		if item.get("worn", false) and id != 25 and SLOT_IDS.has(id):
			slots[id] = item.definition
	return slots


## Whether the garment asked for now shows as asked.
func _settle() -> void:
	if waiting.is_empty():
		return
	if waiting.has("all_off"):
		if _worn_slots().is_empty():
			_tell(waiting.done)
			waiting = {}
		return
	for item: Dictionary in items:
		if item.definition == waiting.definition and item.get("worn", false) == waiting.on:
			_tell(waiting.done, waiting.definition)
			waiting = {}
			return


func _process(delta: float) -> void:
	if opening:
		opening_for += delta
		if opening_for > 2.0:           # the game would not pause here
			opening = false
			host.press(1, "Start", false)
			host.own_screen(false)
	if not open or closing:
		return
	if not waiting.is_empty():
		waiting_for += delta
		if waiting_for > SETTLE:
			_tell(waiting.refused, "", true)
			waiting = {}
	if notice_for > 0.0 or flash_for > 0.0:
		notice_for -= delta
		flash_for -= delta
		canvas.queue_redraw()
	for i in range(refresh_in.size() - 1, -1, -1):
		refresh_in[i] -= delta
		if refresh_in[i] <= 0.0:
			refresh_in.remove_at(i)
			_read()
	if not stage_item.is_empty():
		stage_turn += delta * MODEL_TURN
		_pose_stage()
	if icon_budget == 0:
		icon_budget = 4
	# Icons load as they come into view, a few a frame.
	if shown.any(func(item): return not icons.has(str(item.icon))):
		canvas.queue_redraw()


# ---------------------------------------------------------------- the screen


func _icon(path: String) -> Texture2D:
	if path.is_empty():
		return null
	if icons.has(path):
		return icons[path]
	if icon_budget <= 0:
		return null
	icon_budget -= 1
	var game := "game://" + path.replace("\\", "/").to_lower()
	var loaded: Texture2D = load(game) if ResourceLoader.exists(game) else null
	icons[path] = loaded
	return loaded


func _text(font: Font, at: Vector2, text: String, size: float, colour: Color, width := -1.0, align := HORIZONTAL_ALIGNMENT_LEFT) -> void:
	canvas.draw_string(font, at, text, align, width, int(size), colour, TextServer.JUSTIFICATION_NONE | TextServer.JUSTIFICATION_CONSTRAIN_ELLIPSIS)


## The size this text is drawn at to fit a width: its own, or as much smaller as it takes.
func _fitted(text: String, size: float, width: float) -> float:
	var used := plain.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, int(size)).x
	return size if used <= width else floorf(size * width / used)


func _shade(rect: Rect2, left: float, right: float) -> void:
	var colours := PackedColorArray([Color(SHADE, left), Color(SHADE, right), Color(SHADE, right), Color(SHADE, left)])
	var points := PackedVector2Array([rect.position, rect.position + Vector2(rect.size.x, 0), rect.end, rect.position + Vector2(0, rect.size.y)])
	canvas.draw_polygon(points, colours)


func _rule(from: Vector2, to: Vector2, colour := LINE) -> void:
	canvas.draw_line(from, to, colour, maxf(1.0, tu * 0.0012), true)


## A rounded box as the pause book's rows are: copper edge, dark inside.
func _pill(rect: Rect2, inside: Color, edge: Color, width := 0.0018, radius := 0.5) -> void:
	var box := StyleBoxFlat.new()
	box.bg_color = inside
	box.border_color = edge
	box.set_border_width_all(maxi(1, int(tu * width)))
	box.set_corner_radius_all(int(rect.size.y * radius))
	box.anti_aliasing = true
	canvas.draw_style_box(box, rect)


## A gold curl each way from a centre: the book's ruled heading.
func _flourish(centre: Vector2, half: float, colour: Color) -> void:
	var thin := maxf(1.0, tu * 0.0013)
	canvas.draw_line(centre + Vector2(-half, 0), centre + Vector2(half, 0), Color(colour, 0.65), thin, true)
	for side in [-1.0, 1.0]:
		var points := PackedVector2Array()
		for i in 14:
			var t := i / 13.0
			points.append(centre + Vector2(side * unit * (0.006 + 0.026 * t), -tu * 0.008 * sin(t * PI * 1.5)))
		canvas.draw_polyline(points, colour, thin, true)
	var size := tu * 0.005
	canvas.draw_colored_polygon(PackedVector2Array([centre + Vector2(0, -size), centre + Vector2(size, 0), centre + Vector2(0, size), centre + Vector2(-size, 0)]), colour)


func _draw_screen() -> void:
	var view := canvas.get_viewport_rect().size
	unit = view.y
	_layout(on_touch)
	hits.clear()
	icon_budget = 4
	_shade(Rect2(0, 0, view.x, view.y), 0.2, 0.2)
	var left := PANEL_X * unit
	var width := panel_w * unit
	_pill(Rect2(left, 0.012 * tu, width, (bar_y - 0.024) * unit), LEATHER, COPPER, 0.0026, 0.025)
	_pill(Rect2(left + 0.006 * tu, 0.018 * tu, width - 0.012 * tu, (bar_y - 0.036) * unit), Color(0, 0, 0, 0), Color(COPPER, 0.35), 0.0012, 0.022)
	var inner := left + 0.022 * tu
	var inner_w := width - 0.044 * tu
	_text(face, Vector2(inner, title_y * unit), str(CATEGORIES[category].name), 0.043 * tu, GOLD)
	_draw_search(inner, inner_w)
	_draw_icons(inner, inner_w)
	_flourish(Vector2(inner + inner_w * 0.5, rule_y * unit), inner_w * 0.5, COPPER_LIT)
	_draw_list(inner, inner_w)
	_draw_card(view)
	_draw_bar(view)


## The desktop's layout or the touchscreen's.
func _layout(touch: bool) -> void:
	var l: Dictionary = TOUCH if touch else DESK
	tu = unit * (TOUCH_SCALE if touch else 1.0)
	panel_w = l.panel_w
	title_y = l.title_y
	icons_y = l.icons_y
	labels_y = l.labels_y
	icon_step = l.icon_step
	rule_y = l.rule_y
	head_y = l.head_y
	rows_y = l.rows_y
	row_h = l.row_h
	bar_y = l.bar_y
	card_w = l.card_w


func _draw_search(x: float, width: float) -> void:
	var size := 0.019 * tu
	var box := Rect2(x + width - 0.27 * tu, 0.033 * tu, 0.27 * tu, 0.034 * tu)
	hits.append({ "rect": box, "kind": "search", "value": 0 })
	_pill(box, ROW_INSIDE, COPPER_LIT if searching else COPPER)
	var y := box.position.y + box.size.y * 0.72
	var text := search + ("_" if searching else "")
	if text.is_empty():
		_text(plain, Vector2(box.position.x, y), "Filter", size, DIM, box.size.x - 0.018 * tu, HORIZONTAL_ALIGNMENT_RIGHT)
	else:
		_text(plain, Vector2(box.position.x + 0.016 * tu, y), text, size, CREAM, box.size.x - 0.03 * tu)


func _draw_icons(x: float, width: float) -> void:
	var step := minf(icon_step * unit, width / CATEGORIES.size())
	var y := icons_y * unit
	for i in CATEGORIES.size():
		var centre := Vector2(x + step * (i + 0.5), y)
		var lit := i == category
		var any: bool = items.any(func(item): return _in_category(item, CATEGORIES[i]))
		var colour := GOLD if lit else (Color(CREAM, 0.72) if any else Color(CREAM, 0.26))
		if lit:
			canvas.draw_circle(centre, 0.0245 * tu, Color(COPPER_LIT, 0.16), true, -1.0, true)
			canvas.draw_arc(centre, 0.0245 * tu, 0.0, TAU, 40, COPPER_LIT, maxf(1.0, tu * 0.0018), true)
		_glyph(CATEGORIES[i].icon, centre, 0.0145 * tu, colour)
		var label: String = CATEGORIES[i].get("label", CATEGORIES[i].name)
		_text(plain, Vector2(centre.x - step * 0.5, labels_y * unit), label, _fitted(label, 0.0118 * tu, step * 0.96) if tu != unit else 0.0118 * unit, colour, step, HORIZONTAL_ALIGNMENT_CENTER)
		hits.append({ "rect": Rect2(centre.x - step * 0.5, y - 0.025 * tu, step, 0.07 * tu), "kind": "category", "value": i })


## One category's icon, in plain white shapes around a centre; r is about its half height.
func _glyph(kind: String, c: Vector2, r: float, colour: Color) -> void:
	var line := maxf(1.0, r * 0.14)
	var poly := func(points: Array) -> void:
		var at := PackedVector2Array()
		for p: Vector2 in points:
			at.append(c + p * r)
		canvas.draw_colored_polygon(at, colour)
	var stroke := func(points: Array, width := 1.0) -> void:
		var at := PackedVector2Array()
		for p: Vector2 in points:
			at.append(c + p * r)
		canvas.draw_polyline(at, colour, line * width, true)
	match kind:
		"star":
			var points := []
			for i in 10:
				var angle := -PI * 0.5 + TAU * i / 10.0
				points.append(Vector2.from_angle(angle) * (1.0 if i % 2 == 0 else 0.42))
			poly.call(points)
		"bag":
			poly.call([Vector2(-0.8, -0.35), Vector2(0.8, -0.35), Vector2(0.95, 0.95), Vector2(-0.95, 0.95)])
			stroke.call([Vector2(-0.4, -0.35), Vector2(-0.4, -0.75), Vector2(0.4, -0.75), Vector2(0.4, -0.35)], 1.5)
		"sword":
			poly.call([Vector2(0.95, -0.95), Vector2(0.72, -0.35), Vector2(-0.35, 0.72), Vector2(-0.72, 0.35), Vector2(0.35, -0.72)])
			stroke.call([Vector2(-0.8, 0.1), Vector2(-0.1, 0.8)], 1.8)
			stroke.call([Vector2(-0.5, 0.5), Vector2(-0.95, 0.95)], 2.0)
		"gun":
			poly.call([Vector2(-1.0, -0.5), Vector2(0.95, -0.5), Vector2(0.95, -0.12), Vector2(0.1, -0.12), Vector2(-0.05, 0.1), Vector2(-0.3, 0.1), Vector2(-0.5, 0.85), Vector2(-0.95, 0.75), Vector2(-0.65, -0.12), Vector2(-1.0, -0.12)])
		"shirt":
			poly.call([Vector2(-0.3, -0.85), Vector2(0.3, -0.85), Vector2(1.0, -0.4), Vector2(0.72, 0.05), Vector2(0.5, -0.1), Vector2(0.5, 0.9), Vector2(-0.5, 0.9), Vector2(-0.5, -0.1), Vector2(-0.72, 0.05), Vector2(-1.0, -0.4)])
		"flask":
			poly.call([Vector2(-0.22, -0.55), Vector2(0.22, -0.55), Vector2(0.22, -0.1), Vector2(0.75, 0.75), Vector2(0.6, 0.95), Vector2(-0.6, 0.95), Vector2(-0.75, 0.75), Vector2(-0.22, -0.1)])
			stroke.call([Vector2(-0.35, -0.8), Vector2(0.35, -0.8)], 1.8)
		"apple":
			canvas.draw_circle(c + Vector2(-0.3, 0.2) * r, r * 0.6, colour, true, -1.0, true)
			canvas.draw_circle(c + Vector2(0.3, 0.2) * r, r * 0.6, colour, true, -1.0, true)
			stroke.call([Vector2(0.0, -0.3), Vector2(0.15, -0.9)], 1.6)
		"gift":
			poly.call([Vector2(-0.85, -0.3), Vector2(-0.1, -0.3), Vector2(-0.1, 0.9), Vector2(-0.75, 0.9)])
			poly.call([Vector2(0.1, -0.3), Vector2(0.85, -0.3), Vector2(0.75, 0.9), Vector2(0.1, 0.9)])
			stroke.call([Vector2(0.0, -0.45), Vector2(-0.55, -0.9), Vector2(-0.55, -0.45), Vector2(0.55, -0.45), Vector2(0.55, -0.9), Vector2(0.0, -0.45)], 1.2)
		"book":
			poly.call([Vector2(-1.0, -0.7), Vector2(-0.08, -0.45), Vector2(-0.08, 0.9), Vector2(-1.0, 0.65)])
			poly.call([Vector2(1.0, -0.7), Vector2(0.08, -0.45), Vector2(0.08, 0.9), Vector2(1.0, 0.65)])
		"drop":
			poly.call([Vector2(0.0, -1.0), Vector2(0.5, -0.2), Vector2(0.72, 0.3), Vector2(0.5, 0.75), Vector2(0.0, 0.95), Vector2(-0.5, 0.75), Vector2(-0.72, 0.3), Vector2(-0.5, -0.2)])
		"gem":
			poly.call([Vector2(-0.55, -0.7), Vector2(0.55, -0.7), Vector2(1.0, -0.15), Vector2(0.0, 0.95), Vector2(-1.0, -0.15)])
		"cup":
			poly.call([Vector2(-0.6, -0.9), Vector2(0.6, -0.9), Vector2(0.45, 0.05), Vector2(0.14, 0.3), Vector2(0.14, 0.62), Vector2(0.5, 0.92), Vector2(-0.5, 0.92), Vector2(-0.14, 0.62), Vector2(-0.14, 0.3), Vector2(-0.45, 0.05)])
			stroke.call([Vector2(-0.6, -0.7), Vector2(-0.95, -0.65), Vector2(-0.85, -0.2), Vector2(-0.5, -0.05)])
			stroke.call([Vector2(0.6, -0.7), Vector2(0.95, -0.65), Vector2(0.85, -0.2), Vector2(0.5, -0.05)])
		"chair":
			stroke.call([Vector2(-0.5, -0.95), Vector2(-0.5, 0.95)], 1.8)
			stroke.call([Vector2(-0.5, 0.05), Vector2(0.6, 0.05), Vector2(0.6, 0.95)], 1.8)
		"key":
			canvas.draw_arc(c + Vector2(-0.45, -0.45) * r, r * 0.42, 0.0, TAU, 24, colour, line * 1.5, true)
			stroke.call([Vector2(-0.15, -0.15), Vector2(0.9, 0.9)], 1.6)
			stroke.call([Vector2(0.45, 0.45), Vector2(0.75, 0.15)], 1.6)
			stroke.call([Vector2(0.7, 0.7), Vector2(0.98, 0.42)], 1.6)


func _draw_list(x: float, width: float) -> void:
	var name_width := width - 0.012 * tu
	for column: Dictionary in columns:
		name_width -= column.width * tu
	var size := 0.0195 * tu
	# The heads, each a sort.
	var at := x
	var y := rows_y * unit
	for column: Dictionary in columns:
		var w: float = name_width if column.key == "name" else column.width * tu
		var lit: bool = column.key == sort_column
		var align := HORIZONTAL_ALIGNMENT_LEFT if column.key == "name" or column.key == "kind" else HORIZONTAL_ALIGNMENT_RIGHT
		var title: String = column.title + (" \u25BC" if lit and sort_down else (" \u25B2" if lit else ""))
		var head_x: float = at + (0.036 * tu if column.key == "name" else 0.0)
		var head_w: float = w - 0.004 * tu - (head_x - at)
		_text(plain, Vector2(head_x, head_y * unit), title, _fitted(title, size * 0.86, head_w), GOLD if lit else DIM, head_w, align)
		hits.append({ "rect": Rect2(at, head_y * unit - size, w, size * 1.3), "kind": "sort", "value": column.key })
		at += w
	if shown.is_empty():
		_text(plain, Vector2(x + 0.036 * tu, y + 0.03 * tu), "Nothing here." if search.is_empty() else "Nothing by that name.", size, DIM)
		return
	var end := mini(shown.size(), scroll + _rows_fit())
	for i in range(scroll, end):
		var item: Dictionary = shown[i]
		var rect := Rect2(x - 0.004 * tu, y, width - 0.002 * tu, row_h * unit)
		hits.append({ "rect": rect, "kind": "row", "value": i })
		if i == row:
			_pill(rect, ROW_INSIDE, COPPER_LIT)
		if flash_for > 0.0 and item.definition == flash:
			_pill(rect, Color(COPPER_LIT, 0.55 * flash_for / FLASH_TIME), Color(0, 0, 0, 0), 0.0)
		_draw_row(item, x, y, name_width, size)
		y += row_h * unit
	if shown.size() > _rows_fit():
		bar_top = rows_y * unit
		bar_h = _rows_fit() * row_h * unit
		thumb_h = maxf(0.02 * tu, bar_h * _rows_fit() / shown.size())
		var thumb_y: float = bar_top + (bar_h - thumb_h) * scroll / maxi(1, shown.size() - _rows_fit())
		var bar_x := x + width + 0.003 * tu
		canvas.draw_rect(Rect2(bar_x, bar_top, 0.0035 * tu, bar_h), Color(COPPER, 0.3))
		canvas.draw_rect(Rect2(bar_x, thumb_y, 0.0035 * tu, thumb_h), COPPER_LIT)
		hits.append({ "rect": Rect2(bar_x - 0.006 * tu, bar_top, 0.0155 * tu, bar_h), "kind": "scrollbar", "value": 0 })


func _draw_row(item: Dictionary, x: float, y: float, name_width: float, size: float) -> void:
	var text_y := y + row_h * unit * 0.72
	var icon_size := row_h * unit * 0.86
	var icon := _icon(str(item.icon))
	if icon != null:
		canvas.draw_texture_rect(icon, Rect2(x, y + row_h * unit * 0.07, icon_size, icon_size), false)
	var after := x + 0.04 * tu
	var label := str(item.name)
	if int(item.count) > 1:
		label += " (%d)" % int(item.count)
	var colour := GOLD if _held(item) else CREAM
	var marks := ""
	if _held(item):
		marks += "  " + WORN
	if item.favourite:
		marks += "  " + STAR
	var label_w := x + name_width - after - 0.03 * tu
	var label_size := _fitted(label, size, label_w)
	label_w = minf(plain.get_string_size(label, HORIZONTAL_ALIGNMENT_LEFT, -1, int(label_size)).x, label_w)
	_text(plain, Vector2(after, text_y), label, label_size, colour, label_w + 1.0)
	if not marks.is_empty():
		_text(plain, Vector2(after + label_w, text_y), marks, size * 0.72, GOLD)
	var at := x + name_width
	for column: Dictionary in columns:
		if column.key == "name":
			continue
		var w: float = column.width * tu
		var align := HORIZONTAL_ALIGNMENT_LEFT if column.key == "kind" else HORIZONTAL_ALIGNMENT_RIGHT
		var cell_font: Font = plain if column.key == "kind" or not _cell(item, column.key).is_valid_float() else numerals
		var cell_size: float = size * (0.92 if cell_font == plain else 1.3)
		var cell_y: float = text_y + (size * 0.42 if cell_font == numerals else 0.0)
		_text(cell_font, Vector2(at, cell_y), _cell(item, column.key), cell_size, DIM if column.key == "kind" else CREAM, w - 0.004 * tu, align)
		at += w


## The item to compare with: the weapon in that hand, or what is worn in that slot.
func _compared(item: Dictionary) -> Dictionary:
	if _held(item):
		return {}
	var id := int(item.category)
	for other: Dictionary in items:
		if other == item:
			continue
		if int(item.weapon_class) != 0:
			if int(other.weapon_class) == int(item.weapon_class) and _held(other):
				return other
		elif SLOT_IDS.has(id) and int(other.category) == id and other.get("worn", false):
			return other
	return {}


## The card over the world: the item's model, or its icon large, and a framed box of its name and stats.
func _draw_card(view: Vector2) -> void:
	var item := _current()
	if item.is_empty():
		return
	var free_left := (PANEL_X + panel_w) * unit
	var width := card_w * unit
	var x := free_left + (view.x - free_left - width) * 0.5
	var lines: Array = item.lines
	var other := _compared(item)
	var description := str(item.description)
	var size := 0.0215 * tu
	var step := 0.031 * tu
	var held := _held(item)
	var count := lines.size() + (1 if int(item.value) > 0 else 0) + (1 if not other.is_empty() else 0) + (1 if held else 0)
	var text_h := plain.get_multiline_string_size(description, HORIZONTAL_ALIGNMENT_LEFT, width - 0.05 * tu, int(size * 0.88)).y if not description.is_empty() else 0.0
	var box_h := 0.08 * tu + count * step + text_h + 0.03 * tu
	var box := Rect2(x, bar_y * unit - box_h - 0.03 * tu, width, box_h)
	_stage_item(item)
	var picture := minf(MODEL_SIZE * tu, box.position.y - 0.02 * tu)
	var at := Rect2(box.position.x + (width - picture) * 0.5, box.position.y - picture - 0.008 * tu, picture, picture)
	if stage_model.get_child_count() != 0:
		canvas.draw_texture_rect(stage.get_texture(), at, false)
	else:
		var icon := _icon(str(item.icon))
		if icon != null:
			var icon_size := minf(0.2 * tu, picture)
			canvas.draw_texture_rect(icon, Rect2(box.position.x + (width - icon_size) * 0.5, box.position.y - icon_size - 0.015 * tu, icon_size, icon_size), false)
	_frame(box)
	var inner := x + 0.025 * tu
	var inner_w := width - 0.05 * tu
	var y := box.position.y + 0.045 * tu
	_text(face, Vector2(inner, y), str(item.name), 0.033 * tu, INK, inner_w, HORIZONTAL_ALIGNMENT_CENTER)
	y += 0.016 * tu
	_flourish(Vector2(inner + inner_w * 0.5, y), inner_w * 0.42, INK_DIM)
	y += 0.03 * tu
	if held:
		_text(face, Vector2(inner, y), "Equipped" if int(item.weapon_class) != 0 else "Wearing", size, BETTER, inner_w, HORIZONTAL_ALIGNMENT_CENTER)
		y += step
	for line: Dictionary in lines:
		_text(plain, Vector2(inner, y), _plain_label(str(line.label)), size * 0.95, INK_DIM, inner_w * 0.62)
		var value := str(line.value)
		_text(plain, Vector2(inner, y), value, size, INK, inner_w, HORIZONTAL_ALIGNMENT_RIGHT)
		_compare_mark(other, line, value, inner, y, inner_w, size)
		y += step
	if int(item.value) > 0:
		_text(plain, Vector2(inner, y), "Value", size * 0.95, INK_DIM)
		_text(plain, Vector2(inner, y), str(int(item.value)), size, INK, inner_w, HORIZONTAL_ALIGNMENT_RIGHT)
		y += step
	if not other.is_empty():
		_text(plain, Vector2(inner, y), "Compared with " + str(other.name), size * 0.85, INK_DIM, inner_w)
		y += step
	if not description.is_empty():
		canvas.draw_multiline_string(plain, Vector2(inner, y), description, HORIZONTAL_ALIGNMENT_LEFT, inner_w, int(size * 0.88), -1, INK)


## The parchment page, shaded towards its edges, in a copper frame.
func _frame(box: Rect2) -> void:
	_pill(box, PARCHMENT, COPPER, 0.0028, 0.03)
	var edge := 0.03 * tu
	var clear := Color(PARCHMENT_DARK, 0.0)
	for side in [[box.position, box.position + Vector2(box.size.x, 0), Vector2(0, edge)], [box.position + Vector2(0, box.size.y), box.end, Vector2(0, -edge)]]:
		canvas.draw_polygon(PackedVector2Array([side[0], side[1], side[1] + side[2], side[0] + side[2]]),
			PackedColorArray([PARCHMENT_DARK, PARCHMENT_DARK, clear, clear]))


func _compare_mark(other: Dictionary, line: Dictionary, value: String, x: float, y: float, width: float, size: float) -> void:
	if other.is_empty():
		return
	for theirs: Dictionary in other.lines:
		if theirs.label != line.label:
			continue
		var change := _number(value) - _number(str(theirs.value))
		if absf(change) < 0.001:
			return
		var mark := ("+" if change > 0.0 else "") + String.num(change, 2)
		var used := plain.get_string_size(value, HORIZONTAL_ALIGNMENT_LEFT, -1, int(size)).x
		_text(plain, Vector2(x, y), mark, size * 0.85, BETTER if change > 0.0 else WORSE, width - used - 0.012 * tu, HORIZONTAL_ALIGNMENT_RIGHT)
		return


func _action_name(item: Dictionary) -> String:
	if item.is_empty():
		return ""
	var id := int(item.category)
	if int(item.weapon_class) != 0:
		return "" if _held(item) else "Equip"
	if SLOT_IDS.has(id):
		return "Take off" if item.get("worn", false) else "Wear"
	return "Use" if item.get("usable", false) else ""


## The bar under everything, a long copper-edged pill: the keys on the left, the purse on the right.
func _draw_bar(view: Vector2) -> void:
	var height := 0.058 * tu
	var y := (bar_y + (1.0 - bar_y) * 0.5) * unit
	_pill(Rect2(PANEL_X * unit, y - height * 0.5, view.x - PANEL_X * unit * 2.0, height), LEATHER, COPPER, 0.0026)
	var x := PANEL_X * unit + 0.03 * tu
	if on_touch:
		x = maxf(x, TOUCH_LEFT * unit)
	var action := _action_name(_current())
	# On a touchscreen the hints are the buttons, each tapped from the bar to the screen's bottom edge,
	# and Filter, which needs a keyboard, is left out.
	var touch: bool = on_touch
	var hints := []
	if not action.is_empty():
		hints.append(["E", action, "act"])
	hints.append(["F", "Unfavourite" if _current().get("favourite", false) else "Favourite", "favourite"])
	hints.append(["C", "Sort", "sort"])
	if not touch:
		hints.append(["/", "Filter", "search"])
	hints.append(["Tab", "Close", "close"])
	if CATEGORIES[category].has("undress") and not _worn_slots().is_empty():
		hints.append(["R", "Remove All Clothing", "remove_all"])
	var size := 0.024 * tu
	for hint: Array in hints:
		var start := x
		x = _touch_button(x, y, hint[1], size) if touch else _hint(x, y, hint[0], hint[1], size)
		if touch:
			hits.append({ "rect": Rect2(start, y - height * 0.5, x - start, view.y - (y - height * 0.5)), "kind": hint[2], "value": 0 })
		elif hint[0] == "R":
			hits.append({ "rect": Rect2(start, y - height * 0.5, x - start, height), "kind": "remove_all", "value": 0 })
	var right := view.x - PANEL_X * unit - 0.03 * tu
	if notice_for > 0.0:
		var room := right - 0.2 * tu - x
		var colour: Color = REFUSED if notice_refused else GOLD
		colour.a = clampf(notice_for / 0.5, 0.0, 1.0)
		_text(face, Vector2(x + 0.02 * tu, y + size * 0.34), notice, _fitted(notice, size * 1.05, room), colour, room)
	if gold >= 0:
		var text := str(gold)
		var font_size := size * 1.5
		var width := numerals.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, int(font_size)).x
		_text(numerals, Vector2(right - width, y + font_size * 0.42), text, font_size, COIN)
		_coin(Vector2(right - width - 0.022 * tu, y))


## The purse's coin.
func _coin(at: Vector2) -> void:
	canvas.draw_circle(at, 0.012 * tu, COIN, true, -1.0, true)
	canvas.draw_arc(at, 0.012 * tu, 0.0, TAU, 28, COPPER, maxf(1.0, tu * 0.0016), true)
	canvas.draw_arc(at, 0.007 * tu, 0.0, TAU, 20, Color(COPPER, 0.7), maxf(1.0, tu * 0.0012), true)


## On a touchscreen a hint is a button: its words in a gold-rimmed pill.
func _touch_button(x: float, y: float, label: String, size: float) -> float:
	var width := plain.get_string_size(label, HORIZONTAL_ALIGNMENT_LEFT, -1, int(size)).x + 0.04 * tu
	_pill(Rect2(x, y - size * 0.85, width, size * 1.7), ROW_INSIDE, GOLD, 0.0018, 0.5)
	_text(plain, Vector2(x, y + size * 0.34), label, size, CREAM, width, HORIZONTAL_ALIGNMENT_CENTER)
	return x + width + 0.02 * tu


## A key in the gold box the game's own prompts draw, and what it does.
func _hint(x: float, y: float, key: String, label: String, size: float) -> float:
	var key_w := maxf(plain.get_string_size(key, HORIZONTAL_ALIGNMENT_LEFT, -1, int(size * 0.8)).x + 0.014 * tu, size * 1.25)
	var box := Rect2(x, y - size * 0.68, key_w, size * 1.36)
	_pill(box, ROW_INSIDE, GOLD, 0.0018, 0.2)
	_text(plain, Vector2(x, y + size * 0.28), key, size * 0.8, GOLD, key_w, HORIZONTAL_ALIGNMENT_CENTER)
	var after := x + key_w + 0.009 * tu
	_text(plain, Vector2(after, y + size * 0.34), label, size, CREAM)
	return after + plain.get_string_size(label, HORIZONTAL_ALIGNMENT_LEFT, -1, int(size)).x + 0.028 * tu
