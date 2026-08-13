namespace Refii.FlashGames.PubGames;

public sealed class GamePad
{
    public const int DpadUp = 1;
    public const int DpadDown = 2;
    public const int DpadLeft = 4;
    public const int DpadRight = 8;
    public const int DpadAll = 15;
    public const int Start = 16;
    public const int Back = 32;
    public const int LeftThumbButton = 64;
    public const int RightThumbButton = 128;
    public const int LeftShoulderButton = 256;
    public const int RightShoulderButton = 512;
    public const int AButton = 4096;
    public const int BButton = 8192;
    public const int XButton = 16384;
    public const int YButton = 32768;
    public const int TriggerThreshold = 30;
    public const double LeftThumbDeadzone = 0.23953978087710195;
    public const double RightThumbDeadzone = 0.2651753288369396;

    private int _buttons;
    private int _lastButtons;
    private int _pressedButtons;
    private double _leftTrigger;
    private double _rightTrigger;
    private bool _lastLeftTrigger;
    private bool _lastRightTrigger;
    private bool _pressedLeftTrigger;
    private bool _pressedRightTrigger;
    private bool _releasedLeftTrigger;
    private bool _releasedRightTrigger;
    private double _thumbLX;
    private double _thumbLY;
    private double _thumbRX;
    private double _thumbRY;

    public void Update()
    {
        _pressedButtons = (_lastButtons ^ _buttons) & _buttons;
        _lastButtons = _buttons;

        bool down = _leftTrigger >= TriggerThreshold;
        _pressedLeftTrigger = down && !_lastLeftTrigger;
        _releasedLeftTrigger = !down && _lastLeftTrigger;
        _lastLeftTrigger = down;

        down = _rightTrigger >= TriggerThreshold;
        _pressedRightTrigger = down && !_lastRightTrigger;
        _releasedRightTrigger = !down && _lastRightTrigger;
        _lastRightTrigger = down;
    }

    public void SetValue(
        double leftTrigger, double rightTrigger,
        double thumbLX, double thumbLY, double thumbRX, double thumbRY, int buttons)
    {
        _buttons = buttons;
        _leftTrigger = leftTrigger;
        _rightTrigger = rightTrigger;
        _thumbLX = thumbLX;
        _thumbLY = thumbLY;
        _thumbRX = thumbRX;
        _thumbRY = thumbRY;
    }

    public bool LeftThumbCentered => _thumbLX == 0 && _thumbLY == 0;

    public bool LeftThumbUp => _thumbLY > LeftThumbDeadzone;

    public bool LeftThumbDown => _thumbLY < -LeftThumbDeadzone;

    public bool LeftThumbLeft => _thumbLX < -LeftThumbDeadzone;

    public bool LeftThumbRight => _thumbLX > LeftThumbDeadzone;

    public bool RightThumbCentered => _thumbRX == 0 && _thumbRY == 0;

    public bool RightThumbUp => _thumbRY > RightThumbDeadzone;

    public bool RightThumbDown => _thumbRY < -RightThumbDeadzone;

    public bool RightThumbLeft => _thumbRX < -RightThumbDeadzone;

    public bool RightThumbRight => _thumbRX > RightThumbDeadzone;

    public int Buttons => _buttons;

    public int PressedButtons => _pressedButtons;

    public int DPad => _buttons & DpadAll;

    public bool DPadUp => (_buttons & DpadUp) != 0;

    public bool DPadDown => (_buttons & DpadDown) != 0;

    public bool DPadLeft => (_buttons & DpadLeft) != 0;

    public bool DPadRight => (_buttons & DpadRight) != 0;

    public double ThumbLX => _thumbLX;

    public double ThumbLY => _thumbLY;

    public double ThumbRX => _thumbRX;

    public double ThumbRY => _thumbRY;

    public bool BtnA => (_buttons & AButton) != 0;

    public bool BtnB => (_buttons & BButton) != 0;

    public bool BtnX => (_buttons & XButton) != 0;

    public bool BtnY => (_buttons & YButton) != 0;

    public bool BtnAPressed => (_pressedButtons & AButton) != 0;

    public bool BtnBPressed => (_pressedButtons & BButton) != 0;

    public bool BtnXPressed => (_pressedButtons & XButton) != 0;

    public bool BtnYPressed => (_pressedButtons & YButton) != 0;

    public bool StartDown => (_buttons & Start) != 0;

    public bool BackDown => (_buttons & Back) != 0;

    public bool StartPressed => (_pressedButtons & Start) != 0;

    public bool BackPressed => (_pressedButtons & Back) != 0;

    public bool LeftShoulder => (_buttons & LeftShoulderButton) != 0;

    public bool RightShoulder => (_buttons & RightShoulderButton) != 0;

    public bool LeftShoulderPressed => (_pressedButtons & LeftShoulderButton) != 0;

    public bool RightShoulderPressed => (_pressedButtons & RightShoulderButton) != 0;

    public bool LeftThumb => (_buttons & LeftThumbButton) != 0;

    public bool RightThumb => (_buttons & RightThumbButton) != 0;

    public bool LeftThumbPressed => (_pressedButtons & LeftThumbButton) != 0;

    public bool RightThumbPressed => (_pressedButtons & RightThumbButton) != 0;

    public double LeftTrigger => _leftTrigger;

    public double RightTrigger => _rightTrigger;

    public bool LeftTriggerPressed => _pressedLeftTrigger;

    public bool RightTriggerPressed => _pressedRightTrigger;

    public bool LeftTriggerReleased => _releasedLeftTrigger;

    public bool RightTriggerReleased => _releasedRightTrigger;
}
