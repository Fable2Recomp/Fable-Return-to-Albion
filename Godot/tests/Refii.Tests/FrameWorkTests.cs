using System;
using System.Collections.Generic;
using System.Diagnostics;
using GdUnit4;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Verifies repeated-work totals and their frame-to-diagnostic publication lifecycle.</summary>
[TestSuite]
public sealed class FrameWorkTests
{
    /// <summary>Checks failure paths through the CLI-compatible assertion API.</summary>
    private static void Expect<T>(Action action) where T : Exception
    {
        bool thrown = false;
        try { action(); } catch (T) { thrown = true; }
        AssertThat(thrown).IsTrue();
    }
    /// <summary>Accumulates actors rather than averaging them, preserves names, and rejects invalid durations.</summary>
    [TestCase]
    public void AccumulationAndBoundaries()
    {
        string[] names = { "sample", "submit" };
        var work = new Engine_FrameWork(names);
        names[0] = "changed";
        work.AddTicks(0, Stopwatch.Frequency);
        work.AddTicks(0, Stopwatch.Frequency / 2);
        var values = new Dictionary<string, double>();
        work.Publish((name, ms) => values.Add(name, ms));
        AssertThat(Math.Abs(values["sample"] - 1500) < .001).IsTrue();
        AssertThat(values["submit"]).IsEqual(0d);
        Expect<ArgumentOutOfRangeException>(() => work.AddTicks(0, -1));
        Expect<IndexOutOfRangeException>(() => work.AddTicks(2, 0));
        Expect<ArgumentException>(() => new Engine_FrameWork());
        Expect<ArgumentException>(() => new Engine_FrameWork(" "));
    }

    /// <summary>Publishes a detached total, resets on the next frame, and replaces early-return/exception work with zero.</summary>
    [TestCase]
    public void FramePublicationLifecycle()
    {
        string name = "frame_work_test_" + Guid.NewGuid().ToString("N");
        var work = new Engine_FrameWork(name);
        using (work.BeginFrame())
        {
            long since = Stopwatch.GetTimestamp();
            work.Lap(0, ref since);
            work.AddTicks(0, Stopwatch.Frequency);
        }
        var first = (Dictionary<string, object?>)Engine_FramePhases.Report()[name]!;
        AssertThat((double)first["lastMs"]! >= 1000).IsTrue();
        try
        {
            using var frame = work.BeginFrame();
            throw new InvalidOperationException("Test frame exit");
        }
        catch (InvalidOperationException) { }
        var second = (Dictionary<string, object?>)Engine_FramePhases.Report()[name]!;
        AssertThat((double)second["lastMs"]!).IsEqual(0d);
        AssertThat((double)first["lastMs"]! >= 1000).IsTrue();
        using (work.BeginFrame()) work.AddTicks(0, Stopwatch.Frequency / 4);
        AssertThat(Math.Abs(Engine_FramePhases.Last(name) - 250) < .001).IsTrue();
    }
}
