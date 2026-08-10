
shader_type spatial;
render_mode cull_disabled, ambient_light_disabled, specular_disabled;

#include "res://src/Engine/material/fable_lighting.gdshaderinc"

uniform bool has_lightmap = false;

uniform sampler2DArray lod_array : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray lod_detail_array : filter_linear_mipmap_anisotropic, repeat_enable;

uniform sampler2DArray material_weight : filter_linear, repeat_disable;

// source_color, unlike the colour textures above. A lightmap texture is loaded through the
// GAMMA SIBLING of whatever format the .tex states — the loader passes a3 == 1 and
// sub_82B8CEE0 walks the format table for `stated | 0x7E00` — so the decode is the hardware's
// sRGB, not the shader's squaring. Sampling it raw used gamma values as linear and made the
// ground read far brighter than everything standing on it.
uniform sampler2D lightmap : source_color, filter_linear, repeat_enable, hint_default_white;

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

	ALBEDO = final;

	// THE TERRAIN HAS A LIGHTMAP. This was disputed between two IDA passes — one found no
	// type-6 override of the lightmap-fetch virtual and concluded the heightfield has none —
	// and the shipped shader settles it: PSHADER_LANDSCAPEMATERIAL_..._AMB1, recompiled out of
	// Shaders.sbk, binds `g_TextureLightmapSampler` at sampler s3 and samples it with its own
	// UV set. The IDA negative was real but was evidence about the CPU, and the binding is
	// data-driven through the shared material path, which is why it was not visible there.
	//
	// The guest's composition, from the same shader:
	//
	//     darken  = lerp(AmbientNormalMapDarkeningColour, 1, saturate(dot(Ngeom, Nmapped)))
	//     ambient = pow(lightmap, gamma) * LightmapFactor * darken + GlobalAmbient
	//
	// The darkening term is omitted here and only here: it needs a terrain normal map, which
	// this splat shader does not yet have, so dot(Ngeom, Nmapped) is identically 1 and the term
	// collapses to 1. When terrain normal maps land, that lerp is the line to add.
	vec2 lm_uv = clamp(chunk_co / vec2(cw, ch), vec2(0.000001), vec2(0.999999));
	vec3 bake = has_lightmap ? texture(lightmap, lm_uv).rgb : vec3(0.0);

	// LightmapFactor is NOT applied here, and the measurement is why. The guest's shape is
	// `bake * LightmapFactor + GlobalAmbient` and this region states a factor of
	// (3.54, 3.38, 5.16); against our decoded lightmap, which averages about 0.6 once the sRGB
	// decode is applied, that is 2.1 to 3.1 and the ground clips to flat white.
	//
	// The factor is almost certainly right — it balances exactly against the SH band constant
	// on the probe path, where 0.17 x 3.54 lands on 0.60. What is wrong is far more likely to
	// be OUR LIGHTMAP BINDING, which is known to diverge from the guest in three ways at once:
	// the guest binds one lightmap PER LANDSCAPE PATCH selected by that patch's own 64-bit UID
	// (`sub_82A8EA50`), addresses it with a PLANAR WORLD-XZ projection scaled by
	// 1/(texDim * metres-per-texel), and this level ships two candidate blobs of which we simply
	// take the larger. We bind one texture for the whole heightfield with normalised chunk UVs.
	//
	// So: fix the binding first, then re-apply the factor. Applying it now would be following
	// the guest's arithmetic with the wrong operand.
	EMISSION = final * fable_ambient(bake);
}

void light() {
	DIFFUSE_LIGHT += fable_direct(NORMAL, LIGHT, ATTENUATION, LIGHT_COLOR, ALBEDO);
}
