shader_type spatial;
// MOBILE LITE: ../../terrain_splat.gdshader compiled with code removed (Engine_ShaderVariant,
// `shader_variant = "lite"`): a chunk with cliffs, no layer normal maps.
// Modern lighting model only: FABLE_LITE_MODERN also drops the bakes it multiplies by zero.
// What each define drops: terrain_splat_body.gdshaderinc.
render_mode cull_disabled;

#define FABLE_LITE
#define FABLE_LITE_FLAT_TERRAIN
#define FABLE_LITE_MODERN
#include "res://src/Engine/level/terrain/terrain_splat_body.gdshaderinc"
