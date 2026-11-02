extends Node
## First Person: the switch, per player.
##
## The view is the game's own first-person camera held through camera_override: the game's
## per-player camera override (the one its quests use for their first-person shots), which yields to
## a scene's own camera, steps aside for full cutscenes and canned actions, and never reaches a save.
## A hold names the player, so each player of a split screen or an online game has their own.
##
## mod/camera.lua adjusts that camera for the players who hold it. It runs in the game VM on the
## camera's own update, so it reads the hold where it reads everything else: the player value
## first_person.held (1 held, 0 not) in the player store, written here for each player this machine
## plays (player_here) — the store the game VM reads synchronously, kept across a Lua reload, and the
## same on both co-op machines.
##
## Holds are cleared each time the mods' Lua loads, so they are stated again on lua_ready; a new game
## VM also starts each player's toggle again as start_on says.

const HELD := "first_person.held"
const HOLD_SETTINGS := ["first_person.fov", "first_person.pitch_limit", "first_person.third_person_aim", "first_person.scenes"]

var host: Object
var first_person := 8
var interactive := 2
var on := [false, false, false]     # per player (1, 2): whether they want first person (the toggle)
var stated := ["", "", ""]          # per player: what was last stated, so an unchanged hold is not stated again


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	var named: Dictionary = host.constants()
	first_person = int(named.get("CAMERA_FIRST_PERSON", first_person))
	interactive = int(named.get("CUTSCENE_INTERACTIVE", interactive))
	host.input_action.connect(_on_input_action)
	host.setting_changed.connect(_on_setting_changed)
	host.cutscene.connect(_on_cutscene)
	host.hero_ready.connect(func(_e: Dictionary) -> void: _apply_both())
	host.hero_gone.connect(func(_e: Dictionary) -> void: _apply_both())
	host.lua_ready.connect(func(_e: Dictionary) -> void: _start())
	_start()


func _setting(name: String, fallback: float) -> float:
	return host.setting("first_person." + name, fallback)


# A session's start, and every new game VM: the toggles as start_on says, every hold stated afresh.
func _start() -> void:
	var start := _setting("start_on", 1.0) != 0.0
	on = [false, start, start]
	stated = ["", "", ""]
	_apply_both()


# A shared screen is one camera framing both heroes, so neither player holds it there.
func _own_camera() -> bool:
	return host.coop_mode() != 1 or host.split_screen()


# With the scenes setting off an interactive scene (a conversation under the vignette) keeps the
# game's own camera.
func _wanted(p: int) -> bool:
	if not on[p] or not _own_camera():
		return false
	if _setting("scenes", 1.0) == 0.0 and host.in_cutscene(p, interactive):
		return false
	return true


# States player p's hold, or lets it go.
func _apply(p: int) -> void:
	var held := _wanted(p)
	var fov := _setting("fov", 80.0)
	var pitch := _setting("pitch_limit", 75.0)
	var aim := _setting("third_person_aim", 1.0) != 0.0
	if host.player_here(p):
		host.player_set(p, HELD, 1.0 if held else 0.0)
	var now := "%s/%s/%s" % [fov, pitch, aim] if held else "off"
	if stated[p] == now:
		return
	stated[p] = now
	if held:
		host.camera_override(p, first_person, { "fov": fov, "pitch_limit": pitch, "aim": aim })
	else:
		host.camera_override(p, 0)


func _apply_both() -> void:
	_apply(1)
	_apply(2)


# H (input.json "toggle", rebindable in Options > Controls): whoever pressed it.
func _on_input_action(e: Dictionary) -> void:
	var p := int(e.get("player", 0))
	if e.get("name", "") == "first_person.toggle" and e.get("pressed", false) and (p == 1 or p == 2):
		on[p] = not on[p]
		_apply(p)
		print("first person %s for player %d" % ["on" if on[p] else "off", p])


# A hold's options are stated again when the setting behind one changes.
func _on_setting_changed(key: String, _value: float) -> void:
	if key in HOLD_SETTINGS:
		_apply_both()


# An interactive scene starting or ending (the scenes setting).
func _on_cutscene(e: Dictionary) -> void:
	var p := int(e.get("player", 0))
	if int(e.get("kind", 0)) == interactive and (p == 1 or p == 2):
		_apply(p)
