shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never,
	unshaded, shadows_disabled, fog_disabled;

uniform sampler2D element_tex : hint_default_black, filter_linear, repeat_disable;
uniform vec3 element_dir = vec3(0.0, 1.0, 0.0);
uniform float element_distance = 1900.0;
uniform vec2 element_half = vec2(1.0, 1.0);
uniform vec4 element_colour = vec4(1.0, 1.0, 1.0, 1.0);
uniform float element_facing_power = 0.0;
uniform float element_depth_bias = 0.000001;
uniform float element_vis = 1.0;

// PREMULTIPLY BY THE TEXEL'S ALPHA — 1 for the moon's bloom, 0 for the sun's glare.
uniform float element_premultiply = 0.0;

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

// Additive in linear. Hardware depth testing ensures foreground static objects
// (trees, statues, buildings, terrain) occlude the glare with subpixel accuracy,
// eliminating all 25-tap discrete sample flicker and preventing the halo from
// bleeding onto foreground geometry.
void fragment() {
	vec2 centered_uv = (UV - vec2(0.5)) * 2.0;
	float r = length(centered_uv);
	// Smooth cubic Hermite radial falloff ensuring the halo reaches absolute zero
	// before the billboard edge, completely eliminating visible circular ring borders.
	float radial_falloff = 1.0 - smoothstep(0.35, 0.95, r);

	float vis = clamp(element_vis, 0.0, 1.0);
	vec4 texel = texture(element_tex, UV);
	float alpha_mult = mix(1.0, texel.a, element_premultiply);
	ALBEDO = max(texel.rgb * alpha_mult * v_colour * (radial_falloff * vis), vec3(0.0));
	ALPHA = 1.0;
}
