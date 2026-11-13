// One lit object's share of the targeting highlight composite (mobile_highlight_composite.gdshader)
// without a depth texture (android_highlight_depth_test, Engine_MobileHighlight): a quad over the
// whole view placed at the depth its halo is tested at, so the depth buffer's own test (Mobile's
// reversed-Z GREATER_OR_EQUAL) passes exactly where the scene is at or beyond that distance, the
// composite's `tests[k] <= scene`. With no depth-texture uniform in the list, Mobile keeps the
// opaque and transparent lists in one render pass. One quad per object, each blending its glow
// once ONE / INVSRCALPHA: the composite's n blends of the same glow.
shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_disabled, fog_disabled,
	shadows_disabled, ambient_light_disabled, specular_disabled;

uniform sampler2D layer_tex : filter_nearest;
uniform sampler2D glow_tex : filter_linear;

/// This object's screen rect (u0, v0, u1, v1).
uniform vec4 rect = vec4(0.0);

/// The view distance its halo is tested at, inside the camera's clip range.
uniform float test = 1.0;

void vertex() {
	// A 2x2 quad over the whole view, at the tested distance's depth.
	vec4 at = PROJECTION_MATRIX * vec4(0.0, 0.0, -test, 1.0);
	POSITION = vec4(VERTEX.xy * at.w, at.z, at.w);
}

void fragment() {
	vec2 uv = SCREEN_UV;
	if (uv.x < rect.x || uv.x > rect.z || uv.y < rect.y || uv.y > rect.w || texture(layer_tex, uv).g > 0.5) {
		discard;
	}

	vec4 glow = clamp(texture(glow_tex, uv), vec4(0.0), vec4(1.0));
	ALBEDO = glow.rgb;
	ALPHA = glow.a;
}
