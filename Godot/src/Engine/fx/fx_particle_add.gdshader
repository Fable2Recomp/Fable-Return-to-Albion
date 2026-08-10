shader_type spatial;
render_mode blend_add, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

uniform sampler2D fx_tex : filter_linear, repeat_enable;
uniform float fx_exposure = 3.3333333;

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
	ALBEDO = COLOR.a * fx_exposure * COLOR.rgb * texel.rgb * texel.rgb;
	ALPHA = texel.a * COLOR.a;
}
