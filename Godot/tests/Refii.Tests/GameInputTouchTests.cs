using GdUnit4;
using Godot;
using Refii.Game;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>A touchscreen as an input device: the UI's touch mode and the wheel's levels its controls follow.</summary>
[TestSuite]
public sealed class GameInputTouchTests
{
    /// <summary>The wheel's two levels: a top-level select enters a group; inside one, Back returns and anything else performs.</summary>
    [TestCase]
    public void WheelSelectsFollowTheLevel()
    {
        AssertThat(GameUi.WheelSelectStep(false, 2)).IsEqual((true, false));  // Fun: into the group
        AssertThat(GameUi.WheelSelectStep(true, 3)).IsEqual((true, true));    // Dance: performed
        AssertThat(GameUi.WheelSelectStep(true, 7)).IsEqual((false, false));  // Back: to the top
        AssertThat(GameUi.WheelSelectStep(true, -1)).IsEqual((true, false));  // nothing pointed
    }

    /// <summary>Touch is a pointer for the menus but keeps the pad's prompts; a key or a pad takes the mode back.</summary>
    [TestCase]
    public void TouchModeKeepsPadPrompts()
    {
        bool savedKeys = Engine_UiInputMode.KeyboardMouse, savedTouch = Engine_UiInputMode.Touch;
        try
        {
            Engine_UiInputMode.KeyboardMouse = false;
            int generation = Engine_UiInputMode.Generation;
            Engine_UiInputMode.Touch = true;
            AssertThat(Engine_UiInputMode.KeyboardMouse).IsTrue();
            AssertThat(Engine_UiInputMode.Keycaps).IsFalse();
            AssertThat(Engine_UiInputMode.Generation).IsEqual(generation + 1);
            Engine_UiInputMode.KeyboardMouse = true;
            AssertThat(Engine_UiInputMode.Touch).IsFalse();
            AssertThat(Engine_UiInputMode.Keycaps).IsTrue();
            Engine_UiInputMode.Touch = true;
            Engine_UiInputMode.KeyboardMouse = false;
            AssertThat(Engine_UiInputMode.Touch).IsFalse();
            AssertThat(Engine_UiInputMode.KeyboardMouse).IsFalse();
        }
        finally
        {
            Engine_UiInputMode.KeyboardMouse = savedKeys;
            if (savedTouch) Engine_UiInputMode.Touch = true;
        }
    }

    /// <summary>The menus' ring cursor, with its A, stands for the pad and for touch; the keyboard and mouse go without it.</summary>
    [TestCase]
    public void TouchKeepsTheMenuCursor()
    {
        bool savedKeys = Engine_UiInputMode.KeyboardMouse, savedTouch = Engine_UiInputMode.Touch;
        try
        {
            Engine_UiInputMode.KeyboardMouse = false;
            AssertThat(Engine_FrontEndMenuView.CursorForDevice).IsTrue();
            Engine_UiInputMode.Touch = true;
            AssertThat(Engine_FrontEndMenuView.CursorForDevice).IsTrue();
            Engine_UiInputMode.KeyboardMouse = true;
            AssertThat(Engine_FrontEndMenuView.CursorForDevice).IsFalse();
        }
        finally
        {
            Engine_UiInputMode.KeyboardMouse = savedKeys;
            if (savedTouch) Engine_UiInputMode.Touch = true;
        }
    }
}
