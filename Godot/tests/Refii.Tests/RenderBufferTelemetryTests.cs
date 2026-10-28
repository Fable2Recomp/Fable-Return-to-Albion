using System;
using System.Text.Json;
using GdUnit4;
using Refii.Lighting;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Checks render-thread publication and title/world/resize/retirement lifecycles without native GUI objects.</summary>
[TestSuite]
public class RenderBufferTelemetryTests
{
    /// <summary>No renderer observation exists until valid dimensions have actually been captured.</summary>
    [TestCase]
    public void StartsUnavailableAndRejectsInvalidObservations()
    {
        var telemetry = new Engine_RenderBufferTelemetry();
        AssertThat(telemetry.Latest).IsNull();
        DateTime utc = DateTime.UtcNow;
        AssertThat(telemetry.Publish(0, 720, 3120, 1440, 1, utc)).IsFalse();
        AssertThat(telemetry.Publish(1560, -1, 3120, 1440, 1, utc)).IsFalse();
        AssertThat(telemetry.Publish(1560, 720, 0, 1440, 1, utc)).IsFalse();
        AssertThat(telemetry.Publish(1560, 720, 3120, -1, 1, utc)).IsFalse();
        AssertThat(telemetry.Publish(1560, 720, 3120, 1440, 0, utc)).IsFalse();
        AssertThat(telemetry.Publish(1560, 720, 3120, 1440, 1, DateTime.Now)).IsFalse();
        AssertThat(telemetry.Latest).IsNull();
    }

    /// <summary>The reported internal size is independent of the logical canvas and target size.</summary>
    [TestCase]
    public void MeasurementsAreDetachedAndSerializeWithoutNativeReferences()
    {
        var telemetry = new Engine_RenderBufferTelemetry();
        DateTime utc = DateTime.UtcNow;
        AssertThat(telemetry.Publish(1560, 720, 3120, 1440, 1, utc)).IsTrue();
        var first = telemetry.Latest!;
        string saved = JsonSerializer.Serialize(first);
        telemetry.Publish(2090, 964, 3120, 1440, 1, utc.AddSeconds(1));
        AssertThat(JsonSerializer.Serialize(first)).IsEqual(saved);
        AssertThat(first.InternalWidth).IsEqual(1560);
        AssertThat(first.TargetWidth).IsEqual(3120);
        AssertThat(first.Sequence).IsEqual(1L);
        AssertThat(telemetry.Latest!.Sequence).IsEqual(2L);
        AssertThat(telemetry.Latest.InternalHeight).IsEqual(964);
    }

    /// <summary>A title, world, scale change, retirement and new world never reuse a retired observation.</summary>
    [TestCase]
    public void WorldResizeRetirementAndReentryLifecycle()
    {
        var telemetry = new Engine_RenderBufferTelemetry();
        AssertThat(telemetry.Latest).IsNull();
        DateTime utc = DateTime.UtcNow;
        telemetry.Publish(3120, 1440, 3120, 1440, 1, utc);
        var world = telemetry.Latest!;
        telemetry.Publish(1560, 720, 3120, 1440, 1, utc.AddSeconds(1));
        AssertThat(telemetry.Latest!.InternalWidth).IsEqual(1560);
        telemetry.Clear();
        AssertThat(telemetry.Latest).IsNull();
        AssertThat(world.InternalWidth).IsEqual(3120);
        telemetry.Publish(960, 540, 1920, 1080, 1, utc.AddSeconds(2));
        AssertThat(telemetry.Latest!.Sequence).IsEqual(3L);
        AssertThat(telemetry.Latest.TargetHeight).IsEqual(1080);
        telemetry.Publish(0, 0, 0, 0, 0, utc.AddSeconds(3));
        AssertThat(telemetry.Latest).IsNull();
    }
}
