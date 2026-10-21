namespace Refii.Tests.Level;

using System;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Launcher.Core;
using Refii.Level;

[TestSuite]
public class PropVisibilityCullTests
{
    [TestCase]
    public void TestPropCullDistanceScaleCVarIsRegistered()
    {
        var def = RuntimeCVarsGodot.Definitions.FirstOrDefault(d => d.Name == "prop_cull_distance_scale");
        AssertThat(def).IsNotNull();
        AssertThat(def!.Category).IsEqual(RuntimeCVarsGodot.DisplayCategory);
        AssertThat(def.DefaultValue).IsEqual("1");
    }

    [TestCase]
    public void TestBuildingPathDetectionExemptsArchitecturalModels()
    {
        // Real building shells and major structures
        AssertThat(Engine_PropStreamer.IsBuildingPath("art/environment/regions/bowerstone/buildings/dotxsi/bs_market_tavern/exterior.mdl", 12f)).IsTrue();
        AssertThat(Engine_PropStreamer.IsBuildingPath("art/environment/regions/bowerstone/structures/dotxsi/bs_market_clocktower/clocktower.mdl", 25f)).IsTrue();
        AssertThat(Engine_PropStreamer.IsBuildingPath("art/architecture/bridges/stone_bridge.mdl", 15f)).IsTrue();
        AssertThat(Engine_PropStreamer.IsBuildingPath("art/environment/regions/bowerstone/buildings/dotxsi/bs_market_tavern/interior.mdl", 10f)).IsTrue();
        AssertThat(Engine_PropStreamer.IsBuildingPath("art/environment/regions/bowerstone/buildings/gatehouse.mdl", 14f)).IsTrue();

        // Dressing models under 2.5m must NEVER be exempt, even if located under Buildings/ or Structures/ in the art tree
        AssertThat(Engine_PropStreamer.IsBuildingPath("art/environment/regions/bowerstone/buildings/dotxsi/bs_market_generalshop_canopy_front/bs_market_generalshop_canopy_front.mdl", 1.8f)).IsFalse();
        AssertThat(Engine_PropStreamer.IsBuildingPath("art/environment/regions/bowerstone/buildings/dotxsi/bs_market_generalshop_counter/bs_market_generalshop_counter.mdl", 1.2f)).IsFalse();
        AssertThat(Engine_PropStreamer.IsBuildingPath("art/environment/dressing/props/wooden_barrel.mdl", 0.8f)).IsFalse();
        AssertThat(Engine_PropStreamer.IsBuildingPath("art/environment/dressing/props/market_crate.mdl", 1.0f)).IsFalse();
        AssertThat(Engine_PropStreamer.IsBuildingPath("art/environment/dressing/props/tankard.mdl", 0.2f)).IsFalse();
        AssertThat(Engine_PropStreamer.IsBuildingPath("", 1.0f)).IsFalse();
    }

    [TestCase]
    public void TestSmallPropTieredDistanceCullingCalculation()
    {
        // Default scale = 1.0
        // Extent < 0.5m -> 15m
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(0.2f, 1.0f)).IsEqual(15.0f);
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(0.49f, 1.0f)).IsEqual(15.0f);

        // Extent < 1.0m -> 20m
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(0.5f, 1.0f)).IsEqual(20.0f);
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(0.8f, 1.0f)).IsEqual(20.0f);

        // Extent < 1.5m -> 30m
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(1.0f, 1.0f)).IsEqual(30.0f);
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(1.2f, 1.0f)).IsEqual(30.0f);

        // Extent < 2.5m -> 40m
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(1.5f, 1.0f)).IsEqual(40.0f);
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(2.0f, 1.0f)).IsEqual(40.0f);

        // Extent >= 2.5m (market stalls, large wagons) -> 0 (no size clamp)
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(2.5f, 1.0f)).IsEqual(0.0f);
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(3.5f, 1.0f)).IsEqual(0.0f);
    }

    [TestCase]
    public void TestSmallPropTieredDistanceCullingScalesDynamically()
    {
        // Scale = 1.5
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(0.3f, 1.5f)).IsEqual(22.5f);
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(0.8f, 1.5f)).IsEqual(30.0f);
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(1.2f, 1.5f)).IsEqual(45.0f);
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(2.0f, 1.5f)).IsEqual(60.0f);

        // Scale = 2.0
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(0.3f, 2.0f)).IsEqual(30.0f);
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(0.8f, 2.0f)).IsEqual(40.0f);
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(1.2f, 2.0f)).IsEqual(60.0f);
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(2.0f, 2.0f)).IsEqual(80.0f);

        // Scale <= 0 disables size culling
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(0.3f, 0.0f)).IsEqual(0.0f);
        AssertThat(Engine_PropStreamer.CalculateTieredCullDistance(0.8f, -1.0f)).IsEqual(0.0f);
    }

    [TestCase]
    public void TestSpatialLocalityBatchLimitIsSetToOne()
    {
        AssertThat(Engine_LevelProps.SelectableInstanceLimit).IsEqual(1);
    }
}
