@tool
extends RefCounted

var _tree: Tree = null
var _is_ours: Callable
var _on_activate: Callable
var _originals := {}

func install(tree: Tree, is_ours: Callable, on_activate: Callable) -> void:
	_tree = tree
	_is_ours = is_ours
	_on_activate = on_activate
	_steal("item_activated", _on_item_activated)
	_steal("item_mouse_selected", _on_item_mouse_selected)
	_steal("multi_selected", _on_multi_selected)

func _steal(signal_name: String, handler: Callable) -> void:
	var taken := []
	for c in _tree.get_signal_connection_list(signal_name):
		var callable: Callable = c["callable"]
		taken.append(callable)
		_tree.disconnect(signal_name, callable)
	_originals[signal_name] = taken
	_tree.connect(signal_name, handler)

func _ours() -> bool:
	var item := _tree.get_selected()
	return item != null and _is_ours.call(item)

func _forward(signal_name: String, args: Array) -> void:
	for c in _originals.get(signal_name, []):
		c.callv(args)

func _on_item_activated() -> void:
	if _ours():
		_on_activate.call(_tree.get_selected())
		return
	_forward("item_activated", [])

func _on_item_mouse_selected(position: Vector2, mouse_button_index: int) -> void:
	if _ours():
		return
	_forward("item_mouse_selected", [position, mouse_button_index])

func _on_multi_selected(item: TreeItem, column: int, selected: bool) -> void:
	if item != null and _is_ours.call(item):
		return
	_forward("multi_selected", [item, column, selected])
