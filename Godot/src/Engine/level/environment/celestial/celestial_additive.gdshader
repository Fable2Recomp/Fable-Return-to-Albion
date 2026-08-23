shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

uniform sampler2D element_tex : filter_linear, repeat_disable;
uniform vec3 element_dir = vec3(0.0, 1.0, 0.0);
uniform float element_distance = 1900.0;
uniform vec2 element_half = vec2(1.0, 1.0);
uniform vec4 element_colour = vec4(1.0, 1.0, 1.0, 1.0);
uniform float element_facing_power = 0.0;
uniform float element_depth_bias = 0.000001;

varying vec3 v_colour;

void vertex() {
	vec3 dir_view = (VIEW_MATRIX * vec4(normalize(element_dir), 0.0)).xyz;
	float facing = -dir_view.z;

	if (facing <= 0.0) {
		POSITION = vec4(0.0, 0.0, -10.0, 1.0);
		v_colour = vec3(0.0);
	} else {
		vec3 view_pos = (dir_view * element_distance) +
		                vec3(VERTEX.x * element_half.x, VERTEX.y * element_half.y, 0.0);
		vec4 clip = PROJECTION_MATRIX *
		            vec4(view_pos.xy, min(view_pos.z, -0.01), 1.0);

		float far_depth = step(0.5, -PROJECTION_MATRIX[2][2]);
		POSITION = vec4(clip.xy / clip.w,
		                mix(far_depth, 1.0 - far_depth, element_depth_bias), 1.0);

		v_colour = element_colour.rgb * pow(facing, element_facing_power);
	}
}

// ADDITIVE IN LINEAR, WHICH IS WHAT `blend_add` ALREADY DOES.
//
// Estimating the sky behind the element, adding itself to it in display space and subtracting
// the sky back off — `linear(sky + add) - linear(sky)` — compensates for a premise the shipped
// shaders refute: 0117 sunglare is `oC0.xyz = texel.rgb * g_SunColour.rgb; oC0.w = 1.0`, and
// 0121 sunbeams is the same with a 1/6 attenuation. Two multiplies, no saturate, no transfer.
// The elements write linear HDR and the hardware blend sums them in that space, so the correct
// contribution is just the product, and subtracting an estimated background from it removes
// light the blend never double-counted.
void fragment() {
	vec3 rgb = max(texture(element_tex, UV).rgb * v_colour, vec3(0.0));
	ALBEDO = rgb * rgb;
	ALPHA = 1.0;
}
