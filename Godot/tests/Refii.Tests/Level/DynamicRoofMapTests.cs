namespace Refii.Tests.Level;

using System;
using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Level;

[TestSuite]
public class DynamicRoofMapTests
{
    [TestCase]
    public void TestHysteresisPreventsMapRecomputationOnCameraRotation()
    {
        var map = new Engine_DynamicRoofMap();
        var initialEye = new Vector2(100f, 200f);

        // First call should initialize and compute
        bool first = map.Update(initialEye);
        AssertThat(first).IsTrue();
        Vector2 initialCentre = map.Centre;

        // Camera swinging / orbiting by a few metres around the character (<= 24m)
        bool orbit1 = map.Update(initialEye + new Vector2(4f, -3f));
        AssertThat(orbit1).IsFalse();
        AssertThat(map.Centre).IsEqual(initialCentre);

        bool orbit2 = map.Update(initialEye + new Vector2(-6f, 5f));
        AssertThat(orbit2).IsFalse();
        AssertThat(map.Centre).IsEqual(initialCentre);

        bool orbit3 = map.Update(initialEye + new Vector2(12f, 12f));
        AssertThat(orbit3).IsFalse();
        AssertThat(map.Centre).IsEqual(initialCentre);
    }

    [TestCase]
    public void TestLargeDisplacementTriggersRecomputation()
    {
        var map = new Engine_DynamicRoofMap();
        var eye1 = new Vector2(100f, 200f);
        map.Update(Array.Empty<Engine_ClassicRoofs.Roof>(), eye1);

        // Moving 35 metres away (> 24m hysteresis threshold)
        var eye2 = eye1 + new Vector2(35f, 0f);
        bool moved = map.Update(Array.Empty<Engine_ClassicRoofs.Roof>(), eye2);
        AssertThat(moved).IsTrue();
        AssertThat(map.Centre.X).IsGreater(100f);
    }

    [TestCase]
    public void TestRoofRasterizationCoversArea()
    {
        var map = new Engine_DynamicRoofMap();
        var eye = new Vector2(0f, 0f);

        // Define a rectangular roof around the origin:
        // Godot (x, z) = (-guest_x, guest_y)
        // Let's create a 10m x 10m roof at height Top = 15f
        // guest coords: x in [-5, 5], y in [-5, 5]
        var roof = new Engine_ClassicRoofs.Roof
        {
            C0 = new Vector2(-5f, -5f),
            C1 = new Vector2(5f, -5f),
            C2 = new Vector2(5f, 5f),
            C3 = new Vector2(-5f, 5f),
            Top = 15f,
        };

        bool computed = map.Update(new[] { roof }, eye);
        AssertThat(computed).IsTrue();
        AssertThat(map.HasRoofs).IsTrue();

        // Sample centre of the map (index for (128, 128))
        int centreIdx = (128 * Engine_DynamicRoofMap.Size + 128) * 2;
        AssertThat(map.Pixels[centreIdx]).IsEqual(15f);
        AssertThat(map.Pixels[centreIdx + 1]).IsEqual(1f);

        // Sample far corner (0, 0) which is 80m away from centre - should be empty
        int cornerIdx = 0;
        AssertThat(map.Pixels[cornerIdx]).IsEqual(0f);
        AssertThat(map.Pixels[cornerIdx + 1]).IsEqual(0f);
    }
}
