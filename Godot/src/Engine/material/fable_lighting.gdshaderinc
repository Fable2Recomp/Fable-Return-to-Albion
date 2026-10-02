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

// rgb: this instance's own bake dial (1.0 unless the prop bake-weight option reaches it); the
// theme's LightmapColour is `fable_lightmap_tint`, read through `fable_lightmap_rgb()`.
// w: WHICH BAKE this surface has, because the guest picks between them per instance rather
// than blending, and each arrives by a different route:
//   0 = none                          1 = probe SH, in the probe_* instance uniforms (guest AMB2)
//   2 = COLOR, single decode          3 = COLOR, double decode (guest AMB1)
//
// The names say SINGLE and DOUBLE rather than LINEAR and GAMMA because no bake reaches the light
// undecoded — see `fable_bake`. Mode 2 differs from mode 3 by the vertex shader's extra 2.2, not
// by having no transfer at all.
instance uniform vec4 lightmap_colour = vec4(1.0, 1.0, 1.0, 0.0);

/// THE THEME'S LIGHTING IS GLOBAL, published every frame by Engine_LevelLighting. As instance
/// uniforms these were rewritten on every node once per five game minutes, spread over many
/// frames, so each step crossed the world as a visible wave.
///
/// The theme's LightmapColour — the guest's `g_LightmapFactorAndSRGBGamma.xyz`.
global uniform vec3 fable_lightmap_tint;

vec3 fable_lightmap_rgb() {
	return lightmap_colour.rgb * fable_lightmap_tint;
}

/// The region's object lightmaps in one texture (the amb3 interiors), published by
/// Engine_LevelBakedLighting when the container's atlas builds.
global uniform sampler2D fable_prop_lightmaps;

/// This instance's window into the atlas: offset.xy, scale.zw. Zero size = no texture bake.
instance uniform vec4 lightmap_rect = vec4(0.0);

/// THE REMASTER'S BAKE WEIGHT (`baked_light_weight` in the launcher; 1.0 = shipped balance).
/// Applied to the AUTHORED bakes only — vertex colours, instance colours, texture lightmaps —
/// never to the probe field or the flat GlobalAmbient. Scaling only the terrain's bake left
/// the bridge glowing its full golden bake beside a weighted ground and probe-lit rocks:
/// three adjacent surfaces in three different lighting worlds. One weight, every painted bake.
global uniform float fable_bake_weight;

/// The modern model's SKY TETHER for the probe field. rgb = the live sky-derived ambient (the
/// same value fable_ambient_colour carries, in the same ambient_light_scale space); w = how far the
/// probe bake is pulled toward it. The probes are a baked-era snapshot — authored locality
/// (interiors dark, under-canopy dim) worth keeping, level and tint worth retiring — so the
/// blend keeps their shape at 1-w while the sky sets the tone. w = 0 in the fable model.
global uniform vec4 fable_sky_ambient;

/// 1.0 in the modern model when the level has no sky (a cave, a windowless interior): the
/// authored bakes are then the only light there was, and they ride at full strength instead of
/// the modern bake weight, which was balanced for exteriors where the live sun carries the rest.
global uniform float fable_interior = 0.0;

/// The remaster's interior BAKE lift (`interior_bake_scale`), 1.0 where the level has a sky.
global uniform float fable_interior_bake = 1.0;

/// The placed lights' records, one texel per light: (world x, y, z, range). Published by
/// Engine_LightTable. `light()` is given no position, so a light names its texel through the one
/// per-light scalar that does reach it: SPECULAR_AMOUNT, which Godot fills with 2 × LightSpecular
/// (light_storage.cpp, `specular_amount = param[SPECULAR] * 2.0`). The host sets
/// LightSpecular = slot + 2; anything lower — Godot's default 0.5, an FX light — has no record.
/// Our shaders use SPECULAR_AMOUNT for nothing else.
global uniform sampler2D fable_light_table;

/// The fragment's world position, assigned at the end of every including shader's vertex().
varying vec3 fable_world;

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

/// The theme's AmbientColour already multiplied by its energy.
global uniform vec3 fable_ambient_colour;

/// The theme's AmbientNormalMapDarkeningColour — how dark the ambient goes where a mapped
/// normal turns away from the surface. WHITE IS THE NEUTRAL VALUE, not black: the guest's term
/// is `lerp(this, 1, ndotn^2)`, so this colour is the FLOOR the ambient falls to, and a default
/// of white leaves a surface with no normal map exactly as it was.
///
/// It carries no Factor. This is the only lighting colour the guest registers as type 1 rather
/// than type 2 (`dword_8349EB8C = 1` at 0x83288CA0); the type-1 arm reads Red, Green and Blue,
/// forces w to 1.0 and scales by 1/255, and never looks for the Factor field the type-2 colours
/// have.
global uniform vec3 fable_darkening;

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

/// The guest's PRT ambient for one vertex: rgb, and a = 1 when the mesh carries transfer.
///
/// VERTEX STAGE. The AMB2 vertex shader computes `P0 + dot(T, P1..3)` per channel, where the
/// P are the model's cluster mean and PCA bases dotted with the entity's probe
/// (`sub_8225DC80`). `Engine_MdlTransfer` undoes the compression at mesh build, so CUSTOM0..2
/// hold the vertex's own transfer vector per channel and this is `dot(t_c, L_c)`. The transfer
/// carries the surface's self-occlusion, which a lookup along the normal cannot.
///
/// The guest dots the two raw, so they share one basis — the one `fable_probe_bake` states.
/// Transfer is in MODEL space and the probe in world: the linear band is carried to world as
/// a normal is (the loader's x, z, -y, then the instance rotation) and met there by the same
/// guest-axis map the normal lookup uses. `sub_8225DC80` rotates the probe into model space
/// instead, and only for the `sub_82219AB8` draw class; rotating always is ours.
///
/// A mesh without the channels reads zeros, which no lit vertex holds: its mean is never zero.
vec4 fable_transfer(vec4 tr, vec4 tg, vec4 tb, mat3 model) {
	float present = step(1e-6, dot(abs(tr) + abs(tg) + abs(tb), vec4(1.0)));
	mat3 turn = mat3(normalize(model[0]), normalize(model[1]), normalize(model[2]));
	vec3 lr = turn * vec3(-tr.w, tr.z, tr.y);
	vec3 lg = turn * vec3(-tg.w, tg.z, tg.y);
	vec3 lb = turn * vec3(-tb.w, tb.z, tb.y);
	vec3 rgb = vec3(
		tr.x * probe_r.x + dot(vec3(-probe_r.w, -probe_r.y, probe_r.z), vec3(-lr.x, lr.z, lr.y)),
		tg.x * probe_g.x + dot(vec3(-probe_g.w, -probe_g.y, probe_g.z), vec3(-lg.x, lg.z, lg.y)),
		tb.x * probe_b.x + dot(vec3(-probe_b.w, -probe_b.y, probe_b.z), vec3(-lb.x, lb.z, lb.y)));
	return vec4(rgb, present);
}

/// The bake for this surface, whichever of the guest's modes it is carrying. `vertex_colour` is
/// the interpolated COLOR attribute — a per-instance value on a MultiMesh, a per-vertex one on a
/// mesh that was given an authored bake.
///
/// EVERY MODE TAKES THE PIXEL-SIDE GAMMA. The pixel shader cannot see which vertex shader
/// produced oColor0 — it raises whatever arrived by `g_LightmapFactorAndSRGBGamma.w` and moves
/// on — so the exponent is not a property of the bake's storage. Mode 2 used to return its colour
/// raw, which is the one reading the microcode does not support.
/// The remaster's authored-bake weight, in one place.
///
/// It rides the AUTHORED bakes only; the probe field stays at full strength — it is the
/// ambient a dynamic thing stands in, not a painted picture of sunlight, and dimming it
/// blacked out every character at night.
///
/// One function so the terrain and model shaders cannot drift apart.
/// `Engine_LevelBakedLighting.AuthoredBakeWeight` mirrors it, because the host applies the same
/// figure to a probe-lit prop's instance uniform; keep the two identical.
float fable_authored_bake_weight() {
	float w = fable_bake_weight > 1.0 ? fable_bake_weight
		: (fable_interior > 0.5 || fable_bake_weight <= 0.0 ? 1.0 : fable_bake_weight);
	return w * fable_interior_bake;
}

vec3 fable_bake(vec3 world_normal, vec4 vertex_colour, vec2 lightmap_uv) {
	float mode = lightmap_colour.w;

	// The remaster weight rides the AUTHORED bakes (modes 2/3/4). The probe field stays at
	// full strength — it is the ambient a dynamic thing stands in, not a painted picture of
	// sunlight, and dimming it blacked out every character at night.
	float authored = fable_authored_bake_weight();

	if (mode > 3.5) {
		// The amb3 TEXTURE bake — the mesh's own TexUV2 channel, absolute over the object's
		// 64x64 lightmap (remapped into the region atlas). Single 1.3 decode: the amb3 vertex
		// shader applies no 2.2, unlike amb1's vertex colours.
		//
		// NO SKY TETHER AND NO RESIDUAL WEIGHT HERE, alone of the lit modes: amb3 lightmaps
		// are INTERIORS-ONLY in the shipped data. A windowless room must not tint toward a sky
		// it cannot see, and it has no live sun to hand its look to — the modern model's lower
		// bake weight would simply darken it. The tether being active IS the modern signal, so
		// interiors ride at full authored strength there; fable mode keeps the dial for both.
		// Not an interior test: `fable_sky_ambient.w` is the sky TETHER, published by
		// `Engine_LevelLighting` and 0 in the fable model. It decides model, not roof.
		float tethered = fable_sky_ambient.w > 0.0 ? 1.0 : authored;
		vec2 at = lightmap_rect.xy + lightmap_uv * lightmap_rect.zw;
		return tethered * pow(max(texture(fable_prop_lightmaps, at).rgb, vec3(0.0)),
			vec3(FABLE_BAKE_GAMMA));
	}

	if (mode > 2.5) {
		// The vertex-colour bake, decoded twice: 2.2 in the AMB1 vertex shader, then 1.3 here.
		return authored * pow(
			max(vertex_colour.rgb, vec3(0.0)),
			vec3(FABLE_VERTEX_BAKE_GAMMA * FABLE_BAKE_GAMMA));
	}
	if (mode > 1.5) {
		return authored * pow(max(vertex_colour.rgb, vec3(0.0)), vec3(FABLE_BAKE_GAMMA));
	}
	if (mode > 0.5) {
		// The PRT probe, which its own vertex shader (AMB2) hands over undecoded.
		return pow(fable_probe_bake(world_normal), vec3(FABLE_BAKE_GAMMA));
	}
	return vec3(0.0);
}

/// THE SKY TETHER, ONE RULE FOR EVERY EXTERIOR BAKE — and it runs on the FINISHED ambient
/// half, AFTER LightmapColour. Mixing inside fable_bake put the sky target UNDER the theme's
/// lightmap factor, and that factor is enormous and blue — (4.6, 5.1, 7.1) at midday in
/// chapter2posh, the 360's way of pushing skylight into its bakes — so the "sky" the props
/// blended toward arrived five-to-seven-fold too strong and deep blue, while terrain mixed
/// after the factor: a wash AND a seam. Tethering the probes alone was the previous seam
/// (probe-lit bridge segments tracking a red sunset beside untethered vertex-baked ones), so
/// the rule covers modes 1/2/3; mode 4 (amb3, interiors-only) never calls this — a windowless
/// room must not tint toward a sky it cannot see. `fable_sky_ambient.w` is 0 in fable mode.
vec3 fable_sky_tether(vec3 ambient_half) {
	return mix(ambient_half, fable_sky_ambient.rgb, fable_sky_ambient.w);
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
/// 1.0 WHEN GODOT'S GI CARRIES THE BAKE — the modern model. The guest's fill is two terms,
/// `bake * LightmapColour + GlobalAmbient`, and they are different KINDS of light:
///   the BAKE is occluded and varies across space — that is what Godot's GI now is, so at 1 no
///   per-instance bake (probe/PRT, vertex colours, texture lightmap) emits, or the GI would
///   bounce it a second time;
///   GLOBALAMBIENT is flat, unoccluded and added last — a FLOOR. It stays in both models and it
///   cannot be Godot's environment ambient: inside an SDFGI cascade the engine does
///   `ambient_light = diffuse` (scene_forward_clustered.glsl), REPLACING that ambient with the
///   trace, which is zero under a canopy or beside a wall. Gating the floor off as well left
///   every shadow in Fairfax Gardens black.
/// 0 is the fable model, where Godot's ambient has no energy and both terms are emitted here.
global uniform float fable_unified = 0.0;

/// THE BAKE'S UNOCCLUDED SHARE, modern model only (zero in the fable model). The guest's bake is
/// fill that reaches UNDER a canopy — a night forest floor holds `bake * LightmapColour`, blue and
/// readable — and the GI's sky light reaches only where the sky is seen, so handing it the whole
/// bake left that floor black. A share of the mean bake stays here, flat, in the theme's own
/// `LightmapColour`; the rest is the ambient the GI traces. See `ModernFloorShare`.
global uniform vec3 fable_fill_floor = vec3(0.0);

vec3 fable_ambient(vec3 bake) {
	return (bake * (1.0 - fable_unified)) + fable_ambient_colour + fable_fill_floor;
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
/// The guest's distance falloff — `saturate(1 - d/range)`, linear, one radius — and the shadow.
///
/// Fable's ramp is linear and Godot's is not: at half range the guest is at 0.50 and Godot at
/// 0.88, so on Godot's curve every torch reads as a far larger, flatter pool than the original.
///
/// Godot folds the shadow into ATTENUATION — `omni_attenuation * shadow` and
/// `spot_attenuation * shadow` in scene_forward_lights_inc.glsl — and hands `light()` no
/// position, so from ATTENUATION alone the distance and the shadow cannot be told apart. A placed
/// light therefore carries a record (see `fable_light_table`): the distance is MEASURED, the
/// guest's ramp follows from it, and dividing ATTENUATION by Godot's own window at that distance
/// leaves the shadow — and, for a spot, the cone, which at SpotAngleAttenuation = 1 is already the
/// guest's `(cos - K)/(1 - K)`. The window is the zero-decay form `(1 - (d/r)^4)^2`, which is why
/// Engine_LevelLights pins OmniAttenuation and SpotAttenuation to 0.
///
/// A light without a record — an FX light, or anything at Godot's default specular — is
/// unshadowed by construction, so its ATTENUATION is the window alone and inverts in closed form:
/// d/r = (1 - sqrt(A))^(1/4), exact to 3e-14. That inversion is NOT valid once a shadow is
/// multiplied in, which is why every light that can cast is registered.
///
/// The sun has no distance term (the guest's per-light scalar S = 0 collapses
/// `lerp(1, cone*atten, S)` to 1), so its ATTENUATION is the shadow already and passes through.
///
/// The guest's seven-light cap is NOT reproduced: the ramp reaches exactly zero at the range, so
/// the cap only changes the picture where more than seven radii overlap one point — the worst
/// overlap in the shipped set is 11, and only 4 of 81 lit levels exceed 7 anywhere.
float fable_attenuation(float godot_attenuation, bool directional, float specular_amount, vec3 world) {
	if (directional) {
		return godot_attenuation;
	}

	int slot = int(specular_amount * 0.5 + 0.5) - 2;
	if (slot >= 0 && slot < textureSize(fable_light_table, 0).x) {
		vec4 record = texelFetch(fable_light_table, ivec2(slot, 0), 0);
		float ratio = clamp(distance(world, record.xyz) / max(record.w, 1e-3), 0.0, 1.0);
		float window = 1.0 - (ratio * ratio * ratio * ratio);
		window *= window;
		float rest = window > 1e-3 ? clamp(godot_attenuation / window, 0.0, 1.0) : 1.0;

		// TWO RADII. record.w is the NODE's range: it is what Godot's window above was computed
		// with, so it is what undoes it. The guest's ramp runs over the radius it STATES, which for
		// a flickering torch breathes every step and lives in the table's second row — the node
		// keeps the flicker's envelope so its bounds never move (see Engine_LightTable.Stated).
		float stated = texelFetch(fable_light_table, ivec2(slot, 1), 0).x;
		float ramp = clamp(1.0 - distance(world, record.xyz) / max(stated, 1e-3), 0.0, 1.0);
		return ramp * rest;
	}

	float a = clamp(godot_attenuation, 0.0, 1.0);
	float ratio = pow(max(1.0 - sqrt(a), 0.0), 0.25);
	return clamp(1.0 - ratio, 0.0, 1.0);
}

/// +1 when the interpolated normal is on the side of the surface THE CAMERA SEES, -1 when it is
/// inside out. FRAGMENT STAGE: `view_position` is VERTEX, `view` is VIEW.
///
/// The surface is judged by its own geometry — the face normal from the screen-space derivatives
/// of the position, turned toward the viewer — and NOT by FRONT_FACING. These shaders draw with
/// culling off, and the rasteriser's facing follows the winding and the parent's handedness: the
/// world sits under a region root that negates X and rasterises entirely as BACK faces, a
/// guest-driven entity's node has determinant +1, and a garment or a brazier cage is modelled
/// double-sided, so whichever copy wins the depth test decides. Measured live with the facing
/// view: everything back-facing lit correctly and everything front-facing — the hero's coat,
/// waistcoat and trousers, the brazier — lit inside out. Geometry cannot disagree with itself.
float fable_normal_side(vec3 normal, vec3 view_position, vec3 view) {
	vec3 face = cross(dFdx(view_position), dFdy(view_position));
	face = dot(face, view) < 0.0 ? -face : face;
	return dot(normal, face) < 0.0 ? -1.0 : 1.0;
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
/// The writer is `sub_821B16F8`'s tail via `sub_821B1B08`, descriptor
/// `unk_8331AD90` (register 23). It uploads { -mainLightDirection.xyz, w = 4.0f } — the w is the
/// literal `flt_8209B46C`. So the guest's gate is `saturate(4.0 * NdotL)`: fully open by
/// NdotL = 0.25, a soft-but-quick shutter at the terminator rather than a slope. The constant
/// bypasses the binding table and is stored straight into the device's PS shadow at
/// device+0x1780+16*23.
const float FABLE_SPEC_ANGLE_GATE = 4.0;

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

