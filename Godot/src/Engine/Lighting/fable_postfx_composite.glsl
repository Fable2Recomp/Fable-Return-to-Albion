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
		// THE MODERN CURVE (params.reserved0.y): a filmic fit with a toe and a shoulder, on the
		// same key-scaled input as the original's Reinhard, so exposure, key and floors are
		// untouched and only the shape differs. 0.6 puts its middle grey where the original's is.
		float mapped = params.reserved0.y > 0.5
			? fable_filmic(x * 0.6)
			: ((x * state.v[S_INV_WHITE_SQ]) + 1.0) * x / (x + 1.0);
		float gain = l > 1e-6 ? mapped / l : 1.0;
		// AGAINST DISPLAY LUMINANCE, NOT SCENE LUMINANCE. ColourSensitivityThreshold is 0.15 —
		// a display-space number, like the 0.1/0.3/0.6 bounds and a middle grey of 0.36 — and
		// the shader keeps chroma in proportion to saturate(L/threshold). Measuring a
		// scene-referred L against it strips most of the colour out of the whole frame, since
		// a lit exterior meters around 0.02 before the exposure scalar is applied.
		float s = clamp(l * applied * params.misc.x, 0.0, 1.0);
		col = vec3(l) + s * (c - vec3(l));
		col = clamp(gain * params.sat.y * applied * col, 0.0, 1.0);
		vec3 bloom = clamp(texture(bloom_tex, uv).rgb * params.composite.x, 0.0, 1.0);
		col = col + bloom * (1.0 - col);
	} else {
		col = clamp(c * applied, 0.0, 1.0);
	}

	if (params.bits.z > 0.5) {
		float lc = dot(col, LUMINANCE_WEIGHTS);
		col = col + params.sat.x * (col - vec3(lc));
	}

	col *= params.misc.z;
	col = clamp(col, 0.0, 1.0);
	if (params.misc.y > 0.0) {
		col = pow(max(col, vec3(1e-6)), vec3(params.misc.y));
	}

	imageStore(color_image, coord, vec4(col, texel.a));
}
