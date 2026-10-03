shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/level/environment/dynamic/dynamic_precip.gdshaderinc"

varying vec4 v_col;
varying vec2 v_uv;
varying float v_bokeh;

void vertex() {
	vec4 col;
	float bokeh;
	POSITION = precip_place(false, UV2.x, UV, PROJECTION_MATRIX, VIEW_MATRIX, INV_VIEW_MATRIX,
		CAMERA_POSITION_WORLD, VIEWPORT_SIZE, TIME, col, bokeh);
	v_col = col;
	v_uv = UV;
	v_bokeh = bokeh;
}

void fragment() {
	vec2 c = v_uv * 2.0 - 1.0;
	float r2 = dot(c, c);
	float a = pow(max(1.0 - r2, 0.0), mix(1.2, 2.6, v_bokeh));
	a *= 0.75 + 0.25 * (1.0 - r2);
	ALBEDO = v_col.rgb;
	ALPHA = v_col.a * a;
}
