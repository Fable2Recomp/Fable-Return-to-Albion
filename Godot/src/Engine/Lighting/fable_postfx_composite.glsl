#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict image2D color_image;

layout(set = 0, binding = 1, std430) restrict readonly buffer StateBuffer {
	float v[];
} state;

layout(set = 0, binding = 2, std140) uniform PostFxParams {
	vec4 frame;
	vec4 bounds;
	vec4 pct;
	vec4 contrast;
	vec4 bloom;
	vec4 radial;
	vec4 dof;
	vec4 dof2;
	vec4 sat;
	vec4 misc;
	vec4 bits;
	vec4 depth;
	vec4 composite;
	vec4 reserved0;
	vec4 reserved1;
	vec4 reserved2;
} params;

layout(set = 0, binding = 3) uniform sampler2D bloom_tex;
layout(set = 0, binding = 4) uniform sampler2D dof_tex;
layout(set = 0, binding = 5) uniform sampler2D depth_tex;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	float pad0;
	float pad1;
	vec4 depth;
} push;

const int S_APPLIED_FRAME = 16;
const int S_CURVE_SCALE = 29;
const int S_INV_WHITE_SQ = 30;
const vec3 LUMINANCE_WEIGHTS = vec3(0.2125, 0.7154, 0.0721);

float linear_depth(float z) {
	float ndc_z = z;
	float view_z = push.depth.x * ndc_z + push.depth.y;
	float view_w = push.depth.z * ndc_z + push.depth.w;
	float safe_w = abs(view_w) > 1e-6 ? view_w : 1e-6;
	return -view_z / safe_w;
}

/// The ACES fitted curve: a toe that reaches black and a shoulder that rolls off.
float fable_filmic(float x) {
	return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

const float FILMIC_KNEE = 0.5;

/// The filmic curve for HDR output. Its own shoulder stops at white, leaving nothing to show
/// above it, so past the knee it becomes an exponential approach to the display's headroom
/// that meets the curve's value and slope at the knee. Below the knee it is the curve itself.
float fable_filmic_hdr(float x, float headroom) {
	if (x <= FILMIC_KNEE) {
		return fable_filmic(x);
	}
	float at = fable_filmic(FILMIC_KNEE);
	float slope = (fable_filmic(FILMIC_KNEE + 1e-3) - at) / 1e-3;
	float room = headroom - at;
	return at + room * (1.0 - exp(-(x - FILMIC_KNEE) * slope / room));
}

/// The legacy 2008 extended Reinhard curve with white point.
float fable_reinhard(float x, float inv_white_sq) {
	return ((x * inv_white_sq) + 1.0) * x / (x + 1.0);
}

const float REINHARD_KNEE = 0.5;

/// The extended Reinhard curve for HDR output. Past the knee it becomes an exponential approach
/// to the display headroom that meets the curve's value and slope at the knee, allowing outdoor
/// highlights (sun, sky, water glints) to reach display peak without crushing shadows.
float fable_reinhard_hdr(float x, float inv_white_sq, float headroom) {
	if (x <= REINHARD_KNEE) {
		return fable_reinhard(x, inv_white_sq);
	}
	float at = fable_reinhard(REINHARD_KNEE, inv_white_sq);
	float slope = (fable_reinhard(REINHARD_KNEE + 1e-3, inv_white_sq) - at) / 1e-3;
	float room = max(headroom - at, 1e-4);
	return at + room * (1.0 - exp(-(x - REINHARD_KNEE) * slope / room));
}

/// Gradually desaturates highlights above paper white (1.0) towards luminance as they approach
/// peak display brightness, creating a natural incandescent 'filament effect' without hue shift.
vec3 fable_highlight_desaturate(vec3 color, float lum, float headroom) {
	if (headroom <= 1.001 || lum <= 1.0) {
		return color;
	}
	float t = clamp((lum - 1.0) / max(headroom - 1.0, 1e-4), 0.0, 1.0);
	float desat = pow(t, 1.5) * 0.70;
	return mix(color, vec3(lum), desat);
}

/// Pulls a colour over the display ceiling towards its own luminance until the
/// brightest channel fits, so saturation desaturates gracefully to white without rotating hue.
vec3 fable_fit_ceiling(vec3 color, float ceiling) {
	float peak = max(max(color.r, color.g), color.b);
	if (peak <= ceiling) {
		return color;
	}
	float l = dot(color, LUMINANCE_WEIGHTS);
	if (peak <= l + 1e-5) {
		return vec3(ceiling);
	}
	float t = clamp((peak - ceiling) / (peak - l), 0.0, 1.0);
	return mix(color, vec3(l), t);
}

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= push.size.x || coord.y >= push.size.y) {
		return;
	}

	vec4 texel = imageLoad(color_image, coord);
	// THE EXPOSURE IS A BRIGHTNESS AFTER THE CURVE, NOT A SCALE BEFORE IT.
	//
	// The curve entry is built as `curve(L*key/prevExposure)`, so feeding it an already-exposed
	// luminance cancels that divide and rolls the shoulder off `exposure` times earlier in
	// luminance — highlights clip, mids compress, and the frame reads flat and milky. Applying
	// the scalar after the curve restores the headroom, which is what the pre-rewrite
	// `fable_exposure_apply` did as `texel.rgb * gain * state.v[7]`.
	float applied = max(state.v[S_APPLIED_FRAME], 1e-4);
	vec3 c = texel.rgb;
	vec2 uv = (vec2(coord) + 0.5) / vec2(push.size);
	// THE CEILING IS THE DISPLAY'S (params.reserved0.z): 1 in SDR, where everything below is
	// exactly the SDR path; peak / reference white with HDR output, where what SDR clips at
	// white is shown up to the display's peak instead.
	float headroom = max(params.reserved0.z, 1.0);
	bool hdr = headroom > 1.001;

	if (params.bits.y > 0.5) {
		float z = texelFetch(depth_tex, coord, 0).r;
		float d = linear_depth(z);
		float near_w = min(max((params.dof.y - d) * params.dof.x, 0.0), params.dof2.y);
		float far_w = min(max((d - params.dof.z) * params.dof.w, 0.0), params.dof2.x);
		float w = (d < params.dof.y) ? near_w : ((d > params.dof.z) ? far_w : 0.0);
		w *= 1.0 - clamp(d - 2550.0, 0.0, 1.0);
		vec3 blurred = texture(dof_tex, uv).rgb / applied;
		c = mix(c, blurred, w);
	}

	vec3 col = c;
	if (params.bits.x > 0.5) {
		float l = dot(c, LUMINANCE_WEIGHTS);
		float x = l * state.v[S_CURVE_SCALE];
		float mapped;
		if (hdr) {
			mapped = params.reserved0.y > 0.5
				? fable_filmic_hdr(x * 0.6, headroom)
				: fable_reinhard_hdr(x, state.v[S_INV_WHITE_SQ], headroom);
		} else {
			mapped = params.reserved0.y > 0.5
				? fable_filmic(x * 0.6)
				: fable_reinhard(x, state.v[S_INV_WHITE_SQ]);
		}
		float gain = l > 1e-6 ? mapped / l : 1.0;
		// AGAINST DISPLAY LUMINANCE, NOT SCENE LUMINANCE. ColourSensitivityThreshold is 0.15 —
		// a display-space number, like the 0.1/0.3/0.6 bounds and a middle grey of 0.36 — and
		// the shader keeps chroma in proportion to saturate(L/threshold). Measuring a
		// scene-referred L against it strips most of the colour out of the whole frame, since
		// a lit exterior meters around 0.02 before the exposure scalar is applied.
		float s = clamp(l * applied * params.misc.x, 0.0, 1.0);
		col = vec3(l) + s * (c - vec3(l));
		col = clamp(gain * params.sat.y * applied * col, 0.0, headroom);
		vec3 bloom = clamp(texture(bloom_tex, uv).rgb * params.composite.x, 0.0, headroom);
		col = col + bloom * max(headroom - col, 0.0) / headroom;
	} else {
		col = clamp(c * applied, 0.0, headroom);
	}

	if (params.bits.z > 0.5) {
		float lc = dot(col, LUMINANCE_WEIGHTS);
		col = col + params.sat.x * (col - vec3(lc));
	}

	col *= params.misc.z;
	if (hdr) {
		float l_post = dot(col, LUMINANCE_WEIGHTS);
		col = fable_highlight_desaturate(col, l_post, headroom);
	}
	col = fable_fit_ceiling(col, headroom);
	col = clamp(col, 0.0, headroom);
	if (params.misc.y > 0.0) {
		if (hdr) {
			vec3 sdr_part = clamp(col, 0.0, 1.0);
			vec3 sdr_curved = pow(max(sdr_part, vec3(1e-6)), vec3(params.misc.y));
			col = min(sdr_curved + max(col - 1.0, 0.0), vec3(headroom));
		} else {
			col = min(pow(max(col, vec3(1e-6)), vec3(params.misc.y)), vec3(headroom));
		}
	}

	imageStore(color_image, coord, vec4(col, texel.a));
}
