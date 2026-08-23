shader_type spatial;

// THE CANOPY IS NOT LIT PER PIXEL, AND THAT IS THE WHOLE POINT OF THIS FILE.
//
// Blob 0544 (`treematerial_blt1_glt1_mist1`) contains no `g_GlobalLightParamsPS`, and its only
// `dot` is a squared length inside the mist block — there is no `dot(N, L)` anywhere. Only TWO of
// the forty-four `treematerial` pixel shaders are canopy (0544 and 0573); the other forty-two
// carry `trunk1` and ARE ordinary per-pixel lighting. Do not generalise "trees are not lit per
// pixel" to the family — it is true of the leaves and false of the trunk.
//
// Drawing canopies through `standard_material` is why they read as one flat bright blob: that
// shader takes `dot(L, N)` against the leaf CARD's normal, and a card normal is a poor description
// of a canopy, so every card lands on roughly the same term.
//
// `unshaded` is not a style choice — this surface takes NO light from Godot at all, and there is
// deliberately no `light()` function below. Every lighting term arrives interpolated from the
// vertex stage, exactly as the guest's does. It also implies `ambient_light_disabled` and
// `specular_disabled`, so naming those as well would be noise.
render_mode cull_disabled, unshaded;

#include "res://src/Engine/level/environment/fable_atmosphere.gdshaderinc"

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, hint_default_white;

/// The screen-space shadow. Until we render the guest's shadow buffer this is 1 (fully lit), which
/// is the value that makes both remaps below the identity they would be with no shadow at all.
uniform float screen_shadow : hint_range(0.0, 1.0) = 1.0;

/// `g_StandardMaterialPSConstants(2).xyz` — the material colour, a final multiplier.
uniform vec3 material_colour : source_color = vec3(1.0);

/// `g_StandardMaterialPSConstants(3).w` — the ambient scale, the ONLY slot gating the ambient term.
uniform float ambient_scale = 1.0;

/// `g_StandardMaterialTintColour.xyz` — the per-instance tint.
uniform vec3 tint_colour : source_color = vec3(1.0);

/// `g_GlobalAmbientAndBrightness.w` — the global brightness scalar the tail multiplies by.
uniform float global_brightness = 1.0;

uniform float alpha_threshold : hint_range(0.0, 1.0) = 0.5;

/// `g_FadeBeginDistanceAndRange.w` — a plain scalar on output alpha. `.xyz` is never read.
uniform float alpha_scale = 1.0;

varying vec3 v_front;
varying vec3 v_back;
varying vec3 v_ambient;

void vertex() {
	// THREE INTERPOLATED TERMS, NOT TWO. The guest's canopy vertex stage writes an ambient colour
	// to TEXCOORD1, a FRONT light sum to TEXCOORD2 and a BACK light sum to TEXCOORD3, and the
	// pixel stage reads all three. The back term is translucency — light coming through the leaf —
	// which is what gives a canopy depth without a per-pixel normal.
	//
	// STAND-INS, AND HONESTLY SO. We do not yet run the guest's canopy vertex shader, whose front
	// term is a WRAP diffuse `saturate((N·L + w)/(1 + w))` and whose back term is `saturate(-N·L)`,
	// both summed over the light slots with cone and attenuation. Reproducing that needs the light
	// array in the vertex stage. Until then COLOR carries our own per-instance probe colour — see
	// Engine_LevelBakedLighting.ApplyMultiMeshProbes — and the back term is a fraction of it, which
	// preserves the SHAPE of the model (a lit side, a translucent side, a flat ambient) without
	// inventing a light direction the vertex stage does not have.
	v_front = COLOR.rgb;
	v_back = COLOR.rgb * 0.5;
	v_ambient = COLOR.rgb;
}

void fragment() {
	vec4 texel = texture(albedo_tex, UV);

	// THE ALPHA TEST TAKES THE RAW SAMPLE, NEVER THE SQUARED ONE. The guest's tail is
	// `PSConstants(2).w * rawAlpha` fed to `clip()`, while the colour path squares. Feeding the
	// squared value here moves every leaf's cutout edge inward and thins the whole canopy.
	float raw_alpha = texel.a;
	if (raw_alpha * alpha_scale < alpha_threshold) {
		discard;
	}

	// UN-PREMULTIPLIED IN SQUARED SPACE: the guest computes `rgb*rgb` and `a*a` and divides, which
	// is `(rgb/a)^2`. Algebraically the same as un-premultiplying first, but the guard matters —
	// a zero alpha here is a texel the discard above already removed.
	float a2 = max(raw_alpha * raw_alpha, 1e-6);
	vec3 albedo = (texel.rgb * texel.rgb) / a2;

	// THE 0.6/0.4 REMAP IS APPLIED TWICE, AND THE TWO TERMS GET DIFFERENT SHADOWS. This is not a
	// transcription slip — it is verified register dataflow in 0544, where `r0.w` is written at
	// 1266, read for the front term at 1267, OVERWRITTEN at 1268 and read again for the back term
	// at 1269:
	//
	//     1266  r0.w = r0.w * c255.w + c255.z;      s1 = shadow*0.6 + 0.4
	//     1267  r2.xyz = r0.www * r2.zyx + r7.xyz;  FRONT takes s1
	//     1268  r0.w = r0.w * c255.w + c255.z;      s2 = s1*0.6 + 0.4
	//     1269  r7.xyz = r0.www * r3.zyx;           BACK takes s2
	//
	// with c255 = (1.0, 0.0, 0.4, 0.6). So the back term's shadow is `shadow*0.36 + 0.64` — a
	// shallower floor, which keeps the translucent side alive deep in shadow. Applying s1 to both
	// darkens the canopy's shaded half by a term that never reaches it in the shipped game.
	float s1 = screen_shadow * 0.6 + 0.4;
	float s2 = s1 * 0.6 + 0.4;

	// THE BACK TERM IS MASKED WHERE RED EXCEEDS GREEN — `r0.w = r3.y >= r3.x` at 1273, applied at
	// 1274 to the back contribution alone. A hard 0/1 mask, not a blend. Read plainly it means
	// only the green, leafy texels transmit light and the brown ones do not; whatever the artist
	// intent, the arithmetic is unambiguous and it is the back term only.
	float transmit = albedo.g >= albedo.r ? 1.0 : 0.0;

	vec3 lit = albedo * ((s1 * v_front) + (ambient_scale * v_ambient) + (s2 * v_back * transmit));

	vec3 colour = global_brightness * material_colour * lit * tint_colour;

	// LAST, because it attenuates everything above it.
	//
	// THROUGH THE EMISSION ARGUMENT, NOT THE ALBEDO ONE, and that is not arbitrary. The split in
	// `fable_atmosphere_apply` exists because a LIT surface has its albedo multiplied by Godot
	// after `light()` returns, so the two halves need different treatment. This surface is
	// `unshaded`: one value carries everything, and the term that both attenuates the surface and
	// adds the in-scatter back is the emission one — `e*T + (1-T)*fog`. Passing the colour as the
	// albedo argument would attenuate it and never add the in-scatter, so a distant canopy would
	// fade toward black instead of toward the sky.
	vec3 fog_t;
	vec3 fog_c;
	if (fable_atmosphere_terms(VERTEX, INV_VIEW_MATRIX, fog_t, fog_c)) {
		vec3 unused = vec3(0.0);
		fable_atmosphere_apply(fog_t, fog_c, 1.0, unused, colour);
	}

	// ALBEDO, NOT EMISSION. Under `unshaded` Godot writes ALBEDO straight to the screen and never
	// reads EMISSION — putting the result there instead draws a black canopy, which looks exactly
	// like a failed texture bind and would have cost a run to diagnose.
	ALBEDO = colour;
	ALPHA = 1.0;
}
