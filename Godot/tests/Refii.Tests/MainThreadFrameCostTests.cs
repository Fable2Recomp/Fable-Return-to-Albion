using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Core;
using Refii.Launcher.Core;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The main thread's per-frame savings: the "draw" span split where it waits, the HUD's slab
/// pieces shown hidden from the start, writes skipped when their value has not moved, and the
/// phone's full-rate pose radius.
/// </summary>
[TestSuite]
public sealed class MainThreadFrameCostTests
{
    private static string FileText(string repoPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    private static string Between(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        AssertThat(start).OverrideFailureMessage(from + " not found").IsGreaterEqual(0);
        int end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        AssertThat(end).OverrideFailureMessage(to + " not found after " + from).IsGreater(start);
        return text[start..end];
    }

    /// <summary>The three parts, marked in order, add up to the "draw" span they divide.</summary>
    [TestCase]
    public void TheDrawSpanSplitsIntoPartsThatSumToIt()
    {
        Engine_FramePhases.ResetMetrics();
        Engine_FramePhases.MarkFrameEnd();
        Thread.Sleep(2);
        Engine_FramePhases.MarkDeferredEnd();
        Thread.Sleep(2);
        Engine_FramePhases.MarkPreDraw();
        Thread.Sleep(2);
        Engine_FramePhases.MarkDrawEnd();

        double deferred = Engine_FramePhases.Last("draw.deferred");
        double sync = Engine_FramePhases.Last("draw.sync");
        double post = Engine_FramePhases.Last("draw.post");
        double draw = Engine_FramePhases.Last("draw");
        AssertThat(deferred).IsGreater(0.0);
        AssertThat(sync).IsGreater(0.0);
        AssertThat(post).IsGreater(0.0);
        AssertThat(Math.Abs(deferred + sync + post - draw)).IsLess(0.01);
        Engine_FramePhases.ResetMetrics();
    }

    /// <summary>The mark behind the deferred calls is queued by the last process callback, and the pre-draw mark is the renderer's signal.</summary>
    [TestCase]
    public void TheMarksAreWiredToTheFrame()
    {
        string phases = FileText("Godot/src/Engine/core/Engine_FramePhases.cs");
        AssertThat(phases).Contains("FramePreDraw += Engine_FramePhases.MarkPreDraw;");
        string end = Between(phases, "public sealed partial class Engine_FrameEndNode", "\n}\n");
        AssertThat(end.IndexOf("CallDeferred(DeferredMark);", StringComparison.Ordinal))
            .IsGreater(end.IndexOf("Engine_FramePhases.MarkFrameEnd();", StringComparison.Ordinal));
    }

    /// <summary>The slab pieces the modern panels hide whole, by id.</summary>
    [TestCase]
    public void SlabPiecesAreKnownByTheirIds()
    {
        foreach (string id in new[]
                 {
                     "slidetoggle.Counter1.Counter", "group.Counters.CornerBox", "meter.CornerX.Box",
                     "meter.CornerY.Box", "meter.SubtitlesX.Box", "meter.SubtitlesY.Box",
                     "slidefader2.Subtitles.Subtitles",
                 })
        {
            AssertThat(Engine_HudView.Slabs.Is(id)).OverrideFailureMessage(id).IsTrue();
        }

        foreach (string id in new[]
                 {
                     "group.QuestBox.TopBox", "text.Counter1.Text", "text.Subtitles.Text",
                     "meter.Health.Bar", "slidefader2.Subtitles.Other",
                 })
        {
            AssertThat(Engine_HudView.Slabs.Is(id)).OverrideFailureMessage(id).IsFalse();
        }
    }

    /// <summary>
    /// A slab piece is shown hidden by the walk and the bar drive, the panels test the same
    /// predicate, and an unchanged bar is left where its last drive put it.
    /// </summary>
    [TestCase]
    public void TheHudWalkDoesNotFlipOrRewriteWhatStands()
    {
        string hud = FileText("Godot/src/Engine/ui/Engine_HudView.cs");
        AssertThat(hud).Contains("built.Node.Visible = !HiddenAsSlab(built);\n\n            Engine_UiCloneBinding.Lap(ref TicksGates, ref mark);");
        AssertThat(hud).Contains("else if (HiddenAsSlab(built))");
        AssertThat(hud).NotContains("built.Node.Visible = true;\n        built.Node.Scale");

        string bar = Between(hud, "private void DriveBar(", "\n    }\n");
        AssertThat(bar).Contains("last.Fraction == fraction && last.Scale == scale && last.At == at");
        AssertThat(bar).Contains("built.LastBar = null;");
        AssertThat(bar).Contains("built.LastBar = (placed, fraction, scale, at, built.Node.Transform);");
        AssertThat(hud).Contains("lastBar.From == placed && lastBar.To == standing");
    }

    /// <summary>Only the displacement mesh nodes are waited on for a layer instance.</summary>
    [TestCase]
    public void AnEffectStopsAskingForInstancesItNeverMakes()
    {
        string fx = FileText("Godot/src/Engine/fx/FableFx.cs");
        AssertThat(fx).Contains("bool needMesh = _meshInstances.Count < _dispMeshNodes;");
        AssertThat(fx).Contains("++_dispMeshNodes;");
        AssertThat(fx).Contains("_meshNodes.Clear();\n\t\t_dispMeshNodes = 0;");
        // The one place a mesh node is given the displacement layer is the one counted.
        AssertThat(fx.Split("node.Layers = Engine_FxDisplacement.LayerMask;").Length - 1).IsEqual(1);
    }

    /// <summary>The glares' cloud term is written only when it moves; the water walks its children by index.</summary>
    [TestCase]
    public void SkyWritesOnlyWhatMoved()
    {
        string sky = FileText("Godot/src/Engine/level/environment/Engine_SkyEnvironmentNode.cs");
        AssertThat(sky).Contains("vis != _moonGlareVis");
        AssertThat(sky).Contains("vis != _sunGlareVis");
        AssertThat(sky.Split("SetShaderParameter(Param_element_vis").Length - 1).IsEqual(2);

        string water = Between(FileText("Godot/src/Engine/level/water/Engine_WaterMaterial.cs"),
            "public static void AdvancePhases", "public static void Apply");
        AssertThat(water).NotContains("water.GetChildren()");
    }

    /// <summary>`android_pose_full_rate_m`: 0 keeps the game's 55 m; a phone Tuning row, off by default, with a player's name.</summary>
    [TestCase]
    public void ThePoseRadiusIsAPhoneSettingOffByDefault()
    {
        AssertThat(AndroidPoseRate.Radius(0f)).IsEqual(AndroidPoseRate.GameRadius);
        AssertThat(AndroidPoseRate.Radius(-5f)).IsEqual(AndroidPoseRate.GameRadius);
        AssertThat(AndroidPoseRate.Radius(float.NaN)).IsEqual(AndroidPoseRate.GameRadius);
        AssertThat(AndroidPoseRate.Radius(20f)).IsEqual(20f);
        // Read as text: the entity node's statics are Godot objects, which this host cannot make.
        AssertThat(FileText("Godot/src/game/GameEntities.cs")).Contains("public static float PoseFullRateRadius = 55f;");

        RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == AndroidPoseRate.Key);
        AssertThat(row).OverrideFailureMessage(AndroidPoseRate.Key + " is not a Tuning row").IsNotNull();
        AssertThat(row!.DefaultValue).IsEqual("0");
        AssertThat(AndroidSettingNames.Keys.Contains(AndroidPoseRate.Key)).IsTrue();
        AssertThat(FileText("Godot/src/android/AndroidGameHost.cs")).Contains("AndroidPoseRate.Apply();");
    }
}
