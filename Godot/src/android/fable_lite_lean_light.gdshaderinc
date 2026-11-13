// THE PHONE'S LEAN LIGHT AO (`lite_lean_light_ao`, AndroidLeanTerms.cs beside this file, on by
// default): fable_direct_ao_for_light and fable_specular_ao, what light() does with the combined AO
// for every light that reaches a pixel, without the clamps the AO's range already settles. Included
// by fable_lite_lighting.gdshaderinc in place of upstream's two, only in a lite build with baked AO and
// direct AO compiled in (FABLE_LITE_LEAN_LIGHT_AO without FABLE_LITE_NO_BAKED_AO or _NO_DIRECT_AO,
// where upstream's return 1).
//
// THE RANGE. Every caller hands over the combined AO a lite body wrote from fable_apply_ao_strength
// (v_combined_ao in both light()s; the same value in the phone's shader lights and sun), which returns
// 1 or max(raw, 1e-4) (or that to a positive power): always within [1e-4, 1]. Upstream's clamps of it
// to [0, 1], and the floor at 0 of the specular occlusion (ao^2 + 0.15 ao of a positive ao), are then
// the identity. The same picture bit for bit; where a driver's mix(a, b, 1.0) lands b an ulp above 1,
// every result below still rounds to upstream's.
// Measured (Mobile renderer SPIR-V, the S10e's Low lean-modern composition, longest path): a culled
// prop's sun -4 ALU of 102 and each lamp -3 of 250 (-1 and -2 without specular), the ground's -1 / -2.

/// fable_direct_ao_for_light for a combined AO within [0, 1].
float fable_direct_ao_for_light(float combined_ao, bool directional) {
	if (directional) {
		// fable_direct_ao: max(ao, 0) is ao.
		FABLE_LITE_MP float w = max(0.0, min(1.0, FABLE_UNIFIED * FABLE_AO_LIGHT_AFFECT));
		FABLE_LITE_MP float ao = combined_ao;
		return mix(1.0, ao, w);
	}
	// The clamp of the AO to [0, 1] is the AO.
	float occ = 1.0 - combined_ao;
	float active_gate = step(0.001, FABLE_AO_LIGHT_AFFECT);
	float local_w = max(0.0, min(0.82, FABLE_UNIFIED * active_gate * max(FABLE_AO_LIGHT_AFFECT * 2.2, 0.58)));
	return 1.0 - local_w * occ * mix(0.55, 1.0, occ);
}

/// fable_specular_ao for a combined AO within [0, 1]: its clamp is the AO, and the occlusion of a
/// positive AO is positive, so only its ceiling stays.
float fable_specular_ao(float combined_ao) {
	float ao_c = combined_ao;
	float spec_occ = min(1.0, ao_c * ao_c + 0.15 * ao_c);
	return mix(1.0, spec_occ, max(0.0, min(1.0, FABLE_UNIFIED)));
}
