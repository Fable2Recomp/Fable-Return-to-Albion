#[compute]
#version 450

layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict readonly buffer HistogramBuffer {
	uint bins[];
} histogram;

layout(set = 0, binding = 1, std430) restrict buffer StateBuffer {
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

layout(r32f, set = 0, binding = 3) uniform restrict writeonly image2D exposure_image;

const int BINS = 512;
const float RANGE = 4.0;
const int CURVE_SIZE = 512;
const int THREADS = 256;
const float EXPOSURE_MIN = 1.0;
const float EXPOSURE_MAX = 16.0;
const float EXPOSURE_BLEND = 0.05;

const int S_EXPOSURE = 0;
const int S_APPLIED = 1;
const int S_PREV = 2;
const int S_SM_BLACK = 3;
const int S_SM_MID = 4;
const int S_SM_WHITE = 5;
const int S_SM_P99 = 6;
const int S_RAW_BLACK = 7;
const int S_RAW_MEAN = 8;
const int S_RAW_WHITE = 9;
const int S_RAW_EXP = 10;
const int S_FADE_K = 11;
const int S_HAS_HISTORY = 12;
const int S_CURVE_VALID = 13;
const int S_THRESHOLD = 14;
const int S_EXPOSURE_FRAME = 15;
const int S_APPLIED_FRAME = 16;
const int S_HIST_MIN_PCT = 17;
const int S_HIST_MEAN = 18;
const int S_HIST_MAX_PCT = 19;
const int S_HIST_EXPOSURE_USED = 20;
const int S_HIST_TOTAL = 21;
const int S_HIST_IDX_MIN = 22;
const int S_HIST_IDX_AVG = 23;
const int S_HIST_IDX_MAX = 24;
const int S_HIST_IDX_LAST = 25;
const int S_HIST_AVG_PCT = 26;
const int S_HIST_LAST = 27;
const int S_MATERIAL_EXPOSURE = 28;
const int S_CURVE_SCALE = 29;
const int S_INV_WHITE_SQ = 30;
const int S_RAW_GAIN = 31;
const int S_CURVE = 32;
const int S_LUT = 544;
const int S_CDF = 1056;

shared float s_cdf[512];
shared float s_curve[512];
shared float s_scalar[8];

void main() {
	uint tid = gl_LocalInvocationID.x;

	if (tid == 0u) {
		float speed = clamp(params.bounds.x, 0.0, 1.0);
		float fade = (params.frame.y > 0.5 || state.v[S_HAS_HISTORY] < 0.5)
			? 1.0
			: 1.0 - pow(1.0 - speed, max(params.frame.x, 0.0) * 30.0);
		state.v[S_FADE_K] = fade;

		float exposure = state.v[S_EXPOSURE];

		if (params.frame.z > 0.5) {
			float total = 0.0;
			for (int i = 0; i < BINS; i++) {
				total += float(histogram.bins[i]);
			}

			float thr_min = params.pct.x * total;
			float thr_avg = params.pct.y * total;
			float thr_max = params.pct.z * total;
			float cum = 0.0;
			float weighted = 0.0;
			int idx_min = 0;
			int idx_avg = 0;
			int idx_max = 0;
			int idx_last = 0;
			for (int i = 0; i < BINS; i++) {
				float c = float(histogram.bins[i]);
				if (cum < thr_min) { idx_min = i; }
				if (cum < thr_avg) { idx_avg = i; }
				if (cum < thr_max) { idx_max = i; }
				if (c > 0.0) { idx_last = i; }
				cum += c;
				weighted += float(i) * c;
				s_cdf[i] = total > 0.0 ? cum / total : 0.0;
			}

			float lum_per_bin = RANGE / float(BINS);
			float inv_exp = 1.0 / max(exposure, 1e-6);
			float min_pct = float(idx_min) * lum_per_bin * inv_exp;
			float avg_pct = float(idx_avg) * lum_per_bin * inv_exp;
			float max_pct = float(idx_max) * lum_per_bin * inv_exp;
			float last = float(idx_last) * lum_per_bin * inv_exp;
			float mean = (total > 0.0 ? weighted * lum_per_bin / total : 0.5) * inv_exp;

			// THE REMASTER'S DARK LIFT IS A RAW GAIN (params.dof2.w = the cap, reserved0.x = the
			// onset: 10 outdoors, 1 with no sky). A frame whose mean sits under floor/onset is
			// treated as if it had been lit `gain` times brighter — its statistics AND the
			// curve's input scale together, capped at the lift — and the guest's floors and
			// curve are then applied unchanged. Lowering the floor instead broke the curve's
			// shape: the shoulder is set by the white bound, and the highlights clipped.
			// The exposure loop keeps the UNGAINED statistics so its histogram precision holds.
			float lift = max(params.dof2.w, 1.0);
			float onset = params.reserved0.x > 0.0 ? params.reserved0.x : 10.0;
			float gain = clamp((params.bounds.z / onset) / max(mean, 1e-6), 1.0, lift);
			float black = min(min_pct * gain, params.bounds.y);
			float mid = max(mean * gain, params.bounds.z);
			float white = max(max_pct * gain, params.bounds.w);
			state.v[S_SM_BLACK] += (black - state.v[S_SM_BLACK]) * fade;
			state.v[S_SM_MID] += (mid - state.v[S_SM_MID]) * fade;
			state.v[S_SM_WHITE] += (white - state.v[S_SM_WHITE]) * fade;
			state.v[S_SM_P99] += (max_pct * gain - state.v[S_SM_P99]) * fade;
			state.v[S_RAW_GAIN] = gain;
			state.v[S_RAW_BLACK] = min_pct;
			state.v[S_RAW_MEAN] = mean;
			state.v[S_RAW_WHITE] = max_pct;
			state.v[S_RAW_EXP] = exposure;
			state.v[S_HIST_MIN_PCT] = min_pct;
			state.v[S_HIST_MEAN] = mean;
			state.v[S_HIST_MAX_PCT] = max_pct;
			state.v[S_HIST_EXPOSURE_USED] = exposure;
			state.v[S_HIST_TOTAL] = total;
			state.v[S_HIST_IDX_MIN] = float(idx_min);
			state.v[S_HIST_IDX_AVG] = float(idx_avg);
			state.v[S_HIST_IDX_MAX] = float(idx_max);
			state.v[S_HIST_IDX_LAST] = float(idx_last);
			state.v[S_HIST_AVG_PCT] = avg_pct;
			state.v[S_HIST_LAST] = last;
		} else {
			for (int i = 0; i < BINS; i++) {
				s_cdf[i] = 0.0;
			}
		}

		// SmMid is always below SmWhite, so the avg term is the one that governs and the cut
		// lands near 0.14 of scene luminance on the shipped themes. `composite.w` trims it —
		// raising it blooms only the brighter parts of the frame.
		state.v[S_THRESHOLD] = min(
			state.v[S_SM_WHITE] * params.contrast.w,
			state.v[S_SM_MID] * params.bloom.x) * max(params.composite.w, 1e-4);

		float prev_exp = max(state.v[S_PREV], 1e-4);
		// The raw gain rides the curve's input: a bin's luminance is what the scene would have
		// held had it been lit that much brighter.
		float raw_gain = max(state.v[S_RAW_GAIN], 1.0);
		float bin_to_lum = raw_gain * RANGE / (prev_exp * float(CURVE_SIZE));
		float knee_n = params.pct.w / max(state.v[S_SM_MID] - state.v[S_SM_BLACK], 1e-4);
		float wb = max(state.v[S_SM_WHITE] - state.v[S_SM_BLACK], 1e-4);
		// The composite evaluates the curve directly from these two terms rather than sampling
		// the 512-entry table: the table spans [0, 4] and a scene-referred frame lives in its
		// first handful of bins, where entry 0 is a hard zero and the quantisation posterises
		// every dark surface.
		state.v[S_CURVE_SCALE] = raw_gain * knee_n / prev_exp;
		state.v[S_INV_WHITE_SQ] = 1.0 / (wb * wb);
		s_scalar[0] = bin_to_lum * knee_n;
		s_scalar[1] = max(state.v[S_SM_P99], 0.2) * params.contrast.z;
		s_scalar[2] = max((float(CURVE_SIZE) / RANGE) * state.v[S_SM_P99] * prev_exp, 256.0);
		s_scalar[3] = 1.0 / (wb * wb);
		s_scalar[4] = state.v[S_CURVE_VALID] > 0.5 ? fade : 1.0;
		s_scalar[5] = state.v[S_CURVE_VALID];
		s_scalar[6] = params.frame.z > 0.5 ? 1.0 : 0.0;
	}

	memoryBarrierShared();
	barrier();

	for (int pass = 0; pass < 2; pass++) {
		int i = int(tid) + pass * THREADS;
		float x = float(i) * s_scalar[0];
		float reinhard = x * (1.0 + x * s_scalar[3]) / (1.0 + x);
		float value = reinhard;
		float k1 = params.contrast.x;
		if (k1 > 0.0) {
			// `sub_8217E428`: the CDF is clamped to a band (1-c)*c*max(p99,0.2)*maxDeviation
			// around the linear ramp, so the band closes to zero at both ends of the curve.
			float cdf = state.v[S_CDF + i];
			if (s_scalar[6] > 0.5) {
				cdf += (s_cdf[i] - cdf) * s_scalar[4];
				state.v[S_CDF + i] = cdf;
			}
			float t = min(float(i) / s_scalar[2], 1.0);
			float dev = (1.0 - t) * t * s_scalar[1];
			float eq = clamp(cdf, t - dev, t + dev);
			bool is_square = abs(params.contrast.y - 2.0) < 0.003;
			float pw = is_square ? eq * eq : pow(max(eq, 0.0), params.contrast.y);
			value = reinhard + (pw - reinhard) * k1;
		}

		float old = state.v[S_CURVE + i];
		float smoothed = s_scalar[5] > 0.5 ? old + (value - old) * s_scalar[4] : value;
		s_curve[i] = smoothed;
		state.v[S_CURVE + i] = smoothed;
	}

	memoryBarrierShared();
	barrier();

	for (int pass = 0; pass < 2; pass++) {
		int i = int(tid) + pass * THREADS;
		int j = min(i, CURVE_SIZE - 2);
		float c = s_curve[j];
		float lum = ((float(i) + 0.5) / float(CURVE_SIZE)) * RANGE;
		state.v[S_LUT + i] = c / max(lum, 1e-6);
	}

	if (tid == 0u) {
		state.v[S_CURVE_VALID] = 1.0;

		float exposure = state.v[S_EXPOSURE];
		state.v[S_EXPOSURE_FRAME] = exposure;
		state.v[S_APPLIED_FRAME] = state.v[S_APPLIED];

		float prev = exposure;
		float e;
		if (params.frame.w > 0.0) {
			e = clamp(params.frame.w, EXPOSURE_MIN, EXPOSURE_MAX);
		} else {
			float m = min(state.v[S_HIST_MAX_PCT], 16.0 * state.v[S_HIST_MEAN]) *
				state.v[S_HIST_EXPOSURE_USED];
			m = max(m, 0.1);
			float r = clamp((0.75 * RANGE) / m, 0.1, 10.0);
			float target = clamp(prev * r, EXPOSURE_MIN, EXPOSURE_MAX);
			e = clamp(prev + EXPOSURE_BLEND * (target - prev), EXPOSURE_MIN, EXPOSURE_MAX);
		}

		float applied = params.bloom.w > 0.5 ? e : 1.0;
		state.v[S_PREV] = prev;
		state.v[S_EXPOSURE] = e;
		state.v[S_APPLIED] = applied;
		state.v[S_HAS_HISTORY] = 1.0;

		float material = params.composite.z > 0.5 ? applied : 1.0;
		state.v[S_MATERIAL_EXPOSURE] = material;
		imageStore(exposure_image, ivec2(0, 0), vec4(material, 0.0, 0.0, 0.0));
	}
}
