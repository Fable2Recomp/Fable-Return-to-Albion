extends Node
## Toggle Sprint: one press starts the sprint, the next press ends it, and standing still for a
## while ends it too. One latch per player, so each co-op hero toggles on their own.
##
## THE TOGGLE IS MADE OF CANCELS (a game_action verdict). The game sprints while A is held (Shift on
## the keyboard holds A): the hero's receiver gets ACTION_SPRINT_SET (66) when it goes down and
## ACTION_SPRINT_RELEASE (67) when it comes up, and the release ends a sprint only while the sprint
## component's flag is set. So:
##   SET while sprinting       the second press: cancelled, and its RELEASE passes and ends the sprint;
##   SET while not sprinting   a fresh press: passes, and the game starts the sprint;
##   any other RELEASE         cancelled: the game keeps the sprint after the button comes up.
## The flag is the game's own, read inside the verdict (host.read "sprinting") as it stands when the
## press arrives, so a sprint the game ended itself makes the next press a fresh one.
##
## STANDING STILL. After stop_after seconds with the hero's own velocity (query velocity) under
## STILL_SPEED, end_sprint(p) runs the release's own end: while the flag is set, the flag cleared and
## the sprint movement stopped, so the next step is a walk until the next press (nothing without it).
## A press starts the count again. Where the hero is played (player_here) and while that player is
## in play (in_play).

const STILL_SPEED := 1.0   # metres a second across the ground: slower than a walk, so the slide to a stop counts

var host: Object
var sprint_set := 66
var sprint_release := 67

# Per player, indexed 1 and 2. Fixed size: the verdict reads and writes them on the game's thread.
var ending := [false, false, false]      # the second press's SET was cancelled, its RELEASE ends the sprint
var still := [0.0, 0.0, 0.0]             # seconds stood still since the last press
var slow := [false, false, false]        # the last velocity answer was under STILL_SPEED


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	var named: Dictionary = host.constants()
	sprint_set = int(named.get("ACTION_SPRINT_SET", sprint_set))
	sprint_release = int(named.get("ACTION_SPRINT_RELEASE", sprint_release))
	host.on_verdict("game_action", _on_game_action)
	host.tick.connect(_on_tick)
	host.level_loaded.connect(_on_level_loaded)


func _exit_tree() -> void:
	if is_instance_valid(host):
		host.remove_verdict("game_action", _on_game_action)


# The verdict, on the game's thread inside the seam: it computes and reads, nothing else.
func _on_game_action(e: Dictionary) -> bool:
	var action := int(e.get("action", 0))
	if action != sprint_set and action != sprint_release:
		return false
	var p := int(e.get("player", 0))
	if p != 1 and p != 2:
		return false
	if action == sprint_set:
		still[p] = 0.0
		ending[p] = bool(host.read("sprinting", [p]).get("sprinting", false))
		return ending[p]                     # cancelled while sprinting; a fresh press starts it
	if ending[p]:
		ending[p] = false                    # the second release ends the sprint
		return false
	return true                              # keep the sprint going after the button comes up


func _on_tick(e: Dictionary) -> void:
	var seconds := float(e.get("seconds", 0.0))
	if seconds <= 0.0:
		return
	var limit: float = host.setting("toggle_sprint.stop_after", 0.5)
	for p in [1, 2]:
		var hero: int = host.hero(p)
		if limit <= 0.0 or hero == 0 or not host.player_here(p) or not host.in_play(p):
			still[p] = 0.0
			continue
		_ask(p, hero)
		if slow[p]:
			still[p] += seconds
			if still[p] >= limit:
				still[p] = 0.0
				host.end_sprint(p)
		else:
			still[p] = 0.0


# The hero's velocity, as the guest answers it this tick.
func _ask(p: int, hero: int) -> void:
	host.ask("velocity", [hero], func(r: Dictionary) -> void:
		var vx := float(r.get("x", 0.0))
		var vy := float(r.get("y", 0.0))
		slow[p] = bool(r.get("ok", false)) and vx * vx + vy * vy < STILL_SPEED * STILL_SPEED)


# A level change or a save load starts without anything held.
func _on_level_loaded(_e: Dictionary) -> void:
	for p in [1, 2]:
		ending[p] = false
		still[p] = 0.0
		slow[p] = false
