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
