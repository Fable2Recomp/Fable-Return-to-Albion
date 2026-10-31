extends Node
## Renders the actual production material against its previous unguarded version.
## All assets are procedural. This is a real Mobile/Vulkan GPU check, not an Android timing.

var checks: Array = []
var cases: Array = []
var output: String
var viewport: SubViewport
var mesh: MeshInstance3D
var lamps: Array[Light3D] = []
var baseline: Shader
var candidate: Shader

## Creates one synthetic uniform texture, retaining the normal material sampling path.
func texture(color: Color) -> ImageTexture:
	var image := Image.create(32, 32, false, Image.FORMAT_RGBA8)
	image.fill(color)
	image.generate_mipmaps()
	return ImageTexture.create_from_image(image)

## Records every assertion so a failure cannot be hidden by a later successful case.
func check(condition: bool, label: String) -> void:
	checks.append({"pass": condition, "label": label})
	if not condition:
		push_error(label)

## Registers production globals without creating the game, guest, audio or asset mounts.
func globals() -> void:
	var values := {
		"fable_lightmap_tint": [RenderingServer.GLOBAL_VAR_TYPE_VEC3, Vector3.ONE],
		"fable_prop_lightmaps": [RenderingServer.GLOBAL_VAR_TYPE_SAMPLER2D, texture(Color.WHITE)],
		"fable_bake_weight": [RenderingServer.GLOBAL_VAR_TYPE_FLOAT, 1.0],
		"fable_sky_ambient": [RenderingServer.GLOBAL_VAR_TYPE_VEC4, Vector4.ZERO],
		"fable_interior": [RenderingServer.GLOBAL_VAR_TYPE_FLOAT, 0.0],
		"fable_interior_bake": [RenderingServer.GLOBAL_VAR_TYPE_FLOAT, 1.0],
		"fable_light_table": [RenderingServer.GLOBAL_VAR_TYPE_SAMPLER2D, texture(Color.BLACK)],
		"fable_ambient_colour": [RenderingServer.GLOBAL_VAR_TYPE_VEC3, Vector3.ZERO],
		"fable_darkening": [RenderingServer.GLOBAL_VAR_TYPE_VEC3, Vector3.ONE],
		"fable_unified": [RenderingServer.GLOBAL_VAR_TYPE_FLOAT, 0.0],
		"fable_emission_boost": [RenderingServer.GLOBAL_VAR_TYPE_FLOAT, 1.0],
		"fable_fill_floor": [RenderingServer.GLOBAL_VAR_TYPE_VEC3, Vector3.ZERO],
		"atmos_inscatter": [RenderingServer.GLOBAL_VAR_TYPE_SAMPLER2D, texture(Color.BLACK)],
		"atmos_beta": [RenderingServer.GLOBAL_VAR_TYPE_VEC3, Vector3.ZERO],
		"atmos_sun": [RenderingServer.GLOBAL_VAR_TYPE_VEC3, Vector3(0, 0, 1)],
		"atmos_close_fog": [RenderingServer.GLOBAL_VAR_TYPE_VEC3, Vector3.ZERO],
		"atmos_range": [RenderingServer.GLOBAL_VAR_TYPE_VEC4, Vector4(0, 100, 1, 1)],
		"atmos_curve": [RenderingServer.GLOBAL_VAR_TYPE_VEC3, Vector3.ONE],
		"atmos_fog_on": [RenderingServer.GLOBAL_VAR_TYPE_BOOL, false],
		"fable_interior_fog": [RenderingServer.GLOBAL_VAR_TYPE_FLOAT, 1.0],
		"atmos_factors": [RenderingServer.GLOBAL_VAR_TYPE_VEC2, Vector2.ONE],
		"fable_light_debug": [RenderingServer.GLOBAL_VAR_TYPE_INT, 0],
		"fable_zero_specular_work": [RenderingServer.GLOBAL_VAR_TYPE_BOOL, true],
		"fable_zero_cliff_work": [RenderingServer.GLOBAL_VAR_TYPE_BOOL, true],
	}
	for name in values:
		RenderingServer.global_shader_parameter_add(name, values[name][0], values[name][1])

## Captures after several submitted frames, so previous material/light state cannot pass.
func capture(shader: Shader, parameters: Dictionary) -> Image:
	var material := ShaderMaterial.new()
	material.shader = shader
	for key in parameters:
		material.set_shader_parameter(key, parameters[key])
	mesh.material_override = material
	for frame in range(4):
		await RenderingServer.frame_post_draw
	return viewport.get_texture().get_image()

## Computes actual RGB readback differences and brightness, excluding alpha and window UI.
func compare(a: Image, b: Image) -> Dictionary:
	var maximum := 0.0
	var total := 0.0
	var brightness := 0.0
	for y in range(a.get_height()):
		for x in range(a.get_width()):
			var ca := a.get_pixel(x, y)
			var cb := b.get_pixel(x, y)
			var difference := Vector3(abs(ca.r - cb.r), abs(ca.g - cb.g), abs(ca.b - cb.b))
			maximum = max(maximum, max(difference.x, max(difference.y, difference.z)))
			total += difference.x + difference.y + difference.z
			brightness += cb.r + cb.g + cb.b
	return {"max_rgb_error": maximum, "mean_rgb_error": total / (a.get_width() * a.get_height() * 3),
		"mean_rgb": brightness / (a.get_width() * a.get_height() * 3)}

## Exercises absent, present and reconstructed ORM/PBR specular through all light classes.
func _ready() -> void:
	output = OS.get_environment("REFII_SHADER_PROBE_OUTPUT")
	globals()
	viewport = SubViewport.new()
	viewport.size = Vector2i(128, 128)
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	add_child(viewport)
	var world := Node3D.new()
	viewport.add_child(world)
	var camera := Camera3D.new()
	camera.position = Vector3(0, 0, 3)
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 2.5
	camera.current = true
	world.add_child(camera)
	mesh = MeshInstance3D.new()
	var quad := QuadMesh.new()
	quad.size = Vector2(2, 2)
	mesh.mesh = quad
	world.add_child(mesh)
	var sun := DirectionalLight3D.new()
	var omni := OmniLight3D.new()
	omni.position = Vector3(0, 0, 2)
	omni.omni_range = 10
	omni.omni_attenuation = 0
	var spot := SpotLight3D.new()
	spot.position = Vector3(0, 0, 2)
	spot.spot_range = 10
	spot.spot_attenuation = 0
	spot.spot_angle = 60
	lamps = [sun, omni, spot]
	for lamp in lamps:
		lamp.light_energy = 1
		lamp.shadow_enabled = false
		world.add_child(lamp)
	baseline = load("res://baseline.gdshader")
	candidate = load("res://candidate.gdshader")
	var common := {"albedo_tex": texture(Color(.22, .31, .4, 1)),
		"specular_tex": texture(Color(.7, .5, .3, .25)),
		"orm_tex": texture(Color(1, .4, .6, 1))}
	var variants := [
		{"name": "no-specular", "has_specular": false, "has_orm": false, "is_pbr": false},
		{"name": "regular-specular", "has_specular": true, "has_orm": false, "is_pbr": false},
		{"name": "orm-normal-only", "has_specular": false, "has_orm": true, "is_pbr": false},
		{"name": "orm-legacy-specular", "has_specular": true, "has_orm": true, "is_pbr": false},
		{"name": "orm-pbr", "has_specular": false, "has_orm": true, "is_pbr": true},
		{"name": "pbr-without-orm", "has_specular": false, "has_orm": false, "is_pbr": true},
		{"name": "foliage", "has_specular": true, "has_orm": true, "is_pbr": true, "foliage": true},
	]
	for light_index in range(lamps.size()):
		for i in range(lamps.size()):
			lamps[i].visible = i == light_index
		var no_specular: Image
		for variant in variants:
			var parameters := common.duplicate()
			for key in variant:
				if key != "name":
					parameters[key] = variant[key]
			var before := await capture(baseline, parameters)
			RenderingServer.global_shader_parameter_set("fable_zero_specular_work", false)
			var disabled := await capture(candidate, parameters)
			RenderingServer.global_shader_parameter_set("fable_zero_specular_work", true)
			var after := await capture(candidate, parameters)
			RenderingServer.global_shader_parameter_set("fable_zero_specular_work", false)
			var restored := await capture(candidate, parameters)
			check(compare(disabled, after).max_rgb_error <= 1.0 / 255.0, "same-process false/true parity")
			check(compare(disabled, restored).max_rgb_error <= 1.0 / 255.0, "same-process exact false restoration")
			RenderingServer.global_shader_parameter_set("fable_zero_specular_work", true)
			var result := compare(before, after)
			var name: String = ["directional", "omni", "spot"][light_index] + "-" + variant.name
			result["name"] = name
			cases.append(result)
			check(result.max_rgb_error <= 1.0 / 255.0, "unchanged RGB: " + name)
			check(result.mean_rgb > .005, "nonblank production draw: " + name)
			if variant.name == "no-specular":
				no_specular = after
			elif variant.name == "regular-specular":
				check(compare(no_specular, after).mean_rgb_error > .01, "positive specular control: " + name)
			elif variant.name == "orm-pbr":
				var control_parameters := parameters.duplicate()
				control_parameters["has_orm"] = false
				var control := await capture(candidate, control_parameters)
				check(compare(control, after).mean_rgb_error > .005, "ORM/PBR reconstruction retained: " + name)
			before.save_png(output.path_join(name + "-before.png"))
			after.save_png(output.path_join(name + "-after.png"))
	var failures := checks.filter(func(item): return not item["pass"])
	var report := {"status": "PASS" if failures.is_empty() else "FAIL", "checks": checks, "cases": cases,
		"renderer": RenderingServer.get_current_rendering_method(), "driver": RenderingServer.get_current_rendering_driver_name(),
		"gpu": RenderingServer.get_video_adapter_name(), "scope": "Synthetic desktop GPU equivalence; no Android performance claim."}
	FileAccess.open(output.path_join("report.json"), FileAccess.WRITE).store_string(JSON.stringify(report, "\t"))
	print("REFII_SHADER_PROBE ", JSON.stringify(report))
	get_tree().quit(0 if failures.is_empty() else 1)
