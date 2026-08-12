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
// Keeping this in one file is not tidiness. A second lighting model for terrain — `unshaded`
// with the bake folded in — gives the ground no sun, no shadow and no ambient while
// everything standing on it takes all three, and a street whose buildings darken at dusk over
// a path that does not reads as "the lighting is broken" rather than as two models.

instance uniform vec4 probe_r = vec4(0.0);
instance uniform vec4 probe_g = vec4(0.0);
instance uniform vec4 probe_b = vec4(0.0);

// rgb: the theme's LightmapColour — the guest's `g_LightmapFactorAndSRGBGamma.xyz`.
// w: WHICH BAKE this surface has, because the guest picks between them per instance rather
// than blending, and each arrives by a different route:
//   0 = none                          1 = probe SH, in the probe_* instance uniforms (guest AMB2)
//   2 = COLOR, single decode          3 = COLOR, double decode (guest AMB1)
//
// The names say SINGLE and DOUBLE rather than LINEAR and GAMMA because no bake reaches the light
// undecoded — see `fable_bake`. Mode 2 differs from mode 3 by the vertex shader's extra 2.2, not
// by having no transfer at all.
instance uniform vec4 lightmap_colour = vec4(1.0, 1.0, 1.0, 0.0);

const float FABLE_BAKE_NONE = 0.0;
const float FABLE_BAKE_PROBE = 1.0;
const float FABLE_BAKE_COLOR_SINGLE = 2.0;
const float FABLE_BAKE_COLOR_DOUBLE = 3.0;

/// The gamma the PIXEL shader applies to a bake: `g_LightmapFactorAndSRGBGamma.w`.
///
/// IT IS 1.3, NOT 2.2, and this was wrong in a way that darkened every baked prop in the region.
/// The constant is uploaded by `sub_821B16F8`, verified in raw disassembly rather than taken from
/// a summary:
///
///     821b17cc  lfs   f0, flt_8209950C          ; 0x3FA66666 = 1.3
///     821b17d4  stfs  f0, var_40(r1)            ; -> the float4's w
///     821b1810  stfs  f0, 0x18D0(r11)           ; r11 = g_D3DDevice_PresentModule
///
/// and 0x18D0 is 6352 = 6016 + 21*16, i.e. pixel constant register c21 — which the recompiled
/// microcode names for us: `float4 g_LightmapFactorAndSRGBGamma : packoffset(c21)`. All four
/// components are stored (0x18D0/0x18D4/0x18D8/0x18DC), xyz being the theme's LightmapColour.
const float FABLE_BAKE_GAMMA = 1.3;

/// The EXTRA decode the AMB1 vertex shader applies before the pixel shader's, and the reason 2.2
/// looked right for so long.
///
/// Blob 0244 is `pow(iColor0, 2.2)` — `log2`, multiply by c255.x = 0x400CCCCD, `exp2` — and the
/// pixel shader then raises THAT by 1.3. The two compose, so a per-vertex bake is decoded by
/// 2.2 * 1.3 = 2.86 in total, and the engine knows it: `sub_821B16F8` uploads the same colour with
/// a literal 2.86 in w to the vertex side for the path that does it in one step.
///
/// WHICH BAKE GETS IT IS DECIDED BY THE VERTEX SHADER, and the three variants are distinct:
///   amb1 (blob 0244) reads `iColor0` and applies this 2.2 — a per-vertex authored bake.
///   amb2 (blob 0248) reads `g_PRTConstants` and applies NOTHING — this is the probe/SH path.
///   amb3 (blob 0255) reads neither and applies nothing.
/// The pixel shader cannot tell them apart; it raises whatever reached oColor0 by 1.3. So the
/// probe bake takes 1.3 alone and only the vertex-colour bake takes the pair.
const float FABLE_VERTEX_BAKE_GAMMA = 2.2;

// rgb: the theme's AmbientColour already multiplied by its energy.
instance uniform vec4 ambient_colour = vec4(0.0);

/// rgb: the theme's AmbientNormalMapDarkeningColour — how dark the ambient goes where a mapped
/// normal turns away from the surface. WHITE IS THE NEUTRAL VALUE, not black: the guest's term
/// is `lerp(this, 1, ndotn^2)`, so this colour is the FLOOR the ambient falls to, and a default
/// of white leaves a surface with no normal map exactly as it was.
///
/// It carries no Factor. This is the only lighting colour the guest registers as type 1 rather
/// than type 2 (`dword_8349EB8C = 1` at 0x83288CA0); the type-1 arm reads Red, Green and Blue,
/// forces w to 1.0 and scales by 1/255, and never looks for the Factor field the type-2 colours
/// have.
instance uniform vec4 darkening_colour = vec4(1.0);

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
/// TWO THINGS ARE EASY TO GET WRONG HERE, and both present as a bake lit from somewhere else
/// rather than as a bug. The band constants are not optional — without them the flat term is
/// 3.5x too strong. And the three directional coefficients are NOT plainly x, y, z:
/// coefficient 1 goes with y and NEGATED, 2 with z, 3 with x and negated.
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
///
/// EVERY MODE TAKES THE PIXEL-SIDE GAMMA. The pixel shader cannot see which vertex shader
/// produced oColor0 — it raises whatever arrived by `g_LightmapFactorAndSRGBGamma.w` and moves
/// on — so the exponent is not a property of the bake's storage. Mode 2 used to return its colour
/// raw, which is the one reading the microcode does not support.
vec3 fable_bake(vec3 world_normal, vec4 vertex_colour) {
	float mode = lightmap_colour.w;
	if (mode > 2.5) {
		// The vertex-colour bake, decoded twice: 2.2 in the AMB1 vertex shader, then 1.3 here.
		return pow(
			max(vertex_colour.rgb, vec3(0.0)),
			vec3(FABLE_VERTEX_BAKE_GAMMA * FABLE_BAKE_GAMMA));
	}
	if (mode > 1.5) {
		return pow(max(vertex_colour.rgb, vec3(0.0)), vec3(FABLE_BAKE_GAMMA));
	}
	if (mode > 0.5) {
		// The PRT probe, which its own vertex shader (AMB2) hands over undecoded.
		return pow(fable_probe_bake(world_normal), vec3(FABLE_BAKE_GAMMA));
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
///
/// THE SUN IS NOT A SPECIAL CASE IN THE GUEST, and that is why this same function now carries the
/// region's placed lights too. There is no separate main-light constant anywhere in the shipped
/// microcode: across all 164 `pshader_{standard,landscape,repeated,tree,fur}material_*` blobs, not
/// one references a g_LightDirection or g_LightColour. Every light — sun, torch, lantern — is a
/// slot in ONE array, `g_GlobalLightParamsPS` at pixel register c80, and the `BLT<N>` in a
/// permutation's name is how many of those slots it shades. So the accumulation Godot performs by
/// calling `light()` once per light IS the guest's sum over slots, and a placed light travels
/// exactly the arithmetic below.
///
/// WHAT WE DO NOT REPRODUCE IS THE FALLOFF CURVE, and it is worth being precise about the gap
/// rather than quietly living with it. The guest's distance term is LINEAR, one radius, no
/// inverse-square — from `0263_pshader_standardmaterial_blt1_glt1_mist0`, with c255.x = 1.0:
///
///     r2.xzw = -r2.xyz * g_GlobalLightParamsPS(0).www + g_GlobalLightParamsPS(0).xyz;
///     r1.x   = dot(r2.wxz, r2.wxz);
///     r0.w   = sqrt(abs(r1.x));                                          // d
///     r1.x   = saturate(-r0.w * g_GlobalLightParamsPS(16).x + c255.x);   // saturate(1 - d/range)
///
/// and every one of the 260 blobs touching that slot has the same form. The two combine through a
/// per-light scalar S that also selects directional: `local = lerp(1, cone * atten, S)`, with
/// S = 1 for point and spot and S = 0 for the sun.
///
/// THE DENOMINATOR IS NOT THE AUTHORED RANGE ON ITS OWN. Slot 16's value is built on the CPU by
/// the per-light fill at vtable+0x38 — `sub_8223B1B8` for point, `sub_82A6CDD0` for spot — and both
/// divide by a PRODUCT:
///
///     8223b24c  lfs   f11, 0x1C4(r3)     ; a second scale on the render light
///     8223b254  lfs   f10, 0x140(r3)     ; the authored Range
///     8223b25c  fmuls f9, f11, f10
///     8223b264  fdivs f8, f0, f9         ; f0 = 1.0  ->  A = 1 / (scale * Range)
///
/// The same product is the light's bounding-sphere radius in `sub_82AB6670`, so `scale * Range` is
/// its true reach.
///
/// +0x1C4 DEFAULTS TO 1.0, so our OmniRange — the authored Range alone — is right. The render
/// light's constructor `sub_82B1BFD0` initialises both scales explicitly:
///
///     82b1c068  *(float *)(r31 + 448) = 1.0    ; +0x1C0, the colour scale
///     82b1c070  *(float *)(r31 + 452) = 1.0    ; +0x1C4, the range scale
///
/// so `scale * Range == Range` and, on the colour side, `sub_8223B1B8` splats +0x1C0 over the
/// premultiplied colour at +0xD0 to no effect. Both were previously recorded here as unknown.
///
/// NOT FULLY CLOSED, and the gap is worth stating: the scan that found no other write to these
/// offsets looked for scalar stores. A vector store covering 0x1C0..0x1CF would not appear in a
/// `stfs`-displacement search, so "nothing ever overrides the default" is inferred, not proven.
///
/// The cone IS fully recovered, from the spot fill: `K = cos(HalfAngle)` and
/// `M = 1 / (1 - cos(HalfAngle))` (0x82A6CFB0/0x82A6CFB4), so the shader's
/// `saturate((dot(L, axis) - K) * M)` is `saturate((cos - cos θ) / (1 - cos θ))` — a linear ramp
/// from the cone edge to the axis, from ONE angle. There is no inner/outer pair anywhere.
/// Point and directional lights store the identity K = -1, M = 1.
///
/// The per-light COLOUR needs no work from us: the guest premultiplies it on the CPU at primitive
/// construction, `colour = LightColour[rgb]/255 * Intensity * IntensityFadeMultiplier`
/// (`sub_825C2108`), which is the same product Godot forms from LightColor x LightEnergy.
///
/// Seven is a hard cap, not a convention: `sub_82B1EDC0` reserves 36 constant registers (576 bytes)
/// and clears exactly seven slots, and `sub_82B1EE68` buckets the gathered count through the table
/// at 0x833216F4 = {1,1,2,4,4,7,7,7,0} to pick the BLT1/2/4/7 permutation.
///
/// Godot's own omni falloff is a smooth quartic window with a decay exponent, not that ramp, so a
/// torch reads as a slightly larger and softer pool than the original. Closing it needs more than
/// a better constant: `light()` is handed ATTENUATION with the SHADOW already multiplied in and is
/// given no distance, so the guest's curve cannot be recovered there — it would take evaluating
/// the lights in `fragment()` from our own uniforms, which is a real design change and would give
/// up Godot's clustering and shadow atlas with it.
/// The guest's distance falloff, recovered from Godot's.
///
/// FABLE'S RAMP IS LINEAR AND GODOT'S IS NOT, and the difference is not cosmetic: at half range
/// the guest is at 0.50 and Godot is at 0.88, so every torch read as a far larger, flatter pool of
/// light than the original casts.
///
///     r0.w  = sqrt(abs(r1.x));                                        // d
///     r1.x  = saturate(-r0.w * g_GlobalLightParamsPS(16).x + c255.x);  // saturate(1 - d/range)
///
/// with the same form in all 260 blobs that touch that slot.
///
/// WE INVERT RATHER THAN REPLACE, because Godot has to keep owning light selection, culling,
/// clustering and the shadow atlas — and `light()` is handed a direction and an attenuation but
/// never the light's position, so the distance cannot be measured here. It can be RECOVERED. With
/// the decay exponent set to zero — which is why `Engine_LevelLights` sets OmniAttenuation and
/// SpotAttenuation to 0 — Godot's term is exactly
///
///     A = (1 - (d/r)^4)^2
///
/// which solves in closed form for the ratio, and the guest's ramp follows:
///
///     d/r    = (1 - sqrt(A))^(1/4)
///     linear = 1 - d/r
///
/// This is an identity, not a fit. It is the one place in this file that depends on OUR renderer's
/// internals rather than on the guest's, so it is pinned to the Godot version in this tree; if
/// Godot ever changes `get_omni_attenuation`, this changes with it.
///
/// THREE THINGS MAKE IT SAFE, and each was checked rather than hoped for:
///
///   - EVERY OMNI LIGHT IS UNSHADOWED, so its ATTENUATION carries nothing but distance. That is
///     not luck: `sub_825C0CC0` reads CastShadows only inside the `LightType == 1` branch, so a
///     point light's shadow flag is a field the guest never looks at. All 83 shadowed lights in
///     the shipped set are spots. Godot folds shadow INTO ATTENUATION, so this is what makes the
///     inversion exact instead of merely close.
///   - THE SUN MUST NOT BE INVERTED. A directional light has no distance term — the guest sets its
///     per-light scalar S to 0, collapsing `lerp(1, cone*atten, S)` to 1 — so its ATTENUATION is
///     shadow alone and is already correct. Hence the `directional` branch.
///   - A SPOT'S CONE IS ALREADY EXACT AND SURVIVES THIS. Godot's cone term is
///     `1 - ((1 - cos)/(1 - K))^p`, which at p = 1 is algebraically `(cos - K)/(1 - K)` — the
///     guest's `saturate((cos - K) * M)` with `M = 1/(1 - K)`, the very value the spot fill
///     computes at 0x82A6CFB0. So the cone needs no correction, only `SpotAngleAttenuation = 1`.
///     Inside the beam the cone is 1 and this inversion is exact; only the narrow rim, where the
///     cone and distance terms arrive multiplied together and cannot be separated, is approximate.
///
/// The guest's seven-light cap is NOT reproduced, deliberately. Because the ramp reaches exactly
/// zero at the range, a light past its own radius contributes nothing at all, so the cap only
/// changes the picture where more than seven radii overlap one point. Measured across the shipped
/// set: the worst overlap anywhere is 11, and only 4 of 81 lit levels exceed 7 at any point. The
/// other 77 are identical either way.
float fable_attenuation(float godot_attenuation, bool directional) {
	if (directional) {
		return godot_attenuation;
	}

	float a = clamp(godot_attenuation, 0.0, 1.0);
	float ratio = pow(max(1.0 - sqrt(a), 0.0), 0.25);
	return clamp(1.0 - ratio, 0.0, 1.0);
}

/// NO ALBEDO HERE — GODOT APPLIES IT, to whatever `light()` returns.
///
/// The Forward+ fragment shader composites with an unconditional `diffuse_light *= albedo`
/// (scene_forward_clustered.glsl:3048), OUTSIDE the `#ifdef LIGHT_CODE_USED` branch, so a custom
/// `light()` gets it too — which is why Godot's own documented example multiplies by LIGHT_COLOR
/// and attenuation and stops. Passing ALBEDO in as well squares it a second time on top of the
/// guest's texture decode (`alb.rgb *= alb.rgb`), composing albedo^4 against the guest's albedo^2.
///
/// The `/ PI` IS correct and is a unit conversion: Godot defines LIGHT_COLOR as
/// `colour * energy * PI` (light_storage.cpp:761, non-physical mode) precisely so a
/// physically-based `light()` can divide it out. Dividing recovers `colour * energy` — the theme's
/// MainLightColour, and what the guest puts in `g_GlobalLightParamsPS`. The guest itself applies no
/// 1/PI anywhere: the constant appears in none of the 589 shipped shader blobs.
vec3 fable_direct(vec3 normal, vec3 light, float attenuation, vec3 light_colour) {
	return clamp(dot(normal, light), 0.0, 1.0) * attenuation * (light_colour / PI);
}

/// How much the guest's NdotL gate opens per unit of NdotL: `g_NormalMapLightAngleConstants.w`.
///
/// NOT RECOVERED FROM THE BINARY, and it is the one number in this file that is ours rather than
/// the game's. The constant lives at pixel register c23 (`packoffset(c23)` in the recompiled
/// microcode), and c23 reaches the GPU only through the shader binding table — which is `.data`,
/// is entirely zero in the image, and is populated at shader-load time from each shader's own
/// constant table. An exhaustive scan for a direct store to its shadow slot found none, exactly as
/// for c20. It cannot be read statically; it needs a runtime observation.
///
/// 1.0 is the NEUTRAL choice, not a tuned one: it collapses `saturate(k * NdotL)` to `saturate(NdotL)`,
/// the plain geometric term, so the gate neither sharpens nor softens anything until the real value
/// is measured. Raising it makes the specular reach further round the terminator.
const float FABLE_SPEC_ANGLE_GATE = 1.0;

/// The material's own specular highlight — the guest's `_sp1` and `_sp2` permutations.
///
/// PHONG, AND THE GUEST SPELLS IT OUT rather than using a half-vector:
///
///     r2.xyw = -N * (2 * dot(N, L)) + L      // = reflect(L, N)
///     r6.x   = saturate(dot(-V, r2.xwy))     // = saturate(dot(V, reflect(-L, N)))
///
/// with V pointing toward the eye, which is Godot's VIEW. `_sp1` fixes the exponent by squaring
/// twice (cos^4); `_sp2` raises it to a per-pixel power through log2/exp2. Only `_sp2` is modelled
/// here because it subsumes `_sp1` when the exponent lands on 4.
///
/// THE SPECULAR MAP IS NOT A MASK ON THE DIFFUSE — it is the term's own colour, and its ALPHA is
/// the exponent selector. The composition is two separate multiply-adds:
///
///     r6.xyz = specTex^2 * (specScalar * LightColour) + ambient
///     r2.xyz = albedo^2  * (diffScalar * LightColour) + r6.xyz
///
/// so a material with a black specular map takes no highlight however shiny its exponent says it
/// is, and that is why binding nothing here has to mean black rather than white.
///
/// LIGHT_COLOR IS DIVIDED BY PI for the same reason `fable_direct` does it: Godot hands `light()`
/// a colour already multiplied by PI, and the guest multiplies by the raw one.
vec3 fable_specular(
		vec3 normal,
		vec3 light,
		vec3 view,
		float attenuation,
		vec3 light_colour,
		vec3 specular_colour,
		float specular_power) {
	float cosine = clamp(dot(view, reflect(-light, normal)), 0.0, 1.0);
	float lobe = pow(max(cosine, 1e-6), max(specular_power, 1.0));
	float gate = clamp(FABLE_SPEC_ANGLE_GATE * dot(normal, light), 0.0, 1.0);
	return lobe * gate * attenuation * (light_colour / PI) * specular_colour;
}
