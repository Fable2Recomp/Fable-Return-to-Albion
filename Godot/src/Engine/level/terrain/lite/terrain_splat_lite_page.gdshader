shader_type spatial;
// MOBILE LITE: ../terrain_splat.gdshader compiled with code removed (Engine_ShaderVariant,
// `shader_variant = "lite"`): a vista drawn from its colour pages.
// Safe under either lighting model; the bakes stay.
// What each define drops: terrain_splat_body.gdshaderinc.
render_mode cull_disabled;

#define FABLE_TERRAIN_PAGE
#include "res://src/Engine/level/terrain/terrain_splat_body.gdshaderinc"
