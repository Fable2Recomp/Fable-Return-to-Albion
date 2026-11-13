using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The lite ground's settings (Engine_ShaderSpecialisation.TerrainLeverDefines): `lite_terrain_bit_loop`
/// (on, the same picture), `lite_terrain_max_layers`, `lite_terrain_min_weight`,
/// `lite_terrain_cliff_sharpness`, `lite_terrain_trilinear` and `lite_terrain_vertex_fog` (off), each a
/// Tuning row with a player's name, read into the terrain's defines with its value.
/// </summary>
[TestSuite]
public sealed class LiteTerrainLeversTests
{
    private static readonly string[] Keys =
    {
        Engine_ShaderSpecialisation.TerrainBitLoopKey,
        Engine_ShaderSpecialisation.TerrainMaxLayersKey,
        Engine_ShaderSpecialisation.TerrainMinWeightKey,
        Engine_ShaderSpecialisation.TerrainCliffSharpnessKey,
        Engine_ShaderSpecialisation.TerrainTrilinearKey,
        Engine_ShaderSpecialisation.TerrainVertexFogKey,
    };

    private static Engine_ShaderSpecialisation.TerrainLevers Read(params (string Key, string Value)[] pairs) =>
        Engine_ShaderSpecialisation.ReadTerrainLevers(pairs.ToDictionary(p => p.Key, p => p.Value));

    [TestCase]
    public void EachKeyIsATuningRowWithAPlayersName()
    {
        foreach (string key in Keys)
        {
            RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == key);
            AssertThat(row).IsNotNull();
            AssertThat(AndroidSettingNames.Keys.Contains(key)).IsTrue();
        }

        // The picture-exact one defaults on, the rest off.
        AssertThat(RuntimeCVarsAndroid.Tuning.Single(d => d.Name == Engine_ShaderSpecialisation.TerrainBitLoopKey).DefaultValue).IsEqual("true");
        AssertThat(RuntimeCVarsAndroid.Tuning.Single(d => d.Name == Engine_ShaderSpecialisation.TerrainMaxLayersKey).DefaultValue).IsEqual("0");
        AssertThat(RuntimeCVarsAndroid.Tuning.Single(d => d.Name == Engine_ShaderSpecialisation.TerrainMinWeightKey).DefaultValue).IsEqual("0");
        AssertThat(RuntimeCVarsAndroid.Tuning.Single(d => d.Name == Engine_ShaderSpecialisation.TerrainCliffSharpnessKey).DefaultValue).IsEqual("0");
        AssertThat(RuntimeCVarsAndroid.Tuning.Single(d => d.Name == Engine_ShaderSpecialisation.TerrainTrilinearKey).DefaultValue).IsEqual("false");
        AssertThat(RuntimeCVarsAndroid.Tuning.Single(d => d.Name == Engine_ShaderSpecialisation.TerrainVertexFogKey).DefaultValue).IsEqual("false");
    }

    [TestCase]
    public void UnsetIsTheBitLoopAlone()
    {
        Engine_ShaderSpecialisation.TerrainLevers levers = Read();
        AssertThat(levers).IsEqual(new Engine_ShaderSpecialisation.TerrainLevers(true, 0, 0.0, 0.0, false, false));
        AssertThat(Engine_ShaderSpecialisation.TerrainLeverDefines(true, levers))
            .IsEqual(new[] { "FABLE_SPEC_TERRAIN_ONE_SAMPLE", "FABLE_LITE_TERRAIN_BIT_LOOP" });
        AssertThat(Engine_ShaderSpecialisation.TerrainLeverDefines(false, Read((Engine_ShaderSpecialisation.TerrainBitLoopKey, "false"))))
            .IsEmpty();
    }

    [TestCase]
    public void ValuesAreClampedToWhatTheyCanMean()
    {
        AssertThat(Read((Engine_ShaderSpecialisation.TerrainMaxLayersKey, "2")).MaxLayers).IsEqual(2);
        AssertThat(Read((Engine_ShaderSpecialisation.TerrainMaxLayersKey, "4")).MaxLayers).IsEqual(4);
        // More than four, or less than one, samples every layer.
        AssertThat(Read((Engine_ShaderSpecialisation.TerrainMaxLayersKey, "9")).MaxLayers).IsEqual(0);
        AssertThat(Read((Engine_ShaderSpecialisation.TerrainMaxLayersKey, "-1")).MaxLayers).IsEqual(0);
        AssertThat(Read((Engine_ShaderSpecialisation.TerrainMinWeightKey, "0.9")).MinWeight).IsEqual(0.5);
        AssertThat(Read((Engine_ShaderSpecialisation.TerrainMinWeightKey, "-0.2")).MinWeight).IsEqual(0.0);
        AssertThat(Read((Engine_ShaderSpecialisation.TerrainCliffSharpnessKey, "0.7")).CliffSharpness).IsEqual(0.45);
        AssertThat(Read((Engine_ShaderSpecialisation.TerrainCliffSharpnessKey, "nonsense")).CliffSharpness).IsEqual(0.0);
    }

    [TestCase]
    public void EveryLeverComposesItsDefineAndValueInOrder()
    {
        Engine_ShaderSpecialisation.TerrainLevers levers = Read(
            (Engine_ShaderSpecialisation.TerrainMaxLayersKey, "2"),
            (Engine_ShaderSpecialisation.TerrainMinWeightKey, "0.05"),
            (Engine_ShaderSpecialisation.TerrainCliffSharpnessKey, "0.2"),
            (Engine_ShaderSpecialisation.TerrainTrilinearKey, "true"),
            (Engine_ShaderSpecialisation.TerrainVertexFogKey, "true"));
        AssertThat(Engine_ShaderSpecialisation.TerrainLeverDefines(true, levers)).IsEqual(new[]
        {
            "FABLE_SPEC_TERRAIN_ONE_SAMPLE",
            "FABLE_LITE_TERRAIN_BIT_LOOP",
            "FABLE_LITE_TERRAIN_MAX_LAYERS",
            "FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE 2",
            "FABLE_LITE_TERRAIN_MIN_WEIGHT",
            "FABLE_LITE_TERRAIN_MIN_WEIGHT_VALUE 0.05",
            "FABLE_LITE_TERRAIN_CLIFF_SHARP",
            "FABLE_LITE_TERRAIN_CLIFF_SHARP_VALUE 0.2",
            "FABLE_LITE_TERRAIN_TRILINEAR",
            "FABLE_LITE_TERRAIN_VERTEX_FOG",
        });
        // A whole number is still a float literal.
        string[] whole = Engine_ShaderSpecialisation.TerrainLeverDefines(false, levers with { MinWeight = 0.5 });
        AssertThat(whole.Contains("FABLE_LITE_TERRAIN_MIN_WEIGHT_VALUE 0.5")).IsTrue();
    }

    /// <summary>The specialisation states each as `#define NAME VALUE` ahead of the include, once.</summary>
    [TestCase]
    public void TheBuildStatesTheValuesAheadOfTheInclude()
    {
        const string wrapper = "shader_type spatial;\nrender_mode cull_disabled;\n#define FABLE_LITE\n#include \"res://src/Engine/level/terrain/terrain_splat_body.gdshaderinc\"\n";
        string[] defines = Engine_ShaderSpecialisation.TerrainLeverDefines(true, Read((Engine_ShaderSpecialisation.TerrainMaxLayersKey, "2")));
        string code = Engine_ShaderSpecialisation.Compose(wrapper, defines, false);
        int include = code.IndexOf("#include", StringComparison.Ordinal);
        int value = code.IndexOf("#define FABLE_LITE_TERRAIN_MAX_LAYERS_VALUE 2\n", StringComparison.Ordinal);
        AssertThat(value).IsGreater(-1);
        AssertThat(value).IsLess(include);
        AssertThat(code.Split("#define FABLE_LITE_TERRAIN_MAX_LAYERS\n").Length - 1).IsEqual(1);
        // A build stating a lever already (lite_extra_defines) is not given it twice.
        string again = Engine_ShaderSpecialisation.Compose(code, defines, false);
        AssertThat(again.Split("#define FABLE_LITE_TERRAIN_BIT_LOOP").Length - 1).IsEqual(1);
    }
}
