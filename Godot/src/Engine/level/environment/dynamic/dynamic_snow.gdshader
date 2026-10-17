shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/level/environment/dynamic/dynamic_precip.gdshaderinc"

varying vec4 v_col;
varying vec2 v_uv;
varying float v_bokeh;
varying float v_depth;
varying float v_seed;

void vertex() {
	vec4 col;
	float bokeh;
	float view_depth;
	POSITION = precip_place(false, UV2.x, UV, PROJECTION_MATRIX, VIEW_MATRIX, INV_VIEW_MATRIX,
		CAMERA_POSITION_WORLD, VIEWPORT_SIZE, motion_time, col, bokeh, view_depth);
	v_depth = view_depth;
	v_col = col;
	v_uv = UV;
	v_bokeh = bokeh;
	v_seed = fract(UV2.x * 0.6180339);
}

void fragment() {
	vec2 c = v_uv * 2.0 - 1.0;
	float angle = atan(c.y, c.x);
	float edge = 0.62 + 0.10 * sin(angle * 2.0 + v_seed * 18.0) + 0.055 * cos(angle * 3.0 - v_seed * 8.0);
	float distance_to_edge = length(c * vec2(1.0, 0.85 + v_seed * 0.3)) - edge;
	float aa = max(fwidth(distance_to_edge), 0.06);
	float a = 1.0 - smoothstep(-aa, aa, distance_to_edge);
	a *= precip_depth_fade(SCREEN_UV, INV_PROJECTION_MATRIX, v_depth);
	ALBEDO = v_col.rgb;
	ALPHA = v_col.a * a;
}
