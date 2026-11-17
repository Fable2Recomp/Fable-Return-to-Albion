shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;

// A world-space marker — a heart over an admirer, the sign over an owned house, a quest point —
// drawn ON THE HUD LAYER so the world's exposure and tone curve never touch it, and hidden by
// the world all the same: the guest drew these through its renderer against the scene's depth,
// and a marker seen through a wall reads as a bug.
//
// LINE OF SIGHT IS TESTED AT THE ANCHOR, NOT PER PIXEL. `sight` is the anchor's place on the
// WORLD viewport (uv) and its depth there; `fx_scene_depth` is that viewport's depth, captured
// after the opaque pass (Engine_FxDepthCaptureEffect — raw, reversed: nearer is larger). Five
// taps around the anchor give a fraction rather than a bit, so the whole marker fades as one at
// a wall's edge instead of being cut in half by it.

global uniform sampler2D fx_scene_depth;
global uniform float fx_scene_depth_valid;
// Player two's view's depth on split screen (Engine_FxDepthCaptureEffect.GlobalTextureSecondName).
// The marker draws on the HUD layer, not through the view, so the view it was projected through
// is stated: `sight_view` 1 for player two's half.
global uniform sampler2D fx_scene_depth_second;
uniform int sight_view = 0;

uniform sampler2D tex : source_color, filter_linear_mipmap, repeat_disable, hint_default_transparent;
uniform vec4 tint = vec4(1.0);

// `tex` is a glyph atlas: coverage in alpha, colour from the tint and the run's vertex colour.
uniform bool glyphs = false;

// A glyph label whose title outlines its world-icon text (Engine_UiTextContract.WorldIconTextStyle,
// Outlined): the glyphs marked outlined (UV2.x > 1.5, Engine_UiTextMesh) draw the outline
// ui_text.gdshader draws, from the shipped coverage. `supersample`: texture pixels per shipped texel.
uniform bool outline = false;
uniform float supersample = 1.0;

// Anchor uv.xy and depth; x < 0 leaves the marker untested.
uniform vec3 sight = vec3(-1.0, 0.0, 0.0);

// How much farther than the scene the anchor may read and still count as seen — a relative
// slack, since reversed depth falls off as 1/distance. Covers the sign board under its own
// marker and a head under its own heart.
const float SLACK = 0.04;
const vec2 TAP = vec2(0.0035, 0.006);

varying float seen;

#include "res://src/Engine/ui/ui_text_outline.gdshaderinc"

float tap(vec2 uv) {
	vec2 at = clamp(uv, vec2(0.001), vec2(0.999));
	float scene = sight_view == 1
		? textureLod(fx_scene_depth_second, at, 0.0).r
		: textureLod(fx_scene_depth, at, 0.0).r;
	return sight.z >= scene * (1.0 - SLACK) ? 1.0 : 0.0;
}

void vertex() {
	seen = 1.0;
	if (fx_scene_depth_valid > 0.5 && sight.x >= 0.0) {
		seen = (tap(sight.xy) + tap(sight.xy + vec2(TAP.x, 0.0)) + tap(sight.xy - vec2(TAP.x, 0.0)) +
			tap(sight.xy + vec2(0.0, TAP.y)) + tap(sight.xy - vec2(0.0, TAP.y))) * 0.2;
	}
}

void fragment() {
	vec4 texel = texture(tex, UV);
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);
	vec2 tex_size = max(vec2(textureSize(tex, 0)), vec2(1.0));
	vec4 colour = glyphs
		? vec4(tint.rgb * COLOR.rgb, texel.a * tint.a * COLOR.a)
		: texel * tint;
	if (outline && glyphs && UV2.x > 1.5) {
		vec2 texel_step = max(supersample, 1.0) / tex_size;
		colour = vec4(tint.rgb * COLOR.rgb * texel.a,
			ui_text_outline_cover(tex, UV, texel.a, uv_dx, uv_dy, false, 1e-4, 0.0, texel_step) *
				tint.a * COLOR.a);
	}
	float alpha = colour.a * smoothstep(0.1, 0.5, seen);
	// Zero-discard `depth_draw_never` blend path (`ALPHA = 0.0` is a blend no-op).
	ALBEDO = vec3(0.0);
	ALPHA = 0.0;
	if (alpha >= 0.01) {
		ALBEDO = colour.rgb;
		ALPHA = alpha;
	}
}
