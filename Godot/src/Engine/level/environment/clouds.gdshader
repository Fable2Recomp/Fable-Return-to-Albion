shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded;

uniform sampler2D cloud_density : filter_linear, repeat_enable;
uniform vec4 layer_params;
uniform vec4 uv_scale_offset;
uniform vec4 cloud_globals;
uniform vec4 light_colour;
uniform vec3 light_direction_world = vec3(0.0, 1.0, 0.0);

varying vec3 v_world;

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 uv = (UV * uv_scale_offset.xy) + uv_scale_offset.zw;

	vec4 centre = texture(cloud_density, uv);
	float density = centre.a;

	float distance_xy = length(v_world.xz - CAMERA_POSITION_WORLD.xz);
	float distance_fade = 1.0 - clamp((distance_xy - 1000.0) * 0.001, 0.0, 1.0);
	float alpha = density * layer_params.x * distance_fade;

	if (alpha <= cloud_globals.z) {
		discard;
	}

	vec3 rgb = vec3(0.0);

	if (alpha > 0.001) {
		vec2 texel = 1.0 / vec2(textureSize(cloud_density, 0));
		float plus_x = texture(cloud_density, uv + vec2(2.0 * texel.x, 0.0)).a;
		float plus_y = texture(cloud_density, uv + vec2(0.0, 2.0 * texel.y)).a;
		float minus_x = texture(cloud_density, uv - vec2(2.0 * texel.x, 0.0)).a;
		float minus_y = texture(cloud_density, uv - vec2(0.0, 2.0 * texel.y)).a;

		float gradient_x = plus_x - minus_x;
		float gradient_y = plus_y - minus_y;
		vec3 normal = normalize(vec3(gradient_x, layer_params.w, gradient_y));

		vec3 light_position = CAMERA_POSITION_WORLD +
		                      (light_direction_world * 2000.0);
		vec3 light_dir = normalize(v_world - light_position);
		vec3 view_forward = -normalize(INV_VIEW_MATRIX[2].xyz);

		float grazing = 1.0 - clamp(dot(normal, -view_forward), 0.0, 1.0);
		float facing_pow = pow(grazing, 32.0);
		float back = clamp(dot(light_dir, -normal), 0.0, 1.0);
		float front = clamp(dot(light_dir, normal), 0.0, 1.0);
		float lighting = (front * (1.0 + facing_pow)) +
		                 (back * back * (1.0 - facing_pow));

		// Linear out, no Reinhard and no encode. 0114 pshader_clouds ends
		//
		//     r0.xyz = r0.zyx * r2.yzw;                          // lighting x cloud RGB
		//     r0.xyz = r0.xyz * g_GlobalAmbientAndBrightness.www;
		//     oC0    = r0;
		//
		// and the sampled RGB reaches that multiply UNSQUARED — the only x*x in the shader are
		// g_NormalStrength squared and a saturated dot squared, neither a texture decode. That
		// makes clouds a deliberate exception to Fable's gamma-2.0 texture convention, which
		// the sky dome beside it does follow on its two overlay textures. `centre.rgb` is used
		// raw here for exactly that reason; do not "fix" it to match the material shaders.
		rgb = ((lighting * light_colour.rgb) + vec3(layer_params.y)) *
		      vec3(layer_params.z) * centre.rgb * cloud_globals.x;
	}

	ALBEDO = rgb;
	ALPHA = alpha;
}
