shader_type spatial;
// MOBILE LITE: ../standard_material.gdshader compiled with code removed (Engine_ShaderVariant,
// `shader_variant = "lite"`); the environment cube kept: a material binding one with a specular map.
// Safe under either lighting model; the bakes stay.
// What each define drops: standard_material_body.gdshaderinc. The render mode must match the source's.
render_mode blend_mix, depth_draw_opaque, cull_disabled;

#define FABLE_LITE
#include "res://src/Engine/material/standard_material_body.gdshaderinc"
