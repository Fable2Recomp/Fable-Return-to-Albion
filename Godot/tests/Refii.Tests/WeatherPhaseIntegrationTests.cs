using System;
using System.IO;
using System.Text.RegularExpressions;
using GdUnit4;
using Godot;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// Headless unit and contract tests for host-integrated weather and mist phases (issue #93).
/// </summary>
[TestSuite]
public sealed class WeatherPhaseIntegrationTests
{
    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "src", "Engine", "level")))
            {
                return dir;
            }

            string candidate = Path.Combine(dir, "Godot");
            if (Directory.Exists(Path.Combine(candidate, "src", "Engine", "level")))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate Godot/src/Engine/level from test base directory.");
    }

    private static string StripShaderComments(string source)
    {
        string withoutBlock = Regex.Replace(source, @"/\*[\s\S]*?\*/", string.Empty);
        return Regex.Replace(withoutBlock, @"//[^\r\n]*", string.Empty);
    }

    [TestCase]
    public void PhaseIntegratorSanitizesAndClampsDeltas()
    {
        AssertThat(Engine_PhaseIntegrator.SanitizeDelta(double.NaN)).IsEqual(0.0);
        AssertThat(Engine_PhaseIntegrator.SanitizeDelta(double.PositiveInfinity)).IsEqual(0.0);
        AssertThat(Engine_PhaseIntegrator.SanitizeDelta(double.NegativeInfinity)).IsEqual(0.0);
        AssertThat(Engine_PhaseIntegrator.SanitizeDelta(-0.016)).IsEqual(0.0);
        AssertThat(Engine_PhaseIntegrator.SanitizeDelta(0.0)).IsEqual(0.0);

        double normal = 1.0 / 60.0;
        AssertThat(Math.Abs(Engine_PhaseIntegrator.SanitizeDelta(normal) - normal)).IsLess(1e-12);
        AssertThat(Engine_PhaseIntegrator.SanitizeDelta(5.0)).IsEqual(Engine_PhaseIntegrator.MaxStepSeconds);
    }

    [TestCase]
    public void WrappedUnitAndPeriodicWrapCleanly()
    {
        double phase = 0.0;
        for (int i = 0; i < 500; i++)
        {
            phase = Engine_PhaseIntegrator.AdvanceWrappedUnit(phase, 0.37, 0.05);
            AssertThat(phase >= 0.0 && phase < 1.0).IsTrue();
        }

        double negPhase = 0.1;
        for (int i = 0; i < 200; i++)
        {
            negPhase = Engine_PhaseIntegrator.AdvanceWrappedUnit(negPhase, -0.41, 0.05);
            AssertThat(negPhase >= 0.0 && negPhase < 1.0).IsTrue();
        }

        double beforeWrap = WeatherMotionState.SwayPeriod - 0.005;
        double unwrapped = beforeWrap + 0.01;
        double wrapped = Engine_PhaseIntegrator.AdvancePeriodic(beforeWrap, 0.01, WeatherMotionState.SwayPeriod);
        AssertThat(wrapped >= 0.0 && wrapped < WeatherMotionState.SwayPeriod).IsTrue();
        AssertThat(Math.Abs(Math.Sin(0.9 * unwrapped) - Math.Sin(0.9 * wrapped))).IsLess(1e-6);
        AssertThat(Math.Abs(Math.Cos(0.7 * unwrapped) - Math.Cos(0.7 * wrapped))).IsLess(1e-6);
    }

    [TestCase]
    public void SnowSpeedBlendDoesNotFastForwardAfterLongSession()
    {
        foreach (double dt in new[] { 1.0 / 30.0, 1.0 / 60.0 })
        {
            var state = new WeatherMotionState();
            Vector4 area = Engine_Weather.DefaultArea;
            var wind = new Vector2(1.5f, -0.5f);
            float snowH = area.W;

            // Simulate 1,500 seconds of steady snow (25 minutes uptime).
            int warmupFrames = (int)Math.Round(1500.0 / dt);
            for (int i = 0; i < warmupFrames; i++)
            {
                state.Advance(dt, Engine_Weather.DefaultRainSpeed, 1.0f, wind, area, false, true);
            }

            // Simulate a 5-second painted theme blend ramping SnowFallSpeed 1.0 -> 0.0 -> 1.0.
            int blendFrames = (int)Math.Round(5.0 / dt);
            double maxExpectedStep = (1.0 * dt / snowH) + 1e-6;
            for (int step = 0; step < blendFrames * 2; step++)
            {
                float t = step < blendFrames
                    ? 1f - (step / (float)blendFrames)
                    : (step - blendFrames) / (float)blendFrames;
                float clampedSpeed = MathF.Max(t, Engine_Weather.MinSnowSpeed);

                double prev = state.SnowFallPhase;
                bool moved = state.Advance(dt, Engine_Weather.DefaultRainSpeed, clampedSpeed, wind, area, false, true);
                AssertThat(moved).IsTrue();

                double deltaPhase = state.SnowFallPhase - prev;
                if (deltaPhase < 0.0)
                {
                    deltaPhase += 1.0;
                }

                AssertThat(deltaPhase >= 0.0).IsTrue();
                AssertThat(deltaPhase <= maxExpectedStep).IsTrue();
            }
        }
    }

    [TestCase]
    public void PausedOrInactiveWeatherDoesNotAdvance()
    {
        var state = new WeatherMotionState
        {
            RainFallPhase = 0.25,
            SnowFallPhase = 0.5,
            SnowDriftX = 0.1,
            SnowDriftZ = 0.2,
            SwayTime = 1.25,
        };

        bool pausedMoved = state.Advance(0.0, 14f, 1f, new Vector2(2f, 3f), Engine_Weather.DefaultArea, true, true);
        AssertThat(pausedMoved).IsFalse();
        AssertThat(state.RainFallPhase).IsEqual(0.25);
        AssertThat(state.SnowFallPhase).IsEqual(0.5);
        AssertThat(state.SwayTime).IsEqual(1.25);

        bool inactiveMoved = state.Advance(0.016, 14f, 1f, new Vector2(2f, 3f), Engine_Weather.DefaultArea, false, false);
        AssertThat(inactiveMoved).IsFalse();
        AssertThat(state.RainFallPhase).IsEqual(0.25);
        AssertThat(state.SnowFallPhase).IsEqual(0.5);

        // Snow-only mode leaves RainFallPhase untouched.
        bool snowOnlyMoved = state.Advance(0.05, 14f, 1f, new Vector2(2f, 3f), Engine_Weather.DefaultArea, false, true);
        AssertThat(snowOnlyMoved).IsTrue();
        AssertThat(state.RainFallPhase).IsEqual(0.25);
        AssertThat(state.SnowFallPhase).IsNotEqual(0.5);
    }

    [TestCase]
    public void LegacyWeatherSuppressedUnderClassicOrDynamicSky()
    {
        for (int mask = 0; mask < 8; mask++)
        {
            bool classic = (mask & 1) != 0;
            bool dynamicSky = (mask & 2) != 0;
            bool hideGameWeather = (mask & 4) != 0;
            bool expected = classic || (dynamicSky && hideGameWeather);

            AssertThat(WeatherMotionState.ShouldSuppressLegacyWeather(classic, dynamicSky, hideGameWeather))
                .IsEqual(expected);
        }
    }

    [TestCase]
    public void DynamicPrecipitationSpeedChangeHasNoDiscontinuity()
    {
        var motion = new DynamicPrecipitationMotionState();
        var wind = new Vector3(1.2f, 0f, -0.4f);
        double dt = 1.0 / 60.0;

        for (int i = 0; i < 60 * 1200; i++)
        {
            motion.Advance(dt, wind, 16f, 0.9f);
        }

        double prevRain = motion.RainFallOffset;
        double prevSnow = motion.SnowFallOffset;
        float newRainSpeed = 6.0f;
        float newSnowSpeed = 2.5f;

        AssertThat(motion.Advance(dt, wind, newRainSpeed, newSnowSpeed)).IsTrue();
        AssertThat(Math.Abs((motion.RainFallOffset - prevRain) - (newRainSpeed * dt))).IsLess(1e-9);
        AssertThat(Math.Abs((motion.SnowFallOffset - prevSnow) - (newSnowSpeed * dt))).IsLess(1e-9);

        double pausedRain = motion.RainFallOffset;
        double pausedSnow = motion.SnowFallOffset;
        AssertThat(motion.Advance(0.0, wind, newRainSpeed, newSnowSpeed)).IsFalse();
        AssertThat(motion.RainFallOffset).IsEqual(pausedRain);
        AssertThat(motion.SnowFallOffset).IsEqual(pausedSnow);
    }

    [TestCase]
    public void WeatherAndMistShadersAvoidRawTimeAndKeepDxilSafety()
    {
        string godotRoot = FindRepoRoot();

        string weatherCode = StripShaderComments(File.ReadAllText(
            Path.Combine(godotRoot, "src", "Engine", "level", "environment", "weather.gdshader")));
        AssertThat(Regex.IsMatch(weatherCode, @"\bTIME\b")).IsFalse();
        AssertThat(weatherCode.Contains("weather_phase", StringComparison.Ordinal)).IsTrue();
        AssertThat(weatherCode.Contains("weather_time", StringComparison.Ordinal)).IsTrue();
        AssertThat(weatherCode.Contains("if (!should_discard)", StringComparison.Ordinal)).IsTrue();
        AssertThat(Regex.IsMatch(weatherCode, @"\bdiscard\s*;")).IsFalse();

        string dynamicRainCode = StripShaderComments(File.ReadAllText(
            Path.Combine(godotRoot, "src", "Engine", "level", "environment", "dynamic", "dynamic_rain.gdshader")));
        AssertThat(Regex.IsMatch(dynamicRainCode, @"\bTIME\b")).IsFalse();
        AssertThat(dynamicRainCode.Contains("motion_time", StringComparison.Ordinal)).IsTrue();

        string dynamicPrecipCode = StripShaderComments(File.ReadAllText(
            Path.Combine(godotRoot, "src", "Engine", "level", "environment", "dynamic", "dynamic_precip.gdshaderinc")));
        AssertThat(dynamicPrecipCode.Contains("fall_offset", StringComparison.Ordinal)).IsTrue();

        string modernMistCode = StripShaderComments(File.ReadAllText(
            Path.Combine(godotRoot, "src", "Engine", "level", "environment", "modern_mist.gdshader")));
        AssertThat(Regex.IsMatch(modernMistCode, @"\bTIME\b")).IsFalse();
        AssertThat(modernMistCode.Contains("noise_phase", StringComparison.Ordinal)).IsTrue();
    }
}
