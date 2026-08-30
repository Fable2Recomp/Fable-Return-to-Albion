# Parses every .gdshader in the project and fails the build if any of them errors.
#
# WHY THIS EXISTS: `dotnet build` does not look at shaders, and Godot only parses a .gdshader when
# something loads it — so a syntax error ships silently and surfaces at runtime as every affected
# mesh falling back to the default material. That is what "Using 'return' in the 'light' processor
# function is incorrect" did: the whole world rendered untextured and the build was green.
#
# Godot reports shader errors by printing them, not by returning a status, so the harness around
# this script greps the output. This script's own exit code covers the cases it can see directly:
# a file that will not load at all.
extends SceneTree

const ROOT := "res://src"

func _initialize() -> void:
	var shaders: Array[String] = []
	_collect(ROOT, shaders)
	shaders.sort()

	var unloadable := 0
	for path in shaders:
		# READ THE TEXT AND HAND IT TO A FRESH Shader. Loading the resource and reassigning its
		# own `code` is a no-op — Godot skips `set_code` when the string is unchanged, so nothing
		# is ever parsed. A new Shader with new content forces the compile every time.
		var file := FileAccess.open(path, FileAccess.READ)
		if file == null:
			push_error("shader-check: could not open %s" % path)
			unloadable += 1
			continue
		# NAMED BEFORE IT IS COMPILED. Compiling from a string means Godot has no path to put in
		# the error, so it prints "(:4)" — this marker is what attributes the next SHADER ERROR
		# to a file, and the wrapper reads it back.
		print("shader-check: compiling %s" % path)
		var shader := Shader.new()
		shader.code = file.get_as_text()
		file.close()
		# Binding it to a material is what drives the code through the scene shader compiler,
		# which is where the processor-function rules are enforced.
		var material := ShaderMaterial.new()
		material.shader = shader

	print("shader-check: parsed %d shader(s) under %s" % [shaders.size(), ROOT])
	quit(1 if unloadable > 0 else 0)

func _collect(dir_path: String, out: Array[String]) -> void:
	var dir := DirAccess.open(dir_path)
	if dir == null:
		return
	dir.list_dir_begin()
	var name := dir.get_next()
	while name != "":
		var full := dir_path.path_join(name)
		if dir.current_is_dir():
			if not name.begins_with("."):
				_collect(full, out)
		elif name.ends_with(".gdshader"):
			out.append(full)
		name = dir.get_next()
	dir.list_dir_end()
