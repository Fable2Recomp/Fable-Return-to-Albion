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

instance uniform vec4 xr_tree_fade_in = vec4(0.0);
instance uniform vec4 xr_tree_fade_out = vec4(0.0);
instance uniform vec3 xr_tree_centre = vec3(0.0);
instance uniform float xr_fade_alpha = 1.0;
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
	if (!IN_SHADOW_PASS) {
		discard;
	}

	if (v_fade < 0.999) {
		const float bayer[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0,
			3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
		ivec2 cell = ivec2(FRAGCOORD.xy) & ivec2(3);
		if (v_fade <= (bayer[(cell.y * 4) + cell.x] + 0.5) / 16.0) {
			discard;
		}
	}

	ALBEDO = vec3(0.0);
}
