using System;

namespace Refii.Level;

public sealed class AtmosphereInputs
{
    public float SunIntensity { get; set; } = 1.0f;

    public float BetaRayleighMultiplier { get; set; } = 1.0f;

    public float BetaMieMultiplier { get; set; } = 1.0f;

    public float[] SkyColour { get; } = { 1f, 1f, 1f, 0f };

    public float[] ComplementaryColour { get; } = { 1f, 1f, 1f, 0f };

    public float ComplementaryBias { get; set; }

    public float[] SunsetColour { get; } = { 1f, 1f, 1f, 0f };

    public float FoggingStart { get; set; }

    public float CloseFogMaxDistance { get; set; }

    /// <summary>Whether the theme states a Fogging record at all.</summary>
    /// <remarks>
    /// The guest's clamps below never produce "no fog" — an unstated theme still solves to a
    /// visible curve, because the floors (1 unit of near range, 0.01 density) are there to keep
    /// the logarithms finite rather than to express absence. So absence has to be carried
    /// separately, and it is the theme's own silence: no Fogging record, no fog.
    /// </remarks>
    public bool HasFog { get; set; }

    public float NearDistance { get; set; }

    public float NearDensity { get; set; }

    public float FarDistance { get; set; }

    public float FarDensity { get; set; }

    /// <summary>The theme's CloseFogColour, or white where it states none.</summary>
    public float[] CloseFogColour { get; } = { 1f, 1f, 1f };

    public float[] SunDirection { get; } = { 0f, 0f, 1f, 0f };
}

public sealed class AtmosphereState
{
    public float[] BetaRayleigh { get; } = new float[4];

    public float[] BetaMie { get; } = new float[4];

    public float[] SunDirection { get; } = new float[4];

    public float[] ScatteringMisc { get; } = new float[4];

    public float[] LutBetaRayleighPhase { get; } = new float[4];

    public float[] LutBetaMiePhase { get; } = new float[4];

    public float LutCosSunElevation { get; set; } = 1.0f;

    public float[] LutHg { get; } = new float[4];

    public float[] LutScale { get; } = { 1f, 1f, 1f, 1f };

    public float[] LutColourA { get; } = new float[4];

    public float[] LutColourB { get; } = new float[4];

    public float LutBiasPlusHalf { get; set; } = 0.5f;

    public float[] SunElementColour { get; } = { 1f, 1f, 0f, 0f };

    /// <summary>Whether world surfaces are fogged at all this frame.</summary>
    public bool HasFog { get; set; }

    /// <summary>
    /// The total optical depth the fog reaches at its far control point — the guest's <c>A</c>.
    /// </summary>
    public float FogOpticalDepthAtFar { get; set; }

    /// <summary>One over the far control distance, measured from FoggingStart — the guest's <c>1/F</c>.</summary>
    public float FogRecipFarRange { get; set; }

    /// <summary>The power the optical depth follows with distance — the guest's <c>k</c>.</summary>
    public float FogExponent { get; set; }

    /// <summary>The near-field fog tint, resolved against the guest's default.</summary>
    public float[] FogCloseColour { get; } = { 1f, 1f, 1f };
}

public static class Engine_SkyAtmosphere
{
    public const double BetaRayleighCommon = 248.05023415027725;

    public const double BetaRayleighPowBase = 0.000580084099999878;

    public const double BetaRayleighPowExponent = 2.0;

    public const float RayleighMultiplierScale = 500.0f;

    public const float RayleighPhaseNorm = 0.059683103f;

    public const float MiePhaseNorm = 0.079577468f;

    public const float MieGA0 = 0.10000000149011612f;

    public const float MieGA1 = 0.03180000185966492f;

    public const float MieGB0 = 0.4440000057220459f;

    public const float MieGB1 = 0.05984000116586685f;

    public const float HorizonDistance = 1900.0f;

    public const float RecipMaxFogDistance = 0.00050000002f;

    /// <summary>
    /// How far the fog curve is defined for, past FoggingStart. Beyond it the transmittance holds.
    /// </summary>
    /// <remarks>
    /// Exactly <c>1 / RecipMaxFogDistance</c>, and the guest states it twice over: the LUT baker
    /// walks 256 texels at a step of <c>flt_82000AD8 = 7.8125</c>, which is 2000/256, and the
    /// pixel shader addresses that same LUT with <c>(d - FoggingStart) * c67.w</c> where c67.w is
    /// the literal 0.0005. The texture's clamp addressing is what freezes the curve past the end,
    /// so a surface further out than 2000 units keeps the transmittance of the 2000-unit one.
    /// </remarks>
    public const float MaxFogDistance = 2000.0f;

    /// <summary>The floor under the near control distance — <c>fsel</c> against 1.0 at 0x821FE10C.</summary>
    public const float FogNearRangeFloor = 1.0f;

    /// <summary>How far the far control point is forced past the near one — 9.0, at 0x821FE114.</summary>
    public const float FogFarRangeMargin = 9.0f;

    /// <summary>The floor under both stated densities — <c>flt_8209CB94 = 0.01</c>.</summary>
    public const float FogDensityFloor = 0.01f;

    /// <summary>
    /// The FoggingStart of a theme that states none — <c>flt_8209BC58</c> in the guest's defaults
    /// constructor <c>sub_82A45DD0</c>. Far enough that the gate never opens.
    /// </summary>
    public const float UnfoggedStart = 1000000.0f;

    /// <summary>
    /// What the guest's theme holds for a Fogging field the data does not state.
    /// </summary>
    /// <remarks>
    /// ZERO IS NEVER A VALUE THE GUEST INTENDS. It is only ever what the GDB loader writes for
    /// "not stated", alongside a presence flag of 0 — and the layer blend then guarantees such a
    /// field cannot reach the shader, because a source with a zero presence flag contributes
    /// nothing and the value from the layer below survives. Falling back to 0 on our side skips
    /// that whole mechanism and hands the shader the one number the game never uses.
    ///
    /// It mattered most for CloseFogMaxDistance. The guest's reciprocal guard is
    /// <c>(x > 0) ? 1/x : 1</c> (0x822638AC), so a zero there does not disable the close-fog term —
    /// it makes the reciprocal 1.0, which saturates the near-field colour ramp ONE UNIT from the
    /// camera. The theme's muted CloseFogColour is then never used at any distance and every
    /// fogged pixel takes the sky's in-scatter instead, which is far brighter. That is a fog that
    /// looks violently wrong while its density curve is perfectly correct.
    ///
    /// All seven are seeded by the defaults constructor <c>sub_82A45DD0</c> with presence flag 1.0,
    /// read from the binary rather than guessed:
    /// FoggingStart <c>flt_8209BC58</c> = 1000000, NearDistance <c>flt_82000A54</c> = 10,
    /// Near/FarDensity <c>flt_8209B634</c> = 0.3, FarDistance and CloseFogMaxDistance both
    /// <c>flt_8209BC68</c> = 100, CloseFogColour white.
    ///
    /// APPROXIMATE IN ONE RESPECT, and worth knowing: the guest resolves an unstated field against
    /// the EnvThemeGlobal layer FIRST and only reaches these constructor defaults if that layer is
    /// silent too. We go straight to the defaults, which agrees with the guest wherever
    /// EnvThemeGlobal does not state the field.
    /// </remarks>
    public const float DefaultNearDistance = 10.0f;

    /// <inheritdoc cref="DefaultNearDistance"/>
    public const float DefaultNearDensity = 0.3f;

    /// <inheritdoc cref="DefaultNearDistance"/>
    public const float DefaultFarDistance = 100.0f;

    /// <inheritdoc cref="DefaultNearDistance"/>
    public const float DefaultFarDensity = 0.3f;

    /// <inheritdoc cref="DefaultNearDistance"/>
    public const float DefaultCloseFogMaxDistance = 100.0f;

    /// <summary>
    /// The guest's <c>g_AtmosphericFactors.x</c> — how much of the fog blend is applied.
    /// </summary>
    /// <remarks>
    /// THE GUEST WRITES 1.0, and so do we — this was 0.5 for one turn, to damp a fog that turned
    /// out not to be too thick. The measurement is worth keeping: at the Bowerstone Market theme
    /// the blend fraction is 4% at 100 units and 10% at 200, which is mild, and the picture looked
    /// heavy because <see cref="DomeExposure"/> was inflating the colour being blended toward.
    /// Damping the fog would have hidden that rather than fixed it.
    ///
    /// There are exactly two `stfs` sites for pixel c28 in the whole image
    /// (`device + 0x1780 + 16*28` = `+0x1940`), and between them the register only ever takes
    /// `(1, 1, 0, 0)`, `(1, 0, 0, 0)` or `(0, 0, 0, 0)`:
    ///
    ///   - `sub_82214FB0` builds `{ (float)a1, (float)a2, 0, 0 }` and all nine callers pass a1 = 1
    ///     except one gate.
    ///   - That gate, `sub_82214F10`, passes (0, 0) only when `ctx+0x703` or `ctx+0x704` is set.
    ///     `+0x704` is never written non-zero anywhere; `+0x703` is set only by
    ///     `Render_BloomDownsamplePass_INFERRED` around its own render target. So atmospherics are
    ///     suppressed while filling the bloom source and nowhere else.
    ///   - `.y` drops to 0 on the 2nd..nth pass of a multi-pass material (`drawScope+0x54 != 0`),
    ///     which stops an additive pass re-adding the fog colour it has already added.
    ///
    /// Neither deviation applies to us: Godot owns bloom, and we do not run the guest's multi-pass
    /// material loop. So this is a faithful 1.0 with nothing left to discover.
    /// </remarks>
    public const float FogBlendFactor = 1.0f;

    /// <summary>The guest's <c>g_AtmosphericFactors.y</c> — a scale on the fog colour alone.</summary>
    /// <remarks>
    /// 1.0, which is what the guest writes for a single-pass material. It drops to 0 only on the
    /// repeat passes of a multi-pass one, a loop we do not run.
    /// </remarks>
    public const float FogColourFactor = 1.0f;

    /// <summary>
    /// The scale the sky dome's in-scatter is drawn at, and therefore the one the fog shares.
    /// </summary>
    /// <remarks>
    /// ONE, BECAUSE THE ATMOSPHERE IS NOT EXPOSED SEPARATELY FROM THE WORLD. It was 10, which is
    /// where a whole class of "the lighting looks wrong" came from and is worth writing down.
    ///
    /// The guest builds one linear HDR frame and exposes it once:
    ///
    ///     surface = albedo * light        sky = (1 - T) * InScatter        fog = InScatter
    ///
    /// Nothing multiplies the atmosphere on its own. A gain applied to the dome and the fog but
    /// NOT to surfaces does not brighten the sky — `Engine_ExposureEffect` measures the frame it
    /// is given and normalises it, so the ten went into the sky and the exposure loop took it back
    /// out of everything. The result is the signature of one inflated term under a global
    /// auto-exposure: a blown-out horizon, surfaces two stops too dark, and a fog that washes the
    /// picture at a blend fraction of ten percent because what it is blending toward is an order
    /// of magnitude hotter than what it is blending over.
    ///
    /// It stays as a named constant rather than disappearing because the dome, the water and the
    /// fog all have to carry the SAME value — a surface at MaxFogDistance is replaced by the very
    /// in-scatter the sky is painting behind it, so if they ever diverge the world meets the
    /// horizon at a seam.
    /// </remarks>
    public const float DomeExposure = 1.0f;

    public const float Epsilon = 0.000099999997f;

    public static readonly double[] BetaRayleighChannel =
    {
        0.007337032921211229, 0.009459203185216139, 0.025727610716641394,
    };

    /// <summary>The Mie base, which is CHROMATIC on purpose — a lambda^-2 aerosol term.</summary>
    /// <remarks>
    /// MIE BEING NON-GREY IS NOT A BUG, AND IT LOOKS EXACTLY LIKE ONE. Textbook Mie scattering is
    /// wavelength-independent, so a published blue/red ratio of 1.87 reads as a defect and invites
    /// someone to flatten it to grey. It is authored: normalise both tables to red and they are
    /// 1 : 1.28924 : 3.50652 for Rayleigh and 1 : 1.13545 : 1.87257 for Mie, and
    /// `sqrt(3.50652) = 1.87257` to every digit. This is the lambda^-2 counterpart of the
    /// lambda^-4 table above — ordinary aerosol authoring, not isotropic Mie.
    ///
    /// WHICH TABLE IS WHICH IS PROVEN BY THE PHASE NORMALISATION, and it is worth recording because
    /// the guest only ever SUMS the two vectors, so its own code cannot tell them apart and the
    /// pairing is invisible to a reader of the extinction path. `sub_82263570` at 0x82263900
    /// separates them:
    ///
    ///     82263900  vmulfp128 v10, v126, v13   ; * 0.079577468 = 1/(4pi)   isotropic -> MIE
    ///     82263908  vmulfp128 v9,  v125, v11   ; * 0.059683103 = 3/(16pi)  Rayleigh's own
    ///
    /// 3/(16pi) is the leading coefficient of the Rayleigh phase function, so `v125` — the vector
    /// carrying the x500 scale and the theme's `SKY_BETA_RAYLEIGH_MULTIPLIER` (registrar index 8,
    /// theme block +0x90) — is Rayleigh. `v126` takes `SKY_BETA_MIE_MULTIPLIER` (index 9, +0xA0)
    /// with no extra scale. Swapping them would put a multiplier authored near 1.24 onto the
    /// lambda^-4 table and one near 1.2e-4 onto the aerosol one.
    /// </remarks>
    public static readonly float[] BetaMieChannel =
    {
        0.0056148912f, 0.0063754139f, 0.010514311f,
    };

    public static AtmosphereInputs FromFrame(SkyFrame frame, SkyTheme theme)
    {
        var inputs = new AtmosphereInputs
        {
            SunIntensity = frame.SkyParams[0],
            ComplementaryBias = frame.SkyParams[1],
            BetaRayleighMultiplier = frame.SkyParams[2],
            BetaMieMultiplier = frame.SkyParams[3],
            // A MILLION WHEN UNSTATED, which is the guest's own default and means "never fog".
            // `sub_82A45DD0` constructs the theme with `flt_8209BC58 = 1000000.0` in this slot, so
            // a Fogging record that names distances but no start does NOT start its fog at the
            // camera — it has no fog. Defaulting this to zero instead put the gate behind the near
            // plane and fogged the entire world from the first pixel.
            FoggingStart = theme.HasFoggingStart
                ? MathF.Max(theme.FoggingStart, 0f)
                : UnfoggedStart,

            // NOT `max(x, 0)`. Zero here is "the record did not state it", and the guest's guard
            // turns that into a reciprocal of 1.0 rather than into "no close fog" — see the remark
            // on DefaultNearDistance for why that one substitution wrecks the picture.
            CloseFogMaxDistance = theme.CloseFogMaxDistance > 0f
                ? theme.CloseFogMaxDistance
                : DefaultCloseFogMaxDistance,

            // ALWAYS. The guest has no "is there fog" flag — every theme carries the block, and a
            // theme that states nothing gets a FoggingStart of a million, which is how absence is
            // expressed. Gating on the near and far records instead made our own silence mean
            // something different from the game's.
            HasFog = true,

            // `HasNearFog` and `HasFarFog` are per PAIR rather than per field, so a record stating
            // a distance but not its density takes the default for both. The shipped themes state
            // them together.
            NearDistance = theme.HasNearFog ? theme.NearDistance : DefaultNearDistance,
            NearDensity = theme.HasNearFog ? theme.NearDensity : DefaultNearDensity,
            FarDistance = theme.HasFarFog ? theme.FarDistance : DefaultFarDistance,
            FarDensity = theme.HasFarFog ? theme.FarDensity : DefaultFarDensity,
        };

        // White when unstated, which is the constructor's default for slot 19. Our SkyTheme seeds
        // this array with a blue-grey stand-in that predates the fog work; letting that through
        // would tint the near field with a colour the game never states.
        for (int i = 0; i < 3; i++)
        {
            inputs.CloseFogColour[i] = theme.HasFogColour ? theme.FogColour[i] : 1f;
        }

        for (int i = 0; i < 3; i++)
        {
            inputs.SkyColour[i] = frame.SkyTop[i];
            inputs.ComplementaryColour[i] = frame.SkyBottom[i];
            inputs.SunsetColour[i] = frame.SkySunset[i];
        }

        inputs.SunDirection[0] = frame.SunDirection[0];
        inputs.SunDirection[1] = frame.SunDirection[2];
        inputs.SunDirection[2] = frame.SunDirection[1];
        inputs.SunDirection[3] = 0f;

        return inputs;
    }

    public static AtmosphereState Compute(AtmosphereInputs theme)
    {
        var state = new AtmosphereState();

        double rayleighCommon =
            Math.Pow(BetaRayleighPowBase, BetaRayleighPowExponent) * BetaRayleighCommon;

        var betaRayleighBase = new float[3];
        for (int channel = 0; channel < 3; channel++)
        {
            betaRayleighBase[channel] = (float)(rayleighCommon * BetaRayleighChannel[channel]);
        }

        float rayleighScale = theme.BetaRayleighMultiplier * RayleighMultiplierScale;
        float mieScale = theme.BetaMieMultiplier;
        for (int channel = 0; channel < 3; channel++)
        {
            state.BetaRayleigh[channel] = betaRayleighBase[channel] * rayleighScale;
            state.BetaMie[channel] = BetaMieChannel[channel] * mieScale;
        }

        state.BetaRayleigh[3] = 0f;
        state.BetaMie[3] = 0f;

        for (int i = 0; i < 4; i++)
        {
            state.SunDirection[i] = theme.SunDirection[i];
        }

        state.ScatteringMisc[0] = theme.FoggingStart;
        state.ScatteringMisc[1] = HorizonDistance;
        state.ScatteringMisc[2] =
            theme.CloseFogMaxDistance > 0f ? 1f / theme.CloseFogMaxDistance : 1f;
        state.ScatteringMisc[3] = RecipMaxFogDistance;

        for (int channel = 0; channel < 3; channel++)
        {
            state.LutBetaRayleighPhase[channel] = state.BetaRayleigh[channel] * RayleighPhaseNorm;
            state.LutBetaMiePhase[channel] = state.BetaMie[channel] * MiePhaseNorm;
        }

        state.LutBetaRayleighPhase[3] = 0f;
        state.LutBetaMiePhase[3] = 0f;

        float elevation = SunElevationAngle(theme.SunDirection);
        state.LutCosSunElevation = MathF.Cos(elevation);

        float f10 = elevation * MieGA1;
        float f8 = -f10;
        float f7 = f8 + MieGA0;
        float f9 = MieGB0 - (elevation * MieGB1);
        float f6 = f9 - f7;
        float g = (float)((f6 * 0.5) + f7);

        float gSquared = g * g;
        state.LutHg[0] = (float)(1.0 - gSquared);
        state.LutHg[1] = gSquared + 1f;
        state.LutHg[2] = g * 2f;
        state.LutHg[3] = 0f;

        state.LutScale[0] = 1f;
        state.LutScale[1] = 1f;
        state.LutScale[2] = 1f;
        state.LutScale[3] = theme.SunIntensity;

        float cosElevation = state.LutCosSunElevation;
        state.SunElementColour[0] = ((theme.SunsetColour[0] - 1f) * cosElevation) + 1f;
        state.SunElementColour[1] = ((theme.SunsetColour[1] - 1f) * cosElevation) + 1f;
        state.SunElementColour[2] = theme.SunsetColour[2] * cosElevation;
        state.SunElementColour[3] = 0f;

        for (int i = 0; i < 3; i++)
        {
            state.LutColourA[i] = theme.SkyColour[i];
            state.LutColourB[i] = theme.ComplementaryColour[i];
        }

        state.LutColourA[3] = 0f;
        state.LutColourB[3] = 0f;

        state.LutBiasPlusHalf = theme.ComplementaryBias + 0.5f;

        for (int i = 0; i < 3; i++)
        {
            state.FogCloseColour[i] = theme.CloseFogColour[i];
        }

        SolveFogCurve(theme, state);
        return state;
    }

    /// <summary>
    /// Turns the theme's two (distance, density) control points into the one power law the guest's
    /// fog actually follows.
    /// </summary>
    /// <remarks>
    /// THE TWO DENSITIES ARE NOT A RAMP. Reading them as "this much fog at the near distance,
    /// that much at the far distance, lerp between" is the obvious interpretation and it is wrong
    /// — it cannot even represent the shipped themes, several of which state a near density
    /// HIGHER than the far one.
    ///
    /// <c>sub_821FE040</c>, which bakes the transmittance LUT, is where the real shape is. It
    /// solves an exponent so that a single power law passes through both control points, and what
    /// it is fitting is the INTEGRATED optical depth along the ray, not a per-point density:
    ///
    ///     N  = max(NearDistance - FoggingStart, 1)          ; fsel, 0x821FE10C
    ///     F  = max(FarDistance  - FoggingStart, N + 9)      ; fsel, 0x821FE164
    ///     nD = max(NearDensity, 0.01)                       ; fsel, 0x821FE14C
    ///     fD = max(FarDensity,  0.01)                       ; fsel, 0x821FE174
    ///     A  = fD*(F - N) + nD*N                            ; fnmsubs/fmadds, 0x821FE190/0x821FE198
    ///     k  = log(nD*N / A) / log(N / F)                   ; two log10 calls, 0x821FE1B0/0x821FE1DC
    ///
    ///     opticalDepth(d) = A * (d / F)^k
    ///
    /// so that opticalDepth(N) = nD*N and opticalDepth(F) = A. EVERY DISTANCE IS MEASURED FROM
    /// FoggingStart, including the LUT's own axis — the four subtractions at 0x821FE0A8 and
    /// 0x821FE0BC are the first thing the function does.
    ///
    /// The colour never enters here. Transmittance is <c>exp(-(betaRayleigh + betaMie) * depth)</c>
    /// per channel, evaluated in the shader, which is why fog in this game is the atmosphere's
    /// colour rather than a fog colour: the two coefficient sets differ by about 3.5x between red
    /// and blue, so distance eats blue first and the world converges on the sky's own in-scatter.
    /// <see cref="Engine_SkyEnvironment"/> publishes it; <c>fable_atmosphere.gdshaderinc</c> applies it.
    ///
    /// WE DO NOT BAKE A LUT. The guest's 256-texel 8-bit 1-D texture exists so that Xenos does not
    /// pay a pow and an exp per pixel; the curve above is the contract and the texture is the
    /// accommodation. Evaluating it directly also drops the 8-bit quantisation of the
    /// transmittance, which is visible as banding in a thin fog.
    /// </remarks>
    public static void SolveFogCurve(AtmosphereInputs theme, AtmosphereState state)
    {
        state.HasFog = false;
        if (!theme.HasFog ||
            !float.IsFinite(theme.NearDistance) || !float.IsFinite(theme.FarDistance) ||
            !float.IsFinite(theme.NearDensity) || !float.IsFinite(theme.FarDensity))
        {
            return;
        }

        float near = MathF.Max(theme.NearDistance - theme.FoggingStart, FogNearRangeFloor);
        float far = MathF.Max(theme.FarDistance - theme.FoggingStart, near + FogFarRangeMargin);
        float nearDensity = MathF.Max(theme.NearDensity, FogDensityFloor);
        float farDensity = MathF.Max(theme.FarDensity, FogDensityFloor);

        float depthAtFar = (farDensity * (far - near)) + (nearDensity * near);
        if (!(depthAtFar > 0f))
        {
            return;
        }

        // Both logarithms are safe by construction: `far` is at least `near + 9` so the ratio is
        // strictly under 1 and its log is non-zero, and the numerator is a product of positive
        // floors. The guest takes them base 10; a ratio of logarithms has no base.
        float exponent =
            MathF.Log((nearDensity * near) / depthAtFar) / MathF.Log(near / far);
        if (!float.IsFinite(exponent))
        {
            return;
        }

        state.HasFog = true;
        state.FogOpticalDepthAtFar = depthAtFar;
        state.FogRecipFarRange = 1f / far;
        state.FogExponent = exponent;
    }

    public const int LutWidth = 64;

    public static float[] BuildInScatterLut(AtmosphereState state)
    {
        var outRgba = new float[LutWidth * 4];

        var recip = new float[3];
        for (int channel = 0; channel < 3; channel++)
        {
            recip[channel] = 1f / (state.BetaRayleigh[channel] + state.BetaMie[channel]);
        }

        var colourDelta = new float[3];
        for (int channel = 0; channel < 3; channel++)
        {
            colourDelta[channel] = state.LutColourA[channel] - state.LutColourB[channel];
        }

        float[] rampDelta = { 0f, 0.3f - 1f, 0.1f - 1f };

        for (int i = 0; i < LutWidth; i++)
        {
            float t = i / (float)LutWidth;
            float onePlusT2 = (t * t) + 1f;
            float hgDenominator = MathF.Pow(
                state.LutHg[1] - (state.LutHg[2] * t), 1.5f);
            float tCos = t * state.LutCosSunElevation;
            float hgRatio = state.LutHg[0] / hgDenominator;

            float gated = (t * 0.5f) + state.LutBiasPlusHalf < 0f ? 0f : hgRatio;
            float colourFactor = gated < 1f ? gated : 1f;

            for (int channel = 0; channel < 3; channel++)
            {
                float phase =
                    (state.LutBetaMiePhase[channel] * hgRatio) +
                    (state.LutBetaRayleighPhase[channel] * onePlusT2);
                float ramp = (rampDelta[channel] * tCos) + 1f;
                float colour =
                    (colourDelta[channel] * colourFactor) + state.LutColourB[channel];
                float scaled = ramp * state.LutScale[channel] * state.LutScale[3];
                outRgba[(i * 4) + channel] =
                    scaled * (phase * recip[channel]) * colour;
            }

            outRgba[(i * 4) + 3] = 0f;
        }

        return outRgba;
    }

    // `EvaluateDomeDisplay` used to sit here: a host-side evaluation of the dome for one direction,
    // with its own copy of the horizon-distance curve, its own extinction and its own hardcoded
    // exposure of 10 followed by a Reinhard of its own. It had no callers anywhere in the tree, and
    // it was a THIRD implementation of arithmetic that `sky_dome.gdshaderinc` already warns about
    // carrying twice. Deleted rather than updated alongside DomeExposure — a dead copy that
    // disagrees with the live one is worse than no copy at all.

    public static float SunElevationAngle(float[] sunDirection)
    {
        var negated = new[] { -sunDirection[0], -sunDirection[1], -sunDirection[2] };
        var horizontal = new[] { negated[0], negated[1], 0f };
        return VectorAngle(negated, horizontal);
    }

    private static float VectorAngle(float[] a, float[] b)
    {
        float lengthProduct = MathF.Sqrt(Dot3(a, a)) * MathF.Sqrt(Dot3(b, b));
        if (lengthProduct <= Epsilon)
        {
            return 0f;
        }

        float cosine = Dot3(a, b) / lengthProduct;
        if (MathF.Abs(cosine - 1f) < Epsilon)
        {
            return 0f;
        }

        if (MathF.Abs(cosine + 1f) >= Epsilon)
        {
            return MathF.Acos(cosine);
        }

        return 3.1415927f;
    }

    private static float Dot3(float[] a, float[] b) =>
        (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);
}
