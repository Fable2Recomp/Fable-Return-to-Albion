#[compute]
#version 450

// The guest's exposure loop, run entirely on the GPU.
//
// NOTHING CROSSES THE BUS. `BufferGetData` flushes the rendering device whether or not the data is
// already resident, so the three adapted values, the exposure scalar and the derived curve terms
// live in a storage buffer only the GPU touches: this pass writes them, the apply pass reads them.
// The CPU supplies one number, the elapsed time.
//
// SINGLE INVOCATION, DELIBERATELY. The percentile scan is a running sum over 512 bins and is
// inherently sequential; a parallel prefix-sum would be a hundred lines to save microseconds on a
// pass that runs once per frame over 2 KB. The histogram pass ahead of it is the parallel one.

layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict readonly buffer HistogramBuffer {
	uint bins[];
} histogram;

// min, avg, max, exposure, prevExposure, curveScale, invWhiteSq, brightness
layout(set = 0, binding = 1, std430) restrict buffer StateBuffer {
	float v[];
} state;

layout(push_constant, std430) uniform Params {
	float delta_seconds;
	float first_frame;   // 1.0 while the loop has no history; forces the blend to take the frame whole
	float pad0;
	float pad1;
} params;

const int   BUCKETS    = 512;
const float RANGE      = 4.0;
const float BUCKET_W   = RANGE / float(BUCKETS);

// Every constant below was read out of the image; see Engine_AutoExposure for the addresses.
const float MIN_PCT        = 0.01;
const float MAX_PCT        = 0.99;
const float MIN_UPPER      = 0.1;    // a CEILING on the minimum, not a floor
const float AVG_LOWER      = 0.3;
const float MAX_LOWER      = 0.6;
const float EXPOSURE_SPEED = 0.01;
const float MIDDLE_GRAY    = 0.36;
const float EXPOSURE_MIN   = 1.0;
const float EXPOSURE_MAX   = 16.0;
const float EXPOSURE_BLEND = 0.05;

void main() {
	float exposure = max(state.v[3], EXPOSURE_MIN);

	uint total = 0u;
	for (int i = 0; i < BUCKETS; i++) {
		total += histogram.bins[i];
	}

	if (total == 0u) {
		return;   // nothing binned yet; hold the last state rather than collapsing it
	}

	float min_threshold = MIN_PCT * float(total);
	float max_threshold = MAX_PCT * float(total);

	// The running sum EXCLUDES the bucket being tested, matching `822aacac cmplw cr6, r10, r4`
	// followed by `822aacdc add r10, r8, r10`.
	uint  cumulative = 0u;
	float weighted   = 0.0;
	int   idx_min    = 0;
	int   idx_max    = 0;

	for (int i = 0; i < BUCKETS; i++) {
		if (float(cumulative) < min_threshold) { idx_min = i; }
		if (float(cumulative) < max_threshold) { idx_max = i; }
		uint h = histogram.bins[i];
		cumulative += h;
		weighted += float(h) * float(i);
	}

	// SCENE-REFERRED ALREADY, SO NO DIVISION BY EXPOSURE.
	//
	// The guest divides here because it meters a framebuffer already multiplied by
	// `g_ActualFramebufferBrightness` — dividing recovers the scene-referred value. Our histogram
	// bins the raw HDR colour buffer, which is scene-referred to begin with.
	//
	// The division is not merely a scale: `top` below multiplies by `exposure` to return to
	// display-referred, so dividing here cancels it, leaving `ratio` independent of the current
	// exposure. A constant ratio applied every frame has no fixed point and the scalar walks to a
	// rail. Without it, `target_exposure` settles at 3.0/max — the loop the guest runs.
	float mean         = weighted / float(total) * BUCKET_W;
	float measured_min = float(idx_min) * BUCKET_W;
	float measured_max = float(idx_max) * BUCKET_W;

	// Asymmetric, and that asymmetry is the whole point — it keeps `avg - min` and `max - min`
	// strictly positive whatever the frame holds.
	float target_min = min(measured_min, MIN_UPPER);
	float target_avg = max(mean,         AVG_LOWER);
	float target_max = max(measured_max, MAX_LOWER);

	float k = params.first_frame > 0.5
		? 1.0
		: 1.0 - pow(1.0 - EXPOSURE_SPEED, max(params.delta_seconds, 0.0) * 30.0);

	float adapted_min = state.v[0] + (target_min - state.v[0]) * k;
	float adapted_avg = state.v[1] + (target_avg - state.v[1]) * k;
	float adapted_max = state.v[2] + (target_max - state.v[2]) * k;

	// `82191c90` publishes the previous scalar BEFORE computing the new one, and the curve runs on
	// that previous value — a one-frame lag that is really in the binary.
	float previous = exposure;

	float top   = max(min(measured_max, mean * EXPOSURE_MAX) * exposure, 0.1);
	float ratio = clamp(RANGE * 0.75 / top, 0.1, 10.0);
	float target_exposure = clamp(ratio * exposure, EXPOSURE_MIN, EXPOSURE_MAX);
	float next = clamp(
		exposure + (target_exposure - exposure) * EXPOSURE_BLEND, EXPOSURE_MIN, EXPOSURE_MAX);

	float white = max(adapted_max - adapted_min, 1e-4);
	float key   = MIDDLE_GRAY / max(adapted_avg - adapted_min, 1e-4);

	state.v[0] = adapted_min;
	state.v[1] = adapted_avg;
	state.v[2] = adapted_max;
	state.v[3] = next;
	state.v[4] = previous;
	state.v[5] = key / max(previous, 1e-4);   // curve scale
	state.v[6] = 1.0 / (white * white);
	state.v[7] = next;                        // g_ActualFramebufferBrightness
}
