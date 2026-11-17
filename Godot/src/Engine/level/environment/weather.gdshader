shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded;

uniform vec4 wind_vec;
uniform vec4 rain_p;
uniform vec4 snow_p;
uniform vec4 area;

// World to the dry volume's unit box (Engine_WeatherShelter): nothing falls inside it.
uniform mat4 shelter_box = mat4(1.0);
uniform float shelter_on = 0.0;
// Player two's view's (split screen): the building player two stands in.
uniform mat4 shelter_box_second = mat4(1.0);
uniform float shelter_on_second = 0.0;

#include "res://src/Engine/core/views.gdshaderinc"

#include "res://src/Engine/level/environment/fable_roofs.gdshaderinc"

varying vec2 v_meta;

float wx_hash(float n) {
	return fract(sin(n) * 43758.5453123);
}

void vertex() {
	// UV2 = (index within its kind, kind): rain is kind 0, snow kind 1. The mesh holds
	// full capacity; the first rain_p.x rain and snow_p.x snow quads are live and the rest
	// collapse to a point, so the counts can follow the guest's blended theme per frame.
	float pid = UV2.x;
	bool is_rain = UV2.y < 0.5;
	bool live = pid < (is_rain ? rain_p.x : snow_p.x);
	vec2 uv = UV;

	mat3 inv_model = mat3(inverse(MODEL_MATRIX));
	vec3 eye = (inverse(MODEL_MATRIX) * vec4(CAMERA_POSITION_WORLD, 1.0)).xyz;
	vec3 cam_right = normalize(inv_model * INV_VIEW_MATRIX[0].xyz);
	vec3 cam_up = normalize(inv_model * INV_VIEW_MATRIX[1].xyz);
	float tan_half_x = 1.0 / PROJECTION_MATRIX[0][0];

	float t = TIME;
	float seed = (pid * (is_rain ? 1.6180339 : 2.2360679)) + (is_rain ? 0.0 : 71.0);
	float h1 = wx_hash(seed + 1.3);
	float h2 = wx_hash(seed + 7.7);
	float h3 = wx_hash(seed + 3.9);
	float h4 = wx_hash(seed + 9.1);

	vec3 center = vec3(0.0);
	vec3 off = vec3(0.0);
	float fade = 1.0;

	if (is_rain) {
		float R = area.x;
		float H = area.y;
		float speed = max(rain_p.z, 1.0);
		vec3 fall = vec3(wind_vec.x * 0.14, -speed, wind_vec.z * 0.14);
		float yfrac = 1.0 - fract(h2 + (t * speed / H));
		center.x = eye.x + ((h1 * 2.0) - 1.0) * R;
		center.z = eye.z + ((h3 * 2.0) - 1.0) * R;
		center.y = eye.y - (H * 0.5) + (yfrac * H);

		float len = 0.55 * max(rain_p.y, 0.15);
		float wdt = 0.016 * max(rain_p.y, 0.15);
		vec3 d = normalize(fall);
		vec3 to_cam = center - eye;
		vec3 side = cross(d, to_cam + vec3(0.003, 0.0, 0.007));
		float side_len = length(side);
		side = (side_len > 0.0001) ? side / side_len : vec3(1.0, 0.0, 0.0);
		off = (side * (uv.x - 0.5) * wdt) + (d * (uv.y - 0.5) * len);
		fade = clamp(1.35 - (length(to_cam) / R), 0.0, 1.0);
	} else {
		float R = area.z;
		float H = area.w;
		float speed = max(snow_p.z, 0.15);
		float yfrac = 1.0 - fract(h2 + (t * speed / H));
		float drift_x = wind_vec.x * t * 0.35 / max(2.0 * R, 1.0);
		float drift_z = wind_vec.z * t * 0.35 / max(2.0 * R, 1.0);
		center.x = eye.x + ((fract(h1 + drift_x) * 2.0) - 1.0) * R;
		center.z = eye.z + ((fract(h3 + drift_z) * 2.0) - 1.0) * R;
		center.y = eye.y - (H * 0.5) + (yfrac * H);
		center.x += sin((t * 0.9) + (h4 * 6.2831)) * 0.45 * snow_p.w;
		center.z += cos((t * 0.7) + (h4 * 12.566)) * 0.35 * snow_p.w;

		float s = wx_hash(seed + 5.3) * clamp(snow_p.y, 0.0, 1.0) * 0.109375 *
		          2.0 * tan_half_x;
		off = (cam_right * (uv.x - 0.5) * s) + (cam_up * (uv.y - 0.5) * s);
		vec3 to_cam = center - eye;
		fade = clamp(1.5 - (length(to_cam) / R), 0.0, 1.0);
	}

	bool second = REFII_SECOND_VIEW;
	if ((second ? shelter_on_second : shelter_on) > 0.5) {
		vec3 q = abs(((second ? shelter_box_second : shelter_box) * (MODEL_MATRIX * vec4(center, 1.0))).xyz);
		live = live && max(q.x, max(q.y, q.z)) > 1.0;
	}

	// Under any building's roof, as the title cuts its rain and snow.
	live = live && !fable_under_roof((MODEL_MATRIX * vec4(center, 1.0)).xyz);

	VERTEX = live ? center + off : vec3(0.0);
	v_meta = vec2(is_rain ? 0.0 : 1.0, live ? fade : 0.0);
}

void fragment() {
	ALBEDO = vec3(0.0);
	ALPHA = 0.0;
	bool should_discard = (v_meta.y <= 0.004);

	if (!should_discard) {
		float alpha;
		vec3 col;

		if (v_meta.x < 0.5) {
			float ax = 1.0 - abs((UV.x * 2.0) - 1.0);
			float ay = 1.0 - abs((UV.y * 2.0) - 1.0);
			alpha = pow(clamp(ax, 0.0, 1.0), 2.2) * clamp(ay, 0.0, 1.0) *
			        rain_p.w * v_meta.y;
			col = vec3(0.62, 0.68, 0.78);
		} else {
			vec2 d = (UV * 2.0) - 1.0;
			float r = length(d);
			alpha = smoothstep(1.0, 0.25, r) * 0.9 * v_meta.y;
			col = vec3(0.92, 0.94, 0.98);
		}

		if (alpha >= 0.004) {
			ALBEDO = col;
			ALPHA = alpha;
		}
	}
}
