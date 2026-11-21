namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;
using Refii.Titles.Fable2;

/// <summary>
/// A short fan is slid so the cards shown sit where the full six would. Pure: the offset is
/// arithmetic over the authored <see cref="Engine_FrontEndSaveCards.Fan"/> table.
/// </summary>
[TestSuite]
public class SaveFanCentreTests
{
    private static float Mid(int first, int count) =>
        (Engine_FrontEndSaveCards.Fan[first].X + Engine_FrontEndSaveCards.Fan[first + count - 1].X) * 0.5f;

    [TestCase]
    public void TheFullFanIsNotMoved()
    {
        AssertThat(Engine_FrontEndSaveCards.ArcCentreOffset(0, 6)).IsEqual(0f);
    }

    [TestCase]
    public void AShortFanIsCentredWhereTheFullOneIs()
    {
        float full = Mid(0, 6);
        for (int first = 0; first < 6; first++)
        {
            for (int count = 1; first + count <= 6; count++)
            {
                float offset = Engine_FrontEndSaveCards.ArcCentreOffset(first, count);
                AssertThat(System.MathF.Abs(Mid(first, count) + offset - full) < 0.001f)
                    .OverrideFailureMessage($"{count} card(s) from slot {first + 1}: offset {offset}").IsTrue();
            }
        }
    }

    [TestCase]
    public void FiveSavesFromSlotOneSlideRight()
    {
        // Slots 1..5 span -335..100 (mid -117.5); the full fan's mid is -67.5.
        AssertThat(System.MathF.Abs(Engine_FrontEndSaveCards.ArcCentreOffset(0, 5) - 50f) < 0.001f).IsTrue();
    }

    [TestCase]
    public void NothingShownOrOutOfRangeMeansNoSlide()
    {
        AssertThat(Engine_FrontEndSaveCards.ArcCentreOffset(0, 0)).IsEqual(0f);
        AssertThat(Engine_FrontEndSaveCards.ArcCentreOffset(2, 6)).IsEqual(0f);
    }
}
