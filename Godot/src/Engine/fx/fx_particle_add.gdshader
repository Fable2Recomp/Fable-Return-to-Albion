shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

uniform sampler2D fx_tex : filter_linear, repeat_enable;
uniform float fx_alpha_ref = 1.0;
#include "res://src/Engine/fx/fx_soft.gdshaderinc"
uniform float fx_exposure = 3.3333333;

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
	ALBEDO = COLOR.a * fx_exposure * COLOR.rgb * texel.rgb * texel.rgb * fade;
	ALPHA = mix(1.0, texel.a * COLOR.a * fade, fx_src_alpha);
}
