shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded;

// MIPMAPPED, because these are flat horizontal planes seen almost edge-on. A layer is a rectangle
// at a fixed height, so toward its vanishing line the UVs compress without bound and a tiled
// density texture sampled with no mip selection aliases into hard horizontal stripes. The decoder
// already generates the mip chain (Engine_TexImage).
uniform sampler2D cloud_density : hint_default_black, filter_linear_mipmap, repeat_enable;
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
	// Zero-discard `depth_draw_never` blend path: output `ALPHA = 0.0` below `cloud_globals.z`
	// so `blend_mix` is a hardware no-op without `discard;`.
	vec2 uv_dx = dFdx(uv);
	vec2 uv_dy = dFdy(uv);

	vec4 centre = textureGrad(cloud_density, uv, uv_dx, uv_dy);
	float density = centre.a;

	float distance_xy = length(v_world.xz - CAMERA_POSITION_WORLD.xz);
	float distance_fade = 1.0 - clamp((distance_xy - 1000.0) * 0.001, 0.0, 1.0);

	// FADE AT THE QUAD'S OWN BORDER. A REMASTER DECISION, and labelled as one: blob 0114
	// `pshader_clouds`, which this fragment stage otherwise follows verbatim, has no edge term.
	//
	// The layer is a finite rectangle (`Engine_Clouds` builds four corners with no edge
	// treatment) and `distance_fade` above only ramps between 1000 m and 2000 m of HORIZONTAL
	// distance. Where the polygon's own border falls nearer than that, the layer is still at
	// full alpha when the geometry simply ends — measured on screen as a ONE-PIXEL step from
	// cloud to bare sky, cutting a straight diagonal across the view. That is the seam.
	//
	// `UV` is the quad's parametric 0..1, not the tiled `uv` above — the tiled one repeats and
	// would band the whole layer instead of just its rim.
	vec2 edge_uv = min(UV, 1.0 - UV);
	float edge_fade = smoothstep(0.0, 0.08, min(edge_uv.x, edge_uv.y));

	float alpha = density * layer_params.x * distance_fade * edge_fade;
	bool should_discard = (alpha <= cloud_globals.z);

	vec3 rgb = vec3(0.0);

	if (!should_discard && alpha > 0.001) {
		// One-texel offsets — 0114's gradient taps are `tfetch2D(..., float2(1, 0))` etc.; a
		// two-texel baseline halves the slope and softens every cloud normal.
		vec2 texel = 1.0 / vec2(textureSize(cloud_density, 0));
		float plus_x = textureGrad(cloud_density, uv + vec2(texel.x, 0.0), uv_dx, uv_dy).a;
		float plus_y = textureGrad(cloud_density, uv + vec2(0.0, texel.y), uv_dx, uv_dy).a;
		float minus_x = textureGrad(cloud_density, uv - vec2(texel.x, 0.0), uv_dx, uv_dy).a;
		float minus_y = textureGrad(cloud_density, uv - vec2(0.0, texel.y), uv_dx, uv_dy).a;

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
		// The back term is weighted by (1 - DENSITY) — `ps = c254.w - r2.x` in 0114, where
		// r2.x is the sampled alpha. Thin edges transmit; dense cores do not. Weighting by the
		// view term instead made transmission a function of camera angle.
		float lighting = (front * (1.0 + facing_pow)) +
		                 (back * back * (1.0 - density));

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
		      vec3(layer_params.z) * centre.rgb;
	}

	ALBEDO = should_discard ? vec3(0.0) : rgb;
	ALPHA = should_discard ? 0.0 : alpha;
}
