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

uniform sampler2D tex : source_color, filter_linear_mipmap, repeat_disable;
uniform vec4 tint = vec4(1.0);

// `tex` is a glyph atlas: coverage in alpha, colour from the tint and the run's vertex colour.
uniform bool glyphs = false;

// Anchor uv.xy and depth; x < 0 leaves the marker untested.
uniform vec3 sight = vec3(-1.0, 0.0, 0.0);

// How much farther than the scene the anchor may read and still count as seen — a relative
// slack, since reversed depth falls off as 1/distance. Covers the sign board under its own
// marker and a head under its own heart.
const float SLACK = 0.04;
const vec2 TAP = vec2(0.0035, 0.006);

varying float seen;

float tap(vec2 uv) {
	float scene = textureLod(fx_scene_depth, clamp(uv, vec2(0.001), vec2(0.999)), 0.0).r;
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
	vec4 colour = glyphs
		? vec4(tint.rgb * COLOR.rgb, texel.a * tint.a * COLOR.a)
		: texel * tint;
	float alpha = colour.a * smoothstep(0.1, 0.5, seen);
	if (alpha < 0.01) {
		discard;
	}

	ALBEDO = colour.rgb;
	ALPHA = alpha;
}
