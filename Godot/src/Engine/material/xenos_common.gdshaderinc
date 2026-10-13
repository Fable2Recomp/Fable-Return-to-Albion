// The Xenos support XenosRecomp's Godot dialect emits calls to — one copy, shared by every
// translated shader. The HLSL side of the same contract is XenosRecomp/shader_common.h; a helper
// here has the same name with an `xr_` prefix and the same meaning.
//
// WHAT IS SHARED BETWEEN THE TWO STAGES lives here as well: the varying guards, the boolean word,
// the alpha-test state and the position switch, because a Godot shader is one file holding both a
// vertex() and a fragment() and each stage's include may not declare these twice.
#ifndef XENOS_COMMON_INCLUDED
#define XENOS_COMMON_INCLUDED

#define FLT_MIN uintBitsToFloat(0xff7fffffu)
#define FLT_MAX uintBitsToFloat(0x7f7fffffu)

// Bit 1 of the guest's spec-constant word: alpha test on (SPEC_CONSTANT_ALPHA_TEST).
#define XR_SPEC_CONSTANT_ALPHA_TEST 2u
// The render layer the classic shadow cameras carry in their cull mask and nothing else does:
// a pixel shader reads CAMERA_VISIBLE_LAYERS against it to know it is drawing a shadow map.
#define XR_SHADOW_PASS_LAYER (1u << 23u)

// The guest's 32 boolean constants in one word: vertex b0..15 in the low half, pixel b0..15 at
// bit 16 up. The emitted `#define ps_g_Name (1u << Nu)` masks index into it.
uniform uint xr_booleans = 0u;
uniform uint xr_spec_constants = 0u;
uniform float xr_alpha_threshold = 0.0;
// Bit N set: TEXCOORDN arrives yx-swapped (Xenos vertex format quirk the guest states per draw).
uniform uint xr_swapped_texcoords = 0u;
// True: the guest's own clip-space position (its matrices, fed as uniforms) places the vertex.
// False: Godot's camera does, which is the default until the matrix constants are fed. Only
// shaders whose matrices are not aliased below consult it.
uniform bool xr_use_guest_position = false;
// The alpha a composed material writes. A composition on the title's ONE/ONE blend defines it
// as 0.0 over blend_premul_alpha, which adds the colour and leaves the destination whole.
#ifndef XR_ALPHA_OUT
#define XR_ALPHA_OUT(a) (a)
#endif
// The colour a composed material writes. A composition on the title's SRCALPHA/ZERO blend
// (the terrain's base layer: colour x weight replacing what was there) premultiplies here.
#ifndef XR_COLOUR_OUT
#define XR_COLOUR_OUT(c) (c).rgb
#endif
// A composition's own test on the finished colour before the title's alpha test and the
// output (a second water pass discards what the first pass covered).
#ifndef XR_BEFORE_OUTPUT
#define XR_BEFORE_OUTPUT(c)
#endif
// The clip position a composed vertex stage writes. A composition that draws only part of a
// mesh (the terrain's per-quadrant layer gate) moves the rest outside the clip volume.
// The alpha write. Godot draws any surface whose shader writes ALPHA in the blended alpha
// pass, whatever its render mode, so an opaque composition (Engine_ClassicShaders) defines
// this empty and stays in the opaque pass with a depth prepass.
#ifndef XR_WRITE_ALPHA
#define XR_WRITE_ALPHA(a) ALPHA = (a)
#endif
#ifndef XR_POSITION_OUT
#define XR_POSITION_OUT(p) (p)
#endif

// THE FRAME'S CONSTANT FILE, one texel per register: the 256 pixel and 256 vertex float4
// registers the title's render passes would have set, laid out as the device holds them. The
// title writes its frame constants at fixed registers (Engine_ClassicRegisters names them), so
// everything a shader reads that is neither one of its material's own constants nor an object
// matrix comes from here, by absolute register.
//
// ONE ROW PER LIGHT SET. The frame constants are the same for every draw, but the light block
// (c80..c115 pixel, c76..c107 vertex) is filled per draw with the lights the title assigned to
// that draw, so each instance names its row: row 0 is the frame's own block, and
// Engine_ClassicConstants hands out the rows above it.
// A draw lit by several groups is several passes, and a pass is its own material on the same
// instance; instance uniforms belong to the instance, so each pass reads its own one. The
// composer names the pass's uniform by defining XR_ROW_UNIFORM before this include.
global uniform sampler2D xr_ps_constants;
global uniform sampler2D xr_vs_constants;
instance uniform int xr_light_row = 0;
instance uniform int xr_light_row2 = 0;
instance uniform int xr_light_row3 = 0;
#ifndef XR_ROW_UNIFORM
#define XR_ROW_UNIFORM xr_light_row
#endif

// THE SCENE EXPOSURE. Every world material ends `oC0 *= c20.w` (VS c54.w): the title's
// adaptive pre-exposure E (`sub_82191C18`, [1, 16], once per main-view frame), which the post
// chain's reduce pass computes on the GPU from the frame's own histogram and publishes as
// `fable_exposure_tex`. The feed writes this marker into those lanes and the fetch takes E.
global uniform sampler2D fable_exposure_tex;
const float XR_EXPOSURE_LANE = -12345.0;

//
// THE MAIN VIEW ONLY. The frame setup (`sub_82282320`, 0x8228268C..0x822826CC) loads E into the
// lane only when the view is the main pass (view+0x6FC == 2, +0x706 and +0x707 clear) and 1.0
// otherwise, so the water's mirror is rendered unexposed and the water exposes it once. The main
// camera sees layer 1; a mirror camera sees only the reflected layer and a cascade only its own,
// which is how a stage tells them apart (CAMERA_VISIBLE_LAYERS is readable only in the stage
// bodies, so `xr_const` is a macro over `xr_const_view`).
vec4 xr_const_view(sampler2D registers, int index, bool main_view) {
	vec4 value = texelFetch(registers, ivec2(index, XR_ROW_UNIFORM), 0);
	if (value.w == XR_EXPOSURE_LANE) {
		value.w = main_view ? texelFetch(fable_exposure_tex, ivec2(0, 0), 0).r : 1.0;
	}
	return value;
}

#define xr_const(registers, index) xr_const_view(registers, index, (CAMERA_VISIBLE_LAYERS & 1u) != 0u)

// g_TreeConstants (VS c15..c17) as `sub_82222CE8` writes them per tree. Two lanes are the
// tree's own: c15.x = R+0x250, the tree's phase, seeded once from its creator position and
// advanced by the clock (`sub_822DAB20`), so it is the seed plus the clock the frame carries in
// c15.x; c17.xy = the wind W+0xD38 dotted with R+0x180 and R+0x190, the rows of the tree's
// world-to-local matrix (`sub_8220B560`: the transposed axes over their squared lengths). The
// guest-world wind is the host's register XR_VS_HOST_WIND.
const int XR_VS_HOST_WIND = 200;

uint xr_ror13(uint v) {
	return (v >> 13u) | (v << 19u);
}

// `sub_82A708E0` (0x82A70BB4..0x82A70C2C): the position's x, y, z bit patterns hashed, then
// `LHMath::GFFloatRandom(10, &seed)`: fmod(unsigned ror13(seed * 9377 + 9439), 655360) / 65536.
float xr_tree_seed(vec3 position) {
	uint h = xr_ror13(floatBitsToUint(position.x) * 0x24A1u + 0x24DFu) + floatBitsToUint(position.y);
	uint seed = xr_ror13(h * 0x24A1u + 0x24DFu) + floatBitsToUint(position.z);
	seed = xr_ror13(seed * 9377u + 9439u);
	return float(seed % 655360u) / 65536.0;
}

// c16.w per material (`sub_82222CE8` 0x82222E18..E38): 0.035 when the texture set names its trunk
// slot (record+24, mask bit 0x20 — the canopy, drawn without TRUNK1), else -1.
uniform float xr_tree_c16w = -1.0;

vec4 xr_tree_constant(sampler2D registers, int index, mat4 world) {
	vec4 value = xr_const_view(registers, index, true);
	if (index == 16) {
		value.w = xr_tree_c16w;
	} else if (index == 15) {
		value.x += xr_tree_seed(world[3].xyz);
	} else if (index == 17) {
		vec3 wind = xr_const_view(registers, XR_VS_HOST_WIND, true).xyz;
		value.x = dot(wind, world[0].xyz) / max(dot(world[0].xyz, world[0].xyz), 1e-12);
		value.y = dot(wind, world[1].xyz) / max(dot(world[1].xyz, world[1].xyz), 1e-12);
	}
	return value;
}

// THE AXES. Models are rotated Z-up to Y-up on import (x, y, z) -> (x, z, -y), and the guest's
// world point (x, y, z) is (-x, z, y) in Godot's (GameEntities.ToGodot; the level root is
// scaled (-1, 1, 1)). The shaders were written in the file's axes with constants in the guest's,
// so the vertex inputs are rotated back to the file and the object matrices are composed in the
// guest's world. Both are column-major: `mat * v`. The world map is its own inverse.
const mat4 XR_FILE_TO_MODEL = mat4(
	vec4(1.0, 0.0, 0.0, 0.0),
	vec4(0.0, 0.0, -1.0, 0.0),
	vec4(0.0, 1.0, 0.0, 0.0),
	vec4(0.0, 0.0, 0.0, 1.0));
const mat4 XR_GODOT_TO_GUEST_WORLD = mat4(
	vec4(-1.0, 0.0, 0.0, 0.0),
	vec4(0.0, 0.0, 1.0, 0.0),
	vec4(0.0, 1.0, 0.0, 0.0),
	vec4(0.0, 0.0, 0.0, 1.0));

vec3 xr_file_pos(vec3 model) {
	return vec3(model.x, -model.z, model.y);
}

vec3 xr_file_dir(vec3 model) {
	return vec3(model.x, -model.z, model.y);
}

// Row i of a column-major matrix, which is what a `dot(row, position)` in the shader wants.
vec4 xr_row(mat4 m, int i) {
	return vec4(m[0][i], m[1][i], m[2][i], m[3][i]);
}

// The two object matrices a translated vertex() builds first, because Godot's built-ins are
// only readable inside the processor function:
//   xr_wvp   = PROJECTION_MATRIX * MODELVIEW_MATRIX * XR_FILE_TO_MODEL
//              g_WorldViewProjection: file space to Godot clip space, so oPos IS Godot's;
//   xr_world = XR_GODOT_TO_GUEST_WORLD * MODEL_MATRIX * XR_FILE_TO_MODEL
//              g_WorldTransform: file space to the guest's world, where its lights are stated.

// g_WorldPositionAndReciprocalScale: the object's guest-world position and 1 / its scale.
vec4 xr_world_pos_and_rscale(mat4 world) {
	return vec4(world[3].xyz, 1.0 / max(length(world[0].xyz), 1e-6));
}

// Xenos `cube` writes a direction the following texture fetch picks up by index.
struct CubeMapData {
	vec3 dirs[2];
	int index;
};

float xr_rcp(float x) {
	return 1.0 / x;
}

vec4 xr_dst(vec4 a, vec4 b) {
	return vec4(1.0, a.y * b.y, a.z, b.w);
}

float xr_max4(vec4 v) {
	return max(max(v.x, v.y), max(v.z, v.w));
}

vec4 xr_cube(vec4 value, inout CubeMapData d) {
	int i = d.index;
	d.dirs[i] = value.xyz;
	d.index++;
	return vec4(0.0, 0.0, 0.0, float(i));
}

vec4 xr_tfetchTexcoord(uint swapped, vec4 value, uint index) {
	return (swapped & (1u << index)) != 0u ? value.yxwz : value;
}

// Texture fetches. MACROS, NOT FUNCTIONS: Godot refuses a function whose sampler parameter is
// passed textures of differing filter, repeat or hint settings, and one translated shader
// fetches its diffuse (repeat), its shadow maps (nearest, clamped) and the scene depth (hinted)
// alike; a macro inlines per call, so each fetch names its own sampler. A 1D fetch reads a 2D
// texture at v = 0.5, as the HLSL side does; the 2D offset is in texels, as the instruction
// states it.
#define xr_tfetch1D(t, x) texture(t, vec2((x), 0.5))
#define xr_tfetch2D(t, uv, offset) texture(t, (uv) + (offset) / vec2(textureSize(t, 0)))
#define xr_tfetch2D_depth(t, uv, offset) texture(t, (uv) + (offset) / vec2(textureSize(t, 0)))
// A composition whose title source was rendered unexposed while ours is the exposed scene
// scales the read back (the water's refraction tile, Engine_ClassicWater).
#ifndef XR_SCREEN_SCALE
#define XR_SCREEN_SCALE 1.0
#endif
#define xr_tfetch2D_screen(t, uv, offset) (texture(t, (uv) + (offset) / vec2(textureSize(t, 0))) * XR_SCREEN_SCALE)
// The host's planar mirror: a right-handed camera's image of the reflected world is the title's
// reflection tile mirrored in u (Engine_WaterReflection, water.gdshader read it the same way).
#define xr_tfetch2D_mirror(t, uv, offset) texture(t, xr_mirror_u((uv) + (offset) / vec2(textureSize(t, 0))))
#define xr_getWeights2D(t, uv, offset) xr_weights((uv) * vec2(textureSize(t, 0)) + (offset) - 0.5, (uv))
#define xr_tfetch3D(t, uvw) texture(t, (uvw))
#define xr_tfetchCube(t, uvw, d) texture(t, (d).dirs[int((uvw).z)])

vec2 xr_mirror_u(vec2 at) {
	return vec2(1.0 - at.x, at.y);
}

vec2 xr_weights(vec2 scaled, vec2 uv) {
	vec2 w = fract(scaled);
	return mix(w, vec2(0.0), isnan(uv));
}

// Comparisons as the 0.0 / 1.0 floats Xenos produces (HLSL converts a bool implicitly; GLSL
// does not), one overload per width.
float xr_seq(float a, float b) { return a == b ? 1.0 : 0.0; }
vec2 xr_seq(vec2 a, vec2 b) { return vec2(equal(a, b)); }
vec3 xr_seq(vec3 a, vec3 b) { return vec3(equal(a, b)); }
vec4 xr_seq(vec4 a, vec4 b) { return vec4(equal(a, b)); }
float xr_sne(float a, float b) { return a != b ? 1.0 : 0.0; }
vec2 xr_sne(vec2 a, vec2 b) { return vec2(notEqual(a, b)); }
vec3 xr_sne(vec3 a, vec3 b) { return vec3(notEqual(a, b)); }
vec4 xr_sne(vec4 a, vec4 b) { return vec4(notEqual(a, b)); }
float xr_sgt(float a, float b) { return a > b ? 1.0 : 0.0; }
vec2 xr_sgt(vec2 a, vec2 b) { return vec2(greaterThan(a, b)); }
vec3 xr_sgt(vec3 a, vec3 b) { return vec3(greaterThan(a, b)); }
vec4 xr_sgt(vec4 a, vec4 b) { return vec4(greaterThan(a, b)); }
float xr_sge(float a, float b) { return a >= b ? 1.0 : 0.0; }
vec2 xr_sge(vec2 a, vec2 b) { return vec2(greaterThanEqual(a, b)); }
vec3 xr_sge(vec3 a, vec3 b) { return vec3(greaterThanEqual(a, b)); }
vec4 xr_sge(vec4 a, vec4 b) { return vec4(greaterThanEqual(a, b)); }

// Kill tests: true when any component compares, which is the HLSL `any(a op b)`.
bool xr_any_eq(float a, float b) { return a == b; }
bool xr_any_eq(vec2 a, vec2 b) { return any(equal(a, b)); }
bool xr_any_eq(vec3 a, vec3 b) { return any(equal(a, b)); }
bool xr_any_eq(vec4 a, vec4 b) { return any(equal(a, b)); }
bool xr_any_ne(float a, float b) { return a != b; }
bool xr_any_ne(vec2 a, vec2 b) { return any(notEqual(a, b)); }
bool xr_any_ne(vec3 a, vec3 b) { return any(notEqual(a, b)); }
bool xr_any_ne(vec4 a, vec4 b) { return any(notEqual(a, b)); }
bool xr_any_gt(float a, float b) { return a > b; }
bool xr_any_gt(vec2 a, vec2 b) { return any(greaterThan(a, b)); }
bool xr_any_gt(vec3 a, vec3 b) { return any(greaterThan(a, b)); }
bool xr_any_gt(vec4 a, vec4 b) { return any(greaterThan(a, b)); }
bool xr_any_ge(float a, float b) { return a >= b; }
bool xr_any_ge(vec2 a, vec2 b) { return any(greaterThanEqual(a, b)); }
bool xr_any_ge(vec3 a, vec3 b) { return any(greaterThanEqual(a, b)); }
bool xr_any_ge(vec4 a, vec4 b) { return any(greaterThanEqual(a, b)); }

// Conditional moves: `c op 0.0 ? a : b` per component (the HLSL `select`).
float xr_cnd_eq(float c, float a, float b) { return c == 0.0 ? a : b; }
vec2 xr_cnd_eq(vec2 c, vec2 a, vec2 b) { return mix(b, a, equal(c, vec2(0.0))); }
vec3 xr_cnd_eq(vec3 c, vec3 a, vec3 b) { return mix(b, a, equal(c, vec3(0.0))); }
vec4 xr_cnd_eq(vec4 c, vec4 a, vec4 b) { return mix(b, a, equal(c, vec4(0.0))); }
float xr_cnd_ge(float c, float a, float b) { return c >= 0.0 ? a : b; }
vec2 xr_cnd_ge(vec2 c, vec2 a, vec2 b) { return mix(b, a, greaterThanEqual(c, vec2(0.0))); }
vec3 xr_cnd_ge(vec3 c, vec3 a, vec3 b) { return mix(b, a, greaterThanEqual(c, vec3(0.0))); }
vec4 xr_cnd_ge(vec4 c, vec4 a, vec4 b) { return mix(b, a, greaterThanEqual(c, vec4(0.0))); }
float xr_cnd_gt(float c, float a, float b) { return c > 0.0 ? a : b; }
vec2 xr_cnd_gt(vec2 c, vec2 a, vec2 b) { return mix(b, a, greaterThan(c, vec2(0.0))); }
vec3 xr_cnd_gt(vec3 c, vec3 a, vec3 b) { return mix(b, a, greaterThan(c, vec3(0.0))); }
vec4 xr_cnd_gt(vec4 c, vec4 a, vec4 b) { return mix(b, a, greaterThan(c, vec4(0.0))); }

#endif
