extends Node
## Toggle Sprint: the Shift tap that ends a sprint after standing still.
##
## mod/init.lua publishes toggle_sprint.park_request (a counter) when player one's hero has stood
## still for stop_after seconds with the sprint held. The game ends a sprint only on the button's
## release, which a mod cannot send, so this taps Shift through Godot's input: the game reads the
## keyboard's sprint from Godot's key state (GameInput.KeyboardMouse.cs), the tap reaches the hero
## as SPRINT_SET and SPRINT_RELEASE, and init.lua treats the pair as the second press — the SET is
## cancelled and the RELEASE ends the sprint.

const REQUEST := "toggle_sprint.park_request"
const HOLD_SECONDS := 0.15   # a few guest ticks (30 Hz), so the game samples the key down

var host: Object
var seen := 0.0
var release_in := -1.0


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	var value: float = host.get_value(REQUEST)
	seen = 0.0 if is_nan(value) else value   # a request from before a reload is not ours to answer


func _process(delta: float) -> void:
	if release_in >= 0.0:
		release_in -= delta
		if release_in < 0.0:
			_shift(false)
		return
	var value: float = host.get_value(REQUEST)
	if is_nan(value) or value <= seen:
		return
	seen = value
	_shift(true)
	release_in = HOLD_SECONDS


func _exit_tree() -> void:
	if release_in >= 0.0:
		_shift(false)


func _shift(down: bool) -> void:
	var key := InputEventKey.new()
	key.keycode = KEY_SHIFT
	key.physical_keycode = KEY_SHIFT
	key.pressed = down
	Input.parse_input_event(key)
