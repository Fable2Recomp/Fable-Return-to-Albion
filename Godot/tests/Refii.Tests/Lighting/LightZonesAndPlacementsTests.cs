namespace Refii.Tests.Lighting;

using System;
using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Core;
using Refii.Gdb;
using Refii.Level;
using Refii.Lighting;
using Refii.Models;

/// <summary>
/// Unit and feature tests verifying placed light GDB parsing (`IgnoreZoning`, flame-FX fallback),
/// CECBuilding light/prop zone resolution (market stalls, building exterior vs. interior split),
/// and indoor/outdoor light culling rules (`Engine_LightShadowBudget.ShouldCullForBuildingZone`).
/// </summary>
[TestSuite]
public class LightZonesAndPlacementsTests
{
    private static uint FloatBits(float v) => unchecked((uint)BitConverter.SingleToInt32Bits(v));

    [TestCase]
    public void SphereInView_CorrectlyIdentifiesVisibility()
    {
        // Outward-pointing frustum planes (as returned by Godot 4 Camera3D.GetFrustum):
        // Looking down -Z from origin (0, 0, 0)
        var frustum = new Plane[]
        {
            new Plane(new Vector3(0, 0, 1), -0.05f),   // Near: pointing +Z (backwards)
            new Plane(new Vector3(0, 0, -1), 100.0f),  // Far: pointing -Z (forwards)
            new Plane(new Vector3(-1, 0, 0), 50.0f),   // Left: pointing -X
            new Plane(new Vector3(1, 0, 0), 50.0f),    // Right: pointing +X
            new Plane(new Vector3(0, -1, 0), 50.0f),   // Bottom: pointing -Y
            new Plane(new Vector3(0, 1, 0), 50.0f)     // Top: pointing +Y
        };

        // 1. Inside frustum (in front of camera at Z = -10): distance to all outward planes is negative
        AssertThat(Engine_LightShadowBudget.SphereInView(frustum, new Vector3(0, 0, -10), 1.0f)).IsTrue();

        // 2. Strictly behind camera (at Z = +5, distance to near plane is +5.05 > 1.0f)
        AssertThat(Engine_LightShadowBudget.SphereInView(frustum, new Vector3(0, 0, 5), 1.0f)).IsFalse();

        // 3. Behind camera at Z = 0.2m, but radius is 1.5m (reaches across near plane into view)
        AssertThat(Engine_LightShadowBudget.SphereInView(frustum, new Vector3(0, 0, 0.2f), 1.5f)).IsTrue();

        // 4. Strictly outside right plane (at X = 60, distance is 10 > 2.0f)
        AssertThat(Engine_LightShadowBudget.SphereInView(frustum, new Vector3(60, 0, -10), 2.0f)).IsFalse();

        // 5. Outside right plane at X = 51, but sphere radius 2.0m reaches inside (distance 1.0 <= 2.0f)
        AssertThat(Engine_LightShadowBudget.SphereInView(frustum, new Vector3(51, 0, -10), 2.0f)).IsTrue();
    }

    [TestCase]
    public void TryRead_ReadsIgnoreZoningAndInheritedLightProperties()
    {
        var db = new GdbPatchDatabase();

        const uint colourHash = 0x10000001u;
        var colourRec = new GdbPatchEntry(colourHash, 0x01);
        colourRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Red, GdbFieldType.Int, 255));
        colourRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Green, GdbFieldType.Int, 180));
        colourRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Blue, GdbFieldType.Int, 90));
        db.Entries.Add(colourRec);

        const uint compHash = 0x10000002u;
        var compRec = new GdbPatchEntry(compHash, 0x01);
        compRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.LightType, GdbFieldType.Int, Engine_LevelLights.LightTypePoint));
        compRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Range, GdbFieldType.Float, FloatBits(8.0f)));
        compRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Intensity, GdbFieldType.Float, FloatBits(15.0f)));
        compRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Colour, GdbFieldType.Reference, colourHash));
        compRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.IgnoreZoning, GdbFieldType.Bool, 1u));
        db.Entries.Add(compRec);

        const uint entityHash = 0x10000003u;
        var entityRec = new GdbPatchEntry(entityHash, 0x01);
        entityRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.GraphicAppearanceLightComponent, GdbFieldType.Reference, compHash));
        db.Entries.Add(entityRec);

        byte[] bytes = GdbWriter.Serialize(db);
        AssertThat(GdbView.TryParse(bytes, out GdbView view, out _)).IsTrue();

        var views = new List<GdbView> { view };
        AssertThat(Engine_LevelLights.TryRead(entityHash, views, out Engine_LevelLights.LightPlacement light)).IsTrue();
        AssertThat(light.Active).IsTrue();
        AssertThat(light.IgnoreZoning).IsTrue();
        AssertThat(light.Range).IsEqual(8.0f);
        AssertThat(light.Intensity).IsEqual(15.0f);
        AssertThat(light.Dummy).IsEqual(Engine_LevelLights.LightDummy);
    }

    [TestCase]
    public void LightBearing_IncludesBothAuthoredLightsAndFlameFxFixtures()
    {
        var db = new GdbPatchDatabase();

        // 1. Authored light placement (e.g. ThagsCave_BrazierFloor)
        const uint compHash = 0x20000001u;
        var compRec = new GdbPatchEntry(compHash, 0x01);
        compRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.LightType, GdbFieldType.Int, Engine_LevelLights.LightTypePoint));
        compRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Range, GdbFieldType.Float, FloatBits(8.0f)));
        db.Entries.Add(compRec);

        const uint brazierHash = 0x20000002u;
        var brazierRec = new GdbPatchEntry(brazierHash, 0x01);
        brazierRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.GraphicAppearanceLightComponent, GdbFieldType.Reference, compHash));
        db.Entries.Add(brazierRec);

        // 2. Flame FX fixture without GraphicAppearanceLightComponent (e.g. NewObjectBuildingBSMarketstall_4)
        const string lampDummy = "Prop.FX.Particle.FXENV_Lamp_Flame_01.par";
        uint lampDummyHash = Engine_GameHash.Fnv1(lampDummy);
        db.Names[lampDummyHash] = lampDummy;

        const uint fxItemHash = 0x20000003u;
        var fxItemRec = new GdbPatchEntry(fxItemHash, 0x01);
        fxItemRec.Properties.Add(new GdbPatchProperty(Engine_LevelFx.ParticleEmitter, GdbFieldType.String, 0x4AA5F7DAu));
        fxItemRec.Properties.Add(new GdbPatchProperty(Engine_LevelFx.DummyObject, GdbFieldType.String, lampDummyHash));
        db.Entries.Add(fxItemRec);

        const uint stallHash = 0x20000005u;
        var stallRec = new GdbPatchEntry(stallHash, 0x01);
        stallRec.Properties.Add(new GdbPatchProperty(Engine_LevelFx.ParticleEmitter, GdbFieldType.Reference, fxItemHash));
        db.Entries.Add(stallRec);

        // 3. Non-flame FX fixture (e.g. chimney smoke) should NOT be marked light-bearing
        const string smokeDummy = "Prop.FX.Particle.FX_Chimney_Smoke_01.par";
        uint smokeDummyHash = Engine_GameHash.Fnv1(smokeDummy);
        db.Names[smokeDummyHash] = smokeDummy;

        const uint smokeItemHash = 0x20000006u;
        var smokeItemRec = new GdbPatchEntry(smokeItemHash, 0x01);
        smokeItemRec.Properties.Add(new GdbPatchProperty(Engine_LevelFx.ParticleEmitter, GdbFieldType.String, 0x11111111u));
        smokeItemRec.Properties.Add(new GdbPatchProperty(Engine_LevelFx.DummyObject, GdbFieldType.String, smokeDummyHash));
        db.Entries.Add(smokeItemRec);

        const uint houseHash = 0x20000008u;
        var houseRec = new GdbPatchEntry(houseHash, 0x01);
        houseRec.Properties.Add(new GdbPatchProperty(Engine_LevelFx.ParticleEmitter, GdbFieldType.Reference, smokeItemHash));
        db.Entries.Add(houseRec);

        byte[] bytes = GdbWriter.Serialize(db);
        AssertThat(GdbView.TryParse(bytes, out GdbView view, out _)).IsTrue();

        var placements = new List<GdbPlacement>
        {
            new() { Hash = brazierHash, EntityName = "ThagsCave_BrazierFloor_1" },
            new() { Hash = stallHash, EntityName = "NewObjectBuildingBSMarketstall_4" },
            new() { Hash = houseHash, EntityName = "NewObjectBuildingBWSHouse1" },
        };

        HashSet<uint> bearing = Engine_LevelLights.LightBearing(view, new List<GdbView>(), placements);
        AssertThat(bearing.Contains(brazierHash)).IsTrue();
        AssertThat(bearing.Contains(stallHash)).IsTrue();
        AssertThat(bearing.Contains(houseHash)).IsFalse();
    }

    [TestCase]
    public void ResolveLightZone_HandlesIgnoreZoningAndBuildingHostedStallLights()
    {
        // GiftMarketstall: record = 0x830D537F, CECBuilding rawZone = 0x030D537F (record & 0x7FFFFFFF).
        // In sub_825C2108, CECGraphicAppearanceLight never takes the CECBuilding override; its zone is Outdoors (1).
        const uint giftStallRecord = 0x830D537Fu;
        const uint giftStallRawZone = 0x030D537Fu;
        AssertThat(Engine_LightZones.ResolveLightZone(giftStallRecord, giftStallRawZone, ignoreZoning: false))
            .IsEqual(Engine_ClassicZones.Outdoors);

        // Interior room light placed inside a building: record = 0x99887766, enclosing building zone = 0x12345678.
        const uint roomLightRecord = 0x99887766u;
        const uint buildingZone = 0x12345678u;
        AssertThat(Engine_LightZones.ResolveLightZone(roomLightRecord, buildingZone, ignoreZoning: false))
            .IsEqual(buildingZone);

        // IgnoreZoning = true always forces zone 0 (lights all zones).
        AssertThat(Engine_LightZones.ResolveLightZone(roomLightRecord, buildingZone, ignoreZoning: true))
            .IsEqual(0u);
    }

    [TestCase]
    public void ResolvePropZone_SeparatesMarketStallsAndBuildingExteriorsFromInteriors()
    {
        System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", "modern");
        const uint buildingRecord = 0x830D537Fu;
        const uint buildingRawZone = 0x030D537Fu;

        // 1. Open market stall (CECBuilding with 0 interior meshes) stays in Outdoors (1).
        uint stallZone = Engine_LightZones.ResolvePropZone(
            buildingRecord,
            buildingRawZone,
            "Art/Environment/Shared assets/ShopClutter/dotXSI/ESA_Shop_MarketStall_Gifts/ESA_Shop_MarketStall_Gifts.mdl",
            interiorMeshCount: 0);
        AssertThat(stallZone).IsEqual(Engine_ClassicZones.Outdoors);
        AssertThat(Engine_LightZones.Zoned(Engine_LevelLights.LampHostLayerMask, stallZone))
            .IsEqual(Engine_LevelLights.LampHostLayerMask);

        // 2. Enclosed building's exterior mesh stays in Outdoors (1) so streetlights illuminate facades
        //    and indoor lights do not bleed onto exterior walls.
        uint exteriorZone = Engine_LightZones.ResolvePropZone(
            buildingRecord,
            buildingRawZone,
            "Art/Environment/Regions/Bowerstone/Buildings/dotXSI/BS_TownHouse_01/BS_TownHouse_01_exterior.mdl",
            interiorMeshCount: 1);
        AssertThat(exteriorZone).IsEqual(Engine_ClassicZones.Outdoors);
        uint defaultOutdoorLayers = 1u | Engine_LevelLights.EntityCasterLayerMask;
        AssertThat(Engine_LightZones.Zoned(defaultOutdoorLayers, exteriorZone))
            .IsEqual(defaultOutdoorLayers);

        // 3. Enclosed building's interior mesh keeps the building's indoor zone and trades bit 1 for IndoorMask
        //    (and EntityCasterLayerMask for IndoorEntityCasterMask so indoor movers pair with indoor lights).
        Engine_LightingFrame.CurrentInterior = false;
        uint interiorZone = Engine_LightZones.ResolvePropZone(
            buildingRecord,
            buildingRawZone,
            "Art/Environment/Regions/Bowerstone/Buildings/dotXSI/BS_TownHouse_01/BS_TownHouse_01_interior.mdl",
            interiorMeshCount: 1);
        AssertThat(interiorZone).IsEqual(buildingRawZone);
        AssertThat(Engine_LightZones.Zoned(defaultOutdoorLayers, interiorZone))
            .IsEqual(Engine_LightZones.IndoorMask | Engine_LevelLights.IndoorEntityCasterMask);

        // 3b. An indoor lamp-host fixture (e.g. tavern chandelier or wall oil lamp) trades LampHostLayerMask
        //     for IndoorLampHostMask and never receives IndoorMask, so it is lit by indoor lights without
        //     casting shadows onto itself.
        AssertThat(Engine_LightZones.Zoned(Engine_LevelLights.LampHostLayerMask, interiorZone))
            .IsEqual(Engine_LightZones.IndoorLampHostMask);

        // 4. Single-mesh enclosed CECBuilding chamber in an interior level (e.g. Guild Hall 0x020599CE)
        //    retains its building zone rather than being demoted to Outdoors, and Zoned() leaves layers intact.
        Engine_LightingFrame.CurrentInterior = true;
        const uint guildHallRecord = 0x820599CEu;
        const uint guildHallZone = 0x020599CEu;
        uint chamberZone = Engine_LightZones.ResolvePropZone(
            guildHallRecord,
            guildHallZone,
            "Art/Environment/Regions/Caves/ThagsCave/ThagsCave_GuildHall.mdl",
            interiorMeshCount: 0,
            isLampHost: false);
        AssertThat(chamberZone).IsEqual(guildHallZone);
        AssertThat(Engine_LightZones.Zoned(defaultOutdoorLayers, chamberZone))
            .IsEqual(defaultOutdoorLayers);
        Engine_LightingFrame.CurrentInterior = false;
        System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", null);
    }

    [TestCase]
    public void EvaluateLightingFrame_BlackSkyCaveWithMainLight_ReportsHasSkyFalse()
    {
        System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", "modern");
        System.Environment.SetEnvironmentVariable("REFII_SUN_SHADOWS", "1");
        System.Environment.SetEnvironmentVariable("REFII_INTERIOR_NEUTRAL", "0");
        System.Environment.SetEnvironmentVariable("REFII_INTERIOR_AMBIENT", "1");
        try
        {
            var skyTheme = new SkyTheme();
            var skyFrame = new SkyFrame();
            skyFrame.MainLightDirection[1] = 0.7f;
            skyFrame.MainLightDirection[2] = 0.7f;
            // Black sky dome (SkyTop = 0, SkyBottom = 0), as in ThagsCave.
            Array.Clear(skyFrame.SkyTop, 0, skyFrame.SkyTop.Length);
            Array.Clear(skyFrame.SkyBottom, 0, skyFrame.SkyBottom.Length);
            var lighting = new LightingTheme
            {
                HasMainLightColour = true,
                HasAmbientColour = true,
            };
            lighting.MainLightColour[0] = 1.0f;
            lighting.MainLightColour[1] = 0.58f;
            lighting.MainLightColour[2] = 0.0f;
            lighting.AmbientColour[0] = 0.05f;
            lighting.AmbientColour[1] = 0.05f;
            lighting.AmbientColour[2] = 0.05f;

            LightingFrame frame = Engine_LightingFrame.Evaluate(skyTheme, lighting, skyFrame);
            AssertThat(frame.HasSky).IsFalse();
            AssertThat(frame.SkylightWeight).IsEqual(0.0f);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", null);
            System.Environment.SetEnvironmentVariable("REFII_SUN_SHADOWS", null);
            System.Environment.SetEnvironmentVariable("REFII_INTERIOR_NEUTRAL", null);
            System.Environment.SetEnvironmentVariable("REFII_INTERIOR_AMBIENT", null);
        }
    }

    [TestCase]
    public void ShouldCullForBuildingZone_OnlyCullsOtherBuildingWhenIndoorsAndCasterMaskIncludesIndoors()
    {
        // Every shadow-casting placed light includes StaticCasterMask (1u | IndoorMask) so exterior walls,
        // interior walls/floors/ceilings and static props always cast shadows and block light bleed.
        // Only lights selected for the dynamic entity shadow budget include EntityCasterLayerMask.
        // Neither mask ever includes LampHostLayerMask or IndoorLampHostMask (no self-shadowing).
        uint staticOnlyMask = Engine_LevelLights.CasterMaskOf(includeDynamicEntities: false);
        AssertThat((staticOnlyMask & 1u) != 0u).IsTrue();
        AssertThat((staticOnlyMask & Engine_LightZones.IndoorMask) != 0u).IsTrue();
        AssertThat((staticOnlyMask & Engine_LevelLights.EntityCasterLayerMask) == 0u).IsTrue();
        AssertThat((staticOnlyMask & Engine_LevelLights.LampHostLayerMask) == 0u).IsTrue();
        AssertThat((staticOnlyMask & Engine_LightZones.IndoorLampHostMask) == 0u).IsTrue();

        uint dynamicMask = Engine_LevelLights.CasterMaskOf(includeDynamicEntities: true);
        AssertThat((dynamicMask & 1u) != 0u).IsTrue();
        AssertThat((dynamicMask & Engine_LightZones.IndoorMask) != 0u).IsTrue();
        AssertThat((dynamicMask & Engine_LevelLights.EntityCasterLayerMask) != 0u).IsTrue();
        AssertThat((dynamicMask & Engine_LevelLights.LampHostLayerMask) == 0u).IsTrue();
        AssertThat((dynamicMask & Engine_LightZones.IndoorLampHostMask) == 0u).IsTrue();

        const uint houseA = 0x01111111u;
        const uint houseB = 0x02222222u;

        // 1. Interior/cave level (e.g. ThagsCave): never cull lights for building zones.
        AssertThat(Engine_LightShadowBudget.ShouldCullForBuildingZone(
            viewerIndoors: true,
            isInteriorLevel: true,
            isIndoorLight: false,
            lightZone: Engine_ClassicZones.Outdoors,
            heroZone: Engine_ClassicZones.Outdoors)).IsFalse();

        // 2. Viewer outdoors: never cull indoor lights by building zone (frustum and global range handle culling).
        AssertThat(Engine_LightShadowBudget.ShouldCullForBuildingZone(
            viewerIndoors: false,
            isInteriorLevel: false,
            isIndoorLight: true,
            lightZone: houseA,
            heroZone: Engine_ClassicZones.Outdoors)).IsFalse();

        // 3. Viewer indoors inside House A: outdoor lights remain visible through windows/doors,
        //    and lights in House A remain active, while lights in adjacent House B are culled.
        AssertThat(Engine_LightShadowBudget.ShouldCullForBuildingZone(
            viewerIndoors: true,
            isInteriorLevel: false,
            isIndoorLight: false,
            lightZone: Engine_ClassicZones.Outdoors,
            heroZone: houseA)).IsFalse();

        AssertThat(Engine_LightShadowBudget.ShouldCullForBuildingZone(
            viewerIndoors: true,
            isInteriorLevel: false,
            isIndoorLight: true,
            lightZone: houseA,
            heroZone: houseA)).IsFalse();

        AssertThat(Engine_LightShadowBudget.ShouldCullForBuildingZone(
            viewerIndoors: true,
            isInteriorLevel: false,
            isIndoorLight: true,
            lightZone: houseB,
            heroZone: houseA)).IsTrue();
    }

    [TestCase]
    public void AlignLightDummyToFlame_LowersElevatedChandelierAndStreetlampDummiesToFlameHeight()
    {
        // 1. BS_Light_Ceiling.mdl: Prop.Layout.Light is at Y = 0.698 (up on the suspension chain),
        //    while the 4 ring lamp flames sit at Y = 0.394 (and Att[0] sits at Y = 0.005).
        var chandelierLight = new Transform3D(Basis.Identity, new Vector3(0.0f, 0.698f, -0.011f));
        var chandelierFlames = new List<Vector3>
        {
            new(0.002f, 0.005f, -0.005f),
            new(0.002f, 0.394f, -0.713f),
            new(-0.706f, 0.394f, -0.005f),
            new(0.002f, 0.394f, 0.704f),
            new(0.711f, 0.394f, -0.005f),
        };
        Transform3D alignedChandelier = Engine_LevelLights.AlignLightDummyToFlame(chandelierLight, chandelierFlames);
        AssertThat(Mathf.IsEqualApprox(alignedChandelier.Origin.Y, 0.394f, 0.001f)).IsTrue();

        // 2. BS_Lights_Town.mdl: Layout_Light_DummyObject is at Y = 3.397 (in the top cap),
        //    while FX_Particle_DummyObject is at Y = 3.214 (inside the glass globe).
        var streetlampLight = new Transform3D(Basis.Identity, new Vector3(0.0f, 3.397f, -0.457f));
        var streetlampFlames = new List<Vector3> { new(0.0f, 3.214f, -0.453f) };
        Transform3D alignedStreetlamp = Engine_LevelLights.AlignLightDummyToFlame(streetlampLight, streetlampFlames);
        AssertThat(Mathf.IsEqualApprox(alignedStreetlamp.Origin.Y, 3.214f, 0.001f)).IsTrue();

        // 3. ESA_Fireplace_Grate_Large.mdl: floor grate flame at Y = 0.305 (<= 0.35f) stays at Y = 0.386.
        var fireplaceLight = new Transform3D(Basis.Identity, new Vector3(0.003f, 0.386f, -0.022f));
        var fireplaceFlames = new List<Vector3> { new(0.0f, 0.305f, 0.0f) };
        Transform3D alignedFireplace = Engine_LevelLights.AlignLightDummyToFlame(fireplaceLight, fireplaceFlames);
        AssertThat(Mathf.IsEqualApprox(alignedFireplace.Origin.Y, 0.386f, 0.001f)).IsTrue();
    }

    [TestCase]
    public void ScoreDynamicEntityShadowLight_PrioritizesSameFloorLightsOverUpperAndLowerStories()
    {
        const uint tavernZone = 0x1E65CC19u;
        var heroDownstairs = new Vector3(-118.4f, 40.38f, 294.3f);

        // Downstairs chandelier (same floor, Y = 43.44, dy = +3.06m) vs. upstairs chandelier directly
        // overhead on the second floor (Y = 49.35, dy = +8.97m).
        var downstairsChandelier = new Vector3(-117.7f, 43.44f, 291.7f);
        var upstairsChandelier = new Vector3(-117.6f, 49.35f, 291.6f);

        float downstairsScore = Engine_LightShadowBudget.ScoreDynamicEntityShadowLight(
            downstairsChandelier,
            heroDownstairs,
            range: 9.0f,
            baseEnergy: 9.0f,
            viewerIndoors: true,
            isInteriorLevel: false,
            isIndoorLight: true,
            lightZone: tavernZone,
            heroZone: tavernZone);

        float upstairsScore = Engine_LightShadowBudget.ScoreDynamicEntityShadowLight(
            upstairsChandelier,
            heroDownstairs,
            range: 9.0f,
            baseEnergy: 9.0f,
            viewerIndoors: true,
            isInteriorLevel: false,
            isIndoorLight: true,
            lightZone: tavernZone,
            heroZone: tavernZone);

        AssertThat(downstairsScore < upstairsScore).IsTrue();
        AssertThat(upstairsScore > downstairsScore * 10.0f).IsTrue();

        // Outdoor streetlight is ineligible to cast a dynamic entity shadow on an indoor player.
        float outdoorScoreWhenIndoors = Engine_LightShadowBudget.ScoreDynamicEntityShadowLight(
            new Vector3(-115.0f, 42.0f, 290.0f),
            heroDownstairs,
            range: 12.0f,
            baseEnergy: 12.0f,
            viewerIndoors: true,
            isInteriorLevel: false,
            isIndoorLight: false,
            lightZone: Engine_ClassicZones.Outdoors,
            heroZone: tavernZone);
        AssertThat(outdoorScoreWhenIndoors).IsEqual(float.MaxValue);
    }

    [TestCase]
    public void BuildingInteriorLightScales_IsolateTownBuildingsFromCaves()
    {
        Engine_LightingFrame.CurrentInterior = false;

        // 1. Indoor light in an outdoor level excludes outdoor Layer 1 and outdoor EntityCasterLayerMask from its
        //    ShadowCasterMask so its 6 cube faces only draw IndoorMask (+ IndoorEntityCasterMask when dynamic).
        uint indoorStaticMask = Engine_LevelLights.CasterMaskOf(includeDynamicEntities: false, isIndoorLight: true);
        AssertThat((indoorStaticMask & 1u) == 0u).IsTrue();
        AssertThat((indoorStaticMask & Engine_LightZones.IndoorMask) != 0u).IsTrue();
        AssertThat((indoorStaticMask & Engine_LevelLights.EntityCasterLayerMask) == 0u).IsTrue();
        AssertThat((indoorStaticMask & Engine_LevelLights.IndoorEntityCasterMask) == 0u).IsTrue();

        uint indoorDynamicMask = Engine_LevelLights.CasterMaskOf(includeDynamicEntities: true, isIndoorLight: true);
        AssertThat((indoorDynamicMask & 1u) == 0u).IsTrue();
        AssertThat((indoorDynamicMask & Engine_LightZones.IndoorMask) != 0u).IsTrue();
        AssertThat((indoorDynamicMask & Engine_LevelLights.IndoorEntityCasterMask) != 0u).IsTrue();
        AssertThat((indoorDynamicMask & Engine_LevelLights.EntityCasterLayerMask) == 0u).IsTrue();

        // 2. Live CVar tuning via Engine_RefiiConfig.TrySetGodotCvar updates building interior scales.
        AssertThat(Engine_RefiiConfig.TrySetGodotCvar("placed_light_scale", "1.5")).IsTrue();
        AssertThat(Engine_RefiiConfig.TrySetGodotCvar("placed_light_reach_scale", "1.5")).IsTrue();
        AssertThat(Engine_RefiiConfig.TrySetGodotCvar("building_interior_light_scale", "0.5")).IsTrue();
        AssertThat(Engine_RefiiConfig.TrySetGodotCvar("building_interior_reach_scale", "1.0")).IsTrue();
        AssertThat(Engine_RefiiConfig.TrySetGodotCvar("building_interior_ambient_scale", "0.25")).IsTrue();

        // 2a. Outdoor fixture uses ModernBaseEnergyScale (2.0) * placed_light_scale (1.5) = 3.0x and ModernBaseReachScale (1.5) * placed_light_reach_scale (1.5) = 2.25x.
        Engine_LevelLights.ComputeEffectiveLightValues(
            rawIntensity: 10.0f,
            rawRange: 8.0f,
            isIndoorLight: false,
            out float outdoorEnergy,
            out float outdoorRange);
        AssertThat(Mathf.IsEqualApprox(outdoorEnergy, 30.0f, 0.001f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(outdoorRange, 18.0f, 0.001f)).IsTrue();

        // 2b. Town building indoor fixture uses ModernBaseEnergyScale (2.0) * placed_light_scale (1.5) * building_interior_light_scale (0.5) = 1.5x, and ModernBaseReachScale (1.5) * placed_light_reach_scale (1.5) * reach (1.0) = 2.25x.
        Engine_LevelLights.ComputeEffectiveLightValues(
            rawIntensity: 10.0f,
            rawRange: 8.0f,
            isIndoorLight: true,
            out float indoorEnergy,
            out float indoorRange);
        AssertThat(Mathf.IsEqualApprox(indoorEnergy, 15.0f, 0.001f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(indoorRange, 18.0f, 0.001f)).IsTrue();

        // 2c. A level with no sky (CurrentInterior = true) applies ModernBaseEnergyScale * placed_light_scale and
        //     ModernBaseReachScale * placed_light_reach_scale, while ignoring building_interior_*.
        Engine_LightingFrame.CurrentInterior = true;
        Engine_LevelLights.ComputeEffectiveLightValues(
            rawIntensity: 10.0f,
            rawRange: 8.0f,
            isIndoorLight: true,
            out float caveEnergy,
            out float caveRange);
        AssertThat(Mathf.IsEqualApprox(caveEnergy, 30.0f, 0.001f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(caveRange, 18.0f, 0.001f)).IsTrue();

        Engine_LightingFrame.CurrentInterior = false;
        Engine_RefiiConfig.TrySetGodotCvar("placed_light_scale", "1.0");
        Engine_RefiiConfig.TrySetGodotCvar("placed_light_reach_scale", "1.0");
        Engine_RefiiConfig.TrySetGodotCvar("building_interior_light_scale", "1.0");

        // Verify vanilla 1.0x baseline defaults:
        AssertThat(Mathf.IsEqualApprox(Engine_LevelLights.PlacedScale, 1.0f, 0.001f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(Engine_LevelLights.PlacedRange, 1.0f, 0.001f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(Engine_LevelLights.BuildingInteriorLightScale, 1.0f, 0.001f)).IsTrue();

        // 3. fable_ambient_scale per-instance decoupling check
        Engine_RefiiConfig.TrySetGodotCvar("modern_tone_curve", "fable");
        AssertThat(Mathf.IsEqualApprox(Engine_LevelBakedLighting.BuildingInteriorAmbientScale, 0.25f, 0.001f)).IsTrue();
        Engine_RefiiConfig.TrySetGodotCvar("building_interior_ambient_scale", "1.0");
        AssertThat(Mathf.IsEqualApprox(Engine_LevelBakedLighting.BuildingInteriorAmbientScale, 1.0f, 0.001f)).IsTrue();
        bool outdoorIsIndoor = (1u & (Engine_LightZones.IndoorMask | Engine_LightZones.IndoorLampHostMask)) != 0u;
        AssertThat(outdoorIsIndoor).IsFalse();
        bool indoorIsIndoor = (Engine_LightZones.IndoorMask & (Engine_LightZones.IndoorMask | Engine_LightZones.IndoorLampHostMask)) != 0u;
        AssertThat(indoorIsIndoor).IsTrue();
    }

    [TestCase]
    public void TestPlacedLightBakeMode_IsDynamicToPreventSdfgiScrollAccumulation()
    {
        // Placed level lights must use Light3D.BakeMode.Dynamic (not Static) so Godot 4's SDFGI
        // does not additively re-accumulate static light energy into process_voxels[].light
        // every time a cascade scrolls as the player moves back and forth.
        AssertThat(Engine_LevelLights.PlacedLightBakeMode).IsEqual(Light3D.BakeMode.Dynamic);
        AssertThat(Engine_LevelLights.PlacedLightBakeMode).IsNotEqual(Light3D.BakeMode.Static);
    }

    [TestCase]
    public void SyntheticFlameLights_ClustersMultiFlameFixturesAndCentersAtCentroid_AndKeepsSeparateFixturesDistinct()
    {
        Engine_LevelFx.ClearCaches();

        var db = new GdbPatchDatabase();

        const string flameDummy = "Prop.FX.Particle.FX_Candle_Flame_01.par";
        uint flameDummyHash = Engine_GameHash.Fnv1(flameDummy);
        db.Names[flameDummyHash] = flameDummy;

        const string lanternDummy = "Prop.FX.Particle.FXENV_Lamp_Flame_01.par";
        uint lanternDummyHash = Engine_GameHash.Fnv1(lanternDummy);
        db.Names[lanternDummyHash] = lanternDummy;

        // Model skeleton mock with attachments
        const string candleModel = "Art/Environment/Shared assets/Lights/dotXSI/RS_TOG_Candle_V1.mdl";
        const string lanternModel = "Art/Environment/Shared assets/Lights/dotXSI/RS_Hanging_Lantern.mdl";

        var candleGmd = Engine_GmdSkeleton.CreateForTesting(
            names: Array.Empty<string>(),
            parents: Array.Empty<int>(),
            locals: Array.Empty<Transform3D>(),
            attachments: new[]
            {
                new GmdAttachment(flameDummy, Transform3D.Identity, -1)
            });
        Engine_LevelFx.InjectGmdForTesting(candleModel, candleGmd);

        var lanternGmd = Engine_GmdSkeleton.CreateForTesting(
            names: Array.Empty<string>(),
            parents: Array.Empty<int>(),
            locals: Array.Empty<Transform3D>(),
            attachments: new[]
            {
                new GmdAttachment(lanternDummy, Transform3D.Identity, -1)
            });
        Engine_LevelFx.InjectGmdForTesting(lanternModel, lanternGmd);

        var modelPaths = new Dictionary<uint, string>();
        var placements = new List<GdbPlacement>();

        // 1. Fixture A: 5 candle placements on a stand within 0.25m horizontal radius at Y = 45.0
        Vector3[] candleOffsets =
        {
            new(-0.15f, 0.0f, -0.10f),
            new(0.12f, 0.02f, -0.14f),
            new(0.00f, -0.01f, 0.16f),
            new(-0.11f, 0.01f, 0.12f),
            new(0.14f, -0.02f, 0.08f)
        };
        Vector3 fixtureACenter = new Vector3(348.65f, 45.28f, 166.92f);
        Vector3 expectedFixtureACentroid = Vector3.Zero;

        uint fxItemCandleHash = 0x30000001u;
        var fxItemCandle = new GdbPatchEntry(fxItemCandleHash, 0x01);
        fxItemCandle.Properties.Add(new GdbPatchProperty(Engine_LevelFx.ParticleEmitter, GdbFieldType.String, 0x6E4BB265u));
        fxItemCandle.Properties.Add(new GdbPatchProperty(Engine_LevelFx.DummyObject, GdbFieldType.String, flameDummyHash));
        db.Entries.Add(fxItemCandle);

        for (int i = 0; i < candleOffsets.Length; i++)
        {
            uint candleHash = (uint)(0x31000000 + i);
            var candleRec = new GdbPatchEntry(candleHash, 0x01);
            candleRec.Properties.Add(new GdbPatchProperty(Engine_LevelFx.ParticleEmitter, GdbFieldType.Reference, fxItemCandleHash));
            db.Entries.Add(candleRec);

            Vector3 pos = fixtureACenter + candleOffsets[i];
            expectedFixtureACentroid += pos;

            (float fx, float fy, float fz) = GdbTransformMath.ToFablePosition(pos);
            placements.Add(new GdbPlacement
            {
                Hash = candleHash,
                EntityName = $"Candle_{i}",
                X = fx,
                Y = fy,
                Z = fz
            });
            modelPaths[candleHash] = candleModel;
        }
        expectedFixtureACentroid /= candleOffsets.Length;

        // 2. Fixture B: An adjacent lantern placed 1.5m away horizontally (should NOT cluster with Fixture A)
        uint lanternHash = 0x32000001u;
        uint fxItemLanternHash = 0x30000002u;
        var fxItemLantern = new GdbPatchEntry(fxItemLanternHash, 0x01);
        fxItemLantern.Properties.Add(new GdbPatchProperty(Engine_LevelFx.ParticleEmitter, GdbFieldType.String, 0x48A6D808u));
        fxItemLantern.Properties.Add(new GdbPatchProperty(Engine_LevelFx.DummyObject, GdbFieldType.String, lanternDummyHash));
        db.Entries.Add(fxItemLantern);

        var lanternRec = new GdbPatchEntry(lanternHash, 0x01);
        lanternRec.Properties.Add(new GdbPatchProperty(Engine_LevelFx.ParticleEmitter, GdbFieldType.Reference, fxItemLanternHash));
        db.Entries.Add(lanternRec);

        Vector3 lanternPos = fixtureACenter + new Vector3(1.5f, 0.0f, 0.0f);
        (float lx, float ly, float lz) = GdbTransformMath.ToFablePosition(lanternPos);
        placements.Add(new GdbPlacement
        {
            Hash = lanternHash,
            EntityName = "Adjacent_Lantern",
            X = lx,
            Y = ly,
            Z = lz
        });
        modelPaths[lanternHash] = lanternModel;

        // 3. Fixture C: An elevated ceiling lamp directly above Fixture A by 1.8m (vertical tier check)
        uint highLampHash = 0x33000001u;
        var highLampRec = new GdbPatchEntry(highLampHash, 0x01);
        highLampRec.Properties.Add(new GdbPatchProperty(Engine_LevelFx.ParticleEmitter, GdbFieldType.Reference, fxItemLanternHash));
        db.Entries.Add(highLampRec);

        Vector3 highLampPos = fixtureACenter + new Vector3(0.0f, 1.8f, 0.0f);
        (float hx, float hy, float hz) = GdbTransformMath.ToFablePosition(highLampPos);
        placements.Add(new GdbPlacement
        {
            Hash = highLampHash,
            EntityName = "High_Lamp",
            X = hx,
            Y = hy,
            Z = hz
        });
        modelPaths[highLampHash] = lanternModel;

        byte[] bytes = GdbWriter.Serialize(db);
        AssertThat(GdbView.TryParse(bytes, out GdbView view, out _)).IsTrue();

        var hosts = new HashSet<uint>();
        List<Engine_LevelLights.ResolvedLightInstance> resolved = Engine_LevelLights.ResolvePlacements(
            view,
            new List<GdbView>(),
            placements,
            modelPaths,
            hosts,
            out int dup,
            out int dupElsewhere);

        // Verification:
        // Fixture A (5 candles) merged into 1 light.
        // Fixture B (lantern 1.5m away) emitted 1 light.
        // Fixture C (elevated lamp 1.8m up) emitted 1 light.
        // Total lights resolved should be exactly 3!
        AssertThat(resolved.Count).IsEqual(3);

        // Find the clustered light for Fixture A (member of 0x31000000..0x31000004)
        Engine_LevelLights.ResolvedLightInstance candleLight =
            resolved.Find(r => r.Entity >= 0x31000000u && r.Entity <= 0x31000004u);
        AssertThat(candleLight.Entity).IsNotEqual(0u);

        // Verify the clustered light is placed at the exact centroid
        AssertThat(candleLight.Transform.Origin.DistanceTo(expectedFixtureACentroid))
            .IsLess(0.001f);

        // Verify all 5 candle fixtures, the lantern, and the high lamp are all protected in hosts
        for (int i = 0; i < candleOffsets.Length; i++)
        {
            AssertThat(hosts.Contains((uint)(0x31000000 + i))).IsTrue();
        }
        AssertThat(hosts.Contains(lanternHash)).IsTrue();
        AssertThat(hosts.Contains(highLampHash)).IsTrue();

        Engine_LevelFx.ClearCaches();
    }

    [TestCase]
    public void DynamicColorAndTypeClustering_CollapsesAuthoredCandles_WhilePreservingAdjacentBlueFlame()
    {
        Engine_LevelFx.ClearCaches();

        var db = new GdbPatchDatabase();

        // Warm candle color: RGB (255, 160, 40)
        const uint warmColorHash = 0x40000001u;
        var warmColor = new GdbPatchEntry(warmColorHash, 0x01);
        warmColor.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Red, GdbFieldType.Int, 255));
        warmColor.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Green, GdbFieldType.Int, 160));
        warmColor.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Blue, GdbFieldType.Int, 40));
        db.Entries.Add(warmColor);

        // Blue ritual flame color: RGB (50, 130, 255)
        const uint blueColorHash = 0x40000002u;
        var blueColor = new GdbPatchEntry(blueColorHash, 0x01);
        blueColor.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Red, GdbFieldType.Int, 50));
        blueColor.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Green, GdbFieldType.Int, 130));
        blueColor.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Blue, GdbFieldType.Int, 255));
        db.Entries.Add(blueColor);

        // Authored warm candle light component
        const uint warmCompHash = 0x40000010u;
        var warmComp = new GdbPatchEntry(warmCompHash, 0x01);
        warmComp.Properties.Add(new GdbPatchProperty(Engine_LevelLights.LightType, GdbFieldType.Int, Engine_LevelLights.LightTypePoint));
        warmComp.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Range, GdbFieldType.Float, FloatBits(7.5f)));
        warmComp.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Intensity, GdbFieldType.Float, FloatBits(1.05f)));
        warmComp.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Colour, GdbFieldType.Reference, warmColorHash));
        warmComp.Properties.Add(new GdbPatchProperty(Engine_LevelLights.AttachToEntityPosition, GdbFieldType.Bool, 1u));
        db.Entries.Add(warmComp);

        // Authored blue ritual flame light component
        const uint blueCompHash = 0x40000020u;
        var blueComp = new GdbPatchEntry(blueCompHash, 0x01);
        blueComp.Properties.Add(new GdbPatchProperty(Engine_LevelLights.LightType, GdbFieldType.Int, Engine_LevelLights.LightTypePoint));
        blueComp.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Range, GdbFieldType.Float, FloatBits(7.5f)));
        blueComp.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Intensity, GdbFieldType.Float, FloatBits(1.25f)));
        blueComp.Properties.Add(new GdbPatchProperty(Engine_LevelLights.Colour, GdbFieldType.Reference, blueColorHash));
        blueComp.Properties.Add(new GdbPatchProperty(Engine_LevelLights.AttachToEntityPosition, GdbFieldType.Bool, 1u));
        db.Entries.Add(blueComp);

        var placements = new List<GdbPlacement>();
        Vector3 altarOrigin = new Vector3(-317.0f, 43.3f, 155.9f);
        Vector3[] warmCandleOffsets =
        {
            new(0.0f, 0.0f, 0.0f),
            new(0.85f, 0.05f, 0.20f),
            new(-0.70f, -0.04f, 0.55f),
            new(0.35f, 0.02f, -0.90f),
        };

        for (int i = 0; i < warmCandleOffsets.Length; i++)
        {
            uint entHash = (uint)(0x41000000 + i);
            var rec = new GdbPatchEntry(entHash, 0x01);
            rec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.GraphicAppearanceLightComponent, GdbFieldType.Reference, warmCompHash));
            db.Entries.Add(rec);

            Vector3 pos = altarOrigin + warmCandleOffsets[i];
            (float fx, float fy, float fz) = GdbTransformMath.ToFablePosition(pos);
            placements.Add(new GdbPlacement
            {
                Hash = entHash,
                EntityName = $"HallOfLight_Candle_{i}",
                X = fx,
                Y = fy,
                Z = fz,
            });
        }

        // Place the blue flame right next to the candles (0.4m away horizontally)
        const uint blueEntHash = 0x42000001u;
        var blueRec = new GdbPatchEntry(blueEntHash, 0x01);
        blueRec.Properties.Add(new GdbPatchProperty(Engine_LevelLights.GraphicAppearanceLightComponent, GdbFieldType.Reference, blueCompHash));
        db.Entries.Add(blueRec);

        Vector3 bluePos = altarOrigin + new Vector3(0.40f, 0.0f, 0.10f);
        (float bfx, float bfy, float bfz) = GdbTransformMath.ToFablePosition(bluePos);
        placements.Add(new GdbPlacement
        {
            Hash = blueEntHash,
            EntityName = "HallOfLight_BlueFlame",
            X = bfx,
            Y = bfy,
            Z = bfz,
        });

        byte[] bytes = GdbWriter.Serialize(db);
        AssertThat(GdbView.TryParse(bytes, out GdbView view, out _)).IsTrue();

        var hosts = new HashSet<uint>();
        List<Engine_LevelLights.ResolvedLightInstance> resolved = Engine_LevelLights.ResolvePlacements(
            view,
            new List<GdbView>(),
            placements,
            modelPaths: null,
            hosts,
            out _,
            out _);

        // All 4 warm candles collapse into 1 centroid light, while the blue flame remains separate (total = 2).
        AssertThat(resolved.Count).IsEqual(2);
        AssertThat(resolved.Exists(r => r.Entity == blueEntHash)).IsTrue();
        AssertThat(resolved.Exists(r => r.Entity == 0x41000000u)).IsTrue();
    }

    [TestCase]
    public void EvaluateLightingFrame_AtNightInModernMode_AlignsLightDirectionWithMoonAndDefaultBloomIs025()
    {
        System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", "modern");
        var skyTheme = new SkyTheme();
        var lighting = new LightingTheme
        {
            HasMainLightColour = true,
        };
        lighting.MainLightColour[0] = 0.25f;
        lighting.MainLightColour[1] = 0.30f;
        lighting.MainLightColour[2] = 0.45f;

        var skyFrame = new SkyFrame();
        // Sun is below the horizon at night:
        skyFrame.SunDirection[0] = 0.0f;
        skyFrame.SunDirection[1] = -0.6f;
        skyFrame.SunDirection[2] = 0.8f;

        // MainLightDirection points elsewhere:
        skyFrame.MainLightDirection[0] = 0.5f;
        skyFrame.MainLightDirection[1] = 0.5f;
        skyFrame.MainLightDirection[2] = 0.7071f;

        // Moon is above the horizon at (-0.3, 0.6, 0.7416) in Fable coordinates -> ToGodot = (+0.3, 0.6, 0.7416):
        skyFrame.MoonDirection[0] = -0.3f;
        skyFrame.MoonDirection[1] = 0.6f;
        skyFrame.MoonDirection[2] = 0.7416198f;
        skyFrame.SkyTop[0] = 0.05f;
        skyFrame.SkyTop[1] = 0.08f;
        skyFrame.SkyTop[2] = 0.14f;

        LightingFrame evaluated = Engine_LightingFrame.Evaluate(skyTheme, lighting, skyFrame);
        Vector3 expectedMoon = Engine_SkyFrame.ToGodot(skyFrame.MoonDirection).Normalized();
        AssertThat(Mathf.IsEqualApprox(evaluated.LightDirection[0], expectedMoon.X, 1e-4f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(evaluated.LightDirection[1], expectedMoon.Y, 1e-4f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(evaluated.LightDirection[2], expectedMoon.Z, 1e-4f)).IsTrue();

        AssertThat(Mathf.IsEqualApprox(Engine_PostFxParams.BloomGainDefault, 0.25f, 1e-5f)).IsTrue();
        System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", null);
    }

    [TestCase]
    public void EvaluateLightingFrame_AtNightInModernMode_MoonBelowHorizon_HasZeroSunEnergyAndNoShadow()
    {
        System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", "modern");
        try
        {
            var skyTheme = new SkyTheme();
            var lighting = new LightingTheme
            {
                HasMainLightColour = true,
            };
            lighting.MainLightColour[0] = 0.8f;
            lighting.MainLightColour[1] = 0.5f;
            lighting.MainLightColour[2] = 0.2f;

            var skyFrame = new SkyFrame();
            // Sun is below horizon:
            skyFrame.SunDirection[0] = 0.0f;
            skyFrame.SunDirection[1] = -0.5f;
            skyFrame.SunDirection[2] = 0.866f;

            // Legacy light rig points high in the sky:
            skyFrame.MainLightDirection[0] = 0.3f;
            skyFrame.MainLightDirection[1] = 0.7f;
            skyFrame.MainLightDirection[2] = 0.6f;

            // Moon is also below horizon:
            skyFrame.MoonDirection[0] = 0.0f;
            skyFrame.MoonDirection[1] = -0.3f;
            skyFrame.MoonDirection[2] = -0.95f;

            LightingFrame evaluated = Engine_LightingFrame.Evaluate(skyTheme, lighting, skyFrame);
            AssertThat(evaluated.SunEnergy).IsEqual(0.0f);
            AssertThat(evaluated.SunShadow).IsFalse();
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", null);
        }
    }

    [TestCase]
    public void EvaluateLightingFrame_AtNightInModernMode_MoonLowOnHorizon_AlignsWithMoon()
    {
        System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", "modern");
        try
        {
            var skyTheme = new SkyTheme();
            var lighting = new LightingTheme
            {
                HasMainLightColour = true,
            };
            lighting.MainLightColour[0] = 0.5f;
            lighting.MainLightColour[1] = 0.5f;
            lighting.MainLightColour[2] = 0.8f;

            var skyFrame = new SkyFrame();
            // Sun well below horizon:
            skyFrame.SunDirection[0] = 0.0f;
            skyFrame.SunDirection[1] = -0.5f;
            skyFrame.SunDirection[2] = 0.866f;

            // Legacy rig high in sky:
            skyFrame.MainLightDirection[0] = 0.5f;
            skyFrame.MainLightDirection[1] = 0.7f;
            skyFrame.MainLightDirection[2] = 0.5f;

            // Moon is low above horizon (e.g. elevation 0.04 in Fable coords -> ToGodot elevation 0.04):
            skyFrame.MoonDirection[0] = -0.7f;
            skyFrame.MoonDirection[1] = 0.04f;
            skyFrame.MoonDirection[2] = 0.713f;

            LightingFrame evaluated = Engine_LightingFrame.Evaluate(skyTheme, lighting, skyFrame);
            Vector3 expectedMoon = Engine_SkyFrame.ToGodot(skyFrame.MoonDirection).Normalized();
            AssertThat(Mathf.IsEqualApprox(evaluated.LightDirection[0], expectedMoon.X, 1e-4f)).IsTrue();
            AssertThat(Mathf.IsEqualApprox(evaluated.LightDirection[1], expectedMoon.Y, 1e-4f)).IsTrue();
            AssertThat(Mathf.IsEqualApprox(evaluated.LightDirection[2], expectedMoon.Z, 1e-4f)).IsTrue();
            AssertThat(evaluated.SunEnergy > 0f).IsTrue();
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", null);
        }
    }

    [TestCase]
    public void EngineSkyFrame_InModernMode_SkipsMainLightDirection()
    {
        System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", "modern");
        try
        {
            var theme = new SkyTheme();
            SkyFrame frame = Engine_SkyFrame.Evaluate(theme, 0.5f);
            AssertThat(frame.MainLightDirection[0]).IsEqual(0f);
            AssertThat(frame.MainLightDirection[1]).IsEqual(0f);
            AssertThat(frame.MainLightDirection[2]).IsEqual(0f);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", null);
        }
    }

    [TestCase]
    public void EvaluateLightingFrame_InClassicMode_UsesMainLightDirectionAtNight()
    {
        System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", "fable");
        try
        {
            var skyTheme = new SkyTheme();
            var lighting = new LightingTheme
            {
                HasMainLightColour = true,
            };
            lighting.MainLightColour[0] = 0.5f;
            lighting.MainLightColour[1] = 0.5f;
            lighting.MainLightColour[2] = 0.5f;

            var skyFrame = new SkyFrame();
            skyFrame.SunDirection[0] = 0.0f;
            skyFrame.SunDirection[1] = -0.5f;
            skyFrame.SunDirection[2] = 0.866f;

            skyFrame.MainLightDirection[0] = 0.4f;
            skyFrame.MainLightDirection[1] = 0.6f;
            skyFrame.MainLightDirection[2] = 0.69f;

            skyFrame.MoonDirection[0] = -0.3f;
            skyFrame.MoonDirection[1] = 0.2f;
            skyFrame.MoonDirection[2] = 0.9f;

            LightingFrame evaluated = Engine_LightingFrame.Evaluate(skyTheme, lighting, skyFrame);
            AssertThat(Mathf.IsEqualApprox(evaluated.LightDirection[0], skyFrame.MainLightDirection[0], 1e-4f)).IsTrue();
            AssertThat(Mathf.IsEqualApprox(evaluated.LightDirection[1], skyFrame.MainLightDirection[1], 1e-4f)).IsTrue();
            AssertThat(Mathf.IsEqualApprox(evaluated.LightDirection[2], skyFrame.MainLightDirection[2], 1e-4f)).IsTrue();
            AssertThat(evaluated.SunEnergy > 0f).IsTrue();
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("REFII_LIGHTING_MODEL", null);
        }
    }

    [TestCase]
    public void NewLampOnStick_WithoutParticleEmitterInGdb_ReceivesCandleFlameFx()
    {
        Engine_LevelFx.ClearCaches();

        var db = new GdbPatchDatabase();

        // 1. NewLamp_on_stick placement lacking CECParticleEmitter / CECParticleAttacher in GDB
        uint lampHash = 0xE5B4B895u;
        var lampRec = new GdbPatchEntry(lampHash, 0x01);
        db.Entries.Add(lampRec);

        var lampPlacement = new GdbPlacement
        {
            Hash = lampHash,
            EntityName = "NewLamp_on_stick_1",
            ParentHash = Engine_LevelFx.LampOnStickTemplate,
            X = 323.58f,
            Y = 42.57f,
            Z = 154.54f
        };

        // 2. Ordinary prop without FX
        uint benchHash = 0x12345678u;
        var benchRec = new GdbPatchEntry(benchHash, 0x01);
        db.Entries.Add(benchRec);

        var benchPlacement = new GdbPlacement
        {
            Hash = benchHash,
            EntityName = "Bench_Wood_01",
            ParentHash = 0x99999999u,
            X = 320.0f,
            Y = 40.0f,
            Z = 150.0f
        };

        AssertThat(Engine_LevelFx.IsLampOnStickPlacement(lampPlacement)).IsTrue();
        AssertThat(Engine_LevelFx.IsLampOnStickPlacement(benchPlacement)).IsFalse();

        byte[] bytes = GdbWriter.Serialize(db);
        AssertThat(GdbView.TryParse(bytes, out GdbView view, out _)).IsTrue();

        // 1. Lamp without GDB particle emitter resolves to synthetic candle flame at Prop.Layout.Light
        List<Engine_LevelFx.FxAttachment> lampAttachments =
            Engine_LevelFx.ResolvePlacementAttachments(lampPlacement, new List<GdbView> { view });
        AssertThat(lampAttachments.Count).IsEqual(1);
        AssertThat(lampAttachments[0].Effect).IsEqual(Engine_LevelFx.CandleFlame);
        AssertThat(lampAttachments[0].Dummy).IsEqual("Prop.Layout.Light");

        // 2. Ordinary prop without FX resolves to 0 attachments
        List<Engine_LevelFx.FxAttachment> benchAttachments =
            Engine_LevelFx.ResolvePlacementAttachments(benchPlacement, new List<GdbView> { view });
        AssertThat(benchAttachments.Count).IsEqual(0);

        // 3. Regression test: if a lamp already carries an authored GDB particle emitter, it does NOT synthesize duplicates
        const string customDummy = "Prop.FX.Particle.Custom_Flame.par";
        uint customDummyHash = Engine_GameHash.Fnv1(customDummy);
        db.Names[customDummyHash] = customDummy;

        uint fxItemHash = 0x50000001u;
        var fxItem = new GdbPatchEntry(fxItemHash, 0x01);
        fxItem.Properties.Add(new GdbPatchProperty(Engine_LevelFx.ParticleEmitter, GdbFieldType.String, 0x11223344u));
        fxItem.Properties.Add(new GdbPatchProperty(Engine_LevelFx.DummyObject, GdbFieldType.String, customDummyHash));
        db.Entries.Add(fxItem);

        lampRec.Properties.Add(new GdbPatchProperty(Engine_LevelFx.ParticleEmitter, GdbFieldType.Reference, fxItemHash));
        byte[] authoredBytes = GdbWriter.Serialize(db);
        AssertThat(GdbView.TryParse(authoredBytes, out GdbView authoredView, out _)).IsTrue();

        List<Engine_LevelFx.FxAttachment> authoredLampAttachments =
            Engine_LevelFx.ResolvePlacementAttachments(lampPlacement, new List<GdbView> { authoredView });
        AssertThat(authoredLampAttachments.Count).IsEqual(1);
        AssertThat(authoredLampAttachments[0].Effect).IsEqual(0x11223344u);
        AssertThat(authoredLampAttachments[0].Dummy).IsEqual(customDummy);

        Engine_LevelFx.ClearCaches();
    }
}
