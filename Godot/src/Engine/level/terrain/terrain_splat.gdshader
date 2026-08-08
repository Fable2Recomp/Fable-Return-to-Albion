
shader_type spatial;
render_mode unshaded, cull_disabled;

uniform sampler2DArray lod_array : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray lod_detail_array : filter_linear_mipmap_anisotropic, repeat_enable;

uniform sampler2DArray material_weight : filter_linear, repeat_disable;

uniform sampler2D lightmap : filter_linear, repeat_enable, hint_default_white;

uniform vec4 chunk_origin_extent;

uniform vec4 chunk_grid_size;

uniform vec4 splat_params;

uniform vec4 mesh_xform;

uniform vec4 weight_size;

uniform vec4 material_params[32];

varying vec3 v_local;
varying vec3 v_local_normal;

void vertex() {
	v_local = VERTEX;
	v_local_normal = NORMAL;
}

vec3 sample_material(int slice, vec2 world_xy, float slope_w) {
	int max_slice = max(int(chunk_grid_size.z) - 1, 0);
	int s = min(max(slice, 0), max_slice);
	vec4 mp = material_params[s];

	float base_scale = splat_params.y;
	float detail_scale = splat_params.y;

	vec3 base = texture(lod_array, vec3(world_xy * base_scale, float(s))).rgb;
	vec3 detail = texture(lod_detail_array, vec3(world_xy * detail_scale, float(s))).rgb;

	float detail_w = slope_w * clamp(mp.z, 0.0, 1.0) * clamp(mp.w, 0.0, 1.0);
	return mix(base, detail, detail_w);
}

void fragment() {
	vec2 mesh_xy = vec2(v_local.x, v_local.z);
	vec2 world_xy = (mesh_xy * mesh_xform.xy) + mesh_xform.zw;

	vec2 origin = chunk_origin_extent.xy;
	vec2 extent = chunk_origin_extent.zw;
	float cw = chunk_grid_size.x;
	float ch = chunk_grid_size.y;

	vec2 chunk_co = (world_xy - origin) / extent;
	vec2 chunk_clamped = clamp(chunk_co, vec2(0.0), vec2(cw - 0.001, ch - 0.001));

	float slope_w = clamp((0.90 - abs(normalize(v_local_normal).y)) / 0.10, 0.0, 1.0);

	vec3 final = vec3(0.0);
	float weight_sum = 0.0;

	vec2 inv_weight_size = weight_size.zw;
	vec2 weight_uv = ((chunk_clamped * 32.0) + 0.5) * inv_weight_size;
	vec2 edge_uv = 0.5 * inv_weight_size;
	weight_uv = clamp(weight_uv, edge_uv, vec2(1.0) - edge_uv);

	for (int mat = 0; mat < 32; mat++) {
		if (mat >= int(chunk_grid_size.z)) {
			break;
		}

		float w = textureLod(material_weight, vec3(weight_uv, float(mat)), 0.0).r;
		if (w <= 0.001) {
			continue;
		}

		final += sample_material(mat, world_xy, slope_w) * w;
		weight_sum += w;
	}

	if (weight_sum > 0.001) {
		final /= weight_sum;
	} else {
		final = sample_material(0, world_xy, slope_w);
	}

	vec2 lm_uv = clamp(chunk_co / vec2(cw, ch), vec2(0.000001), vec2(0.999999));
	vec3 baked_light = texture(lightmap, lm_uv).rgb;
	final *= (baked_light * 0.55) + 0.45;

	ALBEDO = final;
}
