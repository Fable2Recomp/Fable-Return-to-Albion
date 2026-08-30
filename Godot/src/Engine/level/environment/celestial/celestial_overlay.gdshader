shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, depth_test_disabled,
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
uniform float occlusion_half = 0.0;
uniform float occlusion_distance = 1900.0;

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

// NO FAIL-OPEN EARLY EXIT. This function IS the depth test for this shader — the render mode
// disables the hardware one — so returning "fully visible" when the occlusion radius is zero draws
// the element straight through the terrain. The discs never set `occlusion_half`, and the glares
// derive it from the disc size, which the theme may author as zero.
//
// A zero radius is not "cannot test", it is "test a point": the 5x5 tap grid collapses onto the
// centre and still returns a valid single-sample answer.
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

			// CLAMPED TO THE SCREEN, NOT SKIPPED. Discarding off-screen taps leaves a glare whose
			// CENTRE is off-screen with no taps at all — and the sun glare is authored at a size
			// scale of 2000 against the disc's 300, so its quad covers much of the screen long
			// after its centre has left it.
			//
			// Sampling the nearest on-screen depth is an approximation, and the right one: at the
			// screen edge the geometry occluding the sun is very likely the geometry still visible
			// beside it. The guest resolves occlusion against its own offscreen target instead.
			vec2 ndc = clamp(clip.xy / clip.w, vec2(-1.0), vec2(1.0));

			vec2 uv = (ndc * 0.5) + vec2(0.5);
			float scene_depth = texture(depth_tex, uv).r;
			total++;
			if (abs(scene_depth - far_value) <= 0.0000001) {
				open++;
			}
		}
	}

	// FAIL CLOSED. No taps means every one was behind the camera, and an element behind the viewer
	// is not something to draw over the world.
	return total == 0 ? 0.0 : float(open) / float(total);
}

// Additive in linear — see celestial_additive.gdshader for why no background-estimate
// correction belongs here. The occlusion term does: it is this shader's
// reason to exist, and it scales the element's own contribution rather than the space it is
// composed in.
void fragment() {
	float geom = geometry_visibility(VIEW_MATRIX, PROJECTION_MATRIX);
	float vis = clamp(geom * element_vis, 0.0, 1.0);
	// No output squaring — the celestial family squares nothing. The vis^2 falloff stays: the
	// occlusion is OUR stand-in for the guest's offscreen resolve, not part of its shader.
	ALBEDO = max(texture(element_tex, UV).rgb * v_colour * (vis * vis), vec3(0.0));
	ALPHA = 1.0;
}
