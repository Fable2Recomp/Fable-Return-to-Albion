shader_type spatial;
// entity_fade_depth and entity_saturation_mask in one overlay, for an entity that is both faded and
// masked (a node carries one material overlay).
render_mode unshaded, blend_add, depth_draw_always, cull_disabled, shadows_disabled, fog_disabled;
stencil_mode write, compare_always, 128;

void fragment() {
	ALBEDO = vec3(0.0);
	ALPHA = 0.0;
}
