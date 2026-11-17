// SHELL FUR, MODERN: the code shared by the colour shells (fur_shell.gdshader) and the kept depth
// a faded coat draws first (fur_shell_depth.gdshader, FUR_SHELL_DEPTH). One vertex stage, so both
// place every shell texel at the same depth. See fur_shell.gdshader for the contract.
#if !defined(FUR_SHELL_DEPTH) && CURRENT_RENDERER == RENDERER_FORWARD_PLUS
render_mode depth_prepass_alpha;
#endif

#include "res://src/Engine/material/fable_lighting.gdshaderinc"
#include "res://src/Engine/level/environment/fable_atmosphere.gdshaderinc"

uniform sampler2D albedo_tex : hint_default_white, filter_linear_mipmap_anisotropic;
uniform bool is_pbr = false;
uniform sampler2DArray fur_slices : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform int fur_layer = 1;
uniform float fur_occlusion = 0.0;

// Per furred instance, per frame: N, and L for its distance.
// `entity_tint` is declared first so its instance uniform slot index (6) matches `standard_material_body.gdshaderinc`.
instance uniform vec4 entity_tint = vec4(1.0);
instance uniform float fur_shells = 16.0;
instance uniform float fur_length = 0.0;

varying vec2 v_fur_uv;
varying float v_occlusion;
varying float v_skin_share;
varying float v_edge;
varying float v_fill;
varying vec3 v_world_normal;
// The fur's share of the lit colour (against the skin's), and of the specular, per pixel.
varying float v_fur_share;
varying float v_fur_spec;

// The cut, on the slice's own alpha: the title's ALPHAREF 16, GREATEREQUAL (Render_LodMeshDraw_INFERRED
// 0x821F1BC8, disc 0x821F1A90). Never the fade: the instance's Transparency is a separate factor, applied by Godot
// to a shader that writes no ALPHA.
const float FUR_SHELL_CUT = 16.0 / 255.0;
const vec3 FUR_PRIMARY = vec3(0.2, 0.2, 0.2) + vec3(0.3, 0.3, 0.35);
const vec3 FUR_SECONDARY = vec3(0.3, 0.3, 0.35);

void vertex() {
	float h = float(fur_layer) / max(fur_shells, 1.0);
	float share = COLOR.a;
	float reach = (h + share - 1.0) * fur_length;
	vec3 pushed = VERTEX + reach * NORMAL;

	vec3 world = (MODEL_MATRIX * vec4(pushed, 1.0)).xyz;
	vec3 world_normal = mat3(MODEL_MATRIX) * NORMAL;
	float edge = 0.0;
	float fill = 0.0;
	if (reach > 0.0) {
		vec3 to_eye = INV_VIEW_MATRIX[3].xyz - world;
		float distance_to_eye = length(to_eye);
		float far = clamp(distance_to_eye / max(fur_length * 200.0, 1e-6), 0.0, 1.0);
		fill = 0.3 * far * abs(dot(to_eye / max(distance_to_eye, 1e-6), world_normal));
		edge = 1.0 - fill;
	}

	float inner = 1.0 - h;
	v_fur_uv = CUSTOM0.xy + CUSTOM0.zw * h;
	v_occlusion = clamp(1.0 - inner * inner * fur_occlusion, 0.0, 1.0);
	v_skin_share = share >= 0.01 ? inner / share : 1.0;
	v_edge = edge;
	v_fill = fill;
	v_world_normal = normalize(world_normal);
	fable_world = world;
	VERTEX = pushed;
}

void fragment() {
	// D3D12 / Intel Arc DXIL safety: NEVER wrap `dFdx`/`dFdy` in `IN_SHADOW_PASS` (runtime UBO bit)
	// and NEVER emit `discard;` in Forward+ PASS_MODE_COLOR (`depth_prepass_alpha` tests alpha in
	// MODE_RENDER_DEPTH, while PASS_MODE_COLOR outputs `ALPHA = 0.0`).
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);
	vec2 fur_uv_dx = dFdx(v_fur_uv);
	vec2 fur_uv_dy = dFdy(v_fur_uv);
#ifndef FUR_SHELL_DEPTH
#if CURRENT_RENDERER == RENDERER_FORWARD_PLUS
	float fable_instance_fade = ALPHA;
	ALPHA = 0.0;
#endif
#endif

	float h = float(fur_layer) / max(fur_shells, 1.0);
	float slice_idx = floor(clamp(h, 0.0, 0.9999) * 20.0);
	vec4 slice = textureGrad(fur_slices, vec3(v_fur_uv, slice_idx), fur_uv_dx, fur_uv_dy);
	float alpha = slice.a * v_edge + v_fill;
	bool should_discard = (alpha < FUR_SHELL_CUT);
	ALBEDO = vec3(0.0);
#ifdef FUR_SHELL_DEPTH
	ALPHA = 0.0;
#else
	EMISSION = vec3(0.0);
	v_fur_share = 0.0;
	v_fur_spec = 0.0;

	if (!should_discard) {
		vec3 skin = textureGrad(albedo_tex, UV, uv_dx, uv_dy).rgb;
		if (is_pbr) {
			skin = mix(skin / 12.92, pow((skin + vec3(0.055)) / 1.055, vec3(2.4)), step(vec3(0.04045), skin));
		} else {
			skin *= skin;
		}
		// occlusion ((1 - share) slice + share) skin, split into the fur's and the skin's parts.
		float fur = (1.0 - v_skin_share) * slice.r;
		float weight = fur + v_skin_share;
		vec3 albedo = v_occlusion * weight * skin * entity_tint.rgb;
		v_fur_share = weight > 0.0 ? fur / weight : 0.0;
		v_fur_spec = v_occlusion * fur;

		vec3 bake = fable_bake(v_world_normal, vec4(1.0), vec2(0.0)) * fable_lightmap_rgb();
		if (lightmap_colour.w < 3.5) {
			bake = fable_sky_tether(bake);
		}
		ALBEDO = albedo;
		EMISSION = albedo * fable_ambient(bake);

		vec3 fog_t;
		vec3 fog_c;
		if (fable_atmosphere_terms_for(VERTEX, INV_VIEW_MATRIX, true, fog_t, fog_c)) {
			fable_atmosphere_apply(fog_t, fog_c, 1.0, ALBEDO, EMISSION);
		}
	}
#endif
#if CURRENT_RENDERER == RENDERER_FORWARD_PLUS
#ifdef FUR_SHELL_DEPTH
	DEPTH = should_discard ? 0.0 : FRAGCOORD.z;
#else
	ALPHA = should_discard ? 0.0 : fable_instance_fade;
#endif
#else
	if (should_discard) {
		discard;
	}
#endif
}

#ifndef FUR_SHELL_DEPTH
void light() {
	if (any(greaterThan(ALBEDO, vec3(0.0)))) {
	float n_dot_l = dot(NORMAL, LIGHT);
	// Both the skin's N.L and the fur's gate are zero below N.L = -0.1.
	float gate = clamp(2.0 * n_dot_l + 0.2, 0.0, 1.0);
	float atten = gate > 0.0
		? fable_attenuation(ATTENUATION, LIGHT_IS_DIRECTIONAL, SPECULAR_AMOUNT, fable_world) : 0.0;
	if (atten > 0.0) {
		vec3 colour = atten * (LIGHT_COLOR / PI);
		vec3 across = VIEW - dot(NORMAL, VIEW) * NORMAL;
		vec3 t = dot(across, across) > 0.0 ? normalize(across) : NORMAL;
		float t_dot_l = dot(t, LIGHT);
		float fur_diffuse = gate * clamp(t_dot_l, 0.0, 1.0);
		DIFFUSE_LIGHT += colour * mix(clamp(n_dot_l, 0.0, 1.0), fur_diffuse, v_fur_share);

		float r_dot_v = clamp(dot(2.0 * t_dot_l * t - LIGHT, VIEW), 0.0, 1.0);
		float r2 = r_dot_v * r_dot_v;
		vec3 lobes = FUR_PRIMARY * clamp(4.0 * clamp(t_dot_l, 0.0, 1.0), 0.0, 1.0) * (r2 * r2)
			+ FUR_SECONDARY * pow(r_dot_v, 7.0);
		SPECULAR_LIGHT += colour * gate * v_fur_spec * lobes;
	}
	}
}
#endif
