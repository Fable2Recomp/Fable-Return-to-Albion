extends Node
## Hotbar: draws the bar and uses the items on it.
##
## Ten slots along the bottom centre, keys 1-0. mod/init.lua publishes what each holds for the
## local player (see its header): the kind, whether it can be used now, whether it is the active
## spell, an item's count and record hash, and the icon path packed three characters to a number.
## The icons are the game's own textures, loaded through the UI's presentation host (the node the
## pause book and HUD load theirs with), so a spell, an expression and an item look as they do in
## the game's own menus.
##
## 1-3 are the game's attack modes (GameInput.KeyboardMouse.cs: melee, ranged, magic); the bar
## follows the last of them pressed to light the mode in force, as the game starts on melee.
##
## An item slot's key uses one through host.use_item: the game's own use, as the Items page does.
##
## A spell slot's key casts through the game's own magic button: when init.lua has lent the spell
## table to the key's spell (hotbar.cast_seq moves, hotbar.casting names the slot), this holds the
## magic key (refii.toml's key_magic, C by default) through Godot's input, which the game reads
## its keyboard from (GameInput.KeyboardMouse.cs), for as long as the slot's key is held: a tap
## casts at once, a hold charges through the power levels as holding the button does. A tap is
## held for a few frames at least, so the game sees the press.
##
## THE LOOK is the game's HUD's own: each slot is the d-pad's glass orb under its pewter ring, cut
## from the HUD atlas (art/gui/persistant/hud.tex, the regions below in pixels of the 2048-square
## enhanced atlas, scaled to the atlas loaded, so an original-size atlas cuts the same), the lit slot glows with the atlas's soft light, the
## bar ends in the filigree of the HUD's eye ornament, and the numbers are set in the Fable II face
## the launcher ships (res://assets/launcher/fable2.ttf).

const SLOTS := 10
const MODE_KEYS := [KEY_1, KEY_2, KEY_3]
const MIN_HOLD := 0.08                  # seconds the magic key is held for a tap

const ATLAS := "art/gui/persistant/hud.tex"
const FONT := "res://assets/launcher/fable2.ttf"

# HUD atlas pieces, in pixels of the 2048-square enhanced atlas (scaled to the atlas loaded).
const RING := Rect2(4, 4, 300, 300)
const ORB := Rect2(12, 336, 260, 256)
const GLOW := Rect2(32, 1316, 448, 440)
const DISC := Rect2(1024, 52, 252, 252)
const LEFT_END := Rect2(16, 652, 228, 268)
const RIGHT_END := Rect2(628, 652, 228, 268)

const GLASS := Color(0.32, 0.31, 0.29, 0.82)      # the orb, dark as the d-pad's
const GLASS_EMPTY := Color(0.24, 0.23, 0.22, 0.55)
const PEWTER := Color(1, 1, 1, 0.95)
const PEWTER_LIT := Color(1.0, 0.86, 0.55)
const LIGHT := Color(1.0, 0.78, 0.38, 0.32)
const CREAM := Color(0.96, 0.91, 0.78)
const SHADOW := Color(0.04, 0.03, 0.02, 0.85)
const BADGE := Color(0.07, 0.06, 0.05, 0.92)

var host: Object
var layer: CanvasLayer
var bar: Control
var presentation: Node
var atlas: Texture2D
var font: Font
var find_wait := 0.0                 # seconds until the presentation host is looked for again
var in_menu := false                # a menu, a screen or the game's pause is up
var loading := false                # between a level going and the next standing
var read_wait := 0.0
var mode := 0                       # 0 melee, 1 ranged, 2 magic: slots 1, 2, 3
var magic_key := KEY_C
var holding := false                # the magic key is down for a hotbar cast
var hold_time := 0.0
var cast_seen := 0
var key_held := {}                  # slot -> its key is down
var paths: Array[String] = []
var textures: Array = []
var state: Array[Dictionary] = []


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	host.input_action.connect(_on_input_action)
	host.menu_opened.connect(func(_e): in_menu = true)
	host.menu_closed.connect(func(_e): in_menu = false)
	host.paused.connect(func(e): in_menu = e.get("paused", false))
	host.level_unloading.connect(func(_e): loading = true)
	host.level_loaded.connect(func(_e): loading = false)
	for i in SLOTS:
		paths.append("")
		textures.append(null)
		state.append({})
	layer = CanvasLayer.new()
	layer.layer = 90
	add_child(layer)
	bar = Control.new()
	bar.mouse_filter = Control.MOUSE_FILTER_IGNORE
	bar.set_anchors_preset(Control.PRESET_FULL_RECT)
	bar.draw.connect(_draw_bar)
	layer.add_child(bar)
	font = load(FONT) as Font
	magic_key = _magic_binding()
	cast_seen = int(_value("hotbar.cast_seq"))
	if font == null:
		font = ThemeDB.fallback_font


func _input(event: InputEvent) -> void:
	var key := event as InputEventKey
	if key != null and key.pressed and not key.echo:
		var at := MODE_KEYS.find(key.physical_keycode)
		if at >= 0:
			mode = at
			bar.queue_redraw()


func _on_input_action(e: Dictionary) -> void:
	var name: String = e.get("name", "")
	if e.get("replica", false) or not name.begins_with("hotbar.slot"):
		return
	var key := int(name.substr(11))
	var s := 10 if key == 0 else key
	key_held[s] = e.get("pressed", false)
	if not e.get("pressed", false):
		return
	var slot: Dictionary = state[s - 1]
	if slot.get("kind", 0) != 3 or slot.get("count", 0) <= 0 or int(host.cutscene_kinds(e.get("player", 1))) & 3 != 0:
		return
	host.use_item("0x%04x%04x" % [slot.hi, slot.lo])


func _process(delta: float) -> void:
	_cast(delta)
	read_wait -= delta
	find_wait -= delta
	if read_wait <= 0.0:
		read_wait = 0.1
		_read()
	bar.visible = _shown()
	bar.queue_redraw()


# The magic key for a hotbar cast: down when init.lua has taken one, up once the slot's key is.
func _cast(delta: float) -> void:
	var seq := int(_value("hotbar.cast_seq"))
	var casting := int(_value("hotbar.casting"))
	if seq != cast_seen:
		cast_seen = seq
		if casting > 0 and not holding and not in_menu and not loading:
			_magic(true)
			hold_time = 0.0
	if holding:
		hold_time += delta
		var up: bool = casting <= 0 or not key_held.get(casting, false)
		if (up and hold_time >= MIN_HOLD) or in_menu or loading:
			_magic(false)


func _magic(down: bool) -> void:
	holding = down
	var e := InputEventKey.new()
	e.keycode = magic_key
	e.physical_keycode = magic_key
	e.pressed = down
	Input.parse_input_event(e)


func _exit_tree() -> void:
	if holding:
		_magic(false)


# key_magic's first key in refii.toml beside the game (the editor's staging root in a dev tree).
func _magic_binding() -> Key:
	for path in [OS.get_executable_path().get_base_dir().path_join("refii.toml"),
			ProjectSettings.globalize_path("res://build/x86_64/refii.toml")]:
		var file := FileAccess.open(path, FileAccess.READ)
		if file == null:
			continue
		for line in file.get_as_text().split("\n"):
			var t := line.strip_edges()
			if t.begins_with("key_magic ") or t.begins_with("key_magic="):
				var value := t.get_slice("=", 1).strip_edges().trim_prefix("\"").trim_suffix("\"")
				var code := OS.find_keycode_from_string(value.get_slice(",", 0).strip_edges())
				if code != KEY_NONE:
					return code
	return KEY_C


func _shown() -> bool:
	if host.setting("hotbar.show", 1.0) < 0.5 or _value("hotbar.ready") < 0.5:
		return false
	if host.local_hero() == 0 or int(host.cutscene_kinds(0)) & 3 != 0:
		return false
	return not in_menu and not loading


func _value(key: String) -> float:
	var v: float = host.get_value(key)
	return 0.0 if is_nan(v) else v


# What init.lua published, and each icon's texture when its path changes.
func _read() -> void:
	if atlas == null:
		atlas = _load(ATLAS)
	for i in SLOTS:
		var s := i + 1
		state[i] = {
			"kind": int(_value("hotbar.kind%d" % s)),
			"ok": _value("hotbar.ok%d" % s) >= 0.5,
			"on": _value("hotbar.on%d" % s) >= 0.5,
			"count": int(_value("hotbar.count%d" % s)),
			"hi": int(_value("hotbar.hi%d" % s)),
			"lo": int(_value("hotbar.lo%d" % s)),
		}
		var path := _icon_path(s)
		if path != paths[i]:
			paths[i] = path
			textures[i] = _load(path)
		elif textures[i] == null and path != "" and presentation == null and find_wait <= 0.0:
			textures[i] = _load(path)   # the presentation host was not up yet


func _icon_path(s: int) -> String:
	var n := int(_value("hotbar.icon%d.n" % s))
	var bytes := PackedByteArray()
	for j in ceili(n / 3.0):
		var packed := int(_value("hotbar.icon%d.%d" % [s, j]))
		bytes.append((packed >> 16) & 0xFF)
		bytes.append((packed >> 8) & 0xFF)
		bytes.append(packed & 0xFF)
	bytes.resize(n)
	return bytes.get_string_from_ascii()


# The game's texture by its data path (art\gui\...\x.tex), through the UI's presentation host.
func _load(path: String) -> Texture2D:
	if path == "":
		return null
	if presentation == null or not is_instance_valid(presentation):
		presentation = null
		if find_wait > 0.0:
			return null
		presentation = _find_presentation(get_tree().root)
		if presentation == null:
			find_wait = 1.0
			return null
	var texture = presentation.call("LoadTexture", path.replace("\\", "/"))
	return texture as Texture2D


func _find_presentation(node: Node) -> Node:
	if node.name == "Presentation" and node.has_method("LoadTexture"):
		return node
	for child in node.get_children():
		var found := _find_presentation(child)
		if found != null:
			return found
	return null


# A piece of the HUD atlas drawn into rect.
func _piece(region: Rect2, rect: Rect2, tint: Color) -> void:
	var scale := Vector2(atlas.get_size()) / 2048.0
	bar.draw_texture_rect_region(atlas, rect,
		Rect2(region.position * scale, region.size * scale), tint)


func _text(at: Vector2, text: String, size: int, width: float, align: HorizontalAlignment, colour: Color) -> void:
	bar.draw_string_outline(font, at, text, align, width, size, maxi(2, size / 5), SHADOW)
	bar.draw_string(font, at, text, align, width, size, colour)


func _draw_bar() -> void:
	if atlas == null:
		return
	var view := bar.get_viewport_rect().size
	var unit := view.y / 720.0
	var orb := 44.0 * unit * clampf(host.setting("hotbar.size", 1.0), 0.4, 2.0)
	var gap := orb * 0.16
	var split := orb * 0.45                       # 1-3 stand apart from 4-0
	var end := Vector2(orb * 1.3 * LEFT_END.size.x / LEFT_END.size.y, orb * 1.3)
	var width := SLOTS * orb + (SLOTS - 1) * gap + split
	var left := (view.x - width) * 0.5
	var number := int(maxf(10.0, orb * 0.36))
	var top: float = view.y - orb - number * 1.15 - 12.0 * unit - host.setting("hotbar.lift", 0.0) * unit

	var mid := top + orb * 0.5
	_piece(LEFT_END, Rect2(left - end.x + orb * 0.06, mid - end.y * 0.5, end.x, end.y), PEWTER)
	_piece(RIGHT_END, Rect2(left + width - orb * 0.06, mid - end.y * 0.5, end.x, end.y), PEWTER)

	for i in SLOTS:
		var slot: Dictionary = state[i]
		var kind: int = slot.get("kind", 0)
		var lit: bool = (i < 3 and i == mode) or slot.get("on", false)
		var x := left + i * (orb + gap) + (split if i >= 3 else 0.0)
		var rect := Rect2(x, top, orb, orb)

		if lit:
			_piece(GLOW, rect.grow(orb * 0.15), LIGHT)
		_piece(ORB, rect.grow(-orb * 0.05), GLASS if kind != 0 else GLASS_EMPTY)
		var texture: Texture2D = textures[i]
		if texture != null and kind != 0:
			var tint := Color.WHITE if slot.get("ok", false) else Color(1, 1, 1, 0.35)
			bar.draw_texture_rect(texture, rect.grow(-orb * 0.13), false, tint)
		_piece(RING, rect, PEWTER_LIT if lit else PEWTER)

		_text(Vector2(x, top + orb + number * 1.05), str((i + 1) % 10), number, orb,
			HORIZONTAL_ALIGNMENT_CENTER, PEWTER_LIT if lit else CREAM)

		# An item's stack, on a dark badge over the ring's lower right.
		var count: int = slot.get("count", -1)
		if kind == 3 and count >= 0:
			var badge := Rect2(x + orb * 0.6, top + orb * 0.6, orb * 0.46, orb * 0.46)
			_piece(DISC, badge, BADGE)
			_piece(RING, badge, PEWTER)
			var size := int(maxf(8.0, badge.size.y * 0.62))
			_text(Vector2(badge.position.x, badge.end.y - badge.size.y * 0.27), str(count), size,
				badge.size.x, HORIZONTAL_ALIGNMENT_CENTER, CREAM if count > 0 else Color(0.9, 0.42, 0.34))
