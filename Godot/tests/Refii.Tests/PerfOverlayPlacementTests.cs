using Godot;
using GdUnit4;
using Refii.Game;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Where the F5 readout's safe-area offset puts the panel, on phones and multi-monitor desktops.</summary>
[TestSuite]
public sealed class PerfOverlayPlacementTests
{
    /// <summary>A phone's cutout in the left edge pushes the panel past it.</summary>
    [TestCase]
    public void PhoneCutoutMovesThePanelPastIt()
    {
        var window = new Rect2I(0, 0, 2340, 1080);
        var safe = new Rect2I(80, 0, 2260, 1080);
        AssertThat(GamePerfOverlay.SafeAreaOffset(safe, window)).IsEqual(new Vector2I(80, 0));
    }

    /// <summary>A screen with no cutout leaves the panel at the window's corner.</summary>
    [TestCase]
    public void WholeScreenSafeAreaLeavesThePanelAtTheCorner()
    {
        var window = new Rect2I(0, 0, 2560, 1440);
        AssertThat(GamePerfOverlay.SafeAreaOffset(window, window)).IsEqual(Vector2I.Zero);
    }

    /// <summary>A fullscreen window on a primary screen placed at (2560, 1440) keeps the panel on screen.</summary>
    [TestCase]
    public void PrimaryScreenAwayFromTheDesktopOriginKeepsThePanelInTheWindow()
    {
        var window = new Rect2I(2560, 1440, 2560, 1440);
        var safe = new Rect2I(2560, 1440, 2560, 1440);
        AssertThat(GamePerfOverlay.SafeAreaOffset(safe, window)).IsEqual(Vector2I.Zero);
    }

    /// <summary>A window on another monitor than the safe area's ignores it.</summary>
    [TestCase]
    public void WindowOnAnotherMonitorIgnoresTheSafeArea()
    {
        var window = new Rect2I(0, 1440, 2560, 1440);
        var safe = new Rect2I(2560, 1440, 2560, 1440);
        AssertThat(GamePerfOverlay.SafeAreaOffset(safe, window)).IsEqual(Vector2I.Zero);
    }

    /// <summary>A desktop panel along the top of the screen moves a fullscreen window's readout below it.</summary>
    [TestCase]
    public void TopTaskbarMovesThePanelBelowIt()
    {
        var window = new Rect2I(2560, 1440, 2560, 1440);
        var safe = new Rect2I(2560, 1484, 2560, 1396);
        AssertThat(GamePerfOverlay.SafeAreaOffset(safe, window)).IsEqual(new Vector2I(0, 44));
    }

    /// <summary>An empty safe area or window leaves the panel at the corner.</summary>
    [TestCase]
    public void EmptyRectanglesLeaveThePanelAtTheCorner()
    {
        var window = new Rect2I(0, 0, 1280, 720);
        AssertThat(GamePerfOverlay.SafeAreaOffset(new Rect2I(0, 0, 0, 0), window)).IsEqual(Vector2I.Zero);
        AssertThat(GamePerfOverlay.SafeAreaOffset(window, new Rect2I(0, 0, 0, 0))).IsEqual(Vector2I.Zero);
    }
}
