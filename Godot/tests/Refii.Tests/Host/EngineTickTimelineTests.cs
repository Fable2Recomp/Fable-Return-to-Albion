namespace Refii.Tests;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Core;

[TestSuite]
public class EngineTickTimelineTests
{
    [TestCase]
    public void TestResetAndAdvanceWithoutObserveDoesNotThrow()
    {
        var timeline = new Engine_TickTimeline(stillWhenUnchanged: true);
        var initialTransform = new Transform3D(Basis.Identity, new Vector3(10f, 20f, 30f));
        float initialFov = 45f;

        timeline.Reset(100.0, initialTransform, initialFov);
        AssertThat(timeline.Empty).IsFalse();
        AssertThat(timeline.Newest.Origin).IsEqual(new Vector3(10f, 20f, 30f));
        AssertThat(timeline.DrawnScalar).IsEqual(45f);

        // Advance must not throw ArgumentException: Argument is not normalized. (Parameter 'axis')
        Transform3D drawn1 = timeline.Advance(1_000_000UL, 100.0);
        AssertThat(drawn1.Origin).IsEqual(new Vector3(10f, 20f, 30f));

        Transform3D drawn2 = timeline.Advance(1_016_666UL, 100.25);
        AssertThat(drawn2.Origin).IsEqual(new Vector3(10f, 20f, 30f));
        AssertThat(timeline.DrawnScalar).IsEqual(45f);
    }

    [TestCase]
    public void TestObserveZeroDeltaRotationDoesNotThrow()
    {
        var timeline = new Engine_TickTimeline(stillWhenUnchanged: true);
        var initialTransform = Transform3D.Identity;

        timeline.Reset(10.0, initialTransform);

        // Observing identity / unchanged transform
        timeline.Observe(11.0, initialTransform);

        // Advancing over time should produce valid transform without throwing
        for (int i = 0; i < 5; i++)
        {
            ulong usec = (ulong)(10_000_000 + (i * 16_666));
            Transform3D drawn = timeline.Advance(usec, 11.0 + (i * 0.1));
            AssertThat(drawn.Origin.IsZeroApprox()).IsTrue();
            AssertThat(drawn.Basis.GetRotationQuaternion().IsEqualApprox(Quaternion.Identity)).IsTrue();
        }
    }

    [TestCase]
    public void TestTinyRotationAndUnnormalizedAxesDoNotThrow()
    {
        var timeline = new Engine_TickTimeline(stillWhenUnchanged: true);
        timeline.Reset(0.0, Transform3D.Identity);

        // Extremely small rotation that could result in near-zero angle or axis precision issues
        var tinyRotation = new Transform3D(new Basis(Vector3.Up, 1e-5f), new Vector3(1f, 0f, 0f));
        timeline.Observe(1.0, tinyRotation);

        ulong startUsec = 5_000_000UL;
        for (int i = 1; i <= 10; i++)
        {
            ulong nowUsec = startUsec + (ulong)(i * 16_666);
            double drawTick = 1.0 + (i * 0.05);

            Transform3D drawn = timeline.Advance(nowUsec, drawTick);
            AssertThat(float.IsNaN(drawn.Origin.X)).IsFalse();
            AssertThat(float.IsNaN(drawn.Basis.X.X)).IsFalse();
        }
    }

    [TestCase]
    public void TestStandardMovementAndRotationAdvance()
    {
        var timeline = new Engine_TickTimeline(stillWhenUnchanged: true);
        timeline.Reset(0.0, Transform3D.Identity);

        // Move 2 meters and rotate 90 degrees around Y axis
        var targetBasis = new Basis(Vector3.Up, Mathf.Pi * 0.5f);
        var targetTransform = new Transform3D(targetBasis, new Vector3(2f, 0f, 0f));

        timeline.Observe(1.0, targetTransform);

        ulong startUsec = 10_000_000UL;
        Transform3D lastDrawn = timeline.Advance(startUsec, 0.0);

        for (int i = 1; i <= 30; i++)
        {
            ulong nowUsec = startUsec + (ulong)(i * 16_666);
            double drawTick = i * (1.0 / 30.0);

            Transform3D drawn = timeline.Advance(nowUsec, drawTick);
            AssertThat(float.IsNaN(drawn.Origin.X)).IsFalse();
            lastDrawn = drawn;
        }

        // After advancing to drawTick ~ 1.0, origin should move towards 2.0
        AssertThat(lastDrawn.Origin.X).IsGreater(0.5f);
    }
}
