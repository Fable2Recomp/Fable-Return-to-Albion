
shader_type spatial;
render_mode cull_disabled, specular_disabled;

#include "res://src/Engine/material/fable_lighting.gdshaderinc"
#include "res://src/Engine/level/environment/fable_atmosphere.gdshaderinc"

global uniform int fable_light_debug;

uniform bool has_lightmap = false;

// THE BACKGROUND PATH (Engine_EhfPages): a vista's albedo is its pre-baked colour page at UV,
// squared as PS 7 does (`r4 = tex*tex`), in place of the splat. The page is an atlas of 16-texel
// chunk rectangles packed edge to edge (black filler between), so it carries no generated mips:
// they would average neighbouring rectangles into every chunk seam.
uniform bool page_mode = false;
uniform sampler2D page_albedo : filter_linear, repeat_disable;

uniform sampler2DArray lod_array : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray lod_detail_array : filter_linear_mipmap_anisotropic, repeat_enable;

// The per-layer normal maps, sampled with their colour partner's UV because that is what the
// guest does — `sub_82215BE0` passes the SAME scale float to the ground pair (material+0xB0 and
// +0xB4, samplers s0/s2, both scaled by UVGroundFactor at +0xBC) and a different one to the
// cliff pair (+0xC8/+0xCC, s13/s14, UVCliffFactor at +0xD4). There is no normal-only UV set.
uniform sampler2DArray lod_normal_array : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray lod_detail_normal_array : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_normals = false;

// The ground's share of the ORM: per-layer cavity derived from the normal slices — base
// occlusion in R, the cliff projection's in G — sampled with the colour's own tiling. Linear
// data, no source_color. It darkens only the AMBIENT half below, like the props' ao does.
uniform sampler2DArray lod_ao_array : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_ao = false;

// The ground's PARALLAX — the same offset-mapping the props run, from a height the same
// wrapped Poisson solved out of each layer's tiling normal, packed in lod_ao_array's B.
// Same formula, same default scale as standard_material, so a cobble prop and the cobbled
// ground displace identically.
uniform bool has_height = false;
uniform float parallax_scale : hint_range(0.0, 0.1) = 0.02;

uniform sampler2DArray material_weight : filter_linear, repeat_disable;

// THE BAKE TAKES TWO DECODES, AND BOTH ARE REAL. This looks like a mistake and is not.
//
// First the hardware. The .lmp loader creates every lightmap texture through `sub_82B8CEE0`
// with a3 == 1, and that arm walks the format table for `stated | 0x7E00` — the sRGB sibling
// of whatever format the blob's own header names — and the chosen D3DFORMAT goes straight to
// XGSetTextureHeader. So the sample arrives already sRGB-decoded, which is what `source_color`
// reproduces here.
//
// Then the shader, on top of it:
//
//     r2.xyw = log2(|lightmap.xyz|) * g_LightmapFactorAndSRGBGamma.www
//     r4.xyz = exp2(r2.xyw)
//
// THE EXPONENT IS 1.3, NOT 2.2, and getting that wrong is a visible exposure error rather than
// a rounding one. There is no binding-id setter for this constant, which is what made an
// earlier reading call it an unwritable 2.2 shader literal — but the CPU writes it DIRECTLY
// into the device constant shadow instead, bypassing the binding table entirely.
// `sub_821B16F8` loads `flt_8209950C` (0x3FA66666 = 1.3f), permutes it against the theme's
// lightmap colour and stores the result to the pixel constant at device+0x18D0..0x18DC —
// which is c21, and the shipped shaders name c21 `g_LightmapFactorAndSRGBGamma`.
//
// The 1.3 is therefore a contrast lift applied after a real sRGB decode, not a gamma standing
// in for one.
//
// AND IT APPLIES TO THE PROPS TOO — this comment used to say the props use 2.2 INSTEAD, and that
// is the one thing here that was wrong. The 2.2 is real and is a literal in the AMB1 vertex
// shader, but it does not replace this exponent, it precedes it: blob 0244 computes
// `oColor0 = pow(iColor0, 2.2)`, and the matching pixel shader (blob 0327) opens with
// `float4 r3 = iColor0` and then raises r3 by `g_LightmapFactorAndSRGBGamma.w` exactly as the
// line above does. A per-vertex bake is decoded by 2.2 * 1.3 = 2.86 in total, which is why the
// engine uploads a literal 2.86 to the vertex side for the path that collapses the two.
//
// The pixel shader has no way to know which vertex shader ran, so this exponent is unconditional
// and the DIFFERENCE between prop and terrain is only whether a 2.2 happened upstream. See
// fable_lighting.gdshaderinc, which now carries both halves.
// CLAMP, not repeat. The engine never calls D3DDevice_SetSamplerState_* at all — sampler state
// lives in deferred state-var records drained by `Render_ApplyDirtyStateVar` — and the
// once-per-frame default pass `sub_822C2C60` writes stage 3 (the lightmap's) as
// AddressU = AddressV = g_Address2DMode = 2 = D3DTADDRESS_CLAMP, Min/Mag/Mip = LINEAR.
// It matters at exactly one place, the outermost half-texel, where wrapping folds the far edge
// of the region onto the near one.
uniform sampler2D lightmap : source_color, filter_linear, repeat_disable, hint_default_white;

const float FABLE_LIGHTMAP_GAMMA = 1.3;

// The remaster's bake weight, SHARED WITH EVERY AUTHORED PROP BAKE through the global — one
// weight, one rule, so the bridge, the rocks and the ground it meets scale together. The
// terrain lightmap is a full lighting solution (the sun pre-painted at a theme factor of five
// to seven in daylight); weighting it hands the ground's daylight to the real-time sun and
// its shadows. The flat GlobalAmbient floor is never scaled. `baked_light_weight` in the
// launcher; 1.0 is the shipped balance exactly. Declared in fable_lighting.gdshaderinc.

uniform vec4 chunk_origin_extent;

uniform vec4 chunk_grid_size;

uniform vec4 splat_params;

uniform vec4 mesh_xform;

uniform vec4 weight_size;

uniform vec4 material_params[32];

varying vec3 v_local;
varying vec3 v_local_normal;
varying flat vec4 v_page_rect;

void vertex() {
	v_local = VERTEX;
	v_page_rect = CUSTOM0;
	v_local_normal = NORMAL;
	fable_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

/// The two vertical projections a cliff layer is sampled through, and their weights.
///
/// THE CLIFF IS TRIPLANAR AND THE GROUND IS NOT. The guest projects its ground layer straight
/// down — horizontal world position times UVGroundFactor — and its cliff layer through two
/// VERTICAL planes times UVCliffFactor, blended by how much the surface faces each of the two
/// horizontal axes: `|n.x|` and `|n.y|` in the guest's Z-up axes, normalised by their sum plus
/// a small epsilon. Sampling a cliff with the ground's downward projection is what smears a
/// texture into vertical streaks on anything steep, which is the whole reason the second
/// projection exists.
///
/// `v_local` is already world space here — `mesh_xform` is (1, 1, 0, 0) — so the height axis is
/// simply `v_local.y`, and the guest's two horizontal axes are our x and z.
void cliff_projection(
		vec3 local, vec3 n, float scale, out vec2 uv_x, out vec2 uv_z, out vec2 w) {
	uv_x = vec2(local.z, local.y) * scale;
	uv_z = vec2(local.x, local.y) * scale;

	float wx = abs(n.x);
	float wz = abs(n.z);
	w = vec2(wx, wz) / (wx + wz + 0.0001);
}

vec3 sample_material(int slice, vec2 world_xy, vec3 local, vec3 n, float slope_w, vec2 par_dir) {
	int max_slice = max(int(chunk_grid_size.z) - 1, 0);
	int s = min(max(slice, 0), max_slice);
	vec4 mp = material_params[s];

	// PER MATERIAL, not one global scale for the whole landscape. mp.x is the layer's
	// UVGroundFactor and mp.y its UVCliffFactor — two different authored numbers that the guest
	// hands to two different sampler pairs. Using `splat_params.y` for both tiled every layer
	// at the same rate and ignored what the .ehf actually states for each.
	float base_scale = mp.x > 0.0 ? mp.x : splat_params.y;
	float cliff_scale = mp.y > 0.0 ? mp.y : base_scale;

	vec2 uv_x;
	vec2 uv_z;
	vec2 pw;
	cliff_projection(local, n, cliff_scale, uv_x, uv_z, pw);

	// One-tap offset mapping in this layer's own UV space, exactly the props' line. The cliff
	// projections stay unoffset — a near-vertical face under the ground's tangent frame would
	// smear, not displace.
	vec2 buv = world_xy * base_scale;
	if (dot(par_dir, par_dir) > 0.0) {
		buv += par_dir * (texture(lod_ao_array, vec3(buv, float(s))).b - 0.5);
	}

	vec3 base = texture(lod_array, vec3(buv, float(s))).rgb;
	vec3 cliff =
		texture(lod_detail_array, vec3(uv_x, float(s))).rgb * pw.x +
		texture(lod_detail_array, vec3(uv_z, float(s))).rgb * pw.y;

	// Gamma 2.0 by squaring, PER SAMPLE — the position matters. The guest squares each fetch
	// the instant it lands, `r11.xyz = r11.zxy * r11.zxy` straight after the tfetch, and it
	// draws one pass per material blending into the framebuffer. So what accumulates is a
	// weighted sum of SQUARED colours. Squaring the blended result instead computes
	// (sum w*c)^2 where the guest computes sum w*c^2, and those differ wherever two layers
	// overlap — which is every transition in the region.
	base *= base;
	cliff *= cliff;

	// mp.w is NOT part of this. It is the layer's UVCliffMacroMapFactor — a UV scale for a
	// macro map the guest samples elsewhere — and multiplying the cliff's share of the blend by
	// it was thinning every slope by an unrelated authored number.
	float cliff_w = slope_w * clamp(mp.z, 0.0, 1.0);
	return mix(base, cliff, cliff_w);
}

/// This layer's tangent-space normal, blended by the same slope weight as its colour.
///
/// Z IS RECONSTRUCTED rather than read. The guest's landscape normals are two-channel — it
/// decodes `n.xy = sample.xy * 2 - 1` and then `n.z = sqrt(1 - x*x - y*y)` — and reconstructing
/// gives the same answer for a three-channel map, so this is correct either way and does not
/// depend on knowing which compressed format a given .tex shipped in.
vec3 sample_material_normal(int slice, vec2 world_xy, vec3 local, vec3 n, float slope_w, vec2 par_dir) {
	int max_slice = max(int(chunk_grid_size.z) - 1, 0);
	int s = min(max(slice, 0), max_slice);
	vec4 mp = material_params[s];

	// Each normal map rides its colour partner's UV and projection — the guest passes the same
	// scale float to s0/s2 and the same one to s13/s14.
	float base_scale = mp.x > 0.0 ? mp.x : splat_params.y;
	float cliff_scale = mp.y > 0.0 ? mp.y : base_scale;

	vec2 uv_x;
	vec2 uv_z;
	vec2 pw;
	cliff_projection(local, n, cliff_scale, uv_x, uv_z, pw);

	// The same displaced coordinate the colour used — a normal sampled off its own colour's
	// texel slides the shading off the relief.
	vec2 buv = world_xy * base_scale;
	if (dot(par_dir, par_dir) > 0.0) {
		buv += par_dir * (texture(lod_ao_array, vec3(buv, float(s))).b - 0.5);
	}

	vec2 base_xy = texture(lod_normal_array, vec3(buv, float(s))).xy;
	vec2 cliff_xy =
		texture(lod_detail_normal_array, vec3(uv_x, float(s))).xy * pw.x +
		texture(lod_detail_normal_array, vec3(uv_z, float(s))).xy * pw.y;

	float cliff_w = slope_w * clamp(mp.z, 0.0, 1.0);
	vec2 xy = mix(base_xy, cliff_xy, cliff_w) * 2.0 - 1.0;
	return vec3(xy, sqrt(max(1.0 - dot(xy, xy), 0.0)));
}

/// This layer's cavity, through the SAME projections and weights as its colour and normal —
/// the R channel rides the ground's downward projection, the G channel the cliff's two planes.
float sample_material_ao(int slice, vec2 world_xy, vec3 local, vec3 n, float slope_w) {
	int max_slice = max(int(chunk_grid_size.z) - 1, 0);
	int s = min(max(slice, 0), max_slice);
	vec4 mp = material_params[s];

	float base_scale = mp.x > 0.0 ? mp.x : splat_params.y;
	float cliff_scale = mp.y > 0.0 ? mp.y : base_scale;

	vec2 uv_x;
	vec2 uv_z;
	vec2 pw;
	cliff_projection(local, n, cliff_scale, uv_x, uv_z, pw);

	float base = texture(lod_ao_array, vec3(world_xy * base_scale, float(s))).r;
	float cliff =
		texture(lod_ao_array, vec3(uv_x, float(s))).g * pw.x +
		texture(lod_ao_array, vec3(uv_z, float(s))).g * pw.y;

	float cliff_w = slope_w * clamp(mp.z, 0.0, 1.0);
	return mix(base, cliff, cliff_w);
}

void fragment() {
	// TWO-SIDED, LIKE THE PROPS — AND THIS LINE IS WHY THE GROUND TOOK NO LIGHT. The level
	// root carries Scale(-1, 1, 1), which flips the winding of everything under it, so the
	// terrain's TOP rasterises as a BACK face and Godot hands fragment NORMAL through
	// un-flipped — pointing into the ground. Every NdotL in light() — the sun's AND every
	// lamp's — clamped to zero, while the shadow term (no NdotL) stayed perfect, which is
	// exactly the year of "shadows received but never seen". standard_material has carried
	// this pair since the beginning; the splat never did.
	float side = fable_normal_side(NORMAL, VERTEX, VIEW);
	NORMAL *= side;
	BINORMAL *= side;

	vec2 mesh_xy = vec2(v_local.x, v_local.z);
	vec2 world_xy = (mesh_xy * mesh_xform.xy) + mesh_xform.zw;

	vec2 origin = chunk_origin_extent.xy;
	vec2 extent = chunk_origin_extent.zw;
	float cw = chunk_grid_size.x;
	float ch = chunk_grid_size.y;

	vec2 chunk_co = (world_xy - origin) / extent;
	vec2 chunk_clamped = clamp(chunk_co, vec2(0.0), vec2(cw - 0.001, ch - 0.001));

	vec3 geom_normal = normalize(v_local_normal);
	float slope_w = clamp((0.90 - abs(geom_normal.y)) / 0.10, 0.0, 1.0);

	// The view direction in the mesh's tangent frame, once per fragment — the analytic frame
	// ToArrayMesh emits (T = local +X, B = local +Z via w = -1) maps tangent xy straight onto
	// the ground UV's axes, so this IS the props' offset-mapping line on the ground's UVs.
	vec2 par_dir = vec2(0.0);
	if (has_height && parallax_scale > 0.0) {
		vec3 tangent_view = normalize(vec3(
			dot(VIEW, TANGENT), dot(VIEW, BINORMAL), dot(VIEW, NORMAL)));
		par_dir = tangent_view.xy * parallax_scale / max(tangent_view.z, 0.4);
	}

	vec3 final = vec3(0.0);
	vec3 mapped_normal = vec3(0.0);
	float ao = 0.0;
	float weight_sum = 0.0;

	vec2 inv_weight_size = weight_size.zw;
	vec2 weight_uv = ((chunk_clamped * 32.0) + 0.5) * inv_weight_size;
	vec2 edge_uv = 0.5 * inv_weight_size;
	weight_uv = clamp(weight_uv, edge_uv, vec2(1.0) - edge_uv);

	for (int mat = 0; mat < 32; mat++) {
		if (page_mode || mat >= int(chunk_grid_size.z)) {
			break;
		}

		float w = textureLod(material_weight, vec3(weight_uv, float(mat)), 0.0).r;
		if (w <= 0.001) {
			continue;
		}

		final += sample_material(mat, world_xy, v_local, geom_normal, slope_w, par_dir) * w;
		if (has_normals) {
			mapped_normal += sample_material_normal(
				mat, world_xy, v_local, geom_normal, slope_w, par_dir) * w;
		}
		if (has_ao) {
			ao += sample_material_ao(mat, world_xy, v_local, geom_normal, slope_w) * w;
		}
		weight_sum += w;
	}

	if (page_mode) {
		// Clamped inside this chunk's own atlas rectangle (CUSTOM0 = u0, v0, u1, v1), half a
		// texel in: bilinear at a rectangle's edge otherwise mixes in its neighbour.
		vec2 half_texel = 0.5 / vec2(textureSize(page_albedo, 0));
		vec2 page_uv = clamp(UV, v_page_rect.xy + half_texel, max(v_page_rect.zw - half_texel, v_page_rect.xy + half_texel));
		vec3 page = texture(page_albedo, page_uv).rgb;
		final = page * page;
		mapped_normal = vec3(0.0, 0.0, 1.0);
		ao = 1.0;
	} else if (weight_sum > 0.001) {
		final /= weight_sum;
		mapped_normal /= weight_sum;
		ao /= weight_sum;
	} else {
		final = sample_material(0, world_xy, v_local, geom_normal, slope_w, par_dir);
		mapped_normal = has_normals
			? sample_material_normal(0, world_xy, v_local, geom_normal, slope_w, par_dir)
			: vec3(0.0, 0.0, 1.0);
		ao = has_ao
			? sample_material_ao(0, world_xy, v_local, geom_normal, slope_w)
			: 1.0;
	}

	if (!has_ao) {
		ao = 1.0;
	}

	mapped_normal = has_normals ? normalize(mapped_normal) : vec3(0.0, 0.0, 1.0);

	// `final` is already squared — `sample_material` does it per fetch, where the guest does.
	//
	// That decode is what the ground was missing, and it is why the theme's LightmapFactor
	// could not be applied before: an unsquared albedo is roughly the square root of the
	// guest's, about twice as bright through the midtones, so a factor authored against the
	// darker operand drove the whole surface to white. The ground reading visibly hotter than
	// the props standing on it was the same defect seen from the other end.
	ALBEDO = final;

	// Godot builds the heightfield's tangent frame; the guest builds its own in the vertex
	// shader from a region-wide normal texture. Handing it the blended tangent-space normal is
	// what makes the direct light respond to the layers' bumps at all — the darkening term
	// below only affects the AMBIENT half.
	if (has_normals) {
		NORMAL_MAP = mapped_normal * 0.5 + 0.5;
	}

	// THE TERRAIN HAS A LIGHTMAP, and the shipped shader is what says so:
	// PSHADER_LANDSCAPEMATERIAL_..._AMB1 binds `g_TextureLightmapSampler` and samples it with
	// its own UV set. There is no type-6 override of the lightmap-fetch virtual to find on the
	// CPU side — the binding is data-driven through the shared material path.
	//
	// The guest's composition, from the same shader:
	//
	//     darken  = lerp(AmbientNormalMapDarkeningColour, 1, saturate(dot(Nbent, Nmapped))^2)
	//     ambient = pow(lightmap, gamma) * LightmapFactor * darken + GlobalAmbient
	//
	// TWO THINGS ABOUT THAT DOT ARE EASY TO GET WRONG.
	// It is SQUARED — `r0.xy = r0.xy * r0.zy` feeds the lerp, not the raw cosine, and
	// two independent shader blobs (0189 and the CLF1 variant 0205) agree. And the first
	// operand is NOT the geometric normal: the vertex shader builds a BENT normal,
	//
	//     t     = 0.5 * (dot(N, g_AmbientLightReferenceDirection) + 1)
	//     Nbent = normalize(lerp(N, g_AmbientLightReferenceDirection, t))
	//
	// and passes it down as its own interpolator. So the term does not vanish on flat ground
	// the way a geometric-vs-mapped dot would.
	//
	// TWO DIVERGENCES REMAIN, and they are in the first operand rather than the shape. The
	// guest's bent normal leans toward `g_AmbientLightReferenceDirection`, a constant whose
	// source we have not established, so the geometric normal stands in for it — which makes
	// the dot measure how far the normal map departs from the surface, the term's evident
	// intent, but not the guest's exact cosine. And the guest builds its tangent frame in the
	// vertex shader from a region-wide normal texture it fetches at s17, Gram-Schmidt'd against
	// the interpolated normal; ours is Godot's heightfield frame. Both make this darker or
	// lighter by degrees, neither changes what it is.
	//
	// With no normal maps loaded the term is exactly 1: `mapped_normal` is (0,0,1), the dot is
	// 1, and the lerp returns the second operand.
	//
	// THE UV IS A PLANAR WORLD PROJECTION, AND IT IS THE HEIGHTFIELD'S OWN. The landscape
	// vertex shader states it in two lines, with no offset of its own anywhere:
	//
	//     oTexCoord3.xy = g_LandscapeLightmapUVFactor.xy * worldXY
	//
	// where worldXY is the same landscape-relative position the heightfield fetch uses,
	// `worldXY * g_TexCoordScale.xy`. Two different constants, and they turn out to be the
	// same number:
	//
	//     g_TexCoordScale.xy            = 1 / ((sampleCount - 1) * metresPerTexel)   [VS binding 393]
	//     g_LandscapeLightmapUVFactor   = 1 / (lightmapTexelDim   * metresPerTexel)  [sub_82A8EA50]
	//
	// The second is built into landscape+0x100 beside the texture lookup and uploaded by
	// `sub_822AA878`, which binds the bake at sampler 3 and writes that float4 to the device
	// constant shadow at +0x860 — the same register the no-lightmap path explicitly zeroes.
	//
	// They agree because a bake's texel grid is exactly the heightfield's CELL grid. Checked
	// against the shipped .lmp rather than assumed — its records carry the sample counts beside
	// the blob, and both of this level's heightfields are exact:
	//
	//     landscape  samples 577x577 -> bake 576x576     (577-1, 577-1)
	//     sea vista  samples  33x65  -> bake  32x64      ( 33-1,  65-1)
	//
	// So the bake spans the heightfield's extent one texel per cell, and the normalised
	// heightfield coordinate below IS that projection. Nothing per-patch is involved: the
	// patches carry no id of their own, and one lightmap covers a landscape's whole grid.
	// THE GUEST'S EXACT MAPPING (sub_82A8EA50, 0x82A8EB18): uv = cell / lightmapTexDim, where
	// the divisor is the LIGHTMAP TEXTURE'S OWN DIMENSIONS — 1/(GetDimensions × metresPerCell)
	// against (world − heightfield origin) — with NO half-texel offset and CLAMP addressing
	// (engine defaults, 0x82B76290). Dividing by the cell count instead (32 × chunks) matched
	// only while a bake really was one texel per cell; any other .lmp size smeared the baked
	// shadows linearly across the region — "the shadows don't line up". `repeat_disable` on
	// the sampler is the guest's CLAMP; texel i owns cell span [i, i+1), corner-aligned, so
	// bilinear at cell/dim needs no +0.5 — adding one would shift the bake half a cell.
	vec2 lm_dim = vec2(textureSize(lightmap, 0));
	vec2 lm_uv = (chunk_clamped * 32.0) / max(lm_dim, vec2(1.0));
	vec3 bake = has_lightmap
		? pow(max(texture(lightmap, lm_uv).rgb, vec3(0.0)), vec3(FABLE_LIGHTMAP_GAMMA))
		: vec3(0.0);

	// darken = lerp(DarkeningColour, 1, saturate(dot(Nbent, Nmapped))^2). The square is the
	// guest's, and the theme's darkening colour arrives through `fable_darkening` — it is the
	// one lighting colour registered as type 1 rather than type 2, so it carries no Factor.
	//
	// THE SAME BENT NORMAL THE PROPS USE: standard_material bends toward the sun (blob 0245:
	// t = 0.5·(dot(N, AmbRef)+1); bent = lerp(N, AmbRef, t)), and the ground's ambient darkens
	// by the same rule as the bridge standing on it. The tangent frame is the analytic one
	// ToArrayMesh emits:
	// T = local +X, B = cross(N, T) × w with w = −1.
	vec3 darken = vec3(1.0);
	if (has_normals) {
		vec3 world_geom = normalize((MODEL_MATRIX * vec4(geom_normal, 0.0)).xyz);
		vec3 world_t = normalize((MODEL_MATRIX * vec4(1.0, 0.0, 0.0, 0.0)).xyz);
		vec3 world_b = cross(world_geom, world_t) * -1.0;
		vec3 mapped_world = normalize(
			(world_t * mapped_normal.x) + (world_b * mapped_normal.y) +
			(world_geom * mapped_normal.z));
		vec3 amb_ref = normalize(vec3(atmos_sun.x, atmos_sun.z, atmos_sun.y));
		float bend = 0.5 * (dot(world_geom, amb_ref) + 1.0);
		vec3 bent = normalize(mix(world_geom, amb_ref, bend));
		float nb = clamp(dot(bent, mapped_world), 0.0, 1.0);
		darken = mix(fable_darkening, vec3(1.0), nb * nb);
	}

	// The theme's LightmapFactor, applied now that it has the operand the guest authored it
	// against. `fable_ambient` adds the flat GlobalAmbient on top, so this line is the guest's
	// `bake * LightmapFactor + GlobalAmbient` with the darkening term collapsed to 1.
	//
	// `g_LandscapeAmbientLightFactor` IS DELIBERATELY ABSENT, AND ITS VALUE HERE IS 1. The guest's
	// landscape pixel shader ends on
	//
	//     r0.xyz = ambient * g_LandscapeAmbientLightFactor.xxx + direct;
	//
	// which reads like a brightness control on the ambient half and is not one. It is pixel
	// constant c11, written only by `sub_822159D8` at 0x82215BB4, and its value is a splatted
	// literal chosen by a PASS COUNTER — 1.0 on a patch's first pass, 0.0 on every later one
	// (0x82215B48 `lwz r11, 0x54(r30)`, then `vspltisw v0, 1` / `vspltisw v0, 0`). It exists so
	// that the additive terms land ONCE PER PATCH rather than once per blend layer, because the
	// guest draws terrain one layer per pass; `g_AtmosphericFactors` is gated by the same
	// predicate two instructions earlier.
	//
	// We draw the whole splat in ONE pass, so we are permanently the first pass and the factor is
	// permanently 1. Multiplying by it would be a no-op; adding it as a uniform would invite
	// someone to tune it, which would be tuning a pass counter. IF THIS SHADER EVER GOES
	// MULTI-PASS, both this ambient term and the atmosphere block below have to be gated off on
	// every pass after the first, or the region gains one extra ambient and one extra in-scatter
	// per layer.
	// CAVITY DARKENS THE WHOLE AMBIENT HALF, the authored bake included — no double count: the
	// terrain lightmap is one texel per heightfield cell, metres across, so it cannot carry
	// texture-scale occlusion. The direct half is untouched, same as the props' ao.
	// The same weight the props take, interior exemption and interior bake factor included.
	float authored_weight = fable_authored_bake_weight();
	vec3 authored = bake * fable_lightmap_rgb() * darken * authored_weight;

	// THE TERRAIN'S SKY TETHER — the SAME plain mix the probe field takes in `fable_bake`,
	// deliberately NOT a chroma-only retint. The painted lightmap carries the 360's baked sun
	// and shadows at full amplitude, and that amplitude is what made the ground a different
	// lighting world from the bridge beside it: the live sun's term (and therefore its
	// SHADOWS) modulated a small fraction of the total and read as nothing, while the props'
	// moderate vertex bakes let their direct share show. Pulling the bake toward the flat sky
	// ambient hands the ground's modelling to the live sun, its shadows and the lamps — the
	// bridge look. A first chroma-only version kept the painted amplitude and changed nothing
	// the eye could see. `fable_sky_ambient.w` is 0 in the fable model: authored, untouched.
	authored = fable_sky_tether(authored);

	// AO ON THE BAKE HALF ONLY, exactly where standard_material puts it: the flat GlobalAmbient
	// floor stays unoccluded, as it does on the props beside the ground. The cavity still
	// occludes the terrain bake itself: the lightmap is metres-per-texel and cannot carry
	// texture-scale occlusion.
	EMISSION = final * fable_ambient(authored * ao);
	// Godot's ambient and GI take the AO the guest's fill took above; 1 in the fable model.
	AO = mix(1.0, ao, fable_unified);

	// LAST, because it attenuates everything above it. Exact here: the ground is
	// `specular_disabled`, so ALBEDO and EMISSION between them carry the whole surface, and the
	// guest's landscape pixel shader (blob 0007) runs the same block instruction for instruction
	// as the props standing on it. Opaque, so the guest's alpha factor is 1.
	vec3 fog_t;
	vec3 fog_c;
	if (fable_atmosphere_terms(VERTEX, INV_VIEW_MATRIX, fog_t, fog_c)) {
		fable_atmosphere_apply(fog_t, fog_c, 1.0, ALBEDO, EMISSION);
	}

	// THE LIGHTING DEBUG VIEWS — `lighting_debug_view` in the launcher, global
	// `fable_light_debug`. ALBEDO = 0 kills the diffuse light term (Godot multiplies
	// DIFFUSE_LIGHT by ALBEDO after light()), so each mode isolates one half of
	// `albedo * (direct + bake)`: 1 = bake only, 2 = direct only, 3 = raw albedo,
	// 4 = the normal-map darken term. Specular still rides modes 1/3/4 — minor.
	if (fable_light_debug == 1) {
		ALBEDO = vec3(0.0);
	} else if (fable_light_debug == 2) {
		EMISSION = vec3(0.0);
	} else if (fable_light_debug == 3) {
		EMISSION = final;
		ALBEDO = vec3(0.0);
	} else if (fable_light_debug == 4) {
		EMISSION = darken;
		ALBEDO = vec3(0.0);
	} else if (fable_light_debug == 8) {
		EMISSION = (NORMAL * 0.5) + 0.5;
		ALBEDO = vec3(0.0);
	} else if (fable_light_debug == 9) {
		// Every light, and under the unified model Godot's ambient and GI, on a WHITE ground.
		EMISSION = vec3(0.0);
		ALBEDO = vec3(1.0);
	} else if (fable_light_debug == 10) {
		EMISSION = FRONT_FACING ? vec3(0.1, 1.0, 0.1) : vec3(1.0, 0.1, 0.1);
		ALBEDO = vec3(0.0);
	} else if (fable_light_debug == 5) {
		// Terrain has one bake source: cyan = its lightmap texture is bound, red = missing.
		EMISSION = has_lightmap ? vec3(0.1, 1.0, 1.0) : vec3(1.0, 0.1, 0.1);
		ALBEDO = vec3(0.0);
	} else if (fable_light_debug == 6 || fable_light_debug == 7) {
		// 6: the directional shadow term alone; 7: SIGNED NdotL (0.5 = grazing, white =
		// facing the sun, black = facing away) — see the same views in standard_material.
		EMISSION = vec3(0.0);
		ALBEDO = vec3(1.0);
	} else if (fable_light_debug == 8) {
		// The VIEW-SPACE NORMAL as colour, straight from the fragment.
		EMISSION = (NORMAL * 0.5) + 0.5;
		ALBEDO = vec3(0.0);
	}
}

void light() {
	// The same linear ramp the props take. The ground is where a torch's pool of light is most
	// readable, so terrain keeping Godot's curve while the wall beside it took the guest's would
	// be the two-lighting-models problem this file's header warns about, in a new place.
	// No ALBEDO argument: Godot multiplies DIFFUSE_LIGHT by it after this function returns.
	// The landscape's direct term carries a second gate the props do not:
	// `saturate(dot(N, PrimaryLightDirection) * 8.0)` (blob 0189, c255.x = 8.0) — a hard
	// shutter over the last ~7 degrees before the terminator, multiplied with plain NdotL.
	// For the directional light LIGHT is that primary direction, so the gate reuses it.
	if (fable_light_debug == 6) {
		// VIEW 6 — the raw directional shadow term; see standard_material's light().
		if (LIGHT_IS_DIRECTIONAL) {
			DIFFUSE_LIGHT = vec3(clamp(ATTENUATION, 0.0, 1.0));
		}
	} else if (fable_light_debug == 7) {
		// VIEW 7 — SIGNED NdotL, the term view 2 cannot separate from the rest.
		if (LIGHT_IS_DIRECTIONAL) {
			DIFFUSE_LIGHT = vec3((dot(NORMAL, LIGHT) * 0.5) + 0.5);
		}
	} else {
		float gate = LIGHT_IS_DIRECTIONAL
			? clamp(8.0 * dot(NORMAL, LIGHT), 0.0, 1.0)
			: 1.0;
		DIFFUSE_LIGHT += gate * fable_direct(
			NORMAL, LIGHT,
			fable_attenuation(ATTENUATION, LIGHT_IS_DIRECTIONAL, SPECULAR_AMOUNT, fable_world),
			LIGHT_COLOR);
	}
}
