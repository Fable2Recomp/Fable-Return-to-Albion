// The classic cascades' merged caster surface (Engine_ClassicCasterMerge): opaque, untested
// STANDARDMATERIAL surfaces of one model joined into one, drawn into a cascade for its depth
// alone. Placed by Godot's own transform, as the translated stages place theirs (xr_wvp is
// PROJECTION_MATRIX * MODELVIEW_MATRIX; the D3D clip conversion is applied twice and cancels).
shader_type spatial;
render_mode unshaded, cull_back, shadows_disabled, depth_draw_opaque, fog_disabled;

void fragment() {
	ALBEDO = vec3(0.0);
}
