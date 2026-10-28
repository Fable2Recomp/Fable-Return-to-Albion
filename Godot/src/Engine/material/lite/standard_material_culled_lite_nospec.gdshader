shader_type spatial;
// MOBILE LITE: ../standard_material_culled.gdshader compiled with code removed (Engine_ShaderVariant,
// `shader_variant = "lite"`); no specular path at all: a material whose specular colour is provably zero.
// Safe under either lighting model; the bakes stay.
// What each define drops: standard_material_body.gdshaderinc. The render mode must match the source's.
render_mode blend_mix, depth_draw_opaque, cull_back;

#define FABLE_SOLID
#define FABLE_LITE_NO_SPEC
#include "res://src/Engine/material/standard_material_body.gdshaderinc"
