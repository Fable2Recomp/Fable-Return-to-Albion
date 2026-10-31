
shader_type spatial;
render_mode cull_disabled;

// The ground. The body lives in terrain_splat_body.gdshaderinc so that the mobile lite variants
// (this file plus defines, built by Engine_ShaderVariant) compile the same source with code removed.
#include "res://src/Engine/level/terrain/terrain_splat_body.gdshaderinc"
