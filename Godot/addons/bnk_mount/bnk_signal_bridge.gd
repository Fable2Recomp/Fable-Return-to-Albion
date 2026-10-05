@tool
extends RefCounted

## Intercepts and filters FileSystem dock tree selection signals for mounted Game:// assets.
##
## Standard editor docks handle activation, mouse selection, and multi-selection with default
## filesystem behaviors. This bridge intercepts those signals, redirecting activation and right-clicks
## on Game:// items to our custom viewer/exporter handlers while forwarding non-game items untouched.

var _tree: Tree = null
var _is_ours: Callable
var _on_activate: Callable
var _on_rmb: Callable
var _originals := {}

## Installs signal hooks onto the target Tree control.
func install(tree: Tree, is_ours: Callable, on_activate: Callable, on_rmb: Callable = Callable()) -> void:
	if _tree != null and is_instance_valid(_tree):
		uninstall()
	_tree = tree
	_is_ours = is_ours
	_on_activate = on_activate
	_on_rmb = on_rmb
	_steal("item_activated", _on_item_activated)
	_steal("item_mouse_selected", _on_item_mouse_selected)
	_steal("multi_selected", _on_multi_selected)

## Restores original signal connections and releases the tree.
func uninstall() -> void:
	if _tree == null or not is_instance_valid(_tree):
		_tree = null
		_originals.clear()
		return
	for signal_name in _originals.keys():
		for c in _tree.get_signal_connection_list(signal_name):
			var callable: Callable = c.get("callable", Callable())
			if callable.get_object() == self:
				_tree.disconnect(signal_name, callable)
		for original in _originals[signal_name]:
			if original is Callable and original.is_valid() and not _tree.is_connected(signal_name, original):
				_tree.connect(signal_name, original)
	_originals.clear()
	_tree = null

## Disconnects existing connections for a signal, saves them, and connects our intercept handler.
func _steal(signal_name: String, handler: Callable) -> void:
	var taken := []
	for c in _tree.get_signal_connection_list(signal_name):
		var callable: Callable = c.get("callable", Callable())
		if callable.get_object() == self:
			continue
		taken.append(callable)
		if callable.is_valid() and _tree.is_connected(signal_name, callable):
			_tree.disconnect(signal_name, callable)
	_originals[signal_name] = taken
	if not _tree.is_connected(signal_name, handler):
		_tree.connect(signal_name, handler)

## Returns true if the currently selected tree item belongs to the Game:// mount.
func _ours() -> bool:
	if _tree == null or not is_instance_valid(_tree) or not _is_ours.is_valid():
		return false
	var item := _tree.get_selected()
	return item != null and _is_ours.call(item)

## Forwards intercepted signals to original receivers when the event did not target a Game:// asset.
func _forward(signal_name: String, args: Array) -> void:
	for c in _originals.get(signal_name, []):
		if c is Callable and c.is_valid():
			c.callv(args)

func _get_item_meta(item: TreeItem) -> String:
	if item == null:
		return ""
	if item.has_meta("__bnk_meta__"):
		return str(item.get_meta("__bnk_meta__"))
	return str(item.get_metadata(0))

func _is_tu_or_gdb(item: TreeItem) -> bool:
	if item == null:
		return false
	var text := item.get_text(0)
	var meta := _get_item_meta(item)
	return text.ends_with(".tu") or meta.ends_with(".tu") or text.ends_with(".gdb") or meta.ends_with(".gdb")

## Double-click / Enter activation handler.
func _on_item_activated() -> void:
	var item := _tree.get_selected() if _tree != null and is_instance_valid(_tree) else null
	if item != null and (_ours() or _is_tu_or_gdb(item)):
		if _on_activate.is_valid():
			_on_activate.call(item)
		return
	_forward("item_activated", [])

## Mouse selection handler, capturing right-clicks on Game:// or .tu/.gdb items to trigger the mod context menu.
func _on_item_mouse_selected(position: Vector2, mouse_button_index: int) -> void:
	if _tree == null or not is_instance_valid(_tree):
		return
	var item := _tree.get_item_at_position(position)
	if item == null:
		item = _tree.get_selected()
	if item != null and (_ours() or _is_tu_or_gdb(item)):
		if mouse_button_index == MOUSE_BUTTON_RIGHT and _on_rmb.is_valid():
			item.select(0)
			var mouse_pos: Vector2
			var win := _tree.get_window()
			if win != null and not win.is_embedded():
				mouse_pos = Vector2(DisplayServer.mouse_get_position())
			else:
				mouse_pos = _tree.get_global_mouse_position()
			_on_rmb.call(item, mouse_pos)
			return
		if _ours():
			return
	_forward("item_mouse_selected", [position, mouse_button_index])

## Suppresses multi-selection processing for Game:// items.
func _on_multi_selected(item: TreeItem, column: int, selected: bool) -> void:
	if item != null and _is_ours.is_valid() and _is_ours.call(item):
		return
	_forward("multi_selected", [item, column, selected])
