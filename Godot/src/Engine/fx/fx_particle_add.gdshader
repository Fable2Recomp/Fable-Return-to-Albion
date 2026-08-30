shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

uniform sampler2D fx_tex : filter_linear, repeat_enable;
uniform float fx_alpha_ref = 1.0;
#include "res://src/Engine/fx/fx_soft.gdshaderinc"
uniform float fx_exposure = 3.3333333;
global uniform sampler2D fable_exposure_tex;
global uniform float fable_exposure_valid;

uniform float fx_src_alpha = 1.0;

// THE SAME COMPOSITION AS THE MIX VARIANT — only the blend differs.
//
// 0210_pshader_particlematerial serves both, and it is a pure multiply chain:
//
//     r2.x   = vertexColour.a * g_GlobalAmbientAndBrightness.w
//     r2.xyz = r2.xxx * vertexColour.rgb
//     r2.xyz = r2.xyz * texel.rgb          <- the texel enters
//     oC0.xyz = r2.xyz * texel.rgb         <- and again: gamma 2.0 by squaring
//     oC0.w   = vertexColour.a * texel.a
//
// So neither a clamp nor `ALPHA = 1.0` is the guest's. It writes a real alpha and that alpha
// is live in every blend case the bank actually asks for —
// SRC_ALPHA:INV_SRC_ALPHA, ZERO:INV_SRC_ALPHA and the ONE:INV_SRC_ALPHA default all consume
// it, and every particle draw enables the alpha test against it too (D3DRS_ALPHATESTENABLE = 1
// at 0x82a5a2dc). Forcing it to 1 makes soft-edged particles hard-edged and stops them fading.
void fragment() {
	vec4 texel = texture(fx_tex, UV);
	if (texel.a * COLOR.a * 255.0 <= fx_alpha_ref) {
		discard;
	}
	float fade = fx_soft_fade(texel.a * COLOR.a, VERTEX, SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
	vec3 rgb = COLOR.a * COLOR.rgb * texel.rgb * texel.rgb;
	// PER CHANNEL AT 1.0 — THE LAYER BOUND, NOT DISPLAY WHITE. The guest's additive FX
	// accumulate in a bounded surface (the AltBloom layer): the ceiling is 1.0 in layer space,
	// independent of the eye. 0509 then adds `fx * exposure` into the scene and the TONE CURVE
	// compresses it. A 1/exposure ceiling instead scales with adaptation — in a dark interior
	// (exposure toward 16) everything above 0.06 flattened to the ceiling and composited at
	// exactly display white: every flame a white-cored blob. Per channel, so a hot core still
	// desaturates toward white as the guest's does.
	if (fable_exposure_valid > 0.5) {
		rgb = min(rgb, vec3(1.0));
	} else {
		rgb = clamp(rgb * fx_exposure, 0.0, 1.0);
	}
	ALBEDO = rgb * fade;
	ALPHA = mix(1.0, texel.a * COLOR.a * fade, fx_src_alpha);
}
