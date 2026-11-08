extends Node
## Minimap: the region's map in a round frame, north up, the hero an arrow that points.
##
## The art, the hero's place and the other quests' objectives come from the host (minimap(player), the
## game's own region map data); the dog and the enemies near are mod/init.lua's, put on the map by
## minimap_point. The map never moves
## or zooms, only the marks on it do. It is the map of the player this machine plays: the first of 1 and 2
## that player_here holds for, so player two's on an online joiner. M steps Normal > Bigger > Biggest (mid-screen) >
## Hidden. A place with no map of its own keeps the last one up, without marks. Shows in play only:
## not in the pause or the menus, not through a level change and not in a cutscene.
##
## ON A TOUCHSCREEN (touch_mode) a tap on the map is M (touch_area), and it stands lower, clear of the
## touch controls' top row of pills and of Spells at the right edge; hidden, a small compass stays where
## it was, and a tap on that brings it back.

const SIZES := [0.23, 0.35, 0.57]                 # diameters, of the screen's height
const CORNERS := [Vector2(0.207, 0.167), Vector2(0.267, 0.222)]   # the centre's distance from the top right, of the height
const TOUCH_CORNERS := [Vector2(0.347, 0.318), Vector2(0.42, 0.35)]   # the same on a touchscreen
const TOUCH_HIDDEN := 0.055                       # the radius of the compass a hidden map leaves on a touchscreen, of the height
const NEEDLE_NORTH := Color(0.86, 0.2, 0.15)
const STEPS := 3                                  # the Hidden step's index
const REFRESH := 0.05                             # seconds between asks of the host
const EASE := 10.0

const CREAM := Color(0.9, 0.84, 0.66)
const GOLD := Color(0.8, 0.63, 0.3)
const GOLD_LIT := Color(0.97, 0.86, 0.55)
const GOLD_DARK := Color(0.32, 0.22, 0.09)
const ARROW := Color(0.98, 0.97, 0.94)
const ARROW_EDGE := Color(0.05, 0.04, 0.03)
const OTHER_QUEST := Color(0.25, 0.68, 0.38)
const ENEMY := Color(0.86, 0.14, 0.11)
const DOG := Color(0.62, 0.42, 0.22)
const DAY := Color(0.65, 0.78, 0.84)
const NIGHT := Color(0.09, 0.16, 0.24)
const SUN := Color(0.98, 0.76, 0.2)
const STAR := Color(0.92, 0.95, 1.0)
const DOT_EDGE := Color(0.05, 0.04, 0.03, 0.8)

var host: Object
var canvas: Control
var player := 1                                   # whose map: the player this machine plays
var step := 0
var waited := REFRESH
var state := {}
var texture: Texture2D
var north := 0.0
var half_size := 157.5
var centre := Vector2.ZERO
var radius := 0.0
var placed := false
var area := Rect2()                               # where a tap is M, as last told to the host


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	host.input_action.connect(_on_input_action)
	var layer := CanvasLayer.new()
	layer.layer = 6                     # over the UI (5), under the loading screen (8)
	add_child(layer)
	canvas = Control.new()
	canvas.mouse_filter = Control.MOUSE_FILTER_IGNORE
	canvas.set_anchors_preset(Control.PRESET_FULL_RECT)
	canvas.draw.connect(_draw_map)
	layer.add_child(canvas)


func _on_input_action(e: Dictionary) -> void:
	if not e.get("pressed", false) or int(e.get("player", 1)) != player:
		return
	if e.get("name", "") == "minimap.size" and _in_play():
		step = (step + 1) % (STEPS + 1)


func _in_play() -> bool:
	return host.in_play(player) and not host.in_cutscene(player)


## The first of 1 and 2 that this machine plays.
func _here() -> int:
	for p in [1, 2]:
		if host.player_here(p):
			return p
	return 1


func _process(delta: float) -> void:
	waited += delta
	if waited >= REFRESH:
		waited = 0.0
		player = _here()
		state = host.minimap(player)
		if state.get("texture") != null:
			texture = state.texture
			north = state.get("north", 0.0)
			half_size = state.get("half_size", half_size)
	_place(delta)
	_touch_area()
	canvas.queue_redraw()


## Eases the frame to where the step puts it; a step away from hiding starts there.
func _place(delta: float) -> void:
	if step >= STEPS:
		placed = false
		return
	var screen := canvas.get_viewport_rect().size
	var wanted_radius: float = SIZES[step] * screen.y * 0.5 * host.setting("minimap.size", 1.0)
	var wanted := screen * 0.5
	if step < CORNERS.size():
		wanted = _corner(step, screen)
	var eased := 1.0 - exp(-EASE * delta) if placed else 1.0
	centre = centre.lerp(wanted, eased)
	radius = lerpf(radius, wanted_radius, eased)
	placed = true


## Where a step's centre stands: the top right, or lower down on a touchscreen.
func _corner(at: int, screen: Vector2) -> Vector2:
	var corner: Vector2 = TOUCH_CORNERS[at] if host.touch_mode() else CORNERS[at]
	return Vector2(screen.x - corner.x * screen.y, corner.y * screen.y)


## On a touchscreen, a tap on the map (or the ring a hidden one leaves) steps its size as M does.
func _touch_area() -> void:
	var wanted := Rect2()
	if host.touch_mode() and texture != null and _in_play() and host.setting("minimap.show", 1.0) >= 0.5:
		if step < STEPS:
			wanted = Rect2(centre - Vector2(radius, radius), Vector2(radius, radius) * 2.0)
		else:
			var screen := canvas.get_viewport_rect().size
			var ring := TOUCH_HIDDEN * screen.y
			wanted = Rect2(_corner(0, screen) - Vector2(ring, ring), Vector2(ring, ring) * 2.0)
	if wanted != area:
		area = wanted
		host.touch_area("minimap.size", wanted)


## An element-unit place on the map, on the screen; held inside the frame.
func _on_screen(place: Vector2) -> Vector2:
	var at := Vector2(place.x, -place.y) * radius / half_size
	var most := radius * 0.92
	return centre + (at.normalized() * most if at.length() > most else at)


func _draw_map() -> void:
	if texture == null or not _in_play() or host.setting("minimap.show", 1.0) < 0.5:
		return
	if step >= STEPS:
		if not area.has_area():
			return
		_draw_compass(area.get_center(), area.size.x * 0.5)
		return
	var unit := radius / 124.0
	var opacity: float = host.setting("minimap.opacity", 0.95)
	var points := PackedVector2Array()
	var uvs := PackedVector2Array()
	for i in 72:
		var edge := Vector2.from_angle(TAU * i / 72.0) * radius
		points.append(centre + edge)
		# The screen's Y runs down and the element's up; the image is turned by north.
		var inside := Vector2(edge.x, -edge.y).rotated(-north) * half_size / radius
		uvs.append(Vector2(inside.x, -inside.y) / (2.0 * half_size) + Vector2(0.5, 0.5))
	canvas.draw_colored_polygon(points, Color(1, 1, 1, opacity), uvs, texture)
	if state.get("proper", false):
		_draw_marks(unit)
	_draw_rim(unit)
	_draw_clock(unit)


func _draw_marks(unit: float) -> void:
	if _value("minimap.dog") >= 0.5:
		var dog = host.minimap_point(_value("minimap.dog_x"), _value("minimap.dog_y"), _value("minimap.dog_z"))
		if dog != null:
			_dot(_on_screen(dog), 3.5 * unit, DOG)
	for i in range(1, int(_value("minimap.n")) + 1):
		var place = host.minimap_point(_value("minimap.x%d" % i), _value("minimap.y%d" % i), _value("minimap.z%d" % i))
		if place != null:
			_dot(_on_screen(place), 4.0 * unit, ENEMY)
	for place in state.get("quests", []):
		_dot(_on_screen(place), 6.0 * unit, OTHER_QUEST)
	if state.has("hero"):
		_draw_arrow(_on_screen(state.hero), -state.get("facing", 0.0), unit)


func _dot(at: Vector2, size: float, colour: Color) -> void:
	canvas.draw_circle(at, size + 1.0, Color(DOT_EDGE, DOT_EDGE.a * colour.a))
	canvas.draw_circle(at, size, colour)


func _draw_arrow(at: Vector2, turn: float, unit: float) -> void:
	var shape := [Vector2(0, -10), Vector2(7, 8), Vector2(0, 4), Vector2(-7, 8)]
	var fill := PackedVector2Array()
	var edge := PackedVector2Array()
	for corner: Vector2 in shape:
		fill.append(at + corner.rotated(turn) * unit)
		edge.append(at + (corner * 1.35).rotated(turn) * unit)
	canvas.draw_colored_polygon(edge, ARROW_EDGE)
	canvas.draw_colored_polygon(fill, ARROW)


func _draw_rim(unit: float) -> void:
	canvas.draw_arc(centre, radius + 1.5 * unit, 0.0, TAU, 96, GOLD_DARK, 6.0 * unit, true)
	canvas.draw_arc(centre, radius + 1.5 * unit, 0.0, TAU, 96, GOLD, 3.5 * unit, true)
	canvas.draw_arc(centre, radius + 0.5 * unit, PI * 1.05, PI * 1.95, 48, GOLD_LIT, 1.0 * unit, true)


## Hidden on a touchscreen: a small compass in the map's frame, turned with the camera, its red needle and N
## toward north; a tap on it brings the map back.
func _draw_compass(at: Vector2, ring: float) -> void:
	canvas.draw_circle(at, ring, Color(NIGHT, 0.8))
	canvas.draw_arc(at, ring, 0.0, TAU, 48, GOLD_DARK, ring * 0.2, true)
	canvas.draw_arc(at, ring, 0.0, TAU, 48, GOLD, ring * 0.11, true)
	var north := Vector2.UP.rotated(-_heading())
	var side := north.orthogonal()
	for i in 4:
		var tick := north.rotated(PI * 0.5 * i)
		canvas.draw_line(at + tick * ring * 0.68, at + tick * ring * 0.84, Color(GOLD, 0.7), maxf(1.0, ring * 0.05), true)
	canvas.draw_colored_polygon(PackedVector2Array([at + north * ring * 0.62, at + side * ring * 0.16, at - side * ring * 0.16]), NEEDLE_NORTH)
	canvas.draw_colored_polygon(PackedVector2Array([at - north * ring * 0.62, at - side * ring * 0.16, at + side * ring * 0.16]), CREAM)
	canvas.draw_circle(at, ring * 0.08, GOLD_DARK)
	var font := canvas.get_theme_default_font()
	var size := int(ring * 0.36)
	var letter := at + north * ring * 1.0
	var mark: String = host.text("MINIMAP_COMPASS_NORTH")
	canvas.draw_string_outline(font, letter + Vector2(-ring * 0.5, size * 0.36), mark, HORIZONTAL_ALIGNMENT_CENTER, ring, size, maxi(2, size / 5), ARROW_EDGE)
	canvas.draw_string(font, letter + Vector2(-ring * 0.5, size * 0.36), mark, HORIZONTAL_ALIGNMENT_CENTER, ring, size, GOLD_LIT)


## The camera's heading on the map, radians clockwise from north (0 when it is not known): the camera and a
## point ahead of it, placed by the same minimap_point the marks are.
func _heading() -> float:
	var camera := canvas.get_viewport().get_camera_3d()
	if camera == null:
		return 0.0
	var forward := -camera.global_transform.basis.z
	forward.y = 0.0
	if forward.length() < 0.001:
		return 0.0
	var from := camera.global_position
	var ahead := from + forward.normalized() * 10.0
	# Godot's axes to the game's: X mirrored, Y and Z trade places.
	var a = host.minimap_point(-from.x, from.z, from.y)
	var b = host.minimap_point(-ahead.x, ahead.z, ahead.y)
	if a == null or b == null:
		return 0.0
	var d: Vector2 = b - a
	return atan2(d.x, d.y) if d.length() > 0.0001 else 0.0


## The day dial on the rim: day on the left, night on the right, noon's hand over the sun.
func _draw_clock(unit: float) -> void:
	var size := 26.0 * unit
	var at := centre + Vector2(1, -1).normalized() * (radius + 2.0 * unit)
	canvas.draw_circle(at, size, NIGHT)
	var day := PackedVector2Array()
	for i in 25:
		day.append(at + Vector2.from_angle(PI * 0.5 + PI * i / 24.0) * size)
	canvas.draw_colored_polygon(day, DAY)
	canvas.draw_circle(at + Vector2(-size * 0.5, 0), size * 0.3, SUN)
	for star: Vector2 in [Vector2(0.45, -0.4), Vector2(0.62, 0.15), Vector2(0.3, 0.5)]:
		canvas.draw_circle(at + star * size, 1.4 * unit, STAR)
	var hand := Vector2.from_angle(PI + TAU * (_value("minimap.hour") - 12.0) / 24.0)
	canvas.draw_line(at, at + hand * size * 0.82, ARROW, 2.5 * unit, true)
	canvas.draw_circle(at, 2.5 * unit, ARROW)
	canvas.draw_arc(at, size, 0.0, TAU, 48, GOLD_DARK, 5.0 * unit, true)
	canvas.draw_arc(at, size, 0.0, TAU, 48, GOLD, 3.0 * unit, true)


func _value(key: String) -> float:
	var v: float = host.get_value(key)
	return 0.0 if is_nan(v) else v
