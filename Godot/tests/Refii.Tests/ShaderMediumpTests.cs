using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GdUnit4;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// FABLE_LITE_MEDIUMP, the phone prototype in fable_lighting.gdshaderinc: it may only ADD precision
/// qualifiers to the light() maths. Without it the shaders are what they were; with it the code is
/// the same once the `mediump`s are taken out, and none lands on a position or a distance.
/// </summary>
[TestSuite]
public sealed class ShaderMediumpTests
{
    private const string Macro = "FABLE_LITE_MP";

    private static readonly string[] Shaders =
    {
        Engine_ShaderFamily.StandardPath,
        Engine_ShaderFamily.StandardCulledPath,
        Engine_ShaderFamily.StandardTranslucentPath,
        Engine_ShaderFamily.StandardTreePath,
        Engine_ShaderFamily.StandardWindPath,
        Engine_ShaderVariant.TerrainFullPath,
    };

    /// <summary>The lite define sets the phone composes (Engine_ShaderVariant), and the full build.</summary>
    private static readonly string[][] DefineSets =
    {
        Array.Empty<string>(),
        new[] { "FABLE_LITE", "FABLE_LITE_NO_ENVMAP" },
        new[] { "FABLE_LITE" },
        new[] { "FABLE_LITE_NO_SPEC" },
        new[] { "FABLE_LITE", "FABLE_LITE_NO_ENVMAP", "FABLE_LITE_MODERN" },
        new[] { "FABLE_LITE", "FABLE_LITE_NO_BAKED_AO", "FABLE_LITE_NO_STATIC_SHADOW" },
        new[] { "FABLE_LITE", "FABLE_LITE_FLAT_TERRAIN", "FABLE_LITE_NO_CLIFF" },
    };

    /// <summary>Highp values a qualifier must never reach: world positions, distances, the light
    /// table's records and the specular cosine that is raised to the material's exponent.</summary>
    private static readonly Regex HighpName = new(
        @"\bmediump\s+\w+\s+(world|world_pos|d|record|row1|row2|stated|cosine|view|specular_power|specular_amount|light_to_world)\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex Mediump = new(@"\bmediump\s+", RegexOptions.CultureInvariant);

    private static string FileText(string resPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    private static string? ReadInclude(string resPath)
    {
        try
        {
            return FileText(resPath);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="resPath"/> composed with <paramref name="defines"/> as the phone does it,
    /// preprocessed, and <see cref="Macro"/> expanded as Godot's preprocessor will expand it: the
    /// active `#define` line names its value; that line and the prototype's own define are dropped.
    /// </summary>
    private static string Build(string resPath, IEnumerable<string> defines)
    {
        string code = FileText(resPath);
        foreach (string define in defines)
        {
            code = Refii.Fx.Engine_FxDepthReads.Compose(code, define);
        }

        string text = Engine_ShaderCeilingSource.Preprocess(code, resPath, ReadInclude);
        string[] lines = text.Split('\n');
        string[] macroLines = lines.Where(l => Regex.IsMatch(l, $@"^#define\s+{Macro}\b")).ToArray();
        AssertThat(macroLines.Length).OverrideFailureMessage($"{resPath}: one active {Macro} define").IsEqual(1);
        string value = Regex.Replace(macroLines[0], $@"^#define\s+{Macro}", string.Empty).Trim();
        string body = string.Join('\n', lines.Where(l =>
            !Regex.IsMatch(l, $@"^#define\s+({Macro}|{Engine_ShaderVariant.Mediump})\b")));
        return Regex.Replace(body, $@"\b{Macro}\b", value);
    }

    private static string Collapse(string code) => Regex.Replace(code, @"\s+", " ");

    /// <summary>Whitespace collapsed, and dropped beside punctuation: an empty macro leaves `( vec3`.</summary>
    private static string Normalise(string code) =>
        Regex.Replace(Regex.Replace(code, @"\s+", " "), @" ?([(){},;=]) ?", "$1").Trim();

    /// <summary>Without the define no qualifier appears; with it a lite build's code differs only by the
    /// qualifiers, and a full build (no FABLE_LITE: the phone's switches are never read) takes none.</summary>
    [TestCase]
    public void TheDefineOnlyAddsQualifiers()
    {
        int builds = 0;
        foreach (string shader in Shaders)
        {
            foreach (string[] defines in DefineSets)
            {
                string off = Build(shader, defines);
                string on = Build(shader, defines.Append(Engine_ShaderVariant.Mediump));
                string label = $"{shader} [{string.Join(',', defines)}]";

                AssertThat(Mediump.IsMatch(off)).OverrideFailureMessage($"{label}: mediump without the define").IsFalse();
                // A full build: no FABLE_LITE, stated or derived by the body (the ground takes none from NO_SPEC).
                if (!off.Split('\n').Contains("#define FABLE_LITE"))
                {
                    AssertThat(Normalise(on)).OverrideFailureMessage($"{label}: the define reached a full build").IsEqual(Normalise(off));
                    builds++;
                    continue;
                }

                AssertThat(Mediump.Matches(on).Count).OverrideFailureMessage($"{label}: no qualifier added").IsGreater(0);
                AssertThat(Normalise(Mediump.Replace(on, string.Empty)))
                    .OverrideFailureMessage($"{label}: the define changed more than qualifiers")
                    .IsEqual(Normalise(off));
                AssertThat(HighpName.Match(on).Value).OverrideFailureMessage($"{label}: mediump on a highp value").IsEmpty();
                builds++;
            }
        }

        AssertThat(builds).IsEqual(Shaders.Length * DefineSets.Length);
    }

    /// <summary>The light() maths takes the qualifiers: the Lambert term's operands, the falloff,
    /// and the specular colours — but never the specular cosine, and through qualified locals, not
    /// qualified parameters (LiteHalfPrecisionLightingTests says why).</summary>
    [TestCase]
    public void QualifiesTheLightMaths()
    {
        string on = Collapse(Build(Engine_ShaderFamily.StandardPath, new[] { "FABLE_LITE", Engine_ShaderVariant.Mediump }));
        AssertThat(on).Contains("vec3 fable_direct(vec3 normal, vec3 light, float attenuation, vec3 light_colour) {");
        AssertThat(on).Contains("mediump vec3 n = normal, l = light, c = light_colour; mediump float a = attenuation; return clamp(dot(n, l), 0.0, 1.0) * a * (c / PI);");
        AssertThat(on).Contains("mediump vec3 n = normal, l = light, c = light_colour, s = specular_colour;");
        AssertThat(on).Contains("return lobe * gate * a * (c / PI) * s;");
        AssertThat(on).Contains("mediump float atten = fable_attenuation_weighted(");
        AssertThat(on).Contains("mediump float lobe = pow(");
        AssertThat(on).Contains("float cosine = clamp(dot(view, reflect(-light, normal)), 0.0, 1.0);");
        AssertThat(on).NotContains("mediump float cosine");
        // The fill's colours and the specular, environment and glow colours in fragment().
        AssertThat(on).Contains("mediump vec3 floor_fill = ");
        AssertThat(on).Contains("mediump vec3 spec_colour = vec3(0.0);");
        AssertThat(on).Contains("mediump vec3 ambient_half = bake * fable_lightmap_rgb() * darken * bake_ao;");
        AssertThat(on).Contains("mediump vec3 env = texture(envmap_tex, reflected).rgb;");
        AssertThat(on).Contains("mediump vec3 selfIllum = texture(emission_tex, uv).rgb;");

        string terrain = Collapse(Build(Engine_ShaderVariant.TerrainFullPath, new[] { "FABLE_LITE", Engine_ShaderVariant.Mediump }));
        AssertThat(terrain).Contains("mediump float gate = LIGHT_IS_DIRECTIONAL");
        AssertThat(terrain).Contains("mediump vec3 authored = bake * fable_lightmap_rgb() * darken * authored_weight;");
        AssertThat(terrain).Contains("mediump vec3 darken = vec3(1.0);");
    }

}
