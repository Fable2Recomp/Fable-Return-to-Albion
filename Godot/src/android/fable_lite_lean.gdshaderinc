// The phone's lean fog and normal-map darkening for the lite surfaces: `lite_fog` and
// `lite_lean_darkening` (AndroidLeanShading.cs beside this file). Included by
// fable_atmosphere.gdshaderinc after its globals, and so after fable_lighting.gdshaderinc's in every
// lite body (standard_material_body, terrain_splat_body), only in a build that asks for one of them.

#ifdef FABLE_LITE_FOG_LEAN
#ifdef FABLE_LITE_FOG_TABLE
/// The transmittance against the distance past FoggingStart (AndroidLeanShading.FogTable), per
/// channel, before the volumetric cancel's clamp: texel j holds the lite curve at
/// dl = j * FABLE_FOG_TABLE_STEP, its near knots, fog scale and volumetric cancel folded in.
global uniform sampler2D atmos_extinction;
/// Texels per knot (7.8125) and the table's width, 256 knots and the end texel: AndroidLeanShading.
const float FABLE_FOG_TABLE_PER_KNOT = 4.0;
const float FABLE_FOG_TABLE_TEXELS = 1025.0;
#endif

/// fable_atmosphere_terms_for, the same terms with less per-pixel work (the lite path's maths).
///
/// THE SAME PICTURE: one inverse square root gives both the radial distance and the view direction
/// (Camera3D hands the server an orthonormalised transform, so |inv_view * v| = |v|); the sun
/// direction is published unit length (Engine_SkyFrame.Normalise), so it is not normalised again;
/// the in-scatter table is Engine_SkyAtmosphere.LutWidth (64) texels wide, so its half texel is a
/// constant rather than a size query; atmos_factors is Engine_SkyAtmosphere's FogBlendFactor and
/// FogColourFactor, both the constant 1, so neither scales anything.
///
/// FABLE_LITE_FOG_TABLE also fetches the transmittance instead of evaluating the curve (the pow,
/// three exps, the near knots and the volumetric cancel's exp): the guest's own arrangement
/// (g_Extinction), in 32-bit float at a quarter knot. Within 0.2/255 of the evaluated curve for
/// the shipped outdoor themes, 1.3/255 for a dense cave at four times its fog.
bool fable_atmosphere_terms_for(
		vec3 view_vertex,
		mat4 inv_view,
		bool close_fog_blend,
		out vec3 transmittance,
		out vec3 fog_colour) {
	transmittance = vec3(1.0);
	fog_colour = vec3(0.0);

	if (!atmos_fog_on) {
		return false;
	}

	float radial_sq = dot(view_vertex, view_vertex);
	float inv_radial = inversesqrt(radial_sq);
	float radial_dist = radial_sq * inv_radial;
	float d = radial_dist - atmos_range.x;
	if (d <= 0.0) {
		return false;
	}

#ifdef FABLE_LITE_FOG_UNIT_SCALE
	// `lite_fog_trims` (AndroidFogTrims): the fable model publishes outdoor_fog_scale, 1 at launch.
	const float fog_scale = 1.0;
#else
	// Upstream's max(scale, 0) is the scale wherever it is read: a scale at or below 0 returns here.
	float fog_scale = fable_interior_fog;
	if (fog_scale <= 0.0) {
		return false;
	}
#endif

#ifdef FABLE_LITE_FOG_SUN_VIEW
	// `lite_fog_trims`: dot(s, R v) = dot(s R, v), so the sun turns into view space (uniform-only)
	// instead of every pixel's ray into world space. The same value to float rounding.
	vec3 sun_view = vec3(-atmos_sun.x, atmos_sun.z, atmos_sun.y) * mat3(inv_view);
	float sun_dot = dot(sun_view, view_vertex) * inv_radial;
#else
	vec3 world_ray = mat3(inv_view) * view_vertex;
	// Upstream's clamp to [0, 1] is inside the half-texel clamp below, which alone decides the fetch.
	float sun_dot = dot(atmos_sun, vec3(-world_ray.x, world_ray.z, world_ray.y)) * inv_radial;
#endif
	const float half_texel = 0.5 / 64.0;
	vec3 sky = textureLod(atmos_inscatter, vec2(clamp(sun_dot, half_texel, 1.0 - half_texel), 0.5), 0.0).rgb;

	vec3 raw;
#ifdef FABLE_LITE_FOG_TABLE
	// Texel j's centre is dl = j * step with dl = d - knot / 2, so the coordinate is linear in d;
	// the clamps are the guest's clamp addressing (Godot's global sampler repeats).
	const float knot = 7.8125;
	float texel = (d * (FABLE_FOG_TABLE_PER_KNOT / knot)) + (0.5 - (0.5 * FABLE_FOG_TABLE_PER_KNOT));
	float u = clamp(texel, 0.5, FABLE_FOG_TABLE_TEXELS - 0.5) * (1.0 / FABLE_FOG_TABLE_TEXELS);
	raw = min(textureLod(atmos_extinction, vec2(u, 0.5), 0.0).rgb, vec3(1.0));
#else
	const float knot = 7.8125;
	float dl = max(d - (0.5 * knot), 0.0);
	if (dl < knot * 4.0 && atmos_near_ready) {
		float below = floor(dl / knot) * knot;
		int index = int(floor(dl / knot));
		raw = mix(fable_atmosphere_near_knot(index), fable_atmosphere_near_knot(index + 1), (dl - below) / knot);
		if (abs(fog_scale - 1.0) > 1e-4) {
			raw = pow(max(raw, vec3(1e-6)), vec3(fog_scale));
		}
	} else {
		float t = min(dl, atmos_range.y) * atmos_curve.y;
		float optical_depth = atmos_curve.x * pow(max(t, 0.0), atmos_curve.z) * fog_scale;
		raw = exp(-atmos_beta * optical_depth);
	}
#ifndef FABLE_LITE_FOG_NO_VOLUMETRIC_CANCEL
	// `lite_fog_trims` compiles this out where fable_volumetric_fog_end is always 0 (AndroidFogTrims).
	if (fable_volumetric_fog_end > 0.0) {
		// One exp, not three: the cancel is the same in every channel.
		float vol_dist = clamp(radial_dist, 0.0, 128.0);
		raw = min(raw * exp(fable_volumetric_fog_end * vol_dist), vec3(1.0));
	}
#endif
#endif

	transmittance = raw;
	fog_colour = close_fog_blend ? mix(atmos_close_fog, sky, clamp(d * atmos_range.z, 0.0, 1.0)) : sky;
	return true;
}
#endif

#ifdef FABLE_LITE_LEAN_DARKEN
/// The ambient's normal-map darkening (standard_material_body), `mix(fable_darkening, 1, nb^2)` with
/// nb = saturate(dot(normalize(mix(N, A, t)), M)), t = (dot(N, A) + 1) / 2, worked out in VIEW space
/// from the view-space normals the body already has: dot, mix and length commute with the camera's
/// rotation (orthonormal, as Camera3D hands it over), so only the reference turns and neither normal
/// goes to world space. And the bent normal is never built: its dot with M is
/// (1 - t) dot(N, M) + t dot(A, M), and its squared length is a function of c = dot(N, A) alone,
/// (1 + c + c^2 - c^3) / 2 (never under 11/27), so nb^2 takes one division instead of three
/// normalisations. The same picture (the same value to float rounding).
/// `normal` is the unit view-space NORMAL, `mapped` the unit view-space mapped normal.
/// The reference turns by the view matrix's rotation, written as the transpose of inv_view's (a
/// vector times a matrix), which the body has already read.
FABLE_LITE_MP vec3 fable_lean_darken(vec3 normal, vec3 mapped, mat4 inv_view) {
	vec3 a = normalize(vec3(fable_ambient_ref.x, fable_ambient_ref.z, fable_ambient_ref.y) * mat3(inv_view));
	float c = dot(normal, a);
	float t = 0.5 * (c + 1.0);
	float x = max(mix(dot(normal, mapped), dot(a, mapped), t), 0.0);
	float bent_sq = 0.5 * (1.0 + (c * (1.0 + (c * (1.0 - c)))));
	FABLE_LITE_MP float nb_sq = min((x * x) / bent_sq, 1.0);
	return mix(fable_darkening, vec3(1.0), nb_sq);
}
#endif
