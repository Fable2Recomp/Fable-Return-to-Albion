namespace Refii.Tests.Host;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;

/// <summary>
/// A mouse button is a pad press from a press made while the cursor was captured until its
/// release: an overlay freeing the cursor under a held lock must not let the lock go and press it
/// again, and a click that began over a free cursor is never a press.
/// </summary>
[TestSuite]
public class EnginePlayButtonsTests
{
    private const int Right = 2;

    [TestCase]
    public void APressInPlayHoldsWhileAnOverlayFreesTheCursor()
    {
        var buttons = new Engine_PlayButtons(10);
        buttons.Update(Right, false, true);
        buttons.Update(Right, true, true);
        AssertThat(buttons.Held(Right)).IsTrue();
        buttons.Update(Right, true, false); // the spell blade frees the cursor
        AssertThat(buttons.Held(Right)).IsTrue();
        buttons.Update(Right, true, true);  // and closes: no new press, the same hold
        AssertThat(buttons.Held(Right)).IsTrue();
        buttons.Update(Right, false, true);
        AssertThat(buttons.Held(Right)).IsFalse();
    }

    [TestCase]
    public void AClickOverAFreeCursorIsNeverAPress()
    {
        var buttons = new Engine_PlayButtons(10);
        buttons.Update(Right, true, false); // a menu click
        AssertThat(buttons.Held(Right)).IsFalse();
        buttons.Update(Right, true, true);  // the menu closes under it and the cursor is captured
        AssertThat(buttons.Held(Right)).IsFalse();
        buttons.Update(Right, false, true);
        buttons.Update(Right, true, true);  // a new press in play
        AssertThat(buttons.Held(Right)).IsTrue();
    }

    [TestCase]
    public void LosingFocusEndsTheHoldUntilANewPress()
    {
        var buttons = new Engine_PlayButtons(10);
        buttons.Update(Right, true, true);
        buttons.ReleaseAll();
        AssertThat(buttons.Held(Right)).IsFalse();
        buttons.Update(Right, true, true);  // still down when focus returns
        AssertThat(buttons.Held(Right)).IsFalse();
        buttons.Update(Right, false, true);
        buttons.Update(Right, true, true);
        AssertThat(buttons.Held(Right)).IsTrue();
    }

    [TestCase]
    public void ButtonsAreIndependentAndOutOfRangeIsNeverHeld()
    {
        var buttons = new Engine_PlayButtons(10);
        buttons.Update(1, true, true);
        AssertThat(buttons.Held(1)).IsTrue();
        AssertThat(buttons.Held(Right)).IsFalse();
        buttons.Update(42, true, true);
        AssertThat(buttons.Held(42)).IsFalse();
        AssertThat(buttons.Held(-1)).IsFalse();
    }
}
