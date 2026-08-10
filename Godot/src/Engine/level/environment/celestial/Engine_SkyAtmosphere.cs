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

    public const float Epsilon = 0.000099999997f;

    public static readonly double[] BetaRayleighChannel =
    {
        0.007337032921211229, 0.009459203185216139, 0.025727610716641394,
    };

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
            FoggingStart = theme.HasFoggingStart ? MathF.Max(theme.FoggingStart, 0f) : 0f,
            CloseFogMaxDistance = MathF.Max(theme.CloseFogMaxDistance, 0f),
        };

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
        return state;
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

    public static float[] EvaluateDomeDisplay(
        AtmosphereState state, float[] lut, float[] frameSunDirection,
        float gx, float gy, float gz)
    {
        float nx = gx;
        float ny = gz;
        float nz = gy;
        float len = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (len > 1e-6f)
        {
            nx /= len;
            ny /= len;
            nz /= len;
        }

        float sx = -frameSunDirection[0];
        float sy = frameSunDirection[2];
        float sz = frameSunDirection[1];
        float slen = MathF.Sqrt((sx * sx) + (sy * sy) + (sz * sz));
        if (slen > 1e-6f)
        {
            sx /= slen;
            sy /= slen;
            sz /= slen;
        }

        float sunDot = Math.Clamp((sx * nx) + (sy * ny) + (sz * nz), 0f, 1f);
        float u = sunDot * (LutWidth - 1);
        int i0 = Math.Clamp((int)u, 0, LutWidth - 1);
        int i1 = Math.Min(i0 + 1, LutWidth - 1);
        float t = u - i0;

        float up = MathF.Max(MathF.Abs(nz), 0.0001f);
        float upPow = MathF.Pow(up, 0.2f);
        float dist = HorizonDistance * (1.05f + upPow) * (1f + upPow);

        var display = new float[3];
        for (int c = 0; c < 3; c++)
        {
            float scatter = (lut[(i0 * 4) + c] * (1f - t)) + (lut[(i1 * 4) + c] * t);
            float tau = (state.BetaRayleigh[c] + state.BetaMie[c]) * dist;
            float extinction = MathF.Pow(2f, -tau * 1.4427f);
            float hdr = (1f - extinction) * scatter * 10f;
            display[c] = hdr / (1f + hdr);
        }

        return display;
    }

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
