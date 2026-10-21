namespace Refii.Tests.Lighting;

using System;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Launcher.Core;
using Refii.Lighting;

[TestSuite]
public class ShadowReachTests
{
    [TestCase]
    public void TestSunShadowReachScaleCVarIsRegistered()
    {
        var def = RuntimeCVarsGodot.Definitions.FirstOrDefault(d => d.Name == "sun_shadow_reach_scale");
        AssertThat(def).IsNotNull();
        AssertThat(def!.Category).IsEqual(RuntimeCVarsGodot.LightingCategory);
        AssertThat(def.DefaultValue).IsEqual("1");
    }

    [TestCase]
    public void TestTieredShadowReachCalculation()
    {
        // Default scale = 1.0
        // Clutter (< 0.6m) -> 18m
        AssertThat(Engine_ShadowReach.CalculateShadowReach(0.2f, 1.0f)).IsEqual(18.0f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(0.59f, 1.0f)).IsEqual(18.0f);

        // Small props (< 1.5m) -> 30m
        AssertThat(Engine_ShadowReach.CalculateShadowReach(0.6f, 1.0f)).IsEqual(30.0f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(1.0f, 1.0f)).IsEqual(30.0f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(1.49f, 1.0f)).IsEqual(30.0f);

        // Medium dressing (< 3.5m) -> 55m
        AssertThat(Engine_ShadowReach.CalculateShadowReach(1.5f, 1.0f)).IsEqual(55.0f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(2.5f, 1.0f)).IsEqual(55.0f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(3.49f, 1.0f)).IsEqual(55.0f);

        // Architecture (< 8.0m) -> 95m
        AssertThat(Engine_ShadowReach.CalculateShadowReach(3.5f, 1.0f)).IsEqual(95.0f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(6.0f, 1.0f)).IsEqual(95.0f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(7.99f, 1.0f)).IsEqual(95.0f);

        // Landscape, cliffs, terrain, large trees, clocktower (>= 8.0m) -> PositiveInfinity
        AssertThat(float.IsPositiveInfinity(Engine_ShadowReach.CalculateShadowReach(8.0f, 1.0f))).IsTrue();
        AssertThat(float.IsPositiveInfinity(Engine_ShadowReach.CalculateShadowReach(20.0f, 1.0f))).IsTrue();
        AssertThat(float.IsPositiveInfinity(Engine_ShadowReach.CalculateShadowReach(100.0f, 1.0f))).IsTrue();
    }

    [TestCase]
    public void TestShadowReachCalculationDynamicScaling()
    {
        // Scale = 1.5
        AssertThat(Engine_ShadowReach.CalculateShadowReach(0.3f, 1.5f)).IsEqual(27.0f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(1.0f, 1.5f)).IsEqual(45.0f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(2.0f, 1.5f)).IsEqual(82.5f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(5.0f, 1.5f)).IsEqual(142.5f);
        AssertThat(float.IsPositiveInfinity(Engine_ShadowReach.CalculateShadowReach(12.0f, 1.5f))).IsTrue();

        // Scale = 0.5
        AssertThat(Engine_ShadowReach.CalculateShadowReach(0.3f, 0.5f)).IsEqual(9.0f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(1.0f, 0.5f)).IsEqual(15.0f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(2.0f, 0.5f)).IsEqual(27.5f);
        AssertThat(Engine_ShadowReach.CalculateShadowReach(5.0f, 0.5f)).IsEqual(47.5f);
        AssertThat(float.IsPositiveInfinity(Engine_ShadowReach.CalculateShadowReach(12.0f, 0.5f))).IsTrue();

        // Scale <= 0 disables reach limits (all objects cast to cascade max)
        AssertThat(float.IsPositiveInfinity(Engine_ShadowReach.CalculateShadowReach(0.3f, 0.0f))).IsTrue();
        AssertThat(float.IsPositiveInfinity(Engine_ShadowReach.CalculateShadowReach(1.0f, -1.0f))).IsTrue();
    }
}
