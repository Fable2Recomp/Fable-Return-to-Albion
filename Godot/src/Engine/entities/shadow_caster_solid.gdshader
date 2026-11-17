// Solid cast-only shadow proxy material for non-fading character, creature, held-weapon,
// and indoor entity-batch casters (`CastShadow = ShadowsOnly`).
// Zero `discard;` instructions and zero vertex-fade math allow hardware Early-Z and fast
// depth-only rasterization across directional, spot, and cubemap shadow passes.
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, depth_draw_opaque, fog_disabled;

void fragment() {
	ALBEDO = vec3(0.0);
}
