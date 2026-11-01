using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GdUnit4;
using Refii.Fx;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The sky's GPU shortcuts (Engine_SkyCosts): the glares' occlusion once per corner.</summary>
[TestSuite]
public sealed class SkyCostsTests
{
    private static string Source(string resPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    private const string Overlay = "res://src/Engine/level/environment/celestial/celestial_overlay.gdshader";

    /// <summary>The body of the stage function <paramref name="name"/>, up to its closing brace at column 0.</summary>
    private static List<string> Stage(List<string> lines, string name)
    {
        int at = lines.FindIndex(l => l.StartsWith("void " + name + "()", StringComparison.Ordinal));
        AssertThat(at >= 0).IsTrue();
        int end = lines.FindIndex(at, l => l == "}");
        return lines.GetRange(at + 1, end - at - 1);
    }

    private static int Calls(IEnumerable<string> lines) =>
        lines.Count(l => l.Contains("geometry_visibility(VIEW_MATRIX, PROJECTION_MATRIX)", StringComparison.Ordinal));

    /// <summary>
    /// Cheap, the vertex stage takes the occlusion once and the fragment stage only reads the flat
    /// varying; as shipped, the fragment stage takes it per pixel as before.
    /// </summary>
    [TestCase]
    public void TheOcclusionMovesToTheVertexStage()
    {
        string code = Source(Overlay);
        string built = Engine_FxDepthReads.Compose(code, Engine_SkyCosts.VertexOcclusionDefine);
        List<string> cheap = FxDepthReadsTests.Preprocess(built, new HashSet<string> { Engine_SkyCosts.VertexOcclusionDefine });
        List<string> shipped = FxDepthReadsTests.Preprocess(code, new HashSet<string>());

        AssertThat(Calls(Stage(cheap, "vertex"))).IsEqual(1);
        AssertThat(Calls(Stage(cheap, "fragment"))).IsEqual(0);
        AssertThat(Stage(cheap, "fragment").Any(l => l.Trim() == "float geom = v_geom;")).IsTrue();
        AssertThat(cheap.Contains("varying flat float v_geom;")).IsTrue();

        AssertThat(Calls(Stage(shipped, "vertex"))).IsEqual(0);
        AssertThat(Calls(Stage(shipped, "fragment"))).IsEqual(1);
        AssertThat(shipped.Any(l => l.Contains("v_geom", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// Everything else is the same text: the two preprocessed builds differ only in the varying,
    /// the tap's explicit level and the line that takes the occlusion.
    /// </summary>
    [TestCase]
    public void NothingElseDiffers()
    {
        string code = Source(Overlay);
        string built = Engine_FxDepthReads.Compose(code, Engine_SkyCosts.VertexOcclusionDefine);
        List<string> cheap = FxDepthReadsTests.Preprocess(built, new HashSet<string> { Engine_SkyCosts.VertexOcclusionDefine })
            .Where(l => !l.StartsWith("#define", StringComparison.Ordinal)).ToList();
        List<string> shipped = FxDepthReadsTests.Preprocess(code, new HashSet<string>())
            .Where(l => !l.StartsWith("#define", StringComparison.Ordinal)).ToList();

        string[] onlyCheap = cheap.Except(shipped).Select(l => l.Trim()).ToArray();
        string[] onlyShipped = shipped.Except(cheap).Select(l => l.Trim()).ToArray();
        AssertThat(onlyCheap.OrderBy(l => l, StringComparer.Ordinal).ToArray()).IsEqual(new[]
        {
            "float geom = v_geom;",
            "v_geom = geometry_visibility(VIEW_MATRIX, PROJECTION_MATRIX);",
            "varying flat float v_geom;",
        });
        AssertThat(onlyShipped).IsEqual(new[] { "float geom = geometry_visibility(VIEW_MATRIX, PROJECTION_MATRIX);" });

        // The taps: level 0 of a one-level texture read nearest, against the fragment stage's read.
        string[] taps = built.Split('\n').Where(l => l.StartsWith("#define OVERLAY_DEPTH_TAP", StringComparison.Ordinal)).ToArray();
        AssertThat(taps).IsEqual(new[]
        {
            "#define OVERLAY_DEPTH_TAP(uv) textureLod(depth_tex, (uv), 0.0).r",
            "#define OVERLAY_DEPTH_TAP(uv) texture(depth_tex, (uv)).r",
        });
        AssertThat(code.Contains("uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>The define goes in after the shader_type line, ahead of everything it guards.</summary>
    [TestCase]
    public void TheDefineLeadsTheShader()
    {
        string built = Engine_FxDepthReads.Compose(Source(Overlay), Engine_SkyCosts.VertexOcclusionDefine);
        string[] lines = built.Split('\n');
        AssertThat(lines[0]).IsEqual("shader_type spatial;");
        AssertThat(lines[1]).IsEqual("#define " + Engine_SkyCosts.VertexOcclusionDefine);
    }

    /// <summary>The debug lever takes a boolean or nothing.</summary>
    [TestCase]
    public void TheLeverReadsABoolean()
    {
        using JsonDocument on = JsonDocument.Parse("{\"skyGlareCheap\": true}");
        using JsonDocument bad = JsonDocument.Parse("{\"skyGlareCheap\": null}");
        AssertThat(Engine_SkyCosts.ReadRequest(on.RootElement, Engine_SkyCosts.GlareCheapRequestKey)).IsEqual(true);
        AssertThat(Engine_SkyCosts.ReadRequest(on.RootElement, "other") is null).IsTrue();
        AssertThrown(() => Engine_SkyCosts.ReadRequest(bad.RootElement, Engine_SkyCosts.GlareCheapRequestKey))
            .IsInstanceOf<ArgumentException>();
    }

    private const Godot.Environment.ReflectionSource NoReflection = Godot.Environment.ReflectionSource.Disabled;
    private const Godot.Environment.AmbientSource NoAmbient = Godot.Environment.AmbientSource.Disabled;
    private const Godot.Environment.BGMode SkyBackground = Godot.Environment.BGMode.Sky;

    /// <summary>
    /// The fable model on a phone: ambient and reflections off, no SDFGI, volumetric fog with no
    /// ambient energy to inject: nothing reads the map. The modern model's colour ambient does not
    /// either, but its SDFGI does.
    /// </summary>
    [TestCase]
    public void TheFableModelReadsNoRadiance()
    {
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, NoAmbient, SkyBackground, false, false, 0f)).IsTrue();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, NoAmbient, SkyBackground, false, true, 0f)).IsTrue();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, Godot.Environment.AmbientSource.Color, SkyBackground, false, false, 0f)).IsTrue();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, Godot.Environment.AmbientSource.Color, SkyBackground, true, false, 0f)).IsFalse();
    }

    /// <summary>Every way Godot samples the map keeps it.</summary>
    [TestCase]
    public void AnyReaderKeepsTheMap()
    {
        AssertThat(Engine_SkyCosts.RadianceUnread(Godot.Environment.ReflectionSource.Sky, NoAmbient, SkyBackground, false, false, 0f)).IsFalse();
        AssertThat(Engine_SkyCosts.RadianceUnread(Godot.Environment.ReflectionSource.Bg, NoAmbient, SkyBackground, false, false, 0f)).IsFalse();
        AssertThat(Engine_SkyCosts.RadianceUnread(Godot.Environment.ReflectionSource.Bg, NoAmbient, Godot.Environment.BGMode.Color, false, false, 0f)).IsTrue();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, Godot.Environment.AmbientSource.Sky, SkyBackground, false, false, 0f)).IsFalse();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, Godot.Environment.AmbientSource.Bg, SkyBackground, false, false, 0f)).IsFalse();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, NoAmbient, SkyBackground, true, false, 0f)).IsFalse();
        AssertThat(Engine_SkyCosts.RadianceUnread(NoReflection, NoAmbient, SkyBackground, false, true, 0.5f)).IsFalse();
    }

    /// <summary>Lean only while asked for and unread; otherwise the sky's own size comes back.</summary>
    [TestCase]
    public void TheSizeIsLeanOnlyWhileAskedAndUnread()
    {
        var own = Godot.Sky.RadianceSizeEnum.Size256;
        AssertThat(Engine_SkyCosts.RadianceSizeFor(true, true, own)).IsEqual(Godot.Sky.RadianceSizeEnum.Size32);
        AssertThat(Engine_SkyCosts.RadianceSizeFor(true, false, own)).IsEqual(own);
        AssertThat(Engine_SkyCosts.RadianceSizeFor(false, true, own)).IsEqual(own);
        AssertThat(Engine_SkyCosts.LeanRadianceSize).IsEqual(Godot.Sky.RadianceSizeEnum.Size32);
    }
}
