shader_type spatial;
// MOBILE LITE: ../standard_material_translucent.gdshader compiled with code removed (Engine_ShaderVariant,
// `shader_variant = "lite"`); no environment-cube code: for a material that binds no cube, or has no specular map to weight one.
// Safe under either lighting model; the bakes stay.
// What each define drops: standard_material_body.gdshaderinc. The render mode must match the source's.
render_mode blend_premul_alpha, depth_draw_never, cull_back;

#define FABLE_TRANSLUCENT
#define FABLE_LITE
#define FABLE_LITE_NO_ENVMAP
#include "res://src/Engine/material/standard_material_body.gdshaderinc"
