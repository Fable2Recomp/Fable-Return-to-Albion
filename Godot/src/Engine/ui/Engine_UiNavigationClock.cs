namespace Refii.Ui;

/// <summary>
/// `GUI:CanNavigate()` / `GUI:DoNavigate()` translated — the shipped auto-repeat, shared by every
/// scroll (`g_Menu:ScrollUp`/`ScrollDown` open with `if GUI:CanNavigate() then GUI:DoNavigate()`).
/// `NavigationCount` rises while held and clears on release, so the long interval applies once per hold.
///
/// <code>
/// function GUI:CanNavigate()
///   return GUI.Timing.NavigateInterval &lt;= GUI.Timing.TimeSinceLastNavigate
/// end
///
/// function GUI:DoNavigate()
///   GUI.Timing.TimeSinceLastNavigate = 0
///   GUI.Input.NavigationCount = GUI.Input.NavigationCount + 1
///   if GUI.Input.NavigationCount &gt; 1 then
///     GUI.Timing.NavigateInterval = GUI.Timing.SUBSEQUENT_NAVIGATE_INTERVAL
///   end
/// end
/// </code>
/// </summary>
public sealed class Engine_UiNavigationClock
{
	private double _sinceLastNavigate = Engine_UiGuiConstants.InputIntervalClamp;
	private double _interval = Engine_UiGuiConstants.FirstNavigateInterval;
	private int _navigationCount;

	/// <summary>Which way the direction is currently held, so a reversal restarts the ramp.</summary>
	private int _held;

	/// <summary>
	/// The two intervals. Settable: a screen's page level answers to `sub_82881B28`, whose gate is
	/// 0.3s falling to 0.01s — see <see cref="Engine_UiScreenInput"/>.
	/// </summary>
	public double FirstInterval { get; set; } = Engine_UiGuiConstants.FirstNavigateInterval;

	public double SubsequentInterval { get; set; } =
		Engine_UiGuiConstants.SubsequentNavigateInterval;

	/// <summary>Advances the clock. Clamped, per `INPUT_INTERVAL_CLAMP`.</summary>
	public void Tick(double delta)
	{
		_sinceLastNavigate = System.Math.Min(
			_sinceLastNavigate + delta, Engine_UiGuiConstants.InputIntervalClamp);
	}

	/// <summary>`GUI:CanNavigate()`.</summary>
	public bool CanNavigate => _interval <= _sinceLastNavigate;

	/// <summary>`GUI:DoNavigate()` — call it when a navigation actually happened.</summary>
	public void DoNavigate()
	{
		_sinceLastNavigate = 0.0;
		++_navigationCount;
		if (_navigationCount > 1)
		{
			_interval = SubsequentInterval;
		}
	}

	/// <summary>
	/// Direction released. `guiinput.dis main.6 pc68..82` writes `NavigationCount = 0`,
	/// `NavigateInterval = FIRST` and `TimeSinceLastNavigate = FIRST`, so `CanNavigate`
	/// (`main.7 pc6`, interval ≤ elapsed) is true next frame. Every press needs its release.
	/// </summary>
	public void Release()
	{
		Rearm();

		// `TimeSinceLastNavigate = interval` (`main.6 pc82`) makes a re-tap instant.
		_sinceLastNavigate = FirstInterval;
	}

	/// <summary>
	/// The ramp half of a release (`NavigationCount = 0`, `NavigateInterval = FIRST`) without the
	/// elapsed write. Used for a direction reversal: the proto resets only on a full release
	/// (`main.6 pc43..67`) and has no reversal case, so a reversal gets no instant re-tap.
	/// </summary>
	private void Rearm()
	{
		_navigationCount = 0;
		_interval = FirstInterval;
	}

	/// <summary>One frame of a held direction: steps to take, 0 or 1 (`onUpdate` polls the latch once per frame).</summary>
	public int Steps(int direction, double delta)
	{
		// Accumulate first, re-arm second (`main.6` pc8..25 before pc68..82).
		if (direction == 0)
		{
			Tick(delta);
			Release();
			_held = 0;
			return 0;
		}

		// A change of direction is a new press: the ramp does not carry across, but the elapsed
		// counter is not advanced (the proto arms only on a full release, `main.6 pc43..67`).
		if (_held != direction)
		{
			_held = direction;
			Rearm();
		}

		Tick(delta);
		if (!CanNavigate)
		{
			return 0;
		}

		DoNavigate();
		return 1;
	}
}
