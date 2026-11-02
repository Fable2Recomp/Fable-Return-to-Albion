using System;

using Godot;

using Refii.Game;
using Refii.Ui;

namespace Refii.Titles.Fable2.PubGames.Framework;

/// <summary>
/// The pad the guest hands the game: pad 0 as the core folds it (<see cref="GameInput.Pad0"/>),
/// in XInput units, then put through the guest's own stick filter.
/// </summary>
/// <remarks>
/// <para>THE GAME BUTTONS ARE THE CORE'S. The keyboard presses them through the player's
/// <c>key_*</c> bindings, a joypad through its player's remap, the touch controls and the
/// pointer's leftovers (a right click is B, a wheel notch a D-pad step) as everywhere else, so a
/// rebind reaches the board and the prompts name what the player bound
/// (<see cref="Engine_UiKeyGlyphs.LabelFor"/>). The board owns the input while it is up
/// (<c>WorldHasInput</c> is false): the menus' keys and a free cursor.</para>
/// <para>THE BOARD'S OWN POINTER UI is the one input added here: the board has nothing to point at,
/// so a left click on it confirms (A) — where a menu's click on nothing is nothing.</para>
/// <para>The guest reads its pad through <c>sub_830F22B8</c> and passes it to
/// <c>pubGame.SetPadValue(0, LT, RT, LX, LY, RX, RY, wButtons)</c> (<c>sub_82EF1650</c>): triggers
/// 0-255, sticks through <c>sub_830F2178</c> — a SQUARE deadzone (both axes within 7849 on the
/// left, 8689 on the right, reads as centred), then value/32767 when positive and value/32768
/// when negative, with no rescale past the deadzone. Up is +Y. The devices only count while the
/// window has focus; unfocused, the pad reads neutral so every press gets its release.</para>
/// </remarks>
public static class PubGameInput
{
    public const int LeftThumbDeadzone = 7849;
    public const int RightThumbDeadzone = 8689;

    /// <summary>
    /// The device the prompts follow, as the core states it (<see cref="Engine_UiInputMode"/>, the
    /// prompt player's controller family and glyph style).
    /// </summary>
    public static string DeviceName =>
        Engine_UiInputMode.Touch ? "touch"
        : Engine_UiInputMode.Keycaps ? "keyboard"
        : $"{Engine_UiKeyGlyphs.PadFamily} pad, {Engine_UiKeyGlyphs.Style} glyphs";

    /// <summary>One raw XInput snapshot of pad 0.</summary>
    public readonly record struct Sample(
        int LeftTrigger, int RightTrigger,
        int ThumbLX, int ThumbLY, int ThumbRX, int ThumbRY, int Buttons);

    /// <summary>
    /// This frame's pad: the core's pad 0, and — with <paramref name="devices"/> (the window has
    /// focus) — the board's left click.
    /// </summary>
    public static Sample Read(bool devices) =>
        FromPad(GameInput.Pad0, devices && Engine_UiPointer.Free && Godot.Input.IsMouseButtonPressed(MouseButton.Left));

    /// <summary>The core's pad as the board's sample, with the board's left click as A (<paramref name="boardClick"/>).</summary>
    public static Sample FromPad(GameInput.PadState pad, bool boardClick) => new(
        (int)Math.Min(pad.LeftTrigger, 255u), (int)Math.Min(pad.RightTrigger, 255u),
        pad.LeftX, pad.LeftY, pad.RightX, pad.RightY,
        (int)(pad.Buttons & 0xFFFFu) | (boardClick ? GamePad.AButton : 0));

    /// <summary><c>pubGame.SetPadValue</c> with the guest's stick filter applied.</summary>
    public static void Publish(PubGame game, Sample sample)
    {
        (double lx, double ly) = Stick(sample.ThumbLX, sample.ThumbLY, LeftThumbDeadzone);
        (double rx, double ry) = Stick(sample.ThumbRX, sample.ThumbRY, RightThumbDeadzone);
        game.SetPadValue(
            0, sample.LeftTrigger, sample.RightTrigger, lx, ly, rx, ry, sample.Buttons);
    }

    /// <summary><c>sub_830F2178</c>: square deadzone, then /32767 up, /32768 down.</summary>
    public static (double X, double Y) Stick(int x, int y, int deadzone)
    {
        if (Math.Abs(x) <= deadzone && Math.Abs(y) <= deadzone)
        {
            return (0, 0);
        }

        return (Normalise(x), Normalise(y));
    }

    private static double Normalise(int value) => value >= 0 ? value / 32767.0 : value / 32768.0;
}
