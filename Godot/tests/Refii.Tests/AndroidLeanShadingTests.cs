using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Core;
using Refii.Launcher.Core;
using Refii.Models;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `lite_fog` (FABLE_LITE_FOG_LEAN, FABLE_LITE_FOG_TABLE) and `lite_lean_darkening`
/// (FABLE_LITE_LEAN_DARKEN): the lite builds' fog and normal-map darkening from
/// src/android/fable_lite_lean.gdshaderinc, and the fog table AndroidLeanShading builds.
/// </summary>
[TestSuite]
public sealed class AndroidLeanShadingTests
{
    private const string PropFable = "res://src/Engine/material/lite/standard_material_culled_lite.gdshader";
    private const string PropModern = "res://src/Engine/material/lite/modern/standard_material_culled_lite.gdshader";
    private static readonly string TerrainFable = Engine_ShaderVariant.TerrainFlatPath;
    private static readonly string TerrainModern = Engine_ShaderVariant.TerrainFlatPath.Replace("/lite/", "/lite/modern/", StringComparison.Ordinal);
    private const string Lean = "FABLE_LITE_FOG_LEAN";
    private const string Table = "FABLE_LITE_FOG_TABLE";
    private const string Dark = "FABLE_LITE_LEAN_DARKEN";
    private const string IncludePath = "res://src/android/fable_lite_lean.gdshaderinc";

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

    private static string Lite(string litePath, params string[] extra) =>
        Engine_ShaderCeilingSource.Preprocess(
            Engine_ShaderVariant.WithExtraDefines(
                Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(litePath)), litePath, false, true, true), extra),
            litePath,
            Include);

    /// <summary>The code lines (comments and declarations aside) that name <paramref name="word"/>.</summary>
    private static string[] Reads(string code, string word) => code.Split('\n')
        .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Contains("global uniform", StringComparison.Ordinal))
        .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\b" + System.Text.RegularExpressions.Regex.Escape(word) + @"\b"))
        .ToArray();

    /// <summary>The code lines (comments and declarations aside) that contain <paramref name="text"/>.</summary>
    private static string[] Lines(string code, string text) => code.Split('\n')
        .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Contains("global uniform", StringComparison.Ordinal))
        .Where(l => l.Contains(text, StringComparison.Ordinal))
        .ToArray();

    private static readonly string[] Paths = { PropFable, PropModern, TerrainFable, TerrainModern };

    [TestCase]
    public void TheKeysAreRegisteredWithPlayersNames()
    {
        RuntimeCVarDefinition? fog = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == AndroidLeanShading.FogKey);
        AssertThat(fog).IsNotNull();
        AssertThat(fog!.DefaultValue).IsEqual("lean");
        RuntimeCVarDefinition? dark = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == AndroidLeanShading.DarkeningKey);
        AssertThat(dark).IsNotNull();
        AssertThat(dark!.DefaultValue).IsEqual("true");
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidLeanShading.FogKey)).IsTrue();
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidLeanShading.DarkeningKey)).IsTrue();
        AssertThat(AndroidLeanShading.FogLeanDefine).IsEqual(Lean);
        AssertThat(AndroidLeanShading.FogTableDefine).IsEqual(Table);
        AssertThat(AndroidLeanShading.DarkeningDefine).IsEqual(Dark);
    }

    [TestCase]
    public void TheDefinesFollowTheKeys()
    {
        var none = new Dictionary<string, string>();
        AssertThat(AndroidLeanShading.FogWanted(none)).IsEqual(AndroidLeanShading.FogPath.Lean);
        AssertThat(AndroidLeanShading.DarkeningWanted(none)).IsTrue();
        AssertThat(AndroidLeanShading.FogWanted(new Dictionary<string, string> { [AndroidLeanShading.FogKey] = "\"table\"" }))
            .IsEqual(AndroidLeanShading.FogPath.Table);
        AssertThat(AndroidLeanShading.ParseFog("full")).IsEqual(AndroidLeanShading.FogPath.Full);
        AssertThat(AndroidLeanShading.ParseFog("something")).IsEqual(AndroidLeanShading.FogPath.Full);
        AssertThat(AndroidLeanShading.DarkeningWanted(new Dictionary<string, string> { [AndroidLeanShading.DarkeningKey] = "false" })).IsFalse();
        AssertThat(AndroidLeanShading.DefinesFor(AndroidLeanShading.FogPath.Full, false)).IsEmpty();
        AssertThat(AndroidLeanShading.DefinesFor(AndroidLeanShading.FogPath.Lean, false)).IsEqual(new[] { Lean });
        AssertThat(AndroidLeanShading.DefinesFor(AndroidLeanShading.FogPath.Table, true)).IsEqual(new[] { Lean, Table, Dark });
        AssertThat(AndroidLeanShading.DefinesFor(AndroidLeanShading.FogPath.Full, true)).IsEqual(new[] { Dark });
    }

    /// <summary>Without the defines no lite build sees the include; the full shaders never do.</summary>
    /// <summary>The table is the modern model's alone: under the fable model (where it cost an S10e 8%)
    /// a process asking for it composes exactly as with lite_fog unset.</summary>
    [TestCase]
    public void TheTableAppliesUnderTheModernModelOnly()
    {
        var none = new Dictionary<string, string>();
        var table = new Dictionary<string, string> { [AndroidLeanShading.FogKey] = "table" };
        AndroidLeanShading.FogPath unset = AndroidLeanShading.FogWanted(none);
        AssertThat(AndroidLeanShading.FogDefault).IsEqual(RuntimeCVarsAndroid.Tuning.Single(d => d.Name == AndroidLeanShading.FogKey).DefaultValue);

        AssertThat(AndroidLeanShading.FogFor(AndroidLeanShading.FogWanted(table), true)).IsEqual(AndroidLeanShading.FogPath.Table);
        AssertThat(AndroidLeanShading.FogFor(AndroidLeanShading.FogWanted(table), false)).IsEqual(unset);
        foreach (bool darkening in new[] { false, true })
        {
            AssertThat(AndroidLeanShading.DefinesFor(AndroidLeanShading.FogFor(AndroidLeanShading.FogWanted(table), false), darkening))
                .IsEqual(AndroidLeanShading.DefinesFor(AndroidLeanShading.FogFor(unset, false), darkening));
            AssertThat(AndroidLeanShading.DefinesFor(AndroidLeanShading.FogFor(AndroidLeanShading.FogWanted(table), true), darkening))
                .Contains(Table);
        }

        // Full and lean are the same sums under either model, and go through untouched.
        foreach (AndroidLeanShading.FogPath path in new[] { AndroidLeanShading.FogPath.Full, AndroidLeanShading.FogPath.Lean })
        {
            AssertThat(AndroidLeanShading.FogFor(path, false)).IsEqual(path);
            AssertThat(AndroidLeanShading.FogFor(path, true)).IsEqual(path);
        }

        // The setting says so, to the launcher and to a player.
        AssertThat(RuntimeCVarsAndroid.Tuning.Single(d => d.Name == AndroidLeanShading.FogKey).Description)
            .Contains("Applies under the modern lighting model only");
        bool offered = GraphicsPresets.Offered;
        try
        {
            GraphicsPresets.Offered = true;
            AssertThat(AndroidSettingNames.About(AndroidLeanShading.FogKey)).Contains("under the modern lighting only");
        }
        finally
        {
            GraphicsPresets.Offered = offered;
        }

        // The process's choice reads the model beside the key.
        string source = File("res://src/android/AndroidLeanShading.cs");
        AssertThat(source).Contains("FogFor(FogWanted(values), Refii.Lighting.Engine_LightingFrame.ModernModel)");
    }

    [TestCase]
    public void WithoutTheDefinesNothingChanges()
    {
        foreach (string path in Paths)
        {
            string plain = Lite(path);
            AssertThat(Reads(plain, "fable_lean_darken")).OverrideFailureMessage(path).IsEmpty();
            AssertThat(Reads(plain, "atmos_extinction")).OverrideFailureMessage(path).IsEmpty();
            AssertThat(Lines(plain, "textureSize(atmos_inscatter")).OverrideFailureMessage(path).IsNotEmpty();
        }

        foreach (string full in new[] { Engine_ShaderFamily.StandardCulledPath, Engine_ShaderVariant.TerrainFullPath })
        {
            string code = Engine_ShaderCeilingSource.Preprocess(File(full), full, Include);
            AssertThat(code.Contains("fable_lean_darken", StringComparison.Ordinal)).OverrideFailureMessage(full).IsFalse();
        }
    }

    /// <summary>Lean: one fog function, no size query, no normalised sun, no fog factors; the curve stays.</summary>
    [TestCase]
    public void TheLeanFogKeepsTheCurve()
    {
        foreach (string path in Paths)
        {
            string lean = Lite(path, Lean);
            AssertThat(System.Text.RegularExpressions.Regex.Matches(lean, @"\bbool fable_atmosphere_terms_for\(").Count)
                .OverrideFailureMessage(path).IsEqual(1);
            AssertThat(Lines(lean, "textureSize(atmos_inscatter")).OverrideFailureMessage(path).IsEmpty();
            AssertThat(Lines(lean, "normalize(atmos_sun)")).OverrideFailureMessage(path).IsEmpty();
            AssertThat(Reads(lean, "atmos_factors")).OverrideFailureMessage(path).IsEmpty();
            AssertThat(Reads(lean, "atmos_curve")).OverrideFailureMessage(path).IsNotEmpty();
            AssertThat(Lines(Lite(path), "normalize(atmos_sun)")).OverrideFailureMessage(path).IsNotEmpty();
        }
    }

    /// <summary>Table: the transmittance is fetched, and nothing of the curve is evaluated per pixel.</summary>
    [TestCase]
    public void TheTableFogReadsNoCurve()
    {
        foreach (string path in Paths)
        {
            string table = Lite(path, Lean, Table);
            AssertThat(Reads(table, "atmos_extinction")).OverrideFailureMessage(path).IsNotEmpty();
            foreach (string gone in new[] { "atmos_curve", "atmos_beta", "atmos_near_ready", "fable_volumetric_fog_end" })
            {
                AssertThat(Reads(table, gone)).OverrideFailureMessage(path + " " + gone).IsEmpty();
            }
        }
    }

    /// <summary>The lean darkening replaces the bent normal in every lite model build, fable and modern.</summary>
    [TestCase]
    public void TheLeanDarkeningReplacesTheBentNormal()
    {
        foreach (string path in new[] { PropFable, PropModern })
        {
            AssertThat(Reads(Lite(path), "amb_ref")).OverrideFailureMessage(path).IsNotEmpty();
            string dark = Lite(path, Dark);
            AssertThat(Reads(dark, "amb_ref")).OverrideFailureMessage(path).IsEmpty();
            AssertThat(Reads(dark, "fable_lean_darken")).OverrideFailureMessage(path).IsNotEmpty();
        }
    }

    /// <summary>The shader's constants are the C#'s, and the lean fold's premises hold.</summary>
    [TestCase]
    public void TheConstantsAgree()
    {
        string include = File(IncludePath);
        AssertThat(include).Contains($"const float FABLE_FOG_TABLE_PER_KNOT = {AndroidLeanShading.PerKnot}.0;");
        AssertThat(include).Contains($"const float FABLE_FOG_TABLE_TEXELS = {AndroidLeanShading.TableTexels}.0;");
        AssertThat(include).Contains($"const float half_texel = 0.5 / {Refii.Level.Engine_SkyAtmosphere.LutWidth}.0;");
        AssertThat(AndroidLeanShading.TableTexels).IsEqual((int)(Refii.Level.Engine_SkyAtmosphere.MaxFogDistance / AndroidLeanShading.Knot * AndroidLeanShading.PerKnot) + 1);
        AssertThat(AndroidLeanShading.Knot).IsEqual(Engine_NearFogKnots.Spacing);
        AssertThat(Refii.Level.Engine_SkyAtmosphere.FogBlendFactor).IsEqual(1.0f);
        AssertThat(Refii.Level.Engine_SkyAtmosphere.FogColourFactor).IsEqual(1.0f);
    }

    [TestCase]
    public void TheTableGlobalIsDeclared()
    {
        string project = File("res://project.godot");
        int globals = project.IndexOf("[shader_globals]", StringComparison.Ordinal);
        AssertThat(globals).IsGreater(-1);
        AssertThat(project.IndexOf("\n" + AndroidLeanShading.TableGlobal + "={\n\"type\": \"sampler2D\"", globals, StringComparison.Ordinal))
            .IsGreater(globals);
    }

    // ---- the table against the lite shader's own transmittance ----

    private static AndroidLeanShading.FogInputs Inputs(float start, float depth, float far, float k, Vector3 beta, float scale, float vol)
    {
        NearFogSnapshot near = Engine_NearFogKnots.Create(true, beta.X, beta.Y, beta.Z, depth, 1f / far, k, 2000f);
        Vector3 Knot(int i) => new(near.At(i).Red, near.At(i).Green, near.At(i).Blue);
        return new(true, beta, depth, 1f / far, k, 2000f, start, near.Ready, Knot(0), Knot(1), Knot(2), Knot(3), Knot(4), scale, vol);
    }

    /// <summary>fable_atmosphere's lite transmittance at distance d past FoggingStart, in double.</summary>
    private static double Reference(in AndroidLeanShading.FogInputs fog, double d, int channel)
    {
        double beta = channel == 0 ? fog.Beta.X : channel == 1 ? fog.Beta.Y : fog.Beta.Z;
        double knot = AndroidLeanShading.Knot;
        double dl = Math.Max(d - (0.5 * knot), 0.0);
        double raw;
        if (dl < knot * 4 && fog.NearReady)
        {
            int i = (int)Math.Floor(dl / knot);
            double fr = (dl - (i * knot)) / knot;
            float a = channel == 0 ? fog.KnotAt(i).X : channel == 1 ? fog.KnotAt(i).Y : fog.KnotAt(i).Z;
            float b = channel == 0 ? fog.KnotAt(i + 1).X : channel == 1 ? fog.KnotAt(i + 1).Y : fog.KnotAt(i + 1).Z;
            raw = a + ((b - a) * fr);
            if (Math.Abs(fog.FogScale - 1.0) > 1e-4)
            {
                raw = Math.Pow(Math.Max(raw, 1e-6), fog.FogScale);
            }
        }
        else
        {
            double t = Math.Min(dl, fog.MaxDistance) * fog.RecipFar;
            raw = Math.Exp(-beta * fog.DepthAtFar * Math.Pow(Math.Max(t, 0.0), fog.Exponent) * fog.FogScale);
        }

        if (fog.VolumetricDensity > 0)
        {
            raw = Math.Min(raw * Math.Exp(fog.VolumetricDensity * Math.Clamp(d + fog.Start, 0.0, AndroidLeanShading.VolumetricRange)), 1.0);
        }

        return raw;
    }

    /// <summary>The shader's fetch: the coordinate fable_lite_lean computes, a linear filter, the clamp to 1.</summary>
    private static double Fetch(float[] table, double d, int channel)
    {
        int n = AndroidLeanShading.TableTexels;
        double texel = (d * (AndroidLeanShading.PerKnot / AndroidLeanShading.Knot)) + (0.5 - (0.5 * AndroidLeanShading.PerKnot));
        double x = Math.Clamp(texel, 0.5, n - 0.5) - 0.5;
        int i = Math.Min((int)Math.Floor(x), n - 2);
        double fr = x - i;
        double v = (table[(i * 4) + channel] * (1 - fr)) + (table[((i + 1) * 4) + channel] * fr);
        return Math.Min(v, 1.0);
    }

    private static double WorstError(in AndroidLeanShading.FogInputs fog)
    {
        float[] table = AndroidLeanShading.FogTable(fog);
        double worst = 0;
        for (double d = 0.01; d < 2100; d += d < 60 ? 0.0137 : 0.311)
        {
            for (int c = 0; c < 3; c++)
            {
                worst = Math.Max(worst, Math.Abs(Fetch(table, d, c) - Reference(fog, d, c)));
            }
        }

        return worst * 255.0;
    }

    /// <summary>Two of the shipped outdoor themes the phones publish, with and without the volumetric cancel: within 0.25/255.</summary>
    [TestCase]
    public void TheTableMatchesTheCurveOutdoors()
    {
        foreach (AndroidLeanShading.FogInputs fog in new[]
        {
            Inputs(18.8f, 553.66f, 1981.2f, 1.136f, new Vector3(0.00519f, 0.00594f, 0.01021f), 1f, 0f),
            Inputs(21.0f, 478.59f, 1979.0f, 0.67f, new Vector3(0.00657f, 0.00752f, 0.01298f), 1f, 0f),
            Inputs(21.0f, 478.59f, 1979.0f, 0.67f, new Vector3(0.00657f, 0.00752f, 0.01298f), 1f, 0.002f),
            Inputs(3.3f, 7983.24f, 1996.7f, 1.35f, new Vector3(0.00216f, 0.00249f, 0.00453f), 1f, 0.01f),
        })
        {
            AssertThat(WorstError(fog)).IsLess(0.25);
        }
    }

    /// <summary>A dense cave at four times its fog: within 1.5/255.</summary>
    [TestCase]
    public void TheTableMatchesTheCurveInADenseCave()
    {
        // SolveFogCurve for start 0, near 2 @ 2.0, far 40 @ 4.0.
        AssertThat(WorstError(Inputs(0f, 156f, 40f, 1.2229f, new Vector3(0.00656f, 0.00751f, 0.01297f), 4f, 0.01f))).IsLess(1.5);
    }

    /// <summary>Exact where the shader is linear: every knot texel holds its knot.</summary>
    [TestCase]
    public void TheKnotTexelsAreTheKnots()
    {
        AndroidLeanShading.FogInputs fog = Inputs(18.8f, 553.66f, 1981.2f, 1.136f, new Vector3(0.00519f, 0.00594f, 0.01021f), 1f, 0f);
        float[] table = AndroidLeanShading.FogTable(fog);
        for (int i = 0; i <= 4; i++)
        {
            int j = i * AndroidLeanShading.PerKnot;
            AssertThat(table[j * 4]).IsEqual(fog.KnotAt(i).X);
            AssertThat(table[(j * 4) + 2]).IsEqual(fog.KnotAt(i).Z);
            AssertThat(table[(j * 4) + 3]).IsEqual(1f);
        }
    }

    /// <summary>The two engine hooks are one line each.</summary>
    [TestCase]
    public void TheHooksAreInPlace()
    {
        AssertThat(File("res://src/Engine/level/environment/Engine_SkyEnvironment.cs"))
            .Contains("Refii.AndroidRuntime.AndroidLeanShading.Atmosphere(hasFog, beta, curve, range, nearFog);");
        AssertThat(File("res://src/Engine/Lighting/Engine_LevelLighting.cs"))
            .Contains("Refii.AndroidRuntime.AndroidLeanShading.LevelFog(effectiveFogScale, Refii.AndroidRuntime.AndroidNearFog.Published(volBaseDensity));");
    }
}
