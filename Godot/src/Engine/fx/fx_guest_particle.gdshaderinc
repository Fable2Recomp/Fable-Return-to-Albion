// THE GUEST'S PARTICLE GROUPS, MODERN: the camera-facing quads of the groups the guest simulates
// (Engine_ClassicParticles' shared feed), placed by our own vertex stage; the pixel side and the
// lighting are fx_guest_particle_pixel's. Learned from the title's particle stages; not its code.
//
// THE STREAM. Four vertices a record. VERTEX is the record's raw guest-space position, CUSTOM0 =
// (size x, size y, rotation, keyframe time), CUSTOM1 = each texture's sprite-sheet frame. The
// corner is the vertex index's position in its quad: (0,0), (1,0), (1,1), (0,1).
//
// PER CORNER. The keyframe tables are sampled at the record's time, linearly between floor(t) and
// the next key: colour, (scale x, scale y, self-illumination, texture time) and the global scale.
//
//   offset     a = (u - 1/2) size.x scale.x,  b = (v - 1/2) size.y scale.y, turned by the rotation
//   corner     Q = centre + a' global.x right + b' global.y up       (camera axes, or the group's)
//   fades      in = sat((|eye - Q| - in.x) in.z),  out = sat((out.x - |eye - Q|) out.y)
//   alpha      key.a in out out.z
//   normal     normalize(normalize(eye - centre) + normalize(Q - centre) / 2) — a puff, lit round

#include "res://src/Engine/fx/fx_guest_particle_pixel.gdshaderinc"

const int PARTICLE_KEYS = 20;

// The group's transform, row-major rows over (position, 1), guest axes.
uniform vec4 vs_g_MatWorld[3];
uniform vec4 vs_g_KeyFrameColors[20];
// (scale x, scale y, self-illumination, texture time) per key.
uniform vec4 vs_g_KeyFrameScalesSelfIllumFactorStartTimes[20];
uniform vec4 vs_g_KeyFrameGlobalScales[20];
// (start, end, 1 / (end - start)), and (end, 1 / (end - start), intensity).
uniform vec4 vs_g_FadeInDistanceInfo;
uniform vec4 vs_g_FadeOutDistanceInfo;
// A group facing fixed axes (guest space) instead of the camera.
uniform bool xr_fixed_axes = false;
uniform vec4 xr_fixed_up = vec4(0.0, 0.0, 1.0, 0.0);
uniform vec4 xr_fixed_right = vec4(1.0, 0.0, 0.0, 0.0);

void vertex() {
	int corner = VERTEX_ID % 4;
	float u = (corner == 1 || corner == 2) ? 1.0 : 0.0;
	float v = corner >= 2 ? 1.0 : 0.0;

	float key = max(CUSTOM0.w, 0.0);
	int k0 = min(int(key), PARTICLE_KEYS - 1);
	int k1 = min(k0 + 1, PARTICLE_KEYS - 1);
	float f = fract(key);
	vec4 colour = mix(vs_g_KeyFrameColors[k0], vs_g_KeyFrameColors[k1], f);
	vec4 scales = mix(vs_g_KeyFrameScalesSelfIllumFactorStartTimes[k0], vs_g_KeyFrameScalesSelfIllumFactorStartTimes[k1], f);
	vec2 global_scale = mix(vs_g_KeyFrameGlobalScales[k0].xy, vs_g_KeyFrameGlobalScales[k1].xy, f);

	vec4 local = vec4(VERTEX, 1.0);
	vec3 centre = particle_swap(vec3(
		dot(vs_g_MatWorld[0], local), dot(vs_g_MatWorld[1], local), dot(vs_g_MatWorld[2], local)));

	vec2 offset = (vec2(u, v) - 0.5) * CUSTOM0.xy * scales.xy;
	float c = cos(CUSTOM0.z);
	float s = sin(CUSTOM0.z);
	vec2 turned = vec2(offset.x * c - offset.y * s, offset.x * s + offset.y * c) * global_scale;

	vec3 right = xr_fixed_axes ? particle_swap(xr_fixed_right.xyz) : INV_VIEW_MATRIX[0].xyz;
	vec3 up = xr_fixed_axes ? particle_swap(xr_fixed_up.xyz) : INV_VIEW_MATRIX[1].xyz;
	vec3 world = centre + turned.x * right + turned.y * up;

	vec3 eye = INV_VIEW_MATRIX[3].xyz;
	float d = distance(eye, world);
	float fade_in = clamp((d - vs_g_FadeInDistanceInfo.x) * vs_g_FadeInDistanceInfo.z, 0.0, 1.0);
	float fade_out = clamp((vs_g_FadeOutDistanceInfo.x - d) * vs_g_FadeOutDistanceInfo.y, 0.0, 1.0);

	vec3 to_eye = eye - centre;
	vec3 out_from = world - centre;
	vec3 puff = (dot(to_eye, to_eye) > 0.0 ? normalize(to_eye) : vec3(0.0, 1.0, 0.0))
		+ (dot(out_from, out_from) > 0.0 ? 0.5 * normalize(out_from) : vec3(0.0));
	v_normal_world = dot(puff, puff) > 0.0 ? normalize(puff) : vec3(0.0, 1.0, 0.0);

	v_key_colour = colour.rgb;
	v_alpha = colour.a * fade_in * fade_out * vs_g_FadeOutDistanceInfo.z;
	v_self_illum = scales.z;
	v_texture_time = scales.w;
	v_frames = CUSTOM1;
	UV = vec2(u, 1.0 - v);

	fable_world = world;
	VERTEX = (VIEW_MATRIX * vec4(world, 1.0)).xyz;
	NORMAL = mat3(VIEW_MATRIX) * v_normal_world;
}
