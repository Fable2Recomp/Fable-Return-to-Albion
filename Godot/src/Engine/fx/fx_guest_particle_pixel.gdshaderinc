// THE GUEST'S PARTICLES AND TRAILS, MODERN — the pixel side both share (fx_guest_particle,
// fx_guest_trail), and the lighting: Godot's lights through the shared model (fable_lighting).
// The behaviour was learned from the title's particle stages; nothing here is the title's code.
//
// Each texture is read in its frame's cell (column = frame mod columns, row = frame / columns) and
// the stack is folded in order, texture n taking weight sat((time - start[n-1]) / (start[n] -
// start[n-1])). The colour is the texel squared (the game's decode) times the vertex's alpha, lit
// (Godot's lights plus the ambient) in the share the self-illumination leaves and the key colour
// emitted in the rest; alpha is the texel's times the vertex's. A soft draw eases out where it
// meets the scene behind (fx_soft). The title's adaptive pre-exposure is Godot's exposure here.
//
// The vertex stage writes the varyings below, `fable_world`, UV, VERTEX and NORMAL (view space).

#include "res://src/Engine/material/fable_lighting.gdshaderinc"
#include "res://src/Engine/fx/fx_soft.gdshaderinc"

// The material's own ambient, added to the scene's; zero when it states none.
uniform vec4 vs_g_GlobalAmbient;

uniform sampler2D ps_g_ParticleTextureSamplers : hint_default_black, filter_linear_mipmap, repeat_disable;
uniform sampler2D ps_s1 : hint_default_black, filter_linear_mipmap, repeat_disable;
uniform sampler2D ps_s2 : hint_default_black, filter_linear_mipmap, repeat_disable;
uniform sampler2D ps_s3 : hint_default_black, filter_linear_mipmap, repeat_disable;
uniform int particle_textures = 1;
// Per texture lane: its start time, the blend rate into the NEXT lane, its columns, and its cell size.
uniform vec4 ps_g_TextureStartTimes;
uniform vec4 ps_g_TextureStartTimesInvDelta;
uniform vec4 ps_g_TextureNumXDivs;
uniform vec4 ps_g_TextureUVScales[4];
uniform vec4 ps_g_DepthFadeScale;
uniform float xr_alpha_threshold = 0.00392157;
// 0 for a ONE/ONE draw: premultiplied with its alpha held at zero, a pure add.
uniform float particle_alpha_out = 1.0;

varying vec3 v_key_colour;
varying float v_alpha;
varying float v_self_illum;
varying float v_texture_time;
varying flat vec4 v_frames;
varying vec3 v_normal_world;

// Godot (x, y, z) is the guest's (-x, z, y); the map is its own inverse, and a rotation, so a
// cross product taken on either side of it is the same vector.
vec3 particle_swap(vec3 v) {
	return vec3(-v.x, v.z, v.y);
}

// A texture lane's texel in its frame's cell; a lane with no sheet reads the whole texture.
// Uses explicit `textureGrad` with precomputed quad derivatives so cell/lane branching stays safe on D3D12.
vec4 particle_texel(sampler2D tex, vec2 uv, vec2 uv_dx, vec2 uv_dy, float frame, float columns, vec4 cell) {
	if (max(cell.x, cell.y) <= 0.0) {
		return textureGrad(tex, uv, uv_dx, uv_dy);
	}
	float across = max(columns, 1.0);
	float n = max(frame, 0.0);
	float row = floor(n / across);
	float column = n - row * across;
	return textureGrad(tex, (uv + vec2(column, row)) * cell.xy, uv_dx * cell.xy, uv_dy * cell.xy);
}

void fragment() {
	vec2 uv_dx = dFdx(UV);
	vec2 uv_dy = dFdy(UV);
	vec4 texel = particle_texel(ps_g_ParticleTextureSamplers, UV, uv_dx, uv_dy, v_frames.x, ps_g_TextureNumXDivs.x, ps_g_TextureUVScales[0]);
	if (particle_textures > 1) {
		float w = clamp((v_texture_time - ps_g_TextureStartTimes.x) * ps_g_TextureStartTimesInvDelta.x, 0.0, 1.0);
		texel = mix(texel, particle_texel(ps_s1, UV, uv_dx, uv_dy, v_frames.y, ps_g_TextureNumXDivs.y, ps_g_TextureUVScales[1]), w);
	}
	if (particle_textures > 2) {
		float w = clamp((v_texture_time - ps_g_TextureStartTimes.y) * ps_g_TextureStartTimesInvDelta.y, 0.0, 1.0);
		texel = mix(texel, particle_texel(ps_s2, UV, uv_dx, uv_dy, v_frames.z, ps_g_TextureNumXDivs.z, ps_g_TextureUVScales[2]), w);
	}
	if (particle_textures > 3) {
		float w = clamp((v_texture_time - ps_g_TextureStartTimes.z) * ps_g_TextureStartTimesInvDelta.z, 0.0, 1.0);
		texel = mix(texel, particle_texel(ps_s3, UV, uv_dx, uv_dy, v_frames.w, ps_g_TextureNumXDivs.w, ps_g_TextureUVScales[3]), w);
	}

	float alpha = texel.a * v_alpha;
	bool should_discard = (alpha < xr_alpha_threshold);
	ALBEDO = vec3(0.0);
	EMISSION = vec3(0.0);
	ALPHA = 0.0;
	// Lit depth_draw_never blended particles never emit mid-PSMain `discard`: ALBEDO/EMISSION/ALPHA = 0
	// is a hardware blend no-op and lets light() skip cleanly without fragment/IBL helper-lane hazards.
	if (!should_discard) {
		float soft = fx_soft_fade_by(ps_g_DepthFadeScale.x, alpha, VERTEX, SCREEN_UV, INV_PROJECTION_MATRIX, INV_VIEW_MATRIX);
		alpha *= soft;
		if (alpha >= xr_alpha_threshold) {
			// Colour premultiplied by the vertex's alpha, as the game writes it; the blend does the rest.
			vec3 base = texel.rgb * texel.rgb * v_alpha * soft;
			vec3 lit = base * (1.0 - v_self_illum);
			ALBEDO = lit;
			EMISSION = lit * (fable_ambient(vec3(0.0)) + vs_g_GlobalAmbient.rgb) + base * v_key_colour * v_self_illum;
			ALPHA = alpha * particle_alpha_out;
			NORMAL = mat3(VIEW_MATRIX) * v_normal_world;
		}
	}
}

void light() {
	if (any(greaterThan(ALBEDO, vec3(0.0))) && dot(NORMAL, LIGHT) > 0.0) {
		float atten = fable_attenuation(ATTENUATION, LIGHT_IS_DIRECTIONAL, SPECULAR_AMOUNT, fable_world);
		if (atten > 0.0) {
			DIFFUSE_LIGHT += fable_direct(NORMAL, LIGHT, atten, LIGHT_COLOR);
		}
	}
}
