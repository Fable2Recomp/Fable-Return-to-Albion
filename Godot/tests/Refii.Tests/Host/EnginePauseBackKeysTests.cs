namespace Refii.Tests.Host;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;

/// <summary>
/// Escape is pause in play and back in a menu, and a held press keeps the meaning it started with:
/// the pause book opening under a held Escape must not see it as B, and a menu closing under one
/// must not see it as Start.
/// </summary>
[TestSuite]
public class EnginePauseBackKeysTests
{
    [TestCase]
    public void EscapeHeldWhileTheBookOpensIsNeverBack()
    {
        var keys = new Engine_PauseBackKeys();
        AssertThat(keys.Map(true, false, false)).IsEqual((false, false));
        AssertThat(keys.Map(true, true, true)).IsEqual((true, false));   // pressed in play: Start
        AssertThat(keys.Map(false, true, true)).IsEqual((false, false)); // the book is up, still held
        AssertThat(keys.Map(false, true, true)).IsEqual((false, false));
        AssertThat(keys.Map(false, false, false)).IsEqual((false, false)); // released
        AssertThat(keys.Map(false, true, true)).IsEqual((false, true));  // a new press in the book: B
    }

    [TestCase]
    public void EscapeHeldWhileAMenuClosesIsNeverStart()
    {
        var keys = new Engine_PauseBackKeys();
        AssertThat(keys.Map(false, false, false)).IsEqual((false, false));
        AssertThat(keys.Map(false, true, true)).IsEqual((false, true));  // pressed in the book: B
        AssertThat(keys.Map(true, true, true)).IsEqual((false, false));  // back in play, still held
        AssertThat(keys.Map(true, false, false)).IsEqual((false, false));
        AssertThat(keys.Map(true, true, true)).IsEqual((true, false));   // a new press in play: Start
    }

    [TestCase]
    public void TabIsStartInBothModes()
    {
        var keys = new Engine_PauseBackKeys();
        keys.Map(false, false, false);
        AssertThat(keys.Map(false, true, false)).IsEqual((true, false)); // Tab in the book resumes
        keys.Map(true, false, false);
        AssertThat(keys.Map(true, true, false)).IsEqual((true, false));
        keys.Map(true, false, false);
        AssertThat(keys.Map(true, false, true)).IsEqual((false, false)); // Backspace alone in play: nothing
    }
}
