using System;

namespace Refii.Core;

/// <summary>A copied RGB transmittance value without mutable buffers or engine objects.</summary>
internal readonly struct NearFogRgb
{
    /// <summary>The red transmittance.</summary>
    internal readonly float Red;
    /// <summary>The green transmittance.</summary>
    internal readonly float Green;
    /// <summary>The blue transmittance.</summary>
    internal readonly float Blue;

    /// <summary>Copies three channel values into an immutable endpoint.</summary>
    /// <param name="red">The red transmittance.</param>
    /// <param name="green">The green transmittance.</param>
    /// <param name="blue">The blue transmittance.</param>
    internal NearFogRgb(float red, float green, float blue) => (Red, Green, Blue) = (red, green, blue);
}

/// <summary>Owns one atmosphere update's five immutable near-field endpoints and readiness.</summary>
internal readonly struct NearFogSnapshot
{
    /// <summary>Whether the endpoints are safe to use instead of the original shader expressions.</summary>
    internal readonly bool Ready;
    private readonly NearFogRgb _zero, _one, _two, _three, _four;

    /// <summary>Copies all endpoints so a later atmosphere update cannot mutate an earlier publication.</summary>
    /// <param name="ready">Whether all five endpoints can replace the analytic near path.</param>
    /// <param name="zero">The zero-distance endpoint.</param>
    /// <param name="one">The first spaced endpoint.</param>
    /// <param name="two">The second spaced endpoint.</param>
    /// <param name="three">The third spaced endpoint.</param>
    /// <param name="four">The last endpoint at the near/far transition.</param>
    internal NearFogSnapshot(bool ready, NearFogRgb zero, NearFogRgb one, NearFogRgb two, NearFogRgb three, NearFogRgb four)
        => (Ready, _zero, _one, _two, _three, _four) = (ready, zero, one, two, three, four);

    /// <summary>Returns one endpoint by value; only the shader's five exact knot indices are accepted.</summary>
    /// <param name="index">The endpoint index from zero through four.</param>
    /// <returns>A copied RGB endpoint.</returns>
    internal NearFogRgb At(int index) => index switch
    {
        0 => _zero, 1 => _one, 2 => _two, 3 => _three, 4 => _four,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

/// <summary>Precomputes only the existing shader's five RGB near-fog endpoints.</summary>
/// <remarks>The analytic far curve and all unsupported-state behavior remain in the original shader.
/// CPU MathF and GPU transcendental functions are not promised bitwise equal; native float readback
/// fixtures establish the numerical tolerance before this path is enabled on a target GPU.</remarks>
internal static class Engine_NearFogKnots
{
    /// <summary>The guest's unchanged endpoint spacing, exactly representable in binary float.</summary>
    internal const float Spacing = 7.8125f;
    /// <summary>The shader readiness global; false always selects the original expressions.</summary>
    internal const string ReadyGlobal = "atmos_near_ready";

    /// <summary>Returns the registered name of one exact endpoint without allocating a mutable name array.</summary>
    /// <param name="index">The endpoint index from zero through four.</param>
    /// <returns>The name registered in project.godot and consumed by the atmosphere shader.</returns>
    internal static string GlobalName(int index) => index switch
    {
        0 => "atmos_near_t0", 1 => "atmos_near_t1", 2 => "atmos_near_t2",
        3 => "atmos_near_t3", 4 => "atmos_near_t4",
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>Computes the same clamped-distance power and RGB exponentials at the five fixed endpoints.</summary>
    /// <param name="hasFog">Whether the published atmosphere enables fog.</param>
    /// <param name="redBeta">The same copied red Rayleigh-plus-Mie coefficient published to the shader.</param>
    /// <param name="greenBeta">The same copied green coefficient.</param>
    /// <param name="blueBeta">The same copied blue coefficient.</param>
    /// <param name="depthAtFar">The published optical depth at the far control point.</param>
    /// <param name="reciprocalFar">The published reciprocal far control range.</param>
    /// <param name="exponent">The published power exponent; zero and negative values retain the original path.</param>
    /// <param name="maximumDistance">The same distance clamp published in atmos_range.y.</param>
    /// <returns>A copied ready snapshot, or harmless unit endpoints with readiness false for fallback.</returns>
    internal static NearFogSnapshot Create(bool hasFog, float redBeta, float greenBeta, float blueBeta,
        float depthAtFar, float reciprocalFar, float exponent, float maximumDistance)
    {
        if (!hasFog || !Nonnegative(redBeta) || !Nonnegative(greenBeta) || !Nonnegative(blueBeta) ||
            !Nonnegative(depthAtFar) || !Nonnegative(reciprocalFar) || !Nonnegative(maximumDistance) ||
            !float.IsFinite(exponent) || exponent <= 0f)
            return Disabled();
        NearFogRgb Evaluate(int index)
        {
            float depth = depthAtFar * MathF.Pow(MathF.Min(index * Spacing, maximumDistance) * reciprocalFar, exponent);
            if (!float.IsFinite(depth)) return new(float.NaN, float.NaN, float.NaN);
            return new(MathF.Exp(-redBeta * depth), MathF.Exp(-greenBeta * depth), MathF.Exp(-blueBeta * depth));
        }
        NearFogRgb zero = Evaluate(0), one = Evaluate(1), two = Evaluate(2), three = Evaluate(3), four = Evaluate(4);
        if (!Finite(zero) || !Finite(one) || !Finite(two) || !Finite(three) || !Finite(four)) return Disabled();
        return new(true, zero, one, two, three, four);
    }

    /// <summary>Publishes a complete endpoint set with readiness false first and enabled only after all copies succeed.</summary>
    /// <param name="snapshot">The same immutable state used for this atmosphere update.</param>
    /// <param name="publishRgb">Main-thread publisher of copied vector channels.</param>
    /// <param name="publishBoolean">Main-thread readiness publisher; a failed endpoint write leaves fallback selected.</param>
    internal static void Publish(NearFogSnapshot snapshot, Action<string, NearFogRgb> publishRgb, Action<string, bool> publishBoolean)
    {
        ArgumentNullException.ThrowIfNull(publishRgb);
        ArgumentNullException.ThrowIfNull(publishBoolean);
        publishBoolean(ReadyGlobal, false);
        for (int i = 0; i < 5; i++) publishRgb(GlobalName(i), snapshot.At(i));
        publishBoolean(ReadyGlobal, snapshot.Ready);
    }

    private static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0f;
    private static bool Finite(NearFogRgb value) => float.IsFinite(value.Red) && float.IsFinite(value.Green) && float.IsFinite(value.Blue);
    private static NearFogSnapshot Disabled()
    {
        var unit = new NearFogRgb(1f, 1f, 1f);
        return new(false, unit, unit, unit, unit, unit);
    }
}
