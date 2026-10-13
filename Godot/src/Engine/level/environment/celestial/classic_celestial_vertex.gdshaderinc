// A sky element's billboard for the classic renderer: the placement celestial.gdshader makes
// (the guest's own CPU billboard, `sub_82A63D40`: centre = dir x distance + eye, corners along
// the view's right and up, half sizes from the theme), feeding the title's element pixel shader
// its one input, TEXCOORD0 = the quad's UV (the moon's phase rect is in the mesh).
#include "res://src/Engine/material/xenos_common.gdshaderinc"
#ifndef XR_V_TEXCOORD0
#define XR_V_TEXCOORD0
varying vec4 v_texcoord0;
#endif
uniform vec3 element_dir = vec3(0.0, 1.0, 0.0);
uniform float element_distance = 1900.0;
uniform vec2 element_half = vec2(1.0, 1.0);
uniform float element_depth_bias = 0.000001;

void vertex()
{
	vec3 dir_view = (VIEW_MATRIX * vec4(normalize(element_dir), 0.0)).xyz;
	if (-dir_view.z <= 0.0) {
		POSITION = vec4(0.0, 0.0, -10.0, 1.0);
	} else {
		vec3 view_pos = (dir_view * element_distance) +
		                vec3(VERTEX.x * element_half.x, VERTEX.y * element_half.y, 0.0);
		vec4 clip = PROJECTION_MATRIX * vec4(view_pos.xy, min(view_pos.z, -0.01), 1.0);
		float far_depth = step(0.5, -PROJECTION_MATRIX[2][2]);
		POSITION = vec4(clip.xy / clip.w, mix(far_depth, 1.0 - far_depth, element_depth_bias), 1.0);
	}
	v_texcoord0 = vec4(UV, 0.0, 0.0);
}
