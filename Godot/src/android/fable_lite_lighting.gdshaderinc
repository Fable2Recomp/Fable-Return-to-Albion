// THE PHONE'S LIGHTING FUNCTIONS: what a lite build (FABLE_LITE, Engine_ShaderVariant) compiles in place
// of, or beside, fable_lighting.gdshaderinc's. Included at the end of fable_lighting in every lite build,
// after every helper they call and before either body; the switches they read are fable_lite_switches'.
// Godot emits only the functions a stage calls, in an order of its own, so where in the file a function
// is written does not change the shader; fable_lighting leaves out its own copy of each one below. (The
// global uniforms do keep their declaration order: the ambient fold's is hooked where it always stood,
// fable_lite_ambient_fold, and the lamps' come last here as they always did.)

// THE GROUND-CONTACT AO a lite build compiles out (fable_lite_switches: FABLE_LITE_OWN_TERRAIN_AO with
// baked AO off or `_NO_TERRAIN_GROUND_AO`, FABLE_LITE_OWN_PROP_AO with baked AO off or `_NO_PROP_GROUND_AO`).
#ifdef FABLE_LITE_OWN_TERRAIN_AO
float fable_sample_terrain_ao(vec3 world_pos) { return 1.0; }
#endif
#ifdef FABLE_LITE_OWN_PROP_AO
#ifdef FABLE_LITE_NO_BAKED_AO
float fable_evaluate_prop_baked_ao(float color_alpha, vec3 world_pos) { return 1.0; }
#else
/// fable_evaluate_prop_baked_ao's value without a ground map.
float fable_evaluate_prop_baked_ao(float color_alpha, vec3 world_pos) {
	if (FABLE_UNIFIED <= 0.5 || FABLE_AO_STRENGTH <= 0.001) {
		return 1.0;
	}
	float vertex_ao = color_alpha > 0.001 ? color_alpha : 1.0;
	float inst_ao = instance_baked_ao > 0.001 ? instance_baked_ao : 1.0;
	return vertex_ao * inst_ao;
}
#endif
#endif

// The props' ground AO read per vertex or near the floor only (AndroidGroundShading).
#if defined(FABLE_VERTEX_GROUND_AO) || defined(FABLE_NEAR_GROUND_AO)
#include "res://src/android/fable_lite_ground_ao.gdshaderinc"
#endif

// THE AO WEIGHTS, fable_lighting's four (left out of every lite build): the phone's switches.
#ifdef FABLE_LITE_NO_BAKED_AO
float fable_apply_ao_strength(float raw_ao) { return 1.0; }
float fable_direct_ao(float combined_ao) { return 1.0; }
#else
float fable_apply_ao_strength(float raw_ao) {
	if (FABLE_AO_STRENGTH <= 0.001 || raw_ao >= 0.999) {
		return 1.0;
	}
#ifdef FABLE_LITE_AO_LINEAR
	// The pow's own value at strengths 0 and 1, without its log and exp between them.
	return max(mix(1.0, raw_ao, FABLE_AO_STRENGTH), 1e-4);
#elif defined(FABLE_LITE_MODERN) && defined(FABLE_LITE_FOLD_AO_DEFAULTS)
	// At the default strength of 1 (see FABLE_AO_LIGHT_AFFECT) the pow is its base.
	return max(raw_ao, 1e-4);
#else
	if (abs(FABLE_AO_STRENGTH - 1.0) < 0.01) {
		return max(raw_ao, 1e-4);
	}
	return pow(max(raw_ao, 1e-4), FABLE_AO_STRENGTH);
#endif
}

// FULL PRECISION, NEVER FABLE_LITE_MP (`lite_half_precision_lighting`). With this weight relaxed, an
// Adreno 640 (Galaxy S10e, OnePlus 7 Pro) drew every lite surface that faces the sun or the moon pure
// white under the modern model, which is the only model that compiles it (the fable model's builds
// take FABLE_LITE_NO_BAKED_AO, above); the faces turned away, which light() skips, kept their colour.
// The S25+ (Adreno 830) drew it right. On the S10e at Low, 2026-11-14: half precision on is white by
// day and night, on with FABLE_LITE_NO_DIRECT_AO is right, and on with this weight at full precision
// is right. One mix per light, so the relaxed form bought nothing measurable. Here and in
// fable_lite_lean_light's directional branch (LiteHalfPrecisionLightingTests).
float fable_direct_ao(float combined_ao) {
#ifdef FABLE_LITE_NO_DIRECT_AO
	return 1.0;
#else
	float w = max(0.0, min(1.0, FABLE_UNIFIED * FABLE_AO_LIGHT_AFFECT));
	return mix(1.0, max(combined_ao, 0.0), w);
#endif
}
#endif

// With baked AO compiled out (FABLE_LITE_NO_BAKED_AO: the fable model, where fable_unified is 0, or
// AO off, where the modern combined AO is 1) these two are exactly 1, so the build says so. A phone
// build may take them without the clamps the combined AO's range settles (`lite_lean_light_ao`).
#if defined(FABLE_LITE_LEAN_LIGHT_AO) && !defined(FABLE_LITE_NO_BAKED_AO) && !defined(FABLE_LITE_NO_DIRECT_AO)
#include "res://src/android/fable_lite_lean_light.gdshaderinc"
#else
float fable_direct_ao_for_light(float combined_ao, bool directional) {
#if defined(FABLE_LITE_NO_BAKED_AO) || defined(FABLE_LITE_NO_DIRECT_AO)
	return 1.0;
#else
	if (directional) {
		return fable_direct_ao(combined_ao);
	}
	float ao_c = max(0.0, min(1.0, combined_ao));
	float occ = 1.0 - ao_c;
	float active_gate = step(0.001, FABLE_AO_LIGHT_AFFECT);
	float local_w = max(0.0, min(0.82, FABLE_UNIFIED * active_gate * max(FABLE_AO_LIGHT_AFFECT * 2.2, 0.58)));
	return 1.0 - local_w * occ * mix(0.55, 1.0, occ);
#endif
}

float fable_specular_ao(float combined_ao) {
#if defined(FABLE_LITE_NO_BAKED_AO) || defined(FABLE_LITE_NO_DIRECT_AO)
	return 1.0;
#else
	float ao_c = max(0.0, min(1.0, combined_ao));
	float spec_occ = max(0.0, min(1.0, ao_c * ao_c + 0.15 * ao_c));
	return mix(1.0, spec_occ, max(0.0, min(1.0, FABLE_UNIFIED)));
#endif
}
#endif

// fable_direct and fable_specular (left out of every lite build) with their inputs copied to locals,
// the only place FABLE_LITE_MEDIUMP may relax them (see fable_lite_switches: never a parameter).
vec3 fable_direct(vec3 normal, vec3 light, float attenuation, vec3 light_colour) {
	FABLE_LITE_MP vec3 n = normal, l = light, c = light_colour;
	FABLE_LITE_MP float a = attenuation;
	return clamp(dot(n, l), 0.0, 1.0) * a * (c / PI);
}

vec3 fable_specular(
		vec3 normal,
		vec3 light,
		vec3 view,
		float attenuation,
		vec3 light_colour,
		vec3 specular_colour,
		float specular_power) {
	float cosine = clamp(dot(view, reflect(-light, normal)), 0.0, 1.0);
	FABLE_LITE_MP float lobe = pow(max(cosine, 1e-6), max(specular_power, 1.0));
	FABLE_LITE_MP vec3 n = normal, l = light, c = light_colour, s = specular_colour;
	FABLE_LITE_MP float a = attenuation;
	FABLE_LITE_MP float gate = clamp(FABLE_SPEC_ANGLE_GATE * dot(n, l), 0.0, 1.0);
	return lobe * gate * a * (c / PI) * s;
}

// WHAT CROSSES FROM vertex() TO fragment(): a lite build may carry less (`lite_trim_varyings` and its
// neighbours).
#include "res://src/android/fable_lite_varyings.gdshaderinc"
