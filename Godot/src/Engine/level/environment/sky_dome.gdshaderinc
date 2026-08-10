// THE SKY DOME'S IN-SCATTER, IN ONE PLACE.
//
// The dome itself draws it and the water reflects it, and they were carrying separate copies of
// the same arithmetic — same coefficients, same 1.05, same 0.2 exponent, differing only in an
// epsilon and in one of them tonemapping and the other not. That divergence is exactly what a
// second implementation buys you, so there is now one.
//
// The shape is the guest's, from 0111 pshader_skycolour:
//
//     h        = |cosZenith| ^ 0.2                                  // c254.x = 0.2
//     k        = g_AtmosphericParameters(3).y * (1.05 - h)          // c250.w = 1.05
//     depthR   = lerp(k, g_TzenithR.x, h)
//     depthM   = lerp(k, g_TzenithM.x, h)
//     tau      = depthR * betaRayleigh + depthM * betaMie
//     exp2(-tau * 1.442695)                                         // log2(e)
//     inscatter = (1 - exp(-tau)) * tfetch1D(g_InScattering, saturate(dot(sunDir, rayDir)))
//
// TWO DELIBERATE DEVIATIONS, both because the guest's operands are not separable here. The
// guest carries a SEPARATE optical depth for Rayleigh and for Mie, each a lerp between a
// horizon constant and its own zenith constant (g_TzenithR / g_TzenithM); we use one depth for
// both, so the two coefficient sets scale together rather than crossing over with altitude.
// And the phase function is a 1D LUT the guest uploads, which we rebuild host-side in
// Engine_SkyAtmosphere.BuildInScatterLut — the shape is ours, the sampling point is the
// guest's saturated sun dot.
//
// The result is LINEAR HDR. There is no tonemap and no transfer here: the game applies one
// luminance curve and one pow() for the whole frame in its post-processing material, and
// Engine_LevelLighting.ConfigureEnvironment is where we stand in for it.

vec3 fable_sky_inscatter(
		vec3 ray_engine,
		vec3 sun_engine,
		sampler2D in_scatter_lut,
		vec3 beta_rayleigh,
		vec3 beta_mie,
		float horizon_distance,
		float dome_exposure) {
	vec3 ray_n = normalize(ray_engine);

	float sun_dot = clamp(dot(normalize(sun_engine), ray_n), 0.0, 1.0);
	vec3 scatter = texture(in_scatter_lut, vec2(sun_dot, 0.5)).rgb;

	float up = max(abs(ray_n.z), 0.0001);
	float up_pow = exp2(log2(up) * 0.2);
	float dist = horizon_distance * (1.05 + up_pow) * (1.0 + up_pow);

	vec3 tau = (beta_rayleigh + beta_mie) * dist;
	vec3 extinction = exp2(-tau * 1.4427);
	return (vec3(1.0) - extinction) * scatter * dome_exposure;
}
