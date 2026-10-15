// The fur's prelit pass for the classic renderer: the FURMATERIAL vertex stages (0226..0228)
// skin in their own code from a bone palette Godot does not hand over, so this stands in for
// them on Godot's skinned vertex, with the outputs the title's stage gives the FURMATERIAL pixel
// stage under g_ProjectToTexture (`sub_82268C70`, VS b0 = 1): TEXCOORD0 the uv, TEXCOORD1 the
// world normal, TEXCOORD2 the world position, the vertex placed at its uv in the target
// ((u, v) -> (2u - 1, 1 - 2v), no half-texel offset: `sub_82B29818`, qword_834C38D4 = 0).
#include "res://src/Engine/material/xenos_common.gdshaderinc"
#ifndef XR_V_TEXCOORD0
#define XR_V_TEXCOORD0
varying vec4 v_texcoord0;
#endif
#ifndef XR_V_TEXCOORD1
#define XR_V_TEXCOORD1
varying vec4 v_texcoord1;
#endif
#ifndef XR_V_TEXCOORD2
#define XR_V_TEXCOORD2
varying vec4 v_texcoord2;
#endif

void vertex()
{
	mat4 xr_world = XR_GODOT_TO_GUEST_WORLD * MODEL_MATRIX * XR_FILE_TO_MODEL;
	v_texcoord0 = vec4(UV, 0.0, 0.0);
	v_texcoord1 = vec4(normalize(mat3(xr_world) * xr_file_dir(NORMAL)), 0.0);
	v_texcoord2 = vec4((xr_world * vec4(xr_file_pos(VERTEX), 1.0)).xyz, 1.0);
	POSITION = XR_D3D_CLIP * vec4(UV.x * 2.0 - 1.0, 1.0 - UV.y * 2.0, 0.5, 1.0);
}
