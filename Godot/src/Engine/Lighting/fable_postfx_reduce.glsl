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
// Flicker trap, after the CDF: the previous frame's mean and the last hit.
const int S_TRAP_PREV_MEAN = 1568;
const int S_TRAP_HIT_FRAME = 1569;
const int S_TRAP_HIT_MEAN = 1570;
const int S_TRAP_HIT_PREV = 1571;

shared float s_cdf[512];
shared float s_curve[512];
shared float s_scalar[8];

void main() {
	uint tid = gl_LocalInvocationID.x;

	if (tid == 0u) {
		bool classic = params.reserved1.z > 0.5;
		float speed = clamp(classic ? params.bounds.x : max(params.bounds.x, EXPOSURE_BLEND), 0.0, 1.0);
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
			state.v[S_SM_P99] += (max_pct - state.v[S_SM_P99]) * fade;
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

			// Flicker trap: the scene's mean luminance against the previous frame's, judged
			// by the ratio the host states in reserved1.y (0 = off). A hit records the host's
			// frame index (reserved1.x) and both means; the host polls them and writes its ring.
			float trap_ratio = params.reserved1.y;
			float prev_mean = state.v[S_TRAP_PREV_MEAN];
			if (trap_ratio > 1.0 && prev_mean > 1e-4 && total > 0.0 &&
				(mean * trap_ratio < prev_mean || mean > prev_mean * trap_ratio)) {
				state.v[S_TRAP_HIT_FRAME] = params.reserved1.x;
				state.v[S_TRAP_HIT_MEAN] = mean;
				state.v[S_TRAP_HIT_PREV] = prev_mean;
			}
			state.v[S_TRAP_PREV_MEAN] = mean;
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
		// In `sub_8217E428`, the Extended Reinhard input `x` is normalized by `knee_n = MiddleGray / (SmMid - SmBlack)`.
		// For `x * (1 + x / W_x^2) / (1 + x)` to reach 1.0 at the white point `l = wb` (where `x = wb * knee_n`),
		// the white point in curve space must be `W_x = max(wb * knee_n, 2.0)` so `1 / W_x^2 <= 0.25` is never super-linear.
		float white_x = max(wb * knee_n, 2.0);
		// THE CLASSIC CURVE IS THE TITLE'S: `sub_8217E428` takes 1 / (max - min)^2 in raw units
		// (0x8217E484 fsubs, 0x8217E518 fmuls, 0x8217E530 fdivs) with no key and no floor, so a
		// night whose stats sit on the floors (max - min ~ 1) runs near-linear.
		float inv_white_sq = params.reserved1.z > 0.5 ? 1.0 / (wb * wb) : 1.0 / (white_x * white_x);
		state.v[S_CURVE_SCALE] = raw_gain * knee_n / prev_exp;
		state.v[S_INV_WHITE_SQ] = inv_white_sq;
		s_scalar[0] = bin_to_lum * knee_n;
		float white_lum = max(state.v[S_SM_WHITE], 2.0 / max(knee_n, 1e-4));
		float eff_p99 = max(state.v[S_SM_P99], white_lum);
		// `sub_8217E428`: `maxDeviation` is stated in histogram bin units (e.g. 32 of 512 bins),
		// so dividing by `s_scalar[2]` (`p99_bins`) converts the parabolic band half-width into `[0, 1]` CDF space.
		s_scalar[2] = max((float(CURVE_SIZE) / RANGE) * eff_p99 * prev_exp, 256.0);
		s_scalar[1] = (max(eff_p99, 0.2) * params.contrast.z) / s_scalar[2];
		s_scalar[3] = inv_white_sq;
		s_scalar[4] = state.v[S_CURVE_VALID] > 0.5 ? fade : 1.0;
		s_scalar[5] = state.v[S_CURVE_VALID];
		s_scalar[6] = params.frame.z > 0.5 ? 1.0 : 0.0;
	}

	memoryBarrierShared();
	barrier();

	for (int pass = 0; pass < 2; pass++) {
		int i = int(tid) + pass * THREADS;
		float x = float(i) * s_scalar[0];
		float reinhard = params.reserved0.y > 0.5
			? clamp((x * 0.6 * (2.51 * x * 0.6 + 0.03)) / (x * 0.6 * (2.43 * x * 0.6 + 0.59) + 0.14), 0.0, 1.0)
			: x * (1.0 + x * s_scalar[3]) / (1.0 + x);
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
			// `sub_82191C18`, constant for constant. Every number below is its literal, not a
			// tuning choice:
			//
			//   min(p99, 16*mean)            0x82191C78 fsubs + 0x82191CB0 fsel, 16.0 = flt_82099410
			//   * exposure-at-capture        +0x2124, the value the histogram was normalised by
			//   max(..., 0.1)                0x82191CD4 fsel, 0.1 = flt_8209B650
			//   0.75 * RANGE / m             0x82191CF8 fsel, 0.75 = flt_8209949C
			//                                RANGE is the histogram's full scale, +0x10C = 4.0
			//   clamp to [0.1, 10]           0x82191D18 fsel, 10.0 = flt_82000A54
			//   target = clamp(prev*r, 1,16) 0x82191D3C / 0x82191D5C
			//   blend by 0.05                0x82191D64 fmadds, 0.05 = flt_820992D8
			//   clamp again to [1, 16]       0x82191D84 / 0x82191DA8
			//
			// It reads the RAW per-frame statistics, not the smoothed ones: the smoothed
			// +0x4198/419C/41A0 feed the tone curve and the bright pass, never this.
			float m = min(state.v[S_HIST_MAX_PCT], 16.0 * state.v[S_HIST_MEAN]) *
				state.v[S_HIST_EXPOSURE_USED];
			m = max(m, 0.1);
			float r = clamp((0.75 * RANGE) / m, 0.1, 10.0);
			float target = clamp(prev * r, EXPOSURE_MIN, EXPOSURE_MAX);

			// The one departure, to preserve the original's timing: the guest applies 0.05 once
			// per RENDER FRAME with no dt term (0x82191D64) at about 30 fps. The step is referred
			// to 30 Hz with the guest's own frame-rate compensation form
			// (0x82276810..0x82276890, `1 - (1 - speed)^(dt * 30)`, 30.0 = flt_820992A4): exactly
			// 0.05 at 30 fps, the same real-time response at any faster rate. Below 30 fps the
			// guest still steps once a frame, so a long frame (a hitch, a load) is ONE step, never
			// a snap to the target.
			float step_k = 1.0 - pow(1.0 - EXPOSURE_BLEND, clamp(params.frame.x * 30.0, 0.0, 1.0));
			e = clamp(prev + step_k * (target - prev), EXPOSURE_MIN, EXPOSURE_MAX);
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
