// THE TREE'S WIND for the modern surfaces: our implementation of the motion the title gives every
// shipped tree (its trunk-permutation tree stage; no shipped texture set takes the canopy one,
// Engine_ShaderFamily.Resolve), from the formula reverse-engineered out of that stage. Nothing here
// is the title's shader: the terms are named and computed our way; the numbers are the game's.
//
// Per vertex, in the file's axes (z up): weights from COLOR1 (CUSTOM1, Engine_MdlTreeStreams) —
// r = branch flexibility, g = twig flutter, b = phase offset, a = flutter rate. Per tree: a phase
// seed hashed from its world origin, and the wind's direction in its own x/y axes. Per frame: the
// clock T, the wind speed S (clamped to [0, 10]) and the integrated phase P = integral of S dt.
//
//   branch sway   k = 0.3849 sin(2pi(0.095T + 0.025P + 0.012r + 0.1b)) (0.7 + 0.25|0.3849 sin(2pi 0.1T)|) + 0.04S
//                 along  = 0.3 S r^2 k
//                 across = 0.019245 S r^2 sin(2pi(0.532T + 0.025P + 0.012r))
//   twig flutter  F = 0.36 S g (0.5 + 0.3849 sin(2pi(0.11T + b)))
//                 fx = 0.3849 F sin(2pi(a(0.4T + 0.025P + 0.23) + b))
//                 fy = 0.3849 F sin(2pi(a(0.4T + 0.025P + 0.11) + b))
//   offset        x = along Wx - across Wy + fx,  y = along Wy + across Wx + fy,
//                 z = -(0.25 along^2 + 0.5 (fx^2 + fy^2))      (the arc drops as it bends)
// where T is the clock plus the tree's seed and (Wx, Wy) the wind in the tree's x/y axes.

// The frame's wind (Engine_SceneWind): direction in the guest's axes, w = clamp(speed, 0, 10).
global uniform vec4 fable_tree_wind;
// x = the clock the trees run on, y = their integrated phase.
global uniform vec2 fable_tree_clock;

#ifdef FABLE_LITE
// `prop_sway_far_metres` (Engine_TreeSwayFar, lite builds only): past it a copy stands still. 0 is off.
global uniform float fable_tree_sway_far;

/// How much of its sway a copy keeps with its centre `distance` metres from the main camera:
/// all of it inside the last eighth of the reach, none past it, a linear ramp between so a
/// copy settles rather than stops. Exactly 1 while the reach is off.
float fable_tree_sway_keep(float distance) {
	return fable_tree_sway_far > 0.0
		? clamp((fable_tree_sway_far - distance) * 8.0 / fable_tree_sway_far, 0.0, 1.0)
		: 1.0;
}
#endif

const float FABLE_TAU = 6.2831853;

uint fable_tree_rotate13(uint v) {
	return (v >> 13u) | (v << 19u);
}

// The tree's phase seed: its guest-world origin's bit patterns hashed and reduced to [0, 10),
// as the game seeds each tree's clock.
float fable_tree_seed(vec3 guest_origin) {
	uint h = fable_tree_rotate13(floatBitsToUint(guest_origin.x) * 9377u + 9439u) + floatBitsToUint(guest_origin.y);
	h = fable_tree_rotate13(h * 9377u + 9439u) + floatBitsToUint(guest_origin.z);
	h = fable_tree_rotate13(h * 9377u + 9439u);
	return float(h % 655360u) / 65536.0;
}

// Godot (x, y, z) is the guest's (-x, z, y).
vec3 fable_tree_guest(vec3 v) {
	return vec3(-v.x, v.z, v.y);
}

/// The swayed vertex, in the model's own (Godot) axes.
vec3 fable_tree_sway(vec3 model_vertex, vec4 weights, mat4 model) {
	float speed = fable_tree_wind.w;
	if (speed <= 0.0) {
		return model_vertex;
	}
	// The wind in the tree's own file-space x and y axes (model x, and model -z), per unit of scale.
	vec3 axis_x = fable_tree_guest(model[0].xyz);
	vec3 axis_y = fable_tree_guest(-model[2].xyz);
	vec2 wind = vec2(
		dot(fable_tree_wind.xyz, axis_x) / max(dot(axis_x, axis_x), 1e-12),
		dot(fable_tree_wind.xyz, axis_y) / max(dot(axis_y, axis_y), 1e-12));
	float t = fable_tree_clock.x + fable_tree_seed(fable_tree_guest(model[3].xyz));
	float p = fable_tree_clock.y * 0.025;

	float flex = weights.r;
	float flutter = weights.g;
	float offset = weights.b;
	float rate = weights.a;

	float gust = 0.3849 * sin(FABLE_TAU * 0.1 * t);
	float swing = 0.3849 * sin(FABLE_TAU * (0.095 * t + p + 0.012 * flex + 0.1 * offset));
	float k = swing * (0.7 + 0.25 * abs(gust)) + 0.04 * speed;
	float bend = speed * flex * flex;
	float along = 0.3 * bend * k;
	float across = 0.019245 * bend * sin(FABLE_TAU * (0.532 * t + p + 0.012 * flex));

	float twig = 0.36 * speed * flutter * (0.5 + 0.3849 * sin(FABLE_TAU * (0.11 * t + offset)));
	float fx = 0.3849 * twig * sin(FABLE_TAU * (rate * (0.4 * t + p + 0.23) + offset));
	float fy = 0.3849 * twig * sin(FABLE_TAU * (rate * (0.4 * t + p + 0.11) + offset));

	vec3 shift = vec3(
		along * wind.x - across * wind.y + fx,
		along * wind.y + across * wind.x + fy,
		-(0.25 * along * along + 0.5 * (fx * fx + fy * fy)));
	// File (x, y, z) is model (x, z, -y).
	return model_vertex + vec3(shift.x, shift.z, -shift.y);
}
