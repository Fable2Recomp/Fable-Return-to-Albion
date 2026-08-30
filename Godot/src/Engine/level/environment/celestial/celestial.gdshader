shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

uniform sampler2D element_tex : filter_linear, repeat_disable;
uniform vec3 element_dir = vec3(0.0, 1.0, 0.0);
uniform float element_distance = 1900.0;
uniform vec2 element_half = vec2(1.0, 1.0);
uniform vec4 element_colour = vec4(1.0, 1.0, 1.0, 1.0);
uniform float element_facing_power = 0.0;
uniform float element_depth_bias = 0.000001;
uniform int element_mode = 0;

varying vec4 v_colour;

void vertex() {
	vec3 dir_view = (VIEW_MATRIX * vec4(normalize(element_dir), 0.0)).xyz;
	float facing = -dir_view.z;

	if (facing <= 0.0) {
		POSITION = vec4(0.0, 0.0, -10.0, 1.0);
		v_colour = vec4(0.0);
	} else {
		vec3 view_pos = (dir_view * element_distance) +
		                vec3(VERTEX.x * element_half.x, VERTEX.y * element_half.y, 0.0);
		vec4 clip = PROJECTION_MATRIX *
		            vec4(view_pos.xy, min(view_pos.z, -0.01), 1.0);

		float far_depth = step(0.5, -PROJECTION_MATRIX[2][2]);
		POSITION = vec4(clip.xy / clip.w,
		                mix(far_depth, 1.0 - far_depth, element_depth_bias), 1.0);

		v_colour = vec4(
			element_colour.rgb * pow(facing, element_facing_power),
			element_colour.a);
	}
}

// THE ELEMENTS WRITE LINEAR HDR AND NOTHING ELSE. No tonemap here, no transfer function here.
//
// Neither a per-element Reinhard nor an sRGB decode belongs here. The shipped shaders are
// pure multiply chains — 0118 sundisc, in full:
//
//     r0    = tfetch2D(g_SunDiscTexture, uv)
//     r1.xyz = r0.xyz * g_SunDiscColour.xyz
//     r1.xyz = r1.xyz * r0.www
//     oC0.xyz = r1.xyz * g_GlobalAmbientAndBrightness.www
//     oC0.w   = r0.w
//
// and 0119 moondisc is the same with MoonColour and `oC0.w = MoonColour.w * texel.a`. There is
// no divide, no saturate, no pow, no sqrt, and no 3.3333 — the only literals in the whole
// celestial set are moonbloom's 0.2 and sunbeams' 1/6.
//
// The game tonemaps ONCE, globally, in the post-processing material: a 1D curve texture indexed
// by `luma * g_RecipMaxLuminance`, applied as a scalar gain, followed by
// `pow(colour, g_GlobalGammaAdjustment.x)`. Both stages live in 0500, not out here — and the
// gamma is in 0499 too, the variant with the tone curve switched OFF, which is what proves it
// belongs to the output stage rather than the tonemap. Per-element Reinhard was compressing the
// same signal twice, and the sRGB decode was inverting a curve the game never applies: its
// transfer is a bare power function with a runtime exponent, with no linear toe anywhere.
//
// So the equivalent of `g_GlobalAmbientAndBrightness.w` goes out raw and Godot's environment
// tonemapper stands in for the guest's global one, which is where it belongs.
// ONE MULTIPLY CHAIN, NOTHING ELSE. 0118 sundisc is
// `texel.rgb * g_SunDiscColour.rgb * texel.a * c20.w`, 0119 moondisc the same with MoonColour —
// no Reinhard, no squaring, no per-element exposure. The celestial set is the family that
// squares NOTHING (0114's asymmetry note), and c20.w's equivalent is the composite's global
// exposure, so it does not belong here either.
void fragment() {
	vec4 texel = texture(element_tex, UV);
	if (element_mode == 0) {
		ALBEDO = max(texel.rgb * v_colour.rgb, vec3(0.0));
		ALPHA = texel.a * v_colour.a;
	} else {
		ALBEDO = max(texel.rgb * v_colour.rgb * texel.a, vec3(0.0));
		ALPHA = element_mode == 2 ? texel.a * v_colour.a : texel.a;
	}
}
