// The modern renderer's mist volume (Engine_ModernMist): one guest mist object as a Godot
// FogVolume. The title's MIST1 stages integrate a height layer over a 2D density texture its
// evolve pass (PSHADER_MIST_EVOLVE_DENSITY, blob 0072) writes each frame:
//   texel = offset + (1 - offset) * (0.5 + p1 / 3 + p2 / 6),
// two gradient-noise octaves over uv * tiling, moved by speed * time; the draw maps the guest's
// world (x, y) to uv through V+352 (scale.xy, offset.zw). Here the same expression feeds Godot's
// volumetric fog, which integrates and lights it.
shader_type fog;

uniform vec3 albedo : source_color = vec3(1.0);
// The guest's density times its strength factor ((strength - 1) * weight + 1).
uniform float extinction = 0.0;
// World (guest x, y) to density uv: scale.xy, offset.zw (V+352).
uniform vec4 remap = vec4(1.0, 1.0, 0.0, 0.0);
// tiling.xy, speed (per second).
uniform vec3 motion = vec3(1.0, 1.0, 0.0);
// The evolve pass's noise offset (c46.x): 1 is a flat texture.
uniform float flatten = 0.0;

vec2 gradient(vec2 cell) {
	float a = fract(sin(dot(cell, vec2(127.1, 311.7))) * 43758.5453) * 6.2831853;
	return vec2(cos(a), sin(a));
}

// Gradient noise, about [-1, 1].
float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	float a = dot(gradient(i), f);
	float b = dot(gradient(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0));
	float c = dot(gradient(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0));
	float d = dot(gradient(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0));
	return 1.41421356 * mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

void fog() {
	// Guest (x, y, z) is Godot (-x, z, y).
	vec2 guest = vec2(-WORLD_POSITION.x, WORLD_POSITION.z);
	vec2 uv = guest * remap.xy + remap.zw;
	vec2 p = uv * motion.xy + vec2(TIME * motion.z);
	float texel = 0.5 + noise(p) / 3.0 + noise(p * 2.0 + 17.0) / 6.0;
	texel = flatten + (1.0 - flatten) * clamp(texel, 0.0, 1.0);
	DENSITY = extinction * texel;
	ALBEDO = albedo;
}
