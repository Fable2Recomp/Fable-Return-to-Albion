// SHELL FUR, MODERN: the code shared by the colour shells (fur_shell.gdshader) and the kept depth
// a faded coat draws first (fur_shell_depth.gdshader, FUR_SHELL_DEPTH). One vertex stage, so both
// place every shell texel at the same depth. See fur_shell.gdshader for the contract.
#if !defined(FUR_SHELL_DEPTH) && CURRENT_RENDERER == RENDERER_FORWARD_PLUS
render_mode depth_prepass_alpha;
#endif

#include "res://src/Engine/material/fable_lighting.gdshaderinc"
#include "res://src/Engine/level/environment/fable_atmosphere.gdshaderinc"

uniform sampler2D albedo_tex : hint_default_white, filter_linear_mipmap_anisotropic;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap_anisotropic;
uniform sampler2D orm_tex : hint_default_white, filter_linear_mipmap_anisotropic;
uniform bool has_normal = false;
uniform bool has_orm = false;
uniform bool is_pbr = false;
uniform sampler2DArray fur_slices : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform int fur_layer = 1;
uniform float fur_occlusion = 0.0;
#ifndef FUR_SHELL_DEPTH
global uniform int fable_light_debug;
#endif

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
// The fur's share of the lit colour (against the skin's), of the specular, and combined AO per pixel.
varying float v_fur_share;
varying float v_fur_spec;
varying float v_combined_ao;

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
	v_combined_ao = 1.0;

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

		// Perturb shell normals from the underlying skin normal map (full depth at roots, softened toward tips)
		// so snout, ear, rib, and muscle relief read cleanly through the coat.
		float ndotn = 1.0;
		vec3 mapped_view = NORMAL;
		float normal_depth = mix(1.0, 0.45, v_fur_share);
		if (has_normal) {
			vec3 nm = textureGrad(normal_tex, UV, uv_dx, uv_dy).rgb;
			NORMAL_MAP = nm;
			NORMAL_MAP_DEPTH = normal_depth;
			vec2 nxy = (nm.xy * 2.0) - 1.0;
			ndotn = sqrt(max(1.0 - dot(nxy, nxy), 0.0));
			vec2 scaled_nxy = nxy * normal_depth;
			float scaled_z = sqrt(max(1.0 - dot(scaled_nxy, scaled_nxy), 0.0));
			mapped_view = normalize(TANGENT * scaled_nxy.x + BINORMAL * scaled_nxy.y + NORMAL * scaled_z);
		}

		vec3 world_normal = normalize(v_world_normal);
		vec3 mapped_world = normalize((INV_VIEW_MATRIX * vec4(mapped_view, 0.0)).xyz);

		float micro_ao = 1.0;
		if (has_orm) {
			micro_ao = textureGrad(orm_tex, UV, uv_dx, uv_dy).r;
		} else if (has_normal) {
			micro_ao = mix(1.0, ndotn * ndotn, 0.35 * fable_unified);
		}
		float ground_ao = fable_evaluate_prop_baked_ao_normal(1.0, fable_world, mapped_world);
		float combined_ao = mix(
			max(micro_ao, 0.0),
			fable_apply_ao_strength(max(micro_ao, 0.0) * max(ground_ao, 0.0)),
			step(0.5, fable_unified));
		v_combined_ao = combined_ao;

		// Suppress Godot's default 0.5 dielectric Fresnel sheen across stacked shells so shadowed fur stays shadowed.
		SPECULAR = 0.0;
		METALLIC = 0.0;
		ROUGHNESS = 1.0;
		AO = mix(1.0, combined_ao, fable_unified);
		AO_LIGHT_AFFECT = fable_unified * fable_ao_light_affect;

		vec3 amb_ref = normalize(vec3(fable_ambient_ref.x, fable_ambient_ref.z, fable_ambient_ref.y));
		float bend = 0.5 * (dot(world_normal, amb_ref) + 1.0);
		vec3 bent = normalize(mix(world_normal, amb_ref, bend));
		float nb = clamp(dot(bent, mapped_world), 0.0, 1.0);
		vec3 darken = mix(fable_darkening, vec3(1.0), nb * nb);

		vec3 ambient_half = vec3(0.0);
		if (fable_unified <= 0.999) {
			vec3 bake = fable_bake(mapped_world, vec4(1.0), vec2(0.0)) * fable_lightmap_rgb() * darken * micro_ao;
			if (lightmap_colour.w < 3.5) {
				bake = fable_sky_tether(bake);
			}
			ambient_half = bake;
		}
		ALBEDO = albedo;
		EMISSION = albedo * fable_ambient_occluded(ambient_half, combined_ao, darken);

		vec3 fog_t;
		vec3 fog_c;
		if (fable_atmosphere_terms_for(VERTEX, INV_VIEW_MATRIX, true, fog_t, fog_c)) {
			fable_atmosphere_apply(fog_t, fog_c, 1.0, ALBEDO, EMISSION);
		}

		if (fable_light_debug == 1) {
			ALBEDO = vec3(0.0);
		} else if (fable_light_debug == 2) {
			EMISSION = vec3(0.0);
		} else if (fable_light_debug == 3) {
			EMISSION = albedo;
			ALBEDO = vec3(0.0);
		} else if (fable_light_debug == 4) {
			EMISSION = darken;
			ALBEDO = vec3(0.0);
		} else if (fable_light_debug == 5) {
			float m = lightmap_colour.w;
			EMISSION = m > 3.5 ? vec3(0.1, 0.3, 1.0)
				: m > 2.5 ? vec3(0.1, 1.0, 0.1)
				: m > 1.5 ? vec3(1.0, 0.1, 1.0)
				: m > 0.5 ? vec3(1.0, 1.0, 0.1)
				: vec3(1.0, 0.1, 0.1);
			ALBEDO = vec3(0.0);
		} else if (fable_light_debug == 6 || fable_light_debug == 7 || fable_light_debug == 9) {
			EMISSION = vec3(0.0);
			ALBEDO = vec3(1.0);
		} else if (fable_light_debug == 8) {
			EMISSION = (mapped_view * 0.5) + 0.5;
			ALBEDO = vec3(0.0);
		} else if (fable_light_debug == 10) {
			EMISSION = FRONT_FACING ? vec3(0.1, 1.0, 0.1) : vec3(1.0, 0.1, 0.1);
			ALBEDO = vec3(0.0);
		} else if (fable_light_debug == 11) {
			EMISSION = vec3(combined_ao * v_occlusion);
			ALBEDO = vec3(0.0);
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
#if CURRENT_RENDERER == RENDERER_FORWARD_PLUS
	bool has_surface_response = (ALPHA > 0.0) && any(greaterThan(ALBEDO, vec3(0.0)));
#else
	bool has_surface_response = any(greaterThan(ALBEDO, vec3(0.0)));
#endif
	if (has_surface_response) {
		float n_dot_l = dot(NORMAL, LIGHT);
		if (fable_light_debug == 6) {
			if (LIGHT_IS_DIRECTIONAL) {
				DIFFUSE_LIGHT = vec3(clamp(ATTENUATION, 0.0, 1.0));
			}
		} else if (fable_light_debug == 7) {
			if (LIGHT_IS_DIRECTIONAL) {
				DIFFUSE_LIGHT = vec3((n_dot_l * 0.5) + 0.5);
			}
		} else {
			// Both the skin's N.L and the fur's gate are zero below N.L = -0.1.
			float gate = clamp(2.0 * n_dot_l + 0.2, 0.0, 1.0);
			float atten = gate > 0.0
				? fable_attenuation_weighted(ATTENUATION, LIGHT_IS_DIRECTIONAL, SPECULAR_AMOUNT, fable_world, max(gate, 0.1))
				  * fable_direct_ao_for_light(v_combined_ao, LIGHT_IS_DIRECTIONAL)
				: 0.0;
			if (atten > 0.0) {
				vec3 colour = atten * (LIGHT_COLOR / PI);
				// View-sign-invariant Kajiya-Kay strand scattering (`sin(T, L)`) blended with soft wrapped N.L
				// so the coat receives full directional/point light and crisp cast shadows from every camera angle.
				vec3 across = (-VIEW) - dot(NORMAL, -VIEW) * NORMAL;
				vec3 t = dot(across, across) > 1e-6 ? normalize(across) : NORMAL;
				float t_dot_l = dot(t, LIGHT);
				float sin_tl = sqrt(max(1.0 - t_dot_l * t_dot_l, 0.0));
				float wrapped_nl = clamp((n_dot_l + 0.18) * 0.8474576, 0.0, 1.0);
				float fur_diffuse = gate * wrapped_nl * (0.68 + 0.36 * sin_tl);
				DIFFUSE_LIGHT += colour * mix(clamp(n_dot_l, 0.0, 1.0), fur_diffuse, v_fur_share);

				if (fable_light_debug != 9) {
					float r_dot_v = clamp(dot(2.0 * t_dot_l * t - LIGHT, VIEW), 0.0, 1.0);
					float r2 = r_dot_v * r_dot_v;
					vec3 lobes = FUR_PRIMARY * clamp(2.5 * sin_tl, 0.0, 1.0) * (r2 * r2)
						+ FUR_SECONDARY * pow(r_dot_v, 7.0);
					SPECULAR_LIGHT += colour * gate * v_fur_spec * fable_specular_ao(v_combined_ao) * lobes;
				}
			}
		}
	}
}
#endif
