shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded, shadows_disabled, fog_disabled;

uniform sampler2D element_tex : hint_default_transparent, filter_linear, repeat_disable;
uniform vec3 element_dir = vec3(0.0, 1.0, 0.0);
uniform float element_distance = 1900.0;
uniform vec2 element_half = vec2(1.0, 1.0);
uniform vec4 element_colour = vec4(1.0, 1.0, 1.0, 1.0);
uniform float element_facing_power = 0.0;
uniform float element_depth_bias = 0.000001;
uniform int element_mode = 0;
uniform bool is_pbr = false;
uniform vec4 element_phase = vec4(0.0, 0.0, 1.0, 0.0);

global uniform vec2 fable_vista_ocean;

varying vec4 v_colour;
varying vec3 v_world_offset;
varying vec2 v_quad_pos;
varying flat float v_upright_cam;

void vertex() {
	vec3 dir_view = (VIEW_MATRIX * vec4(normalize(element_dir), 0.0)).xyz;
	float facing = -dir_view.z;

	if (facing <= 0.0) {
		POSITION = vec4(0.0, 0.0, -10.0, 1.0);
		v_colour = vec4(0.0);
		v_world_offset = vec3(0.0);
		v_quad_pos = vec2(0.0);
		v_upright_cam = 0.0;
	} else {
		vec3 view_pos = (dir_view * element_distance) +
		                vec3(VERTEX.x * element_half.x, VERTEX.y * element_half.y, 0.0);
		vec4 clip = PROJECTION_MATRIX *
		            vec4(view_pos.xy, min(view_pos.z, -0.01), 1.0);
		float safe_w = abs(clip.w) > 1e-6 ? clip.w : 1e-6;

		float far_depth = step(0.5, -PROJECTION_MATRIX[2][2]);
		POSITION = vec4(clip.xy / safe_w,
		                mix(far_depth, 1.0 - far_depth, element_depth_bias), 1.0);

		v_colour = vec4(
			element_colour.rgb * pow(facing, element_facing_power),
			element_colour.a);
		v_world_offset = (INV_VIEW_MATRIX * vec4(view_pos, 0.0)).xyz;
		v_quad_pos = VERTEX.xy;
		v_upright_cam = INV_VIEW_MATRIX[1].y;
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
global uniform float fable_emission_boost = 1.0;

void fragment() {
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);
	vec4 texel = textureGrad(element_tex, UV, uv_dx, uv_dy);

	float ocean_vis = 1.0;
	if (v_upright_cam > 0.0 && fable_vista_ocean.x > 0.5 && CAMERA_POSITION_WORLD.y > (fable_vista_ocean.y + 0.02)) {
		float world_y = CAMERA_POSITION_WORLD.y + v_world_offset.y;
		ocean_vis = smoothstep(-1.5, 1.5, world_y - fable_vista_ocean.y);
	}

	float hdr_boost = max(fable_emission_boost, 1.0);
	float boost = is_pbr
		? 1.0
		: ((element_mode == 0 || element_mode == 1)
			? hdr_boost
			: (element_mode == 2 ? mix(1.0, hdr_boost, 0.45) : 1.0));
	if (element_mode == 0) {
		ALBEDO = max(texel.rgb * v_colour.rgb * boost, vec3(0.0));
		ALPHA = texel.a * v_colour.a * ocean_vis;
	} else if (element_mode == 2) {
		// Modern Moon disc: sample the Full-Moon crater map with a pole-to-pole elliptical
		// crescent/gibbous terminator and perpendicular gradient AA so partial phases render
		// true horned crescents while the covered side blends seamlessly into dusk/night sky.
		vec2 q_disc = v_quad_pos / 0.92;
		float r_disc = length(q_disc);
		float disc_mask = 1.0 - smoothstep(0.95, 1.0, r_disc);

		float dir_xy_len = length(element_phase.xy);
		float term_mask = element_phase.z > 0.0 ? 1.0 : 0.0;
		float lommel = 1.0;
		if (dir_xy_len > 1e-4) {
			vec2 axis_u = element_phase.xy / dir_xy_len;
			vec2 axis_v = vec2(-axis_u.y, axis_u.x);
			float u = dot(q_disc, axis_u);
			float v = dot(q_disc, axis_v);
			float one_minus_v2 = max(1.0 - (v * v), 0.0);
			float x_semi = sqrt(one_minus_v2);
			float d_term = u + (element_phase.z * x_semi);
			float inv_grad = inversesqrt(1.0 + ((element_phase.z * element_phase.z * v * v) / max(one_minus_v2, 0.025)));
			float d_perp = d_term * inv_grad;
			term_mask = smoothstep(-0.022, 0.028, d_perp);
			lommel = 0.42 + (0.58 * smoothstep(-0.02, 0.28, d_term));
		}

		vec3 tex_lin = is_pbr ? texel.rgb : (texel.rgb * texel.rgb);
		float edge_alpha = clamp(texel.a * (1.0 / 0.502), 0.0, 1.0) * disc_mask * term_mask;

		ALBEDO = edge_alpha > 1e-5
			? max(tex_lin * v_colour.rgb * (0.64 * lommel * boost), vec3(0.0))
			: vec3(0.0);
		ALPHA = (0.55 * edge_alpha) * v_colour.a * ocean_vis;
	} else {
		ALBEDO = max(texel.rgb * v_colour.rgb * texel.a * boost * ocean_vis, vec3(0.0));
		ALPHA = texel.a * ocean_vis;
	}
}
