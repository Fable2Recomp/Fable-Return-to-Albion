// Targeting highlight silhouette: one object's second draw into the half-res highlight
// buffer, the host's `PSHADER_OUTLINEMATERIAL_GLT1` (Shaders.sbk entry 1076, blob 0520).
//
// The guest's pixel shader samples the resolved scene depth, linearises both depths and
// discards where the object is more than 0.15 units behind the scene; everything else writes the
// flat `g_OutliningColour`. Here the colour is replaced by the object's SLOT (R = slot / 8), and
// G = 1 is written whether or not the pixel survived the clip — that is the silhouette the
// composite keeps its glow off, which the guest writes to stencil bit 0x20 with the z-test OFF.
//
// The scene depth is the previous frame's (the capture runs in the main viewport's PostOpaque,
// after this viewport has drawn), the same trade the additive FX layer makes.
shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled, shadows_disabled, ambient_light_disabled, specular_disabled;

global uniform sampler2D fx_scene_depth;
global uniform float fx_scene_depth_valid;
#include "res://src/Engine/core/views.gdshaderinc"

uniform float slot = 0.0;
uniform sampler2D albedo_tex : hint_default_white, filter_linear_mipmap;
uniform bool alpha_scissor_on = false;
uniform float alpha_scissor_threshold = 0.5;

/// `c255.x` in the guest's pixel shader: how far behind the scene a silhouette pixel may lie.
const float OcclusionTolerance = 0.15;

void fragment() {
	if (alpha_scissor_on) {
		float a = texture(albedo_tex, UV).a;
		if (a < alpha_scissor_threshold) {
			discard;
		}
	}

	float visible = 1.0;
	if (fx_scene_depth_valid > 0.5) {
		float z = REFII_SCENE_DEPTH(SCREEN_UV).r;
		vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, z, 1.0);
		float scene = -view.z / view.w;
		if (-VERTEX.z > scene + OcclusionTolerance) {
			visible = 0.0;
		}
	}

	ALBEDO = vec3(visible * slot / 8.0, 1.0, 0.0);
}
