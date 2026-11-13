using System;
using System.IO;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// Per-frame CPU savings, second pass: the FX light curves read once per frame and phase, the
/// recipe pass reusing its snapshot lists, the HUD behaviour pass's cached delegates, and the
/// phone's skipped repeat Environment writes.
/// </summary>
[TestSuite]
public sealed class MainThreadFrameCostMoreTests
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

    /// <summary>The light state reads its curves only when the frame or the flicker phase moved.</summary>
    [TestCase]
    public void LightCurvesAreReadOnlyWhenTheirInputsMove()
    {
        string fx = FileText("Godot/src/Engine/fx/Engine_FxSystem.cs");
        string state = Between(fx, "private static FxLightState LightState(", "/// <summary>\n    /// The halo texture");
        AssertThat(state).Contains("if (!c.Valid || c.Frame != f || c.Phase != phase)");
        AssertThat(state.Split("EvalFrame(").Length - 1).IsEqual(8);
        AssertThat(fx).Contains("ref light.NowCurves");
        AssertThat(fx).Contains("ref light.PrevCurves");
    }

    /// <summary>Begin walks reused snapshots of the anchor maps, not a new list each frame.</summary>
    [TestCase]
    public void TheRecipePassReusesItsSnapshotLists()
    {
        string begin = Between(FileText("Godot/src/Engine/fx/Engine_FxRecipes.cs"), "public static void Begin()", "/// Dresses or undresses");
        AssertThat(begin).NotContains("new List<");
        AssertThat(begin).Contains("_anchorScratch.AddRange(_anchorOf);");
        AssertThat(begin).Contains("_withScratch.AddRange(_withAnchors);");
    }

    /// <summary>The HUD behaviour and stand passes pass cached delegates over fields, not lambdas over locals.</summary>
    [TestCase]
    public void TheHudPassesDoNotMakeClosures()
    {
        string ui = FileText("Godot/src/Titles/Fable2/game/GameUi.cs");
        AssertThat(ui).Contains("behaviours.Observe(_observeLive ??= ObserveLive)");
        AssertThat(ui).Contains("_statedPlayTest ??= StatedPlay,");
        AssertThat(ui).NotContains("behaviours.Observe(ordinal =>");
        AssertThat(ui).NotContains("ordinal => ordinal >= 0 && ordinal < statedPlays.Length");
    }

    /// <summary>A group is written the first time and when its key moves, and skipped while the key holds.</summary>
    [TestCase]
    public void EnvironmentWritesAreSkippedWhileTheirKeyHolds()
    {
        var seen = new AndroidEnvironmentWrites.Seen();
        AssertThat(AndroidEnvironmentWrites.Record(seen, 0, 1f)).IsFalse();
        AssertThat(AndroidEnvironmentWrites.Record(seen, 0, 1f)).IsTrue();
        AssertThat(AndroidEnvironmentWrites.Record(seen, 0, 2f)).IsFalse();
        AssertThat(AndroidEnvironmentWrites.Record(seen, 0, 2f)).IsTrue();
        // Groups are independent.
        AssertThat(AndroidEnvironmentWrites.Record(seen, 1, 2f)).IsFalse();
    }

    /// <summary>The lighting hooks are one line each, after the switch and before the constants.</summary>
    [TestCase]
    public void TheLightingHooksAreOneLine()
    {
        string lighting = FileText("Godot/src/Engine/Lighting/Engine_LevelLighting.cs");
        AssertThat(lighting.Split("AndroidEnvironmentWrites.Unchanged(").Length - 1).IsEqual(2);
        AssertThat(Between(lighting, "AndroidEnvironmentWrites.Unchanged(environment, 0", "environment.SdfgiCascades"))
            .NotContains("environment.Sdfgi");
        AssertThat(Between(lighting, "AndroidEnvironmentWrites.Unchanged(environment, 1", "environment.SsaoRadius"))
            .NotContains("environment.Ssao");
    }
}
