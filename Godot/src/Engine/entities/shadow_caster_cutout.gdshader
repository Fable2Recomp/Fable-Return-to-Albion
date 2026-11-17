// Lightweight 1-tap derivative-free alpha-cutout shadow caster for character hair, lace,
// and garment cutout surfaces on `ShadowProxy` (`CastShadow = ShadowsOnly`).
// Samples `textureLod(..., 0.0)` with zero `dFdx`/`dFdy` and zero `NORMAL`/`TANGENT` reads.
shader_type spatial;
render_mode unshaded, cull_disabled, shadows_disabled, depth_draw_opaque, fog_disabled;

uniform sampler2D albedo_tex : hint_default_white, filter_linear_mipmap;
uniform float alpha_scissor_threshold : hint_range(0.0, 1.0) = 0.25;

void fragment() {
	float a = textureLod(albedo_tex, UV, 0.0).a;
	bool should_discard = (a < alpha_scissor_threshold);
	ALBEDO = vec3(0.0);
	if (should_discard) {
		discard;
	}
}
