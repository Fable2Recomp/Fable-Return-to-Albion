namespace Refii.Core;

/// <summary>
/// How many game ticks of the guest's world a drawn frame covers — what every playhead on
/// Godot's clock advances by, at the motion the guest states per tick: the frame's real time at
/// the guest's own pace.
/// </summary>
/// <remarks>
/// NOT WALL TIME ALONE. The guest does not always tick at 15 a second: under load it advances one
/// step and re-bases (`sub_82276D10` 0x82276FA4), and runs measured at 64% of real time have been
/// logged (`[game-pace]`). A playhead moved by `step × 15` per real second outran the guest by a
/// third there, ran into its correction bound and snapped backwards — NPCs hitching on a walk.
/// The pace is the world's draw point's (<see cref="Refii.Entities.Engine_DrawnFrame.DrawTick"/>)
/// own rate, so a clip, a body and the camera slow down together, which is the guest's own rule:
/// below pace the world runs slow rather than lurching.
///
/// SMOOTHED, NOT FOLLOWED FRAME BY FRAME. The draw point's per-frame advance carries the shared
/// playhead's anchor corrections; passed straight through they were a ±5% wobble in every limb
/// (res://debug/anim_timing_lab). Over <see cref="PaceSeconds"/> the pace is 1.0 in normal play
/// and finds a slow guest within half a second; the playheads' steering takes the rest.
/// </remarks>
public static class Engine_DrawAdvance
{
	/// <summary>The time constant the pace is smoothed over.</summary>
	public const double PaceSeconds = 0.4;

	/// <summary>A jump of the draw point past this is a re-base, not motion, and measures nothing.</summary>
	public const double MaxTicks = 8.0;

	/// <summary>The guest's world against real time, 1.0 at full pace; for the debug channel.</summary>
	public static double Pace { get; private set; } = 1.0;

	private const int WindowSize = 16;
	private static readonly double[] _winSeconds = new double[WindowSize];
	private static readonly double[] _winMoved = new double[WindowSize];
	private static int _winCount;
	private static int _winAt;
	private static double _sumSeconds;
	private static double _sumMoved;

	private static ulong _paceUsec;
	private static double _paceDrawTick = double.NaN;

	private static void ClearWindow()
	{
		_winCount = 0;
		_winAt = 0;
		_sumSeconds = 0.0;
		_sumMoved = 0.0;
	}

	/// <summary>Resets all pace measurement state cleanly for deterministic automated tests.</summary>
	public static void ResetForTests()
	{
		Pace = 1.0;
		_paceUsec = 0UL;
		_paceDrawTick = double.NaN;
		ClearWindow();
	}

	/// <summary>
	/// The ticks a playhead advances for <paramref name="seconds"/> of drawn time. The pace is
	/// measured once per frame, from the first caller's frame instant and draw point. Zero while
	/// the guest states a pause (<see cref="Engine_StatedPause"/>), and the pace measurement is
	/// re-seeded on release so the held draw point is never read as a slow guest.
	/// </summary>
	public static double Ticks(double seconds, ulong nowUsec, double drawTick)
	{
		if (Engine_StatedPause.Latched)
		{
			_paceUsec = 0UL;
			_paceDrawTick = double.NaN;
			ClearWindow();
			return 0.0;
		}

		Measure(nowUsec, drawTick);
		return seconds * Engine_TickClock.GameTicksPerSecond * Pace;
	}

	/// <summary>
	/// The world time a `_Process` consumer advances by for a frame of <paramref name="delta"/>
	/// real seconds: the frame at the guest's pace, 0 while the guest states a pause. Identical
	/// to <paramref name="delta"/> at full pace.
	/// </summary>
	public static double Seconds(double delta) => Engine_StatedPause.Latched ? 0.0 : delta * Pace;

	private static void Measure(ulong nowUsec, double drawTick)
	{
		if (nowUsec == _paceUsec)
		{
			return;
		}

		ulong lastUsec = _paceUsec;
		double lastTick = _paceDrawTick;
		_paceUsec = nowUsec;
		_paceDrawTick = drawTick;
		if (lastUsec == 0UL || nowUsec <= lastUsec || double.IsNaN(drawTick) || double.IsNaN(lastTick))
		{
			ClearWindow();
			return;
		}

		double seconds = (nowUsec - lastUsec) * 1e-6;
		double moved = drawTick - lastTick;
		if (seconds > 0.25 || moved < -0.5 || moved > MaxTicks)
		{
			ClearWindow();
			return;
		}

		if (_winCount == WindowSize)
		{
			_sumSeconds -= _winSeconds[_winAt];
			_sumMoved -= _winMoved[_winAt];
		}
		else
		{
			++_winCount;
		}

		_winSeconds[_winAt] = seconds;
		_winMoved[_winAt] = moved;
		_sumSeconds += seconds;
		_sumMoved += moved;
		_winAt = (_winAt + 1) % WindowSize;

		if (_sumSeconds > 1e-9)
		{
			double instant = System.Math.Clamp(_sumMoved / (_sumSeconds * Engine_TickClock.GameTicksPerSecond), 0.0, 2.0);
			Pace += (instant - Pace) * (1.0 - System.Math.Exp(-seconds / PaceSeconds));
		}
	}
}
