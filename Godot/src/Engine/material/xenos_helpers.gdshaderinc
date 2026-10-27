// The pure helpers XenosRecomp's Godot dialect calls: functions, constants and fetch macros with
// no Godot built-in, uniform or render state in them, so the same text serves the spatial
// shaders (through xenos_common.gdshaderinc) and the classic post chain's compute passes
// (Engine_ClassicPostPass), which define `texture` as an explicit level 0 before including it.
#ifndef XENOS_HELPERS_INCLUDED
#define XENOS_HELPERS_INCLUDED

#ifndef FLT_MIN
#define FLT_MIN uintBitsToFloat(0xff7fffffu)
#define FLT_MAX uintBitsToFloat(0x7f7fffffu)
#endif

// THE TITLE'S CLIP SPACE. D3D's clip y points up the screen; Godot's projection carries a y flip
// (RenderSceneDataRD: `correction.set_depth_correction(flip_y)`), so its clip y points down. A
// translated vertex stage computes in the title's convention, because some derive screen
// coordinates from their own clip position (the rain's depth lookup, `0.5 * (w - y)`), and the
// position written to Godot flips back. The matrix is its own inverse.
const mat4 XR_D3D_CLIP = mat4(
	vec4(1.0, 0.0, 0.0, 0.0),
	vec4(0.0, -1.0, 0.0, 0.0),
	vec4(0.0, 0.0, 1.0, 0.0),
	vec4(0.0, 0.0, 0.0, 1.0));

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

// An indexed vertex fetch (a trail's control points): element `element` of point `index` from a
// stream the host packs one column a point, one row an element. Xenos rounds the index to nearest
// or truncates it as the fetch instruction says.
vec4 xr_stream_fetch(sampler2D stream, float index, uint element, bool rounded) {
	int at = rounded ? int(floor(index + 0.5)) : int(floor(index));
	ivec2 size = textureSize(stream, 0);
	at = clamp(at, 0, max(size.x - 1, 0));
	return texelFetch(stream, ivec2(at, int(element)), 0);
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
#define xr_tfetch1D(t, coord) texture(t, vec2((coord), 0.5))
// A 1D fetch with the instruction's texel offset (the tone map's +0.5 lands its lookup on a texel
// centre), and a 2D fetch in unnormalised texel coordinates (texCoordDenorm).
// Godot's preprocessor substitutes a parameter even after a '.', so no parameter here may share a
// name with a swizzle (a parameter `x` turned `textureSize(t, 0).x` into `.r1.x`).
#define xr_tfetch1D_offset(t, coord, offset) texture(t, vec2((coord) + (offset) / float(textureSize(t, 0).x), 0.5))
#define xr_tfetch2D_denorm(t, uv, offset) texture(t, ((uv) + (offset)) / vec2(textureSize(t, 0)))
#define xr_tfetch2D(t, uv, offset) texture(t, (uv) + (offset) / vec2(textureSize(t, 0)))
#define xr_tfetch2D_depth(t, uv, offset) texture(t, (uv) + (offset) / vec2(textureSize(t, 0)))
// The spot maps: one layer per shadowing spot (Engine_ClassicSpotShadows), the layer the light
// group's row states.
#define xr_tfetch2D_layer(t, uv, offset, layer) texture(t, vec3((uv) + (offset) / vec2(textureSize(t, 0).xy), layer))
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
