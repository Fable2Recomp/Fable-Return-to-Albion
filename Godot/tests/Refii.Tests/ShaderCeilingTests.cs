using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GdUnit4;
using Refii.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The `shaderCeiling` lever: its request policy, and the replacement source it builds from the real shaders.</summary>
[TestSuite]
public sealed class ShaderCeilingTests
{
    private static string Root([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));

    private static string? Read(string resPath)
    {
        if (Refii.Models.Engine_ShaderVariant.IsBuild(resPath))
        {
            return Read(Refii.Models.Engine_ShaderVariant.FullPathOf(resPath)) is { } full
                ? Refii.Models.Engine_ShaderVariant.Compose(full, resPath)
                : null;
        }

        string path = Path.Combine(Root(), resPath["res://".Length..]);
        return File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
    }

    private static Engine_ShaderCeilingSource.Built Build(string resPath, bool cut) =>
        Engine_ShaderCeilingSource.Build(Read(resPath)!, resPath, Read, cut, (0.5f, 0.4f, 0.3f))
        ?? throw new InvalidOperationException(resPath);

    private static Engine_ShaderCeiling.Request? Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return Engine_ShaderCeiling.Parse(document.RootElement);
    }

    /// <summary>Each request states the whole ceiling; `all` fills what it does not name; bad values throw.</summary>
    [TestCase]
    public void ParsesTheWholeCeiling()
    {
        AssertThat(Parse("{\"op\":\"render\"}") is null).IsTrue();

        Engine_ShaderCeiling.Request object_ = Parse("{\"shaderCeiling\":{\"props\":\"flat\",\"trees\":\"flatcut\",\"entities\":\"hide\"}}")!.Value;
        AssertThat(object_.Modes[(int)Engine_ShaderCeiling.Category.Props] == Engine_ShaderCeiling.Mode.Flat).IsTrue();
        AssertThat(object_.Modes[(int)Engine_ShaderCeiling.Category.Trees] == Engine_ShaderCeiling.Mode.FlatCut).IsTrue();
        AssertThat(object_.Modes[(int)Engine_ShaderCeiling.Category.Terrain] == Engine_ShaderCeiling.Mode.Off).IsTrue();
        AssertThat(object_.Modes[(int)Engine_ShaderCeiling.Category.Entities] == Engine_ShaderCeiling.Mode.Hide).IsTrue();
        AssertThat(object_.Unshaded).IsFalse();

        Engine_ShaderCeiling.Request all = Parse("{\"shaderCeiling\":{\"all\":\"flat\",\"terrain\":\"off\"}}")!.Value;
        AssertThat(all.Modes[(int)Engine_ShaderCeiling.Category.Entities] == Engine_ShaderCeiling.Mode.Flat).IsTrue();
        AssertThat(all.Modes[(int)Engine_ShaderCeiling.Category.Terrain] == Engine_ShaderCeiling.Mode.Off).IsTrue();

        Engine_ShaderCeiling.Request unshaded = Parse("{\"shaderCeiling\":\"unshaded\"}")!.Value;
        AssertThat(unshaded.Unshaded).IsTrue();
        AssertThat(Array.TrueForAll(unshaded.Modes, m => m == Engine_ShaderCeiling.Mode.Off)).IsTrue();
        AssertThat(Array.TrueForAll(Parse("{\"shaderCeiling\":\"off\"}")!.Value.Modes, m => m == Engine_ShaderCeiling.Mode.Off)).IsTrue();

        foreach (string bad in new[] { "{\"shaderCeiling\":3}", "{\"shaderCeiling\":\"dim\"}", "{\"shaderCeiling\":{\"water\":\"flat\"}}", "{\"shaderCeiling\":{\"props\":\"unshaded\"}}" })
        {
            bool threw = false;
            try
            {
                Parse(bad);
            }
            catch (JsonException)
            {
                threw = true;
            }
            AssertThat(threw).IsTrue();
        }
    }

    /// <summary>
    /// The entities' split: attachments follow the characters, the entities' lights go with the root
    /// exactly when it went whole before, the switches are off unless named, `nodraw` parses, and the
    /// hide-only categories take nothing else.
    /// </summary>
    [TestCase]
    public void SplitsTheEntities()
    {
        static Engine_ShaderCeiling.Mode M(Engine_ShaderCeiling.Request r, Engine_ShaderCeiling.Category c) => r.Modes[(int)c];
        const Engine_ShaderCeiling.Mode Off = Engine_ShaderCeiling.Mode.Off, Hide = Engine_ShaderCeiling.Mode.Hide;

        // The old whole hide: everything under the root, its lights with it; no switch.
        Engine_ShaderCeiling.Request whole = Parse("{\"shaderCeiling\":{\"entities\":\"hide\"}}")!.Value;
        AssertThat(M(whole, Engine_ShaderCeiling.Category.Characters) == Hide).IsTrue();
        AssertThat(M(whole, Engine_ShaderCeiling.Category.Attachments) == Hide).IsTrue();
        AssertThat(M(whole, Engine_ShaderCeiling.Category.EntityLights) == Hide).IsTrue();
        AssertThat(M(whole, Engine_ShaderCeiling.Category.Highlight) == Off).IsTrue();
        AssertThat(M(whole, Engine_ShaderCeiling.Category.EntityFeed) == Off).IsTrue();

        // The rigid entities alone, as before: the lights stay.
        Engine_ShaderCeiling.Request rigid = Parse("{\"shaderCeiling\":{\"entities\":\"hide\",\"characters\":\"off\"}}")!.Value;
        AssertThat(M(rigid, Engine_ShaderCeiling.Category.Attachments) == Off).IsTrue();
        AssertThat(M(rigid, Engine_ShaderCeiling.Category.EntityLights) == Off).IsTrue();

        // Every node by node, the root and its lights kept.
        Engine_ShaderCeiling.Request nodes = Parse("{\"shaderCeiling\":{\"entities\":\"hide\",\"entitylights\":\"off\"}}")!.Value;
        AssertThat(M(nodes, Engine_ShaderCeiling.Category.Characters) == Hide).IsTrue();
        AssertThat(M(nodes, Engine_ShaderCeiling.Category.EntityLights) == Off).IsTrue();

        // Drawn by nobody, shown in the tree; the parts follow, a named part does not.
        Engine_ShaderCeiling.Request noDraw = Parse("{\"shaderCeiling\":{\"entities\":\"nodraw\",\"attachments\":\"off\"}}")!.Value;
        AssertThat(M(noDraw, Engine_ShaderCeiling.Category.Characters) == Engine_ShaderCeiling.Mode.NoDraw).IsTrue();
        AssertThat(M(noDraw, Engine_ShaderCeiling.Category.Attachments) == Off).IsTrue();
        AssertThat(M(noDraw, Engine_ShaderCeiling.Category.EntityLights) == Off).IsTrue();

        Engine_ShaderCeiling.Request parts = Parse("{\"shaderCeiling\":{\"characters\":\"flat\"}}")!.Value;
        AssertThat(M(parts, Engine_ShaderCeiling.Category.Attachments) == Engine_ShaderCeiling.Mode.Flat).IsTrue();
        AssertThat(M(parts, Engine_ShaderCeiling.Category.Entities) == Off).IsTrue();

        Engine_ShaderCeiling.Request switches = Parse("{\"shaderCeiling\":{\"highlight\":\"hide\",\"entityfeed\":\"hide\"}}")!.Value;
        AssertThat(M(switches, Engine_ShaderCeiling.Category.Highlight) == Hide).IsTrue();
        AssertThat(M(switches, Engine_ShaderCeiling.Category.EntityFeed) == Hide).IsTrue();
        AssertThat(M(switches, Engine_ShaderCeiling.Category.Entities) == Off).IsTrue();

        // `all` with a drawing mode leaves the hide-only categories off; `all` hide takes the
        // entities' lights but never a switch, which acts only when named.
        Engine_ShaderCeiling.Request allFlat = Parse("{\"shaderCeiling\":{\"all\":\"flat\"}}")!.Value;
        AssertThat(M(allFlat, Engine_ShaderCeiling.Category.Highlight) == Off).IsTrue();
        AssertThat(M(allFlat, Engine_ShaderCeiling.Category.EntityLights) == Off).IsTrue();
        AssertThat(allFlat.Words[(int)Engine_ShaderCeiling.Category.EntityLights] is null).IsTrue();
        foreach (string allHide in new[] { "{\"shaderCeiling\":\"hide\"}", "{\"shaderCeiling\":{\"all\":\"hide\"}}" })
        {
            Engine_ShaderCeiling.Request hidden = Parse(allHide)!.Value;
            for (int c = 0; c < hidden.Modes.Length; c++)
            {
                bool isSwitch = (Engine_ShaderCeiling.Category)c is Engine_ShaderCeiling.Category.Highlight or Engine_ShaderCeiling.Category.EntityFeed;
                AssertThat(hidden.Modes[c] == (isSwitch ? Off : Hide)).IsTrue();
            }
        }
        Engine_ShaderCeiling.Request allAndOne = Parse("{\"shaderCeiling\":{\"all\":\"hide\",\"highlight\":\"hide\"}}")!.Value;
        AssertThat(M(allAndOne, Engine_ShaderCeiling.Category.Highlight) == Hide).IsTrue();
        AssertThat(M(allAndOne, Engine_ShaderCeiling.Category.EntityFeed) == Off).IsTrue();

        foreach (string bad in new[]
        {
            "{\"shaderCeiling\":{\"highlight\":\"flat\"}}",
            "{\"shaderCeiling\":{\"entitylights\":\"nodraw\"}}",
            "{\"shaderCeiling\":{\"entityfeed\":\"+shadows_disabled\"}}",
        })
        {
            bool threw = false;
            try
            {
                Parse(bad);
            }
            catch (JsonException)
            {
                threw = true;
            }
            AssertThat(threw).IsTrue();
        }
    }

    /// <summary>A swaying tree keeps its sway and, cut, its scissor; the PRT transfer and the lighting go.</summary>
    [TestCase]
    public void TreeKeepsItsSwayAndItsCutout()
    {
        Engine_ShaderCeilingSource.Built tree = Build("res://src/Engine/material/lite/standard_material_tree_lite.gdshader", cut: true);
        AssertThat(tree.KeptVertex).IsTrue();
        AssertThat(tree.Cut).IsTrue();
        AssertThat(tree.Code.Contains("VERTEX = mix(VERTEX, fable_tree_sway(VERTEX, CUSTOM1, MODEL_MATRIX), sway_keep);")).IsTrue();
        AssertThat(tree.Code.Contains("if (alpha_scissor_on && fable_ceiling_texel.a < alpha_scissor_threshold)")).IsTrue();
        AssertThat(tree.Code.Contains("render_mode unshaded, blend_mix, depth_draw_opaque, cull_disabled;")).IsTrue();
        AssertThat(tree.Code.Contains("fable_transfer")).IsFalse();
        AssertThat(tree.Code.Contains("fable_world =")).IsFalse();
        AssertThat(tree.Code.Contains("void light()")).IsFalse();
        AssertThat(tree.Code.Contains("#include")).IsFalse();

        Engine_ShaderCeilingSource.Built flat = Build("res://src/Engine/material/lite/standard_material_tree_lite.gdshader", cut: false);
        AssertThat(flat.Code.Contains("discard")).IsFalse();
        AssertThat(flat.AlphaTested).IsTrue();
    }

    /// <summary>`flatvs` keeps the whole vertex stage and reads what it hands on, behind a test never taken.</summary>
    [TestCase]
    public void FlatVsKeepsTheVertexStageAndItsOutputs()
    {
        Engine_ShaderCeilingSource.Built standard = Engine_ShaderCeilingSource.Build(
            Read("res://src/Engine/material/lite/standard_material_lite.gdshader")!,
            "res://src/Engine/material/lite/standard_material_lite.gdshader", Read, cut: true, (0.5f, 0.4f, 0.3f),
            keepVaryings: true)!.Value;
        AssertThat(standard.KeptVertex).IsTrue();
        AssertThat(standard.Code.Contains("// shaderCeiling: flatvs of")).IsTrue();
        AssertThat(standard.Code.Contains("fable_transfer(CUSTOM0")).IsTrue();
        AssertThat(standard.Code.Contains("ALBEDO += (FRAGCOORD.x < 0.0 ? 1.0 : 0.0) * vec3(")).IsTrue();
        foreach (string read in new[] { "float(v_transfer.w)", "float(v_tree_fade)", "float(fable_world.z)", "NORMAL.x", "UV.x", "UV2.x", "TANGENT.x" })
        {
            AssertThat(standard.Code.Contains(read)).IsTrue();
        }
        AssertThat(standard.Code.Contains("void light()")).IsFalse();
        AssertThat(standard.Code.Contains("float(v_specular")).IsFalse();

        Engine_ShaderCeilingSource.Built tree = Engine_ShaderCeilingSource.Build(
            Read("res://src/Engine/material/lite/standard_material_tree_lite.gdshader")!,
            "res://src/Engine/material/lite/standard_material_tree_lite.gdshader", Read, cut: true, (0.5f, 0.4f, 0.3f),
            keepVaryings: true)!.Value;
        AssertThat(tree.Code.Contains("fable_tree_sway(VERTEX, CUSTOM1, MODEL_MATRIX)")).IsTrue();
        AssertThat(tree.Cut).IsTrue();

        // flatcut is as it was.
        AssertThat(Build("res://src/Engine/material/lite/standard_material_lite.gdshader", cut: true).Code.Contains("FRAGCOORD.x < 0.0 ? 1.0")).IsFalse();
    }

    /// <summary>The wind's WIND1 offset stays; its PRT varying goes, and with it the transfer function.</summary>
    [TestCase]
    public void WindKeepsTheOffsetNotTheTransfer()
    {
        Engine_ShaderCeilingSource.Built wind = Build("res://src/Engine/material/standard_material_wind.gdshader", cut: false);
        AssertThat(wind.KeptVertex).IsTrue();
        AssertThat(wind.Code.Contains("VERTEX += inverse(mat3(MODEL_MATRIX)) * world_offset;")).IsTrue();
        AssertThat(wind.Code.Contains("v_transfer =")).IsFalse();
        AssertThat(wind.Code.Contains("vec4 fable_transfer(")).IsFalse();
        // Every declaration stays, so the material's parameters bind as they did.
        AssertThat(wind.Code.Contains("uniform sampler2D albedo_tex")).IsTrue();
        AssertThat(wind.Code.Contains("instance uniform float xr_fade_alpha")).IsTrue();
    }

    /// <summary>A still surface draws on Godot's own vertex stage, with its cull word.</summary>
    [TestCase]
    public void StillSurfacesTakeThePlainVertex()
    {
        Engine_ShaderCeilingSource.Built culled = Build("res://src/Engine/material/lite/standard_material_culled_lite.gdshader", cut: true);
        AssertThat(culled.KeptVertex).IsFalse();
        AssertThat(culled.AlphaTested).IsFalse();
        AssertThat(culled.Code.Contains("void vertex()")).IsFalse();
        AssertThat(culled.Code.Contains("cull_back")).IsTrue();
        AssertThat(culled.Code.Contains("discard")).IsFalse();

        Engine_ShaderCeilingSource.Built terrain = Build("res://src/Engine/level/terrain/lite/terrain_splat_lite.gdshader", cut: false);
        AssertThat(terrain.KeptVertex).IsFalse();
        AssertThat(terrain.Code.Contains("render_mode unshaded, cull_disabled;")).IsTrue();
        AssertThat(terrain.Code.Contains("uniform sampler2DArray material_weight")).IsTrue();
    }

    /// <summary>A surface that writes ALPHA stays in the transparent list; the canopy's test is its own.</summary>
    [TestCase]
    public void AlphaWritersStayTransparent()
    {
        Engine_ShaderCeilingSource.Built canopy = Build("res://src/Engine/material/families/tree_canopy.gdshader", cut: true);
        AssertThat(canopy.WritesAlpha).IsTrue();
        AssertThat(canopy.Code.Contains("ALPHA = 1.0;")).IsTrue();
        AssertThat(canopy.Code.Contains("if ((fable_ceiling_texel.a * alpha_scale) < alpha_threshold)")).IsTrue();

        Engine_ShaderCeilingSource.Built glass = Build("res://src/Engine/material/standard_material_translucent.gdshader", cut: false);
        AssertThat(glass.WritesAlpha).IsTrue();
        AssertThat(glass.Code.Contains("blend_mix")).IsTrue();
        AssertThat(glass.Code.Contains("depth_draw_never")).IsTrue();
        AssertThat(glass.Code.Contains("cull_disabled")).IsTrue();
    }

    /// <summary>Includes resolve once and relative to their file; conditionals and `#if` evaluate as Godot's do.</summary>
    [TestCase]
    public void PreprocessesLikeGodot()
    {
        var files = new Dictionary<string, string>
        {
            ["res://a/b.gdshaderinc"] = "#include \"c.gdshaderinc\"\nfloat b() { return 1.0; }\n",
            ["res://a/c.gdshaderinc"] = "// c\nconst float C = 2.0; /* gone */\n",
        };
        string code = "shader_type spatial;\n#define ON\n#include \"res://a/b.gdshaderinc\"\n#include \"res://a/c.gdshaderinc\"\n" +
            "#ifdef ON\nfloat yes;\n#else\nfloat no;\n#endif\n#if CURRENT_RENDERER == RENDERER_MOBILE && !defined(OFF)\nfloat mobile;\n#endif\n";
        string active = Engine_ShaderCeilingSource.Preprocess(code, "res://x.gdshader", p => files.TryGetValue(p, out string? t) ? t : null);
        AssertThat(active.Split("const float C").Length).IsEqual(2);
        AssertThat(active.Contains("float yes;")).IsTrue();
        AssertThat(active.Contains("float no;")).IsFalse();
        AssertThat(active.Contains("float mobile;")).IsTrue();
        AssertThat(active.Contains("gone")).IsFalse();
        AssertThat(Engine_ShaderCeilingSource.Resolve("res://a/b/c.gdshader", "../d.gdshaderinc")).IsEqual("res://a/d.gdshaderinc");
    }
    /// <summary>A `+` mode names its words, per category or for all; a word must look like one.</summary>
    [TestCase]
    public void ParsesRenderModeWords()
    {
        Engine_ShaderCeiling.Request request = Parse("{\"shaderCeiling\":{\"all\":\"+shadows_disabled\",\"terrain\":\"+shadows_disabled, ambient_light_disabled\",\"trees\":\"off\"}}")!.Value;
        AssertThat(request.Modes[(int)Engine_ShaderCeiling.Category.Props] == Engine_ShaderCeiling.Mode.RenderModes).IsTrue();
        AssertThat(string.Join(",", request.Words[(int)Engine_ShaderCeiling.Category.Entities]!)).IsEqual("shadows_disabled");
        AssertThat(string.Join(",", request.Words[(int)Engine_ShaderCeiling.Category.Terrain]!)).IsEqual("shadows_disabled,ambient_light_disabled");
        AssertThat(request.Modes[(int)Engine_ShaderCeiling.Category.Trees] == Engine_ShaderCeiling.Mode.Off).IsTrue();
        AssertThat(request.Words[(int)Engine_ShaderCeiling.Category.Trees] is null).IsTrue();

        bool threw = false;
        try
        {
            Parse("{\"shaderCeiling\":{\"props\":\"+shadows disabled;\"}}");
        }
        catch (JsonException)
        {
            threw = true;
        }
        AssertThat(threw).IsTrue();
    }

    /// <summary>The words merge into the top-level shader's own render_mode; the include and the rest stay as written.</summary>
    [TestCase]
    public void RenderModesMergeIntoTheTopLevelShader()
    {
        const string path = "res://src/Engine/material/lite/standard_material_lite.gdshader";
        string code = Engine_ShaderCeilingSource.WithRenderModes(
            Read(path)!, path, Read, new[] { "shadows_disabled", "ambient_light_disabled", "cull_disabled" })!;
        AssertThat(code.Contains("render_mode blend_mix, depth_draw_opaque, cull_disabled, shadows_disabled, ambient_light_disabled;")).IsTrue();
        AssertThat(code.Split("render_mode").Length).IsEqual(2);
        AssertThat(code.Contains("#include \"res://src/Engine/material/standard_material_body.gdshaderinc\"")).IsTrue();
        AssertThat(code.Contains("#define FABLE_LITE")).IsTrue();

        // Already stated: nothing to change. A clash with the shader's own group: refused.
        AssertThat(Engine_ShaderCeilingSource.WithRenderModes(Read(path)!, path, Read, new[] { "cull_disabled" }) is null).IsTrue();
        bool threw = false;
        try
        {
            Engine_ShaderCeilingSource.WithRenderModes(Read(path)!, path, Read, new[] { "cull_back" });
        }
        catch (FormatException)
        {
            threw = true;
        }
        AssertThat(threw).IsTrue();

        // A render_mode stated only by an include gets one of its own beside it; relative includes become absolute.
        var files = new Dictionary<string, string> { ["res://a/body.gdshaderinc"] = "render_mode cull_back;\nvoid fragment() {}\n" };
        string included = Engine_ShaderCeilingSource.WithRenderModes(
            "shader_type spatial;\n#include \"body.gdshaderinc\"\n", "res://a/top.gdshader",
            p => files.TryGetValue(p, out string? t) ? t : null, new[] { "shadows_disabled", "cull_back" })!;
        AssertThat(included).IsEqual("shader_type spatial;\nrender_mode shadows_disabled;\n#include \"res://a/body.gdshaderinc\"\n");
    }
    /// <summary>`define:NAME` lands in the top-level text before the first include; a name already defined is skipped.</summary>
    [TestCase]
    public void DefinesLandBeforeTheBody()
    {
        Engine_ShaderCeiling.Request request = Parse("{\"shaderCeiling\":{\"props\":\"+shadows_disabled,define:FABLE_SOLID\"}}")!.Value;
        AssertThat(string.Join(",", request.Words[(int)Engine_ShaderCeiling.Category.Props]!)).IsEqual("shadows_disabled,define:FABLE_SOLID");

        const string path = "res://src/Engine/material/lite/standard_material_tree_lite.gdshader";
        string code = Engine_ShaderCeilingSource.WithRenderModes(
            Read(path)!, path, Read, new[] { "shadows_disabled", "define:FABLE_SOLID" })!;
        AssertThat(code.Contains("render_mode blend_mix, depth_draw_opaque, cull_disabled, shadows_disabled;")).IsTrue();
        int define = code.IndexOf("#define FABLE_SOLID\n", StringComparison.Ordinal);
        AssertThat(define > code.IndexOf("#define FABLE_TREE_WIND", StringComparison.Ordinal)).IsTrue();
        AssertThat(define < code.IndexOf("#include", StringComparison.Ordinal)).IsTrue();

        // The culled shaders are FABLE_SOLID already: nothing to change.
        const string culled = "res://src/Engine/material/lite/standard_material_culled_lite.gdshader";
        AssertThat(Engine_ShaderCeilingSource.WithRenderModes(Read(culled)!, culled, Read, new[] { "define:FABLE_SOLID" }) is null).IsTrue();

        // No include: after the render mode.
        AssertThat(Engine_ShaderCeilingSource.WithRenderModes(
            "shader_type spatial;\nrender_mode cull_back;\nvoid fragment() {}\n", "res://x.gdshader", _ => null, new[] { "define:X" }))
            .IsEqual("shader_type spatial;\nrender_mode cull_back;\n#define X\n\nvoid fragment() {}\n");
    }

    /// <summary>The census: which standard shaders compile a discard, and which carry the scissor gate.</summary>
    [TestCase]
    public void InspectsDiscards()
    {
        Engine_ShaderCeilingSource.Facts Facts(string path) => Engine_ShaderCeilingSource.Inspect(Read(path)!, path, Read);
        AssertThat(Facts("res://src/Engine/material/lite/standard_material_lite.gdshader") == new Engine_ShaderCeilingSource.Facts(true, true)).IsTrue();
        AssertThat(Facts("res://src/Engine/material/lite/standard_material_tree_lite.gdshader").Discards).IsTrue();
        AssertThat(Facts("res://src/Engine/material/lite/standard_material_culled_lite.gdshader") == new Engine_ShaderCeilingSource.Facts(false, true)).IsTrue();
        AssertThat(Facts("res://src/Engine/material/families/tree_canopy.gdshader") == new Engine_ShaderCeilingSource.Facts(true, false)).IsTrue();
        AssertThat(Facts("res://src/Engine/level/terrain/lite/terrain_splat_lite.gdshader").Discards).IsFalse();
    }
}
