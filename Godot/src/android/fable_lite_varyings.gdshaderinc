// WHAT CROSSES FROM vertex() TO fragment() in the phone's lite surfaces (AndroidLeanVaryings.cs
// beside this file): the props' standard_material_body and the ground's terrain_splat_body. Included
// by fable_lite_lighting.gdshaderinc (the end of fable_lighting.gdshaderinc) in every lite build, so
// after the ground-AO switches fable_lite_switches defines and before either body declares a varying;
// a full build declares and writes every varying as upstream wrote it.
//
// WHY. A tile GPU stores every vertex's outputs for the binning pass and interpolates them per pixel;
// at about two triangles per pixel that storage is a large share of an Adreno 640's time (fog moved
// to the vertex stage, six more components, measured 12% slower despite ~100 fewer pixel
// instructions). Godot's own varyings follow its *_USED defines, which are set for the whole shader
// when a built-in is named in any stage, so COLOR or UV2 read only in vertex() still crosses. What
// is ours can go:
//   FABLE_LITE_TRIM_VARYINGS   (`lite_trim_varyings`) varyings no pixel reads, gone: the tree fade of
//                              a solid prop (its discard is compiled out), the ground's page rectangle
//                              outside the vista pages, the ground's position and normal in a page,
//                              its normal in a flat build with no cliff; in a bake-mode build
//                              (`lite_bake_variants`) UV2 unless amb3, the fable model's PRT transfer
//                              unless the probe's with CUSTOM0 (a constant zero). The tree fade, constant over a
//                              copy, is flat. The same picture, bit for bit.
//   FABLE_LITE_PACK_VARYINGS   (`lite_pack_varyings`, modern model) the props' scalars ride COLOR's
//                              rgb, which that model never reads (the vertex bake it holds is one of
//                              the bakes it multiplies by zero): the height above the ground, the tree
//                              fade, the vertex ground AO. COLOR crosses at Godot's half precision, the
//                              height only gates a fetch with 0.58 m to spare: the same picture.
//   FABLE_LITE_FRAGMENT_WORLD  (`lite_fragment_world`) fable_world, the world position the lights, the
//                              ground AO and the fog read, rebuilt per pixel from Godot's view-space
//                              VERTEX instead of carried: three components fewer for twelve pixel
//                              instructions. The same picture to float rounding (2/255 on a few pixels
//                              3 km from the origin).
//   FABLE_LITE_TERRAIN_VIEW_FRAME (`lite_terrain_view_frame`) the ground's model-space position and normal
//                              rebuilt per pixel from the world position and Godot's NORMAL instead of
//                              carried (six components, ~40 pixel instructions). Assumes the ground's
//                              transform is a rotation, mirror and uniform scale (the level root's
//                              Scale(-1, 1, 1)); the normal comes through Godot's half-precision
//                              interpolant: within 2/255 near the origin, 7/255 on cliff pixels 2 km out.
//   FABLE_LITE_MEDIUMP_VARYINGS (`lite_mediump_varyings`) the small-range varyings that remain (fog terms,
//                              ground AO, heights, the PRT transfer, the ground's normal) declared mediump,
//                              RelaxedPrecision on the interface: half-width storage where the GPU packs it.
//   FABLE_LITE_PROP_FRAGMENT_WORLD (`lite_prop_fragment_world`) FABLE_LITE_FRAGMENT_WORLD on the props only.
//   FABLE_LITE_PACK_LIGHTMAP_UV (`lite_pack_lightmap_uv`, modern, with PACK) a solid prop's UV2 in COLOR.gb.
// THE STEP THE LAST TWO AIM AT (AndroidLeanVaryings.cs): on an Adreno 640 a prop whose pixel reads at most
// 13-16 interpolated components ran ~7% of the frame faster than the linear instruction count says; with
// `lite_normal_maps = "fast"` these take a lean modern prop from 23 to 14 (12 with the UV2 pack).

#ifdef FABLE_LITE_MEDIUMP_VARYINGS
#define FABLE_VARY_MP mediump
#else
#define FABLE_VARY_MP
#endif

// Each body declares a varying under its FABLE_*_VARYING switch. A packed one's name is a macro for its
// slot in COLOR, so the bodies' reads and writes stay as written; a dropped one's write is guarded.

// THE PROPS' BODY (standard_lite_switches) defines FABLE_MODEL_HEIGHT before it includes fable_lighting, and
// so before this; the ground's body does not. The props-only switches below key on it.
#ifdef FABLE_MODEL_HEIGHT
#define FABLE_VARY_PROPS
#endif

// fable_world: written by vertex(), or rebuilt by fragment() and then a plain value fragment() hands
// light() (Godot forbids a fragment() write to a varying vertex() wrote). FABLE_LITE_PROP_FRAGMENT_WORLD
// (`lite_prop_fragment_world`) rebuilds it on the props only; the ground keeps carrying it.
#if defined(FABLE_LITE_FRAGMENT_WORLD) || (defined(FABLE_LITE_PROP_FRAGMENT_WORLD) && defined(FABLE_VARY_PROPS))
#define FABLE_WORLD_REBUILT
#else
#define FABLE_WORLD_VARYING
#endif

// COLOR's rgb free for the props' scalars: the modern model, where only COLOR.a (the vertex AO) is read.
#if defined(FABLE_LITE_PACK_VARYINGS) && defined(FABLE_LITE_MODERN)
#define FABLE_PACK_COLOR
#endif

// A CHARACTER'S BUILD (FABLE_SPEC_CHARACTER, `android_character_lite`, AndroidCharacterShading): drawn only
// by models with skinned parts, which state neither a tree band nor an arrival fade (the entities apply
// their draw range without one; Engine_PropFade dresses props only), so the fade is the constant 1, and
// whose meshes carry no UV2 (Engine_MdlMesh leaves skinned models out of the amb3 coordinates; Godot's
// default for the missing stream is zero), so the lightmap coordinate is the constant it already reads.
// FABLE_SPEC_GROUND_FROM_ORIGIN (`android_character_ground_ao = "origin"`): the near ground AO's per-vertex
// gate is the height above the model's origin instead of four taps of the floor map (standard_material_body's
// vertex(); the fragment is unchanged). Below the origin it is 0, so a part hanging lower (a held blade's tip)
// always takes the taps; above it, a pixel FABLE_NEAR_GROUND_REACH or more over the origin skips them, which is
// exact unless a floor within a texel of the pixel stands 0.58 m or more above the origin (a ledge beside a
// character's chest).

// THE TREE FADE (standard_material_body): none in a solid prop (no pixel reads it) or a character (none
// stated), else COLOR.g or its own.
#if defined(FABLE_SOLID) && defined(FABLE_LITE_TRIM_VARYINGS)
#define FABLE_TREE_FADE_NONE
#elif defined(FABLE_SPEC_CHARACTER)
#define FABLE_TREE_FADE_NONE
#define v_tree_fade 1.0
#elif defined(FABLE_PACK_COLOR)
#define v_tree_fade COLOR.g
#else
#define FABLE_TREE_FADE_VARYING
#endif
#ifdef FABLE_LITE_TRIM_VARYINGS
// One value per copy (its distance and its own fade): flat interpolates nothing and is exact.
#define FABLE_TREE_FADE_QUAL flat FABLE_VARY_MP
#else
#define FABLE_TREE_FADE_QUAL FABLE_VARY_MP
#endif

// THE PROPS' GROUND AO (fable_lite_switches' FABLE_NEAR_GROUND_AO / FABLE_VERTEX_GROUND_AO): the height above
// the ground in COLOR.r; or the vertex AO's (contact AO, height, vertex AO) in COLOR.rba (COLOR.g is the
// fade), the instance's AO, one value per draw, read where it is used, as fable_vertex_prop_ground_ao
// reads it; else their own varyings.
#if defined(FABLE_NEAR_GROUND_AO)
#ifdef FABLE_PACK_COLOR
#define v_prop_ground_height COLOR.r
#else
#define FABLE_GROUND_HEIGHT_VARYING
#endif
#endif
#if defined(FABLE_VERTEX_GROUND_AO)
#ifdef FABLE_PACK_COLOR
#define FABLE_GROUND_AO_IN_COLOR
#define v_prop_ground_ao vec4(COLOR.r, COLOR.b, COLOR.a, instance_baked_ao > 0.001 ? instance_baked_ao : 1.0)
#else
#define FABLE_GROUND_AO_VARYING
#endif
#endif

// The fable model's PRT transfer, which a FABLE_SPEC_NO_PRT build (another bake mode, or a mesh without
// CUSTOM0: `lite_bake_variants`) hands on as the constant zero, whose `present` test then never passes.
#if defined(FABLE_LITE_TRIM_VARYINGS) && defined(FABLE_SPEC_NO_PRT)
#define FABLE_TRANSFER_CONSTANT
#endif

// UV2 is read only by the amb3 lightmap: a build folded to another bake mode passes nothing there.
// FABLE_LITE_PACK_LIGHTMAP_UV (`lite_pack_lightmap_uv`): in a solid prop under the modern model with COLOR
// packed, whose g and b nothing else holds (no fade under trim, the height in r, the vertex AO in a; not the
// vertex ground AO, which takes b), UV2 rides COLOR.gb, written last in vertex() (FABLE_PROP_VERTEX_OUT): two
// interpolated components fewer. COLOR crosses at Godot's half precision: within 1/60 of a texel of the
// 64x64 object lightmap. (UV2 is still named, so Godot still writes its own copy; no pixel reads it.)
#if (defined(FABLE_LITE_TRIM_VARYINGS) && (defined(FABLE_SPEC_BAKE_NONE) || defined(FABLE_SPEC_BAKE_PROBE) || defined(FABLE_SPEC_BAKE_COLOR_SINGLE) || defined(FABLE_SPEC_BAKE_COLOR_DOUBLE))) || defined(FABLE_SPEC_CHARACTER)
#define FABLE_LIGHTMAP_UV vec2(0.0)
#elif defined(FABLE_LITE_PACK_LIGHTMAP_UV) && defined(FABLE_PACK_COLOR) && defined(FABLE_TREE_FADE_NONE) && defined(FABLE_VARY_PROPS) && !defined(FABLE_VERTEX_GROUND_AO)
#define FABLE_LIGHTMAP_UV COLOR.gb
#define FABLE_PROP_VERTEX_OUT COLOR.gb = UV2;
#else
#define FABLE_LIGHTMAP_UV UV2
#endif
#ifndef FABLE_PROP_VERTEX_OUT
#define FABLE_PROP_VERTEX_OUT
#endif

// THE GROUND (terrain_splat_body). Its page rectangle is read only by the vista pages.
#if !defined(FABLE_LITE_TRIM_VARYINGS) || defined(FABLE_TERRAIN_PAGE)
#define FABLE_TERRAIN_PAGE_RECT_VARYING
#endif
// Its model-space position: carried, rebuilt from VERTEX, or (a page, which never reads it) zero.
#if defined(FABLE_LITE_TERRAIN_VIEW_FRAME)
#define FABLE_TERRAIN_LOCAL_REBUILT
#elif !(defined(FABLE_LITE_TRIM_VARYINGS) && defined(FABLE_TERRAIN_PAGE))
#define FABLE_TERRAIN_LOCAL_VARYING
#endif
// Its model-space normal: as the position, and also unread in a flat build with no cliff (no slope
// weight, no cliff projection, no darkening).
#if defined(FABLE_LITE_TERRAIN_VIEW_FRAME)
#define FABLE_TERRAIN_NORMAL_REBUILT
#elif !(defined(FABLE_LITE_TRIM_VARYINGS) && (defined(FABLE_TERRAIN_PAGE) || (defined(FABLE_LITE_FLAT_TERRAIN) && defined(FABLE_LITE_NO_CLIFF))))
#define FABLE_TERRAIN_NORMAL_VARYING
#endif

// THE PIXEL STAGE'S HOOKS: what was not carried, made.
// Props: the world position from Godot's view-space one, rebuilt where the body first needs it
// (FABLE_PROP_FRAGMENT_WORLD, standard_lite_baked_ao, before the ground AO) rather than first thing, so
// it is not held through the normal map's frame, and as a 3x3 product plus the translation (no w row).
#if defined(FABLE_WORLD_REBUILT) && defined(FABLE_VARY_PROPS)
#define FABLE_PROP_FRAGMENT_WORLD fable_world = (mat3(INV_VIEW_MATRIX) * VERTEX) + INV_VIEW_MATRIX[3].xyz;
#else
#define FABLE_PROP_FRAGMENT_WORLD
#endif
// The ground: the world position from Godot's too (not from its own position through MODEL_MATRIX,
// which a fragment() rebuilds from the instance data: 28 instructions against 12); then the position and
// the normal it reads, rebuilt or, where nothing reads them, constants (locals named as the varyings they
// stand for).
#ifdef FABLE_LITE_FRAGMENT_WORLD
#define FABLE_TERRAIN_WORLD_IN fable_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
#else
#define FABLE_TERRAIN_WORLD_IN
#endif
#if defined(FABLE_TERRAIN_LOCAL_REBUILT)
#define FABLE_TERRAIN_LOCAL_IN vec3 v_local = fable_terrain_local_from_world(fable_world, MODEL_MATRIX);
#elif !defined(FABLE_TERRAIN_LOCAL_VARYING)
#define FABLE_TERRAIN_LOCAL_IN vec3 v_local = vec3(0.0);
#else
#define FABLE_TERRAIN_LOCAL_IN
#endif
#if defined(FABLE_TERRAIN_NORMAL_REBUILT)
#define FABLE_TERRAIN_NORMAL_IN vec3 v_local_normal = fable_terrain_local_normal(NORMAL, FRONT_FACING, INV_VIEW_MATRIX, MODEL_MATRIX);
#elif !defined(FABLE_TERRAIN_NORMAL_VARYING)
#define FABLE_TERRAIN_NORMAL_IN vec3 v_local_normal = vec3(0.0, 1.0, 0.0);
#else
#define FABLE_TERRAIN_NORMAL_IN
#endif
#define FABLE_TERRAIN_FRAGMENT_IN FABLE_TERRAIN_WORLD_IN FABLE_TERRAIN_LOCAL_IN FABLE_TERRAIN_NORMAL_IN

/// The ground's model-space position from its world one (FABLE_LITE_TERRAIN_VIEW_FRAME): the inverse of
/// a rotation-mirror-uniform-scale transform is its transpose over the squared scale.
vec3 fable_terrain_local_from_world(vec3 world, mat4 model) {
	vec3 d = world - model[3].xyz;
	return vec3(dot(model[0].xyz, d), dot(model[1].xyz, d), dot(model[2].xyz, d)) / dot(model[0].xyz, model[0].xyz);
}

/// The ground's model-space normal, unnormalised (the body normalises it), from Godot's view-space
/// one as Godot interpolated it: before the body's side flip and with Godot's own back-face flip
/// (DO_SIDE_CHECK, the ground being cull_disabled) undone. Godot's normal matrix is the model's
/// basis or its inverse transpose; either way the model's transpose brings it back, to scale.
vec3 fable_terrain_local_normal(vec3 view_normal, bool front_facing, mat4 inv_view, mat4 model) {
	vec3 w = mat3(inv_view) * (front_facing ? view_normal : -view_normal);
	return vec3(dot(model[0].xyz, w), dot(model[1].xyz, w), dot(model[2].xyz, w));
}
