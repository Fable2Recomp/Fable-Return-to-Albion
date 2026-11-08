using Godot;
using Refii.Core;
using Refii.Game;
using System;

namespace Refii.AndroidRuntime;

/// <summary>
/// What the app does when nobody is using it: the game left open on a table used to draw and
/// simulate at full rate with the screen held on until the battery was gone.
/// </summary>
/// <remarks>
/// <para>THE SCREEN. Godot's <c>keep_screen_on</c> defaults to on, so Android's own screen timeout
/// never applies. After <see cref="ScreenMinutes"/> with no input play pauses, as it does on a return
/// (<see cref="PauseNow"/>), and the screen is let go. Android then turns it off by its own setting,
/// and the app is stopped. A phone
/// set to stay awake on a charger keeps the screen on, at the menu's frame cap.</para>
/// <para>THE MENU. A menu nobody has touched for <see cref="MenuIdleSeconds"/> draws at
/// <see cref="MenuFrameCap"/> fps: the world behind it stands still. The first input takes the cap
/// off at once. Play, cutscenes and loading screens are never capped, as a load's per-frame work
/// must not wait.</para>
/// <para>IN CO-OP a partner is playing, so neither applies.</para>
/// <para>THE LAUNCHER has nothing to redraw while it waits, so it runs in Godot's low-processor
/// mode, drawing only what changed, and lets the screen go by Android's timeout. An install holds
/// the screen on itself (<c>AndroidPhoneLauncher</c>).</para>
/// <para>THE BATTERY'S LEVEL IS NOT READ. A phone's own battery saver covers it, and a phone that
/// drains on a weak charger reads as charging, so a level would not catch the case this is for.</para>
/// </remarks>
public partial class AndroidIdlePower : Node
{
    /// <summary>The refii.toml key: minutes without input until the screen is let go; 0 never.</summary>
    public const string ScreenMinutesKey = "android_idle_screen_minutes";

    /// <summary>The default of <see cref="ScreenMinutesKey"/>: past the longest scene nobody touches.</summary>
    public const int DefaultScreenMinutes = 10;

    /// <summary>Seconds a menu stands untouched before it is capped.</summary>
    public const double MenuIdleSeconds = 30;

    /// <summary>The frame rate of an untouched menu.</summary>
    public const int MenuFrameCap = 10;

    /// <summary>A stick's rest noise: motion under this is nobody's hand.</summary>
    private const float StickNoise = 0.2f;

    private static bool _menuCapped;
    private static int _configuredCap;

    /// <summary>Asks for the pause a return would press, on its own checks.</summary>
    public Action? PauseNow { get; set; }

    /// <summary>The value of <see cref="ScreenMinutesKey"/>.</summary>
    public int ScreenMinutes { get; set; } = DefaultScreenMinutes;

    private double _idle, _age;
    private bool _screenLetGo;

    /// <summary>The launcher's power policy: draw on change, the screen on Android's timeout.</summary>
    public static void EnterLauncher()
    {
        OS.LowProcessorUsageMode = true;
        DisplayServer.ScreenSetKeepOn(false);
    }

    /// <summary>The frame cap to apply, given the one the settings ask for.</summary>
    public static int FrameCap(int configured)
    {
        _configuredCap = configured;
        return Capped(_menuCapped, configured);
    }

    /// <summary>The lower of a configured cap (0 is none) and the idle menu's, while it applies.</summary>
    public static int Capped(bool menuCapped, int configured) =>
        menuCapped && (configured == 0 || configured > MenuFrameCap) ? MenuFrameCap : configured;

    /// <summary>Whether an untouched menu draws at the cap.</summary>
    public static bool CapMenu(double idleSeconds, bool menuUp, bool panelUp, bool coop) =>
        menuUp && !panelUp && !coop && idleSeconds >= MenuIdleSeconds;

    /// <summary>Whether the screen is left to Android's timeout.</summary>
    public static bool LetScreenGo(double idleSeconds, int screenMinutes, bool coop) =>
        screenMinutes > 0 && !coop && idleSeconds >= screenMinutes * 60.0;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        // The game's own policy, whatever scene came before it.
        OS.LowProcessorUsageMode = false;
        DisplayServer.ScreenSetKeepOn(true);
        GetWindow().WindowInput += OnInput;
    }

    public override void _ExitTree()
    {
        GetWindow().WindowInput -= OnInput;
        _menuCapped = false;
    }

    public override void _Process(double delta)
    {
        _idle += delta;
        _age += delta;
        if (_age < 1) return;
        _age = 0;
        bool coop = Coop;
        _menuCapped = CapMenu(_idle, Engine_StatedPause.Latched, GamePhotoMode.Active, coop);
        bool letGo = LetScreenGo(_idle, ScreenMinutes, coop);
        if (letGo == _screenLetGo) return;
        _screenLetGo = letGo;
        if (letGo)
        {
            GD.Print($"[power] no input for {ScreenMinutes} min: pausing play and letting the screen go");
            PauseNow?.Invoke();
        }
        DisplayServer.ScreenSetKeepOn(!letGo);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationResumed) Touched();
    }

    /// <summary>Whether a partner plays on this guest.</summary>
    private static bool Coop => Engine_TitleGame.Active.LocalCoop || Engine_TitleGame.Active.PeerJoined ||
        Engine_TitleGame.Active.OnlineClient;

    /// <summary>Every input the window gets, before anything can mark it handled.</summary>
    private void OnInput(InputEvent input)
    {
        if (input is InputEventJoypadMotion motion && Mathf.Abs(motion.AxisValue) < StickNoise) return;
        Touched();
    }

    /// <summary>Someone is here: the full rate and the screen, at once.</summary>
    private void Touched()
    {
        _idle = 0;
        if (_menuCapped)
        {
            _menuCapped = false;
            Engine.MaxFps = _configuredCap;
        }
        if (_screenLetGo)
        {
            _screenLetGo = false;
            DisplayServer.ScreenSetKeepOn(true);
        }
    }
}
