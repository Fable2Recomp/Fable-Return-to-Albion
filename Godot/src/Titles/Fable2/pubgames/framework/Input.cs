using System;

namespace Refii.Titles.Fable2.PubGames.Framework;

public sealed class Input
{
    public const int MaxPads = 4;

    private readonly GamePad?[] _pads = new GamePad?[MaxPads];
    private readonly GamePad _keyboardPad = new();

    public void SetPadValue(
        int index, double leftTrigger, double rightTrigger,
        double thumbLX, double thumbLY, double thumbRX, double thumbRY, int buttons)
    {
        if (index < 0 || index >= MaxPads)
        {
            return;
        }

        GamePad pad = _pads[index] ??= new GamePad();
        pad.SetValue(leftTrigger, rightTrigger, thumbLX, thumbLY, thumbRX, thumbRY, buttons);
    }

    public GamePad? GetPadState(int index) =>
        index >= 0 && index < MaxPads ? _pads[index] : null;

    public GamePad GetKeyboardPadState() => _keyboardPad;

    public void Update()
    {
        foreach (GamePad? pad in _pads)
        {
            pad?.Update();
        }
    }

    public void SetKeyboardState(
        double leftTrigger, double rightTrigger,
        double thumbLX, double thumbLY, double thumbRX, double thumbRY, int buttons)
    {
        _keyboardPad.SetValue(leftTrigger, rightTrigger, thumbLX, thumbLY, thumbRX, thumbRY, buttons);
        _keyboardPad.Update();
    }

    public const int KeyBackspace = 8;
    public const int KeyEnter = 13;
    public const int KeyLeft = 37;
    public const int KeyUp = 38;
    public const int KeyRight = 39;
    public const int KeyDown = 40;
    public const int KeyA = 65;
    public const int KeyB = 66;
    public const int KeyL = 76;
    public const int KeyR = 82;
    public const int KeyX = 88;
    public const int KeyY = 89;
    public const int KeyNumpad1 = 97;
    public const int KeyNumpad2 = 98;
    public const int KeyNumpad3 = 99;
    public const int KeyNumpad4 = 100;
    public const int KeyNumpad6 = 102;
    public const int KeyNumpad7 = 103;
    public const int KeyNumpad8 = 104;
    public const int KeyNumpad9 = 105;

    public void UpdateFromKeyboard(Func<int, bool> isDown)
    {
        double leftTrigger = 0;
        double rightTrigger = 0;
        double thumbLX = 0;
        double thumbLY = 0;
        double thumbRX = 0;
        double thumbRY = 0;
        int buttons = 0;

        if (isDown(KeyRight))
        {
            buttons |= GamePad.DpadRight;
            thumbLX += 1;
        }

        if (isDown(KeyLeft))
        {
            buttons |= GamePad.DpadLeft;
            thumbLX -= 1;
        }

        if (isDown(KeyUp))
        {
            buttons |= GamePad.DpadUp;
            thumbLY += 1;
        }

        if (isDown(KeyDown))
        {
            buttons |= GamePad.DpadDown;
            thumbLY -= 1;
        }

        if (isDown(KeyNumpad6))
        {
            thumbRX += 1;
        }

        if (isDown(KeyNumpad4))
        {
            thumbRX -= 1;
        }

        if (isDown(KeyNumpad8))
        {
            thumbRY += 1;
        }

        if (isDown(KeyNumpad2))
        {
            thumbRY -= 1;
        }

        if (isDown(KeyA))
        {
            buttons |= GamePad.AButton;
        }

        if (isDown(KeyB))
        {
            buttons |= GamePad.BButton;
        }

        if (isDown(KeyX))
        {
            buttons |= GamePad.XButton;
        }

        if (isDown(KeyY))
        {
            buttons |= GamePad.YButton;
        }

        if (isDown(KeyL))
        {
            buttons |= GamePad.LeftThumbButton;
        }

        if (isDown(KeyR))
        {
            buttons |= GamePad.RightThumbButton;
        }

        if (isDown(KeyNumpad7))
        {
            leftTrigger = 255;
        }

        if (isDown(KeyNumpad9))
        {
            rightTrigger = 255;
        }

        if (isDown(KeyNumpad1))
        {
            buttons |= GamePad.LeftShoulderButton;
        }

        if (isDown(KeyNumpad3))
        {
            buttons |= GamePad.RightShoulderButton;
        }

        if (isDown(KeyEnter))
        {
            buttons |= GamePad.Start;
        }

        if (isDown(KeyBackspace))
        {
            buttons |= GamePad.Back;
        }

        SetKeyboardState(
            leftTrigger, rightTrigger, thumbLX, thumbLY, thumbRX, thumbRY, buttons);
    }
}
