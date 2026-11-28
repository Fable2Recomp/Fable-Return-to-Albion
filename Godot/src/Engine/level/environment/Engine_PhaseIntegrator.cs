using System;
using Godot;

namespace Refii.Level;

/// <summary>
/// Shared, allocation-free host integrators for periodic and linear shader phases.
/// Integrating instantaneous guest rates on the host prevents <c>TIME * d(speed)/dt</c>
/// fast-forward spikes, preserves precision over multi-hour sessions, and follows
/// <see cref="Refii.Core.Engine_DrawAdvance"/> pause/pace scaling.
/// </summary>
public static class Engine_PhaseIntegrator
{
    /// <summary>Caps single-frame hitch deltas so particles never teleport across the view.</summary>
    public const double MaxStepSeconds = 0.25;

    /// <summary>Rejects non-finite/non-positive deltas and clamps hitches to <see cref="MaxStepSeconds"/>.</summary>
    public static double SanitizeDelta(double delta)
    {
        if (!double.IsFinite(delta) || delta <= 0.0)
        {
            return 0.0;
        }

        return delta > MaxStepSeconds ? MaxStepSeconds : delta;
    }

    /// <summary>Advances a periodic unit phase in <c>[0, 1)</c> in double precision.</summary>
    public static double AdvanceWrappedUnit(double phase, double ratePerSecond, double dt)
    {
        if (dt <= 0.0 || !double.IsFinite(ratePerSecond) || ratePerSecond == 0.0)
        {
            return phase;
        }

        double next = phase + (ratePerSecond * dt);
        return next - Math.Floor(next);
    }

    /// <summary>Advances a bounded clock in <c>[0, period)</c>.</summary>
    public static double AdvancePeriodic(double clock, double dt, double period)
    {
        if (dt <= 0.0 || !double.IsFinite(period) || period <= 0.0)
        {
            return clock;
        }

        double next = (clock + dt) % period;
        return next < 0.0 ? next + period : next;
    }

    /// <summary>Advances an unbounded linear offset in double precision.</summary>
    public static double AdvanceLinear(double offset, double speed, double dt)
    {
        if (dt <= 0.0 || !double.IsFinite(speed) || speed == 0.0)
        {
            return offset;
        }

        return offset + (speed * dt);
    }
}

/// <summary>
/// Host-integrated phases for <c>weather.gdshader</c> (<see cref="Engine_WeatherNode"/>).
/// </summary>
public struct WeatherMotionState
{
    /// <summary>
    /// Exact common period of both snow sway frequencies in <c>weather.gdshader</c>:
    /// <c>0.9 * 20*PI = 18*PI = 9 * 2*PI</c> and <c>0.7 * 20*PI = 14*PI = 7 * 2*PI</c>.
    /// </summary>
    public const double SwayPeriod = 20.0 * Math.PI;

    public double RainFallPhase;
    public double SnowFallPhase;
    public double SnowDriftX;
    public double SnowDriftZ;
    public double SwayTime;

    public readonly Vector4 PhaseVector =>
        new((float)RainFallPhase, (float)SnowFallPhase, (float)SnowDriftX, (float)SnowDriftZ);

    public readonly float SwayClock => (float)SwayTime;

    /// <summary>
    /// Authoritative gate for whether the legacy <see cref="Engine_WeatherNode"/> must stay hidden
    /// (Classic renderer draws its own snow/rain, or Dynamic Sky hides legacy weather).
    /// </summary>
    public static bool ShouldSuppressLegacyWeather(
        bool classicActive, bool dynamicSkyEnabled, bool hideGameWeather) =>
        classicActive || (dynamicSkyEnabled && hideGameWeather);

    /// <summary>Advances active weather phases over <paramref name="worldDelta"/>; returns true if anything moved.</summary>
    public bool Advance(
        double worldDelta,
        float rainSpeed,
        float snowSpeed,
        Vector2 wind,
        Vector4 area,
        bool rainActive,
        bool snowActive)
    {
        double dt = Engine_PhaseIntegrator.SanitizeDelta(worldDelta);
        if (dt <= 0.0 || (!rainActive && !snowActive))
        {
            return false;
        }

        if (rainActive)
        {
            double rainH = Math.Max(area.Y, 1.0);
            double clampedRainSpeed = Math.Max(rainSpeed, Engine_Weather.MinRainSpeed);
            RainFallPhase = Engine_PhaseIntegrator.AdvanceWrappedUnit(
                RainFallPhase, clampedRainSpeed / rainH, dt);
        }

        if (snowActive)
        {
            double snowR = Math.Max(area.Z, 0.5);
            double snowH = Math.Max(area.W, 1.0);
            double clampedSnowSpeed = Math.Max(snowSpeed, Engine_Weather.MinSnowSpeed);
            SnowFallPhase = Engine_PhaseIntegrator.AdvanceWrappedUnit(
                SnowFallPhase, clampedSnowSpeed / snowH, dt);

            double driftDenom = Math.Max(2.0 * snowR, 1.0);
            SnowDriftX = Engine_PhaseIntegrator.AdvanceWrappedUnit(
                SnowDriftX, wind.X * Engine_Weather.SnowWindDriftScale / driftDenom, dt);
            SnowDriftZ = Engine_PhaseIntegrator.AdvanceWrappedUnit(
                SnowDriftZ, wind.Y * Engine_Weather.SnowWindDriftScale / driftDenom, dt);
            SwayTime = Engine_PhaseIntegrator.AdvancePeriodic(SwayTime, dt, SwayPeriod);
        }

        return true;
    }
}

/// <summary>
/// Host-integrated motion state for <see cref="Engine_DynamicPrecipitation"/> and
/// <c>dynamic_precip.gdshaderinc</c>.
/// </summary>
public struct DynamicPrecipitationMotionState
{
    public double MotionTime;
    public double WindX;
    public double WindZ;
    public double SnowWindX;
    public double SnowWindZ;
    public double RainFallOffset;
    public double SnowFallOffset;

    public readonly Vector2 RainWindOffset => new((float)WindX, (float)WindZ);

    public readonly Vector2 SnowWindOffset => new((float)SnowWindX, (float)SnowWindZ);

    /// <summary>Advances motion time, wind drift, and vertical fall offsets over <paramref name="worldDelta"/>.</summary>
    public bool Advance(double worldDelta, Vector3 wind, float rainSpeed, float snowSpeed)
    {
        double dt = Engine_PhaseIntegrator.SanitizeDelta(worldDelta);
        if (dt <= 0.0)
        {
            return false;
        }

        WindX = Engine_PhaseIntegrator.AdvanceLinear(WindX, wind.X, dt);
        WindZ = Engine_PhaseIntegrator.AdvanceLinear(WindZ, wind.Z, dt);
        MotionTime = Engine_PhaseIntegrator.AdvanceLinear(MotionTime, 1.0, dt);
        double gust = 1.0 + 0.35 * Math.Sin(MotionTime * 0.43) + 0.15 * Math.Sin(MotionTime * 1.13);
        SnowWindX = Engine_PhaseIntegrator.AdvanceLinear(SnowWindX, wind.X * gust, dt);
        SnowWindZ = Engine_PhaseIntegrator.AdvanceLinear(SnowWindZ, wind.Z * gust, dt);
        RainFallOffset = Engine_PhaseIntegrator.AdvanceLinear(RainFallOffset, Math.Max(rainSpeed, 0.1f), dt);
        SnowFallOffset = Engine_PhaseIntegrator.AdvanceLinear(SnowFallOffset, Math.Max(snowSpeed, 0.05f), dt);
        return true;
    }
}
