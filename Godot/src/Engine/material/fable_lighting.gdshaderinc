// THE lighting model. One copy, included by every surface that Fable lights.
//
// A region is lit by two terms and they come from different places:
//
//   BAKED   — what the level's own bake says reaches this point with the sun ignored. Props
//             read it from the light-probe SH field, terrain reads it from the .lmp lightmap
//             texture. Same term, two storage formats, so the SOURCE is the caller's job and
//             the composition is here.
//   DIRECT  — the theme's main light, evaluated live so it moves with time of day and casts
//             real shadows.
//
// Keeping this in one file is not tidiness. Terrain used to be `unshaded` with the bake
// folded in as `bake * 0.55 + 0.45`, so the ground took no sun, no shadow and no ambient
// while everything standing on it took all three — and a street where the buildings darken
// at dusk over a path that does not reads as "the lighting is broken" rather than as two
// lighting models.

instance uniform vec4 probe_r = vec4(0.0);
instance uniform vec4 probe_g = vec4(0.0);
instance uniform vec4 probe_b = vec4(0.0);

// rgb: the theme's LightmapColour — the guest's `g_LightmapFactorAndSRGBGamma.xyz`.
// w: WHICH BAKE this surface has, because the guest picks between them per instance rather
// than blending, and each arrives by a different route:
//   0 = none              1 = probe SH, in the probe_* instance uniforms
//   2 = COLOR, linear     3 = COLOR, gamma-encoded and needing the decode
instance uniform vec4 lightmap_colour = vec4(1.0, 1.0, 1.0, 0.0);

const float FABLE_BAKE_NONE = 0.0;
const float FABLE_BAKE_PROBE = 1.0;
const float FABLE_BAKE_COLOR_LINEAR = 2.0;
const float FABLE_BAKE_COLOR_GAMMA = 3.0;

/// The gamma the guest applies to a baked vertex colour: `g_LightmapFactorAndSRGBGamma.w`.
/// It is a runtime constant there and 2.2 is what the AMB1 vertex shader bakes in
/// (`exp2(log2(c) * 2.2)`, constant 0x400CCCCD).
const float FABLE_BAKE_GAMMA = 2.2;

// rgb: the theme's AmbientColour already multiplied by its energy.
instance uniform vec4 ambient_colour = vec4(0.0);

/// The probe field's order-2 spherical harmonics, evaluated for a world normal.
///
/// THE BASIS IS D3DX's, AND ITS SIGNS ARE NOT SYMMETRIC. Decompiling `D3DXSHEvalDirection`
/// (0x82D13BB0) gives the four band coefficients directly — it computes 1/sqrt(PI) and
/// sqrt(3)/sqrt(PI) at runtime with `__fsqrts`, which is why searching the image for the
/// literals finds nothing:
///
///     out[0] =  0.2820948            (0x82D13C0C)
///     out[1] = -0.4886025 * y        (0x82D13C20)
///     out[2] = +0.4886025 * z        (0x82D13C28)
///     out[3] = -0.4886025 * x        (0x82D13C30)
///
/// Two things were wrong here before. The band constants were missing entirely, so the flat
/// term came out 3.5x too strong; and the three directional coefficients were paired with the
/// wrong axes in the wrong signs — coefficient 1 goes with y and NEGATED, 2 with z, 3 with x
/// and negated, where this used to read them as plainly x, y, z. That does not look like a bug,
/// it looks like the bake is lit from somewhere else.
///
/// The field is stored in the GUEST's axes — X right, Y forward, Z up — so the normal is
/// carried back into them first. Godot's is Y-up and the level root is mirrored through X on
/// top of that, which is the (x, y, z) -> (-x, z, y) below.
vec3 fable_probe_bake(vec3 world_normal) {
	vec3 gn = vec3(-world_normal.x, world_normal.z, world_normal.y);
	vec4 basis = vec4(0.2820948, -0.4886025 * gn.y, 0.4886025 * gn.z, -0.4886025 * gn.x);
	vec3 sh = vec3(dot(probe_r, basis), dot(probe_g, basis), dot(probe_b, basis));
	return max(sh, vec3(0.0));
}

/// The bake for this surface, whichever of the guest's modes it is carrying. `vertex_colour` is
/// the interpolated COLOR attribute — a per-instance value on a MultiMesh, a per-vertex one on a
/// mesh that was given an authored bake.
vec3 fable_bake(vec3 world_normal, vec4 vertex_colour) {
	float mode = lightmap_colour.w;
	if (mode > 2.5) {
		return pow(max(vertex_colour.rgb, vec3(0.0)), vec3(FABLE_BAKE_GAMMA));
	}
	if (mode > 1.5) {
		return max(vertex_colour.rgb, vec3(0.0));
	}
	if (mode > 0.5) {
		return fable_probe_bake(world_normal);
	}
	return vec3(0.0);
}

/// Baked irradiance plus the theme's flat ambient — the second half of the guest's line.
///
/// The whole composition is `bake * LightmapFactor + GlobalAmbient`, and it is PROVEN from the
/// shipped shader rather than the executable. Recompiling the Xenos microcode in Shaders.sbk
/// gives PSHADER_STANDARDMATERIAL_..._AMB1 a named constant buffer and one line:
///
///     r12.xyz = bake.xyz * g_LightmapFactorAndSRGBGamma.xyz + g_GlobalAmbientAndBrightness.xyz
///
/// So the theme's LightmapColour IS the lightmap factor — the constant says so in its own name.
/// This is why IDA could not close it: the multiply never happens on the CPU, which is exactly
/// why `theme+0x40` looked like a value nothing consumed.
///
/// THE FACTOR IS THE CALLER'S, not applied here, because the two callers are not in the same
/// state. Props multiply it in and the magnitudes agree — the SH lands near 0.17 for this
/// region and 0.17 x 3.54 is 0.60. Terrain does not, because our lightmap binding is still
/// wrong in three ways and applying the factor to the wrong operand clips the ground to white;
/// the reasoning is recorded at that call site.
vec3 fable_ambient(vec3 bake) {
	return bake + ambient_colour.rgb;
}

/// The main light. Lambert, because Fable's main light is one directional source and the
/// specular response is carried by the material's own maps rather than by this term.
///
/// ATTENUATION is where the shadow arrives, so a surface only needs to pass it through.
vec3 fable_direct(vec3 normal, vec3 light, float attenuation, vec3 light_colour, vec3 albedo) {
	return clamp(dot(normal, light), 0.0, 1.0) * attenuation * (light_colour / PI) * albedo;
}
