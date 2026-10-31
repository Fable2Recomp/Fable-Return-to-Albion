shader_type spatial;
// MOBILE LITE: ../terrain_splat.gdshader compiled with code removed (Engine_ShaderVariant,
// `shader_variant = "lite"`): a chunk with no slope past the ground band, normal maps bound.
// Safe under either lighting model; the bakes stay.
// What each define drops: terrain_splat_body.gdshaderinc.
render_mode cull_disabled;

#define FABLE_LITE
#define FABLE_LITE_NO_CLIFF
#include "res://src/Engine/level/terrain/terrain_splat_body.gdshaderinc"
