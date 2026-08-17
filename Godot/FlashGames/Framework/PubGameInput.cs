using Godot;
using Refii.FlashGames.Framework;
using PubInput = Refii.FlashGames.Framework.Input;

namespace Refii.FlashGames.Framework;

public static class PubGameInput
{
    public const float TriggerScale = 255f;

    private static readonly bool IgnorePads =
        System.Environment.GetEnvironmentVariable("REFII_PUBGAME_NO_PAD") is "1";

    public static void Poll(PubInput input, int device = 0)
    {
        if (IgnorePads || Godot.Input.GetConnectedJoypads().Count == 0)
        {
            input.UpdateFromKeyboard(IsKeyDown);
            return;
        }

        int buttons = 0;
        buttons |= Bit(device, JoyButton.A, GamePad.AButton);
        buttons |= Bit(device, JoyButton.B, GamePad.BButton);
        buttons |= Bit(device, JoyButton.X, GamePad.XButton);
        buttons |= Bit(device, JoyButton.Y, GamePad.YButton);
        buttons |= Bit(device, JoyButton.Start, GamePad.Start);
        buttons |= Bit(device, JoyButton.Back, GamePad.Back);
        buttons |= Bit(device, JoyButton.LeftStick, GamePad.LeftThumbButton);
        buttons |= Bit(device, JoyButton.RightStick, GamePad.RightThumbButton);
        buttons |= Bit(device, JoyButton.LeftShoulder, GamePad.LeftShoulderButton);
        buttons |= Bit(device, JoyButton.RightShoulder, GamePad.RightShoulderButton);
        buttons |= Bit(device, JoyButton.DpadUp, GamePad.DpadUp);
        buttons |= Bit(device, JoyButton.DpadDown, GamePad.DpadDown);
        buttons |= Bit(device, JoyButton.DpadLeft, GamePad.DpadLeft);
        buttons |= Bit(device, JoyButton.DpadRight, GamePad.DpadRight);

        float leftTrigger = Godot.Input.GetJoyAxis(device, JoyAxis.TriggerLeft) * TriggerScale;
        float rightTrigger = Godot.Input.GetJoyAxis(device, JoyAxis.TriggerRight) * TriggerScale;

        input.SetPadValue(
            0,
            leftTrigger,
            rightTrigger,
            Godot.Input.GetJoyAxis(device, JoyAxis.LeftX),
            -Godot.Input.GetJoyAxis(device, JoyAxis.LeftY),
            Godot.Input.GetJoyAxis(device, JoyAxis.RightX),
            -Godot.Input.GetJoyAxis(device, JoyAxis.RightY),
            buttons);
    }

    private static int Bit(int device, JoyButton button, int flag) =>
        Godot.Input.IsJoyButtonPressed(device, button) ? flag : 0;

    private static bool IsKeyDown(int flashKeyCode)
    {
        Key key = flashKeyCode switch
        {
            PubInput.KeyBackspace => Key.Backspace,
            PubInput.KeyEnter => Key.Enter,
            PubInput.KeyLeft => Key.Left,
            PubInput.KeyUp => Key.Up,
            PubInput.KeyRight => Key.Right,
            PubInput.KeyDown => Key.Down,
            PubInput.KeyA => Key.A,
            PubInput.KeyB => Key.B,
            PubInput.KeyL => Key.L,
            PubInput.KeyR => Key.R,
            PubInput.KeyX => Key.X,
            PubInput.KeyY => Key.Y,
            PubInput.KeyNumpad1 => Key.Kp1,
            PubInput.KeyNumpad2 => Key.Kp2,
            PubInput.KeyNumpad3 => Key.Kp3,
            PubInput.KeyNumpad4 => Key.Kp4,
            PubInput.KeyNumpad6 => Key.Kp6,
            PubInput.KeyNumpad7 => Key.Kp7,
            PubInput.KeyNumpad8 => Key.Kp8,
            PubInput.KeyNumpad9 => Key.Kp9,
            _ => Key.None,
        };

        return key != Key.None && Godot.Input.IsKeyPressed(key);
    }
}
