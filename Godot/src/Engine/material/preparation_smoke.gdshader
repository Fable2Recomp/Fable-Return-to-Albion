shader_type canvas_item;

// Adjust this angle to tilt the lighting & smoke alignment (e.g. -15.0 to -25.0 to match the emblem)
uniform float tilt_angle_degrees : hint_range(-60.0, 60.0) = 15.0;

uniform vec4 blue_smoke_color : source_color = vec4(0.12, 0.40, 0.85, 1.0);
uniform vec4 blue_light_core  : source_color = vec4(0.40, 0.75, 1.00, 1.0);
uniform vec2 blue_light_pos = vec2(0.5, -0.05);
uniform float blue_light_radius : hint_range(0.2, 2.0) = 0.85;
uniform float blue_intensity : hint_range(0.5, 3.0) = 1.35;

uniform vec4 red_smoke_color  : source_color = vec4(0.85, 0.12, 0.25, 1.0);
uniform vec4 red_light_core   : source_color = vec4(1.00, 0.40, 0.45, 1.0);
uniform vec2 red_light_pos = vec2(0.5, 1.05);
uniform float red_light_radius : hint_range(0.2, 2.0) = 0.85;
uniform float red_intensity : hint_range(0.5, 3.0) = 1.35;

uniform vec4 void_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);

global uniform float fable_emission_boost = 1.0;

uniform sampler2D noise_texture : hint_default_black, repeat_enable, filter_linear;
uniform float flow_speed : hint_range(0.005, 0.15) = 0.02;
uniform float distortion_intensity : hint_range(0.0, 1.0) = 0.5;

uniform float center_clear_radius : hint_range(0.1, 0.6) = 0.30;
uniform float center_clear_softness : hint_range(0.05, 0.5) = 0.80;

vec2 rotate_2d(vec2 p, float rad) {
	float c = cos(rad);
	float s = sin(rad);
	return vec2(p.x * c - p.y * s, p.x * s + p.y * c);
}

void fragment() {
	vec2 uv = UV;
	float aspect = (SCREEN_PIXEL_SIZE.x > 0.0 && SCREEN_PIXEL_SIZE.y > 0.0)
		? (SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x)
		: 1.777778;

	// Tilt the coordinate space around the center to give the lighting a directional offset
	float angle_rad = radians(tilt_angle_degrees);
	vec2 tilted_uv = rotate_2d(uv - vec2(0.5), angle_rad) + vec2(0.5);

	vec2 base_uv = vec2(uv.x * aspect, uv.y);

	// Turbulence scrolling vectors
	vec2 flow_a = vec2(TIME * flow_speed * 0.6, TIME * flow_speed * 0.4);
	vec2 flow_b = vec2(-TIME * flow_speed * 0.35, TIME * flow_speed * 0.55);
	vec2 flow_c = vec2(TIME * flow_speed * 0.2, -TIME * flow_speed * 0.3);

	// Isotropic domain warping using orthogonal noise samples
	float warp_x = texture(noise_texture, base_uv + flow_a).r;
	float warp_y = texture(noise_texture, base_uv + flow_b).r;
	vec2 warped_uv = base_uv + vec2(warp_x - 0.5, warp_y - 0.5) * distortion_intensity;

	// Billowing smoke octaves using warped UV coordinates
	float smoke1 = texture(noise_texture, warped_uv + flow_c).r;
	float smoke2 = texture(noise_texture, (warped_uv * 1.55) - flow_a).r;
	float raw_density = (smoke1 * 0.6) + (smoke2 * 0.4);
	float smoke_density = smoothstep(0.26, 0.72, raw_density);

	// Directional Blue light gradient (calculated in tilted space)
	vec2 diff_blue = vec2((tilted_uv.x - blue_light_pos.x) * aspect * 0.65, tilted_uv.y - blue_light_pos.y);
	float dist_blue = length(diff_blue);
	float boost = max(fable_emission_boost, 1.0);
	float blue_factor = clamp(1.0 - (dist_blue / blue_light_radius), 0.0, 1.0);
	blue_factor = smoothstep(0.0, 1.0, blue_factor);
	vec3 blue_lit = mix(blue_smoke_color.rgb, blue_light_core.rgb * boost, pow(blue_factor, 1.8));

	// Directional Red light gradient (calculated in tilted space)
	vec2 diff_red = vec2((tilted_uv.x - red_light_pos.x) * aspect * 0.65, tilted_uv.y - red_light_pos.y);
	float dist_red = length(diff_red);
	float red_factor = clamp(1.0 - (dist_red / red_light_radius), 0.0, 1.0);
	red_factor = smoothstep(0.0, 1.0, red_factor);
	vec3 red_lit = mix(red_smoke_color.rgb, red_light_core.rgb * boost, pow(red_factor, 1.8));

	// Combined volumetric illumination
	vec3 smoke_illumination = (blue_lit * (blue_factor * blue_intensity)) +
	                          (red_lit * (red_factor * red_intensity));
	float total_light = max(blue_factor * blue_intensity, red_factor * red_intensity);

	// Center clearing mask to keep the medallion area dark & contrasty
	vec2 diff_center = vec2((uv.x - 0.5) * aspect, uv.y - 0.5);
	float center_dist = length(diff_center);
	float center_mask = smoothstep(center_clear_radius, center_clear_radius + center_clear_softness, center_dist);

	float final_alpha = smoke_density * clamp(total_light, 0.0, 1.0) * center_mask;
	COLOR = vec4(mix(void_color.rgb, smoke_illumination, final_alpha), 1.0);
}
