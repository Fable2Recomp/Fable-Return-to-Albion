using GdUnit4;
using Refii.Game;
using Refii.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Tests authored-distance algebra and configuration/override lifecycles without native Godot objects.</summary>
[TestSuite]
public sealed class MeshLodPolicyTests
{
    /// <summary>One configured pixel follows the existing desktop rule at real physical heights.</summary>
    [TestCase]
    public void PhysicalHeightAndUserQualityFollowDesktopFormula()
    {
        AssertThat(Engine_MeshLodPolicy.Calculate(1f, 720f, 1f)).IsEqual(1f);
        AssertThat(Engine_MeshLodPolicy.Calculate(1f, 1080f, 1f)).IsEqual(1.5f);
        AssertThat(Engine_MeshLodPolicy.Calculate(1f, 1440f, 1f)).IsEqual(2f);
        AssertThat(Engine_MeshLodPolicy.Calculate(2f, 1080f, 1f)).IsEqual(3f);
        AssertThat(Engine_MeshLodPolicy.Calculate(0f, 1080f, 1f)).IsEqual(0f);
        var settings = GameWindowSettings.FromValues(new Dictionary<string, string>
            { ["mesh_lod_pixels"] = "2", ["display_resolution"] = "640x360" }, "runtime.toml");
        // The Android window is externally sized: a desktop display_resolution must not
        // replace the real root extent when applying the shared quality setting.
        AssertThat(Engine_MeshLodPolicy.Calculate(settings.MeshLodPixels, 1080f, 1f)).IsEqual(3f);
    }

    /// <summary>Threshold and projection compensation cancel global scale at the same authored switch distance.</summary>
    [TestCase]
    public void GlobalScaleAndPhysicalWidthPreserveTheSelectionRatio()
    {
        const float key = 0.25f, distance = 200f, projectionX = 1.5f;
        float reference = 0f;
        foreach (float projectScale in new[] { 0.5f, 1f, 2f })
        {
            float selected = key / (distance * Engine_MeshLodPolicy.DistanceMultiplier(projectionX, projectScale));
            float threshold = Engine_MeshLodPolicy.NormalizedThreshold(
                Engine_MeshLodPolicy.Calculate(1f, 1080f, projectScale), 1920f);
            float ratio = selected / threshold;
            if (reference == 0f) reference = ratio;
            AssertThat(ratio).IsEqual(reference);
        }
        // The logical 1280-wide canvas would predict a different level; it is not the renderer width.
        AssertThat(Engine_MeshLodPolicy.NormalizedThreshold(1.5f, 1920f)).IsEqual(0.00078125f);
        AssertThat(Engine_MeshLodPolicy.NormalizedThreshold(1.5f, 1280f) >
            Engine_MeshLodPolicy.NormalizedThreshold(1.5f, 1920f)).IsTrue();
    }

    /// <summary>Unchanged ticks preserve live overrides, while resize, configuration and global changes reapply quality.</summary>
    [TestCase]
    public void ConfigurationAndOverrideLifecycleHasOneChangedInputPublisher()
    {
        var owner = new Engine_MeshLodPolicy();
        float actual = -1f;
        int writes = 0;
        void Publish(float value) { actual = value; writes++; }
        AssertThat(owner.Apply(1f, 1920f, 1080f, 1f, Publish)).IsTrue();
        float original = actual;
        actual = 0f; // Explicit full-detail diagnostic override.
        AssertThat(owner.Apply(1f, 1920f, 1080f, 1f, Publish)).IsFalse();
        AssertThat(actual).IsEqual(0f);
        actual = original; // Exact restore, without changing configured ownership.
        AssertThat(owner.Apply(1f, 1920f, 1080f, 1f, Publish)).IsFalse();
        AssertThat(actual).IsEqual(original);
        AssertThat(writes).IsEqual(1);
        AssertThat(owner.Apply(1f, 1080f, 1920f, 1f, Publish)).IsTrue();
        AssertThat(actual).IsEqual(1920f / 720f);
        AssertThat(owner.Apply(1f, 1200f, 1920f, 1f, Publish)).IsTrue();
        AssertThat(owner.Apply(2f, 1200f, 1920f, 1f, Publish)).IsTrue();
        AssertThat(owner.Apply(2f, 1200f, 1920f, 0.5f, Publish)).IsTrue();
        AssertThat(owner.ConfiguredPixels).IsEqual(2f);
        AssertThat(owner.ParityThreshold).IsEqual(1920f / 720f);
        AssertThat(new Engine_MeshLodPolicy().ParityThreshold).IsNull();
    }

    /// <summary>A failed setter or invalid input leaves the last good configuration retryable.</summary>
    [TestCase]
    public void FailureAndInvalidInputsNeverClaimAChangedThreshold()
    {
        var owner = new Engine_MeshLodPolicy();
        owner.Apply(1f, 1280f, 720f, 1f, _ => { });
        bool failed = false;
        try { owner.Apply(1f, 1920f, 1080f, 1f, _ => throw new InvalidOperationException("setter")); }
        catch (InvalidOperationException) { failed = true; }
        AssertThat(failed).IsTrue();
        AssertThat(owner.ParityThreshold).IsEqual(1f);
        AssertThat(owner.Apply(1f, 1920f, 1080f, 1f, _ => { })).IsTrue();
        foreach ((float pixels, float width, float height, float scale) in new[]
        {
            (float.NaN,1920f,1080f,1f),(-1f,1920f,1080f,1f),(65f,1920f,1080f,1f),
            (1f,0f,1080f,1f),(1f,1920f,0f,1f),(1f,1920f,float.PositiveInfinity,1f),
            (1f,1920f,1080f,0f),(1f,1920f,1080f,float.NegativeInfinity)
        })
        {
            bool rejected = false;
            try { owner.Apply(pixels, width, height, scale, _ => throw new Exception("must not publish")); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            AssertThat(rejected).IsTrue();
            AssertThat(owner.ParityThreshold).IsEqual(1.5f);
        }
    }

    /// <summary>Diagnostic JSON is strict, bounded and round-trips the original float exactly for restoration.</summary>
    [TestCase]
    public void ProfilerThresholdValidationAndExactRestoration()
    {
        foreach (float value in new[] { 0f, 1.5f, 2.6666667f, 256f })
        {
            using var request = JsonDocument.Parse(JsonSerializer.Serialize(new { meshLodThreshold = value }));
            AssertThat(Engine_MeshLodPolicy.ReadProfilerThreshold(request.RootElement)).IsEqual(value);
        }
        using (var request = JsonDocument.Parse("{\"lightBudget\":16}"))
            AssertThat(Engine_MeshLodPolicy.ReadProfilerThreshold(request.RootElement)).IsNull();
        foreach (string json in new[] { "null", "true", "\"1.5\"", "[]", "{}", "-0.1", "256.01", "1e100" })
        {
            using var request = JsonDocument.Parse("{\"meshLodThreshold\":" + json + "}");
            bool rejected = false;
            try { Engine_MeshLodPolicy.ReadProfilerThreshold(request.RootElement); }
            catch (JsonException) { rejected = true; }
            AssertThat(rejected).IsTrue();
        }
    }
}
