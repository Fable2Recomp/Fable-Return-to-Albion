using GdUnit4;
using Refii.Core;
using Refii.Animations;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks upstream timestamp interpolation and clip restart lifecycles without native rendering.</summary>
[TestSuite]
public sealed class UpstreamTimingLifecycleTests
{
    /// <summary>Wraps a bounded timestamp ring, interpolates samples, rejects stale spans and retires a region.</summary>
    [TestCase]
    public void TimestampRingWrapAndRegionRetirement()
    {
        var ring = new Engine_StampRing(3);
        AssertThat(ring.Locate(0, 2, out _, out _, out _) == Engine_StampRing.Where.Empty).IsTrue();
        ring.Push(1); ring.Push(2); ring.Push(3); ring.Push(4);
        AssertThat(ring.Count).IsEqual(3);
        AssertThat(ring.Locate(2.5, 2, out int earlier, out int later, out double fraction) == Engine_StampRing.Where.Between).IsTrue();
        AssertThat(fraction).IsEqual(.5);
        AssertThat(earlier != later).IsTrue();
        ring.Push(10);
        AssertThat(ring.Locate(6, 2, out _, out _, out _) == Engine_StampRing.Where.Stale).IsTrue();
        ring.Clear();
        AssertThat(double.IsNaN(ring.NewestStamp)).IsTrue();
        double first = ring.NextHalfTick(20);
        ring.Push(first);
        double second = ring.NextHalfTick(20);
        AssertThat(first).IsEqual(19.5);
        AssertThat(second).IsEqual(20.0);
    }

    /// <summary>Anchors a clip, accepts repeated reads, handles a loop boundary and applies an explicit cut immediately.</summary>
    [TestCase]
    public void ClipObservationLoopAndCutLifecycle()
    {
        var clip = new Engine_ClipPlayhead();
        clip.Observe(.9, .95, 10, 1, true, false, 10);
        AssertThat(clip.Started).IsTrue();
        AssertThat(clip.Time).IsEqual(.95);
        double stamp = clip.Stamp;
        clip.Observe(.9, .95, 10, 1, true, false, 10);
        AssertThat(clip.Stamp).IsEqual(stamp);
        clip.Observe(.95, 0, 11, 1, true, false, 11);
        AssertThat(System.Math.Abs(clip.Rate - .75) < 1e-10).IsTrue();
        // A real jump: 0.4 in one tick is past RestartSeconds. A stated cut that a steady rate
        // could explain (the same 0.4 over four ticks) is played through instead, since 1db02f80.
        clip.Observe(.4, .4, 12, 1, true, true, 12);
        AssertThat(clip.Time).IsEqual(.4);
        AssertThat(clip.Rate).IsEqual(0.0);
        // Supply the frame stamp explicitly: the CLI has no native Godot clock.
        Refii.Entities.Engine_DrawnFrame.BeginFrame(1_000_000);
        try { AssertThat(clip.Advance(0, 12)).IsEqual(.4); }
        finally { Refii.Entities.Engine_DrawnFrame.BeginFrame(0); }
    }
}
