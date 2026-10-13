// The background landscape patch's vertex stage for the classic renderer, standing in for the
// title's VSHADER_LANDSCAPE_TESSELLATOR_VS (VS 15), whose tessellated grid Godot cannot run. The
// page mesh (Engine_EhfPages) already carries what that stage computes per vertex: the grid
// position and normal, and the atlas coordinate looked up through the U/V maps. Its outputs are
// VS 15's: TEXCOORD0 = (atlas u, atlas v, lightmap u, lightmap v), TEXCOORD1 = the guest-world
// position, TEXCOORD2 = the guest-world normal; the lightmap coordinate is
// (position.xy - landscape origin) * g_LandscapeLightmapUVFactor.xy.
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
// (origin x, origin y, factor x, factor y): the landscape's origin and g_LandscapeLightmapUVFactor.
uniform vec4 xr_background_lightmap = vec4(0.0);

void vertex()
{
	vec3 guest = (XR_GODOT_TO_GUEST_WORLD * MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 normal = normalize(mat3(XR_GODOT_TO_GUEST_WORLD) * (mat3(MODEL_MATRIX) * NORMAL));
	v_texcoord0 = vec4(UV, (guest.xy - xr_background_lightmap.xy) * xr_background_lightmap.zw);
	v_texcoord1 = vec4(guest, 1.0);
	v_texcoord2 = vec4(normal, 0.0);
}
