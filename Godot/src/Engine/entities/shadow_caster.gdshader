// The cast-only material: every shadow proxy, prop and entity batch twin, and held-weapon caster.
//
// INVISIBLE BY CONSTRUCTION. These meshes are ShadowsOnly, so Godot never draws them in a colour
// pass; discarding every fragment outside the shadow pass keeps them unseen whatever the cast
// setting says. In the shadow pass the branch is never taken; only depth is written.
shader_type spatial;
render_mode unshaded, cull_disabled;

void fragment() {
	if (!IN_SHADOW_PASS) {
		discard;
	}

	ALBEDO = vec3(0.0);
}
