extends Node
## Health Bars: draws a bar over each enemy mod/init.lua publishes (see its header).
##
## A bar hangs over the creature's drawn body: the node the host draws it with (node_for), lifted
## to the top of its mesh bounds as the HUD's own markers are, projected through the camera of
## the view. It shrinks with distance, and a pale trail follows a hit down so the damage reads.
## Bars show in play only: not in the pause or the menus, not through a level change and not in a
## cutscene.

const FONT := "res://assets/launcher/fable2.ttf"

const WIDTH := 96.0                 # pixels at full size on a 1080-line screen
const HEIGHT := 10.0
const LIFT := 0.0                   # metres over the top of the body
const NEAR := 6.0                   # full size within, metres
const FAR := 30.0                   # smallest beyond
const TRAIL_WAIT := 0.45            # seconds the trail holds before it follows
const TRAIL_SPEED := 0.9            # of a full bar a second

const BACK := Color(0.05, 0.04, 0.03, 0.78)
const RIM := Color(0.78, 0.66, 0.42, 0.9)
const FILL := Color(0.72, 0.09, 0.07)
const FILL_TOP := Color(0.92, 0.25, 0.18)
const TRAIL := Color(0.98, 0.86, 0.62, 0.85)
const CREAM := Color(0.96, 0.91, 0.78)
const SHADOW := Color(0.04, 0.03, 0.02, 0.85)

var host: Object
var layer: CanvasLayer
var canvas: Control
var font: Font
var in_menu := false
var loading := false
var bars := {}                      # handle -> { shown, trail, wait, height }


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	host.menu_opened.connect(func(_e): in_menu = true)
	host.menu_closed.connect(func(_e): in_menu = false)
	host.paused.connect(func(e): in_menu = e.get("paused", false))
	host.level_unloading.connect(_on_level_unloading)
	host.level_loaded.connect(func(_e): loading = false)
	layer = CanvasLayer.new()
	layer.layer = 80
	add_child(layer)
	canvas = Control.new()
	canvas.mouse_filter = Control.MOUSE_FILTER_IGNORE
	canvas.set_anchors_preset(Control.PRESET_FULL_RECT)
	canvas.draw.connect(_draw_bars)
	layer.add_child(canvas)
	font = load(FONT) as Font
	if font == null:
		font = ThemeDB.fallback_font


func _on_level_unloading(_e: Dictionary) -> void:
	loading = true
	bars.clear()


func _process(delta: float) -> void:
	var listed := {}
	for i in range(1, int(_value("health_bars.n")) + 1):
		var handle := int(_value("health_bars.e%d" % i))
		var most := _value("health_bars.m%d" % i)
		if handle <= 0 or most <= 0.0:
			continue
		var share := clampf(_value("health_bars.c%d" % i) / most, 0.0, 1.0)
		listed[handle] = true
		var bar: Dictionary = bars.get(handle, {})
		if bar.is_empty():
			bar = { "shown": share, "trail": share, "wait": 0.0, "height": -1.0 }
			bars[handle] = bar
		if share < bar.shown:
			bar.wait = TRAIL_WAIT
		elif share > bar.trail:
			bar.trail = share
		bar.shown = share
		bar.current = _value("health_bars.c%d" % i)
		bar.most = most
		if bar.wait > 0.0:
			bar.wait -= delta
		else:
			bar.trail = maxf(bar.shown, bar.trail - TRAIL_SPEED * delta)
	for handle in bars.keys():
		if not listed.has(handle):
			bars.erase(handle)
	canvas.queue_redraw()


func _shown() -> bool:
	return not in_menu and not loading and host.setting("health_bars.show", 1.0) >= 0.5 \
		and int(host.cutscene_kinds(0)) == 0


func _value(key: String) -> float:
	var v: float = host.get_value(key)
	return 0.0 if is_nan(v) else v


## The top of the body in world space, from the drawn mesh's bounds (kept per creature).
func _top(handle: int, bar: Dictionary) -> Variant:
	var node: Node3D = host.node_for(handle)
	if node == null or not node.is_visible_in_tree():
		return null
	if bar.height < 0.0:
		var mesh := node as MeshInstance3D
		bar.height = clampf(mesh.get_aabb().end.y, 0.3, 6.0) if mesh != null and mesh.mesh != null else 1.8
	return node.global_transform * Vector3(0.0, bar.height, 0.0) + Vector3(0.0, LIFT, 0.0)


func _draw_bars() -> void:
	if bars.is_empty() or not _shown():
		return
	var camera := canvas.get_viewport().get_camera_3d()
	if camera == null:
		return
	var size: float = host.setting("health_bars.size", 1.0) * canvas.get_viewport_rect().size.y / 1080.0
	var numbers: bool = host.setting("health_bars.numbers", 0.0) >= 0.5
	for handle in bars:
		var bar: Dictionary = bars[handle]
		var top = _top(handle, bar)
		if top == null or camera.is_position_behind(top):
			continue
		var distance := camera.global_position.distance_to(top)
		var scale := lerpf(1.0, 0.45, clampf((distance - NEAR) / (FAR - NEAR), 0.0, 1.0)) * size
		var at := camera.unproject_position(top)
		var w := WIDTH * scale
		var h := maxf(4.0, HEIGHT * scale)
		var rect := Rect2(at.x - w * 0.5, at.y - h, w, h)
		var inner := rect.grow(-1.0)
		canvas.draw_rect(rect.grow(1.0), BACK)
		canvas.draw_rect(Rect2(inner.position, Vector2(inner.size.x * bar.trail, inner.size.y)), TRAIL)
		var fill := Rect2(inner.position, Vector2(inner.size.x * bar.shown, inner.size.y))
		canvas.draw_rect(fill, FILL)
		canvas.draw_rect(Rect2(fill.position, Vector2(fill.size.x, fill.size.y * 0.4)), FILL_TOP)
		canvas.draw_rect(rect.grow(1.0), RIM, false, 1.0)
		if numbers and h >= 7.0:
			var text := "%d / %d" % [roundi(bar.current), roundi(bar.most)]
			var font_size := int(round(14.0 * scale))
			var spot := Vector2(rect.position.x, rect.position.y - 3.0)
			canvas.draw_string(font, spot + Vector2(1, 1), text, HORIZONTAL_ALIGNMENT_CENTER, w, font_size, SHADOW)
			canvas.draw_string(font, spot, text, HORIZONTAL_ALIGNMENT_CENTER, w, font_size, CREAM)
