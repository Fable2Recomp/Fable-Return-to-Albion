namespace Refii.Tests.Launcher;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Gdb;
using Refii.Launcher.Core;
using Refii.Level;
using Refii.Lighting;

[TestSuite]
public class LightingSettingsTests
{
    [TestCase]
    public void TestLightingPresetsResolveCorrectly()
    {
        var model = new RuntimeSettingsModel();

        // 1. Performance preset
        model.ApplyLightingPreset("performance");
        AssertThat(model.CurrentLightingPreset()).IsEqual("performance");
        AssertThat(model.Values["placed_light_range"]).IsEqual("45.0");
        AssertThat(model.Values["placed_shadow_range"]).IsEqual("30.0");
        AssertThat(model.Values["terrain_ao_resolution"]).IsEqual("2048");
        AssertThat(model.Values["static_shadow_resolution"]).IsEqual("2048");

        // 2. Balanced preset
        model.ApplyLightingPreset("balanced");
        AssertThat(model.CurrentLightingPreset()).IsEqual("balanced");
        AssertThat(model.Values["placed_light_range"]).IsEqual("65.0");
        AssertThat(model.Values["placed_shadow_range"]).IsEqual("50.0");
        AssertThat(model.Values["terrain_ao_resolution"]).IsEqual("4096");
        AssertThat(model.Values["static_shadow_resolution"]).IsEqual("2048");

        // 3. Quality preset
        model.ApplyLightingPreset("quality");
        AssertThat(model.CurrentLightingPreset()).IsEqual("quality");
        AssertThat(model.Values["placed_light_range"]).IsEqual("85.0");
        AssertThat(model.Values["placed_shadow_range"]).IsEqual("70.0");
        AssertThat(model.Values["terrain_ao_resolution"]).IsEqual("8192");
        AssertThat(model.Values["static_shadow_resolution"]).IsEqual("4096");
    }

    [TestCase]
    public void TestModifyingAnySettingSwitchesToCustom()
    {
        var model = new RuntimeSettingsModel();
        model.ApplyLightingPreset("balanced");
        AssertThat(model.CurrentLightingPreset()).IsEqual("balanced");

        // Tweak light range
        model.Values["placed_light_range"] = "50.0";
        AssertThat(model.CurrentLightingPreset()).IsEqual("custom");

        // Restore it
        model.Values["placed_light_range"] = "65.0";
        AssertThat(model.CurrentLightingPreset()).IsEqual("balanced");

        // Tweak shadow range
        model.Values["placed_shadow_range"] = "40.0";
        AssertThat(model.CurrentLightingPreset()).IsEqual("custom");

        // Restore it
        model.Values["placed_shadow_range"] = "50.0";
        AssertThat(model.CurrentLightingPreset()).IsEqual("balanced");
    }

    [TestCase]
    public void TestLightingCategoryAndHdrPlacement()
    {
        RuntimeCVarDefinition? presetDef = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "lighting_preset");
        AssertThat(presetDef).IsNotNull();
        AssertThat(presetDef!.Category).IsEqual(RuntimeCVarsGodot.LightingCategory);
        AssertThat(RuntimeSettingsModel.IsAdvanced(presetDef)).IsFalse();

        RuntimeCVarDefinition? hdrDef = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "hdr_output");
        AssertThat(hdrDef).IsNotNull();
        AssertThat(hdrDef!.Category).IsEqual(RuntimeCVarsGodot.LightingCategory);
        AssertThat(RuntimeSettingsModel.IsAdvanced(hdrDef)).IsFalse();

        // retired budget cvars should not be registered
        AssertThat(RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "placed_light_budget")).IsNull();
        AssertThat(RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "placed_shadow_static_budget")).IsNull();
        AssertThat(RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "placed_shadow_dynamic_budget")).IsNull();
        AssertThat(RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "placed_light_shadow_budget")).IsNull();
    }

    [TestCase]
    public void TestBuildGroupsExcludesAdvancedLightingSettings()
    {
        var model = new RuntimeSettingsModel();
        // Even with ShowAdvanced = true, advanced lighting settings must NOT be in the main grid
        model.ShowAdvanced = true;
        var groups = model.BuildGroups();
        var lightingGroup = groups.FirstOrDefault(g => g.Category == RuntimeCVarsGodot.LightingCategory);
        AssertThat(lightingGroup).IsNotNull();

        var names = lightingGroup!.Definitions.Select(d => d.Name).ToList();
        AssertThat(names).Contains("lighting_preset");
        AssertThat(names).Contains("hdr_output");
        AssertThat(names).NotContains("placed_shadow_range");
        AssertThat(names).NotContains("sun_shadows");
        AssertThat(names).NotContains("gi_half_resolution");
    }

    [TestCase]
    public void TestEngineRefiiConfigLightingParsers()
    {
        var emptyConfig = new Dictionary<string, string>();
        AssertThat(Refii.Core.Engine_RefiiConfig.Float(emptyConfig, "placed_light_range", 65.0f)).IsEqual(65.0f);
        AssertThat(Refii.Core.Engine_RefiiConfig.Float(emptyConfig, "placed_shadow_range", 50.0f)).IsEqual(50.0f);

        var configured = new Dictionary<string, string>
        {
            ["placed_light_range"] = "45.0",
            ["placed_shadow_range"] = "30.0",
        };
        AssertThat(Refii.Core.Engine_RefiiConfig.Float(configured, "placed_light_range", 65.0f)).IsEqual(45.0f);
        AssertThat(Refii.Core.Engine_RefiiConfig.Float(configured, "placed_shadow_range", 50.0f)).IsEqual(30.0f);

        AssertThat(Refii.Lighting.Engine_LightingFrame.OutdoorFogScale).IsEqual(1.0f);
        AssertThat(Refii.Lighting.Engine_PostFxParams.InteriorVolumetricShaftScale).IsEqual(1.0f);
        AssertThat(Refii.Lighting.Engine_PostFxParams.OutdoorVolumetricShaftScale).IsEqual(1.0f);

        bool prevInterior = Refii.Lighting.Engine_LightingFrame.CurrentInterior;
        try
        {
            Refii.Lighting.Engine_LightingFrame.CurrentInterior = false;
            AssertThat(Refii.Lighting.Engine_PostFxParams.VolumetricShaftScale).IsEqualApprox(0.80f, 1e-4f);
            Refii.Lighting.Engine_LightingFrame.CurrentInterior = true;
            AssertThat(Refii.Lighting.Engine_PostFxParams.VolumetricShaftScale).IsEqualApprox(0.50f, 1e-4f);
        }
        finally
        {
            Refii.Lighting.Engine_LightingFrame.CurrentInterior = prevInterior;
        }
    }

    [TestCase]
    public void TestFogCvarDefaultsAndVolumetricFogRegistration()
    {
        RuntimeCVarDefinition? interiorFogDef = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "interior_fog_scale");
        AssertThat(interiorFogDef).IsNotNull();
        AssertThat(interiorFogDef!.DefaultValue).IsEqual("1.0");
        AssertThat(interiorFogDef.Category).IsEqual(RuntimeCVarsGodot.LightingCategory);

        RuntimeCVarDefinition? outdoorFogDef = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "outdoor_fog_scale");
        AssertThat(outdoorFogDef).IsNotNull();
        AssertThat(outdoorFogDef!.DefaultValue).IsEqual("1.0");
        AssertThat(outdoorFogDef.Category).IsEqual(RuntimeCVarsGodot.LightingCategory);

        RuntimeCVarDefinition? outdoorShaftDef = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "outdoor_volumetric_shaft_scale");
        AssertThat(outdoorShaftDef).IsNotNull();
        AssertThat(outdoorShaftDef!.DefaultValue).IsEqual("1.0");
        AssertThat(outdoorShaftDef.Category).IsEqual(RuntimeCVarsGodot.LightingCategory);

        RuntimeCVarDefinition? interiorShaftDef = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "interior_volumetric_shaft_scale");
        AssertThat(interiorShaftDef).IsNotNull();
        AssertThat(interiorShaftDef!.DefaultValue).IsEqual("1.0");
        AssertThat(interiorShaftDef.Category).IsEqual(RuntimeCVarsGodot.LightingCategory);

        RuntimeCVarDefinition? volFogDef = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "volumetric_fog");
        AssertThat(volFogDef).IsNotNull();
        AssertThat(volFogDef!.DefaultValue).IsEqual("true");
        AssertThat(volFogDef.Type).IsEqual(RuntimeCVarType.Bool);
        AssertThat(volFogDef.Category).IsEqual(RuntimeCVarsGodot.LightingCategory);
        AssertThat(RuntimeSettingsModel.IsAdvanced(volFogDef)).IsTrue();
    }

    [TestCase]
    public void TestRootSchemaPrototypeDoesNotZeroEnvThemeGlobalOrUnstatedFogFields()
    {
        static uint RawFloat(float v) => unchecked((uint)BitConverter.SingleToInt32Bits(v));

        // Shared GDB (`environmentthemes.gdb`): root schema prototypes with 0.0 fields and no parent,
        // plus `EnvThemeGlobal` stating Rayleigh = 1, Mie = 1, FarDistance = 1000, CloseFogMaxDistance = 1000.
        var sharedDb = new GdbPatchDatabase();

        const uint RootSkyProto = 0xEC362230u;
        var rootSky = new GdbPatchEntry(RootSkyProto, 0x01);
        rootSky.Properties.Add(new GdbPatchProperty(EnvHash.SkyBetaRayleighMultiplier, GdbFieldType.Float, RawFloat(0f)));
        rootSky.Properties.Add(new GdbPatchProperty(EnvHash.SkyBetaMieMultiplier, GdbFieldType.Float, RawFloat(0f)));
        sharedDb.Entries.Add(rootSky);

        const uint RootFogProto = 0xD3516348u;
        var rootFog = new GdbPatchEntry(RootFogProto, 0x01);
        rootFog.Properties.Add(new GdbPatchProperty(EnvHash.NearDistance, GdbFieldType.Float, RawFloat(0f)));
        rootFog.Properties.Add(new GdbPatchProperty(EnvHash.NearDensity, GdbFieldType.Float, RawFloat(0f)));
        rootFog.Properties.Add(new GdbPatchProperty(EnvHash.FarDistance, GdbFieldType.Float, RawFloat(0f)));
        rootFog.Properties.Add(new GdbPatchProperty(EnvHash.FarDensity, GdbFieldType.Float, RawFloat(0f)));
        rootFog.Properties.Add(new GdbPatchProperty(EnvHash.FoggingStart, GdbFieldType.Float, RawFloat(0f)));
        rootFog.Properties.Add(new GdbPatchProperty(EnvHash.CloseFogMaxDistance, GdbFieldType.Float, RawFloat(0f)));
        sharedDb.Entries.Add(rootFog);

        const uint GlobalSkyHash = 0x11110001u;
        var globalSky = new GdbPatchEntry(GlobalSkyHash, 0x01);
        globalSky.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, RootSkyProto));
        globalSky.Properties.Add(new GdbPatchProperty(EnvHash.SkyBetaRayleighMultiplier, GdbFieldType.Float, RawFloat(1.0f)));
        globalSky.Properties.Add(new GdbPatchProperty(EnvHash.SkyBetaMieMultiplier, GdbFieldType.Float, RawFloat(1.0f)));
        sharedDb.Entries.Add(globalSky);

        const uint GlobalFogHash = 0x11110002u;
        var globalFog = new GdbPatchEntry(GlobalFogHash, 0x01);
        globalFog.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, RootFogProto));
        globalFog.Properties.Add(new GdbPatchProperty(EnvHash.FarDistance, GdbFieldType.Float, RawFloat(1000f)));
        globalFog.Properties.Add(new GdbPatchProperty(EnvHash.CloseFogMaxDistance, GdbFieldType.Float, RawFloat(1000f)));
        sharedDb.Entries.Add(globalFog);

        var globalTheme = new GdbPatchEntry(EnvHash.EnvThemeGlobal, 0x01);
        globalTheme.Properties.Add(new GdbPatchProperty(EnvHash.Sky, GdbFieldType.Reference, GlobalSkyHash));
        globalTheme.Properties.Add(new GdbPatchProperty(EnvHash.Fogging, GdbFieldType.Reference, GlobalFogHash));
        sharedDb.Entries.Add(globalTheme);

        // Level GDB: DaySet theme whose Fogging record states only CloseFogColour (like Guild Hall)
        // and whose Sky record states only SkyColour, with parent pointers to RootFogProto / RootSkyProto.
        var levelDb = new GdbPatchDatabase();
        const uint LevelFogHash = 0x22220001u;
        var levelFog = new GdbPatchEntry(LevelFogHash, 0x01);
        levelFog.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, RootFogProto));
        levelFog.Properties.Add(new GdbPatchProperty(EnvHash.CloseFogColourRed, GdbFieldType.Float, RawFloat(25f)));
        levelFog.Properties.Add(new GdbPatchProperty(EnvHash.CloseFogColourGreen, GdbFieldType.Float, RawFloat(25f)));
        levelFog.Properties.Add(new GdbPatchProperty(EnvHash.CloseFogColourBlue, GdbFieldType.Float, RawFloat(25f)));
        levelFog.Properties.Add(new GdbPatchProperty(EnvHash.CloseFogColourFactor, GdbFieldType.Float, RawFloat(0.25f)));
        levelDb.Entries.Add(levelFog);

        const uint LevelSkyHash = 0x22220002u;
        var levelSky = new GdbPatchEntry(LevelSkyHash, 0x01);
        levelSky.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, RootSkyProto));
        levelSky.Properties.Add(new GdbPatchProperty(EnvHash.SkyColourRed, GdbFieldType.Float, RawFloat(255f)));
        levelSky.Properties.Add(new GdbPatchProperty(EnvHash.SkyColourGreen, GdbFieldType.Float, RawFloat(255f)));
        levelSky.Properties.Add(new GdbPatchProperty(EnvHash.SkyColourBlue, GdbFieldType.Float, RawFloat(255f)));
        levelSky.Properties.Add(new GdbPatchProperty(EnvHash.SkyColourFactor, GdbFieldType.Float, RawFloat(6.0f)));
        levelDb.Entries.Add(levelSky);

        const uint DayThemeHash = 0x22220003u;
        var dayTheme = new GdbPatchEntry(DayThemeHash, 0x01);
        dayTheme.Properties.Add(new GdbPatchProperty(EnvHash.Sky, GdbFieldType.Reference, LevelSkyHash));
        dayTheme.Properties.Add(new GdbPatchProperty(EnvHash.Fogging, GdbFieldType.Reference, LevelFogHash));
        levelDb.Entries.Add(dayTheme);

        const uint DayEntryHash = 0x22220004u;
        var dayEntry = new GdbPatchEntry(DayEntryHash, 0x01);
        dayEntry.Properties.Add(new GdbPatchProperty(EnvHash.TimeOfDay, GdbFieldType.Float, RawFloat(12.0f)));
        dayEntry.Properties.Add(new GdbPatchProperty(EnvHash.Theme, GdbFieldType.Reference, DayThemeHash));
        levelDb.Entries.Add(dayEntry);

        const uint DaySetHash = 0x22220005u;
        var daySet = new GdbPatchEntry(DaySetHash, 0x01);
        daySet.Properties.Add(new GdbPatchProperty(0x33330001u, GdbFieldType.Reference, DayEntryHash));
        levelDb.Entries.Add(daySet);

        var levelData = new GdbPatchEntry(EnvHash.LevelData, 0x01);
        levelData.Properties.Add(new GdbPatchProperty(EnvHash.EnvironmentThemeDaySet, GdbFieldType.Reference, DaySetHash));
        levelData.Properties.Add(new GdbPatchProperty(EnvHash.EnvThemeGlobal, GdbFieldType.Reference, EnvHash.EnvThemeGlobal));
        levelDb.Entries.Add(levelData);

        var chain = new List<byte[]>
        {
            GdbWriter.Serialize(levelDb),
            GdbWriter.Serialize(sharedDb),
        };

        bool ok = Engine_EnvironmentTheme.TryExtractSky(chain, 0.5f, out SkyTheme extracted);
        AssertThat(ok).IsTrue();

        // 1. Root schema prototype 0.0 values must NOT override EnvThemeGlobal Rayleigh/Mie/FarDistance/CloseFogMaxDistance
        AssertThat(extracted.Rayleigh).IsEqualApprox(1.0f, 1e-4f);
        AssertThat(extracted.Mie).IsEqualApprox(1.0f, 1e-4f);
        AssertThat(extracted.HasFarDistance).IsTrue();
        AssertThat(extracted.FarDistance).IsEqualApprox(1000f, 1e-3f);
        AssertThat(extracted.HasCloseFogMaxDistance).IsTrue();
        AssertThat(extracted.CloseFogMaxDistance).IsEqualApprox(1000f, 1e-3f);

        // 2. Unstated FoggingStart must NOT be marked stated from RootFogProto (stays unfogged 1000000)
        AssertThat(extracted.HasFoggingStart).IsFalse();

        // 3. Unclamped linear scene-referred sky colour (> 4.0 preserved)
        AssertThat(extracted.SkyColour[0]).IsEqualApprox(6.0f, 1e-3f);
        SkyFrame skyFrame = Engine_SkyFrame.Evaluate(extracted, 0.5f);
        AssertThat(skyFrame.SkyTop[0]).IsEqualApprox(6.0f, 1e-3f);

        AtmosphereInputs inputs = Engine_SkyAtmosphere.FromFrame(skyFrame, extracted);
        AssertThat(inputs.FoggingStart).IsEqualApprox(Engine_SkyAtmosphere.UnfoggedStart, 1f);
        AssertThat(inputs.FarDistance).IsEqualApprox(1000f, 1e-3f);
        AssertThat(inputs.NearDensity).IsEqualApprox(Engine_SkyAtmosphere.DefaultNearDensity, 1e-4f);
    }

    [TestCase]
    public void TestInteriorFogContrastFactorAndVolumetricMediumCalibration()
    {
        // 1. Guild Cave (thagscave): CloseFogColour ~ (0.049, 0.049, 0.049), Ambient ~ (0.0185, 0.0185, 0.0185)
        //    Ratio = 0.049 / (2.5 * max(0.0185, 0.02)) = 0.98 -> clamped to 1.0 (no extra boost).
        var guildTheme = new SkyTheme
        {
            HasFogColour = true,
            HasNearFog = true,
            HasFarFog = true,
            HasFarDistance = true, FarDistance = 1000f,
            HasFarDensity = true, FarDensity = 4.0f,
            HasFoggingStart = true, FoggingStart = 0f,
            HasCloseFogMaxDistance = true, CloseFogMaxDistance = 1000f,
        };
        guildTheme.FogColour[0] = 0.049f;
        guildTheme.FogColour[1] = 0.049f;
        guildTheme.FogColour[2] = 0.049f;

        SkyFrame guildSkyFrame = Engine_SkyFrame.Evaluate(guildTheme, 0.5f);
        AtmosphereInputs guildInputs = Engine_SkyAtmosphere.FromFrame(guildSkyFrame, guildTheme);
        AtmosphereState guildAtmos = Engine_SkyAtmosphere.Compute(guildInputs);

        var guildLightFrame = new LightingFrame
        {
            HasSky = false,
            HasAmbient = true,
            AmbientEnergy = 1.0f,
        };
        guildLightFrame.AmbientColour[0] = 0.0185f;
        guildLightFrame.AmbientColour[1] = 0.0185f;
        guildLightFrame.AmbientColour[2] = 0.0185f;

        float guildKappa = Engine_LevelLighting.ComputeInteriorFogContrastFactor(guildAtmos, guildLightFrame);
        AssertThat(guildKappa).IsEqualApprox(1.0f, 1e-4f);

        // 2. Howling Halls (palacecave): CloseFogColour ~ (0.0882, 0.2510, 0.3882) (Y ~ 0.2263),
        //    Ambient ~ (0.0196, 0.0209, 0.0366) (Y ~ 0.0218).
        //    Ratio = 0.2263 / (2.5 * 0.0218) ~ 4.14 -> lifts fog density/emission to match Classic cave contrast.
        var howlingTheme = new SkyTheme
        {
            HasFogColour = true,
            HasNearFog = true,
            HasFarFog = true,
            HasNearDistance = true, NearDistance = 12f,
            HasNearDensity = true, NearDensity = 0.5f,
            HasFarDistance = true, FarDistance = 200f,
            HasFarDensity = true, FarDensity = 2.0f,
            HasFoggingStart = true, FoggingStart = 5f,
            HasCloseFogMaxDistance = true, CloseFogMaxDistance = 40f,
        };
        howlingTheme.FogColour[0] = 0.0882f;
        howlingTheme.FogColour[1] = 0.2510f;
        howlingTheme.FogColour[2] = 0.3882f;
        howlingTheme.SkyColour[0] = 0.0686f;
        howlingTheme.SkyColour[1] = 0.1451f;
        howlingTheme.SkyColour[2] = 0.1922f;

        SkyFrame howlingSkyFrame = Engine_SkyFrame.Evaluate(howlingTheme, 0.5f);
        AtmosphereInputs howlingInputs = Engine_SkyAtmosphere.FromFrame(howlingSkyFrame, howlingTheme);
        AtmosphereState howlingAtmos = Engine_SkyAtmosphere.Compute(howlingInputs);

        var howlingLightFrame = new LightingFrame
        {
            HasSky = false,
            HasAmbient = true,
            AmbientEnergy = 1.0f,
        };
        howlingLightFrame.AmbientColour[0] = 0.0196f;
        howlingLightFrame.AmbientColour[1] = 0.0209f;
        howlingLightFrame.AmbientColour[2] = 0.0366f;

        float howlingKappa = Engine_LevelLighting.ComputeInteriorFogContrastFactor(howlingAtmos, howlingLightFrame);
        AssertThat(howlingKappa).IsGreater(4.0f);
        AssertThat(howlingKappa).IsLessEqual(4.5f);

        float sigma = Engine_LevelLighting.ComputeVolumetricMediumParameters(
            howlingAtmos,
            howlingKappa,
            out Godot.Color albedo,
            out Godot.Color emission,
            out float emissionEnergy);

        AssertThat(sigma).IsGreater(0.002f);
        AssertThat(albedo.B).IsGreater(albedo.R);
        AssertThat(emission.B).IsGreater(emission.R);
        AssertThat(emissionEnergy).IsGreater(0.25f);
    }

    [TestCase]
    public void TestFoldPaintedSkyAndDominantLayerSeeding()
    {
        // 1. FoldPaintedSky merges stated painted DaySet overrides into the base SkyTheme:
        var baseTheme = new SkyTheme
        {
            HasAny = true,
            HasFarDistance = true, FarDistance = 1000f,
            HasFarDensity = true, FarDensity = 0f,
            HasFoggingStart = false, FoggingStart = 1000000f,
        };

        var paintedWraithmarsh = new SkyTheme
        {
            HasAny = true,
            HasFarDistance = true, FarDistance = 85f,
            HasFarDensity = true, FarDensity = 1.8f,
            HasFoggingStart = true, FoggingStart = 0f,
            HasCloseFogMaxDistance = true, CloseFogMaxDistance = 40f,
            HasFogColour = true,
        };
        paintedWraithmarsh.FogColour[0] = 0.12f;
        paintedWraithmarsh.FogColour[1] = 0.16f;
        paintedWraithmarsh.FogColour[2] = 0.18f;

        SkyTheme folded = Engine_EnvironmentTheme.FoldPaintedSky(baseTheme, paintedWraithmarsh, 1.0f);
        AssertThat(folded.HasFoggingStart).IsTrue();
        AssertThat(folded.FoggingStart).IsEqualApprox(0f, 1e-4f);
        AssertThat(folded.FarDistance).IsEqualApprox(85f, 1e-3f);
        AssertThat(folded.FarDensity).IsEqualApprox(1.8f, 1e-4f);
        AssertThat(folded.HasFogColour).IsTrue();
        AssertThat(folded.FogColour[2]).IsEqualApprox(0.18f, 1e-4f);

        // 2. SeedDominantLayer activates the dominant painted layer (>= 50% of painted cells) at weight 1.0:
        var cells = new uint[10];
        for (int i = 0; i < 9; i++)
        {
            cells[i] = 0x27DE3408u;
        }
        cells[9] = 0x538F6575u;

        var paintedGrid = new Engine_PaintedDaySets();
        paintedGrid.Add(new Engine_PaintedDaySets.Grid
        {
            Width = 10,
            Height = 1,
            Cells = cells,
        });
        paintedGrid.SeedDominantLayer();

        AssertThat(paintedGrid.Layers.Count).IsEqual(1);
        AssertThat(paintedGrid.Layers[0].Hash).IsEqual(0x27DE3408u);
        AssertThat(paintedGrid.Layers[0].Weight).IsEqualApprox(1.0f, 1e-4f);
    }

    [TestCase]
    public void TestSkyInScatterAndHdrVolumetricMedium()
    {
        var templeTheme = new SkyTheme
        {
            HasFogColour = true,
            HasNearFog = true,
            HasFarFog = true,
            HasNearDistance = true, NearDistance = 10f,
            HasNearDensity = true, NearDensity = 0.4f,
            HasFarDistance = true, FarDistance = 150f,
            HasFarDensity = true, FarDensity = 1.5f,
            HasFoggingStart = true, FoggingStart = 0f,
            HasCloseFogMaxDistance = true, CloseFogMaxDistance = 16f,
            HasSunIntensity = true, SunIntensity = 12.0f,
            HasSkyColour = true,
            HasComplementaryColour = true,
        };
        templeTheme.FogColour[0] = 0.02f;
        templeTheme.FogColour[1] = 0.02f;
        templeTheme.FogColour[2] = 0.02f;
        templeTheme.SkyColour[0] = 1.0f;
        templeTheme.SkyColour[1] = 0.95f;
        templeTheme.SkyColour[2] = 0.80f;
        templeTheme.ComplementaryColour[0] = 0.85f;
        templeTheme.ComplementaryColour[1] = 0.80f;
        templeTheme.ComplementaryColour[2] = 0.70f;

        SkyFrame frame = Engine_SkyFrame.Evaluate(templeTheme, 0.5f);
        AtmosphereInputs inputs = Engine_SkyAtmosphere.FromFrame(frame, templeTheme);
        AtmosphereState state = Engine_SkyAtmosphere.Compute(inputs);

        float[] lut = Engine_SkyAtmosphere.BuildInScatterLut(state);
        Engine_SkyAtmosphere.SampleInScatter(state, 0.0f, out float sR0, out float sG0, out float sB0);
        AssertThat(sR0).IsEqualApprox(lut[0], 1e-4f);
        AssertThat(sG0).IsEqualApprox(lut[1], 1e-4f);
        AssertThat(sB0).IsEqualApprox(lut[2], 1e-4f);

        float sigma = Engine_LevelLighting.ComputeVolumetricMediumParameters(
            state,
            1.0f,
            out Godot.Color _,
            out Godot.Color linearTint,
            out Godot.Color _,
            out float emissionEnergy);

        AssertThat(sigma).IsGreater(0.0025f);
        // Unclamped HDR sky in-scatter lifts emissionEnergy well above the dim CloseFogColour (0.02),
        // and exceeds 1.0 in scene-referred HDR space when SunIntensity is high.
        AssertThat(emissionEnergy).IsGreater(1.0f);
        AssertThat(linearTint.R).IsGreater(0.5f);
    }

    [TestCase]
    public void TestVolumetricLightShaftsHybridCapsuleLinkingAndSnapshot()
    {
        var capsules = new Engine_VolumetricLightShafts.VolumetricCapsule[]
        {
            // Capsule 0: Hero standing at (2, 0, 0), height 1.7m, radius 0.28m
            new(new Godot.Vector3(2f, 0.3f, 0f), 0.28f, new Godot.Vector3(2f, 1.7f, 0f), 0.14f),
            // Capsule 1: Dog standing at (5, 0, -1), horizontal spine
            new(new Godot.Vector3(5f, 0.35f, -1.3f), 0.22f, new Godot.Vector3(5f, 0.35f, -0.7f), 0.11f),
            // Capsule 2: Distant NPC at (30, 0, 0) outside light radius
            new(new Godot.Vector3(30f, 0.3f, 0f), 0.30f, new Godot.Vector3(30f, 1.7f, 0f), 0.15f),
        };

        // Light at origin with 6m range reaches Capsule 0 (2m) and Capsule 1 (~5.1m), but not Capsule 2 (30m).
        uint mask6m = Engine_VolumetricLightShafts.ComputeLightCapsuleMask(
            Godot.Vector3.Zero, 6.0f, capsules);
        AssertThat(mask6m).IsEqual(0b011u);

        // Light at (-15, 0, 0) with 5m range reaches no capsules.
        uint maskEmpty = Engine_VolumetricLightShafts.ComputeLightCapsuleMask(
            new Godot.Vector3(-15f, 0f, 0f), 5.0f, capsules);
        AssertThat(maskEmpty).IsEqual(0u);

        // Verify snapshot publishing with omni + spot + synthetic flame lights and mist boxes.
        Engine_VolumetricLightShafts.SetMedium(0.012f, new Godot.Color(0.8f, 0.9f, 1.0f), 0.65f, 4.0f);
        AssertThat(Engine_VolumetricLightShafts.HasActiveMedium).IsTrue();

        var lights = new Engine_VolumetricLightShafts.ShaftLightInput[]
        {
            new(
                Position: new Godot.Vector3(0f, 2f, 0f),
                StatedRange: 7.0f,
                NodeRange: 7.0f,
                LinearColor: new Godot.Vector3(1.0f, 0.35f, 0.05f),
                Energy: 1.4f,
                FogEnergyScale: 28.0f,
                AtlasRect: new Godot.Vector4(0f, 0f, 0.25f, 0f),
                IndoorFromOutside: false,
                SpotDir: Godot.Vector3.Zero,
                SpotCosHalfAngle: -2.0f),
            new(
                Position: new Godot.Vector3(4f, 4f, -1f),
                StatedRange: 10.0f,
                NodeRange: 10.0f,
                LinearColor: new Godot.Vector3(0.9f, 0.95f, 1.0f),
                Energy: 16.0f,
                FogEnergyScale: 4.5f,
                AtlasRect: new Godot.Vector4(0.25f, 0f, 0.25f, 1f),
                IndoorFromOutside: true,
                SpotDir: new Godot.Vector3(0f, -1f, 0f),
                SpotCosHalfAngle: 0.7071f),
        };

        var mists = new Engine_VolumetricLightShafts.MistVolumeSnapshot[]
        {
            new(
                Min: new Godot.Vector3(-20f, -2f, -20f),
                Extinction: 0.08f,
                Max: new Godot.Vector3(20f, 3f, 20f),
                Flatten: 0.2f,
                LinearAlbedo: new Godot.Vector3(0.7f, 0.8f, 0.85f),
                NoiseScaleOffset: new Godot.Vector4(0.05f, 0.05f, 1.2f, 0.8f)),
        };

        Engine_VolumetricLightShafts.Update(Godot.Vector3.Zero, lights, mists, capsules);

        var outLights = new Engine_VolumetricLightShafts.ShaftLightInput[4];
        var outMists = new Engine_VolumetricLightShafts.MistVolumeSnapshot[4];
        var outCapsules = new Engine_VolumetricLightShafts.VolumetricCapsule[4];

        bool ok = Engine_VolumetricLightShafts.TrySnapshot(
            outLights,
            out int lCount,
            outMists,
            out int mCount,
            outCapsules,
            out int cCount,
            out Godot.Vector3 medScat,
            out float medExt,
            out float medAniso,
            out float fogStart);

        AssertThat(ok).IsTrue();
        AssertThat(lCount).IsEqual(2);
        AssertThat(mCount).IsEqual(1);
        AssertThat(cCount).IsEqual(3);
        AssertThat(medExt).IsEqualApprox(0.012f, 1e-5f);
        AssertThat(medAniso).IsEqualApprox(0.65f, 1e-4f);
        AssertThat(fogStart).IsEqualApprox(4.0f, 1e-4f);
        AssertThat(medScat.Z).IsGreater(0.01f);

        // Light 0 (synthetic flame at (0,2,0), range 7m) automatically links to Capsule 0 & Capsule 1.
        AssertThat(outLights[0].CasterMask).IsEqual(0b011u);
        AssertThat(outLights[0].FogEnergyScale).IsEqualApprox(28.0f, 1e-4f);
        AssertThat(outLights[0].SpotCosHalfAngle).IsLess(-1.0f);

        // Light 1 (spotlight at (4,4,-1), range 10m) preserves spot axis and cosine half-angle.
        AssertThat(outLights[1].SpotCosHalfAngle).IsEqualApprox(0.7071f, 1e-4f);
        AssertThat(outLights[1].SpotDir.Y).IsEqualApprox(-1.0f, 1e-4f);
        AssertThat(outLights[1].IndoorFromOutside).IsTrue();

        // HideVolume clears active snapshot so compute pass early-outs with 0 dispatches.
        Engine_VolumetricLightShafts.HideVolume();
        bool afterHide = Engine_VolumetricLightShafts.TrySnapshot(
            outLights, out int afterL, outMists, out _, outCapsules, out _, out _, out _, out _, out _);
        AssertThat(afterHide).IsFalse();
        AssertThat(afterL).IsEqual(0);
    }

    [TestCase]
    public void TestBowerstoneDaytimeStaysThinWhileWraithmarshRemainsFoggy()
    {
        // Bowerstone Market daytime: FoggingStart=16, NearDistance=25 (0.02), FarDistance=50 (0.07)
        var bwsDay = new SkyTheme
        {
            HasFogColour = true,
            HasNearFog = true,
            HasFarFog = true,
            HasNearDistance = true, NearDistance = 25f,
            HasNearDensity = true, NearDensity = 0.02f,
            HasFarDistance = true, FarDistance = 50f,
            HasFarDensity = true, FarDensity = 0.07f,
            HasFoggingStart = true, FoggingStart = 16f,
            HasCloseFogMaxDistance = true, CloseFogMaxDistance = 50f,
        };
        bwsDay.FogColour[0] = 0.45f;
        bwsDay.FogColour[1] = 0.55f;
        bwsDay.FogColour[2] = 0.70f;

        AtmosphereState bwsState = Engine_SkyAtmosphere.Compute(
            Engine_SkyAtmosphere.FromFrame(Engine_SkyFrame.Evaluate(bwsDay, 0.5f), bwsDay));
        float bwsSigma = Engine_LevelLighting.ComputeVolumetricMediumParameters(
            bwsState, 1.0f, out _, out _, out _, out _);

        // Daytime market haze stays very light (< 0.0010 m^-1) so hero/street visibility is crystal clear.
        AssertThat(bwsSigma).IsGreater(0.0001f);
        AssertThat(bwsSigma).IsLess(0.0010f);

        // Elevated Wraithmarsh: FoggingStart=0, FarDistance=85, FarDensity=1.8
        var wraith = new SkyTheme
        {
            HasFogColour = true,
            HasNearFog = true,
            HasFarFog = true,
            HasNearDistance = true, NearDistance = 15f,
            HasNearDensity = true, NearDensity = 0.45f,
            HasFarDistance = true, FarDistance = 85f,
            HasFarDensity = true, FarDensity = 1.8f,
            HasFoggingStart = true, FoggingStart = 0f,
            HasCloseFogMaxDistance = true, CloseFogMaxDistance = 40f,
        };
        wraith.FogColour[0] = 0.12f;
        wraith.FogColour[1] = 0.16f;
        wraith.FogColour[2] = 0.18f;

        AtmosphereState wraithState = Engine_SkyAtmosphere.Compute(
            Engine_SkyAtmosphere.FromFrame(Engine_SkyFrame.Evaluate(wraith, 0.5f), wraith));
        float wraithSigma = Engine_LevelLighting.ComputeVolumetricMediumParameters(
            wraithState, 1.0f, out _, out _, out _, out _);

        // Elevated Wraithmarsh maintains rich 3D atmospheric fog (> 0.0035 m^-1) even above low ground mist boxes.
        AssertThat(wraithSigma).IsGreater(0.0035f);
        AssertThat(wraithSigma).IsGreater(bwsSigma * 8.0f);
    }

    [TestCase]
    public void TestModernBaseEnergyScaleAndUnauthoredLightCalibration()
    {
        // 1. Default CVars placed_light_scale and placed_light_reach_scale remain 1.0
        RuntimeCVarDefinition? scaleDef = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "placed_light_scale");
        AssertThat(scaleDef).IsNotNull();
        AssertThat(scaleDef!.DefaultValue).IsEqual("1.0");
        RuntimeCVarDefinition? reachDef = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "placed_light_reach_scale");
        AssertThat(reachDef).IsNotNull();
        AssertThat(reachDef!.DefaultValue).IsEqual("1.0");

        // 2. ModernBaseEnergyScale doubles base energy (2.0x) and ModernBaseReachScale increases reach by 50% (1.5x)
        AssertThat(Engine_LevelLights.ModernBaseEnergyScale).IsEqualApprox(2.0f, 1e-4f);
        AssertThat(Engine_LevelLights.ModernBaseReachScale).IsEqualApprox(1.5f, 1e-4f);
        AssertThat(Engine_LevelLights.DefaultModernIntensity).IsEqualApprox(1.05f, 1e-4f);
        AssertThat(Engine_LevelLights.DefaultVolumetricFogScale).IsEqualApprox(5.0f, 1e-4f);

        bool prevInterior = Engine_LightingFrame.CurrentInterior;
        try
        {
            Engine_LightingFrame.CurrentInterior = false;
            Engine_LevelLights.ComputeEffectiveLightValues(
                rawIntensity: 1.05f,
                rawRange: 6.0f,
                isIndoorLight: false,
                out float baseEnergy,
                out float authoredRange);

            float expectedEnergy = 1.05f * Engine_LevelLights.ModernBaseEnergyScale * Engine_LevelLights.PlacedScale;
            float expectedRange = 6.0f * Engine_LevelLights.ModernBaseReachScale * Engine_LevelLights.PlacedRange;
            AssertThat(baseEnergy).IsEqualApprox(expectedEnergy, 1e-4f);
            AssertThat(authoredRange).IsEqualApprox(expectedRange, 1e-4f);
        }
        finally
        {
            Engine_LightingFrame.CurrentInterior = prevInterior;
        }
    }

    [TestCase]
    public void TestFilmicBaseAmbientScaleAndGuestRowsSplinePreservation()
    {
        // 1. Normalized ambient_light_scale CVar default stays 1.0 while FilmicBaseAmbientScale doubles ambient in Filmic mode
        RuntimeCVarDefinition? ambDef = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "ambient_light_scale");
        AssertThat(ambDef).IsNotNull();
        AssertThat(ambDef!.DefaultValue).IsEqual("1.0");
        AssertThat(Engine_LevelBakedLighting.FilmicBaseAmbientScale).IsEqualApprox(2.0f, 1e-4f);

        bool prevInterior = Engine_LightingFrame.CurrentInterior;
        try
        {
            Engine_LightingFrame.CurrentInterior = false;
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("ambient_light_scale", "1.0");

            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("modern_tone_curve", "fable");
            AssertThat(Engine_PostFxParams.FilmicCurve).IsFalse();
            AssertThat(Engine_LevelBakedLighting.EffectiveAmbientScale(null)).IsEqualApprox(1.0f, 1e-4f);

            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("modern_tone_curve", "all");
            AssertThat(Engine_PostFxParams.FilmicCurve).IsTrue();
            AssertThat(Engine_LevelBakedLighting.EffectiveAmbientScale(null)).IsEqualApprox(2.0f, 1e-4f);
        }
        finally
        {
            Engine_LightingFrame.CurrentInterior = prevInterior;
            Refii.Core.Engine_RefiiConfig.TrySetGodotCvar("modern_tone_curve", "filmic");
        }

        // 2. HostBridge.GuestRows preserves world-space spline override coordinates when normalize is false
        static uint ToBits(float f) => unchecked((uint)BitConverter.SingleToInt32Bits(f));
        uint[] words =
        {
            ToBits(-317.12f), ToBits(160.75f), ToBits(44.95f),
            ToBits(-316.95f), ToBits(161.30f), ToBits(44.96f),
            ToBits(0f), ToBits(0f), ToBits(0f),
        };
        var rows = new Godot.Vector3[3];
        Refii.Host.HostBridge.GuestRows(words, 0, rows, normalize: false);
        AssertThat(rows[0].X).IsEqualApprox(-317.12f, 1e-3f);
        AssertThat(rows[0].Y).IsEqualApprox(160.75f, 1e-3f);
        AssertThat(rows[0].Z).IsEqualApprox(44.95f, 1e-3f);
        AssertThat(rows[1].X).IsEqualApprox(-316.95f, 1e-3f);

        Refii.Host.HostBridge.GuestRows(words, 0, rows, normalize: true);
        AssertThat(rows[0].Length()).IsEqualApprox(1.0f, 1e-4f);
    }

    [TestCase]
    public void TestPlacedShadowLimitCvarAndBudgetClamping()
    {
        AssertThat(RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "omni_shadow_mode")).IsNull();

        RuntimeCVarDefinition? limitDef = RuntimeCVarSet.All.FirstOrDefault(d => d.Name == "placed_shadow_limit");
        AssertThat(limitDef).IsNotNull();
        AssertThat(limitDef!.Type).IsEqual(RuntimeCVarType.Int);
        AssertThat(limitDef.Category).IsEqual(RuntimeCVarsGodot.LightingCategory);
        AssertThat(limitDef.DefaultValue).IsEqual("2");

        AssertThat(Engine_LightShadowBudget.ParseShadowLimit(new Dictionary<string, string>())).IsEqual(2);
        AssertThat(Engine_LightShadowBudget.ParseShadowLimit(new Dictionary<string, string> { ["placed_shadow_limit"] = "0" })).IsEqual(1);
        AssertThat(Engine_LightShadowBudget.ParseShadowLimit(new Dictionary<string, string> { ["placed_shadow_limit"] = "4" })).IsEqual(4);
        AssertThat(Engine_LightShadowBudget.ParseShadowLimit(new Dictionary<string, string> { ["placed_shadow_limit"] = "99" })).IsEqual(16);
    }

}
