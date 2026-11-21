// shadow_caster.gdshader without the fade dither (AndroidShadowTwinDither): for a prop batch's
// cast-only twin while it has no fade band and has finished arriving, where the dither never
// discards. Depth is the same; the shadow pass's fragment stage has no discard left.
//
// Upstream's structure (#34): `should_discard` first, ONE tail discard. Under Forward+ this file is
// not one of the casters allowed a discard (shader-dxil-safety.md), so it has none there: like
// shadow_caster_solid.gdshader, a ShadowsOnly twin is never drawn in a colour pass. On Mobile
// (the phones, the only renderer that loads this) the tail discard stays as before.
shader_type spatial;
render_mode unshaded, cull_disabled;

// The same pinned slots as shadow_caster.gdshader, so the twin's instance values keep their places.
instance uniform vec4 xr_tree_fade_in : instance_index(10) = vec4(0.0);
instance uniform vec4 xr_tree_fade_out : instance_index(11) = vec4(0.0);
instance uniform vec3 xr_tree_centre : instance_index(12) = vec3(0.0);
instance uniform float xr_fade_alpha : instance_index(13) = 1.0;

void fragment() {
#if CURRENT_RENDERER == RENDERER_FORWARD_PLUS
	ALBEDO = vec3(0.0);
#else
	bool should_discard = !IN_SHADOW_PASS;

	ALBEDO = vec3(0.0);
	if (should_discard) {
		discard;
	}
#endif
}
