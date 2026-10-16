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
// Clamped to the title's scene target: 7e3 (PIXEL_FORMAT_A2B10G10R10F_EDRAM, the string at
// 0x820FB2F8) holds 0..31.875 and no sign. Exposure (c20.w) is already in oC0, so this is the
// exact ceiling; qo240's ghost tint of 392 reads as a faint blue only because of it.
#ifndef XR_COLOUR_OUT
#define XR_COLOUR_OUT(c) clamp((c).rgb, vec3(0.0), vec3(31.875))
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
// The pixel stage's parameter register (Xenos param gen): xy the pixel position, signed by the
// face; zw the point sprite's coordinate in [0, 1], which only a point draw has. A composition
// that draws points defines this as POINT_COORD.
#ifndef XR_PARAM_GEN_ZW
#define XR_PARAM_GEN_ZW vec2(0.0)
#endif
// The point size a vertex stage writes, under the draw's POINTSIZE_MIN / POINTSIZE_MAX states,
// which a composition that draws points defines.
#ifndef XR_POINT_SIZE
#define XR_POINT_SIZE(s) (s)
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

// A vertex-baked placement's bake stream (Engine_ClassicVertexBakes): its block of the shared
// texture, one texel per vertex in the mesh's order, the surface's start a material uniform.
#ifdef XR_VERTEX_BAKE_FETCH
global uniform sampler2D xr_vertex_bakes;
// PINNED SLOTS. Godot numbers unhinted instance uniforms in declaration order, and the stages'
// own (the tint, the fade) come after this file; a uniform declared only under a define would
// shift theirs, and one instance mixes variants with and without it (a composite's transfer
// parts beside its plain ones), so the tint was read from the wrong slot. Explicit indices
// leave every other slot where it always was; no composition numbers past 13 on its own.
instance uniform int xr_bake_base : instance_index(15) = 0;
uniform int xr_bake_surface = 0;
vec4 xr_vertex_bake(int vertex_id) {
	int width = textureSize(xr_vertex_bakes, 0).x;
	int at = xr_bake_base + xr_bake_surface + vertex_id;
	return texelFetch(xr_vertex_bakes, ivec2(at % width, at / width), 0);
}
#endif

// A transfer placement's g_PRTConstants (Engine_ClassicPrtRows): its row of the shared texture.
#ifdef XR_PRT_ROW_FETCH
global uniform sampler2D xr_prt_constants;
instance uniform int xr_prt_row : instance_index(14) = 0;
// A composite's part reads its own row of the placement's block.
uniform int xr_prt_surface = 0;
vec4 xr_prt(int index) {
	return texelFetch(xr_prt_constants, ivec2(index, xr_prt_row + xr_prt_surface), 0);
}
#endif

// A vertex stage's register read the composition may replace (the particle family's fixed axes).
#ifndef XR_VS_CONST
#define XR_VS_CONST(reg_index, reg_value) (reg_value)
#endif

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

// A draw whose world matrix is not its placement's (the billboard capture, `sub_82A85510`: the
// model at the origin, unscaled, with the placement's own tree lanes) states them: X = (the
// placement's x axis over its squared length, c15.x phase), Y = (its y axis likewise, 1). The wind
// stays the frame's register. The composition defines both macros to its instance uniforms.
#ifndef XR_TREE_STATED_X
#define XR_TREE_STATED_X vec4(0.0)
#define XR_TREE_STATED_Y vec4(0.0)
#endif

vec4 xr_tree_constant(sampler2D registers, int index, mat4 world) {
	vec4 value = xr_const_view(registers, index, true);
	vec4 stated_x = XR_TREE_STATED_X;
	vec4 stated_y = XR_TREE_STATED_Y;
	bool stated = stated_y.w > 0.5;
	if (index == 16) {
		value.w = xr_tree_c16w;
	} else if (index == 15) {
		value.x += stated ? stated_x.w : xr_tree_seed(world[3].xyz);
	} else if (index == 17) {
		vec3 wind = xr_const_view(registers, XR_VS_HOST_WIND, true).xyz;
		if (stated) {
			value.x = dot(wind, stated_x.xyz);
			value.y = dot(wind, stated_y.xyz);
		} else {
			value.x = dot(wind, world[0].xyz) / max(dot(world[0].xyz, world[0].xyz), 1e-12);
			value.y = dot(wind, world[1].xyz) / max(dot(world[1].xyz, world[1].xyz), 1e-12);
		}
	}
	return value;
}

#include "res://src/Engine/material/xenos_helpers.gdshaderinc"

#endif
