using System;
using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The two HUD widgets the `GUI` natives drive and nothing else does: the golf swing meter
/// and the minigame-completion tally. The expression meter is the guest's own object, read back
/// through RefiiHostGetExpressionMeter and drawn by Engine_UiHudExpressionMeter.
/// </summary>
/// <remarks>
/// The natives set targets; the widget runs a clock. `StartBarJobFill` states a RATE and the
/// bar travels on its own, so each widget here carries the per-frame arithmetic.
/// </remarks>
public sealed class Engine_UiGuiWidgets
{
	/// <summary>Bumped whenever anything changes, so a view can skip untouched frames.</summary>
	public ulong Generation { get; private set; }

	// ================= THE GOLF SWING METER =================================================

	/// <summary>The angular limits the swing travels between, in RADIANS.</summary>
	public const float GolfAngleMin = -2.7415926f;

	public const float GolfAngleMax = -0.4f;

	public const float GolfAngleCentre = -1.5707964f;

	/// <summary>`GolfAngleMax - GolfAngleCentre`, the span a "percentage" is taken against.</summary>
	public const float GolfAngleRange = 1.1707964f;

	/// <summary>The meter is configured and can be opened.</summary>
	public bool GolfConfigured { get; private set; }

	/// <summary>The meter is up and swinging.</summary>
	public bool GolfActive { get; private set; }

	/// <summary>`Display` opens it directly; otherwise a button press does.</summary>
	public bool GolfPressToClose { get; private set; }

	/// <summary>The swing's angle, radians, travelling between the two limits.</summary>
	public float GolfAngle { get; private set; }

	/// <summary>Angular velocity, radians per second; its sign flips at each limit.</summary>
	public float GolfSpeed { get; private set; }

	/// <summary>The sweet spot's HALF-width in radians, shrinking as the swing runs.</summary>
	public float GolfSweetHalfWidth { get; private set; }

	private float _golfMinHalfWidth;

	private float _golfMaxHalfWidth;

	private float _golfShrinkRate;

	/// <summary>Recomputed every frame — a live flag, never a latch.</summary>
	public bool GolfInSweetSpot { get; private set; }

	/// <summary>
	/// Sound names the widgets have asked for since the last drain.
	/// </summary>
	/// <remarks>
	/// The guest plays the golf meter's sounds from inside the widget, so owning the widget
	/// means owning them. A queue, not a flag: a bounce and a close can land in one frame.
	/// </remarks>
	private readonly List<string> _sounds = new();

	/// <summary>Takes the sounds asked for since the last call.</summary>
	public IReadOnlyList<string> TakeSounds()
	{
		if (_sounds.Count == 0)
		{
			return System.Array.Empty<string>();
		}

		var taken = _sounds.ToArray();
		_sounds.Clear();
		return taken;
	}

	/// <summary>
	/// `GUI.DisplayGolfSwingMeter(table)` — six keys, and every one of them is an ANGLE.
	/// </summary>
	/// <remarks>
	/// The key names say pixels and percent; the units are RADIANS. `BarSizePixels`,
	/// `SweetSpotShrinkPixelsPerSecond` and `SweetSpotMovementPixelsPerSecond` are scaled into
	/// the angular range before storing. The clamps are the guest's own.
	/// </remarks>
	public void ConfigureGolf(
		float barSize, float minPercent, float initialPercent,
		float shrink, float movement, bool pressToClose)
	{
		_golfMinHalfWidth = GolfAngleRange * Math.Clamp(minPercent, 0.1f, 0.5f);
		_golfMaxHalfWidth = GolfAngleRange * Math.Clamp(barSize, 0.5f, 1.0f);
		GolfSweetHalfWidth = _golfMaxHalfWidth * Math.Clamp(initialPercent, minPercent, 1.0f);

		// Negative is legal and means the spot grows; the clamp is symmetric.
		_golfShrinkRate = Math.Clamp(shrink, -0.2f, 0.2f);
		GolfSpeed = Math.Clamp(movement, 1.0f, 10.0f);
		GolfPressToClose = pressToClose;
		GolfConfigured = true;

		// Press-to-close opens immediately; otherwise Display only configures.
		if (pressToClose)
		{
			OpenGolf();
		}

		++Generation;
	}

	/// <summary>Puts the meter up and starts the swing at its far end.</summary>
	public void OpenGolf()
	{
		GolfActive = true;
		GolfInSweetSpot = false;
		GolfAngle = GolfAngleMin;
		++Generation;
	}

	/// <summary>
	/// `GUI.CloseGolfSwingMeter()` — and the close is what says whether the swing landed.
	/// </summary>
	public void CloseGolf()
	{
		if (!GolfActive)
		{
			return;
		}

		GolfActive = false;
		_sounds.Add(GolfInSweetSpot
			? "SE_GUI_SWING_METER_SUCCESS"
			: "SE_GUI_SWING_METER_FAIL");
		++Generation;
	}

	// ================= THE MINIGAME TALLY ===================================================

	/// <summary>One row of the tally: a label and a number that counts up to its target.</summary>
	public sealed class MiniGameRow
	{
		public string Label = string.Empty;

		/// <summary>What is on screen right now.</summary>
		public int Displayed;

		/// <summary>Where it is counting to.</summary>
		public int Target;
	}

	/// <summary>How many rows the tally has — a sixth is silently dropped.</summary>
	public const int MiniGameRows = 5;

	private readonly List<MiniGameRow> _miniGame = new();

	/// <summary>The rows in use, in the order they were claimed.</summary>
	public IReadOnlyList<MiniGameRow> MiniGame => _miniGame;

	/// <summary>The tally is up.</summary>
	public bool MiniGameUp { get; private set; }

	/// <summary>`GUI.TurnOnMiniGameComplete(title, button)` — the caller resolves both tags.</summary>
	public void TurnOnMiniGame()
	{
		MiniGameUp = true;
		++Generation;
	}

	/// <summary>
	/// `GUI.AddAmountToMiniGameComplete(label, amount)` — "add" means ADD A ROW.
	/// </summary>
	/// <remarks>
	/// The amount is assigned, not accumulated (`stw r26, 8(r31)`), so two calls with the same
	/// label make two rows. A sixth call finds no free row and is dropped with no error.
	/// </remarks>
	public MiniGameRow? AddMiniGameAmount(string label, int amount)
	{
		if (_miniGame.Count >= MiniGameRows)
		{
			return null;
		}

		var row = new MiniGameRow { Label = label, Target = amount, Displayed = 0 };
		_miniGame.Add(row);
		++Generation;
		return row;
	}

	/// <summary>`GUI.TurnOffMiniGameComplete()`.</summary>
	public void TurnOffMiniGame()
	{
		if (!MiniGameUp)
		{
			return;
		}

		MiniGameUp = false;
		_miniGame.Clear();
		++Generation;
	}

	/// <summary>
	/// The confirm press: first fast-forwards the tally, second closes it.
	/// </summary>
	/// <returns>True when the widget closed.</returns>
	public bool ConfirmMiniGame()
	{
		bool snapped = false;
		foreach (MiniGameRow row in _miniGame)
		{
			if (row.Displayed != row.Target)
			{
				row.Displayed = row.Target;
				snapped = true;
			}
		}

		++Generation;
		if (snapped)
		{
			return false;
		}

		TurnOffMiniGame();
		return true;
	}

	// ================= THE CLOCKS ===========================================================

	/// <summary>
	/// Runs every widget's own motion for one frame.
	/// </summary>
	public void Tick(double delta)
	{
		float dt = (float)delta;
		bool changed = TickGolf(dt) | TickMiniGame();
		if (changed)
		{
			++Generation;
		}
	}

	private bool TickGolf(float dt)
	{
		if (!GolfActive)
		{
			return false;
		}

		// The sweet spot shrinks (or grows, for a negative rate) and is held between its limits.
		GolfSweetHalfWidth = Math.Clamp(
			GolfSweetHalfWidth - (_golfShrinkRate * dt), _golfMinHalfWidth, _golfMaxHalfWidth);

		GolfAngle += GolfSpeed * dt;
		if (GolfAngle > GolfAngleMax)
		{
			GolfAngle = GolfAngleMax;
			GolfSpeed = -GolfSpeed;
			_sounds.Add("SE_GUI_SWING_METER_TARGET_RIGHT");
		}
		else if (GolfAngle < GolfAngleMin)
		{
			GolfAngle = GolfAngleMin;
			GolfSpeed = -GolfSpeed;
			_sounds.Add("SE_GUI_SWING_METER_TARGET_LEFT");
		}

		// Strict at both ends, unlike the expression meter's inclusive test.
		GolfInSweetSpot = GolfAngle > GolfAngleCentre - GolfSweetHalfWidth &&
			GolfAngle < GolfAngleCentre + GolfSweetHalfWidth;
		return true;
	}

	/// <summary>
	/// Advances the tally — one row per tick, by a step that grows with the digit count.
	/// </summary>
	/// <remarks>
	/// The guest breaks on the first row that moved, so rows count up in order, not together.
	/// The step is 1, 19, 199, 1999 … one order of magnitude per digit, clamped so the last step
	/// hits the target exactly.
	/// </remarks>
	private bool TickMiniGame()
	{
		if (!MiniGameUp)
		{
			return false;
		}

		foreach (MiniGameRow row in _miniGame)
		{
			if (row.Displayed == row.Target)
			{
				continue;
			}

			bool down = row.Target < 0;
			int step = down ? -1 : 1;
			for (int value = row.Displayed; Math.Abs(value) >= 10; value /= 10)
			{
				step = (10 * step) + (down ? -9 : 9);
			}

			int next = row.Displayed + step;
			row.Displayed = down ? Math.Max(next, row.Target) : Math.Min(next, row.Target);
			return true;
		}

		return false;
	}
}
