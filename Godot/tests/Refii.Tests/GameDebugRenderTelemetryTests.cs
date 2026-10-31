using System;
using System.Collections.Generic;
using System.Text.Json;
using GdUnit4;
using Refii.Game;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Exercises strict render JSON and startup/sample/restart lifecycles without Godot nodes.</summary>
[TestSuite]
public sealed class GameDebugRenderTelemetryTests
{
    /// <summary>Real finite samples, including Linux-style CPU shares and measured zero, retain their types and precision.</summary>
    [TestCase]
    public void FiniteMeasurementsAndScalarIdentityRemainExact()
    {
        var source = new Dictionary<string, object?>
        {
            ["cpuShare"] = .625d, ["playheadVsNewest"] = -1.25d, ["refreshHz"] = 59.94f,
            ["zero"] = 0d, ["maximum"] = double.MaxValue, ["minimum"] = float.MinValue,
            ["shadowBudget"] = 8, ["sequence"] = ulong.MaxValue, ["sunShadow"] = (bool?)null,
            ["reflections"] = true, ["hero"] = "(-12.3, 4.5, 6.7)", ["painted"] = "layers=[]",
        };
        var snapshot = GameDebugServer.FiniteRenderTelemetry(source);
        AssertThat(snapshot.Count).IsEqual(source.Count);
        foreach ((string key, object? value) in source)
            AssertThat(Equals(snapshot[key], value)).IsTrue();
        AssertThat(snapshot["refreshHz"] is float).IsTrue();
        AssertThat(snapshot["cpuShare"] is double).IsTrue();
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        AssertThat(json.RootElement.GetProperty("zero").GetDouble()).IsEqual(0d);
        AssertThat(json.RootElement.GetProperty("sequence").GetUInt64()).IsEqual(ulong.MaxValue);
    }

    /// <summary>Unsupported samples and all nonfinite float forms become null rather than fictitious zeroes or named literals.</summary>
    [TestCase]
    public void UnsupportedAndInfiniteMeasurementsSerializeAsNull()
    {
        var source = new Dictionary<string, object?>
        {
            ["unsupportedCpuShare"] = double.NaN, ["playheadVsNewest"] = double.NaN,
            ["positiveDouble"] = double.PositiveInfinity, ["negativeDouble"] = double.NegativeInfinity,
            ["nanFloat"] = float.NaN, ["positiveFloat"] = float.PositiveInfinity,
            ["negativeFloat"] = float.NegativeInfinity, ["nullable"] = (float?)float.NaN,
            ["alreadyNull"] = null, ["measuredZero"] = 0f,
        };
        var snapshot = GameDebugServer.FiniteRenderTelemetry(source);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        foreach (string key in source.Keys)
        {
            if (key == "measuredZero")
                AssertThat(json.RootElement.GetProperty(key).GetSingle()).IsEqual(0f);
            else
                AssertThat(json.RootElement.GetProperty(key).ValueKind).IsEqual(JsonValueKind.Null);
        }
        AssertThat(double.IsNaN((double)source["unsupportedCpuShare"]!)).IsTrue();
        AssertThat(float.IsPositiveInfinity((float)source["positiveFloat"]!)).IsTrue();
    }

    /// <summary>A title without clock fit, a ready world and a restarted fit each publish stable, independently serializable snapshots.</summary>
    [TestCase]
    public void TitleWorldAndRestartSnapshotsRemainDetached()
    {
        var source = new Dictionary<string, object?>
        {
            ["playheadVsNewest"] = double.NaN, ["refreshHz"] = 60f,
            ["shadowBudget"] = 8, ["sunShadow"] = null, ["hero"] = null,
        };
        var title = GameDebugServer.FiniteRenderTelemetry(source);
        string titleJson = JsonSerializer.Serialize(title);
        source["playheadVsNewest"] = -2.5d;
        source["hero"] = "(-12.3, 4.5, 6.7)";
        var world = GameDebugServer.FiniteRenderTelemetry(source);
        string worldJson = JsonSerializer.Serialize(world);
        source["playheadVsNewest"] = double.NaN;
        source["refreshHz"] = float.PositiveInfinity;
        source["hero"] = null;
        var restart = GameDebugServer.FiniteRenderTelemetry(source);
        AssertThat(JsonSerializer.Serialize(title)).IsEqual(titleJson);
        AssertThat(JsonSerializer.Serialize(world)).IsEqual(worldJson);
        AssertThat(title["playheadVsNewest"]).IsNull();
        AssertThat(world["playheadVsNewest"]).IsEqual(-2.5d);
        AssertThat(restart["playheadVsNewest"]).IsNull();
        AssertThat(restart["refreshHz"]).IsNull();
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(restart));
        AssertThat(json.RootElement.GetProperty("sunShadow").ValueKind).IsEqual(JsonValueKind.Null);
        AssertThat(json.RootElement.GetProperty("shadowBudget").GetInt32()).IsEqual(8);
    }

    /// <summary>Empty input remains valid and a missing source fails before any mutation.</summary>
    [TestCase]
    public void EmptyAndInvalidSourcesHaveExplicitBehavior()
    {
        AssertThat(JsonSerializer.Serialize(GameDebugServer.FiniteRenderTelemetry(
            new Dictionary<string, object?>()))).IsEqual("{}");
        bool rejected = false;
        try { GameDebugServer.FiniteRenderTelemetry(null!); }
        catch (ArgumentNullException) { rejected = true; }
        AssertThat(rejected).IsTrue();
    }
}
