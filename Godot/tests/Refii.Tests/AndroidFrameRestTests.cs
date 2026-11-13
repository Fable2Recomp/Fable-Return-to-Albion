using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Fx;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The phone's frame outside the opaque pass: `android_canvas_layout_size` (the root's 2D at the
/// layout size), `android_highlight_depth_test` (the target outline without a depth texture) and
/// `android_fx_hard_all` (no effect particle reads depth).
/// </summary>
[TestSuite]
public sealed class AndroidFrameRestTests
{
    private static string FileText(string repoPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    private static RuntimeCVarDefinition Row(RuntimeCVarDefinition[] group, string key)
    {
        RuntimeCVarDefinition? row = group.SingleOrDefault(d => d.Name == key);
        AssertThat(row).IsNotNull();
        return row!;
    }

    // android_canvas_layout_size

    /// <summary>The S10e's 2280 x 1080 against the 1560-wide expand layout is a 1.46 stretch on both axes.</summary>
    [TestCase]
    public void StretchIsTheUniformScreenToLayoutRatio()
    {
        float stretch = AndroidCanvasResolution.Stretch(new Vector2I(2280, 1080), new Vector2(1560f, 738.95f));
        AssertThat(MathF.Abs(stretch - 2280f / 1560f) < 0.002f).IsTrue();
        AssertThat(AndroidCanvasResolution.Stretch(new Vector2I(0, 1080), new Vector2(1560f, 739f))).IsEqual(1f);
        AssertThat(AndroidCanvasResolution.Stretch(new Vector2I(2280, 1080), Vector2.Zero)).IsEqual(1f);
    }

    /// <summary>Only on, and only where the screen is enough larger than the layout.</summary>
    [TestCase]
    public void AppliesOnlyWhenOnAndTheScreenIsLarger()
    {
        AssertThat(AndroidCanvasResolution.Applies(true, 1.46f)).IsTrue();
        AssertThat(AndroidCanvasResolution.Applies(false, 1.46f)).IsFalse();
        AssertThat(AndroidCanvasResolution.Applies(true, 1.02f)).IsFalse();
        AssertThat(AndroidCanvasResolution.Applies(true, 0.8f)).IsFalse();
    }

    /// <summary>
    /// The world keeps its pixels: 40% of the screen is 58.5% of the layout, the same 912 columns on
    /// the S10e; capped at the layout size; untouched while off.
    /// </summary>
    [TestCase]
    public void WorldScaleKeepsTheWorldsPixels()
    {
        float stretch = 2280f / 1560f;
        float scale = AndroidCanvasResolution.WorldScale(0.4f, stretch, applies: true);
        AssertThat(MathF.Abs(1560f * scale - 912f) < 0.01f).IsTrue();
        AssertThat(AndroidCanvasResolution.WorldScale(0.8f, stretch, applies: true)).IsEqual(1f);
        AssertThat(AndroidCanvasResolution.WorldScale(0.4f, stretch, applies: false)).IsEqual(0.4f);
    }

    /// <summary>The size the mesh LOD rule is normalised by follows the root's target.</summary>
    [TestCase]
    public void RenderedSizeFollowsTheRootsTarget()
    {
        AssertThat(AndroidCanvasResolution.RenderedSize(new Vector2I(2280, 1080), new Vector2(1560f, 738.9f), true))
            .IsEqual(new Vector2I(1560, 739));
        AssertThat(AndroidCanvasResolution.RenderedSize(new Vector2I(2280, 1080), new Vector2(1560f, 738.9f), false))
            .IsEqual(new Vector2I(2280, 1080));
    }

    /// <summary>The render scale policy takes the adjusted scale, and re-applies when only the adjustment moves.</summary>
    [TestCase]
    public void RenderScalePolicyAppliesTheAdjustedScale()
    {
        var settings = Refii.Game.GameWindowSettings.FromValues(
            new Dictionary<string, string> { ["render_scale_percent"] = "40" }, "refii.toml");
        var policy = new AndroidRenderScale();
        float applied = 0f;
        AssertThat(policy.Apply(settings, v => applied = v)).IsTrue();
        AssertThat(applied).IsEqual(0.4f);
        AssertThat(policy.Apply(settings, v => applied = v, s => AndroidCanvasResolution.WorldScale(s, 1.5f, true))).IsTrue();
        AssertThat(MathF.Abs(applied - 0.6f) < 1e-6f).IsTrue();
        AssertThat(policy.Apply(settings, _ => throw new InvalidOperationException("unchanged"),
            s => AndroidCanvasResolution.WorldScale(s, 1.5f, true))).IsFalse();
    }

    /// <summary>The host applies it before the render scale and feeds the LOD rule the root's rendered size.</summary>
    [TestCase]
    public void TheHostHooksIt()
    {
        string host = FileText("Godot/src/android/AndroidGameHost.cs");
        int apply = host.IndexOf("AndroidCanvasResolution.Apply(GetTree().Root", StringComparison.Ordinal);
        int scale = host.IndexOf("_renderScale.Apply(settings", StringComparison.Ordinal);
        AssertThat(apply > 0 && apply < scale).IsTrue();
        AssertThat(host).Contains("Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), AndroidCanvasResolution.Key, false)");
        AssertThat(host).Contains("AndroidCanvasResolution.WorldScale(scale, stretch, canvasLayout)");
        AssertThat(host).Contains("Vector2I physicalSize = renderedSize;");
    }

    /// <summary>A phone Tuning row, off by default (it changes the picture), with a player's name.</summary>
    [TestCase]
    public void CanvasLayoutKeyIsRegisteredOff()
    {
        AssertThat(Row(RuntimeCVarsAndroid.Tuning, AndroidCanvasResolution.Key).DefaultValue).IsEqual("false");
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidCanvasResolution.Key)).IsTrue();
    }

    // android_highlight_depth_test

    /// <summary>A quad's distance stays inside the clip range: a camera inside its target puts it just past near.</summary>
    [TestCase]
    public void QuadDistanceStaysInsideTheClipRange()
    {
        AssertThat(AndroidTargetingHighlight.QuadDistance(0f, 0.1f, 4000f) > 0.1f).IsTrue();
        AssertThat(AndroidTargetingHighlight.QuadDistance(0f, 0.1f, 4000f) < 0.101f).IsTrue();
        AssertThat(AndroidTargetingHighlight.QuadDistance(12.5f, 0.1f, 4000f)).IsEqual(12.5f);
        AssertThat(AndroidTargetingHighlight.QuadDistance(9000f, 0.1f, 4000f) < 4000f).IsTrue();
    }

    /// <summary>
    /// n quads each blending the glow once (ONE / INVSRCALPHA) leave what the composite's single
    /// quad writes for n objects: rgb * (1 - (1 - a)^n) / a over the scene * (1 - a)^n.
    /// </summary>
    [TestCase]
    public void OneBlendPerObjectIsTheCompositesNBlends()
    {
        var random = new Random(7);
        for (int trial = 0; trial < 200; ++trial)
        {
            float g = (float)random.NextDouble();
            float a = trial == 0 ? 0f : (float)random.NextDouble();
            float scene = (float)random.NextDouble() * 4f;
            int n = 1 + random.Next(8);

            float sequential = scene;
            for (int k = 0; k < n; ++k)
            {
                sequential = g + sequential * (1f - a);
            }

            float keep = MathF.Pow(1f - a, n);
            float sum = a > 1e-4f ? (1f - keep) / a : n;
            float composite = g * sum + scene * keep;
            AssertThat(MathF.Abs(sequential - composite) < 1e-3f).IsTrue();
        }
    }

    /// <summary>The tested quad declares no depth or screen texture and keeps the depth test; the composite's discards stay.</summary>
    [TestCase]
    public void TheTestedShaderReadsNoDepth()
    {
        string shader = FileText("Godot/src/android/mobile_highlight_composite_tested.gdshader");
        AssertThat(shader).NotContains("hint_depth_texture");
        AssertThat(shader).NotContains("hint_screen_texture");
        AssertThat(shader).NotContains("depth_test_disabled");
        AssertThat(shader).Contains("blend_premul_alpha");
        AssertThat(shader).Contains("depth_draw_never");
        AssertThat(shader).Contains("PROJECTION_MATRIX * vec4(0.0, 0.0, -test, 1.0)");
        AssertThat(shader).Contains("texture(layer_tex, uv).g > 0.5");
        string composite = FileText("Godot/src/android/mobile_highlight_composite.gdshader");
        AssertThat(composite).Contains("texture(layer_tex, uv).g > 0.5");
        AssertThat(composite).Contains("tests[k] <= scene");
    }

    /// <summary>The highlight hides its depth-reading quad while the tested ones draw.</summary>
    [TestCase]
    public void TheHighlightSwitchesQuads()
    {
        string mobile = FileText("Godot/src/android/Engine_MobileHighlight.cs");
        AssertThat(mobile).Contains("Refii.AndroidRuntime.AndroidTargetingHighlight.DepthTest");
        AssertThat(mobile).Contains("_quad.Visible = count > 0 && !tested;");
        AssertThat(mobile).Contains("AndroidTargetingHighlight.QuadDistance(_tests[k], camera.Near, camera.Far)");
    }

    /// <summary>A phone Tuning row, off until it is checked on a phone, with a player's name.</summary>
    [TestCase]
    public void DepthTestKeyIsRegisteredOff()
    {
        AssertThat(Row(RuntimeCVarsAndroid.Tuning, AndroidTargetingHighlight.DepthTestKey).DefaultValue).IsEqual("false");
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidTargetingHighlight.DepthTestKey)).IsTrue();
    }

    // android_fx_hard_all

    /// <summary>Hard: every material under hard-all; else only the fade-less ones under fx_no_depth_reads.</summary>
    [TestCase]
    public void ShouldBeHardTruthTable()
    {
        AssertThat(Engine_FxDepthReads.ShouldBeHard(noDepthReads: false, hardAll: false, soft: false)).IsFalse();
        AssertThat(Engine_FxDepthReads.ShouldBeHard(noDepthReads: true, hardAll: false, soft: false)).IsTrue();
        AssertThat(Engine_FxDepthReads.ShouldBeHard(noDepthReads: true, hardAll: false, soft: true)).IsFalse();
        AssertThat(Engine_FxDepthReads.ShouldBeHard(noDepthReads: false, hardAll: true, soft: true)).IsTrue();
        AssertThat(Engine_FxDepthReads.ShouldBeHard(noDepthReads: false, hardAll: true, soft: false)).IsTrue();
    }

    /// <summary>Off Android only fade-less fx_soft materials are kept, as before; the key reads off there.</summary>
    [TestCase]
    public void TheDesktopKeepsWhatItKept()
    {
        AssertThat(Refii.Core.Engine_Platform.IsAndroid).IsFalse();
        AssertThat(Engine_FxDepthReads.Adoptable(0f, "mix")).IsTrue();
        AssertThat(Engine_FxDepthReads.Adoptable(0.5f, "mix")).IsFalse();
        AssertThat(Engine_FxDepthReads.Adoptable(0f, "disp")).IsFalse();
        AssertThat(Engine_FxDepthReads.Adoptable(0f, "addlayer")).IsFalse();
        AssertThat(Engine_FxDepthReads.HardAll).IsFalse();
    }

    /// <summary>Both particle paths ask the one rule.</summary>
    [TestCase]
    public void BothParticlePathsAskTheRule()
    {
        string fx = FileText("Godot/src/Engine/fx/FableFx.cs");
        AssertThat(fx).Contains("if (Engine_FxDepthReads.Adoptable(softDepth, mode))");
        AssertThat(fx).Contains("Engine_FxDepthReads.Adopt(material, shader, softDepth > 0f);");
        string classic = FileText("Godot/src/android/Engine_ClassicParticles.Android.cs");
        AssertThat(classic).Contains("Engine_FxDepthReads.ShouldBeHard(Refii.Fx.Engine_FxDepthReads.Enabled, Refii.Fx.Engine_FxDepthReads.HardAll, soft)");
    }

    /// <summary>A phone effects row, off by default (it changes the picture), with a player's name.</summary>
    [TestCase]
    public void HardAllKeyIsRegisteredOff()
    {
        AssertThat(Row(RuntimeCVarsAndroid.EffectsAndCanvas, Engine_FxDepthReads.HardAllKey).DefaultValue).IsEqual("false");
        AssertThat(AndroidSettingNames.Keys.Contains(Engine_FxDepthReads.HardAllKey)).IsTrue();
    }
}
