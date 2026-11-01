// Targeting highlight composite where the post chain cannot run (Engine_MobileHighlight): the work of
// fable_postfx_highlightcomposite.glsl as a screen quad in the main view's transparent pass, so it
// can read the scene depth Godot copies for it (Mobile runs no post-opaque compositor effect, so the
// FX depth capture is never there) and blends into the scene before the tone curve, as the guest's
// does. Kept off the highlighted silhouettes (layer G), tested per object against the scene depth,
// blended ONE / INVSRCALPHA once per object whose rect covers the pixel.
shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_test_disabled, depth_draw_never, cull_disabled, fog_disabled,
	shadows_disabled, ambient_light_disabled, specular_disabled;

uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;
uniform sampler2D layer_tex : filter_nearest;
uniform sampler2D glow_tex : filter_linear;

/// Objects with a rect on screen this frame.
uniform int count = 0;

/// Per object: screen rect (u0, v0, u1, v1).
uniform vec4 rects[8];

/// Per object: the view distance the halo is tested at (the centre's for a small target, the
/// nearest corner's for a large one).
uniform float tests[8];

void vertex() {
	// A 2x2 quad over the whole view, at the near plane.
	POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
	vec2 uv = SCREEN_UV;
	bool covered = false;
	for (int k = 0; k < count; ++k) {
		vec4 r = rects[k];
		covered = covered || (uv.x >= r.x && uv.x <= r.z && uv.y >= r.y && uv.y <= r.w);
	}

	if (!covered || texture(layer_tex, uv).g > 0.5) {
		discard;
	}

	float z = texture(depth_tex, uv).r;
	vec4 view = INV_PROJECTION_MATRIX * vec4(uv * 2.0 - 1.0, z, 1.0);
	float scene = -view.z / view.w;
	int n = 0;
	for (int k = 0; k < count; ++k) {
		vec4 r = rects[k];
		if (uv.x >= r.x && uv.x <= r.z && uv.y >= r.y && uv.y <= r.w && tests[k] <= scene) {
			++n;
		}
	}

	if (n == 0) {
		discard;
	}

	// n blends of the same glow: rgb * (1 + (1 - a) + ...) over the scene * (1 - a)^n.
	vec4 glow = clamp(texture(glow_tex, uv), vec4(0.0), vec4(1.0));
	float keep = pow(1.0 - glow.a, float(n));
	float sum = glow.a > 1e-4 ? (1.0 - keep) / glow.a : float(n);
	ALBEDO = glow.rgb * sum;
	ALPHA = 1.0 - keep;
}
