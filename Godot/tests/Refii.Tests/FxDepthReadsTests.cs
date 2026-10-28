using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using GdUnit4;
using Refii.Fx;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `fx_no_depth_reads`: the hard particle builds have no depth uniform and the same fade at zero
/// soft distance, and a glare is hidden only where its vertex stage leaves it no pixel.
/// </summary>
[TestSuite]
public sealed class FxDepthReadsTests
{
    private static string Source(string resPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    private const string SoftInclude = "res://src/Engine/fx/fx_soft.gdshaderinc";

    /// <summary>The mode each effect material is built in, and its shader (FableFx.LookupMaterial).</summary>
    private static readonly (string Mode, string Path)[] Modes =
    {
        ("mix", FableFx.MixShaderPath),
        ("add", FableFx.AddShaderPath),
        ("addone", FableFx.AddShaderPath),
        ("addlayer", FableFx.AddLayerShaderPath),
        ("mixalpha", FableFx.MixAlphaShaderPath),
        ("darken", FableFx.DarkenShaderPath),
        ("sub", FableFx.SubShaderPath),
        ("disp", FableFx.DispShaderPath),
    };

    /// <summary>
    /// The lines Godot's preprocessor keeps with <paramref name="defined"/> set: enough of
    /// #ifdef / #ifndef / #else / #endif for these files.
    /// </summary>
    internal static List<string> Preprocess(string code, ISet<string> defined)
    {
        var kept = new List<string>();
        var stack = new Stack<bool>();
        bool live = true;
        foreach (string raw in code.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("#ifdef ", StringComparison.Ordinal) || line.StartsWith("#ifndef ", StringComparison.Ordinal))
            {
                bool want = line.StartsWith("#ifdef ", StringComparison.Ordinal);
                stack.Push(live);
                live = live && defined.Contains(line.Split(' ')[1]) == want;
            }
            else if (line == "#else")
            {
                bool outer = stack.Peek();
                live = outer && !live;
            }
            else if (line == "#endif")
            {
                live = stack.Pop();
            }
            else if (live)
            {
                kept.Add(raw);
            }
        }

        AssertThat(stack.Count).IsEqual(0);
        return kept;
    }

    /// <summary>The define goes before the first #include, so every included body sees it.</summary>
    [TestCase]
    public void TheDefinePrecedesTheFirstInclude()
    {
        const string code = "shader_type spatial;\r\nrender_mode unshaded;\r\n\r\n#include \"a\"\r\nuniform float x;\r\n#include \"b\"\r\n";
        string built = Engine_FxDepthReads.Compose(code, "FX_HARD");
        AssertThat(built).IsEqual("shader_type spatial;\nrender_mode unshaded;\n\n#define FX_HARD\n#include \"a\"\nuniform float x;\n#include \"b\"\n");
    }

    /// <summary>Without an include the define follows the shader_type line.</summary>
    [TestCase]
    public void WithoutAnIncludeTheDefineFollowsTheShaderType()
    {
        AssertThat(Engine_FxDepthReads.Compose("shader_type spatial;\nvoid fragment() {}\n", "X"))
            .IsEqual("shader_type spatial;\n#define X\nvoid fragment() {}\n");
        AssertThat(Engine_FxDepthReads.Compose("shader_type spatial;", "X")).IsEqual("shader_type spatial;\n#define X\n");
    }

    /// <summary>
    /// Every mode whose shader includes the soft fade is hard-capable, the define reaches that
    /// include, and the two others (the additive layer's, displacement) never declare the uniform.
    /// </summary>
    [TestCase]
    public void EverySoftShaderTakesTheHardBuild()
    {
        foreach ((string mode, string path) in Modes)
        {
            string code = Source(path);
            bool soft = code.Contains(SoftInclude, StringComparison.Ordinal);
            AssertThat(Engine_FxDepthReads.SoftMode(mode)).IsEqual(soft);
            if (soft)
            {
                string built = Engine_FxDepthReads.Compose(code, Engine_FxDepthReads.HardDefine);
                int define = built.IndexOf("#define " + Engine_FxDepthReads.HardDefine, StringComparison.Ordinal);
                AssertThat(define >= 0 && define < built.IndexOf(SoftInclude, StringComparison.Ordinal)).IsTrue();
            }
            else
            {
                AssertThat(code.Contains("hint_depth_texture", StringComparison.Ordinal)).IsFalse();
            }
        }
    }

    /// <summary>
    /// Hard, fx_soft declares no depth texture and its fade is a constant 1; soft, the uniform
    /// stays and a zero `inv` still returns 1 before anything is read, which is the hard answer.
    /// </summary>
    [TestCase]
    public void TheHardFadeIsTheZeroDistanceFade()
    {
        string code = Source(SoftInclude);
        List<string> hard = Preprocess(code, new HashSet<string> { Engine_FxDepthReads.HardDefine });
        List<string> soft = Preprocess(code, new HashSet<string>());
        AssertThat(hard.Any(l => l.Contains("hint_depth_texture", StringComparison.Ordinal))).IsFalse();
        AssertThat(hard.Any(l => l.Contains("fx_depth_tex", StringComparison.Ordinal))).IsFalse();
        AssertThat(soft.Count(l => l.Contains("hint_depth_texture", StringComparison.Ordinal))).IsEqual(1);
        AssertThat(hard.Any(l => l.Contains("uniform float fx_soft_inv", StringComparison.Ordinal))).IsTrue();

        static string FirstStatement(List<string> lines)
        {
            int at = lines.FindIndex(l => l.StartsWith("float fx_soft_fade_by(", StringComparison.Ordinal));
            return string.Join("\n", lines.Skip(at + 1).Take(3).Select(l => l.Trim()));
        }

        AssertThat(FirstStatement(hard).StartsWith("return 1.0;", StringComparison.Ordinal)).IsTrue();
        AssertThat(FirstStatement(soft)).IsEqual("if (inv <= 0.0) {\nreturn 1.0;\n}");
    }

    /// <summary>The debug lever takes a boolean or nothing.</summary>
    [TestCase]
    public void TheLeverReadsABoolean()
    {
        using JsonDocument on = JsonDocument.Parse("{\"fxNoDepthReads\": false}");
        using JsonDocument none = JsonDocument.Parse("{}");
        using JsonDocument bad = JsonDocument.Parse("{\"fxNoDepthReads\": \"yes\"}");
        AssertThat(Engine_FxDepthReads.ReadRequest(on.RootElement)).IsEqual(false);
        AssertThat(Engine_FxDepthReads.ReadRequest(none.RootElement) is null).IsTrue();
        AssertThrown(() => Engine_FxDepthReads.ReadRequest(bad.RootElement)).IsInstanceOf<ArgumentException>();
    }

    /// <summary>A 16:9 perspective 70 degrees tall, Godot's column layout, written out.</summary>
    private static Projection Lens
    {
        get
        {
            float f = 1f / MathF.Tan(35f * MathF.PI / 180f), near = 0.05f, far = 4000f;
            return new Projection(
                new Vector4(f / (16f / 9f), 0f, 0f, 0f),
                new Vector4(0f, f, 0f, 0f),
                new Vector4(0f, 0f, -(far + near) / (far - near), -1f),
                new Vector4(0f, 0f, -2f * far * near / (far - near), 0f));
        }
    }

    private static bool Reaches(Vector3 dirView, float half) =>
        Engine_CelestialOverlayReach.MayReach(dirView.Normalized(), 1999f, new Vector2(half, half), Lens, 0.5f, 0.05f);

    /// <summary>Ahead it draws; behind (past the facing margin) the vertex stage collapses it.</summary>
    [TestCase]
    public void AGlareAheadDrawsAndOneBehindDoesNot()
    {
        AssertThat(Reaches(new Vector3(0f, 0f, -1f), 100f)).IsTrue();
        AssertThat(Reaches(new Vector3(0.3f, 0.2f, -1f), 100f)).IsTrue();
        AssertThat(Reaches(new Vector3(0f, 0.2f, 1f), 4000f)).IsFalse();
        AssertThat(Reaches(new Vector3(1f, 0f, 0.2f), 4000f)).IsFalse();
    }

    /// <summary>
    /// Just behind the eye's plane still counts (a frame's camera turn), and just ahead of it the
    /// quad, held in front of the eye, spans the whole view.
    /// </summary>
    [TestCase]
    public void TheEyesPlaneIsKeptBothSides()
    {
        AssertThat(Reaches(new Vector3(1f, 0f, 0.01f), 10f)).IsTrue();
        AssertThat(Reaches(new Vector3(1f, 0f, -0.001f), 4000f)).IsTrue();
    }

    /// <summary>
    /// To the side, a small glare leaves the view and a big one still reaches it; the margin
    /// keeps one just past the edge.
    /// </summary>
    [TestCase]
    public void TheQuadsExtentDecides()
    {
        // 75 degrees off axis, far past the 51-degree half-width of a 16:9 view 70 degrees tall.
        var side = new Vector3(MathF.Sin(75f * MathF.PI / 180f), 0f, -MathF.Cos(75f * MathF.PI / 180f));
        AssertThat(Reaches(side, 50f)).IsFalse();
        AssertThat(Reaches(side, 4000f)).IsTrue();

        // Just past the edge (55 degrees): inside the margin.
        var edge = new Vector3(MathF.Sin(55f * MathF.PI / 180f), 0f, -MathF.Cos(55f * MathF.PI / 180f));
        AssertThat(Reaches(edge, 1f)).IsTrue();
    }

    /// <summary>A direction that does not project is kept, never hidden on a guess.</summary>
    [TestCase]
    public void AnUnprojectableDirectionIsKept()
    {
        AssertThat(Engine_CelestialOverlayReach.MayReach(
            new Vector3(float.NaN, 0f, -1f), 1999f, new Vector2(10f, 10f), Lens, 0.5f, 0.05f)).IsTrue();
    }
}
