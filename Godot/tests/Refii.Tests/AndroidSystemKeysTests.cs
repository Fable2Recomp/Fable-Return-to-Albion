using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Game;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The phone's back gesture and the app's return, as the game takes them.</summary>
[TestSuite]
public sealed class AndroidSystemKeysTests
{
    /// <summary>Back pauses play, backs out of a menu, closes a host panel, and never reaches a game that is not up.</summary>
    [TestCase]
    public void BackIsTheConsolesBack()
    {
        AssertThat(AndroidSystemKeys.Back(gameUp: false, panel: false, playing: false, overlay: false)).IsEqual(AndroidBack.Ignore);
        AssertThat(AndroidSystemKeys.Back(gameUp: true, panel: false, playing: true, overlay: false)).IsEqual(AndroidBack.Pause);
        AssertThat(AndroidSystemKeys.Back(gameUp: true, panel: false, playing: false, overlay: false)).IsEqual(AndroidBack.BackOut);
        AssertThat(AndroidSystemKeys.Back(gameUp: true, panel: true, playing: true, overlay: false)).IsEqual(AndroidBack.ClosePanel);
        // A wheel held open by its latch is closed where it was opened.
        AssertThat(AndroidSystemKeys.Back(gameUp: true, panel: false, playing: true, overlay: true)).IsEqual(AndroidBack.Ignore);
    }

    /// <summary>
    /// At a pub game's board back pauses the board, or backs out of its dialog, whatever the guest's
    /// own play state: the board reads its own pad.
    /// </summary>
    [TestCase]
    public void BackAtABoardIsTheBoards()
    {
        AssertThat(AndroidSystemKeys.Back(gameUp: true, panel: false, playing: false, overlay: false, board: true)).IsEqual(AndroidBack.Pause);
        AssertThat(AndroidSystemKeys.Back(gameUp: true, panel: false, playing: true, overlay: false, board: true)).IsEqual(AndroidBack.Pause);
        AssertThat(AndroidSystemKeys.Back(gameUp: true, panel: false, playing: false, overlay: false, board: true, boardDialog: true)).IsEqual(AndroidBack.BackOut);
        AssertThat(AndroidSystemKeys.Back(gameUp: false, panel: false, playing: false, overlay: false, board: true)).IsEqual(AndroidBack.Ignore);
        AssertThat(AndroidSystemKeys.PauseOnReturn(wasAway: true, panel: false, playing: false, overlay: false, board: true)).IsTrue();
        AssertThat(AndroidSystemKeys.PauseOnReturn(wasAway: true, panel: false, playing: false, overlay: false, board: true, boardDialog: true)).IsFalse();
    }

    /// <summary>A press once reaches a pub game's board through its touch pad, not only the guest's pad.</summary>
    [TestCase]
    public void APressOnceReachesTheBoard()
    {
        GameInput.PressOnce(GameInput.PadStart);
        AssertThat(GamePubGames.TouchPad().Buttons & (int)GameInput.PadStart).IsEqual((int)GameInput.PadStart);
        GameInput.PressOnce(0u);
        AssertThat(GamePubGames.TouchPad().Buttons & (int)GameInput.PadStart).IsEqual(0);
    }

    /// <summary>Only play left mid-stride comes back paused, and only once for the notices of one return.</summary>
    [TestCase]
    public void ReturningPausesPlayOnce()
    {
        AssertThat(AndroidSystemKeys.PauseOnReturn(wasAway: true, panel: false, playing: true, overlay: false)).IsTrue();
        AssertThat(AndroidSystemKeys.PauseOnReturn(wasAway: false, panel: false, playing: true, overlay: false)).IsFalse();
        AssertThat(AndroidSystemKeys.PauseOnReturn(wasAway: true, panel: false, playing: false, overlay: false)).IsFalse();
        AssertThat(AndroidSystemKeys.PauseOnReturn(wasAway: true, panel: true, playing: true, overlay: false)).IsFalse();
    }

    /// <summary>A button pressed once is down at once, and a press of nothing clears it.</summary>
    [TestCase]
    public void APressOnceLetsGo()
    {
        GameInput.PressOnce(GameInput.PadStart);
        AssertThat(GameInput.PressedOnce).IsEqual(GameInput.PadStart);
        GameInput.PressOnce(0u);
        AssertThat(GameInput.PressedOnce).IsEqual(0u);
    }
}
