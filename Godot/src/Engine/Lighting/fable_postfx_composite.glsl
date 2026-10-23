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
// g_SaturationMaskSampler (Engine_PostFxSaturationMask): 1 where a SetSaturationMask entity drew.
layout(set = 0, binding = 6) uniform sampler2D satmask_tex;

layout(push_constant, std430) uniform Params {
	ivec2 size;
	float pad0;
	float pad1;
	vec4 depth;
} push;

const int S_PREV = 2;
const int S_SM_BLACK = 3;
const int S_SM_MID = 4;
const int S_SM_WHITE = 5;
const int S_SM_P99 = 6;
const int S_APPLIED_FRAME = 16;
const int S_CURVE_SCALE = 29;
const int S_INV_WHITE_SQ = 30;
const int S_LUT = 544;
const int S_CDF = 1056;
const int LUT_SIZE = 512;

/// The title's tone map texture T(L)/L (`sub_8217E428`, 512 x 1, built into the state by the
/// reduce pass) as its 1D fetch reads it: linear between texel centres, clamped.
float classic_tone(float t) {
	float x = clamp(t, 0.0, 1.0) * float(LUT_SIZE) - 0.5;
	int i0 = clamp(int(floor(x)), 0, LUT_SIZE - 1);
	int i1 = min(i0 + 1, LUT_SIZE - 1);
	float f = clamp(x - floor(x), 0.0, 1.0);
	return mix(state.v[S_LUT + i0], state.v[S_LUT + i1], f);
}
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

const float SDR_KNEE_Y = 0.58;
const float HDR_KNEE_Y = 0.80;

/// Unified C1 exponential highlight shoulder applied in curve output space (`curved > knee`)
/// for both SDR (`headroom == 1.0`, `knee = 0.58`) and HDR (`headroom > 1.0`, `knee = 0.80`).
/// Because it operates on the combined curve output (after blending Extended Reinhard with the
/// CDF `pw`), its knee value and unit slope (`1.0`) are invariant to white-point adaptation and
/// never invert or subtract an uncompressed curve.
float fable_shoulder(float curved, float headroom) {
	float knee = headroom > 1.001 ? HDR_KNEE_Y : SDR_KNEE_Y;
	if (curved <= knee) {
		return curved;
	}
	float room = max(headroom - knee, 1e-4);
	return knee + room * (1.0 - exp(-(curved - knee) / room));
}

/// Evaluates `sub_8217E428`'s parabolic-band-clamped CDF power curve `pw = eq^2` directly from
/// the smoothed cumulative histogram `state.v[S_CDF]` at exposed luminance `l_exp`. At `l_exp = 0`
/// (`t = 0`), `dev = 0` and `eq = 0`, so `pw(0) = 0` cleanly without dark-bin quantisation.
/// `p99` is floored by the effective white-point luminance (`white_lum`) so looking away from the
/// sun toward a uniform diffuse ground does not collapse `p99_bins` and stretch ground midtones
/// to paper white.
float fable_cdf_pw(float l_exp) {
	float norm = clamp(l_exp * params.misc.w, 0.0, 1.0);
	float pos = norm * float(LUT_SIZE - 1);
	int i0 = clamp(int(floor(pos)), 0, LUT_SIZE - 2);
	float f = pos - float(i0);
	float cdf = mix(state.v[S_CDF + i0], state.v[S_CDF + i0 + 1], f);
	float knee_n = params.pct.w / max(state.v[S_SM_MID] - state.v[S_SM_BLACK], 1e-4);
	float white_lum = max(state.v[S_SM_WHITE], 2.0 / max(knee_n, 1e-4));
	float p99 = max(state.v[S_SM_P99], white_lum);
	float p99_bins = max(float(LUT_SIZE) * params.misc.w * p99 * max(state.v[S_PREV], 1e-4), 256.0);
	float dev_scale = (max(p99, 0.2) * params.contrast.z) / p99_bins;
	float t = clamp(pos / p99_bins, 0.0, 1.0);
	float dev = (1.0 - t) * t * dev_scale;
	float eq = clamp(cdf, t - dev, t + dev);
	return abs(params.contrast.y - 2.0) < 0.003 ? eq * eq : pow(max(eq, 0.0), params.contrast.y);
}

/// Applies a piecewise-sRGB-to-gamma-2.2 shadow toe on [0, 1] (where sRGB's linear 12.92x segment
/// raised to 2.2 creates a smooth O(c^2.2) black-point toe that deepens shadow blacks without
/// shifting mid-grey or paper white), while passing HDR radiance above paper white (1.0) linearly
/// up to `headroom`.
vec3 fable_gamma_toe_hdr(vec3 col, float gamma_ratio, float headroom) {
	float g = gamma_ratio > 0.0 ? gamma_ratio : 1.0;
	vec3 sdr = clamp(col, 0.0, 1.0);
	vec3 srgb = mix(
		sdr * 12.92,
		1.055 * pow(max(sdr, vec3(0.0)), vec3(1.0 / 2.4)) - 0.055,
		step(vec3(0.0031308), sdr));
	vec3 curved = pow(max(srgb, vec3(0.0)), vec3(2.2 * g));
	return min(curved + max(col - 1.0, 0.0), vec3(headroom));
}

/// Gradually desaturates HDR highlights above paper white (1.0) towards luminance as they
/// approach `headroom`, creating a natural incandescent 'filament effect' without hue shift.
vec3 fable_highlight_desaturate(vec3 color, float lum, float headroom) {
	if (headroom <= 1.001 || lum <= 1.0) {
		return color;
	}
	float t = clamp((lum - 1.0) / max(headroom - 1.0, 1e-4), 0.0, 1.0);
	float desat = pow(t, 1.5) * 0.70;
	return mix(color, vec3(lum), desat);
}

/// Fits a colour over the display ceiling without hue rotation by blending ratio-preserving
/// chromatic scaling (`color * (ceiling / peak)`) for mild/moderate SDR highlight overages with
/// luminance-preserving desaturation (`mix(color, vec3(l), t)`) for intense emissive cores.
vec3 fable_fit_ceiling(vec3 color, float ceiling) {
	float peak = max(max(color.r, color.g), color.b);
	if (peak <= ceiling) {
		return color;
	}
	float l = dot(color, LUMINANCE_WEIGHTS);
	if (peak <= l + 1e-5) {
		return vec3(ceiling);
	}
	vec3 scaled = color * (ceiling / max(peak, 1e-6));
	float t = clamp((peak - ceiling) / (peak - l), 0.0, 1.0);
	vec3 lum_fit = mix(color, vec3(l), t);
	float excess = clamp((peak - ceiling) / max(ceiling * 1.5, 1e-6), 0.0, 1.0);
	float desat = excess * excess * 0.5;
	return mix(scaled, lum_fit, desat);
}

void main() {
	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
	if (coord.x >= push.size.x || coord.y >= push.size.y) {
		return;
	}

	vec4 texel = imageLoad(color_image, coord);
	// In the modern pipeline, the scene colour buffer is unexposed physical radiance (`[0, inf)`),
	// whereas the classic look's materials already multiplied `E` in (`c20.w = E`).
	bool classic = params.reserved1.z > 0.5;
	float applied = classic ? 1.0 : max(state.v[S_APPLIED_FRAME], 1e-4);
	vec3 c = texel.rgb;
	vec2 uv = (vec2(coord) + 0.5) / vec2(push.size);
	// THE CEILING IS THE DISPLAY'S (params.reserved0.z): 1 in SDR, peak / reference white with HDR
	// output, where highlights above paper white roll off up to the display's peak.
	float headroom = max(params.reserved0.z, 1.0);
	bool hdr = headroom > 1.001;

	if (params.bits.y > 0.5) {
		float z = texelFetch(depth_tex, coord, 0).r;
		float d = linear_depth(z);
		float near_w = min(max((params.dof.y - d) * params.dof.x, 0.0), params.dof2.y);
		float far_w = min(max((d - params.dof.z) * params.dof.w, 0.0), params.dof2.x);
		float w = (d < params.dof.y) ? near_w : ((d > params.dof.z) ? far_w : 0.0);
		w *= 1.0 - clamp(d - 2550.0, 0.0, 1.0);
		vec4 dof = texture(dof_tex, uv);
		vec3 blurred = dof.a > 1e-5 ? dof.rgb / (dof.a * applied) : c;
		c = mix(c, blurred, w);
	}

	// THE MASKED PAIR IS A DELTA (shader 0505): m = mask >= 0.5, then Brightness + 1 + m x
	// BrightnessMasked and Saturation + m x SaturationMasked. The Cursed Snowglobe states -1 and
	// +1: the world grey, its masked Shadows, hero and weapons in their own colour.
	float m = (params.reserved1.w > 0.5 && texelFetch(satmask_tex, coord, 0).r >= 0.5) ? 1.0 : 0.0;
	float brightness = params.sat.y + (m * params.sat.w);
	float saturation = params.sat.x + (m * params.sat.z);

	vec3 col = c;
	if (classic && params.bits.x > 0.5) {
		// PSHADER_POSTPROCESSINGMATERIAL_TONEMAP1 (shader 0500), instruction for instruction:
		// lum = dot(hdr, (.2125, .7154, .0721)); s = T(lum * c46.x) * c77.y; bloom =
		// saturate(bloom * c31.x); k = saturate(lum * c47.x); c = saturate(s * (lum + k * (hdr -
		// lum))); c = bloom + c - bloom * c; c += (c - lum(c)) * c77.x; then the gamma power.
		// c46.x = 1/range (misc.w), c47.x = 1/ColourSensitivityThreshold (misc.x), c77 =
		// (Saturation, Brightness + 1) (sat.xy), c31.x = the bloom factor (composite.x).
		float lum = dot(c, LUMINANCE_WEIGHTS);
		float s = classic_tone(lum * params.misc.w) * brightness;
		vec3 bloom = clamp(texture(bloom_tex, uv).rgb * params.composite.x, 0.0, 1.0);
		float k = clamp(lum * params.misc.x, 0.0, 1.0);
		col = clamp(s * (vec3(lum) + k * (c - vec3(lum))), 0.0, 1.0);
		col = bloom + col - bloom * col;
		float l2 = dot(col, LUMINANCE_WEIGHTS);
		col = (col - vec3(l2)) * saturation + col;
		// c78.x = GAMMA * 0.45454544 (`sub_821E5DB8`, 0x821E6404): the title writes
		// pow(col, c78.x) straight to its 8-bit back buffer. Godot sRGB-encodes this target on
		// output, so the power is decoded through sRGB here and the screen shows the power
		// exactly, toe included.
		float gamma = params.misc.y > 0.0 ? params.misc.y : 1.0;
		col = pow(clamp(col, 0.0, 1.0), vec3(0.45454544 * gamma));
		col = mix(col / 12.92, pow((col + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), col));
		imageStore(color_image, coord, vec4(clamp(col, 0.0, 1.0), texel.a));
		return;
	}
	bool fable_style = params.reserved0.w > 0.5;
	if (params.bits.x > 0.5) {
		float l = dot(c, LUMINANCE_WEIGHTS);
		float peak_c = max(max(c.r, c.g), c.b);
		// Blend luminance with peak channel (35%) when evaluating the Fable tone curve so strongly
		// colored lights (warm tavern firelight on skin, golden sunlight) compress according to their
		// dominant channel energy instead of driving Red above 1.0 while luminance is still mid-range.
		float l_tone = fable_style ? mix(l, peak_c, 0.35) : l;
		// `S_CURVE_SCALE` in `fable_postfx_reduce.glsl` is `raw_gain * knee_n / prev_exp` because
		// the 512-entry histogram/CDF table is indexed by EXPOSED luminance `l_exp = l * applied`.
		// Evaluating `x = l_tone_exp * S_CURVE_SCALE` matches `sub_8217E428`'s bin coordinate `i * s_scalar[0]`,
		// and blending `base_r` with `fable_cdf_pw(l_tone_exp)` BEFORE the highlight shoulder preserves
		// Fable 2's CDF local contrast and midtone lift without ever subtracting an uncompressed curve.
		float l_exp = l * applied;
		float l_tone_exp = l_tone * applied;
		float x = l_tone_exp * state.v[S_CURVE_SCALE];
		float mapped;
		if (params.reserved0.y > 0.5) {
			mapped = hdr ? fable_filmic_hdr(x * 0.6, headroom) : fable_filmic(x * 0.6);
		} else {
			float base_r = fable_reinhard(x, state.v[S_INV_WHITE_SQ]);
			// Matches `0x8217e734`: CDF contrast weight tapers to 0 as `base_r` approaches 1.0.
			float cdf_w = (fable_style && params.contrast.x > 0.0)
				? params.contrast.x * clamp(1.0 - base_r, 0.0, 1.0)
				: 0.0;
			float curved = cdf_w > 0.0 ? mix(base_r, fable_cdf_pw(l_tone_exp), cdf_w) : base_r;
			mapped = fable_shoulder(curved, headroom);
		}
		float gain = l_tone > 1e-6 ? mapped / l_tone : applied * state.v[S_CURVE_SCALE];
		// AGAINST DISPLAY LUMINANCE, NOT SCENE LUMINANCE. ColourSensitivityThreshold is 0.15 —
		// a display-space number, like the 0.1/0.3/0.6 bounds and a middle grey of 0.36 — and
		// the shader keeps chroma in proportion to saturate(L/threshold). Measuring a
		// scene-referred L against it strips most of the colour out of the whole frame, since
		// a lit exterior meters around 0.02 before the exposure scalar is applied.
		float s = clamp(l_exp * params.misc.x, 0.0, 1.0);
		col = vec3(l) + s * (c - vec3(l));
		col = fable_fit_ceiling(max(gain * brightness * col, vec3(0.0)), headroom);
		col = clamp(col, 0.0, headroom);
		vec3 bloom = clamp(texture(bloom_tex, uv).rgb * params.composite.x, 0.0, headroom);
		col = col + bloom * max(headroom - col, 0.0) / headroom;
	} else {
		col = clamp(c * applied, 0.0, headroom);
	}

	if (params.bits.z > 0.5) {
		float lc = dot(col, LUMINANCE_WEIGHTS);
		col = col + saturation * (col - vec3(lc));
	}

	col *= params.misc.z;
	if (hdr) {
		float l_post = dot(col, LUMINANCE_WEIGHTS);
		col = fable_highlight_desaturate(col, l_post, headroom);
	}
	col = fable_fit_ceiling(col, headroom);
	col = clamp(col, 0.0, headroom);
	if (fable_style) {
		col = fable_gamma_toe_hdr(col, params.misc.y, headroom);
	} else if (params.misc.y > 0.0) {
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
