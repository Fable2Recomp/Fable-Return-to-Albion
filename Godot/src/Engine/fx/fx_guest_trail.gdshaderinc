// THE GUEST'S PARTICLE TRAILS, MODERN: the strips the guest's trail and double-trail records state
// (Engine_ClassicParticles.Trails' shared feed), placed by our own vertex stage; the pixel side and
// the lighting are fx_guest_particle_pixel's. Learned from the title's trail stages; not its code.
// #define FX_TRAIL_DOUBLE for a double trail.
//
// THE STRIP. A triangle strip of 2((n - 1)s + 1) vertices carrying nothing but their index; the
// control points are a texture, column = point, row 0 = the first stream, row 1 = the second.
// Vertex i is pair j = i / 2 on side i mod 2. With p = (j - 1) / s, segment g = trunc(p) and
// t = (j - s g) / s, the strip runs through a Catmull-Rom over points floor(p), g + 1, g + 2, g + 3
// (indices clamped to the stream), through the group's transform:
//
//   C(t) = ((2 P1) + (P2 - P0) t + (2 P0 - 5 P1 + 4 P2 - P3) t^2 + (3 P1 - P0 - 3 P2 + P3) t^3) / 2
//
// and the second stream is read at floor(p) and g + 1, lerped by t.
//
// A TRAIL. Row 1 is (key time, half width). The tangent T is the sum of the unit steps from
// C(t - 0.01) to C(t) and from C(t) to C(t + 0.01); the side S = T x Z, Z the camera's (or the
// trail's fixed) axis. A vertex stands at C(t) +- width S (+ on side 0), its normal T x S.
//
// A DOUBLE TRAIL. Row 1 is a second edge curve and the key time. Side 0 stands on the first edge,
// side 1 on the second, each drawn from the middle by the key's scale. The normal is the unit
// vector from the second edge to the first, crossed with the first edge's unit step from t - 0.01
// to t, and is left at the length that gives (the game does not normalise it).
//
// The key time picks the keyframes (colour; self-illumination, texture time, scale); the fades and
// alpha are the quads' (fx_guest_particle), by the vertex's own distance; UV = (side, j / ((n-1)s)).

#include "res://src/Engine/fx/fx_guest_particle_pixel.gdshaderinc"

uniform sampler2D xr_vertex_stream : filter_nearest, repeat_disable;
uniform vec4 vs_g_MatWorld[3];
uniform vec4 vs_g_KeyFrameColors[20];
// (self-illumination, texture time, the double trail's scale) per key.
uniform vec4 vs_g_KeyFrameSelfIllumFactorStartTimesScale[20];
// (s, 1 / ((n - 1) s), 1 / s).
uniform vec4 vs_g_ParticleTrailProperties;
uniform vec4 vs_g_FadeInDistanceInfo;
uniform vec4 vs_g_FadeOutDistanceInfo;
// The single trail's facing axis, guest space.
uniform vec4 vs_g_CameraZVector;

vec4 trail_point(float index, int row) {
	int last = max(textureSize(xr_vertex_stream, 0).x - 1, 0);
	return texelFetch(xr_vertex_stream, ivec2(clamp(int(floor(index)), 0, last), row), 0);
}

vec3 trail_curve(vec3 p0, vec3 p1, vec3 p2, vec3 p3, float t) {
	vec3 local = 0.5 * ((2.0 * p1) + (p2 - p0) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * (t * t)
		+ (3.0 * p1 - p0 - 3.0 * p2 + p3) * (t * t * t));
	vec4 l = vec4(local, 1.0);
	return vec3(dot(vs_g_MatWorld[0], l), dot(vs_g_MatWorld[1], l), dot(vs_g_MatWorld[2], l));
}

vec3 trail_unit(vec3 v) {
	return dot(v, v) > 0.0 ? normalize(v) : vec3(0.0);
}

void vertex() {
	float j = float(VERTEX_ID / 2);
	float side = float(VERTEX_ID % 2);
	float s = vs_g_ParticleTrailProperties.x;
	float inv_s = vs_g_ParticleTrailProperties.z;
	float p = (j - 1.0) * inv_s;
	float g = trunc(p);
	float t = (j - s * g) * inv_s;

	vec4 q0 = trail_point(p, 1);
	vec4 q1 = trail_point(g + 1.0, 1);

#ifdef FX_TRAIL_DOUBLE
	vec3 a0 = trail_point(p, 0).xyz, a1 = trail_point(g + 1.0, 0).xyz;
	vec3 a2 = trail_point(g + 2.0, 0).xyz, a3 = trail_point(g + 3.0, 0).xyz;
	vec3 b0 = q0.xyz, b1 = q1.xyz;
	vec3 b2 = trail_point(g + 2.0, 1).xyz, b3 = trail_point(g + 3.0, 1).xyz;
	vec3 edge_a = trail_curve(a0, a1, a2, a3, t);
	vec3 edge_b = trail_curve(b0, b1, b2, b3, t);
	float key = mix(q0.w, q1.w, t);
#else
	vec3 p0 = trail_point(p, 0).xyz, p1 = trail_point(g + 1.0, 0).xyz;
	vec3 p2 = trail_point(g + 2.0, 0).xyz, p3 = trail_point(g + 3.0, 0).xyz;
	vec3 at = trail_curve(p0, p1, p2, p3, t);
	float key = mix(q0.x, q1.x, t);
#endif

	float k = max(key, 0.0);
	int k0 = min(int(k), 19);
	int k1 = min(k0 + 1, 19);
	float f = fract(k);
	vec4 colour = mix(vs_g_KeyFrameColors[k0], vs_g_KeyFrameColors[k1], f);
	vec4 keyed = mix(vs_g_KeyFrameSelfIllumFactorStartTimesScale[k0], vs_g_KeyFrameSelfIllumFactorStartTimesScale[k1], f);

#ifdef FX_TRAIL_DOUBLE
	vec3 middle = 0.5 * (edge_a + edge_b);
	vec3 guest = middle + ((side > 0.5 ? edge_b : edge_a) - middle) * keyed.z;
	vec3 back = trail_curve(a0, a1, a2, a3, t - 0.01);
	vec3 normal = cross(trail_unit(edge_a - edge_b), trail_unit(edge_a - back));
#else
	vec3 tangent = trail_unit(at - trail_curve(p0, p1, p2, p3, t - 0.01))
		+ trail_unit(trail_curve(p0, p1, p2, p3, t + 0.01) - at);
	vec3 across = cross(tangent, vs_g_CameraZVector.xyz);
	float width = mix(q0.y, q1.y, t);
	vec3 guest = at + across * (side > 0.5 ? -width : width);
	vec3 normal = trail_unit(cross(tangent, across));
#endif

	vec3 world = particle_swap(guest);
	vec3 eye = INV_VIEW_MATRIX[3].xyz;
	float d = distance(eye, world);
	float fade_in = clamp((d - vs_g_FadeInDistanceInfo.x) * vs_g_FadeInDistanceInfo.z, 0.0, 1.0);
	float fade_out = clamp((vs_g_FadeOutDistanceInfo.x - d) * vs_g_FadeOutDistanceInfo.y, 0.0, 1.0);

	v_normal_world = particle_swap(normal);
	v_key_colour = colour.rgb;
	v_alpha = colour.a * fade_in * fade_out * vs_g_FadeOutDistanceInfo.z;
	v_self_illum = keyed.x;
	v_texture_time = keyed.y;
	v_frames = vec4(0.0);
	UV = vec2(side, j * vs_g_ParticleTrailProperties.y);

	fable_world = world;
	VERTEX = (VIEW_MATRIX * vec4(world, 1.0)).xyz;
	NORMAL = mat3(VIEW_MATRIX) * v_normal_world;
}
