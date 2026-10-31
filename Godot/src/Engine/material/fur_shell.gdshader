// SHELL FUR, MODERN (Engine_ModernFur): the furred surface drawn again once per shell, pushed out
// along its normal, cut by the fur slices, and lit by Godot's lights through the shared model.
// Learned from the title's shell stages; not its code.
//
// Per shell, h = layer / N (layer 1..N-1). Per vertex, its fur record: COLOR.a = the hair's
// length share, CUSTOM0 = the fur texture's uv at the root (xy) and its drift per unit h (zw).
//
//   reach      r = (h + length - 1) L                        L = the fur length for the distance
//   position   vertex + r normal
//   occlusion  sat(1 - (1 - h)^2 k)                          k = the material's occlusion
//   skin share (1 - h) / length (1 when length < 0.01): the root takes the skin, the tip the fur
//   alpha      slice.a (1 - e) + e, e = 0.3 sat(|eye - p| / 200 L) |N.V| — far fur fills in;
//              zero where the hair ends before this shell (r <= 0)
//   colour     occlusion mix(slice.rgb lit fur, lit skin, share), skin the surface's albedo
//
// THE FUR'S LIGHT (the fur material's stage, which the title bakes into its prelit fur texture):
// T = the view projected into the surface, normalize(V - (N.V) N); R = reflect(-L, T). Per light,
// gated by sat(2 N.L + 0.2): diffuse albedo sat(T.L); a primary lobe (0.5, 0.5, 0.55)
// sat(4 T.L) (R.V)^4 and a secondary (0.3, 0.3, 0.35) (R.V)^7 (the primary is summed with both
// lobe colours). The skin takes plain Lambert, as its own surface does.
//
// The slice is floor(clamp(h, 0, 0.9999) 20) of the material's 20 (Engine_ClassicFur.SlicesFor).
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back;

#include "res://src/Engine/material/fable_lighting.gdshaderinc"
#include "res://src/Engine/level/environment/fable_atmosphere.gdshaderinc"

uniform sampler2D albedo_tex : hint_default_white, filter_linear_mipmap_anisotropic;
uniform bool is_pbr = false;
uniform sampler2DArray fur_slices : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform int fur_layer = 1;
uniform float fur_occlusion = 0.0;

// Per furred instance, per frame: N, and L for its distance.
// `entity_tint` is declared first so its instance uniform slot index (6) matches `standard_material_body.gdshaderinc`.
instance uniform vec4 entity_tint = vec4(1.0);
instance uniform float fur_shells = 16.0;
instance uniform float fur_length = 0.0;

varying vec2 v_fur_uv;
varying float v_occlusion;
varying float v_skin_share;
varying float v_edge;
varying float v_fill;
varying vec3 v_world_normal;
// The fur's share of the lit colour (against the skin's), and of the specular, per pixel.
varying float v_fur_share;
varying float v_fur_spec;

const vec3 FUR_PRIMARY = vec3(0.2, 0.2, 0.2) + vec3(0.3, 0.3, 0.35);
const vec3 FUR_SECONDARY = vec3(0.3, 0.3, 0.35);

void vertex() {
	float h = float(fur_layer) / max(fur_shells, 1.0);
	float share = COLOR.a;
	float reach = (h + share - 1.0) * fur_length;
	vec3 pushed = VERTEX + reach * NORMAL;

	vec3 world = (MODEL_MATRIX * vec4(pushed, 1.0)).xyz;
	vec3 world_normal = mat3(MODEL_MATRIX) * NORMAL;
	float edge = 0.0;
	float fill = 0.0;
	if (reach > 0.0) {
		vec3 to_eye = INV_VIEW_MATRIX[3].xyz - world;
		float distance_to_eye = length(to_eye);
		float far = clamp(distance_to_eye / max(fur_length * 200.0, 1e-6), 0.0, 1.0);
		fill = 0.3 * far * abs(dot(to_eye / max(distance_to_eye, 1e-6), world_normal));
		edge = 1.0 - fill;
	}

	float inner = 1.0 - h;
	v_fur_uv = CUSTOM0.xy + CUSTOM0.zw * h;
	v_occlusion = clamp(1.0 - inner * inner * fur_occlusion, 0.0, 1.0);
	v_skin_share = share >= 0.01 ? inner / share : 1.0;
	v_edge = edge;
	v_fill = fill;
	v_world_normal = normalize(world_normal);
	fable_world = world;
	VERTEX = pushed;
}

void fragment() {
	float h = float(fur_layer) / max(fur_shells, 1.0);
	vec4 slice = texture(fur_slices, vec3(v_fur_uv, floor(clamp(h, 0.0, 0.9999) * 20.0)));
	float alpha = slice.a * v_edge + v_fill;

	vec3 skin = texture(albedo_tex, UV).rgb;
	if (is_pbr) {
		skin = mix(skin / 12.92, pow((skin + vec3(0.055)) / 1.055, vec3(2.4)), step(vec3(0.04045), skin));
	} else {
		skin *= skin;
	}
	// occlusion ((1 - share) slice + share) skin, split into the fur's and the skin's parts.
	float fur = (1.0 - v_skin_share) * slice.r;
	float weight = fur + v_skin_share;
	vec3 albedo = v_occlusion * weight * skin * entity_tint.rgb;
	v_fur_share = weight > 0.0 ? fur / weight : 0.0;
	v_fur_spec = v_occlusion * fur;

	vec3 bake = fable_bake(v_world_normal, vec4(1.0), vec2(0.0)) * fable_lightmap_rgb();
	if (lightmap_colour.w < 3.5) {
		bake = fable_sky_tether(bake);
	}
	ALBEDO = albedo;
	EMISSION = albedo * fable_ambient(bake);

	vec3 fog_t;
	vec3 fog_c;
	if (fable_atmosphere_terms_for(VERTEX, INV_VIEW_MATRIX, true, fog_t, fog_c)) {
		fable_atmosphere_apply(fog_t, fog_c, 1.0, ALBEDO, EMISSION);
	}
	ALPHA = alpha;
	ALPHA_SCISSOR_THRESHOLD = 16.0 / 255.0;
}

void light() {
	float n_dot_l = dot(NORMAL, LIGHT);
	// Both the skin's N.L and the fur's gate are zero below N.L = -0.1.
	float gate = clamp(2.0 * n_dot_l + 0.2, 0.0, 1.0);
	float atten = gate > 0.0
		? fable_attenuation(ATTENUATION, LIGHT_IS_DIRECTIONAL, SPECULAR_AMOUNT, fable_world) : 0.0;
	if (atten > 0.0) {
		vec3 colour = atten * (LIGHT_COLOR / PI);
		vec3 across = VIEW - dot(NORMAL, VIEW) * NORMAL;
		vec3 t = dot(across, across) > 0.0 ? normalize(across) : NORMAL;
		float t_dot_l = dot(t, LIGHT);
		float fur_diffuse = gate * clamp(t_dot_l, 0.0, 1.0);
		DIFFUSE_LIGHT += colour * mix(clamp(n_dot_l, 0.0, 1.0), fur_diffuse, v_fur_share);

		float r_dot_v = clamp(dot(2.0 * t_dot_l * t - LIGHT, VIEW), 0.0, 1.0);
		float r2 = r_dot_v * r_dot_v;
		vec3 lobes = FUR_PRIMARY * clamp(4.0 * clamp(t_dot_l, 0.0, 1.0), 0.0, 1.0) * (r2 * r2)
			+ FUR_SECONDARY * pow(r_dot_v, 7.0);
		SPECULAR_LIGHT += colour * gate * v_fur_spec * lobes;
	}
}
