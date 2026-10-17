using System;

using Godot;

using PubInput = Refii.FlashGames.Framework.Input;

namespace Refii.FlashGames.Framework;

/// <summary>
/// The pad the guest hands the game: every connected joypad and the keyboard merged into pad 0,
/// in XInput units, then put through the guest's own stick filter.
/// </summary>
/// <remarks>
/// The guest reads its pad through <c>sub_830F22B8</c> and passes it to
/// <c>pubGame.SetPadValue(0, LT, RT, LX, LY, RX, RY, wButtons)</c> (<c>sub_82EF1650</c>): triggers
/// 0-255, sticks through <c>sub_830F2178</c> — a SQUARE deadzone (both axes within 7849 on the
/// left, 8689 on the right, reads as centred), then value/32767 when positive and value/32768
/// when negative, with no rescale past the deadzone. Up is +Y. Devices only count while the
/// window has focus; unfocused, the pad is published neutral so every press gets its release.
/// </remarks>
public static class PubGameInput
{
    public const int LeftThumbDeadzone = 7849;
    public const int RightThumbDeadzone = 8689;

    private static readonly bool IgnorePads =
        System.Environment.GetEnvironmentVariable("REFII_PUBGAME_NO_PAD") is "1";

    /// <summary>One raw XInput snapshot of pad 0.</summary>
    public readonly record struct Sample(
        int LeftTrigger, int RightTrigger,
        int ThumbLX, int ThumbLY, int ThumbRX, int ThumbRY, int Buttons);

    public static Sample Read(bool devices)
    {
        if (!devices)
        {
            return default;
        }

        int buttons = KeyboardButtons(out int leftTrigger, out int rightTrigger,
            out int leftX, out int leftY, out int rightX, out int rightY);

        // The mouse's two buttons are the pad's A and B — the pub games' only answers are
        // confirm and back, and their own art carries the prompts.
        buttons |= Godot.Input.IsMouseButtonPressed(MouseButton.Left) ? GamePad.AButton : 0;
        buttons |= Godot.Input.IsMouseButtonPressed(MouseButton.Right) ? GamePad.BButton : 0;

        if (!IgnorePads)
        {
            foreach (int pad in Godot.Input.GetConnectedJoypads())
            {
                buttons |= JoypadButtons(pad);
                leftTrigger = Math.Max(leftTrigger, Trigger(pad, JoyAxis.TriggerLeft));
                rightTrigger = Math.Max(rightTrigger, Trigger(pad, JoyAxis.TriggerRight));
                leftX = Dominant(leftX, Axis(pad, JoyAxis.LeftX));
                leftY = Dominant(leftY, -Axis(pad, JoyAxis.LeftY));
                rightX = Dominant(rightX, Axis(pad, JoyAxis.RightX));
                rightY = Dominant(rightY, -Axis(pad, JoyAxis.RightY));
            }
        }

        return new Sample(leftTrigger, rightTrigger, leftX, leftY, rightX, rightY, buttons);
    }

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

    private static int Dominant(int a, int b) => Math.Abs(b) > Math.Abs(a) ? b : a;

    private static int Trigger(int pad, JoyAxis axis) =>
        Math.Clamp((int)MathF.Round(Godot.Input.GetJoyAxis(pad, axis) * 255f), 0, 255);

    private static int Axis(int pad, JoyAxis axis) =>
        Math.Clamp((int)MathF.Round(Godot.Input.GetJoyAxis(pad, axis) * 32767f), -32768, 32767);

    private static int JoypadButtons(int pad)
    {
        int buttons = 0;
        buttons |= Bit(pad, JoyButton.A, GamePad.AButton);
        buttons |= Bit(pad, JoyButton.B, GamePad.BButton);
        buttons |= Bit(pad, JoyButton.X, GamePad.XButton);
        buttons |= Bit(pad, JoyButton.Y, GamePad.YButton);
        buttons |= Bit(pad, JoyButton.Start, GamePad.Start);
        buttons |= Bit(pad, JoyButton.Back, GamePad.Back);
        buttons |= Bit(pad, JoyButton.LeftStick, GamePad.LeftThumbButton);
        buttons |= Bit(pad, JoyButton.RightStick, GamePad.RightThumbButton);
        buttons |= Bit(pad, JoyButton.LeftShoulder, GamePad.LeftShoulderButton);
        buttons |= Bit(pad, JoyButton.RightShoulder, GamePad.RightShoulderButton);
        buttons |= Bit(pad, JoyButton.DpadUp, GamePad.DpadUp);
        buttons |= Bit(pad, JoyButton.DpadDown, GamePad.DpadDown);
        buttons |= Bit(pad, JoyButton.DpadLeft, GamePad.DpadLeft);
        buttons |= Bit(pad, JoyButton.DpadRight, GamePad.DpadRight);
        return buttons;
    }

    private static int Bit(int pad, JoyButton button, int flag) =>
        Godot.Input.IsJoyButtonPressed(pad, button) ? flag : 0;

    /// <summary>
    /// The keyboard as a pad, with <c>Input::UpdateFromKeyboard</c>'s key map: arrows are the
    /// D-pad and the left stick, the numpad the right stick, triggers and shoulders.
    /// </summary>
    private static int KeyboardButtons(
        out int leftTrigger, out int rightTrigger,
        out int leftX, out int leftY, out int rightX, out int rightY)
    {
        int buttons = 0;
        leftX = Key(PubInput.KeyRight) - Key(PubInput.KeyLeft);
        leftY = Key(PubInput.KeyUp) - Key(PubInput.KeyDown);
        rightX = Key(PubInput.KeyNumpad6) - Key(PubInput.KeyNumpad4);
        rightY = Key(PubInput.KeyNumpad8) - Key(PubInput.KeyNumpad2);
        leftX *= 32767;
        leftY *= 32767;
        rightX *= 32767;
        rightY *= 32767;
        leftTrigger = Key(PubInput.KeyNumpad7) * 255;
        rightTrigger = Key(PubInput.KeyNumpad9) * 255;

        buttons |= Key(PubInput.KeyRight) * GamePad.DpadRight;
        buttons |= Key(PubInput.KeyLeft) * GamePad.DpadLeft;
        buttons |= Key(PubInput.KeyUp) * GamePad.DpadUp;
        buttons |= Key(PubInput.KeyDown) * GamePad.DpadDown;
        buttons |= Key(PubInput.KeyA) * GamePad.AButton;
        buttons |= Key(PubInput.KeyB) * GamePad.BButton;
        buttons |= Key(PubInput.KeyX) * GamePad.XButton;
        buttons |= Key(PubInput.KeyY) * GamePad.YButton;
        buttons |= Key(PubInput.KeyL) * GamePad.LeftThumbButton;
        buttons |= Key(PubInput.KeyR) * GamePad.RightThumbButton;
        buttons |= Key(PubInput.KeyNumpad1) * GamePad.LeftShoulderButton;
        buttons |= Key(PubInput.KeyNumpad3) * GamePad.RightShoulderButton;
        buttons |= Key(PubInput.KeyEnter) * GamePad.Start;
        buttons |= Key(PubInput.KeyBackspace) * GamePad.Back;
        return buttons;
    }

    private static int Key(int flashKeyCode)
    {
        Godot.Key key = flashKeyCode switch
        {
            PubInput.KeyBackspace => Godot.Key.Backspace,
            PubInput.KeyEnter => Godot.Key.Enter,
            PubInput.KeyLeft => Godot.Key.Left,
            PubInput.KeyUp => Godot.Key.Up,
            PubInput.KeyRight => Godot.Key.Right,
            PubInput.KeyDown => Godot.Key.Down,
            PubInput.KeyA => Godot.Key.A,
            PubInput.KeyB => Godot.Key.B,
            PubInput.KeyL => Godot.Key.L,
            PubInput.KeyR => Godot.Key.R,
            PubInput.KeyX => Godot.Key.X,
            PubInput.KeyY => Godot.Key.Y,
            PubInput.KeyNumpad1 => Godot.Key.Kp1,
            PubInput.KeyNumpad2 => Godot.Key.Kp2,
            PubInput.KeyNumpad3 => Godot.Key.Kp3,
            PubInput.KeyNumpad4 => Godot.Key.Kp4,
            PubInput.KeyNumpad6 => Godot.Key.Kp6,
            PubInput.KeyNumpad7 => Godot.Key.Kp7,
            PubInput.KeyNumpad8 => Godot.Key.Kp8,
            PubInput.KeyNumpad9 => Godot.Key.Kp9,
            _ => Godot.Key.None,
        };

        return key != Godot.Key.None && Godot.Input.IsKeyPressed(key) ? 1 : 0;
    }
}
