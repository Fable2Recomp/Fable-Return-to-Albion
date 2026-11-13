// THE PROPS' SWITCHES in a lite build (FABLE_LITE): standard_material_body.gdshaderinc includes this
// before fable_lighting, in place of upstream's spelling of its model-space hooks (its #else).
// FABLE_MODEL_HEIGHT also tells fable_lite_varyings it is the props' body.
//
// FABLE_LITE_VERTEX_FOG: the atmosphere's two terms (transmittance, in-scatter) are computed per
// VERTEX into varyings (standard_lite_vertex_out) and only applied per pixel: no length, normalize or
// LUT fetch in the fragment stage. Fog is smooth, so the cost is a small error on long triangles
// that span the fog start or a large range.

// FABLE_LITE_WORLD_VERTEX (`lite_world_vertex`): vertex() runs in world space (world_vertex_coords),
// so Godot transforms model -> world -> view instead of concatenating model and view matrices per
// vertex (a batch copy's matrix is per instance, so that product is per vertex), and fable_world is
// VERTEX itself. The model's height and up for the grass bend and the wind come from MODEL_MATRIX:
// the same as model space for any unsheared transform. Godot then refuses a solid build its shared
// depth-only shadow material, so this is for presets that render no shadow map.
#ifdef FABLE_LITE_WORLD_VERTEX
render_mode world_vertex_coords;
#define FABLE_WORLD_VERTEX
#endif
#ifdef FABLE_WORLD_VERTEX
#define FABLE_MODEL_HEIGHT(v) (dot((v) - MODEL_MATRIX[3].xyz, MODEL_MATRIX[1].xyz) / dot(MODEL_MATRIX[1].xyz, MODEL_MATRIX[1].xyz))
#define FABLE_MODEL_UP normalize(MODEL_MATRIX[1].xyz)
#define FABLE_MODEL_NORMAL normalize(NORMAL)
#else
#define FABLE_MODEL_HEIGHT(v) (v).y
#define FABLE_MODEL_UP vec3(0.0, 1.0, 0.0)
#define FABLE_MODEL_NORMAL NORMAL
#endif

// Phone normal-map paths, `FABLE_LITE_NM_*` through `lite_normal_maps` or lite_extra_defines (off by default):
//   _DIRECT      the body's own mapped normal is written to NORMAL at the end, NORMAL_MAP is never
//                written, so Godot's second copy of the same TBN maths is not compiled and the
//                tangent frame and texel die where the body uses them. Same lighting; with a
//                shadow map, Godot's normal-offset bias then follows the mapped normal.
//   _DERIV       _DIRECT with the tangent frame rebuilt per pixel from screen derivatives of
//                VERTEX and UV: TANGENT and BINORMAL are never named, so Godot drops the tangent
//                decode and transforms in the vertex stage and the two vec3 varyings. The frame is
//                the mesh's, signs included (see the fragment); flat across each triangle.
//   _FRAME_ONLY  diagnostic: the tangent frame is still carried and read but the map is neither
//                fetched nor applied (flat, as FABLE_SPEC_NO_NORMAL_MAP), to split vertex from pixel cost.
#if defined(FABLE_LITE_NM_DERIV) || defined(FABLE_LITE_NM_FRAME_ONLY)
#ifndef FABLE_LITE_NM_DIRECT
#define FABLE_LITE_NM_DIRECT
#endif
#endif
