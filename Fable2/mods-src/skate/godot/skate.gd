extends Node
## The board under the hero, its flips and grabs, the score, and the stick for mod/init.lua.
##
## The board is one player's at a time, mod/init.lua's skate.player: their hero rides it and is posed, and
## the mouse flips and grabs only where this machine's keyboard plays them (attack_mode). Every word drawn
## is a key of language/<culture>/text/book.babel.csv (host.text), and the controls panel draws what each
## action is bound to now (input_glyph, input_label).

const MODEL := "game://art/inventory/weapons/skateboard/skateboard.mdl"
const TOP_PUSH := 9.0               # metres a second, the most pushing reaches
const FONT := "res://assets/launcher/fable2.ttf"
const FLIP_TIME := 0.36             # as mod/init.lua has it
const FLIP_DROP := 0.16             # how far under the feet the board falls away to turn
const GRAB_LIFT := 0.5              # how far up the board comes to meet a hand
const GRAB_TILT := 0.75             # radians
const GRAB_EASE := 12.0
# The tricks' names by id, as text keys; a spin's id is its degrees, shown as the number.
const NAMES := {1: "SKATE_TRICK_FLOURISH", 2: "SKATE_TRICK_BACK_FLOURISH", 3: "SKATE_TRICK_CHEEKY_SHOVE", 4: "SKATE_TRICK_IMPROBABLE",
	5: "SKATE_TRICK_PIE_GRAB", 6: "SKATE_TRICK_HEROS_GRIP", 7: "SKATE_TRICK_BALVERINES_TAIL", 8: "SKATE_TRICK_GARGOYLES_NOSE",
	9: "SKATE_TRICK_WHEELIE", 10: "SKATE_TRICK_RAIL_RIDER", 11: "SKATE_TRICK_GUILD_GRINDER"}
const WHITE := Color(1.0, 0.97, 0.88)
const GOLD := Color(1.0, 0.8, 0.28)
const GOOD := Color(0.55, 1.0, 0.45)
const BAD := Color(1.0, 0.3, 0.22)
const MANUAL_TILT := 0.3            # radians, the nose up
const MANUAL_LIFT := 0.05
const MANUAL_EASE := 7.0
const METER := Vector2(340.0, 14.0)
const TILT_EASE := 9.0
const ROLL_MOST := 0.16             # radians, leaning into a full turn at speed
const BIG_COMBO := 3000.0           # the points at which the score text is at its largest
const CROUCH_REST := 0.42            # riding along
const CROUCH_AIR := 0.8
const CROUCH_SPEED := 0.2             # more, at top speed
const CROUCH_GRIND := 0.65
const CROUCH_MANUAL := 0.25
const CROUCH_GRAB := 0.85
const SPRING := 90.0                # how hard the crouch springs back to where it wants to be
const SPRING_DAMP := 12.0
const HOP_KICK := -4.0              # the crouch's speed from a hop: it stands up
const LAND_KICK := 6.0              # and from a landing: it squashes
const POSE_EASE := 8.0
const AIR_TUCK := 0.22              # metres the board and feet come up in the air
const FLIP_TUCK := 0.2              # metres the feet come up over a turning board
const PUSH_RATE := 8.0              # radians a second through a push's kick
const SOUNDS := "game://sounds/skate/%s.wav"
const SOUND_VOLUME := -5.0          # decibels
const LOOP_VOLUME := -9.0
const LOOP_EASE := 8.0              # decibels a tenth of a second, fading a loop in and out
const SILENT := -60.0
const SHOT_PLAYERS := 4              # a hop and a flip can start together
const SOUND_VARY := 0.06            # of a one-shot's pitch, either way, so a repeat isn't identical
const GRIND_PITCHES := Vector2(0.85, 1.2) # by speed
const ROLL_PITCHES := Vector2(0.7, 1.5)
const ROLL_QUIET := -22.0           # decibels when barely moving, up to LOOP_VOLUME at speed
const ROLL_FROM := 0.2              # metres a second below which the wheels make no sound
const TOP_SPEED := 14.0             # the most a hill can reach, in mod/init.lua
const SCORE_AT := Vector2(30.0, 62.0)
const TOUCH_SCORE_AT := Vector2(40.0, 355.0)   # on a touchscreen, under Lock and over the items (720 to the screen's height)
const PANEL_AT := Vector2(26.0, 330.0)
const PANEL_ROW := 30.0
const PANEL_ROW_PAD := 44.0
const PANEL_KEYS_AT := 170.0        # the keys' column, from the panel's left
const PANEL_WIDTH := 430.0
const PANEL_KEYS_AT_PAD := 230.0
const PANEL_WIDTH_PAD := 540.0
# What does what: its text key, then how the keyboard and the pad do it. A "text" is a text key drawn as
# it is (the game's own movement and the mouse buttons); otherwise "pattern", a text key whose %1 (and %2)
# stand for the bound inputs of "binds", each drawn as its glyph (input_glyph) or, with none, its name.
const CONTROLS := [
	["SKATE_CONTROL_PUSH", { "text": "SKATE_CONTROL_PUSH_KEYS" }, { "text": "SKATE_CONTROL_PUSH_STICK" }],
	["SKATE_CONTROL_STEER", { "text": "SKATE_CONTROL_STEER_KEYS" }, { "text": "SKATE_CONTROL_STEER_STICK" }],
	["SKATE_CONTROL_HOP", { "binds": ["skate.ollie"] }, { "binds": ["skate.ollie"] }],
	["SKATE_TRICK_FLOURISH", { "text": "SKATE_CONTROL_LEFT_CLICK" }, { "binds": ["skate.flip"] }],
	["SKATE_TRICK_PIE_GRAB", { "text": "SKATE_CONTROL_HOLD_RIGHT_CLICK" }, { "pattern": "SKATE_CONTROL_HOLD", "binds": ["skate.grab"] }],
	["SKATE_TRICK_WHEELIE", { "pattern": "SKATE_CONTROL_HOLD", "binds": ["skate.manual"] }, { "pattern": "SKATE_CONTROL_HOLD", "binds": ["skate.manual"] }],
	["SKATE_TRICK_RAIL_RIDER", { "pattern": "SKATE_CONTROL_NEAR_LEDGE", "binds": ["skate.grind"] }, { "pattern": "SKATE_CONTROL_NEAR_LEDGE", "binds": ["skate.grind"] }],
	["SKATE_CONTROL_STEP_OFF", { "binds": ["skate.toggle"] }, { "binds": ["Back"] }],
]
const CHORD := "%1 + %2"            # a held pad modifier and the button under it
const BOUND := "%1"
const GUILD_GRINDER := 11            # the trick that earns its own callout
const SHOWN := 5                    # trick names on the line at once
const FLASH_TIME := 1.4

## The hero's body while riding, posed after the game has posed it, for drawing only: the game's pose is
## never written. The mod has no animations of its own, so this works from where the body already is: the
## hips drop while the feet stay where they were, the chest leans, and the arms and legs reach for points by
## two-bone inverse kinematics, which needs no knowledge of how each bone happens to be turned.
##
## godot/skate.gd sets what's below each frame, in world axes, and active only while riding.
class Rider extends SkeletonModifier3D:
	const HIP_DROP := 0.42              # metres, at a full crouch
	const CHEST_LEAN := 0.6             # radians, at a full crouch, toward the toes
	const MANUAL_LEAN := 0.55            # radians back along the board
	const ARM_OUT := 0.3                # metres out along the board from each shoulder
	const ARM_DOWN := 0.46
	const ARM_FRONT := 0.08             # and toward the toes
	const HIP_TURN := 0.16              # metres over the toes or heels, in a full turn
	const CHEST_TURN := 0.32            # radians the same way
	const HIP_BACK := 0.14              # metres toward the tail, in a manual
	const ELBOW_BACK := 0.8
	const ELBOW_OUT := 0.5
	const ELBOW_DOWN := 0.35
	const ARM_SWAY := 0.12               # metres an arm rises or falls in a full turn
	const PUSH_REACH := 0.34            # metres along the board that a pushing foot sweeps
	const PUSH_SIDE := 0.22             # metres off the deck toward the heels
	const PUSH_DROP := 0.11             # metres from the deck down to the ground
	const PUSH_LIFT := 0.06             # metres clear of the ground on the way forward

	var crouch := 0.3                   # 0 standing to 1 full
	var nose := Vector3.FORWARD         # along the board, the way the nose points
	var toes := Vector3.RIGHT           # the way the hero faces
	var turn := 0.0                     # -1 to 1, leaning over the toes or heels
	var manual := 0.0                   # 0 to 1
	var arms := 1.0                     # 0 down to 1 out
	var push := 0.0                     # 0 to 1, how far into pushing
	var push_phase := 0.0               # radians
	var grab_hand := 0                  # 0 none, -1 the left hand, 1 the right
	var grab_at := Vector3.ZERO         # the point on the board it reaches for
	var grab := 0.0                     # 0 to 1
	var feet_lift := 0.0                # metres the feet come up, with the board
	var deck_tilt := 0.0                # radians the board's nose is up, which the feet follow

	var ready_bones := false
	var hips := -1
	var chest := -1
	var thigh := [-1, -1]
	var shin := [-1, -1]
	var foot := [-1, -1]
	var upper := [-1, -1]
	var fore := [-1, -1]
	var hand := [-1, -1]


	func _bone_named(skeleton: Skeleton3D, names: Array) -> int:
		for name in names:
			var found := skeleton.find_bone(name)
			if found >= 0:
				return found
		return -1


	func find_bones(skeleton: Skeleton3D) -> bool:
		var names := PackedStringArray()
		for b in skeleton.get_bone_count():
			names.append(skeleton.get_bone_name(b))
		print("skate: hero's bones: ", ", ".join(names))
		hips = _bone_named(skeleton, ["Shadow_Hip"])
		chest = _bone_named(skeleton, ["Shadow_Chest", "Shadow_SpineControl"])
		var sides := ["L", "R"]
		for i in 2:
			var side: String = sides[i]
			foot[i] = _bone_named(skeleton, ["Shadow_%s_Heel" % side])
			if foot[i] >= 0:
				shin[i] = _bone_named(skeleton, ["Shadow_%s_Shin" % side])
				thigh[i] = _bone_named(skeleton, ["Shadow_%s_Thigh" % side])
			hand[i] = _bone_named(skeleton, ["Shadow_%s_BlendHand" % side])
			if hand[i] >= 0:
				fore[i] = _bone_named(skeleton, ["Shadow_%s_BlendForearm" % side])
				upper[i] = _bone_named(skeleton, ["Shadow_%s_BlendUpperarm" % side])
		print("skate: posing with hips %d chest %d thigh %s shin %s foot %s upper %s fore %s hand %s" % [hips, chest, thigh, shin, foot, upper, fore, hand])
		ready_bones = hips >= 0 and foot[0] >= 0 and foot[1] >= 0 and shin[0] >= 0 and shin[1] >= 0 and thigh[0] >= 0 and thigh[1] >= 0
		return ready_bones


	func _rotation_of(skeleton: Skeleton3D, bone: int) -> Quaternion:
		if bone < 0:
			return Quaternion.IDENTITY
		return skeleton.get_bone_global_pose(bone).basis.get_rotation_quaternion()


	func _set_global_rotation(skeleton: Skeleton3D, bone: int, turned: Quaternion) -> void:
		var parent := _rotation_of(skeleton, skeleton.get_bone_parent(bone))
		skeleton.set_bone_pose_rotation(bone, (parent.inverse() * turned).normalized())


	## Turns a bone in skeleton space, so that what was at from (seen from the bone) is toward to.
	func _aim(skeleton: Skeleton3D, bone: int, from: Vector3, to: Vector3) -> void:
		var origin := skeleton.get_bone_global_pose(bone).origin
		var a := from - origin
		var b := to - origin
		if a.length_squared() < 1e-8 or b.length_squared() < 1e-8:
			return
		a = a.normalized()
		b = b.normalized()
		var cross := a.cross(b)
		if cross.length_squared() < 1e-8:
			return
		_set_global_rotation(skeleton, bone, Quaternion(a, b) * _rotation_of(skeleton, bone))


	## Puts the end of a chain of two at a point, the joint bending toward pole. Skeleton space throughout.
	func _reach(skeleton: Skeleton3D, root: int, joint: int, end: int, target: Vector3, pole: Vector3) -> void:
		var p0 := skeleton.get_bone_global_pose(root).origin
		var p1 := skeleton.get_bone_global_pose(joint).origin
		var p2 := skeleton.get_bone_global_pose(end).origin
		var la := p0.distance_to(p1)
		var lb := p1.distance_to(p2)
		if la < 1e-4 or lb < 1e-4:
			return
		var to := target - p0
		var distance := clampf(to.length(), absf(la - lb) + 0.001, la + lb - 0.001)
		var along := to.normalized()
		var aside := pole - along * pole.dot(along)
		if aside.length_squared() < 1e-6:
			aside = (p1 - p0) - along * (p1 - p0).dot(along)
		aside = aside.normalized()
		var cos_a := clampf((la * la + distance * distance - lb * lb) / (2.0 * la * distance), -1.0, 1.0)
		var wanted := p0 + along * la * cos_a + aside * la * sqrt(1.0 - cos_a * cos_a)
		_aim(skeleton, root, p1, wanted)
		_aim(skeleton, joint, skeleton.get_bone_global_pose(end).origin, p0 + along * distance)


	func _process_modification_with_delta(_delta: float) -> void:
		var skeleton := get_skeleton()
		if skeleton == null or not ready_bones:
			return
		var inward := skeleton.global_transform.affine_inverse().basis
		var up := (inward * Vector3.UP).normalized()
		var along := (inward * nose).normalized()
		var front := (inward * toes).normalized()
		var height := (inward * Vector3.UP).length()     # world metres to skeleton units

		# Where the feet are before the hips move, and how they are turned.
		var feet := [skeleton.get_bone_global_pose(foot[0]), skeleton.get_bone_global_pose(foot[1])]

		# The hips: down, and over the toes or heels with a turn.
		var drop := (-up * crouch * HIP_DROP + front * turn * HIP_TURN - along * manual * HIP_BACK) * height
		var parent := _rotation_of(skeleton, skeleton.get_bone_parent(hips))
		skeleton.set_bone_pose_position(hips, skeleton.get_bone_pose_position(hips) + parent.inverse() * drop)

		# The legs, back to the feet. A pushing foot leaves the deck for the ground.
		var pushing := -1
		if push > 0.0:
			pushing = 0 if (feet[0].origin - feet[1].origin).dot(along) < 0.0 else 1
		# The feet stay on the deck as it rises and tilts.
		var middle: Vector3 = (feet[0].origin + feet[1].origin) * 0.5
		var tilt_axis := along.cross(up).normalized()
		var tilted := Quaternion(tilt_axis, deck_tilt)
		for i in 2:
			var target: Vector3 = feet[i].origin + up * feet_lift * height
			target += tilted * (feet[i].origin - middle) - (feet[i].origin - middle)
			if i == pushing:
				var sweep := sin(push_phase)
				var down := -PUSH_DROP * maxf(0.0, cos(push_phase)) + PUSH_LIFT * maxf(0.0, -cos(push_phase))
				target += (along * (sweep * PUSH_REACH) - front * PUSH_SIDE + up * down) * height * push
			_reach(skeleton, thigh[i], shin[i], foot[i], target, front)
			_set_global_rotation(skeleton, foot[i], tilted * feet[i].basis.get_rotation_quaternion())

		# The chest: forward with the crouch, and back along the board in a manual.
		if chest >= 0:
			var over := up.cross(front).normalized()
			var bend := Quaternion(over, crouch * CHEST_LEAN + turn * CHEST_TURN)
			var back := Quaternion(along.cross(up).normalized(), manual * MANUAL_LEAN)
			_set_global_rotation(skeleton, chest, back * bend * _rotation_of(skeleton, chest))

		# The arms: out along the board, one reaching for the board in a grab.
		for i in 2:
			if upper[i] < 0 or fore[i] < 0 or hand[i] < 0:
				continue
			var shoulder := skeleton.get_bone_global_pose(upper[i]).origin
			var side_sign := 1.0 if (shoulder - skeleton.get_bone_global_pose(chest if chest >= 0 else hips).origin).dot(along) >= 0.0 else -1.0
			var rest := skeleton.get_bone_global_pose(hand[i]).origin
			var out := shoulder + (along * side_sign * ARM_OUT + up * (turn * side_sign * ARM_SWAY - ARM_DOWN) + front * ARM_FRONT) * height
			var aim := rest.lerp(out, arms)
			var is_grab := (i == 0 and grab_hand < 0) or (i == 1 and grab_hand > 0)
			if is_grab and grab > 0.0:
				aim = aim.lerp(skeleton.global_transform.affine_inverse() * grab_at, grab)
			# Elbows back, out and a little down, as an arm held for balance has them.
			var elbow := -front * ELBOW_BACK + along * side_sign * ELBOW_OUT - up * ELBOW_DOWN
			_reach(skeleton, upper[i], fore[i], hand[i], aim, elbow)


var host: Object
var player := 1                     # whose board: skate.player
var marker := RegEx.create_from_string("%(\\d)")   # a pattern's %N
var interactive := 2                # the cutscene kind that also takes the player's hands
var board: MeshInstance3D
var canvas: Control
var face: Font
var stick := Vector2.ZERO
var mouse_ours := false
var flip_seen := 0.0
var flip_kind := 0
var flip_left := 0.0
var grab_shown := 0.0
var grab_kind := 0
var air_shown := 0.0
var feet_up := 0.0
var deck_shown := 0.0
var trick_seen := 0.0
var landed_seen := 0.0
var line: PackedStringArray = []
var flash := 0.0
var flash_text := ""
var flash_colour := GOOD
var flash_size := 1.0
var manual_shown := 0.0
var pitch_shown := 0.0
var roll_shown := 0.0
var sparks: CPUParticles3D
var shots: Array[AudioStreamPlayer] = []
var grind_loop: AudioStreamPlayer
var roll_loop: AudioStreamPlayer
var wheelie_loop: AudioStreamPlayer
var hop_sound: AudioStream
var land_sound: AudioStream
var thrown_sound: AudioStream
var flourish_sound: AudioStream
var grab_sound: AudioStream
var grab_was := 0
var hop_seen := 0.0
var land_seen := 0.0
var rider: Rider
var rider_failed := false
var crouch := CROUCH_REST
var crouch_speed := 0.0
var on_pad := false


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	interactive = int(host.constants().get("CUTSCENE_INTERACTIVE", interactive))
	board = MeshInstance3D.new()
	board.visible = false
	add_child(board)
	for i in SHOT_PLAYERS:
		var shot := AudioStreamPlayer.new()
		add_child(shot)
		shots.append(shot)
	grind_loop = AudioStreamPlayer.new()
	grind_loop.volume_db = SILENT
	add_child(grind_loop)
	roll_loop = AudioStreamPlayer.new()
	roll_loop.volume_db = SILENT
	add_child(roll_loop)
	wheelie_loop = AudioStreamPlayer.new()
	wheelie_loop.volume_db = SILENT
	add_child(wheelie_loop)
	hop_seen = _value("skate.hop_seq")
	land_seen = _value("skate.land_seq")
	sparks = _build_sparks()
	add_child(sparks)
	face = load(FONT) as Font
	if face == null:
		face = ThemeDB.fallback_font
	var layer := CanvasLayer.new()
	layer.layer = 6                     # over the UI (5), under the loading screen (8)
	add_child(layer)
	canvas = Control.new()
	canvas.mouse_filter = Control.MOUSE_FILTER_IGNORE
	canvas.set_anchors_preset(Control.PRESET_FULL_RECT)
	canvas.draw.connect(_draw_score)
	layer.add_child(canvas)
	# What a run before a reload left published is not news.
	flip_seen = _value("skate.flip_seq")
	trick_seen = _value("skate.trick_seq")
	landed_seen = _value("skate.landed_seq")


func _load_sounds() -> void:
	hop_sound = load(SOUNDS % "hop") as AudioStream
	land_sound = load(SOUNDS % "land") as AudioStream
	thrown_sound = load(SOUNDS % "thrown") as AudioStream
	flourish_sound = load(SOUNDS % "flourish") as AudioStream
	grab_sound = load(SOUNDS % "grab") as AudioStream
	_start_loop(grind_loop, "grind")
	_start_loop(roll_loop, "roll")
	_start_loop(wheelie_loop, "wheelie")
	print("skate: sounds hop=%s land=%s thrown=%s grind=%s roll=%s" % [hop_sound != null, land_sound != null, thrown_sound != null, grind_loop.stream != null, roll_loop.stream != null])


func _start_loop(player: AudioStreamPlayer, name: String) -> void:
	var sound := load(SOUNDS % name) as AudioStreamWAV
	if sound != null:
		sound.loop_mode = AudioStreamWAV.LOOP_FORWARD
		sound.loop_end = int(sound.get_length() * sound.mix_rate)
		player.stream = sound
		player.play()


func _shot(sound: AudioStream) -> void:
	if sound == null:
		return
	var free := shots[0]
	for shot in shots:
		if not shot.playing:
			free = shot
			break
	free.stream = sound
	free.pitch_scale = 1.0 + randf_range(-SOUND_VARY, SOUND_VARY)
	free.volume_db = SOUND_VOLUME
	free.play()


func _fade_loop(player: AudioStreamPlayer, wanted: float, delta: float) -> void:
	player.volume_db = move_toward(player.volume_db, wanted if _in_play() else SILENT, delta * LOOP_EASE * 10.0)


func _update_sounds(delta: float, riding: bool) -> void:
	var speed := clampf(_value("skate.speed") / TOP_SPEED, 0.0, 1.0)
	var grinding := riding and int(_value("skate.balance")) == 2
	_fade_loop(grind_loop, LOOP_VOLUME if grinding else SILENT, delta)
	grind_loop.pitch_scale = lerpf(GRIND_PITCHES.x, GRIND_PITCHES.y, speed)
	# Wheels on the ground, moving.
	var rolling := riding and not grinding and _value("skate.air") < 0.5 and _value("skate.bailing") < 0.5 and _value("skate.speed") > ROLL_FROM
	# On its back wheels, the wheelie's whirr takes over from the hum.
	var wheelie := rolling and int(_value("skate.balance")) == 1
	var wheels := lerpf(ROLL_QUIET, LOOP_VOLUME, speed)
	_fade_loop(roll_loop, wheels if rolling and not wheelie else SILENT, delta)
	_fade_loop(wheelie_loop, wheels if wheelie else SILENT, delta)
	roll_loop.pitch_scale = lerpf(ROLL_PITCHES.x, ROLL_PITCHES.y, speed)
	wheelie_loop.pitch_scale = roll_loop.pitch_scale


func _build_sparks() -> CPUParticles3D:
	var glow := StandardMaterial3D.new()
	glow.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	glow.billboard_mode = BaseMaterial3D.BILLBOARD_ENABLED
	glow.albedo_color = Color(1.0, 0.72, 0.28)
	glow.disable_fog = true
	var quad := QuadMesh.new()
	quad.size = Vector2(0.025, 0.025)
	quad.material = glow
	var made := CPUParticles3D.new()
	made.mesh = quad
	made.amount = 28
	made.lifetime = 0.35
	made.emitting = false
	made.local_coords = false
	made.direction = Vector3.UP
	made.spread = 70.0
	made.initial_velocity_min = 1.5
	made.initial_velocity_max = 3.5
	made.gravity = Vector3(0.0, -9.0, 0.0)
	made.scale_amount_min = 0.5
	made.scale_amount_max = 1.2
	return made


func _value(key: String) -> float:
	var value: float = host.get_value(key)
	return 0.0 if is_nan(value) else value


## The board owner's own move stick, x right and y up: player 1's keys, touch stick and pad, player 2's pad.
func _read_stick() -> Vector2:
	var read: Vector2 = host.move_stick(player)
	return Vector2.ZERO if read.length() < 0.2 else read


## While riding, the left mouse button flips and the right one grabs, in place of what the game has them do.
func _in_play() -> bool:
	return host.in_play(player) and not host.in_cutscene(player, interactive)


func _input(event: InputEvent) -> void:
	# The controls are named for whichever was used last.
	if event is InputEventJoypadButton:
		on_pad = true
	elif event is InputEventJoypadMotion:
		on_pad = on_pad or absf(event.axis_value) > 0.4
	elif event is InputEventKey or event is InputEventMouseButton:
		on_pad = false
	if not mouse_ours or not event is InputEventMouseButton:
		return
	var click := event as InputEventMouseButton
	if click.button_index == MOUSE_BUTTON_LEFT and click.pressed:
		host.run_lua("Skate.flip()")
	elif click.button_index == MOUSE_BUTTON_RIGHT:
		host.run_lua("Skate.grab(true)" if click.pressed else "Skate.grab(false)")




func _process(delta: float) -> void:
	var riding := _value("skate.on") >= 0.5
	player = maxi(1, int(_value("skate.player")))
	# The claims in init.lua take what the buttons would press: X, Y and B, the attack button, and LT.
	mouse_ours = riding and _in_play() and host.attack_mode(player) != 0
	if not riding:
		if rider != null and is_instance_valid(rider) and rider.active:
			rider.active = false
			rider.get_skeleton().modifier_callback_mode_process = Skeleton3D.MODIFIER_CALLBACK_MODE_PROCESS_MANUAL
		board.visible = false
		sparks.emitting = false
		line.clear()
		flash = 0.0
		_update_sounds(delta, false)
		canvas.queue_redraw()
		return
	var read := _read_stick() if _in_play() else Vector2.ZERO
	if read.distance_to(stick) > 0.02:
		stick = read
		host.run_lua("Skate.stick(%.3f, %.3f)" % [stick.x, stick.y])
	if hop_sound == null and land_sound == null:
		_load_sounds()
	_read_tricks()
	_update_sounds(delta, true)
	flash = maxf(0.0, flash - delta)
	canvas.queue_redraw()
	# A bail leaves the board where it was.
	if _value("skate.bailing") >= 0.5:
		sparks.emitting = false
		return
	var hero = host.node_for(host.hero(player))
	if not hero is Node3D:
		board.visible = false
		return
	if board.mesh == null:
		board.mesh = load(MODEL) as Mesh
	board.global_transform = _board_transform(hero.global_position, delta)
	board.visible = true
	sparks.global_position = hero.global_position + Vector3.DOWN * 0.03
	sparks.emitting = int(_value("skate.balance")) == 2
	_pose_rider(delta, hero, _value("skate.heading"))


## Puts the rider's pose on the hero's skeleton, the first time it's found.
func _attach_rider(hero: Node) -> void:
	if rider_failed or (rider != null and is_instance_valid(rider) and rider.get_parent() != null):
		return
	for found in hero.find_children("Skeleton", "Skeleton3D", true, false):
		var skeleton := found as Skeleton3D
		rider = Rider.new()
		if rider.find_bones(skeleton):
			skeleton.add_child(rider)
			print("skate: rider posing")
		else:
			rider = null
			rider_failed = true
			push_warning("skate: the hero's skeleton lacks the bones to pose")
		return


func _pose_rider(delta: float, hero: Node3D, heading: float) -> void:
	_attach_rider(hero)
	if rider == null or not is_instance_valid(rider):
		return
	var balance := int(_value("skate.balance"))
	var air := _value("skate.air") >= 0.5
	var bailing := _value("skate.bailing") >= 0.5
	rider.active = not bailing
	# The engine leaves its skeletons' modifier pass off, so it's asked for while riding.
	rider.get_skeleton().modifier_callback_mode_process = Skeleton3D.MODIFIER_CALLBACK_MODE_PROCESS_IDLE
	var wanted := CROUCH_REST + CROUCH_SPEED * clampf(_value("skate.speed") / TOP_PUSH, 0.0, 1.0)
	if int(_value("skate.grab")) != 0:
		wanted = CROUCH_GRAB
	elif balance == 2:
		wanted = CROUCH_GRIND
	elif balance == 1:
		wanted = CROUCH_MANUAL
	elif air:
		wanted = CROUCH_AIR
	crouch_speed += (SPRING * (wanted - crouch) - SPRING_DAMP * crouch_speed) * delta
	crouch = clampf(crouch + crouch_speed * delta, 0.0, 1.0)
	var ease := minf(1.0, delta * POSE_EASE)
	rider.crouch = crouch
	rider.feet_lift = feet_up
	rider.deck_tilt = deck_shown
	rider.nose = Vector3(-cos(heading), 0.0, sin(heading))
	rider.toes = Vector3(-sin(heading), 0.0, -cos(heading))
	rider.turn = lerpf(rider.turn, _value("skate.roll"), ease)
	rider.manual = lerpf(rider.manual, 1.0 if balance == 1 else 0.0, ease)
	rider.arms = lerpf(rider.arms, 1.0, ease)
	rider.push = lerpf(rider.push, _value("skate.pushing"), ease)
	if rider.push > 0.01:
		rider.push_phase += delta * PUSH_RATE
	var grabbing := int(_value("skate.grab"))
	rider.grab = lerpf(rider.grab, 1.0 if grabbing != 0 else 0.0, minf(1.0, delta * GRAB_EASE))
	if grabbing != 0:
		var reach: Vector3 = board.global_transform.basis.x * 0.1
		match grabbing:
			3:
				reach = board.global_transform.basis.z * -0.42
			4:
				reach = board.global_transform.basis.z * 0.42
			2:
				reach = board.global_transform.basis.x * -0.1
		rider.grab_at = board.global_position + reach
		rider.grab_hand = 1 if grabbing == 1 or grabbing == 3 else -1


func _read_tricks() -> void:
	var seen := _value("skate.hop_seq")
	if seen != hop_seen:
		hop_seen = seen
		_shot(hop_sound)
		crouch_speed = HOP_KICK
	seen = _value("skate.land_seq")
	if seen != land_seen:
		land_seen = seen
		_shot(land_sound)
		crouch_speed = LAND_KICK
	seen = _value("skate.flip_seq")
	if seen != flip_seen:
		flip_seen = seen
		flip_kind = int(_value("skate.flip"))
		flip_left = FLIP_TIME
		_shot(flourish_sound)
	var grab := int(_value("skate.grab"))
	if grab != 0 and grab_was == 0:
		_shot(grab_sound)
	grab_was = grab
	seen = _value("skate.trick_seq")
	if seen != trick_seen:
		trick_seen = seen
		var id := int(_value("skate.trick"))
		line.append(host.text(NAMES[id]) if NAMES.has(id) else "%d" % id)
		if id == GUILD_GRINDER:
			flash = FLASH_TIME
			flash_text = host.text("SKATE_GUILD_GRINDER_CALLOUT")
			flash_colour = GOLD
			flash_size = 1.3
	if _value("skate.count") == 0.0:
		line.clear()
	seen = _value("skate.landed_seq")
	if seen != landed_seen:
		landed_seen = seen
		var amount := int(_value("skate.landed"))
		flash = FLASH_TIME
		var sketchy := _value("skate.sketchy") >= 0.5
		if amount < 0:
			_shot(thrown_sound)
			flash_text = host.text("SKATE_CRASH")
			flash_colour = BAD
		elif sketchy:
			flash_text = host.text("SKATE_CLOSE_CALL") if amount == 0 else host.text("SKATE_CLOSE_CALL_POINTS", [amount])
			flash_colour = GOLD
		else:
			flash_text = "+%d" % amount
			flash_colour = GOOD
		flash_size = _grown(maxf(0.0, amount))


func _board_transform(feet: Vector3, delta: float) -> Transform3D:
	var heading := _value("skate.heading")
	# The model's nose is its +Z here, and looking_at aims -Z, so it is aimed away to lead with the nose.
	var along := Vector3(cos(heading), 0.0, -sin(heading))
	var basis := Basis.looking_at(along, Vector3.UP)
	var drop := 0.0
	var flip_up := 0.0
	if flip_left > 0.0:
		flip_left = maxf(0.0, flip_left - delta)
		var done := 1.0 - flip_left / FLIP_TIME
		var turn := TAU * done
		match flip_kind:
			2:
				basis = basis.rotated(basis.z, -turn)
			3:
				basis = basis.rotated(basis.y, turn)
			4:
				basis = basis.rotated(basis.x, turn)
			_:
				basis = basis.rotated(basis.z, turn)
		drop = sin(done * PI) * FLIP_DROP
		flip_up = sin(done * PI) * FLIP_TUCK
	var wanted := int(_value("skate.grab"))
	if wanted != 0:
		grab_kind = wanted
	grab_shown = move_toward(grab_shown, 1.0 if wanted != 0 else 0.0, delta * GRAB_EASE)
	air_shown = move_toward(air_shown, 1.0 if _value("skate.air") >= 0.5 else 0.0, delta * GRAB_EASE)
	var lift := grab_shown * GRAB_LIFT + air_shown * AIR_TUCK * (1.0 - grab_shown)
	var ease := minf(1.0, delta * TILT_EASE)
	pitch_shown = lerpf(pitch_shown, _value("skate.pitch"), ease)
	roll_shown = lerpf(roll_shown, _value("skate.roll") * ROLL_MOST, ease)
	basis = basis.rotated(basis.x, -pitch_shown).rotated(basis.z, roll_shown)
	deck_shown = pitch_shown
	manual_shown = move_toward(manual_shown, 1.0 if int(_value("skate.balance")) == 1 else 0.0, delta * MANUAL_EASE)
	if manual_shown > 0.0:
		basis = basis.rotated(basis.x, -(MANUAL_TILT + _value("skate.lean") * MANUAL_TILT * 0.4) * manual_shown)
		lift += MANUAL_LIFT * manual_shown
		deck_shown += (MANUAL_TILT + _value("skate.lean") * MANUAL_TILT * 0.4) * manual_shown
	feet_up = lift + flip_up
	if grab_shown > 0.0:
		match grab_kind:
			2:
				basis = basis.rotated(basis.z, -GRAB_TILT * grab_shown)
			3:
				basis = basis.rotated(basis.x, -GRAB_TILT * grab_shown)
			4:
				basis = basis.rotated(basis.x, GRAB_TILT * grab_shown)
			_:
				basis = basis.rotated(basis.z, GRAB_TILT * grab_shown)
	return Transform3D(basis, feet + Vector3.UP * (lift - drop))


func _draw_controls(scale: float) -> void:
	var corner := PANEL_AT * scale
	# Larger on a pad, where a row carries the game's button pictures.
	var row := (PANEL_ROW_PAD if on_pad else PANEL_ROW) * scale
	var size := int((24 if on_pad else 20) * scale)
	var keys_at := (PANEL_KEYS_AT_PAD if on_pad else PANEL_KEYS_AT) * scale
	var panel := Rect2(corner - Vector2(12.0, 28.0) * scale, Vector2((PANEL_WIDTH_PAD if on_pad else PANEL_WIDTH) * scale, row * (CONTROLS.size() + 1.25)))
	canvas.draw_rect(panel, Color(0.0, 0.0, 0.0, 0.38))
	canvas.draw_string(face, corner, host.text("SKATE_CONTROLS_HINT"), HORIZONTAL_ALIGNMENT_LEFT, -1, int(16 * scale), Color(GOLD, 0.8))
	for i in CONTROLS.size():
		var at := corner + Vector2(0.0, row * (i + 1))
		var entry: Array = CONTROLS[i]
		canvas.draw_string(face, at, host.text(entry[0]), HORIZONTAL_ALIGNMENT_LEFT, -1, size, Color(WHITE, 0.72))
		_draw_how(at + Vector2(keys_at, 0.0), entry[2] if on_pad else entry[1], size, Color(GOLD, 0.95))


## One control's how: its text, or its pattern with each %N drawn as that bound input.
func _draw_how(at: Vector2, how: Dictionary, size: int, colour: Color) -> void:
	if how.has("text"):
		canvas.draw_string(face, at, host.text(how.text), HORIZONTAL_ALIGNMENT_LEFT, -1, size, colour)
		return
	var binds: Array = how.get("binds", [])
	var pattern: String = CHORD if how.get("chord", false) else (host.text(how.pattern) if how.has("pattern") else BOUND)
	var x := at.x
	var from := 0
	for found: RegExMatch in marker.search_all(pattern):
		x += _words(Vector2(x, at.y), pattern.substr(from, found.get_start() - from), size, colour)
		var which := found.get_string(1).to_int() - 1
		if which >= 0 and which < binds.size():
			x += _bound(Vector2(x, at.y), binds[which], size, colour)
		from = found.get_end()
	_words(Vector2(x, at.y), pattern.substr(from), size, colour)


func _words(at: Vector2, text: String, size: int, colour: Color) -> float:
	if text.is_empty():
		return 0.0
	canvas.draw_string(face, at, text, HORIZONTAL_ALIGNMENT_LEFT, -1, size, colour)
	return face.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1, size).x


## What an action or a pad button is bound to now, on the device in use: its glyph standing on the line,
## or its name where there is no glyph. The width taken.
func _bound(at: Vector2, name: String, size: int, colour: Color) -> float:
	var glyph: Texture2D = host.input_glyph(name)
	if glyph == null:
		return _words(at, host.input_label(name), size, colour)
	var high := size * (1.6 if on_pad else 1.2)
	var wide := high * glyph.get_width() / maxf(1.0, glyph.get_height())
	canvas.draw_texture_rect(glyph, Rect2(at.x, at.y - size * 0.35 - high * 0.5, wide, high), false)
	return wide


## How much larger a score shows, from 1 up to half as big again.
func _grown(points: float) -> float:
	return 1.0 + 0.5 * clampf(points / BIG_COMBO, 0.0, 1.0)


func _text(at: Vector2, text: String, size: int, colour: Color) -> void:
	var width := canvas.size.x
	canvas.draw_string_outline(face, Vector2(0.0, at.y), text, HORIZONTAL_ALIGNMENT_CENTER, width, size, maxi(2, size / 8), Color(0.0, 0.0, 0.0, colour.a))
	canvas.draw_string(face, Vector2(0.0, at.y), text, HORIZONTAL_ALIGNMENT_CENTER, width, size, colour)


func _draw_score() -> void:
	if _value("skate.on") < 0.5 or not _in_play():
		return
	var scale := canvas.size.y / 1080.0
	# On a touchscreen the pad's own buttons are captioned with the tricks (input.json's "touch").
	if not host.touch_mode():
		_draw_controls(scale)
	# Top left, clear of the objective above the middle and the map on the right. On a touchscreen the
	# health stands there (lower still under a corner's camera) and the objective under the top row: the
	# score goes down the left edge, between the touch controls' Lock and the HUD's items.
	var score_at := SCORE_AT * scale
	if host.touch_mode():
		score_at = TOUCH_SCORE_AT * canvas.size.y / 720.0
	var score_size := int(34 * scale)
	var score := "%d" % int(_value("skate.total"))
	canvas.draw_string_outline(face, score_at, score, HORIZONTAL_ALIGNMENT_LEFT, -1, score_size, maxi(2, score_size / 8), Color.BLACK)
	canvas.draw_string(face, score_at, score, HORIZONTAL_ALIGNMENT_LEFT, -1, score_size, GOLD)
	var count := int(_value("skate.count"))
	if not line.is_empty():
		var from := maxi(0, line.size() - SHOWN)
		_text(Vector2(0.0, 760.0 * scale), " + ".join(line.slice(from)), int(40 * scale), WHITE)
	if count > 0:
		var so_far := _value("skate.combo") * count
		var shade := GOLD.lerp(Color(1.0, 0.45, 0.2), clampf(so_far / BIG_COMBO, 0.0, 1.0))
		_text(Vector2(0.0, 815.0 * scale), host.text("SKATE_COMBO", [int(_value("skate.combo")), count]), int(46 * scale * _grown(so_far)), shade)
	if flash > 0.0:
		var shade := flash_colour
		shade.a = minf(1.0, flash / (FLASH_TIME * 0.4))
		_text(Vector2(0.0, 700.0 * scale), flash_text, int(72 * scale * flash_size), shade)
	var kind := int(_value("skate.balance"))
	if kind != 0:
		_draw_meter(kind, clampf(_value("skate.lean"), -1.0, 1.0), scale)


## The balance: across for a grind, whose stick is left and right, and upright for a manual.
func _draw_meter(kind: int, lean: float, scale: float) -> void:
	var size := METER * scale
	var centre := Vector2(canvas.size.x * 0.5, 862.0 * scale)
	var danger := absf(lean)
	var mark := WHITE.lerp(BAD, danger * danger)
	if kind == 2:
		canvas.draw_rect(Rect2(centre - size * 0.5, size), Color(0.0, 0.0, 0.0, 0.6))
		var at := centre + Vector2(lean * size.x * 0.5, 0.0)
		canvas.draw_rect(Rect2(at - Vector2(5.0, size.y) * scale, Vector2(10.0, size.y * 2.0) * scale), mark)
	else:
		var upright := Vector2(size.y, size.x * 0.6)
		var corner := Vector2(canvas.size.x * 0.5 + 280.0 * scale, 780.0 * scale)
		canvas.draw_rect(Rect2(corner - upright * 0.5, upright), Color(0.0, 0.0, 0.0, 0.6))
		var at := corner + Vector2(0.0, -lean * upright.y * 0.5)
		canvas.draw_rect(Rect2(at - Vector2(upright.x, 5.0 * scale), Vector2(upright.x * 2.0, 10.0 * scale)), mark)
