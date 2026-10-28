using GdUnit4;
using Godot;
using Refii.Game;
using System;
using System.Collections.Generic;
using System.Text.Json;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Tests read-only drawn-camera mapping and unavailable/transition states without native nodes.</summary>
[TestSuite]
public sealed class GameCameraTelemetryTests
{
    /// <summary>The inverse guest rotation and actual region reflection keep height separate from forward.</summary>
    [TestCase]
    public void GuestAndRegionAxesFollowTheProductionMap()
    {
        Vector3 guest = new(135.1f, 254.6f, 39.9f);
        Vector3 global = GameWorld.GuestToGodot(guest);
        var region = new Transform3D(Basis.Identity.Scaled(new Vector3(-1f, 1f, 1f)), Vector3.Zero);
        var at = new Transform3D(Basis.Identity, global);
        using JsonDocument snapshot = JsonDocument.Parse(JsonSerializer.Serialize(GameCameraTelemetry.Describe(at, 70f, region, true)));
        JsonElement root = snapshot.RootElement;
        AssertThat(root.GetProperty("regionAvailable").GetBoolean()).IsTrue();
        Near(Component(root, "guestWorld", "position", "x"), guest.X);
        Near(Component(root, "guestWorld", "position", "y"), guest.Y);
        Near(Component(root, "guestWorld", "position", "z"), guest.Z);
        Near(Component(root, "regionLocal", "position", "x"), guest.X);
        Near(Component(root, "regionLocal", "position", "y"), guest.Z);
        Near(Component(root, "regionLocal", "position", "z"), guest.Y);
        Near(Component(root, "global", "forward", "z"), -1d);
        Near(Component(root, "guestWorld", "forward", "y"), -1d);
        Near(Component(root, "regionLocal", "right", "x"), -1d);
    }

    /// <summary>Translation affects positions alone; inverse rotated/nonuniform basis maps directions independently.</summary>
    [TestCase]
    public void AffineRegionMappingDoesNotTranslateOrScaleUnitDirections()
    {
        var basis = new Basis(Vector3.Up, Mathf.Pi / 2f).Scaled(new Vector3(2f, 3f, -4f));
        var region = new Transform3D(basis, new Vector3(100f, 80f, -40f));
        Vector3 local = new(7f, 2f, 9f);
        var at = new Transform3D(new Basis(Vector3.Up, 0.37f), region * local);
        using JsonDocument first = JsonDocument.Parse(JsonSerializer.Serialize(GameCameraTelemetry.Describe(at, 65f, region, true)));
        Transform3D translatedRegion = region;
        translatedRegion.Origin += new Vector3(500f, -300f, 40f);
        Transform3D translatedCamera = at;
        translatedCamera.Origin += new Vector3(500f, -300f, 40f);
        using JsonDocument second = JsonDocument.Parse(JsonSerializer.Serialize(GameCameraTelemetry.Describe(translatedCamera, 65f, translatedRegion, true)));
        foreach (string axis in new[] { "x", "y", "z" })
        {
            Near(Component(first.RootElement, "regionLocal", "position", axis), axis == "x" ? 7 : axis == "y" ? 2 : 9);
            Near(Component(first.RootElement, "regionLocal", "forward", axis), Component(second.RootElement, "regionLocal", "forward", axis));
            Near(Component(first.RootElement, "regionLocal", "right", axis), Component(second.RootElement, "regionLocal", "right", axis));
        }
        foreach (string direction in new[] { "forward", "right" })
        {
            double x = Component(first.RootElement, "regionLocal", direction, "x");
            double y = Component(first.RootElement, "regionLocal", direction, "y");
            double z = Component(first.RootElement, "regionLocal", direction, "z");
            Near(x * x + y * y + z * z, 1d);
        }
    }

    /// <summary>Off-world and absent/invalid camera states stay explicit instead of inventing steering axes.</summary>
    [TestCase]
    public void OffWorldAndInvalidTransformsHaveNoFabricatedLocalAxes()
    {
        AssertThat(GameCameraTelemetry.Describe(null, null, null, false) is null).IsTrue();
        var invalid = new Transform3D(Basis.Identity, new Vector3(float.NaN, 0f, 0f));
        AssertThat(GameCameraTelemetry.Describe(invalid, 70f, null, false) is null).IsTrue();
        AssertThat(GameCameraTelemetry.Describe(new Transform3D(new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero), Vector3.Zero), 70f, null, false) is null).IsTrue();
        var camera = Transform3D.Identity;
        using JsonDocument off = JsonDocument.Parse(JsonSerializer.Serialize(GameCameraTelemetry.Describe(camera, float.NaN, null, false)));
        AssertThat(off.RootElement.GetProperty("guestWorld").ValueKind == JsonValueKind.Null).IsTrue();
        AssertThat(off.RootElement.GetProperty("regionLocal").ValueKind == JsonValueKind.Null).IsTrue();
        AssertThat(off.RootElement.GetProperty("fovDegrees").ValueKind == JsonValueKind.Null).IsTrue();
        AssertThat(off.RootElement.GetProperty("regionAvailable").GetBoolean()).IsFalse();
        var singular = new Transform3D(new Basis(Vector3.Zero, Vector3.Up, Vector3.Back), Vector3.Zero);
        using JsonDocument badRegion = JsonDocument.Parse(JsonSerializer.Serialize(GameCameraTelemetry.Describe(camera, 70f, singular, true)));
        AssertThat(badRegion.RootElement.GetProperty("regionAvailable").GetBoolean()).IsFalse();
        AssertThat(badRegion.RootElement.GetProperty("guestWorld").ValueKind == JsonValueKind.Null).IsTrue();
        AssertThat(badRegion.RootElement.GetProperty("regionError").GetString()).IsEqual("non-finite or singular region transform");
    }

    /// <summary>Snapshot JSON keeps useful six-decimal numeric heading values and leaves source transforms untouched.</summary>
    [TestCase]
    public void NumericPrecisionAndSnapshotLifecycleRemainReadOnly()
    {
        var region = new Transform3D(Basis.Identity.Scaled(new Vector3(-1f, 1f, 1f)), Vector3.Zero);
        var before = new Transform3D(new Basis(Vector3.Up, 0.1234567f), new Vector3(-1.2345678f, 40f, 250f));
        Transform3D original = before;
        Dictionary<string, object?>? captured = GameCameraTelemetry.Describe(before, 71.1234567f, region, true);
        before.Origin = Vector3.Zero;
        using JsonDocument first = JsonDocument.Parse(JsonSerializer.Serialize(captured));
        JsonElement x = first.RootElement.GetProperty("global").GetProperty("position").GetProperty("x");
        AssertThat(x.ValueKind == JsonValueKind.Number).IsTrue();
        AssertThat(x.GetDouble()).IsEqual(Math.Round((double)original.Origin.X, 6));
        Near(Component(first.RootElement, "global", "forward", "x"), -Math.Sin(0.1234567d));
        using JsonDocument missing = JsonDocument.Parse(JsonSerializer.Serialize(GameCameraTelemetry.Describe(original, 71f, null, false)));
        AssertThat(missing.RootElement.GetProperty("regionLocal").ValueKind == JsonValueKind.Null).IsTrue();
        using JsonDocument reloaded = JsonDocument.Parse(JsonSerializer.Serialize(GameCameraTelemetry.Describe(original, 71f, region, true)));
        AssertThat(reloaded.RootElement.GetProperty("regionAvailable").GetBoolean()).IsTrue();
    }

    /// <summary>Reads one documented numeric component from its serialized public debug schema.</summary>
    private static double Component(JsonElement root, string frame, string vector, string axis) =>
        root.GetProperty(frame).GetProperty(vector).GetProperty(axis).GetDouble();

    /// <summary>Checks six-decimal telemetry without treating float transform arithmetic as exact decimal arithmetic.</summary>
    private static void Near(double actual, double expected) => AssertThat(Math.Abs(actual - expected) < 0.0001d).IsTrue();
}
