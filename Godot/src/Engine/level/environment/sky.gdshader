shader_type sky;

#include "res://src/Engine/level/environment/sky_dome.gdshaderinc"

uniform sampler2D in_scatter_lut : hint_default_black, filter_linear, repeat_disable;
uniform vec3 beta_rayleigh = vec3(0.001, 0.0015, 0.004);
uniform vec3 beta_mie = vec3(0.0056, 0.0064, 0.0105);
uniform vec3 sun_engine = vec3(1.0, 0.0, 0.0);
uniform float horizon_distance = 1900.0;

void sky() {
	// EYEDIR is Godot's (x, up, z); the guest's dome works in (x, y, up).
	COLOR = fable_sky_overlaid(EYEDIR, fable_sky_inscatter(
		vec3(EYEDIR.x, EYEDIR.z, EYEDIR.y),
		sun_engine,
		in_scatter_lut,
		beta_rayleigh,
		beta_mie,
		horizon_distance));
}
