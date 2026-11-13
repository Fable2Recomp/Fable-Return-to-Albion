using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`lite_skip_black_lights`: a preset setting that composes FABLE_LITE_SKIP_BLACK_LIGHTS into the lite builds.</summary>
[TestSuite]
public sealed class LiteSkipBlackLightsTests
{
    private const string Define = "FABLE_LITE_SKIP_BLACK_LIGHTS";

    private static string Source(params string[] defines) =>
        string.Concat(defines.Select(d => $"#define {d}\n")) + "#include \"body.gdshaderinc\"\n";

    [TestCase]
    public void LowMediumHighAndUltraTurnItOn()
    {
        foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
        {
            AssertThat(GraphicsPresets.Values(preset)!["lite_skip_black_lights"]).IsEqual("true");
        }
    }

    /// <summary>A phone on a named preset from an earlier revision takes the key at its next start.</summary>
    [TestCase]
    public void AnOlderNamedPresetUpgradesToTakeIt()
    {
        var values = new Dictionary<string, string>
        {
            [GraphicsPresets.Key] = GraphicsPresets.Low,
            [GraphicsPresets.RevisionKey] = "16",
            ["lite_skip_black_lights"] = "false",
        };
        AssertThat(GraphicsPresets.Upgrade(values)).IsTrue();
        AssertThat(values["lite_skip_black_lights"]).IsEqual("true");
    }

    [TestCase]
    public void TheKeyIsRegisteredOnWithAPlayersName()
    {
        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == "lite_skip_black_lights");
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("true");
        AssertThat(AndroidSettingNames.Keys.Contains("lite_skip_black_lights")).IsTrue();
        AssertThat(Engine_ShaderVariant.SkipBlackLights).IsEqual(Define);
    }

    [TestCase]
    public void UnsetIsOnAndFalseIsOff()
    {
        AssertThat(Engine_ShaderVariant.SkipBlackLightsWanted(new Dictionary<string, string>())).IsTrue();
        AssertThat(Engine_ShaderVariant.SkipBlackLightsWanted(new Dictionary<string, string> { ["lite_skip_black_lights"] = "false" })).IsFalse();
    }

    [TestCase]
    public void TheProcessDefinesCarryItOnlyWhenOnAheadOfTheExtras()
    {
        string[] extra = { "FABLE_LITE_MEDIUMP" };
        AssertThat(Engine_ShaderVariant.ProcessDefines(true, extra)).IsEqual(new[] { Define, "FABLE_LITE_MEDIUMP" });
        AssertThat(Engine_ShaderVariant.ProcessDefines(false, extra)).IsEqual(extra);
    }

    [TestCase]
    public void ComposingAddsTheDefineOnceAndOnlyWhenAsked()
    {
        string code = Source("FABLE_LITE");
        string on = Engine_ShaderVariant.WithExtraDefines(code, Engine_ShaderVariant.ProcessDefines(true, Array.Empty<string>()));
        string off = Engine_ShaderVariant.WithExtraDefines(code, Engine_ShaderVariant.ProcessDefines(false, Array.Empty<string>()));
        AssertThat(Engine_ShaderVariant.Defines(on, Define)).IsTrue();
        AssertThat(Engine_ShaderVariant.Defines(off, Define)).IsFalse();
        // A build that states it already, or a free-form extra naming it too, is not defined twice.
        string stated = Engine_ShaderVariant.WithExtraDefines(Source("FABLE_LITE", Define),
            Engine_ShaderVariant.ProcessDefines(true, new[] { Define }));
        AssertThat(stated.Split(Define).Length - 1).IsEqual(1);
    }
}
