shader_type spatial;
render_mode blend_premul_alpha, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

uniform sampler2D fx_tex : filter_linear, repeat_enable;
uniform float fx_exposure = 3.3333333;

// `texel.rgb * texel.rgb` STAYS — that is Fable's colour-texture decode, gamma exactly 2.0,
// the same one the material shaders and the sky dome's overlay textures use.
//
// What goes is the sRGB encode that followed it, and the clamp. Nothing in this game applies
// the sRGB curve: its single display transfer is `pow(colour, g_GlobalGammaAdjustment.x)` at
// the end of the post-processing material, and every element shader before it writes linear
// HDR unclamped. Squaring on the way in and sRGB-encoding on the way out was two different
// gamma models stacked on one surface.
void fragment() {
	vec4 texel = texture(fx_tex, UV);
	ALBEDO = COLOR.a * fx_exposure * COLOR.rgb * texel.rgb * texel.rgb;
	ALPHA = texel.a * COLOR.a;
}
