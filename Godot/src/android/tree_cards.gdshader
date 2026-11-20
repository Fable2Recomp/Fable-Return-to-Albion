shader_type spatial;
// THE PHONE'S TREE CARDS (AndroidTreeCards, `tree_billboards`): one quad per tree placement past 0.75 F,
// showing its model's picture from the capture atlas. The quad's corners are (+-1, +-1); each instance's
// transform holds the world centre of the model's bound sphere and its radius as the scale, and
// INSTANCE_CUSTOM holds (cell, start, cull, 1 when the cell holds its picture), where start is where the
// model ends (AndroidTreeCardMaths.Start). The card turns about the vertical only, fades in over
// [0.75, 0.875] start and out over the last eighth before the cull (AndroidTreeCardMaths.CardFade), and is
// cut at alpha 0.5, so a crown erodes in rather than dissolving. Fog is worked out per card at its
// centre, on the trees' line (no close-fog tint), as the lite props work it out per vertex.
// AndroidTreeCards composes FABLE_LITE and this process's fog defines ahead of the include, and
// TREE_CARDS_OPAQUE_CUT under `tree_billboards_debug_opaque_cut` (AndroidTreeCards.CardDefines).
render_mode unshaded, cull_disabled, depth_draw_opaque, shadows_disabled, fog_disabled, skip_vertex_transform;

#include "res://src/Engine/level/environment/fable_atmosphere.gdshaderinc"

/// The capture atlas with its mips: unexposed linear colour, alpha 1 where a model stood.
uniform sampler2D tree_atlas : hint_default_transparent, filter_linear_mipmap, repeat_disable;
/// (columns, rows) of cells in the atlas.
uniform vec2 atlas_cells = vec2(8.0, 1.0);

varying vec3 v_fog_t;
varying vec3 v_fog_c;
varying float v_fade;

void vertex() {
	vec3 centre = MODEL_MATRIX[3].xyz;
	float radius = length(MODEL_MATRIX[0].xyz);
	float f = INSTANCE_CUSTOM.y;
	float cull = INSTANCE_CUSTOM.z;
	float d = distance(INV_VIEW_MATRIX[3].xyz, centre);
	float fade = 0.0;
	if (f > 0.0 && cull > 0.0) {
		fade = clamp((d - (0.75 * f)) / (0.125 * f), 0.0, 1.0) * clamp((cull - d) / (0.125 * cull), 0.0, 1.0);
	}
	v_fog_t = vec3(1.0);
	v_fog_c = vec3(0.0);

	float cell = floor(INSTANCE_CUSTOM.x + 0.5);
	float col = mod(cell, atlas_cells.x);
	float row = floor(cell / atlas_cells.x);
	if (INSTANCE_CUSTOM.w < 0.5 || row >= atlas_cells.y) {
		// A model that arrived after the last capture has no picture yet.
		fade = 0.0;
	}
	v_fade = fade;
	UV = (vec2(col, row) + vec2((VERTEX.x * 0.5) + 0.5, 0.5 - (VERTEX.y * 0.5))) / atlas_cells;

	if (fade <= 0.0) {
		// Outside its band: all four corners on one point, so no fragment runs.
		VERTEX = vec3(0.0);
	} else {
		vec3 right = vec3(INV_VIEW_MATRIX[0].x, 0.0, INV_VIEW_MATRIX[0].z);
		right = dot(right, right) > 1e-6 ? normalize(right) : vec3(1.0, 0.0, 0.0);
		vec3 world = centre + (radius * ((VERTEX.x * right) + (VERTEX.y * vec3(0.0, 1.0, 0.0))));
		VERTEX = (VIEW_MATRIX * vec4(world, 1.0)).xyz;
		vec3 fog_t = vec3(1.0);
		vec3 fog_c = vec3(0.0);
		fable_atmosphere_terms_for((VIEW_MATRIX * vec4(centre, 1.0)).xyz, INV_VIEW_MATRIX, false, fog_t, fog_c);
		v_fog_t = fog_t;
		v_fog_c = fog_c;
	}
	NORMAL = vec3(0.0, 0.0, 1.0);
}

void fragment() {
	vec4 picture = texture(tree_atlas, UV);
	// The mips are boxed against a transparent black background, so a crown's edge texels carry
	// colour times coverage: divided back, the edge keeps the crown's colour instead of darkening.
	vec3 colour = picture.rgb / max(picture.a, 1e-3);
	vec3 emission = vec3(0.0);
	fable_atmosphere_apply(v_fog_t, v_fog_c, 1.0, colour, emission);
	ALBEDO = colour + emission;
#ifndef TREE_CARDS_OPAQUE_CUT
	ALPHA = picture.a * v_fade;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
#else
	// `tree_billboards_debug_opaque_cut`: the same cut by a plain discard, with ALPHA never written, so
	// Godot builds the card's colour pipeline without blending (scene_shader_forward_mobile.cpp blends
	// any opaque material that writes ALPHA, scissor or not; the scissor then writes alpha 1 anyway).
	// Last in fragment(), the one tail discard (shader-dxil-safety.md).
	if (picture.a * v_fade < 0.5) {
		discard;
	}
#endif
}
