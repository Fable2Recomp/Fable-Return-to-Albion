using System;
using GdUnit4;
using Godot;
using Refii.Game;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Pure Android controller capture units and input/menu ownership lifecycles without native scene objects.</summary>
[TestSuite]
public sealed class GameJoypadButtonCaptureTests
{
    private const uint A = 0x1000, B = 0x2000;

    /// <summary>One brief click survives a released poll and distinct taps retain an intervening release.</summary>
    [TestCase]
    public void ShortClicksAreSampledExactlyOnce()
    {
        var capture = Capture();
        Tap(capture, 7, A);
        AssertThat(capture.PendingButtons(7)).IsEqual(A);
        AssertThat(capture.Sample(7, 0)).IsEqual(A);
        AssertThat(capture.Sample(7, 0)).IsEqual(0u);
        AssertThat(capture.Sample(7, 0)).IsEqual(0u);
        Tap(capture, 7, A); Tap(capture, 7, A);
        foreach (uint expected in new[] { A, 0u, A, 0u })
            AssertThat(capture.Sample(7, 0)).IsEqual(expected);
    }

    /// <summary>Continuous holds and Dpad states remain current; duplicate downs cannot manufacture another edge.</summary>
    [TestCase]
    public void HoldsAndInitialHeldStateAreUnchanged()
    {
        var capture = Capture();
        AssertThat(capture.Sample(7, A)).IsEqual(A);
        capture.Capture(7, A, true);
        for (int i = 0; i < 8; ++i) AssertThat(capture.Sample(7, A)).IsEqual(A);
        AssertThat(capture.PendingButtons(7)).IsEqual(0u);
        capture.Capture(7, A, false);
        AssertThat(capture.Sample(7, 0)).IsEqual(0u);
        capture.Capture(7, 1, true);
        for (int i = 0; i < 8; ++i) AssertThat(capture.Sample(7, 1)).IsEqual(1u);
        capture.SetContext(true, default(GameInputButtonRoute) with { FrontEndScreen = 2 });
        AssertThat(capture.Sample(7, 1)).IsEqual(1u);
    }

    /// <summary>Focus, real ownership changes and disconnected/reused identities cannot replay old clicks.</summary>
    [TestCase]
    public void ContextAndDeviceRetirementClearPendingClicks()
    {
        var capture = Capture();
        Tap(capture, 7, A); capture.SetContext(false, default);
        AssertThat(capture.PendingButtons(7)).IsEqual(0u);
        AssertThat(capture.Sample(7, A)).IsEqual(0u);
        Tap(capture, 7, B); capture.SetContext(true, default);
        AssertThat(capture.Sample(7, 0)).IsEqual(0u);
        Tap(capture, 7, A);
        capture.SetContext(true, default(GameInputButtonRoute) with { Modal = 24 });
        AssertThat(capture.Sample(7, 0)).IsEqual(0u);
        Tap(capture, 7, B); capture.RefreshDevices(new[] { 8 });
        capture.RefreshDevices(new[] { 7, 8 });
        AssertThat(capture.Sample(7, 0)).IsEqual(0u);
    }

    /// <summary>A changes-only identity leaves an unchanged menu's click intact and excludes highlight/frame cadence.</summary>
    [TestCase]
    public void EqualRoutingIdentityDoesNotEraseANewClick()
    {
        var capture = Capture();
        var route = new GameInputButtonRoute(true, 2, 0, uint.MaxValue, 0, 0, 0, 128);
        capture.SetContext(true, route);
        Tap(capture, 7, A); capture.SetContext(true, route with { });
        AssertThat(capture.Sample(7, 0)).IsEqual(A);
        AssertThat(capture.Sample(7, 0)).IsEqual(0u);
    }

    /// <summary>Unexpected device/button events and excessive clicks cannot grow an unlimited command backlog.</summary>
    [TestCase]
    public void CapacityAndVocabularyAreBounded()
    {
        var capture = Capture();
        capture.Capture(99, A, true); capture.Capture(7, A | B, true); capture.Capture(7, 0x400, true);
        AssertThat(capture.PendingButtons(7)).IsEqual(0u);
        for (int i = 0; i <= GameJoypadButtonCapture.MaxPendingPresses; ++i) Tap(capture, 7, A);
        AssertThat(capture.Overflows).IsEqual(1L);
        AssertThat(capture.Sample(7, 0)).IsEqual(0u);
        Tap(capture, 7, B);
        AssertThat(capture.Sample(7, 0)).IsEqual(B);
        AssertThat(capture.DeviceCount).IsEqual(2);
    }

    /// <summary>Events use all fourteen existing XInput bits and ignore controller extras rather than inventing guest mappings.</summary>
    [TestCase]
    public void ButtonEventMappingMatchesTheProductionVocabulary()
    {
        JoyButton[] buttons = { JoyButton.DpadUp, JoyButton.DpadDown, JoyButton.DpadLeft, JoyButton.DpadRight,
            JoyButton.Start, JoyButton.Back, JoyButton.LeftStick, JoyButton.RightStick, JoyButton.LeftShoulder,
            JoyButton.RightShoulder, JoyButton.A, JoyButton.B, JoyButton.X, JoyButton.Y };
        uint[] bits = { 1, 2, 4, 8, 0x10, 0x20, 0x40, 0x80, 0x100, 0x200, A, B, 0x4000, 0x8000 };
        uint aggregate = 0;
        for (int i = 0; i < buttons.Length; ++i)
        {
            AssertThat(GameInput.JoyButtonBit(buttons[i])).IsEqual(bits[i]); aggregate |= bits[i];
        }
        AssertThat(aggregate).IsEqual(GameJoypadButtonCapture.SupportedButtons);
        AssertThat(GameInput.JoyButtonBit(JoyButton.Guide)).IsEqual(0u);
    }

    /// <summary>A raw host-menu click remains masked natively and a physically held unmask cannot invent a guest edge.</summary>
    [TestCase]
    public void MaskedTapAndHeldUnmaskLifecycleDoesNotLeak()
    {
        var capture = Capture(); uint deniedBefore = 0, heldAcross = 0;
        uint Filter(uint raw, uint denied)
        {
            heldAcross |= deniedBefore & ~denied & raw;
            heldAcross &= raw; deniedBefore = denied;
            return raw & ~denied & ~heldAcross;
        }
        Tap(capture, 7, A); uint raw = capture.Sample(7, 0);
        AssertThat(raw).IsEqual(A); AssertThat(Filter(raw, A)).IsEqual(0u);
        AssertThat(Filter(capture.Sample(7, 0), 0)).IsEqual(0u);
        capture.Capture(7, A, true); raw = capture.Sample(7, A); Filter(raw, A);
        AssertThat(Filter(raw, 0)).IsEqual(0u); AssertThat(heldAcross).IsEqual(A);
        capture.Capture(7, A, false); Filter(capture.Sample(7, 0), 0);
        Tap(capture, 7, A); AssertThat(Filter(capture.Sample(7, 0), 0)).IsEqual(A);
    }

    /// <summary>The actual production menu still refuses animation-time activation and receives no delayed retry.</summary>
    [TestCase]
    public void CaptureDoesNotBypassTheRealMenuAnimationGate()
    {
        var capture = Capture(); var menu = new Engine_FrontEndMenu();
        menu.Add(new Engine_UiExpandableMenu.Item("Options", "Options") { Command = "Options" });
        menu.Model.Animating = true; Tap(capture, 7, A);
        AssertThat(capture.Sample(7, 0)).IsEqual(A); AssertThat(menu.Activate()).IsNull();
        menu.Model.Animating = false; AssertThat(capture.Sample(7, 0)).IsEqual(0u);
        Tap(capture, 7, A); AssertThat(capture.Sample(7, 0)).IsEqual(A);
        AssertThat(menu.Activate()).IsEqual("Options");
    }

    private static GameJoypadButtonCapture Capture()
    {
        var capture = new GameJoypadButtonCapture(); capture.RefreshDevices(new[] { 7, 8 }); return capture;
    }
    private static void Tap(GameJoypadButtonCapture capture, int id, uint bit)
    {
        capture.Capture(id, bit, true); capture.Capture(id, bit, false);
    }
}
