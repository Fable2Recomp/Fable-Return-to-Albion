shader_type spatial;
// MOBILE LITE: ../../standard_material_translucent.gdshader compiled with code removed (Engine_ShaderVariant,
// `shader_variant = "lite"`); the environment cube kept: a material binding one with a specular map.
// Modern lighting model only: FABLE_LITE_MODERN also drops the bakes it multiplies by zero.
// What each define drops: standard_material_body.gdshaderinc. The render mode must match the source's.
render_mode blend_mix, depth_draw_never, cull_disabled;

#define FABLE_TRANSLUCENT
#define FABLE_LITE
#define FABLE_LITE_MODERN
#include "res://src/Engine/material/standard_material_body.gdshaderinc"
