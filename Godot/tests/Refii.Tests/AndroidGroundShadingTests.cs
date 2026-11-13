using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `lite_ground_ao` (<see cref="AndroidGroundShading"/>): the setting, the defines
/// each level composes, and what those defines leave in the lite builds' stages.
/// </summary>
[TestSuite]
public sealed class AndroidGroundShadingTests
{
    private const string PropModern = "res://src/Engine/material/lite/modern/standard_material_lite.gdshader";
    private const string TerrainModern = "res://src/Engine/level/terrain/lite/modern/terrain_splat_lite.gdshader";
    private const string ContactMap = "fable_terrain_contact_ao";

    private static string Lite(string path, params string[] extra) => LiteAoDefinesTests.Lite(path, extra);

    private static string Stage(string code, string stage) => LiteAoDefinesTests.Stage(code, stage);

    /// <summary>The code without its #define lines: what a define that changes nothing leaves the same.</summary>
    private static string Code(string text) => string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith("#define ", StringComparison.Ordinal)));

    [TestCase]
    public void TheKeyIsRegisteredWithAPlayersName()
    {
        RuntimeCVarDefinition? level = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == AndroidGroundShading.Key);
        AssertThat(level).IsNotNull();
        AssertThat(level!.DefaultValue).IsEqual(AndroidGroundShading.Near);
        foreach (string value in new[] { "full", "near", "fast", "fastest", "off" })
        {
            AssertThat((level.AllowedValues ?? string.Empty).Split(',').Any(o => o.StartsWith(value + "|", StringComparison.Ordinal))).OverrideFailureMessage(value).IsTrue();
        }

        // `one_tap` is gone: not offered, and a stored one reads as the default.
        AssertThat((level.AllowedValues ?? string.Empty).Contains("one_tap", StringComparison.Ordinal)).IsFalse();

        AssertThat(AndroidSettingNames.Keys.Contains(AndroidGroundShading.Key)).IsTrue();

        RuntimeCVarDefinition? gather = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == AndroidGroundShading.GatherKey);
        AssertThat(gather).IsNotNull();
        AssertThat(gather!.Type).IsEqual(RuntimeCVarType.Bool);
        AssertThat(gather.DefaultValue).IsEqual("false");
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidGroundShading.GatherKey)).IsTrue();
        AssertThat(AndroidGroundShading.GatherWanted(new Dictionary<string, string>())).IsFalse();
        AssertThat(AndroidGroundShading.GatherWanted(new Dictionary<string, string> { [AndroidGroundShading.GatherKey] = "true" })).IsTrue();
    }

    [TestCase]
    public void UnsetAndUnknownAreNear()
    {
        AssertThat(AndroidGroundShading.Level(new Dictionary<string, string>())).IsEqual(AndroidGroundShading.Near);
        AssertThat(AndroidGroundShading.Level(new Dictionary<string, string> { [AndroidGroundShading.Key] = "\"Fastest\"" })).IsEqual(AndroidGroundShading.Fastest);
        AssertThat(AndroidGroundShading.Parse("bogus")).IsEqual(AndroidGroundShading.Near);
        AssertThat(AndroidGroundShading.Parse("One_Tap")).IsEqual(AndroidGroundShading.Default);
        AssertThat(AndroidGroundShading.Level(new Dictionary<string, string> { [AndroidGroundShading.Key] = "\"one_tap\"" })).IsEqual(AndroidGroundShading.Default);
    }

    [TestCase]
    public void EachLevelComposesItsDefines()
    {
        string[] Of(string level) => AndroidGroundShading.Defines(level, true);
        AssertThat(Of("full")).IsEqual(Array.Empty<string>());
        AssertThat(Of("near")).IsEqual(new[] { "FABLE_LITE_NEAR_GROUND_AO" });
        AssertThat(Of("fast")).IsEqual(new[] { "FABLE_LITE_VERTEX_GROUND_AO" });
        AssertThat(Of("fastest")).IsEqual(new[] { "FABLE_LITE_VERTEX_GROUND_AO", "FABLE_LITE_VERTEX_TERRAIN_AO" });
        AssertThat(Of("off")).IsEqual(new[] { "FABLE_LITE_NO_PROP_GROUND_AO", "FABLE_LITE_NO_TERRAIN_GROUND_AO" });
        AssertThat(Of("one_tap")).IsEqual(Of(AndroidGroundShading.Default));

        // The gathered taps join a level whose props read taps, and no other.
        string[] Gathered(string level) => AndroidGroundShading.Defines(level, true, true);
        AssertThat(Gathered("near")).IsEqual(new[] { "FABLE_LITE_NEAR_GROUND_AO", "FABLE_LITE_GROUND_AO_GATHER" });
        AssertThat(Gathered("fast")).IsEqual(new[] { "FABLE_LITE_VERTEX_GROUND_AO", "FABLE_LITE_GROUND_AO_GATHER" });
        AssertThat(Gathered("fastest")).IsEqual(new[] { "FABLE_LITE_VERTEX_GROUND_AO", "FABLE_LITE_VERTEX_TERRAIN_AO", "FABLE_LITE_GROUND_AO_GATHER" });
        AssertThat(Gathered("full")).IsEqual(Array.Empty<string>());
        AssertThat(Gathered("off")).IsEqual(Of("off"));
    }

    /// <summary>Nothing where the AO is not compiled (the fable model, baked AO off), and nothing off Android.</summary>
    [TestCase]
    public void NothingWithoutTheAo()
    {
        foreach (string level in new[] { "full", "near", "fast", "fastest", "off" })
        {
            AssertThat(AndroidGroundShading.Defines(level, false)).IsEqual(Array.Empty<string>());
            AssertThat(AndroidGroundShading.Defines(level, false, true)).IsEqual(Array.Empty<string>());
        }

        AssertThat(AndroidGroundShading.ProcessDefines().Any()).IsFalse();
    }

    /// <summary>
    /// Near: the vertex measures its height above the walkable floor from the map's four taps, the
    /// fragment takes its taps only within the reach, and where it does not it returns exactly what a
    /// match weight of 0 gives.
    /// </summary>
    [TestCase]
    public void NearFetchesOnlyBelowTheReach()
    {
        string prop = Lite(PropModern, AndroidGroundShading.NearDefine);
        string vertex = Stage(prop, "vertex");
        AssertThat(vertex.Contains("v_prop_ground_height = fable_vertex_prop_ground_ao(COLOR.a, world_at).y", StringComparison.Ordinal)).IsTrue();
        AssertThat(vertex.Contains("texelFetch(" + ContactMap, StringComparison.Ordinal)).IsTrue();

        string near = LiteAoDefinesTests.Function(prop, "float fable_near_prop_baked_ao(");
        int gate = near.IndexOf("abs(vertex_height) < FABLE_NEAR_GROUND_REACH", StringComparison.Ordinal);
        AssertThat(gate).IsGreater(-1);
        AssertThat(near.IndexOf("fable_evaluate_prop_baked_ao(", StringComparison.Ordinal)).IsGreater(gate);
        AssertThat(near.Contains("(color_alpha > 0.001 ? color_alpha : 1.0) * (instance_baked_ao > 0.001 ? instance_baked_ao : 1.0)", StringComparison.Ordinal)).IsTrue();
        AssertThat(near.Contains("FABLE_UNIFIED <= 0.5 || FABLE_AO_STRENGTH <= 0.001", StringComparison.Ordinal)).IsTrue();
        AssertThat(prop.Contains("#define FABLE_NEAR_GROUND_REACH 1.0", StringComparison.Ordinal)).IsTrue();
        AssertThat(Stage(prop, "fragment").Contains("fable_near_prop_baked_ao(COLOR.a, fable_world, v_prop_ground_height)", StringComparison.Ordinal)).IsTrue();
        AssertThat(Stage(prop, "light").Contains(ContactMap, StringComparison.Ordinal)).IsFalse();

        // The match weight the skipped pixels stand past: 0 from 0.42 m off a tap's height, under the reach.
        string evaluate = LiteAoDefinesTests.Function(prop, "float fable_evaluate_prop_baked_ao(");
        AssertThat(evaluate.Contains("1.0 - smoothstep(0.06, 0.42, abs(world_pos.y - h00))", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Near yields to the vertex path, to the prop switch and to the fable model; it leaves the terrain alone.</summary>
    [TestCase]
    public void NearYieldsToTheOtherSwitches()
    {
        string both = Lite(PropModern, AndroidGroundShading.NearDefine, AndroidGroundShading.VertexDefine);
        AssertThat(both.Contains("v_prop_ground_height", StringComparison.Ordinal)).IsFalse();
        AssertThat(both.Contains("varying FABLE_VARY_MP vec4 v_prop_ground_ao;", StringComparison.Ordinal)).IsTrue();
        foreach (string off in new[] { AndroidGroundShading.NoPropDefine, Engine_ShaderVariant.NoBakedAo })
        {
            string prop = Lite(PropModern, AndroidGroundShading.NearDefine, off);
            AssertThat(prop.Contains("v_prop_ground_height", StringComparison.Ordinal)).IsFalse();
            AssertThat(Stage(prop, "vertex").Contains(ContactMap, StringComparison.Ordinal)).IsFalse();
        }

        // The terrain's stages are untouched (only the props' helpers, which it never calls, are added).
        foreach (string stage in new[] { "vertex", "fragment", "light" })
        {
            AssertThat(Stage(Lite(TerrainModern, AndroidGroundShading.NearDefine), stage)).IsEqual(Stage(Lite(TerrainModern), stage));
        }
    }

    /// <summary>Fastest's terrain half: the ground reads the map in vertex() and no fragment code does.</summary>
    [TestCase]
    public void VertexTerrainMovesTheGroundsFetchIntoVertex()
    {
        string terrain = Lite(TerrainModern, AndroidGroundShading.VertexTerrainDefine);
        AssertThat(Stage(terrain, "vertex").Contains("v_terrain_contact_ao = fable_sample_terrain_ao(world_at)", StringComparison.Ordinal)).IsTrue();
        AssertThat(Stage(terrain, "vertex").Contains("textureLod(" + ContactMap, StringComparison.Ordinal)).IsTrue();
        AssertThat(Stage(terrain, "fragment").Contains(ContactMap, StringComparison.Ordinal)).IsFalse();
        AssertThat(Stage(terrain, "fragment").Contains("mix(1.0, v_terrain_contact_ao, step(0.5, ", StringComparison.Ordinal)).IsTrue();

        string before = Lite(TerrainModern);
        AssertThat(Stage(before, "fragment").Contains("textureLod(" + ContactMap, StringComparison.Ordinal)).IsTrue();
        AssertThat(Stage(before, "vertex").Contains(ContactMap, StringComparison.Ordinal)).IsFalse();

        foreach (string off in new[] { AndroidGroundShading.NoTerrainDefine, Engine_ShaderVariant.NoBakedAo })
        {
            string none = Lite(TerrainModern, AndroidGroundShading.VertexTerrainDefine, off);
            AssertThat(none.Contains("v_terrain_contact_ao", StringComparison.Ordinal)).IsFalse();
            AssertThat(Stage(none, "vertex").Contains(ContactMap, StringComparison.Ordinal)).IsFalse();
            AssertThat(Stage(none, "fragment").Contains(ContactMap, StringComparison.Ordinal)).IsFalse();
        }

        AssertThat(Code(Lite(PropModern, AndroidGroundShading.VertexTerrainDefine))).IsEqual(Code(Lite(PropModern)));
    }

    /// <summary>
    /// The gathered taps: near's pixel and vertex, and fast's vertex, read the map with textureGather and no
    /// texel fetch; the weights are upstream's; nothing changes where no prop reads taps.
    /// </summary>
    [TestCase]
    public void GatherReadsTheSameTapsGathered()
    {
        string near = Lite(PropModern, AndroidGroundShading.NearDefine, AndroidGroundShading.GatherDefine);
        foreach (string stage in new[] { "vertex", "fragment" })
        {
            string code = Stage(near, stage);
            AssertThat(code.Contains("textureGather(" + ContactMap, StringComparison.Ordinal)).OverrideFailureMessage(stage).IsTrue();
            AssertThat(code.Contains("texelFetch(" + ContactMap, StringComparison.Ordinal)).OverrideFailureMessage(stage).IsFalse();
        }

        string taps = LiteAoDefinesTests.Function(near, "void fable_gather_ground_taps(");
        AssertThat(taps.Contains("vec2 uv = (base_f + 1.0) / fable_terrain_ao_height.zw;", StringComparison.Ordinal)).IsTrue();
        string pixel = LiteAoDefinesTests.Function(near, "float fable_gather_prop_baked_ao(");
        AssertThat(pixel.Contains("vec4(1.0) - smoothstep(vec4(0.06), vec4(0.42), abs(vec4(world_pos.y) - h))", StringComparison.Ordinal)).IsTrue();
        AssertThat(pixel.Contains("float fade = max_hw * smoothstep(0.0, 0.12, sum_w);", StringComparison.Ordinal)).IsTrue();

        string fast = Lite(PropModern, AndroidGroundShading.VertexDefine, AndroidGroundShading.GatherDefine);
        AssertThat(Stage(fast, "vertex").Contains("textureGather(" + ContactMap, StringComparison.Ordinal)).IsTrue();
        AssertThat(Stage(fast, "vertex").Contains("texelFetch(" + ContactMap, StringComparison.Ordinal)).IsFalse();

        // Full (no near or vertex path), the props' switch, the fable model and the terrain: unchanged.
        AssertThat(Code(Lite(PropModern, AndroidGroundShading.GatherDefine))).IsEqual(Code(Lite(PropModern)));
        AssertThat(Code(Lite(PropModern, AndroidGroundShading.NoPropDefine, AndroidGroundShading.GatherDefine))).IsEqual(Code(Lite(PropModern, AndroidGroundShading.NoPropDefine)));
        AssertThat(Code(Lite(PropModern, AndroidGroundShading.NearDefine, Engine_ShaderVariant.NoBakedAo, AndroidGroundShading.GatherDefine))).IsEqual(Code(Lite(PropModern, AndroidGroundShading.NearDefine, Engine_ShaderVariant.NoBakedAo)));
        foreach (string stage in new[] { "vertex", "fragment", "light" })
        {
            AssertThat(Stage(Lite(TerrainModern, AndroidGroundShading.NearDefine, AndroidGroundShading.GatherDefine), stage)).IsEqual(Stage(Lite(TerrainModern), stage));
        }
    }
}
