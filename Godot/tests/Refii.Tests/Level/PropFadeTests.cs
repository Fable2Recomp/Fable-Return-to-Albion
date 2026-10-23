namespace Refii.Tests.Level;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Level;

/// <summary>
/// The edge of the tiered prop cull (Engine_PropStreamer.CalculateTieredCullDistance): each copy
/// dithers over the last share of its radius, and a late arrival dithers in over time.
/// </summary>
[TestSuite]
public class PropFadeTests
{
    [TestCase]
    public void TestBandIsAShareOfTheRadiusAtEveryTier()
    {
        AssertFloat(Engine_PropFade.Band(15f)).IsEqualApprox(3f, 1e-5f);
        AssertFloat(Engine_PropFade.Band(20f)).IsEqualApprox(4f, 1e-5f);
        AssertFloat(Engine_PropFade.Band(30f)).IsEqualApprox(6f, 1e-5f);
        AssertFloat(Engine_PropFade.Band(40f)).IsEqualApprox(8f, 1e-5f);

        // Scaled by prop_cull_distance_scale the band keeps its share.
        AssertFloat(Engine_PropFade.Band(Engine_PropStreamer.CalculateTieredCullDistance(0.3f, 2f)))
            .IsEqualApprox(18f, 1e-5f);

        // No cull, no band.
        AssertFloat(Engine_PropFade.Band(0f)).IsEqual(0f);
        AssertFloat(Engine_PropFade.Band(-1f)).IsEqual(0f);
    }

    [TestCase]
    public void TestDistanceFactorIsWholeInsideTheBandAndNothingAtTheRadius()
    {
        const float radius = 15f;

        // A copy well inside the radius looks exactly as before.
        AssertFloat(Engine_PropFade.DistanceFactor(0f, radius)).IsEqual(1f);
        AssertFloat(Engine_PropFade.DistanceFactor(11.9f, radius)).IsEqual(1f);
        AssertFloat(Engine_PropFade.DistanceFactor(12f, radius)).IsEqualApprox(1f, 1e-5f);

        // Linear across the band, gone at the radius.
        AssertFloat(Engine_PropFade.DistanceFactor(13.5f, radius)).IsEqualApprox(0.5f, 1e-5f);
        AssertFloat(Engine_PropFade.DistanceFactor(14.25f, radius)).IsEqualApprox(0.25f, 1e-5f);
        AssertFloat(Engine_PropFade.DistanceFactor(15f, radius)).IsEqual(0f);
        AssertFloat(Engine_PropFade.DistanceFactor(40f, radius)).IsEqual(0f);
    }

    [TestCase]
    public void TestDistanceFactorFallsMonotonically()
    {
        float last = 2f;
        for (float d = 0f; d <= 50f; d += 0.25f)
        {
            float factor = Engine_PropFade.DistanceFactor(d, 40f);
            AssertBool(factor <= last).IsTrue();
            AssertBool(factor >= 0f && factor <= 1f).IsTrue();
            last = factor;
        }
    }

    [TestCase]
    public void TestNoRadiusDrawsWhole()
    {
        AssertFloat(Engine_PropFade.DistanceFactor(500f, 0f)).IsEqual(1f);
        (float end, float inverseLength, float power) = Engine_PropFade.OutBand(0f);
        AssertFloat(end).IsEqual(0f);
        AssertFloat(inverseLength).IsEqual(0f);
        AssertFloat(power).IsEqual(0f);
    }

    [TestCase]
    public void TestOutBandMatchesTheShaderAndTheFactor()
    {
        // The shader computes pow(clamp((end - d) * inverseLength, 0, 1), power).
        (float end, float inverseLength, float power) = Engine_PropFade.OutBand(20f);
        AssertFloat(end).IsEqual(20f);
        AssertFloat(inverseLength).IsEqualApprox(0.25f, 1e-5f);
        AssertFloat(power).IsEqual(1f);

        for (float d = 0f; d <= 30f; d += 0.5f)
        {
            float shader = System.MathF.Pow(System.Math.Clamp((end - d) * inverseLength, 0f, 1f), power);
            AssertFloat(shader).IsEqualApprox(Engine_PropFade.DistanceFactor(d, 20f), 1e-5f);
        }
    }

    [TestCase]
    public void TestBatchCullNeverRemovesACopyTheDitherStillShows()
    {
        // A batch node cuts at radius + spread, measured to its centre; every copy's origin is
        // within the spread of that centre, so a copy in a cut cell is at least the radius
        // away, where its factor is already zero.
        const float radius = 30f;
        const float spread = 22.6f;
        float cellCentre = radius + spread + 0.01f;
        float nearestCopy = cellCentre - spread;
        AssertFloat(Engine_PropFade.DistanceFactor(nearestCopy, radius)).IsEqual(0f);
    }

    [TestCase]
    public void TestArrivalFactorRisesOverTheArrivalTime()
    {
        AssertFloat(Engine_PropFade.ArrivalFactor(-1.0)).IsEqual(0f);
        AssertFloat(Engine_PropFade.ArrivalFactor(0.0)).IsEqual(0f);
        AssertFloat(Engine_PropFade.ArrivalFactor(Engine_PropFade.ArrivalSeconds * 0.5)).IsEqualApprox(0.5f, 1e-5f);
        AssertFloat(Engine_PropFade.ArrivalFactor(Engine_PropFade.ArrivalSeconds)).IsEqual(1f);
        AssertFloat(Engine_PropFade.ArrivalFactor(10.0)).IsEqual(1f);

        float last = -1f;
        for (double t = 0.0; t <= 1.0; t += 1.0 / 120.0)
        {
            float factor = Engine_PropFade.ArrivalFactor(t);
            AssertBool(factor >= last).IsTrue();
            last = factor;
        }
    }
}
