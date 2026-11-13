using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The debug channel's `lightpairs` (AndroidLightPairCensus): the pairing rule and the light boxes.</summary>
[TestSuite]
public sealed class AndroidLightPairCensusTests
{
    private static Aabb Box(float x, float z, float half = 1f) =>
        new(new Vector3(x - half, -half, z - half), new Vector3(half, half, half) * 2f);

    private static AndroidLightPairCensus.LightBox Omni(float x, float z, float range, bool live = true, uint mask = 0xFFFFFFFF) =>
        new("omni", false, AndroidLightPairCensus.OmniBox(new Transform3D(Basis.Identity, new Vector3(x, 0f, z)), range), mask, live);

    /// <summary>An instance pairs with a light whose box meets its own, per type, and the loops' calls follow.</summary>
    [TestCase]
    public void PairsByBoxAndCountsTheLoops()
    {
        var lights = new[]
        {
            Omni(0f, 0f, 5f),
            Omni(20f, 0f, 2f, live: false),
            new AndroidLightPairCensus.LightBox("spot", true,
                AndroidLightPairCensus.SpotBox(new Transform3D(Basis.Identity, new Vector3(0f, 0f, 3f)), 4f, 30f), 0xFFFFFFFF, true),
        };
        var instances = new[]
        {
            new AndroidLightPairCensus.InstanceBox(Box(1f, 1f), 1u, 2, false),   // both lamps near the origin
            new AndroidLightPairCensus.InstanceBox(Box(20f, 0f), 1u, 1, false),  // the black lamp only
            new AndroidLightPairCensus.InstanceBox(Box(60f, 0f), 1u, 3, true),   // nothing reaches it
        };

        AndroidLightPairCensus.Tally t = AndroidLightPairCensus.Count(lights, instances);
        AssertThat(t.Instances).IsEqual(3);
        AssertThat(t.Surfaces).IsEqual(6);
        AssertThat(t.Batches).IsEqual(1);
        AssertThat(t.WithOmni).IsEqual(2);
        AssertThat(t.WithSpot).IsEqual(1);
        AssertThat(t.WithAny).IsEqual(2);
        AssertThat(t.WithLive).IsEqual(1);
        AssertThat(t.OmniPairs).IsEqual(2);
        AssertThat(t.SpotPairs).IsEqual(1);
        AssertThat(t.LivePairs).IsEqual(2);
        AssertThat(t.PerLight).ContainsExactly(1, 1, 1);
        // Surfaces x (omni + spot): 2 x 2 + 1 x 1 + 3 x 0; with the fillers' floor of one per loop: 2 x 2 + 1 x 2 + 3 x 2.
        AssertThat(t.LoopCalls).IsEqual(5L);
        AssertThat(t.LoopCallsWithFillers).IsEqual(12L);
        AssertThat(t.OmniHistogram).ContainsExactly(1, 2, 0, 0, 0);
    }

    /// <summary>A light's cull mask must meet the instance's layers, and no instance takes more than the limit of a type.</summary>
    [TestCase]
    public void HonoursTheCullMaskAndThePerTypeLimit()
    {
        var lights = new AndroidLightPairCensus.LightBox[10];
        for (int i = 0; i < lights.Length; i++) lights[i] = Omni(0f, 0f, 10f, mask: i == 0 ? 2u : 1u);
        var instances = new[] { new AndroidLightPairCensus.InstanceBox(Box(0f, 0f), 1u, 1, false) };

        AndroidLightPairCensus.Tally t = AndroidLightPairCensus.Count(lights, instances);
        AssertThat(t.OmniPairs).IsEqual(AndroidLightPairCensus.MaxPerType);
        AssertThat(t.PerLight[0]).IsEqual(0);
        AssertThat(t.OmniHistogram[4]).IsEqual(1);
    }

    /// <summary>The boxes are Godot's: an omni's range cube, a narrow spot's cone down -Z, a wide spot's whole cube.</summary>
    [TestCase]
    public void BuildsGodotsLightBoxes()
    {
        var at = new Transform3D(Basis.Identity, new Vector3(1f, 2f, 3f));
        Aabb omni = AndroidLightPairCensus.OmniBox(at, 4f);
        AssertThat(omni.Position).IsEqual(new Vector3(-3f, -2f, -1f));
        AssertThat(omni.Size).IsEqual(new Vector3(8f, 8f, 8f));

        Aabb spot = AndroidLightPairCensus.SpotBox(Transform3D.Identity, 10f, 45f);
        AssertThat(spot.Position.Z).IsEqual(-10f);
        AssertThat(spot.End.Z).IsEqual(0f);
        AssertThat(Mathf.IsEqualApprox(spot.Size.X, 20f)).IsTrue();

        Aabb wide = AndroidLightPairCensus.SpotBox(Transform3D.Identity, 10f, 120f);
        AssertThat(wide.Size).IsEqual(new Vector3(20f, 20f, 20f));
    }

    /// <summary>A light leaves the buffer when hidden or when the camera is past the end of its fade.</summary>
    [TestCase]
    public void DropsLightsPastTheirFade()
    {
        AssertThat(AndroidLightPairCensus.InBuffer(true, false, 0f, 0f, 500f)).IsTrue();
        AssertThat(AndroidLightPairCensus.InBuffer(false, false, 0f, 0f, 1f)).IsFalse();
        AssertThat(AndroidLightPairCensus.InBuffer(true, true, 30f, 15f, 44f)).IsTrue();
        AssertThat(AndroidLightPairCensus.InBuffer(true, true, 30f, 15f, 46f)).IsFalse();
    }
}
