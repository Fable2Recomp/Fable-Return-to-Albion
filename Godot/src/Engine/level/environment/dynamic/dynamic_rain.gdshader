shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/level/environment/dynamic/dynamic_precip.gdshaderinc"

varying vec4 v_col;
varying vec2 v_uv;
varying float v_bokeh;

void vertex() {
	vec4 col;
	float bokeh;
	POSITION = precip_place(true, UV2.x, UV, PROJECTION_MATRIX, VIEW_MATRIX, INV_VIEW_MATRIX,
		CAMERA_POSITION_WORLD, VIEWPORT_SIZE, TIME, col, bokeh);
	v_col = col;
	v_uv = UV;
	v_bokeh = bokeh;
}

void fragment() {
	vec2 c = v_uv * 2.0 - 1.0;
	float across = 1.0 - c.x * c.x;
	float along = 1.0 - c.y * c.y;
	float a = pow(across, mix(2.0, 3.5, v_bokeh)) * along * along;
	ALBEDO = v_col.rgb * v_col.a * a;
	ALPHA = 1.0;
}
