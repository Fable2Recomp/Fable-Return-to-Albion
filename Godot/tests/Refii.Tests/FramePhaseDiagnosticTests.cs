using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GdUnit4;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks that CPU phase evidence is finite, detached and readable independently of unavailable OS counters.</summary>
[TestSuite]
public sealed class FramePhaseDiagnosticTests
{
    /// <summary>Reads the endpoint's checked-out source without requiring a native node instance.</summary>
    private static string Source([CallerFilePath] string source="") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!,"../../src/game/GameDebugServer.cs")));
    /// <summary>A scope can publish, serialize and be sampled again without mutating an earlier report.</summary>
    [TestCase]
    public void PublishReadAndDetachedSnapshotLifecycle()
    {
        string name="diagnostic_test_"+Guid.NewGuid().ToString("N");
        using(Engine_FramePhases.Measure(name)) { _=Math.Sqrt(123); }
        var first=Engine_FramePhases.Report();
        var row=(Dictionary<string,object?>)first[name]!;
        AssertThat(double.IsFinite((double)row["avgMs"]!)).IsTrue();
        JsonSerializer.Serialize(first);
        row["lastMs"]=-123d;
        using(Engine_FramePhases.Measure(name)) { _=Math.Sqrt(456); }
        var second=Engine_FramePhases.Report();
        AssertThat((double)((Dictionary<string,object?>)second[name]!)["lastMs"]!>=0).IsTrue();
        AssertThat((double)row["lastMs"]!).IsEqual(-123d);
        JsonSerializer.Serialize(second);
    }

    /// <summary>The read-only endpoint obtains only phase evidence and does not execute the broader metrics operation.</summary>
    [TestCase]
    public void IndependentReadEndpoint()
    {
        string text=Source();
        int start=text.IndexOf("case \"framephases\":",StringComparison.Ordinal);
        int end=text.IndexOf("case \"",start+8,StringComparison.Ordinal);
        AssertThat(start>=0 && end>start).IsTrue();
        string endpoint=text[start..end];
        AssertThat(endpoint.Contains("return Ok(Refii.Core.Engine_FramePhases.Report());")).IsTrue();
        AssertThat(endpoint.Contains("Engine_RenderStats")).IsFalse();
    }
}
