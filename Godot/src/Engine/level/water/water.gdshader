shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded;

#include "res://src/Engine/level/environment/sky_dome.gdshaderinc"

uniform sampler2D normal_map : filter_linear_mipmap, repeat_enable;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear;

// The mirrored render of the world — Engine_WaterReflection's SubViewport, one per body. Until it
// is mounted `planar_reflection` stays false and the sky dome stands in, which is what every path
// that builds water without a live camera (the viewers, headless) gets.
uniform sampler2D reflection_tex : filter_linear, repeat_disable;
uniform bool planar_reflection = false;

// The authored coverage mask. BILINEAR AND CLAMPED, both proven rather than chosen:
// `Render_BindMaterialTextures_INFERRED` sets MagFilter/MinFilter from *(0x83316FC4) = 1 and
// AddressU/V from *(0x83316F40) = 2, which decode through the D3D layer as k_Linear and
// k_ClampToEdge. Point sampling here would make the whole edge ramp a no-op.
uniform sampler2D water_mask : filter_linear, repeat_disable;
uniform bool has_water_mask = false;

// (minX, minZ, 1/sizeX, 1/sizeZ) — turns a local XZ position into the mask's UV, so the mesh does
// not have to carry one. The guest's patches use a plain 0..1 parametric UV over their own extent;
// ours spans the body because our mesh merges its tiles.
uniform vec4 w_mask_bounds = vec4(0.0, 0.0, 1.0, 1.0);

// (EdgeBlendMin, EdgeBlendMax, EdgeBlendBias, unused) — theme-only, no per-body slot exists.
uniform vec4 w_edge = vec4(0.0, 1.0, 0.0, 0.0);

uniform vec4 w_bias;
uniform vec4 w_speed;
uniform vec4 w_scale;
uniform vec4 w_surface;
uniform vec4 w_deep;
uniform vec4 w_reflp;
uniform vec4 w_glit;
uniform vec3 sky_constant = vec3(0.58, 0.62, 0.57);

// THE SUN GLINT IS LIT BY THE LEVEL'S MAIN DIRECTIONAL LIGHT, and it used to be lit by a
// hardcoded direction that never moved. The water draw's own upload settles it: the offscreen
// water renderer `sub_82B3CDE8` takes the LAST entry of the frame's directional-light vector
// (`dword_834A4D68`/`_4D6C`, built by `sub_821BD0C8` filtering PRIMITIVE_INSTANCE_DIRECTIONAL_LIGHT,
// type 10) and uploads `light+0xD0` to the binding the shader reads as `g_LightColor`:
//
//     0x82b3cf0c  lwz    r30, -4(r11)      ; last light in the list
//     0x82b3cf34  lvx128 v1, r30, 0xD0     ; its colour
//     0x82b3cfc4  li     r3, 0x7A          ; binding 122
//
// and that +0xD0 is written from the day-set theme's MAIN_LIGHT_COLOUR (`sub_821DF670` loads
// theme+16 and sends it as command id 29; `sub_822BD190` stores it to +0xD0). It is the same
// light our DirectionalLight3D is built from, so it arrives here the same way the props' bake
// does — per instance, refreshed whenever time of day moves.
//
// Nothing is water-specific about it: the identical `li 0xD0 -> binding 122` snippet appears in
// the mesh, quad, rain and snow paths too.
//
// WORLD SPACE, pointing TOWARD the sun. The guest hands its light the NEGATED sun direction and
// the shader negates it back at the reflect; carrying the toward-sun vector skips both.
instance uniform vec4 sun_direction = vec4(0.0, 1.0, 0.0, 0.0);

/// rgb: the main light's colour already multiplied by its energy.
instance uniform vec4 sun_colour = vec4(1.0);

uniform sampler2D in_scatter_lut : filter_linear, repeat_disable;
uniform vec3 beta_rayleigh = vec3(0.001, 0.0015, 0.004);
uniform vec3 beta_mie = vec3(0.0056, 0.0064, 0.0105);
uniform vec3 sun_engine = vec3(1.0, 0.0, 0.0);
uniform float horizon_distance = 1900.0;
uniform float dome_exposure = 0.0;

/// Whether the atmosphere uniforms above have been filled in yet.
///
/// A FLAG, NOT A THRESHOLD ON THE EXPOSURE. This used to be `dome_exposure > 0.5`, which worked
/// only because the exposure happened to be 10 — it is now 1.0, and any future value under 0.5
/// would have silently swapped every water body's sky reflection for a flat constant with nothing
/// to indicate why.
uniform bool has_dome = false;

varying vec3 v_local;
varying vec3 v_eye_local;
varying vec3 v_sun_local;

void vertex() {
	v_local = VERTEX;
	v_eye_local = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;

	// The glint is built against `v_local` and `v_eye_local`, so the sun has to be in the same
	// space as they are. w = 0 — this is a direction, and the body's translation must not reach it.
	v_sun_local = (inverse(MODEL_MATRIX) * vec4(sun_direction.xyz, 0.0)).xyz;
}

// The reflected sky is THE SAME DOME the sky shader draws, so it comes from the same function.
// A second copy of the arithmetic here would have to tonemap too, and then the water reflects
// a compressed sky while the sky above it is not compressed at all.
vec3 dome_reflect(vec3 r) {
	return fable_sky_inscatter(
		vec3(r.x, r.z, r.y),
		sun_engine,
		in_scatter_lut,
		beta_rayleigh,
		beta_mie,
		horizon_distance,
		dome_exposure);
}

void fragment() {
	vec2 phase01 = fract(TIME * w_speed.xy);
	vec2 phase23 = fract(TIME * w_speed.zw);

	vec2 uv0 = (v_local.xz * w_scale.xy) + phase01;
	vec2 uv1 = (v_local.xz * w_scale.zw) + phase23;

	vec2 n0 = (texture(normal_map, uv0).xy * 2.0) - 1.0;
	vec2 n1 = (texture(normal_map, uv1).xy * 2.0) - 1.0;
	float z0 = sqrt(clamp(1.0 - dot(n0, n0), 0.0, 1.0));
	float z1 = sqrt(clamp(1.0 - dot(n1, n1), 0.0, 1.0));
	vec2 nxy = n0 + n1;
	float nz = z0 + z1;

	vec3 view = v_eye_local - v_local;
	float dist = length(view);
	vec3 V = view / max(dist, 1e-5);

	vec3 Nf = normalize(vec3(nxy.x, w_bias.z * nz, nxy.y));
	float fres = clamp((1.0 - dot(V, Nf)) + w_bias.x, 0.0, 1.0);

	vec3 watercol = mix(w_deep.rgb, w_surface.rgb, fres);

	float shadow_f = 1.0;
	float frefl = clamp(fres + w_bias.y, 0.0, 1.0);
	float refl_str = clamp(w_deep.w, 0.0, 1.0);

	// saturate(distance / MaxRefractionDistance) — the guest's `g_WaterConstants(24).x` is the
	// reciprocal, taken on the CPU at `821E5054`; w_bias.w carries the stated distance instead.
	float distf = clamp(dist / max(w_bias.w, 0.001), 0.0, 1.0);

	// THE REFLECTION IS A MIRRORED RENDER OF THE WORLD, sampled in SCREEN SPACE.
	//
	// The guest renders the scene a second time from a camera mirrored about the water plane into a
	// 256x256 "ReflectionTile" (`sub_82B394C8` / `sub_82B39A18`) and fetches it with the screen
	// coordinate, displaced by the wave:
	//
	//     r7.xy   = g_WaterConstants(13).yx * 0.5 * r2.yx   ; 0.5 * ReflectionScale * n
	//     r7.xyzw = r1.zwzw + r7.xywz                       ; added to the screen UV
	//     r3.xyz  = tfetch2D(g_ReflectionSampler, r7.yx)
	//
	// Screen space is the correct lookup precisely BECAUSE the mirrored camera keeps the main
	// projection: a point on the surface projects to the same pixel in both views.
	//
	// The dome stays as the fallback for the frame before the viewport has rendered, and for any
	// path that mounts water without a reflection (the model viewer, a headless build).
	vec2 refl_uv = SCREEN_UV + (nxy * 0.5 * w_reflp.x);
	vec3 Nr = normalize(vec3(nxy.x * w_reflp.x * 0.5, 1.0, nxy.y * w_reflp.x * 0.5));
	vec3 R = reflect(-V, Nr);
	R.y = abs(R.y);
	vec3 dome = has_dome ? dome_reflect(R) : sky_constant;

	// Rf — the guest folds the shadow mask in as `shadow * ShadowScaleBias + (1 - ShadowScaleBias)`
	// (`g_WaterConstants(23)`, built as the pair `(v, 1-v)` at `821E5014`). We have no water shadow
	// mask yet, so shadow_f is 1.0 and the term is the identity.
	vec3 Rf = (planar_reflection
		? texture(reflection_tex, clamp(refl_uv, vec2(0.0), vec2(1.0))).rgb
		: dome) * shadow_f;

	vec2 refr_uv = SCREEN_UV + (nxy * 0.5 * w_reflp.y);
	vec3 Rr = texture(screen_tex, refr_uv).rgb;

	// THE GUEST'S COMPOSITION, IN ITS OWN ORDER — 0057 pshader_ocean_water:
	//
	//     near   = lerp(W, lerp(Rr, Rf, f), s)
	//     far    = lerp(W, Rf, s)
	//     colour = lerp(near, far, t)
	//
	// with W the fresnel-blended body colour, f = fresnel + ReflectionBias, s = ReflectionStrength
	// and t = distance/MaxRefractionDistance. Refraction fades out with distance and reflection
	// does not, which is what the two endpoints encode.
	vec3 near_col = mix(watercol, mix(Rr, Rf, frefl), refl_str);
	vec3 far_col = mix(watercol, Rf, refl_str);
	vec3 col = mix(near_col, far_col, distf);

	vec3 g = normalize(vec3(Nf.x, -max(w_reflp.z, 0.0) * Nf.y, Nf.z));
	vec3 L = normalize(v_sun_local);
	vec3 Rl = L - (2.0 * dot(L, g) * g);
	float spec = clamp(dot(V, Rl), 0.0, 1.0);
	float glint = pow(max(spec, 1e-6), max(w_glit.x, 1.0));
	col += sun_colour.rgb * (glint * max(w_reflp.w, 0.0));

	// LINEAR, LIKE THE SHIPPED WATER. 0055 pshader_waterpatch composes reflection and
	// refraction with a straight fresnel lerp and writes it —
	//
	//     oC0.xyz = r1.xyzz * r0.yyyy + r0.xzww;   // lerp(refraction, reflection, fresnel)
	//     oC0.w   = 1.0
	//
	// with no transfer function and not even a brightness multiply. 0057 ocean_water is longer
	// (screen-space reflection and refraction fetches, a water body colour, a pow() sun glint
	// and an extinction/in-scatter LUT pair) and still ends the same way, on a single
	// `* g_GlobalAmbientAndBrightness.www`. The one x*x in either shader is normal-Z
	// reconstruction after a *2-1 unpack, not a gamma decode.
	//
	// Both the screen texture and the reflection viewport are linear HDR — the reflection camera
	// deliberately carries no exposure effect, so this composes in one space and the main camera's
	// tone curve runs once over the result.
	ALBEDO = col;

	// THE SHORELINE, from the authored coverage mask.
	//
	// The guest's alpha, isolated cleanly in the z-pass blob 0061 and identical in 0057:
	//
	//     mask  = tfetch2D(g_WaterMaskSampler, TEXCOORD3.xy).x
	//     d     = 1.0 - mask
	//     a     = saturate(-d * WC25.y + WC25.x)
	//     ALPHA = 1.0 - a
	//
	// with the constants built on the CPU at the tail of `sub_821E4C88`:
	//
	//     821e50c0  WC25.y = 1 / (Max - Min)
	//     821e50d8  WC25.x = (Bias + Max) / (Max - Min)
	//
	// Substituting collapses the double negation into a plain linear ramp on coverage — zero at
	// `Min + Bias`, one at `Max + Bias`:
	//
	//     ALPHA = saturate((d - (Min + Bias)) / (Max - Min))
	//
	// The mask reads 1 for LAND (Engine_WaterMask inverts on the way in, as `sub_82B3B4F8` does),
	// so `d` is water coverage and the ramp opens as coverage rises.
	//
	// WITHOUT A MASK THIS STAYS OPAQUE rather than guessing a falloff from geometry — a body whose
	// tiles carry no cells is water everywhere, which is exactly the guest's empty-mask branch at
	// 0x82B3AB24: a 4x4 texture of constant 0.0, and no blur.
	if (has_water_mask) {
		vec2 mask_uv = (v_local.xz - w_mask_bounds.xy) * w_mask_bounds.zw;
		float mask = texture(water_mask, clamp(mask_uv, vec2(0.0), vec2(1.0))).r;
		float d = 1.0 - mask;
		ALPHA = clamp(
			(d - (w_edge.x + w_edge.z)) / max(w_edge.y - w_edge.x, 1e-4),
			0.0, 1.0);
	} else {
		ALPHA = 1.0;
	}
}
