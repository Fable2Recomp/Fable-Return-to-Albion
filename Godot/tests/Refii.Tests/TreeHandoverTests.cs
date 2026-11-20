using GdUnit4;
using Refii.Level;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`tree_handover_scale`: a tree's main and `_LOD1` models trade places nearer.</summary>
[TestSuite]
public sealed class TreeHandoverTests
{
    // A tree with L = 80 m and a far reach of 1024 m, in a batch that spreads 10 m (Engine_PropDrawRange.TreeBand).
    private static readonly float[] MainBand = { 0f, 0f, 1f, 80f, 1f / 10f, 2f };
    private static readonly float[] MainStated = { 0f, 0f, 90f, 0f, 0f };
    private static readonly float[] LodBand = { 70f, 1f / 10f, 1f, 1024f, 1f / 128f, 1f };
    private static readonly float[] LodStated = { 60f, 0f, 1034f, 0f, 0f };

    [TestCase]
    public void MainModelEndsAtTheScaledDistance()
    {
        (float[] band, float[] stated) = Engine_TreeReach.ScaleHandover(MainBand, MainStated, 0.5f);

        AssertThat(band[3]).IsEqual(40f);
        AssertThat(band[4]).IsEqual(1f / 5f);
        AssertThat(stated[2]).IsEqual(50f);
        AssertThat(MainBand[3]).IsEqual(80f);
    }

    [TestCase]
    public void FarModelStartsWhereTheMainModelNowFades()
    {
        (float[] band, float[] stated) = Engine_TreeReach.ScaleHandover(LodBand, LodStated, 0.5f);

        // The fade-in still runs over [0.875, 1] of the scaled distance, and the far end is untouched.
        AssertThat(band[0]).IsEqual(35f);
        AssertThat(band[0] + (1f / band[1])).IsEqual(40f);
        AssertThat(band[3]).IsEqual(1024f);
        AssertThat(stated[0]).IsEqual(25f);
        AssertThat(stated[2]).IsEqual(1034f);
    }

    [TestCase]
    public void AShortHandoverIsNotBroughtNearerThanTheFloor()
    {
        // L = 30 m, under the 40 m floor: untouched. L = 60 m at half would be 30 m: held at 40 m.
        float[] nearMain = { 0f, 0f, 1f, 30f, 1f / 3.75f, 2f };
        float[] nearStated = { 0f, 0f, 40f, 0f, 0f };
        (float[] band, float[] stated) = Engine_TreeReach.ScaleHandover(nearMain, nearStated, 0.5f);
        AssertThat(band).IsSame(nearMain);
        AssertThat(stated).IsSame(nearStated);

        float[] midMain = { 0f, 0f, 1f, 60f, 1f / 7.5f, 2f };
        float[] midStated = { 0f, 0f, 70f, 0f, 0f };
        (band, stated) = Engine_TreeReach.ScaleHandover(midMain, midStated, 0.5f);
        AssertThat(band[3]).IsEqualApprox(Engine_TreeReach.HandoverFloorMetres, 0.001f);
        AssertThat(stated[2]).IsEqualApprox(Engine_TreeReach.HandoverFloorMetres + 10f, 0.001f);

        // The far model of the same tree starts where that main model now fades.
        float[] midLod = { 52.5f, 1f / 7.5f, 1f, 1024f, 1f / 128f, 1f };
        float[] midLodStated = { 42.5f, 0f, 1034f, 0f, 0f };
        (band, _) = Engine_TreeReach.ScaleHandover(midLod, midLodStated, 0.5f);
        AssertThat(band[0] + (1f / band[1])).IsEqualApprox(Engine_TreeReach.HandoverFloorMetres, 0.001f);
    }

    [TestCase]
    public void CapScalesTheHandOverBeforeItCapsTheReach()
    {
        // L = 300 m, reach 200 m, half the hand-over: the far model fades in over [131.25, 150] m and,
        // scaled first, ends at the 200 m reach. Capped first it would end at 300 / 0.875, about 343 m.
        float[] lodBand = { 262.5f, 1f / 37.5f, 1f, 1024f, 1f / 128f, 1f };
        float[] lodStated = { 252.5f, 0f, 1034f, 0f, 0f };
        (float[] band, float[] stated) = Engine_TreeReach.Cap(lodBand, lodStated, 0.5f, 200f);

        AssertThat(band[0]).IsEqualApprox(131.25f, 0.001f);
        AssertThat(band[0] + (1f / band[1])).IsEqualApprox(150f, 0.001f);
        AssertThat(band[3]).IsEqualApprox(200f, 0.001f);
        AssertThat(band[4]).IsEqualApprox(1f / 25f, 0.0001f);
        AssertThat(stated[0]).IsEqualApprox(121.25f, 0.001f);
        AssertThat(stated[2]).IsEqualApprox(210f, 0.001f);

        // The main model ends at 150 m, inside the reach, so the cap leaves it.
        float[] mainBand = { 0f, 0f, 1f, 300f, 1f / 37.5f, 2f };
        float[] mainStated = { 0f, 0f, 310f, 0f, 0f };
        (band, stated) = Engine_TreeReach.Cap(mainBand, mainStated, 0.5f, 200f);
        AssertThat(band[3]).IsEqualApprox(150f, 0.001f);
        AssertThat(stated[2]).IsEqualApprox(160f, 0.001f);

        // A quarter: the hand-over at 75 m, and the far model still ends at the reach.
        (band, _) = Engine_TreeReach.Cap(lodBand, lodStated, 0.25f, 200f);
        AssertThat(band[0] + (1f / band[1])).IsEqualApprox(75f, 0.001f);
        AssertThat(band[3]).IsEqualApprox(200f, 0.001f);
    }

    [TestCase]
    public void OneKeepsTheStatedDistance()
    {
        (float[] band, float[] stated) = Engine_TreeReach.ScaleHandover(MainBand, MainStated, 1f);

        AssertThat(band).IsSame(MainBand);
        AssertThat(stated).IsSame(MainStated);
    }

    [TestCase]
    public void ATreeWithNoFarModelIsLeftAlone()
    {
        float[] band = { 0f, 0f, 1f, 300f, 1f / 37.5f, 1f };
        float[] stated = { 0f, 0f, 310f, 0f, 0f };

        (float[] scaled, float[] range) = Engine_TreeReach.ScaleHandover(band, stated, 0.5f);

        AssertThat(scaled).IsSame(band);
        AssertThat(range).IsSame(stated);
    }
}
