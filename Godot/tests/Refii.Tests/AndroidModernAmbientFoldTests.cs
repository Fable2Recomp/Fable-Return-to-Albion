using System;
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
/// `android_modern_ambient_fold` (<see cref="AndroidModernAmbientFold"/>): the modern lite builds,
/// and only they, get `ambient_light_disabled` with the define that adds Godot's ambient back in
/// the body, from a declared global carrying what the renderer would read.
/// </summary>
[TestSuite]
public sealed class AndroidModernAmbientFoldTests
{
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

    private static string Build(string lite, bool fold) =>
        Engine_ShaderVariant.Compose(File(Engine_ShaderVariant.FullPathOf(lite)), lite, false, false, false, fold);

    private static string RenderMode(string code) =>
        code.Split('\n').Single(l => l.TrimStart().StartsWith("render_mode", StringComparison.Ordinal));

    /// <summary>With the key on, every lite/modern/ build gets the word and the define; every lite/ build is untouched.</summary>
    [TestCase]
    public void OnlyTheModernBuildsFold()
    {
        int modern = 0;
        foreach (string lite in Engine_ShaderVariant.AllVariantPaths)
        {
            string plain = Build(lite, false);
            string folded = Build(lite, true);
            bool isModern = lite.Contains("/lite/modern/", StringComparison.Ordinal);
            AssertThat(Engine_ShaderVariant.FoldsAmbient(lite)).IsEqual(isModern);
            if (!isModern)
            {
                AssertThat(folded).OverrideFailureMessage(lite + " changed").IsEqual(plain);
                continue;
            }

            modern++;
            AssertThat(Engine_ShaderVariant.Defines(folded, AndroidModernAmbientFold.Define)).IsTrue();
            AssertThat(RenderMode(folded).EndsWith(", " + AndroidModernAmbientFold.RenderModeWord + ";", StringComparison.Ordinal)).IsTrue();
            // Nothing else: the word and the define line are all that differ.
            string back = folded
                .Replace(", " + AndroidModernAmbientFold.RenderModeWord + ";", ";", StringComparison.Ordinal)
                .Replace("#define " + AndroidModernAmbientFold.Define + "\n", string.Empty, StringComparison.Ordinal);
            AssertThat(back).IsEqual(plain);
        }

        AssertThat(modern).IsGreater(0);
    }

    /// <summary>With the key off (the default, and every desktop), no build names the word or the define.</summary>
    [TestCase]
    public void OffComposesNothing()
    {
        foreach (string lite in Engine_ShaderVariant.AllVariantPaths)
        {
            string plain = Build(lite, false);
            AssertThat(plain.Contains(AndroidModernAmbientFold.RenderModeWord, StringComparison.Ordinal)).IsFalse();
            AssertThat(Engine_ShaderVariant.Defines(plain, AndroidModernAmbientFold.Define)).IsFalse();
        }

        AssertThat(AndroidModernAmbientFold.On).IsFalse();
    }

    /// <summary>Shadowless on top of the fold extends the same render mode once more.</summary>
    [TestCase]
    public void ShadowlessStacksOnTheFold()
    {
        string lite = "res://src/Engine/material/lite/modern/standard_material_lite.gdshader";
        string built = Engine_ShaderSpecialisation.Compose(Build(lite, true), Array.Empty<string>(), true);
        AssertThat(RenderMode(built).EndsWith(", ambient_light_disabled, shadows_disabled;", StringComparison.Ordinal)).IsTrue();
        AssertThat(Engine_ShaderSpecialisation.WithRenderMode(built, "ambient_light_disabled")).IsEqual(built);
        AssertThat(Engine_ShaderSpecialisation.WithRenderMode("shader_type spatial;\nvoid fragment() {}", "ambient_light_disabled"))
            .IsEqual("shader_type spatial;\nrender_mode ambient_light_disabled;\nvoid fragment() {}");
    }

    /// <summary>
    /// The global is declared in the shader and in project.godot's [shader_globals], and a folded
    /// build, preprocessed, declares it and adds the fold at the end of both bodies' fragment().
    /// </summary>
    [TestCase]
    public void TheGlobalIsDeclared()
    {
        // Declared by the phone's include, which fable_lighting hooks in a lite build; the shared file never names it.
        string lighting = File("res://src/Engine/material/fable_lighting.gdshaderinc");
        AssertThat(lighting.Contains("#ifdef FABLE_LITE\n#include \"res://src/android/fable_lite_ambient_fold.gdshaderinc\"\n#endif", StringComparison.Ordinal)).IsTrue();
        AssertThat(lighting.Contains(AndroidModernAmbientFold.GlobalName, StringComparison.Ordinal)).IsFalse();
        AssertThat(File("res://src/android/fable_lite_ambient_fold.gdshaderinc")
            .Contains("global uniform vec3 " + AndroidModernAmbientFold.GlobalName, StringComparison.Ordinal)).IsTrue();

        string project = File("res://project.godot");
        int globals = project.IndexOf("[shader_globals]", StringComparison.Ordinal);
        AssertThat(globals).IsGreater(-1);
        int entry = project.IndexOf("\n" + AndroidModernAmbientFold.GlobalName + "={\n\"type\": \"vec3\"", globals, StringComparison.Ordinal);
        AssertThat(entry).IsGreater(globals);
        int nextSection = project.IndexOf("\n[", globals + 1, StringComparison.Ordinal);
        AssertThat(nextSection < 0 || entry < nextSection).IsTrue();

        foreach (string lite in new[]
        {
            "res://src/Engine/material/lite/modern/standard_material_lite.gdshader",
            "res://src/Engine/material/lite/modern/standard_material_translucent_lite_nospec.gdshader",
            "res://src/Engine/level/terrain/lite/modern/terrain_splat_lite.gdshader",
            "res://src/Engine/level/terrain/lite/modern/terrain_splat_lite_page.gdshader",
        })
        {
            string folded = Engine_ShaderCeilingSource.Preprocess(Build(lite, true), lite, Include);
            AssertThat(folded.Contains("global uniform vec3 " + AndroidModernAmbientFold.GlobalName, StringComparison.Ordinal))
                .OverrideFailureMessage(lite + " does not declare the global").IsTrue();
            AssertThat(folded.Contains("EMISSION += fable_godot_ambient_fold(ALBEDO, AO, METALLIC);", StringComparison.Ordinal))
                .OverrideFailureMessage(lite + " does not fold").IsTrue();

            string plain = Engine_ShaderCeilingSource.Preprocess(Build(lite, false), lite, Include);
            AssertThat(plain.Contains(AndroidModernAmbientFold.GlobalName, StringComparison.Ordinal)).IsFalse();
        }
    }

    /// <summary>The renderer's own ambient: srgb_to_linear(colour) * energy for a Color source, zero otherwise.</summary>
    [TestCase]
    public void TheGlobalIsTheRenderersAmbient()
    {
        var colour = new Color(0.5f, 0.25f, 1.0f);
        Vector3 ambient = AndroidModernAmbientFold.Ambient(Godot.Environment.AmbientSource.Color, colour, 0.8f);
        Color linear = colour.SrgbToLinear();
        AssertThat(ambient.X).IsEqualApprox(linear.R * 0.8f, 1e-6f);
        AssertThat(ambient.Y).IsEqualApprox(linear.G * 0.8f, 1e-6f);
        AssertThat(ambient.Z).IsEqualApprox(linear.B * 0.8f, 1e-6f);
        // A colour handed in through Engine_LightColour comes back linear.
        Vector3 round = AndroidModernAmbientFold.Ambient(Godot.Environment.AmbientSource.Color,
            Refii.Lighting.Engine_LightColour.ForGodot(new Color(0.2f, 0.4f, 0.05f)), 1f);
        AssertThat(round.X).IsEqualApprox(0.2f, 1e-4f);
        AssertThat(round.Y).IsEqualApprox(0.4f, 1e-4f);
        AssertThat(round.Z).IsEqualApprox(0.05f, 1e-4f);
        AssertThat(AndroidModernAmbientFold.Ambient(Godot.Environment.AmbientSource.Disabled, colour, 0.8f)).IsEqual(Vector3.Zero);
        AssertThat(AndroidModernAmbientFold.Ambient(Godot.Environment.AmbientSource.Sky, colour, 0.8f)).IsEqual(Vector3.Zero);
    }

    /// <summary>The key is a phone Tuning row, off by default, with a player's name.</summary>
    [TestCase]
    public void TheKeyIsRegisteredOff()
    {
        Refii.Launcher.Core.RuntimeCVarDefinition? row = Refii.Launcher.Core.RuntimeCVarsAndroid.Tuning
            .SingleOrDefault(d => d.Name == AndroidModernAmbientFold.Key);
        AssertThat(row).IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("false");
        AssertThat(Refii.Launcher.Core.AndroidSettingNames.Keys.Contains(AndroidModernAmbientFold.Key)).IsTrue();
    }
}
