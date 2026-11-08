extends Node
## Quicksave: F8 saves and keeps a quicksave, F9 loads it back without leaving the game.
##
## F8 asks mod/init.lua for the game's own save (Refii.save_game, where the game allows one), which
## answers with the signal quicksave.answer; once the save is written (save_written, ok) the save
## folder is copied to this mod's snapshot beside it (host.save_snapshot), never in the Load Game
## list. F9 loads that snapshot through the game's own in-game reload (host.load_snapshot), loading
## screen and all; the save folder gets its own files back once the level stands, so a quickload
## leaves the player's save as it was.
##
## In play only (host.in_play): the keys do nothing in menus, on loading screens or while a save or
## load is under way, and the quickload is refused during a cutscene. Alone only: a snapshot is the
## player-one save, and a load in co-op would leave the partner behind.
##
## Every message is a key of language/<culture>/text/book.babel.csv, read in the player's language
## through host.text; the notice's title names the mod's own {QUICKSAVE_MOD_NAME}.

const KEY := "quicksave.quick"
const TITLE := "{QUICKSAVE_MOD_NAME}"

var host: Object
var asked := false                  # init.lua asked to save, its answer not yet in
var saving := false                 # the game is saving, its save_written not yet in
var quickloading := false           # a quickload's level not yet up


func _ready() -> void:
	host = Engine.get_singleton("RefiiHost")
	host.input_action.connect(_on_input_action)
	host.mod_signal.connect(_on_mod_signal)
	host.save_written.connect(_on_save_written)
	host.level_loaded.connect(_on_level_loaded)


func _on_input_action(e: Dictionary) -> void:
	if e.get("replica", false) or not e.get("pressed", false) or int(e.get("player", 1)) != 1:
		return
	match e.get("name", ""):
		"quicksave.quicksave":
			_quicksave()
		"quicksave.quickload":
			_quickload()


func _busy() -> bool:
	return asked or saving or quickloading or not host.in_play(1)


func _quicksave() -> void:
	if _busy():
		return
	if host.coop_mode() != 0:
		_notice("QUICKSAVE_SAVE_ALONE_ONLY")
		return
	asked = host.run_lua("Quicksave.save(1)")


func _quickload() -> void:
	if _busy():
		return
	if host.coop_mode() != 0:
		_notice("QUICKSAVE_LOAD_ALONE_ONLY")
		return
	var taken: int = host.snapshot_time(KEY)
	if taken == 0:
		_notice("QUICKSAVE_NONE_YET")
		return
	if host.in_cutscene(1):
		_notice("QUICKSAVE_CANT_LOAD_NOW")
		return
	if host.load_snapshot(KEY):
		quickloading = true
	else:
		_notice("QUICKSAVE_LOAD_FAILED")


func _on_mod_signal(mod: String, name: String, payload: String) -> void:
	if mod != "quicksave" or name != "answer" or not asked:
		return
	asked = false
	if payload == "saving":
		saving = true
	else:
		_notice("QUICKSAVE_CANT_SAVE_NOW")


func _on_save_written(e: Dictionary) -> void:
	if not saving or int(e.get("player", 0)) != 1:
		return
	saving = false
	if not e.get("ok", true):
		_notice("QUICKSAVE_GAME_NOT_SAVED")
	elif host.save_snapshot(KEY):
		_notice("QUICKSAVE_SAVED")
	else:
		_notice("QUICKSAVE_NOT_KEPT")


# A new level: nothing asked before it is still coming.
func _on_level_loaded(_e: Dictionary) -> void:
	asked = false
	saving = false
	if not quickloading:
		return
	quickloading = false
	_notice("QUICKSAVE_LOADED", [_clock(host.snapshot_time(KEY))])


# A notice: `key` a text key, %1 ... filled from `args`.
func _notice(key: String, args: Array = []) -> void:
	host.notice(TITLE, host.text(key, args), 3.0)


# A unix time as the local hour and minute.
func _clock(unix: int) -> String:
	var bias: int = Time.get_time_zone_from_system().get("bias", 0)
	var t := Time.get_datetime_dict_from_unix_time(unix + bias * 60)
	return "%02d:%02d" % [t.hour, t.minute]
