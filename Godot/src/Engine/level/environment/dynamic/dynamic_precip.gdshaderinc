uniform int particle_count = 0;
uniform vec3 box = vec3(10.0, 8.0, 10.0);
uniform vec3 box_far = vec3(36.0, 18.0, 36.0);
uniform float near_fraction = 0.35;
uniform vec3 wind = vec3(2.0, 0.0, 0.5);
uniform float fall_speed = 1.0;
uniform vec2 particle_size = vec2(0.01, 0.01);
uniform vec2 min_pixels = vec2(1.0, 1.0);
uniform vec2 max_pixels = vec2(3.0, 60.0);
uniform vec4 particle_colour : source_color = vec4(1.0, 1.0, 1.0, 0.5);
uniform vec3 fog_tint : source_color = vec3(0.72, 0.74, 0.78);
uniform float depth_fade = 0.06;
uniform float brightness = 1.0;
uniform float sway = 0.0;
uniform float wind_scale = 1.0;
uniform float size_jitter = 0.5;
uniform float speed_jitter = 0.3;

// World to the dry volume's unit box (Engine_WeatherShelter): nothing falls inside it.
#include "res://src/Engine/level/environment/fable_roofs.gdshaderinc"
uniform mat4 shelter_box = mat4(1.0);
uniform float shelter_on = 0.0;

vec3 precip_hash31(float n) {
	return fract(sin(vec3(n, n + 1.3, n + 2.7)) * vec3(43758.5453, 22578.1459, 19642.3490));
}

// THE SPELLS' PUSHES ON THE SNOW (Engine_SnowPushes): eight slots the guest's weather primitive
// holds, each a segment A -> B swept from time tA to tB, with a reach d. Learned from the title's
// snow stage; not its code. For a flake at P (guest axes) with its own random R in [0, 1)^3 and a
// lifetime, per slot: s = the clamped projection of P onto the segment, C its point, T its time,
// f = sat(1 - |C - P| / d), age a = (clock - T)(R.x + 1/2), acting while 0 < clock - T < lifetime:
//
//   pull   a^2 / (a^2 + 0.3) (C - P)                  toward the line, held
//   carry  5 a^2 / (a^2 + 0.5) (B - A) / |B - A|      along the push, held
//   swirl  0.3 sat(1 - (a - 0.9)^2)^4 (u sin t + w cos t), t = 10 (2pi R.y + a + R.x), in the
//          plane across Q = 2R - 1: u = (B - A) x Q, w = Q x u, both unit
//
// all times f, summed over the slots, every slot measured from the same P. Texels 0..15 are the
// slots' ends (xyz, w the time), 16..17 the eight 1 / d; fable_snow_push = (clock, on).
global uniform sampler2D fable_snow_pushes;
global uniform vec2 fable_snow_push;

vec3 precip_unit(vec3 v) {
	return dot(v, v) > 0.0 ? normalize(v) : vec3(0.0);
}

vec3 precip_pushed(vec3 p, vec3 r, float lifetime) {
	vec3 moved = vec3(0.0);
	vec3 q = 2.0 * r - 1.0;
	for (int i = 0; i < 8; i++) {
		vec4 a = texelFetch(fable_snow_pushes, ivec2(2 * i, 0), 0);
		vec4 b = texelFetch(fable_snow_pushes, ivec2(2 * i + 1, 0), 0);
		float inv_reach = texelFetch(fable_snow_pushes, ivec2(16 + i / 4, 0), 0)[i % 4];
		vec3 along = b.xyz - a.xyz;
		float length2 = dot(along, along);
		float t = dot(p - a.xyz, along);
		float s = t <= 0.0 ? 0.0 : (t >= length2 ? 1.0 : t / length2);
		vec3 toward = a.xyz + s * along - p;
		float f = clamp(1.0 - length(toward) * inv_reach, 0.0, 1.0);
		float since = fable_snow_push.x - mix(a.w, b.w, s);
		if (f > 0.0 && since > 0.0 && since < lifetime) {
			float age = since * (r.x + 0.5);
			float a2 = age * age;
			float bump = clamp(1.0 - (age - 0.9) * (age - 0.9), 0.0, 1.0);
			bump *= bump;
			vec3 u = precip_unit(cross(along, q));
			vec3 w = precip_unit(cross(q, u));
			float turn = 10.0 * (6.2831853 * r.y + age + r.x);
			moved += f * ((a2 / (a2 + 0.3)) * toward
				+ (5.0 * a2 / (a2 + 0.5)) * precip_unit(along)
				+ (0.3 * bump * bump) * (u * sin(turn) + w * cos(turn)));
		}
	}
	return moved;
}

vec4 precip_place(
		bool streak, float pid, vec2 corner,
		mat4 projection, mat4 view, mat4 inv_view,
		vec3 cam, vec2 viewport, float t, out vec4 col, out float bokeh) {
	vec4 clip;
	if (pid >= float(particle_count)) {
		clip = vec4(0.0, 0.0, 2.0, 1.0);
		col = vec4(0.0);
		bokeh = 0.0;
	} else {
		vec3 h = precip_hash31(pid * 1.618 + 3.0);
		vec3 h2 = precip_hash31(pid * 2.236 + 11.0);
		vec3 h3 = precip_hash31(pid * 3.142 + 29.0);

		bool near = h3.x < near_fraction;
		vec3 b = near ? box : box_far;

		vec3 vel = vec3(wind.x * wind_scale,
			-fall_speed * (1.0 - speed_jitter * 0.5 + h2.x * speed_jitter),
			wind.z * wind_scale);

		vec3 raw = h * b + vel * t;
		if (sway > 0.0) {
			raw.xz += vec2(sin(t * 1.3 + h2.y * 6.283), cos(t * 1.1 + h2.z * 6.283)) *
				sway * (0.5 + h3.y);
		}

		vec3 origin = cam - b * 0.5;
		vec3 rel = raw - origin;
		rel -= b * floor(rel / b);
		vec3 world = origin + rel;
		if (!streak && fable_snow_push.y > 0.5) {
			// Godot (x, y, z) is the guest's (-x, z, y); the map is its own inverse.
			vec3 r = precip_hash31(pid * 4.123 + 47.0);
			vec3 guest = vec3(-world.x, world.z, world.y);
			vec3 moved = precip_pushed(guest, r, 10.0 * fract(r.z * 7.31 + r.x));
			world += vec3(-moved.x, moved.z, moved.y);
		}

		vec3 to_cam = cam - world;
		float dist = length(to_cam);
		vec3 view_dir = to_cam / max(dist, 1e-3);

		float pixel_world = dist * 2.0 / (projection[1][1] * viewport.y);
		float jitter = 1.0 - size_jitter * 0.5 + h2.y * size_jitter;
		vec2 size = particle_size * jitter * (near ? 1.0 : 0.7);
		size = clamp(size, min_pixels * pixel_world, max_pixels * pixel_world);

		vec3 right;
		vec3 up;
		if (streak) {
			vec3 axis = normalize(vel);
			right = normalize(cross(view_dir, axis));
			up = axis;
		} else {
			right = normalize(inv_view[0].xyz);
			up = normalize(inv_view[1].xyz);
		}

		bokeh = 1.0 - smoothstep(0.3, 2.5, dist);
		size *= 1.0 + bokeh * 1.5;

		vec3 pos = world + right * ((corner.x - 0.5) * size.x) +
			up * ((corner.y - 0.5) * size.y);

		float far_fade = 1.0 - smoothstep(b.x * 0.36, b.x * 0.5, dist);
		float near_fade = smoothstep(0.15, 0.7, dist);
		float attenuation = exp(-dist * depth_fade);
		float per_flake = 0.55 + 0.45 * h3.z;
		float fade = far_fade * near_fade * attenuation * per_flake * (1.0 - bokeh * 0.5);

		float haze = 1.0 - exp(-dist * depth_fade * 0.8);
		vec3 rgb = mix(particle_colour.rgb, fog_tint, haze) * brightness;

		col = vec4(rgb, particle_colour.a * fade);
		clip = projection * (view * vec4(pos, 1.0));

		if (shelter_on > 0.5) {
			vec3 q = abs((shelter_box * vec4(world, 1.0)).xyz);
			if (max(q.x, max(q.y, q.z)) <= 1.0) {
				clip = vec4(0.0, 0.0, 2.0, 1.0);
				col = vec4(0.0);
			}
		}
		// Under any building's roof, as the title cuts its rain and snow.
		if (fable_under_roof(world)) {
			clip = vec4(0.0, 0.0, 2.0, 1.0);
			col = vec4(0.0);
		}
	}

	return clip;
}
