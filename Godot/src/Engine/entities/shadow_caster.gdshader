// The cast-only material: every shadow proxy, prop and entity batch twin, and held-weapon caster.
//
// INVISIBLE BY CONSTRUCTION. These meshes are ShadowsOnly, so Godot never draws them in a colour
// pass; discarding every fragment outside the shadow pass keeps them unseen whatever the cast
// setting says. In the shadow pass the branch is never taken; only depth is written.
//
// A PROP'S BAND, the body's (standard_material_body.gdshaderinc, Engine_PropFade): a tier-culled
// prop dithers out over the last share of its radius and in over its arrival, and its shadow has
// to go with it or it would stand on the ground after the copy is gone. The same instance
// uniforms, copied to the twin or proxy (Engine_PropFade.Copy), the same ordered dither, measured
// from the main camera (MAIN_CAM_INV_VIEW_MATRIX is the main view in the shadow passes). Unset,
// they draw the caster whole, as every other owner of this material expects.
shader_type spatial;
render_mode unshaded, cull_disabled;

#include "res://src/Engine/material/fable_dither.gdshaderinc"

// PINNED to the body's slots: a twin keeps a prop's alpha-cut surfaces with their own (body)
// material beside this one on the same instance, and Godot takes one index per name.
instance uniform vec4 xr_tree_fade_in : instance_index(10) = vec4(0.0);
instance uniform vec4 xr_tree_fade_out : instance_index(11) = vec4(0.0);
instance uniform vec3 xr_tree_centre : instance_index(12) = vec3(0.0);
instance uniform float xr_fade_alpha : instance_index(13) = 1.0;
varying float v_fade;

void vertex() {
	v_fade = xr_fade_alpha;
	if (xr_tree_fade_out.x > 0.0) {
		float d = distance(MAIN_CAM_INV_VIEW_MATRIX[3].xyz, (MODEL_MATRIX * vec4(xr_tree_centre, 1.0)).xyz);
		float fade_in = xr_tree_fade_in.y > 0.0
			? pow(clamp((d - xr_tree_fade_in.x) * xr_tree_fade_in.y, 0.0, 1.0), xr_tree_fade_in.z)
			: 1.0;
		float fade_out = pow(clamp((xr_tree_fade_out.x - d) * xr_tree_fade_out.y, 0.0, 1.0), xr_tree_fade_out.z);
		v_fade *= fade_in * fade_out;
	}
}

void fragment() {
	// D3D12 / Intel Arc DXIL warning: NEVER place multiple or mid-shader `discard;` statements.
	// Evaluate non-shadow pass first into `should_discard` so skipped fragments bypass Bayer math,
	// and emit ONE single `if (should_discard) { discard; }` at the very end of `fragment()`.
	bool should_discard = !IN_SHADOW_PASS;

	if (!should_discard && v_fade < 0.999) {
		if (v_fade <= fable_bayer_threshold(FRAGCOORD.xy)) {
			should_discard = true;
		}
	}

	ALBEDO = vec3(0.0);
	if (should_discard) {
		discard;
	}
}
