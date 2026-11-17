#include "res://src/Engine/fx/fx_soft_math.gdshaderinc"

uniform float fx_soft_inv = 0.0;
// FX_HARD (Engine_FxDepthReads, for a material whose soft distance is zero): no depth uniform at
// all. Godot counts the DECLARATION as a depth read, and on Mobile one such material splits the
// frame's render pass and copies depth. The fade is 1 there either way.
#ifndef FX_HARD
uniform sampler2D fx_depth_tex : hint_depth_texture, filter_nearest;
#endif

/// The ease over squared distance to the scene behind, scaled by `inv` (1 / the soft distance); 0 = off.
float fx_soft_fade_by(float inv, float out_alpha, vec3 view_pos, vec2 screen_uv, mat4 inv_proj, mat4 inv_view) {
#ifdef FX_HARD
	return 1.0;
#else
	if (inv <= 0.0) {
		return 1.0;
	}
	if (out_alpha <= 0.01) {
		return 0.0;
	}
	// D3D12 / Intel Arc DXIL warning: callers may evaluate soft fade after per-pixel `discard;`
	// or inside an early-out branch, so sample depth via explicit `textureLod(..., 0.0)`.
	float depth = textureLod(fx_depth_tex, screen_uv, 0.0).x;
	return fx_soft_fade_from_depth(inv, out_alpha, view_pos, screen_uv, depth, inv_proj, inv_view);
#endif
}

float fx_soft_fade(float out_alpha, vec3 view_pos, vec2 screen_uv, mat4 inv_proj, mat4 inv_view) {
	return fx_soft_fade_by(fx_soft_inv, out_alpha, view_pos, screen_uv, inv_proj, inv_view);
}
