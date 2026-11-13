using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The phone's fine-grained AO switches (fable_lighting.gdshaderinc, composed through
/// <c>lite_extra_defines</c>): each define removes what it names from the lite builds and nothing
/// else, and none reaches the desktop.
/// </summary>
[TestSuite]
public sealed class LiteAoDefinesTests
{
    private const string PropLite = "res://src/Engine/material/lite/standard_material_lite.gdshader";
    private const string NoProp = "FABLE_LITE_NO_PROP_GROUND_AO";
    private const string NoTerrain = "FABLE_LITE_NO_TERRAIN_GROUND_AO";
    private const string NoDirect = "FABLE_LITE_NO_DIRECT_AO";
    private const string Linear = "FABLE_LITE_AO_LINEAR";
    private const string VertexGround = "FABLE_LITE_VERTEX_GROUND_AO";

    private static readonly string[] All = { NoProp, NoTerrain, NoDirect, Linear, VertexGround,
        AndroidGroundShading.NearDefine, AndroidGroundShading.VertexTerrainDefine };

    private static string File(string resPath, [CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    private static string? Include(string resPath)
    {
        try
        {
            return File(resPath);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>A lite build with <paramref name="extra"/> composed, preprocessed as Godot would.</summary>
    internal static string Lite(string litePath, params string[] extra) =>
        Engine_ShaderCeilingSource.Preprocess(
            Engine_ShaderVariant.WithExtraDefines(
                Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(litePath)), litePath, false, false, false), extra),
            litePath,
            Include);

    /// <summary>A full shader file, preprocessed, with <paramref name="defines"/> forced in front of its include.</summary>
    private static string Full(string fullPath, params string[] defines)
    {
        string code = File(fullPath);
        foreach (string define in defines)
        {
            code = Refii.Fx.Engine_FxDepthReads.Compose(code, define);
        }

        return Engine_ShaderCeilingSource.Preprocess(code, fullPath, Include);
    }

    /// <summary>The function whose definition starts with <paramref name="signature"/>: its text, braces included.</summary>
    internal static string Function(string code, string signature)
    {
        int start = code.IndexOf(signature, StringComparison.Ordinal);
        AssertThat(start).IsGreater(-1);
        int open = code.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return code[start..(i + 1)];
            }
        }

        throw new FormatException("unbalanced " + signature);
    }

    private static string Without(string code, string signature) => code.Replace(Function(code, signature), string.Empty, StringComparison.Ordinal);

    /// <summary>A read of the ground-contact map: filtered, at a level, or one texel.</summary>
    private static readonly System.Text.RegularExpressions.Regex ContactFetch = new(
        @"\b(?:texture|textureLod|texelFetch)\(fable_terrain_contact_ao\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool Fetches(string code) => ContactFetch.IsMatch(code);

    /// <summary>
    /// Whether anything the shader runs reaches a read of the ground AO map: Godot emits only the
    /// functions a stage reaches, so the stages' reachable code is what counts.
    /// </summary>
    private static bool ReachesGroundAo(string code) =>
        new[] { "vertex", "fragment", "light" }.Any(stage => Fetches(Stage(code, stage)));

    private static string Declaration(string code) =>
        code.Split('\n').Single(l => l.Contains("global uniform sampler2D fable_terrain_contact_ao", StringComparison.Ordinal));

    /// <summary>Without a switch, both families fetch the ground AO, as before.</summary>
    [TestCase]
    public void TheLiteBuildsFetchTheGroundAo()
    {
        AssertThat(ReachesGroundAo(Lite(PropLite))).IsTrue();
        AssertThat(ReachesGroundAo(Lite(Engine_ShaderVariant.TerrainLitePath))).IsTrue();
    }

    /// <summary>The prop switch removes the props' fetch and keeps vertex x instance AO; the terrain keeps its own.</summary>
    [TestCase]
    public void NoPropGroundAoRemovesOnlyThePropsFetch()
    {
        string prop = Lite(PropLite, NoProp);
        AssertThat(ReachesGroundAo(prop)).IsFalse();
        string evaluate = Function(prop, "float fable_evaluate_prop_baked_ao(");
        AssertThat(evaluate.Contains("instance_baked_ao", StringComparison.Ordinal)).IsTrue();
        AssertThat(evaluate.Contains("color_alpha", StringComparison.Ordinal)).IsTrue();
        AssertThat(evaluate.Contains("smoothstep", StringComparison.Ordinal)).IsFalse();
        AssertThat(ReachesGroundAo(Lite(Engine_ShaderVariant.TerrainLitePath, NoProp))).IsTrue();
    }

    /// <summary>The terrain switch removes the terrain's fetch; props keep theirs.</summary>
    [TestCase]
    public void NoTerrainGroundAoRemovesOnlyTheTerrainsFetch()
    {
        AssertThat(ReachesGroundAo(Lite(Engine_ShaderVariant.TerrainLitePath, NoTerrain))).IsFalse();
        AssertThat(ReachesGroundAo(Lite(Engine_ShaderVariant.TerrainPagePath, NoTerrain))).IsFalse();
        AssertThat(ReachesGroundAo(Lite(PropLite, NoTerrain))).IsTrue();
    }

    /// <summary>With both, no lite build of either set reaches the fetch.</summary>
    [TestCase]
    public void BothGroundSwitchesRemoveEveryFetch()
    {
        int builds = 0;
        foreach (string path in Engine_ShaderVariant.AllVariantPaths)
        {
            AssertThat(ReachesGroundAo(Lite(path, NoProp, NoTerrain))).IsFalse();
            builds++;
        }

        AssertThat(builds).IsEqual(36);
    }

    /// <summary>No direct AO: light() is not weighted, and the weight's uniforms are not read for it.</summary>
    [TestCase]
    public void NoDirectAoMakesTheWeightOne()
    {
        string off = Function(Lite(PropLite, NoDirect), "float fable_direct_ao(");
        AssertThat(off.Contains("return 1.0;", StringComparison.Ordinal)).IsTrue();
        AssertThat(off.Contains("fable_ao_light_affect", StringComparison.Ordinal)).IsFalse();
        string on = Function(Lite(Engine_ShaderVariant.TerrainLitePath), "float fable_direct_ao(");
        AssertThat(on.Contains("FABLE_AO_LIGHT_AFFECT", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Linear AO: the strength is a mix, no pow.</summary>
    [TestCase]
    public void LinearAoDropsThePow()
    {
        string linear = Function(Lite(PropLite, Linear), "float fable_apply_ao_strength(");
        AssertThat(linear.Contains("pow(", StringComparison.Ordinal)).IsFalse();
        AssertThat(linear.Contains("mix(1.0, raw_ao, FABLE_AO_STRENGTH)", StringComparison.Ordinal)).IsTrue();
        AssertThat(Function(Lite(PropLite), "float fable_apply_ao_strength(").Contains("pow(", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>A function definition's header: return type, name, parameters, opening brace.</summary>
    private static readonly System.Text.RegularExpressions.Regex Definition = new(
        @"^[ \t]*(?:[A-Za-z_]\w*[ \t]+)+([A-Za-z_]\w*)[ \t]*\([^;{)]*\)[ \t]*\{",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The code <paramref name="stage"/> (vertex, fragment or light) runs: its processor function
    /// and every function it reaches, which is what Godot emits for that stage.
    /// </summary>
    internal static string Stage(string code, string stage)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in Definition.Matches(code))
        {
            string name = match.Groups[1].Value;
            if (name is not ("if" or "for" or "while" or "switch") && !bodies.ContainsKey(name))
            {
                bodies[name] = Function(code[match.Index..], match.Value);
            }
        }

        AssertThat(bodies.ContainsKey(stage)).IsTrue();
        var reached = new HashSet<string>(StringComparer.Ordinal) { stage };
        var pending = new Queue<string>(reached);
        while (pending.Count > 0)
        {
            string body = bodies[pending.Dequeue()];
            foreach (string name in bodies.Keys.Where(name => !reached.Contains(name) &&
                System.Text.RegularExpressions.Regex.IsMatch(body, @"\b" + name + @"\s*\(")).ToList())
            {
                reached.Add(name);
                pending.Enqueue(name);
            }
        }

        return string.Join("\n", reached.Select(name => bodies[name]));
    }

    private const string ContactMap = "fable_terrain_contact_ao";

    /// <summary>
    /// Vertex ground AO: in both models' prop builds the ground-contact map is read in vertex()
    /// (textureLod: vertex() has no derivatives) and by no fragment or light() code, which takes the
    /// vertex's varying. Without the define it is the fragment's fetch, as before.
    /// </summary>
    [TestCase]
    public void VertexGroundAoMovesThePropsFetchIntoVertex()
    {
        foreach (string path in new[] { PropLite, PropLite.Replace("/lite/", "/lite/modern/", StringComparison.Ordinal) })
        {
            string prop = Lite(path, VertexGround);
            string vertex = Stage(prop, "vertex");
            AssertThat(vertex.Contains("texelFetch(" + ContactMap, StringComparison.Ordinal)).IsTrue();
            AssertThat(vertex.Contains("v_prop_ground_ao =", StringComparison.Ordinal)).IsTrue();
            string fragment = Stage(prop, "fragment");
            AssertThat(Fetches(fragment)).IsFalse();
            AssertThat(fragment.Contains("fable_prop_baked_ao_from_vertex(v_prop_ground_ao)", StringComparison.Ordinal)).IsTrue();
            AssertThat(Fetches(Stage(prop, "light"))).IsFalse();

            string before = Lite(path);
            AssertThat(Fetches(Stage(before, "fragment"))).IsTrue();
            AssertThat(Fetches(Stage(before, "vertex"))).IsFalse();
            AssertThat(before.Contains("v_prop_ground_ao", StringComparison.Ordinal)).IsFalse();
        }
    }

    /// <summary>
    /// The terrain keeps its per-pixel fetch under the vertex define, and the switches that remove the
    /// props' ground AO still remove it from every stage.
    /// </summary>
    [TestCase]
    public void VertexGroundAoLeavesTheTerrainAndYieldsToTheSwitches()
    {
        string terrain = Lite(Engine_ShaderVariant.TerrainLitePath, VertexGround);
        AssertThat(ReachesGroundAo(terrain)).IsTrue();
        AssertThat(Fetches(Stage(terrain, "vertex"))).IsFalse();
        foreach (string off in new[] { NoProp, Engine_ShaderVariant.NoBakedAo })
        {
            string prop = Lite(PropLite, VertexGround, off);
            AssertThat(prop.Contains("v_prop_ground_ao", StringComparison.Ordinal)).IsFalse();
            AssertThat(Fetches(Stage(prop, "vertex"))).IsFalse();
            AssertThat(Fetches(Stage(prop, "fragment"))).IsFalse();
        }
    }

    /// <summary>
    /// The ground AO map is never mipmapped, on any platform: its B/A bytes are one split 16-bit
    /// height that filtering between levels would corrupt (the phone's former `android_ground_ao_mips`).
    /// </summary>
    [TestCase]
    public void TheGroundAoMapIsNeverMipmapped()
    {
        foreach (string path in Engine_ShaderVariant.AllVariantPaths)
        {
            AssertThat(Declaration(Lite(path)).Contains("filter_linear,", StringComparison.Ordinal)).IsTrue();
        }

        AssertThat(Declaration(Full(Engine_ShaderFamily.StandardPath)).Contains("filter_linear,", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// Every switch needs FABLE_LITE: forced into a full shader they change nothing — the desktop's
    /// code is the code it was. Only the #define lines themselves differ.
    /// </summary>
    [TestCase]
    public void TheFullShadersIgnoreEverySwitch()
    {
        static string Code(string text) => string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("#define FABLE_LITE_", StringComparison.Ordinal)));
        foreach (string full in new[] { Engine_ShaderFamily.StandardPath, Engine_ShaderVariant.TerrainFullPath })
        {
            AssertThat(Code(Full(full, All))).IsEqual(Code(Full(full)));
        }
    }

    /// <summary>A define the build already states is not stated twice; a new one goes ahead of the include.</summary>
    [TestCase]
    public void ExtraDefinesSkipWhatTheBuildStates()
    {
        string full = File(Engine_ShaderVariant.FullPathOf(PropLite));
        string built = Engine_ShaderVariant.WithExtraDefines(
            Engine_ShaderVariant.Compose(full, PropLite, false, false, false), new[] { "FABLE_LITE", NoDirect, NoDirect });
        int Count(string define) => built.Split('\n').Count(l => l == "#define " + define);
        AssertThat(Count("FABLE_LITE")).IsEqual(1);
        AssertThat(Count(NoDirect)).IsEqual(1);
        AssertThat(built.IndexOf("#define " + NoDirect, StringComparison.Ordinal))
            .IsLess(built.IndexOf("#include", StringComparison.Ordinal));
        AssertThat(Engine_ShaderVariant.Defines("#define FABLE_LITE_NO_SPEC\n", "FABLE_LITE")).IsFalse();
        AssertThat(Engine_ShaderVariant.Defines("x\n  #define FABLE_LITE\n", "FABLE_LITE")).IsTrue();
    }

    /// <summary>The helpers compile out with baked AO off or under the fable model; only modern with AO on keeps them.</summary>
    [TestCase]
    public void NoBakedAoUnderTheFableModel()
    {
        AssertThat(Engine_ShaderVariant.NoBakedAoFor(bakedAoEnabled: true, modernModel: true)).IsFalse();
        AssertThat(Engine_ShaderVariant.NoBakedAoFor(bakedAoEnabled: true, modernModel: false)).IsTrue();
        AssertThat(Engine_ShaderVariant.NoBakedAoFor(bakedAoEnabled: true, modernModel: false, fableFold: false)).IsFalse();
        AssertThat(Engine_ShaderVariant.NoBakedAoFor(bakedAoEnabled: false, modernModel: false, fableFold: false)).IsTrue();
        AssertThat(Engine_ShaderVariant.NoBakedAoFor(bakedAoEnabled: false, modernModel: true)).IsTrue();
        AssertThat(Engine_ShaderVariant.NoBakedAoFor(bakedAoEnabled: false, modernModel: false)).IsTrue();
    }

    /// <summary>
    /// Why the define is exact under the fable model (strength 0, light-affect 0): the strength helper
    /// returns 1 at zero strength before reading its argument, the direct weight is the light-affect
    /// times `fable_unified`, and the raw baked AO reaches nothing but the strength helper.
    /// </summary>
    [TestCase]
    public void NoBakedAoMatchesZeroStrength()
    {
        foreach (string path in new[] { PropLite, Engine_ShaderVariant.TerrainLitePath })
        {
            string code = Lite(path);
            string strength = Function(code, "float fable_apply_ao_strength(");
            int early = strength.IndexOf("if (FABLE_AO_STRENGTH <= 0.001 ||", StringComparison.Ordinal);
            AssertThat(early).IsGreater(-1);
            early = strength.IndexOf('{', early);
            AssertThat(strength.IndexOf("return 1.0;", early, StringComparison.Ordinal))
                .IsLess(strength.IndexOf("raw_ao", early, StringComparison.Ordinal));
            AssertThat(Function(code, "float fable_direct_ao(")
                .Contains("max(0.0, min(1.0, FABLE_UNIFIED * FABLE_AO_LIGHT_AFFECT))", StringComparison.Ordinal)).IsTrue();

            string raw = path == PropLite ? "baked_ao" : "terrain_contact_ao";
            var uses = code.Split('\n')
                .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"(?<![\w.])" + raw + @"\b") &&
                    !l.TrimStart().StartsWith("float " + raw + " =", StringComparison.Ordinal))
                .ToList();
            AssertThat(uses.Count).IsGreater(0);
            foreach (string use in uses)
            {
                AssertThat(use.Contains("fable_apply_ao_strength(", StringComparison.Ordinal)).OverrideFailureMessage(use).IsTrue();
            }

            string folded = Engine_ShaderCeilingSource.Preprocess(
                Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(path)), path, true, false, false), path, Include);
            AssertThat(Function(folded, "float fable_apply_ao_strength(").Contains("return 1.0;", StringComparison.Ordinal)).IsTrue();
            AssertThat(Function(folded, "float fable_direct_ao(").Contains("fable_ao_light_affect", StringComparison.Ordinal)).IsFalse();
            // Upstream's never-called fable_sample_terrain_ao_and_height stays in the text; no stage reaches it.
            AssertThat(ReachesGroundAo(folded)).IsFalse();
        }
    }
}
