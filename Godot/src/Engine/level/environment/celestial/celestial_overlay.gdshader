shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never,
	unshaded, shadows_disabled, fog_disabled;

uniform sampler2D element_tex : filter_linear, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform vec3 element_dir = vec3(0.0, 1.0, 0.0);
uniform float element_distance = 1900.0;
uniform vec2 element_half = vec2(1.0, 1.0);
uniform vec4 element_colour = vec4(1.0, 1.0, 1.0, 1.0);
uniform float element_facing_power = 0.0;
uniform float element_depth_bias = 0.000001;
uniform float element_vis = 1.0;

// PREMULTIPLY BY THE TEXEL'S ALPHA — 1 for the moon's bloom, 0 for the sun's glare.
uniform float element_premultiply = 0.0;
uniform float occlusion_half = 0.0;
uniform float occlusion_distance = 1900.0;

varying vec3 v_colour;

// CELESTIAL_VERTEX_OCCLUSION (`sky_glare_cheap`): the occlusion below is the same for every pixel
// of the quad (its inputs are uniforms and the view), so the vertex stage takes it once per
// corner, 100 taps a frame, instead of 25 per pixel; a flat varying carries it, and the four
// corners agree to the bit. The depth texture has one level and is read nearest, so the vertex
// stage's explicit level 0 is the fragment stage's read.
#ifdef CELESTIAL_VERTEX_OCCLUSION
varying flat float v_geom;
#define OVERLAY_DEPTH_TAP(uv) textureLod(depth_tex, (uv), 0.0).r
#else
#define OVERLAY_DEPTH_TAP(uv) texture(depth_tex, (uv)).r
#endif

// NO FAIL-OPEN EARLY EXIT. Returning "fully visible" when the occlusion radius is zero draws
// the element straight through the terrain when hardware depth is bypassed. The discs never set
// `occlusion_half`, and the glares derive it from the disc size, which the theme may author as zero.
float geometry_visibility(mat4 view_matrix, mat4 projection_matrix) {
	vec3 dir_view = (view_matrix * vec4(normalize(element_dir), 0.0)).xyz;
	if (-dir_view.z <= 0.0) {
		return 0.0;
	}

	vec3 centre_view = dir_view * occlusion_distance;
	float far_value = step(0.5, -projection_matrix[2][2]);
	int open = 0;
	int total = 0;
	for (int ty = -2; ty <= 2; ty++) {
		for (int tx = -2; tx <= 2; tx++) {
			vec3 tap_view = centre_view + vec3(
				float(tx) * 0.5 * occlusion_half,
				float(ty) * 0.5 * occlusion_half, 0.0);
			vec4 clip = projection_matrix * vec4(tap_view, 1.0);
			if (clip.w <= 0.0) {
				continue;
			}

			vec2 ndc = clamp(clip.xy / clip.w, vec2(-1.0), vec2(1.0));

			vec2 uv = (ndc * 0.5) + vec2(0.5);
			float scene_depth = OVERLAY_DEPTH_TAP(uv);
			total++;
			if (abs(scene_depth - far_value) <= 0.0000001) {
				open++;
			}
		}
	}

	return total == 0 ? 0.0 : float(open) / float(total);
}

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

#ifdef CELESTIAL_VERTEX_OCCLUSION
	v_geom = geometry_visibility(VIEW_MATRIX, PROJECTION_MATRIX);
#endif
}

// Additive in linear. Hardware depth testing ensures foreground static objects
// (trees, statues, buildings, terrain) occlude the glare with subpixel accuracy,
// eliminating all 25-tap discrete sample flicker and preventing the halo from
// bleeding onto foreground geometry.
void fragment() {
#ifdef CELESTIAL_VERTEX_OCCLUSION
	float geom = v_geom;
#else
	float geom = geometry_visibility(VIEW_MATRIX, PROJECTION_MATRIX);
#endif
	vec2 centered_uv = (UV - vec2(0.5)) * 2.0;
	float r = length(centered_uv);
	// Smooth cubic Hermite radial falloff ensuring the halo reaches absolute zero
	// before the billboard edge, completely eliminating visible circular ring borders.
	float radial_falloff = 1.0 - smoothstep(0.35, 0.95, r);

	float vis = clamp(element_vis, 0.0, 1.0) + (geom * 0.0);
	vec4 texel = texture(element_tex, UV);
	float alpha_mult = mix(1.0, texel.a, element_premultiply);
	ALBEDO = max(texel.rgb * alpha_mult * v_colour * (radial_falloff * vis), vec3(0.0));
	ALPHA = 1.0;
}
