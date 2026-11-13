using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `lite_trim_varyings`, `lite_pack_varyings`, `lite_fragment_world`, `lite_terrain_view_frame` and
/// `lite_mediump_varyings` (<see cref="AndroidLeanVaryings"/>): the settings, their defines, and what the
/// defines leave crossing from vertex() to fragment() in the lite builds (src/android/fable_lite_varyings.gdshaderinc).
/// </summary>
[TestSuite]
public sealed class AndroidLeanVaryingsTests
{
    private const string Culled = "res://src/Engine/material/lite/standard_material_culled_lite.gdshader";
    private const string CulledModern = "res://src/Engine/material/lite/modern/standard_material_culled_lite.gdshader";
    private const string TwoSided = "res://src/Engine/material/lite/standard_material_lite.gdshader";
    private const string TwoSidedModern = "res://src/Engine/material/lite/modern/standard_material_lite.gdshader";
    private static readonly string Terrain = Engine_ShaderVariant.TerrainLitePath;
    private static readonly string TerrainModern = Modern(Engine_ShaderVariant.TerrainLitePath);
    private static readonly string TerrainFlatNoCliff = Modern(Engine_ShaderVariant.TerrainFlatNoCliffPath);
    private static readonly string TerrainPage = Modern(Engine_ShaderVariant.TerrainPagePath);

    private const string Trim = AndroidLeanVaryings.TrimDefine;
    private const string Pack = AndroidLeanVaryings.PackDefine;
    private const string World = AndroidLeanVaryings.FragmentWorldDefine;
    private const string ViewFrame = AndroidLeanVaryings.TerrainViewFrameDefine;
    private const string Mediump = AndroidLeanVaryings.MediumpDefine;
    private const string Near = AndroidGroundShading.NearDefine;
    private const string PropWorld = AndroidLeanVaryings.PropFragmentWorldDefine;
    private const string PackUv = AndroidLeanVaryings.PackLightmapUvDefine;

    private static string Modern(string litePath) => litePath.Replace("/lite/", "/lite/modern/", StringComparison.Ordinal);

    private static string Lite(string path, params string[] extra) => LiteAoDefinesTests.Lite(path, extra);

    private static string Stage(string code, string stage) => LiteAoDefinesTests.Stage(code, stage);

    /// <summary>The code lines (comments and #define lines aside) that name <paramref name="word"/>.</summary>
    private static string[] Reads(string code, string word) => code.Split('\n')
        .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith("#define", StringComparison.Ordinal))
        .Where(l => Regex.IsMatch(l, @"\b" + Regex.Escape(word) + @"\b"))
        .ToArray();

    /// <summary>Whether a varying named <paramref name="name"/> is declared, whatever its qualifiers.</summary>
    private static bool Declares(string code, string name) =>
        Regex.IsMatch(code, @"(?m)^\s*varying\s+(?:\w+\s+)*" + Regex.Escape(name) + @"\s*;");

    private static bool Defines(string code, string line) =>
        code.Split('\n').Any(l => l.Trim() == line);

    private static string Source(string resPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    private static string? Include(string resPath)
    {
        try
        {
            return Source(resPath);
        }
        catch (IOException)
        {
            return null;
        }
    }

    [TestCase]
    public void TheKeysAreRegisteredWithPlayersNames()
    {
        foreach ((string key, string value) in new[]
        {
            (AndroidLeanVaryings.TrimKey, "true"), (AndroidLeanVaryings.PackKey, "true"), (AndroidLeanVaryings.FragmentWorldKey, "false"),
            (AndroidLeanVaryings.TerrainViewFrameKey, "false"), (AndroidLeanVaryings.MediumpKey, "false"),
            (AndroidLeanVaryings.PropFragmentWorldKey, "false"), (AndroidLeanVaryings.PackLightmapUvKey, "false"),
        })
        {
            RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == key);
            AssertThat(row).OverrideFailureMessage(key).IsNotNull();
            AssertThat(row!.DefaultValue).OverrideFailureMessage(key).IsEqual(value);
            AssertThat(row.Type).OverrideFailureMessage(key).IsEqual(RuntimeCVarType.Bool);
            AssertThat(AndroidSettingNames.Keys.Contains(key)).OverrideFailureMessage(key).IsTrue();
        }

        AssertThat(Trim).IsEqual("FABLE_LITE_TRIM_VARYINGS");
        AssertThat(Pack).IsEqual("FABLE_LITE_PACK_VARYINGS");
        AssertThat(World).IsEqual("FABLE_LITE_FRAGMENT_WORLD");
        AssertThat(ViewFrame).IsEqual("FABLE_LITE_TERRAIN_VIEW_FRAME");
        AssertThat(Mediump).IsEqual("FABLE_LITE_MEDIUMP_VARYINGS");
        AssertThat(PropWorld).IsEqual("FABLE_LITE_PROP_FRAGMENT_WORLD");
        AssertThat(PackUv).IsEqual("FABLE_LITE_PACK_LIGHTMAP_UV");
        AssertThat(AndroidLeanVaryings.PropFragmentWorldKey).IsEqual("lite_prop_fragment_world");
        AssertThat(AndroidLeanVaryings.PackLightmapUvKey).IsEqual("lite_pack_lightmap_uv");
    }

    [TestCase]
    public void TheDefinesFollowTheKeys()
    {
        AssertThat(AndroidLeanVaryings.Wanted(new Dictionary<string, string>())).IsEqual(AndroidLeanVaryings.Defaults);
        AssertThat(AndroidLeanVaryings.DefinesFor(AndroidLeanVaryings.Defaults)).IsEqual(new[] { Trim, Pack });
        var off = new Dictionary<string, string> { [AndroidLeanVaryings.TrimKey] = "false", [AndroidLeanVaryings.PackKey] = "false" };
        AssertThat(AndroidLeanVaryings.DefinesFor(AndroidLeanVaryings.Wanted(off))).IsEmpty();
        var all = new Dictionary<string, string>
        {
            [AndroidLeanVaryings.FragmentWorldKey] = "true", [AndroidLeanVaryings.TerrainViewFrameKey] = "true", [AndroidLeanVaryings.MediumpKey] = "true",
            [AndroidLeanVaryings.PropFragmentWorldKey] = "true", [AndroidLeanVaryings.PackLightmapUvKey] = "true",
        };
        // The props-only world is in the whole one already.
        AssertThat(AndroidLeanVaryings.DefinesFor(AndroidLeanVaryings.Wanted(all))).IsEqual(new[] { Trim, Pack, World, ViewFrame, Mediump, PackUv });
        var props = new Dictionary<string, string> { [AndroidLeanVaryings.PropFragmentWorldKey] = "true", [AndroidLeanVaryings.PackLightmapUvKey] = "true" };
        AssertThat(AndroidLeanVaryings.DefinesFor(AndroidLeanVaryings.Wanted(props))).IsEqual(new[] { Trim, Pack, PropWorld, PackUv });
        // The UV2 pack rides the packed colour, so not without it.
        props[AndroidLeanVaryings.PackKey] = "false";
        AssertThat(AndroidLeanVaryings.DefinesFor(AndroidLeanVaryings.Wanted(props))).IsEqual(new[] { Trim, PropWorld });
        // Off a phone the process composes none of them.
        AssertThat(AndroidLeanVaryings.Defines()).IsEmpty();
    }

    /// <summary>The process list carries them, and only a lite build sees the include; a full shader keeps every varying, as upstream writes it.</summary>
    [TestCase]
    public void TheHookAndTheFullShaders()
    {
        AssertThat(Source("res://src/android/Engine_ShaderVariant.cs"))
            .Contains(".Concat(Refii.AndroidRuntime.AndroidLeanVaryings.Defines())");
        foreach (string full in new[] { Engine_ShaderFamily.StandardCulledPath, Engine_ShaderVariant.TerrainFullPath })
        {
            string code = Engine_ShaderCeilingSource.Preprocess(Source(full), full, Include);
            AssertThat(code.Contains("fable_terrain_local_from_world", StringComparison.Ordinal)).OverrideFailureMessage(full).IsFalse();
            AssertThat(Stage(code, "vertex").Contains("fable_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;", StringComparison.Ordinal)).OverrideFailureMessage(full).IsTrue();
            AssertThat(code.Contains("src/android/", StringComparison.Ordinal)).OverrideFailureMessage(full).IsFalse();
        }

        string culled = Engine_ShaderCeilingSource.Preprocess(Source(Engine_ShaderFamily.StandardCulledPath), Engine_ShaderFamily.StandardCulledPath, Include);
        AssertThat(Declares(culled, "v_tree_fade")).IsTrue();
        AssertThat(culled.Contains("fable_bake(world_normal, COLOR, FABLE_LIGHTMAP_UV)", StringComparison.Ordinal)).IsTrue();
        AssertThat(Defines(culled, "#define FABLE_LIGHTMAP_UV UV2")).IsTrue();
        string terrain = Engine_ShaderCeilingSource.Preprocess(Source(Engine_ShaderVariant.TerrainFullPath), Engine_ShaderVariant.TerrainFullPath, Include);
        foreach (string varying in new[] { "v_local", "v_local_normal", "v_page_rect" })
        {
            AssertThat(Declares(terrain, varying)).OverrideFailureMessage(varying).IsTrue();
        }
    }

    /// <summary>Without the defines every lite build carries what it carried.</summary>
    [TestCase]
    public void WithoutTheDefinesNothingIsDropped()
    {
        foreach (string path in new[] { Culled, CulledModern, TwoSidedModern, Terrain, TerrainModern, TerrainFlatNoCliff })
        {
            string code = Lite(path, Near);
            AssertThat(Stage(code, "vertex").Contains("fable_world = world_at;", StringComparison.Ordinal)).OverrideFailureMessage(path).IsTrue();
            AssertThat(Defines(code, "#define FABLE_VARY_MP")).OverrideFailureMessage(path).IsTrue();
        }

        AssertThat(Declares(Lite(CulledModern, Near), "v_tree_fade")).IsTrue();
        AssertThat(Declares(Lite(CulledModern, Near), "v_prop_ground_height")).IsTrue();
        AssertThat(Declares(Lite(TerrainModern), "v_page_rect")).IsTrue();
        AssertThat(Declares(Lite(TerrainFlatNoCliff), "v_local_normal")).IsTrue();
    }

    /// <summary>Trim: a solid prop's fade, the ground's page rectangle outside the pages, a page's ground frame,
    /// a flat no-cliff chunk's normal go; the tree fade turns flat. What is read stays.</summary>
    [TestCase]
    public void TrimDropsWhatNoPixelReads()
    {
        foreach (string path in new[] { Culled, CulledModern })
        {
            string culled = Lite(path, Near, Trim);
            AssertThat(Declares(culled, "v_tree_fade")).OverrideFailureMessage(path).IsFalse();
            AssertThat(Reads(culled, "v_tree_fade")).OverrideFailureMessage(path).IsEmpty();
        }

        string twoSided = Lite(TwoSidedModern, Near, Trim);
        AssertThat(Declares(twoSided, "v_tree_fade")).IsTrue();
        AssertThat(Defines(twoSided, "#define FABLE_TREE_FADE_QUAL flat FABLE_VARY_MP")).IsTrue();
        AssertThat(Stage(twoSided, "fragment").Contains("if (v_tree_fade < 0.999)", StringComparison.Ordinal)).IsTrue();

        foreach (string path in new[] { Terrain, TerrainModern, TerrainFlatNoCliff })
        {
            string ground = Lite(path, Trim);
            AssertThat(Reads(ground, "v_page_rect")).OverrideFailureMessage(path).IsEmpty();
            AssertThat(Reads(ground, "CUSTOM0")).OverrideFailureMessage(path).IsEmpty();
            AssertThat(Declares(ground, "v_local")).OverrideFailureMessage(path).IsTrue();
        }

        AssertThat(Declares(Lite(TerrainModern, Trim), "v_local_normal")).IsTrue();
        string flat = Lite(TerrainFlatNoCliff, Trim);
        AssertThat(Declares(flat, "v_local_normal")).IsFalse();
        AssertThat(Defines(flat, "#define FABLE_TERRAIN_NORMAL_IN vec3 v_local_normal = vec3(0.0, 1.0, 0.0);")).IsTrue();

        string page = Lite(TerrainPage, Trim);
        AssertThat(Declares(page, "v_page_rect")).IsTrue();
        AssertThat(Declares(page, "v_local")).IsFalse();
        AssertThat(Declares(page, "v_local_normal")).IsFalse();
        AssertThat(Defines(page, "#define FABLE_TERRAIN_LOCAL_IN vec3 v_local = vec3(0.0);")).IsTrue();
        AssertThat(Stage(page, "fragment").Contains("FABLE_TERRAIN_FRAGMENT_IN", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Trim with a bake-mode build: UV2 goes unless amb3, and the fable model's constant-zero transfer goes.</summary>
    [TestCase]
    public void TrimFollowsTheBakeMode()
    {
        foreach (string path in new[] { Culled, CulledModern })
        {
            string none = Lite(path, Trim, "FABLE_SPEC_BAKE_NONE", "FABLE_SPEC_NO_PRT");
            AssertThat(Reads(none, "UV2")).OverrideFailureMessage(path).IsEmpty();
            AssertThat(Defines(none, "#define FABLE_LIGHTMAP_UV vec2(0.0)")).OverrideFailureMessage(path).IsTrue();
            AssertThat(Declares(none, "v_transfer")).OverrideFailureMessage(path).IsFalse();
            string amb3 = Lite(path, Trim, "FABLE_SPEC_BAKE_AMB3", "FABLE_SPEC_NO_PRT");
            AssertThat(Defines(amb3, "#define FABLE_LIGHTMAP_UV UV2")).OverrideFailureMessage(path).IsTrue();
            AssertThat(Reads(Lite(path, "FABLE_SPEC_BAKE_NONE", "FABLE_SPEC_NO_PRT"), "UV2")).OverrideFailureMessage(path).IsEmpty();
            AssertThat(Defines(Lite(path, "FABLE_SPEC_BAKE_NONE", "FABLE_SPEC_NO_PRT"), "#define FABLE_LIGHTMAP_UV UV2")).OverrideFailureMessage(path).IsTrue();
        }

        // The fable model's probe build with the mesh's transfer keeps it.
        AssertThat(Declares(Lite(Culled, Trim), "v_transfer")).IsTrue();
        AssertThat(Declares(Lite(Culled, Trim, "FABLE_SPEC_NO_PRT"), "v_transfer")).IsFalse();
    }

    /// <summary>Pack: under the modern model the height and the fade are COLOR's r and g, the vertex ground AO its rba;
    /// the fable model, whose COLOR.rgb is the vertex bake, keeps its own varyings.</summary>
    [TestCase]
    public void PackRidesTheColourUnderTheModernModelOnly()
    {
        string modern = Lite(TwoSidedModern, Near, Trim, Pack);
        AssertThat(Defines(modern, "#define v_prop_ground_height COLOR.r")).IsTrue();
        AssertThat(Defines(modern, "#define v_tree_fade COLOR.g")).IsTrue();
        AssertThat(Declares(modern, "v_prop_ground_height")).IsFalse();
        AssertThat(Declares(modern, "v_tree_fade")).IsFalse();
        AssertThat(Stage(modern, "vertex").Contains("v_prop_ground_height = fable_vertex_prop_ground_ao(COLOR.a, world_at).y", StringComparison.Ordinal)).IsTrue();
        AssertThat(Stage(modern, "fragment").Contains("fable_near_prop_baked_ao(COLOR.a, fable_world, v_prop_ground_height)", StringComparison.Ordinal)).IsTrue();
        // Nothing else of the modern model's pixel stage reads COLOR's rgb.
        AssertThat(Regex.IsMatch(Stage(modern, "fragment"), @"\bCOLOR\b(?!\.a\b)")).IsFalse();

        // A solid prop under trim has no fade to pack.
        AssertThat(Defines(Lite(CulledModern, Near, Trim, Pack), "#define v_tree_fade COLOR.g")).IsFalse();

        string vertexAo = Lite(CulledModern, AndroidGroundShading.VertexDefine, Trim, Pack);
        AssertThat(Declares(vertexAo, "v_prop_ground_ao")).IsFalse();
        AssertThat(Stage(vertexAo, "vertex").Contains("COLOR.rba = fable_vertex_prop_ground_ao(COLOR.a, world_at).xyz;", StringComparison.Ordinal)).IsTrue();
        AssertThat(Defines(vertexAo, "#define v_prop_ground_ao vec4(COLOR.r, COLOR.b, COLOR.a, instance_baked_ao > 0.001 ? instance_baked_ao : 1.0)")).IsTrue();

        string fable = Lite(TwoSided, Near, Trim, Pack);
        AssertThat(Declares(fable, "v_tree_fade")).IsTrue();
        AssertThat(Defines(fable, "#define v_tree_fade COLOR.g")).IsFalse();
    }

    /// <summary>Fragment world: vertex() no longer writes fable_world; fragment() rebuilds it first, and light() reads it.</summary>
    [TestCase]
    public void FragmentWorldRebuildsThePosition()
    {
        foreach (string path in new[] { Culled, CulledModern, TwoSidedModern, Terrain, TerrainModern, TerrainPage })
        {
            string code = Lite(path, Near, Trim, World);
            AssertThat(Stage(code, "vertex").Contains("fable_world = world_at;", StringComparison.Ordinal)).OverrideFailureMessage(path).IsFalse();
            AssertThat(Stage(code, "light").Contains("fable_world", StringComparison.Ordinal)).OverrideFailureMessage(path).IsTrue();
        }

        // The props rebuild it where the ground AO first reads it, not first thing.
        string rebuilt = "#define FABLE_PROP_FRAGMENT_WORLD fable_world = (mat3(INV_VIEW_MATRIX) * VERTEX) + INV_VIEW_MATRIX[3].xyz;";
        AssertThat(Defines(Lite(CulledModern, World), rebuilt)).IsTrue();
        AssertThat(Defines(Lite(CulledModern), "#define FABLE_PROP_FRAGMENT_WORLD")).IsTrue();
        string fragment = Stage(Lite(CulledModern, Near, World), "fragment");
        int hook = fragment.IndexOf("FABLE_PROP_FRAGMENT_WORLD", StringComparison.Ordinal);
        AssertThat(hook).IsGreater(fragment.IndexOf("texture(normal_tex, uv)", StringComparison.Ordinal));
        AssertThat(hook).IsLess(fragment.IndexOf("fable_near_prop_baked_ao(", StringComparison.Ordinal));
        AssertThat(Defines(Lite(TerrainModern, World), "#define FABLE_TERRAIN_WORLD_IN fable_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;")).IsTrue();
        AssertThat(Defines(Lite(TerrainModern), "#define FABLE_TERRAIN_WORLD_IN")).IsTrue();
        // The props take no first-statement hook: their only rebuild is the one above.
        AssertThat(Lite(CulledModern, World).Contains("FABLE_PROP_FRAGMENT_IN", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>The props-only world: the props rebuild it as the whole switch does, the ground keeps its varying.</summary>
    [TestCase]
    public void PropFragmentWorldLeavesTheGround()
    {
        foreach (string path in new[] { Culled, CulledModern, TwoSidedModern })
        {
            string code = Lite(path, Near, Trim, PropWorld);
            AssertThat(Stage(code, "vertex").Contains("fable_world = world_at;", StringComparison.Ordinal)).OverrideFailureMessage(path).IsFalse();
            AssertThat(Defines(code, "#define FABLE_PROP_FRAGMENT_WORLD fable_world = (mat3(INV_VIEW_MATRIX) * VERTEX) + INV_VIEW_MATRIX[3].xyz;")).OverrideFailureMessage(path).IsTrue();
        }

        foreach (string path in new[] { Terrain, TerrainModern, TerrainPage })
        {
            string code = Lite(path, Trim, PropWorld);
            AssertThat(Stage(code, "vertex").Contains("fable_world = world_at;", StringComparison.Ordinal)).OverrideFailureMessage(path).IsTrue();
            AssertThat(Defines(code, "#define FABLE_TERRAIN_WORLD_IN")).OverrideFailureMessage(path).IsTrue();
        }
    }

    /// <summary>The UV2 pack: a solid modern prop with COLOR packed reads its lightmap UV from COLOR.gb, written last
    /// in vertex(); a two-sided one (its fade in g), the vertex ground AO (b), the fable model and a full build keep UV2.</summary>
    [TestCase]
    public void PackLightmapUvRidesTheFreeColourChannels()
    {
        string culled = Lite(CulledModern, Near, Trim, Pack, PackUv);
        AssertThat(Defines(culled, "#define FABLE_LIGHTMAP_UV COLOR.gb")).IsTrue();
        AssertThat(Defines(culled, "#define FABLE_PROP_VERTEX_OUT COLOR.gb = UV2;")).IsTrue();
        string vertex = Stage(culled, "vertex");
        // After the height's write, so nothing overwrites COLOR.gb.
        AssertThat(vertex.IndexOf("FABLE_PROP_VERTEX_OUT", StringComparison.Ordinal))
            .IsGreater(vertex.IndexOf("v_prop_ground_height = fable_vertex_prop_ground_ao", StringComparison.Ordinal));
        AssertThat(Stage(culled, "fragment").Contains("fable_bake_amb3(FABLE_LIGHTMAP_UV)", StringComparison.Ordinal)).IsTrue();
        // Nothing else of the pixel stage reads COLOR's g or b.
        AssertThat(Regex.IsMatch(Stage(culled, "fragment"), @"\bCOLOR\.(?:[rgba]*[gb][rgba]*)\b")).IsFalse();

        foreach (string[] defines in new[]
        {
            new[] { Near, Trim, Pack },
            new[] { Near, Pack, PackUv },
            new[] { Near, Trim, PackUv },
            new[] { AndroidGroundShading.VertexDefine, Trim, Pack, PackUv },
        })
        {
            AssertThat(Defines(Lite(CulledModern, defines), "#define FABLE_LIGHTMAP_UV UV2")).OverrideFailureMessage(string.Join(",", defines)).IsTrue();
            AssertThat(Defines(Lite(CulledModern, defines), "#define FABLE_PROP_VERTEX_OUT")).OverrideFailureMessage(string.Join(",", defines)).IsTrue();
        }

        AssertThat(Defines(Lite(TwoSidedModern, Near, Trim, Pack, PackUv), "#define FABLE_LIGHTMAP_UV UV2")).IsTrue();
        AssertThat(Defines(Lite(Culled, Near, Trim, Pack, PackUv), "#define FABLE_LIGHTMAP_UV UV2")).IsTrue();
        // A bake-mode build that is not amb3 still passes nothing.
        AssertThat(Defines(Lite(CulledModern, Near, Trim, Pack, PackUv, "FABLE_SPEC_BAKE_NONE", "FABLE_SPEC_NO_PRT"), "#define FABLE_LIGHTMAP_UV vec2(0.0)")).IsTrue();
        // The ground has no lightmap UV and takes no vertex hook.
        AssertThat(Stage(Lite(TerrainModern, Trim, Pack, PackUv), "vertex").Contains("UV2", StringComparison.Ordinal)).IsFalse();
        // A full build: neither hook (they live in the phone's includes), and upstream's UV2.
        string full = Engine_ShaderCeilingSource.Preprocess(Source(Engine_ShaderFamily.StandardCulledPath), Engine_ShaderFamily.StandardCulledPath, Include);
        AssertThat(full.Contains("FABLE_PROP_VERTEX_OUT", StringComparison.Ordinal)).IsFalse();
        AssertThat(full.Contains("FABLE_PROP_FRAGMENT_WORLD", StringComparison.Ordinal)).IsFalse();
        AssertThat(Defines(full, "#define FABLE_LIGHTMAP_UV UV2")).IsTrue();
    }

    /// <summary>The view frame: the ground carries neither its position nor its normal, and rebuilds both.</summary>
    [TestCase]
    public void TheViewFrameRebuildsTheGroundsFrame()
    {
        foreach (string path in new[] { Terrain, TerrainModern, TerrainFlatNoCliff })
        {
            string code = Lite(path, Trim, World, ViewFrame);
            AssertThat(Declares(code, "v_local")).OverrideFailureMessage(path).IsFalse();
            AssertThat(Declares(code, "v_local_normal")).OverrideFailureMessage(path).IsFalse();
            AssertThat(Defines(code, "#define FABLE_TERRAIN_LOCAL_IN vec3 v_local = fable_terrain_local_from_world(fable_world, MODEL_MATRIX);")).OverrideFailureMessage(path).IsTrue();
            AssertThat(Defines(code, "#define FABLE_TERRAIN_NORMAL_IN vec3 v_local_normal = fable_terrain_local_normal(NORMAL, FRONT_FACING, INV_VIEW_MATRIX, MODEL_MATRIX);")).OverrideFailureMessage(path).IsTrue();
        }

        // The props never see it.
        AssertThat(Lite(CulledModern, ViewFrame).Contains("FABLE_TERRAIN_FRAGMENT_IN", StringComparison.Ordinal)).IsTrue();
        AssertThat(Stage(Lite(CulledModern, ViewFrame), "fragment").Contains("FABLE_TERRAIN_FRAGMENT_IN", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>Mediump: the small-range varyings' qualifier, and the shader-ceiling tool still sees them as varyings.</summary>
    [TestCase]
    public void MediumpQualifiesTheSmallVaryings()
    {
        string code = Lite(TerrainModern, Mediump, "FABLE_LITE_TERRAIN_VERTEX_FOG");
        AssertThat(Defines(code, "#define FABLE_VARY_MP mediump")).IsTrue();
        AssertThat(code.Contains("varying FABLE_VARY_MP vec3 v_fog_t;", StringComparison.Ordinal)).IsTrue();
        AssertThat(code.Contains("varying FABLE_VARY_MP vec3 v_local_normal;", StringComparison.Ordinal)).IsTrue();
        // The positions stay full precision.
        AssertThat(code.Contains("varying vec3 v_local;", StringComparison.Ordinal)).IsTrue();
        AssertThat(Source("res://src/Engine/material/fable_lighting.gdshaderinc").Contains("varying vec3 fable_world;", StringComparison.Ordinal)).IsTrue();

        Engine_ShaderCeilingSource.Built flatvs = Engine_ShaderCeilingSource.Build(
            Engine_ShaderVariant.WithExtraDefines(Engine_ShaderVariant.Compose(
                Source(Engine_ShaderVariant.FullPathOf(TwoSided)), TwoSided, false, false, false), new[] { "FABLE_LITE_VERTEX_FOG", Mediump }),
            TwoSided, Include, cut: true, (0.5f, 0.4f, 0.3f), keepVaryings: true)!.Value;
        AssertThat(flatvs.Code.Contains("float(v_fog_t.x)", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>The include is the phone's, and the shared files keep one hook each.</summary>
    [TestCase]
    public void TheIncludeIsThePhonesAndHookedOnce()
    {
        // fable_lighting hooks the phone's lighting functions once; they include the varyings' switches once.
        string lighting = Source("res://src/Engine/material/fable_lighting.gdshaderinc");
        AssertThat(Regex.Matches(lighting, Regex.Escape("#include \"res://src/android/fable_lite_lighting.gdshaderinc\"")).Count).IsEqual(1);
        AssertThat(lighting.Contains("fable_lite_varyings", StringComparison.Ordinal)).IsFalse();
        string phone = Source("res://src/android/fable_lite_lighting.gdshaderinc");
        AssertThat(Regex.Matches(phone, Regex.Escape("#include \"res://src/android/fable_lite_varyings.gdshaderinc\"")).Count).IsEqual(1);
        AssertThat(Source("res://src/android/fable_lite_varyings.gdshaderinc").Contains("void light()", StringComparison.Ordinal)).IsFalse();
        AssertThat(Source("res://src/Engine/material/standard_material_body.gdshaderinc").Contains("FABLE_LITE_TRIM_VARYINGS", StringComparison.Ordinal)).IsFalse();
        AssertThat(Source("res://src/Engine/level/terrain/terrain_splat_body.gdshaderinc").Contains("FABLE_LITE_FRAGMENT_WORLD", StringComparison.Ordinal)).IsFalse();
    }
}
