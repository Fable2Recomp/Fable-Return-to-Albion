namespace Refii.Tests.Launcher;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Launcher.Core;
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

        // 2. Balanced preset
        model.ApplyLightingPreset("balanced");
        AssertThat(model.CurrentLightingPreset()).IsEqual("balanced");
        AssertThat(model.Values["placed_light_range"]).IsEqual("65.0");
        AssertThat(model.Values["placed_shadow_range"]).IsEqual("50.0");

        // 3. Quality preset
        model.ApplyLightingPreset("quality");
        AssertThat(model.CurrentLightingPreset()).IsEqual("quality");
        AssertThat(model.Values["placed_light_range"]).IsEqual("85.0");
        AssertThat(model.Values["placed_shadow_range"]).IsEqual("70.0");
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
    }
}
