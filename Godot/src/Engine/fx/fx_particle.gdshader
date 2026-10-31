shader_type spatial;
render_mode blend_premul_alpha, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

#include "res://src/Engine/fx/fx_billboard.gdshaderinc"

uniform sampler2D fx_tex : filter_linear, repeat_enable;
uniform float fx_alpha_ref = 1.0;
#include "res://src/Engine/fx/fx_soft.gdshaderinc"
uniform float fx_exposure = 3.3333333;
global uniform sampler2D fable_exposure_tex;
global uniform float fable_exposure_valid;
uniform bool is_pbr = false;

// `texel.rgb * texel.rgb` STAYS — that is Fable's colour-texture decode, gamma exactly 2.0,
// the same one the material shaders and the sky dome's overlay textures use.
// Modern PBR mod particle textures (is_pbr = true) bypass squaring.
//
// What goes is the sRGB encode that followed it, and the clamp. Nothing in this game applies
// the sRGB curve: its single display transfer is `pow(colour, g_GlobalGammaAdjustment.x)` at
// the end of the post-processing material, and every element shader before it writes linear
// HDR unclamped. Squaring on the way in and sRGB-encoding on the way out was two different
// gamma models stacked on one surface.
void fragment() {
	vec4 texel = texture(fx_tex, UV);
	if (texel.a * COLOR.a * 255.0 <= fx_alpha_ref) {
		discard;
	}
	float fade = fx_soft_fade(texel.a * COLOR.a, VERTEX, SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
	vec3 tex_rgb = is_pbr ? texel.rgb : (texel.rgb * texel.rgb);
	vec3 rgb = COLOR.a * COLOR.rgb * tex_rgb;
	// NO CLAMP — MIX-BLENDED PARTICLES ARE SCENE CONTENT. The guest draws them into the HDR
	// scene with no bound of their own and the tone curve compresses them like any surface;
	// only the ADDITIVE layer is bounded. Clamping here pinned smoke to a fixed fraction of
	// display white whatever the scene around it was doing.
	if (fable_exposure_valid <= 0.5) {
		rgb *= fx_exposure;
	}
	rgb = max(rgb, vec3(0.0));
	ALBEDO = rgb * fade;
	ALPHA = texel.a * COLOR.a * fade;
}
