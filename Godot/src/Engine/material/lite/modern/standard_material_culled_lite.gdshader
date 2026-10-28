shader_type spatial;
// MOBILE LITE: ../../standard_material_culled.gdshader compiled with code removed (Engine_ShaderVariant,
// `shader_variant = "lite"`); no environment-cube code: for a material that binds no cube, or has no specular map to weight one.
// Modern lighting model only: FABLE_LITE_MODERN also drops the bakes it multiplies by zero.
// What each define drops: standard_material_body.gdshaderinc. The render mode must match the source's.
render_mode blend_mix, depth_draw_opaque, cull_back;

#define FABLE_SOLID
#define FABLE_LITE
#define FABLE_LITE_NO_ENVMAP
#define FABLE_LITE_MODERN
#include "res://src/Engine/material/standard_material_body.gdshaderinc"
