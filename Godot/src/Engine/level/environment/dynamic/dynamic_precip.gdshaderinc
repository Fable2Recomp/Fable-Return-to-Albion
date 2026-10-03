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

vec3 precip_hash31(float n) {
	return fract(sin(vec3(n, n + 1.3, n + 2.7)) * vec3(43758.5453, 22578.1459, 19642.3490));
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
	}

	return clip;
}
