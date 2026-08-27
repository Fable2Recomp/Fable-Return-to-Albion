shader_type spatial;
render_mode blend_sub, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

uniform sampler2D fx_tex : filter_linear, repeat_enable;
uniform float fx_alpha_ref = 1.0;
#include "res://src/Engine/fx/fx_soft.gdshaderinc"
uniform float fx_exposure = 3.3333333;
global uniform sampler2D fable_exposure_tex;
global uniform float fable_exposure_valid;

void fragment() {
	vec4 texel = texture(fx_tex, UV);
	if (texel.a * COLOR.a * 255.0 <= fx_alpha_ref) {
		discard;
	}
	float fade = fx_soft_fade(texel.a * COLOR.a, VERTEX, SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
	vec3 rgb = COLOR.a * COLOR.rgb * texel.rgb * texel.rgb;
	// CAP BY PEAK CHANNEL, NOT PER CHANNEL. The ceiling is unchanged — display white once the
	// composite has applied the same exposure — but clipping each channel on its own is what
	// dragged a coloured effect toward its primary. Scaling the whole colour holds the hue.
	// The exposure is NOT multiplied in here: the composite already scales the HDR target by
	// it, and doing it twice blows every additive effect to white.
	if (fable_exposure_valid > 0.5) {
		float applied = max(texelFetch(fable_exposure_tex, ivec2(0), 0).r, 1e-4);
		rgb /= max(max(max(rgb.r, rgb.g), rgb.b) * applied, 1.0);
	} else {
		rgb *= fx_exposure / max(max(max(rgb.r, rgb.g), rgb.b) * fx_exposure, 1.0);
	}
	ALBEDO = rgb * fade;
	ALPHA = 1.0;
}
