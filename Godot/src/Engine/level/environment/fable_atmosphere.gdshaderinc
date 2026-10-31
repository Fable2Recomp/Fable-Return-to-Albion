// FOG, WHICH IN THIS GAME IS NOT A FOG.
//
// Fable has no depth-fog ramp and no fog colour in the usual sense. A world surface is attenuated
// by the SAME atmosphere the sky dome draws, and what it is attenuated toward is the sky's own
// in-scatter in that direction. `sky_dome.gdshaderinc` is the other half of this; both sample the
// one in-scatter table and both use the theme's Rayleigh and Mie coefficients as the extinction.
//
// The block below is the guest's, from 0263_pshader_standardmaterial_blt1_glt1_mist0 recompiled
// out of Shaders.sbk (byte-identical in landscapematerial 0007 and ocean_water 0057):
//
//     ps   = -g_AtmosphericParameters(3).x - -r1.y;      // d - FoggingStart
//     p0   = r0.x > 0.0;                                  // GATE, and there is NO else branch
//     if (p0) {
//       r1.x   = saturate(dot(-r6.zxy, g_AtmosphericParameters(2).zxy));   // view . sun
//       r2.xyz = tfetch1D(g_Extinction,   (d - FoggingStart) * c67.w).xyz; // T, RGB
//       r1.xyz = tfetch1D(g_InScattering, r1.x).xyz;                       // the sky, RGB
//       r0.x   = saturate(g_AtmosphericParameters(3).z * r0.x);            // close-fog fade
//       r1.xyz = r0.xxx * (Sky - CloseFogColour) + CloseFogColour;
//       colour = colour + Fx * ((1 - T) * Fy * alpha * C - (1 - T) * colour);
//     }
//
// THREE THINGS THAT LOOK LIKE DETAILS AND ARE NOT:
//
//   - The gate is FoggingStart, not the near distance. Below it there is no fog term at all, and
//     the guest goes further and clears pixel-shader bool b4 for any object whose distance plus
//     radius falls short of it (sub_822109B0, 0x82210ABC).
//   - The distance is RADIAL, `length(worldPos - eyePos)`, recovered in the pixel shader from
//     g_EyePosition. Not a Z depth. Godot's view-space VERTEX gives it for free.
//   - CloseFogColour is a NEAR-FIELD tint only. It fades into the sky's in-scatter over
//     CloseFogMaxDistance, so it governs the first few dozen metres and nothing beyond.
//
// The transmittance is per channel, and that is the whole look: betaRayleigh differs by about
// 3.5x between red and blue, so distance eats blue first. A scalar fog factor cannot produce it.
//
// What is not here, and where it went instead. The guest has a second, independent system on the
// same surfaces — a ground mist slab, height-field integrated with a real Beer-Lambert term, the
// `mist1` half of every shader permutation. It stacks on top of this and is deliberately NOT in
// this file: the authored volumes are emitted as Godot `FogVolume`s and lit by Godot's volumetric
// fog (`Engine_LevelScene`), which is the half of the atmosphere a real participating medium
// models better than a per-surface term can — it takes light from the fixtures standing in it.
// The level-wide `VolumetricFogDensity` goes to ZERO there, because Godot's default 0.05 fills
// every cubic metre once the effect is on.
//
// That conversion is PARTIAL. `MistVolume` parses Falloff, Reciprocal, ScrollU/V, GenScalar1/2
// and PackedX/Y; only the box, Density and Colour reach the `FogMaterial`. Falloff and Reciprocal
// are printed by the `[mist]` line and read nowhere else — Godot's `FogMaterial.HeightFalloff`
// and `EdgeFade` are the obvious homes and nothing has checked whether they mean the same thing.

// The atmosphere is one thing for the whole region, and it reaches thousands of materials, so it
// is published once per frame as global uniforms rather than set per material. Engine_SkyEnvironment
// writes them; project.godot declares them.
global uniform sampler2D atmos_inscatter;

/// betaRayleigh + betaMie. The sum is what the guest's LUT baker exponentiates — it loads both
/// registers of the atmospheric block and adds them per channel before the exp (0x821FE260).
global uniform vec3 atmos_beta;

/// The direction to the sun, in the same axes `sky_dome.gdshaderinc` takes its ray in.
global uniform vec3 atmos_sun;

/// The theme's CloseFogColour, raw. The guest forces its w to zero and never uses it.
global uniform vec3 atmos_close_fog;

/// x: FoggingStart. y: how far the curve is defined for past it. z: 1 / CloseFogMaxDistance.
/// w: the dome exposure, which the fog shares so that the world meets the horizon without a seam.
global uniform vec4 atmos_range;

/// The solved power law: x = optical depth at the far control point, y = 1 / far range, z = the
/// exponent. See Engine_SkyAtmosphere.SolveFogCurve for where the three come from.
global uniform vec3 atmos_curve;

/// Whether the region's theme states a Fogging record at all.
global uniform bool atmos_fog_on;

// Endpoint values are copied from the same atmosphere update as beta/curve. Readiness is
// cleared before those globals change, and enabled only after all five endpoint writes.
// Unsupported curves and the diagnostic switch retain the original shader calculations.
global uniform bool fable_near_fog_precompute;
global uniform bool atmos_near_ready;
global uniform vec3 atmos_near_t0;
global uniform vec3 atmos_near_t1;
global uniform vec3 atmos_near_t2;
global uniform vec3 atmos_near_t3;
global uniform vec3 atmos_near_t4;

/// The remaster's INTERIOR FOG SCALE (`interior_fog_scale`): 1.0 where the level has a sky,
/// the cvar where it has none. Scales optical depth in the Beer-Lambert exponent so values > 1.0
/// physically thicken the fog without clamping or inverting transmittance.
global uniform float fable_interior_fog = 1.0;

/// View-space Z depth (`VolumetricFogLength`, e.g. 128.0m) up to which Godot's built-in 3D
/// Volumetric Fog integrates near-to-mid-field atmosphere in Modern mode; 0.0 when disabled or
/// in Classic mode.
global uniform float fable_volumetric_fog_end = 0.0;

/// The guest's `g_AtmosphericFactors` — x scales the whole fog blend, y scales the fog colour.
///
/// These are the only two components of that constant any shipped shader reads, and they are the
/// one part of the guest's fog line whose CPU source we have not found: the value reaches pixel
/// register c28 through a numeric binding id, and the id-to-register map lives in the shader bank
/// rather than in the executable. Until that is pinned, x is a HOST SETTING occupying a slot the
/// guest owns — see Engine_SkyAtmosphere.FogBlendFactor. It is not a fudge factor bolted onto the
/// side of the model; it is the guest's own damping term with a value we do not yet know.
global uniform vec2 atmos_factors;

// Explicit globals avoid a dynamically indexed uniform array on Mobile. The near branch
// supplies only indices zero through four, including the endpoint at the far transition.
vec3 fable_atmosphere_near_knot(int index) {
	if (index == 0) { return atmos_near_t0; }
	if (index == 1) { return atmos_near_t1; }
	if (index == 2) { return atmos_near_t2; }
	if (index == 3) { return atmos_near_t3; }
	return atmos_near_t4;
}

/// The two terms of the guest's line for this fragment. False when it takes no fog at all.
///
/// `view_vertex` is the fragment's VIEW-space position, so its length is already the guest's
/// radial distance and no world transform is needed to recover it.
///
/// `close_fog` false is the TREE line: the tree vertex shader (blob 0536 1379-1411) takes the
/// in-scatter table times the additive factor and never reads the close-fog colour.
bool fable_atmosphere_terms_for(
		vec3 view_vertex,
		mat4 inv_view,
		bool close_fog_blend,
		out vec3 transmittance,
		out vec3 fog_colour) {
	transmittance = vec3(1.0);
	fog_colour = vec3(0.0);

	if (!atmos_fog_on) {
		return false;
	}

	float radial_dist = length(view_vertex);
	float d = radial_dist - atmos_range.x;
	if (d <= 0.0) {
		return false;
	}

	float fog_scale = max(fable_interior_fog, 0.0);
	if (fog_scale <= 0.0) {
		return false;
	}

	const float knot = 7.8125;
	float dl = max(d - (0.5 * knot), 0.0);

	// The world direction from the eye toward this fragment, then into the axes the dome works in.
	// Godot (x, y, z) maps to the guest's dome axes as (-x, z, y) — matching sky.gdshader and sky_dome.gdshaderinc.
	vec3 world_dir = normalize((inv_view * vec4(view_vertex, 0.0)).xyz);
	float sun_dot = clamp(
		dot(normalize(atmos_sun), vec3(-world_dir.x, world_dir.z, world_dir.y)), 0.0, 1.0);
	// Clamped to the edge texel centres: a global sampler takes no hint and Godot's default
	// REPEATS, so at u = 0 linear filtering would blend in the sun-facing end texel. The guest's
	// tfetch1D addresses this LUT with clamp.
	float half_texel = 0.5 / float(textureSize(atmos_inscatter, 0).x);
	vec3 sky = texture(atmos_inscatter,
		vec2(clamp(sun_dot, half_texel, 1.0 - half_texel), 0.5)).rgb;
	vec3 close_fog = atmos_close_fog;

	// CLAMPED IN DISTANCE, NOT IN THE RATIO. The guest's LUT spans a fixed 2000 units and its
	// clamp addressing freezes the curve past the end — but the ratio the power is taken of is
	// against the FAR CONTROL POINT, which is usually nearer than that, so it legitimately exceeds
	// one and the fog keeps thickening beyond the far distance. Clamping it to one instead caps
	// the fog at whatever the theme happened to state as its far density.
	//
	// HALF A KNOT LATE, as the guest reads its table: each texel holds the curve at its LEFT edge
	// (i x 7.8125, Engine_ClassicAtmosphere) and the fetch filters about texel CENTRES, so the
	// guest's fog at distance d is the curve at d - 3.906 — exactly clear for the first 3.9 units
	// past FoggingStart, where the unshifted curve already put up to a fifth too much depth.
	vec3 raw;
#ifdef FABLE_LITE
	// LITE: the published knots whenever they are ready (every supported curve), and the far
	// curve alone otherwise. Only the analytic knot fallback for an unsupported curve, and the
	// `fable_near_fog_precompute` diagnostic switch, are compiled out.
	if (dl < knot * 4.0 && atmos_near_ready) {
#else
	if (dl < knot * 4.0 && fable_near_fog_precompute && atmos_near_ready) {
#endif
		float below = floor(dl / knot) * knot;
		int index = int(floor(dl / knot));
		raw = mix(fable_atmosphere_near_knot(index), fable_atmosphere_near_knot(index + 1), (dl - below) / knot);
		if (abs(fog_scale - 1.0) > 1e-4) {
			raw = pow(max(raw, vec3(1e-6)), vec3(fog_scale));
		}
	} else {
		// Keep the original far expression and unsupported-state behavior. In particular,
		// CPU/GPU pow(0,0) behavior is not repaired by the endpoint optimization.
		float t = min(dl, atmos_range.y) * atmos_curve.y;
		float optical_depth = atmos_curve.x * pow(max(t, 0.0), atmos_curve.z) * fog_scale;
		raw = exp(-atmos_beta * optical_depth);
#ifndef FABLE_LITE
		if (dl < knot * 4.0) {
			// Interpolated as the LUT is: in transmittance, between the knots either side.
			float below = floor(dl / knot) * knot;
			float depth_below = atmos_curve.x * pow(min(below, atmos_range.y) * atmos_curve.y, atmos_curve.z) * fog_scale;
			float depth_above = atmos_curve.x * pow(min(below + knot, atmos_range.y) * atmos_curve.y, atmos_curve.z) * fog_scale;
			raw = mix(exp(-atmos_beta * depth_below), exp(-atmos_beta * depth_above), (dl - below) / knot);
		}
#endif
	}

	// When Godot's built-in 3D Volumetric Fog uniform medium is active (`fable_volumetric_fog_end > 0.0`),
	// `fable_volumetric_fog_end` stores the uniform 3D scattering medium's base density `sigma_vol`
	// integrated across the `0 .. 128m` froxel frustum from the camera (`radial_dist`).
	// Subtract the full froxel optical depth (`sigma_vol * min(radial_dist, 128.0)`) so near-field 3D
	// volumetric fog never compounds onto the distance analytic fog curve.
	if (fable_volumetric_fog_end > 0.0) {
		float vol_dist = clamp(radial_dist, 0.0, 128.0);
		vec3 cancel_depth = vec3(fable_volumetric_fog_end * vol_dist);
		raw = min(raw * exp(cancel_depth), vec3(1.0));
	}

	// Fx AND Fy FOLDED IN HERE, so that `transmittance` is the EFFECTIVE one and every caller —
	// including the ones attenuating a specular tint — takes the damping automatically. The guest's
	// line is
	//
	//     colour + Fx*( (1-T)*Fy*alpha*C - (1-T)*colour )
	//   = colour*(1 - Fx*(1-T)) + Fx*(1-T)*Fy*alpha*C
	//
	// so the surface's factor is `1 - Fx*(1-T)` and the added term carries Fy. Written this way the
	// apply below is a plain lerp and no caller has to know the algebra.
	transmittance = vec3(1.0) - (atmos_factors.x * (vec3(1.0) - raw));
	fog_colour = (close_fog_blend ? mix(close_fog, sky, clamp(d * atmos_range.z, 0.0, 1.0)) : sky) * atmos_factors.y;
	return true;
}

bool fable_atmosphere_terms(
		vec3 view_vertex,
		mat4 inv_view,
		out vec3 transmittance,
		out vec3 fog_colour) {
	return fable_atmosphere_terms_for(view_vertex, inv_view, true, transmittance, fog_colour);
}

/// Applies those terms as the guest's `colour*T + (1-T)*C*alpha`.
///
/// SPLIT ACROSS ALBEDO AND EMISSION, because Godot gives a spatial shader no hook after lighting.
/// Every lit term is proportional to ALBEDO — Godot multiplies DIFFUSE_LIGHT by it after `light()`
/// returns — so scaling ALBEDO scales the lit result, and EMISSION carries both the scaled bake
/// and the in-scatter that replaces what the extinction removed.
///
/// A SPECULAR LOBE IS NOT PROPORTIONAL TO ALBEDO and so is not covered by this; each caller that
/// has one attenuates its own specular tint by the same transmittance. Doing it here instead
/// would mean this function knowing which of three different specular arrangements the caller
/// uses.
///
/// `alpha` is the diffuse map's alpha, which the guest folds into the in-scatter
/// (`r1.w = g_AtmosphericFactors.y * r3.x`) and which is 1 on every opaque surface.
void fable_atmosphere_apply(
		vec3 transmittance,
		vec3 fog_colour,
		float alpha,
		inout vec3 albedo,
		inout vec3 emission) {
	albedo *= transmittance;
	emission = (emission * transmittance) + ((vec3(1.0) - transmittance) * fog_colour * alpha);
}
