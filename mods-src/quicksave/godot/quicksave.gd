extends Node
## Quicksave: F8 saves and keeps a quicksave, F9 loads it back without leaving the game.
##
## F8 asks mod/init.lua for the game's own save (AutoSaveGame, where the game allows one); once the
## save is written (the host's save_written) the save folder is copied to this mod's snapshot
## beside it (host.save_snapshot), never in the Load Game list. F9 loads that snapshot through the
## game's own in-game reload (host.load_snapshot), loading screen and all; the save folder gets its
## own files back once the level stands, so a quickload leaves the player's save as it was.
##
## In play only: the keys do nothing in menus, on loading screens or while a save or load is under
## way, and the quickload is refused during a cutscene. Alone only: a load in co-op would leave the partner
## behind.

const KEY := "quicksave.quick"
const SAVE_WAIT := 30.0             # seconds the game's save may take before it counts as failed
const TITLE := "Quicksave"

var host: Object
var in_menu := false                # a menu, a screen or the game's pause is up
var loading := false                # between a level going and the next standing
var ask := 0                        # the last save asked of init.lua
var saving := false                 # asked, and the game's answer or its save not yet in
var answered := false               # init.lua said it is saving
var waited := 0.0
var quickloading := false           # a quickload's level not yet up


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	host.input_action.connect(_on_input_action)
	host.save_written.connect(_on_save_written)
	host.menu_opened.connect(func(_e): in_menu = true)
	host.menu_closed.connect(func(_e): in_menu = false)
	host.paused.connect(func(e): in_menu = e.get("paused", false))
	host.level_unloading.connect(func(_e): loading = true)
	host.level_loaded.connect(_on_level_loaded)
	# A number of its own per session, so an answer from an earlier run is never taken for this one.
	ask = int(Time.get_unix_time_from_system()) % 1000000


func _on_input_action(e: Dictionary) -> void:
	if e.get("replica", false) or not e.get("pressed", false) or int(e.get("player", 1)) != 1:
		return
	match e.get("name", ""):
		"quicksave.quicksave":
			_quicksave()
		"quicksave.quickload":
			_quickload()


func _busy() -> bool:
	return in_menu or loading or saving or quickloading


func _quicksave() -> void:
	if _busy():
		return
	if host.coop_mode() != 0:
		_notice("Quicksave works in a single-player game only.")
		return
	ask += 1
	saving = true
	answered = false
	waited = 0.0
	host.run_lua("Quicksave.save(%d)" % ask)


func _quickload() -> void:
	if _busy():
		return
	if host.coop_mode() != 0:
		_notice("Quickload works in a single-player game only.")
		return
	var taken: int = host.snapshot_time(KEY)
	if taken == 0:
		_notice("There is no quicksave yet. Press F8 to make one.")
		return
	if host.in_cutscene(1, 7):
		_notice("You can't quickload right now.")
		return
	if host.load_snapshot(KEY):
		quickloading = true
	else:
		_notice("The quicksave could not be loaded.")


func _process(delta: float) -> void:
	if not saving:
		return
	waited += delta
	if not answered:
		var seq: float = host.get_value("quicksave.seq")
		if not is_nan(seq) and int(seq) == ask:
			if int(host.get_value("quicksave.answer")) == 1:
				answered = true
			else:
				saving = false
				_notice("You can't save right now.")
				return
	if waited > SAVE_WAIT:
		saving = false
		_notice("The game did not save. Nothing was quicksaved.")


func _on_save_written(e: Dictionary) -> void:
	if not saving or int(e.get("player", 0)) != 1:
		return
	saving = false
	if host.save_snapshot(KEY):
		_notice("Quicksaved.")
	else:
		_notice("The game saved, but the quicksave could not be kept.")


func _on_level_loaded(_e: Dictionary) -> void:
	loading = false
	if not quickloading:
		return
	quickloading = false
	_notice("Quickloaded the save from %s." % _clock(host.snapshot_time(KEY)))


func _notice(text: String) -> void:
	host.notice(TITLE, text, 3.0, 0)


# A unix time as the local hour and minute.
func _clock(unix: int) -> String:
	var bias: int = Time.get_time_zone_from_system().get("bias", 0)
	var t := Time.get_datetime_dict_from_unix_time(unix + bias * 60)
	return "%02d:%02d" % [t.hour, t.minute]
