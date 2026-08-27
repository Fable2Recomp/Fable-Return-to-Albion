using System;
using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The three HUD widgets the `GUI` natives drive and nothing else does: the expression meter,
/// the golf swing meter, and the minigame-completion tally.
/// </summary>
/// <remarks>
/// The natives set targets; the widget runs a clock. `StartBarJobFill` states a RATE and the
/// bar travels on its own, so each widget here carries the per-frame arithmetic.
/// </remarks>
public sealed class Engine_UiGuiWidgets
{
	/// <summary>Bumped whenever anything changes, so a view can skip untouched frames.</summary>
	public ulong Generation { get; private set; }

	// ================= THE EXPRESSION METER =================================================

	/// <summary>What the expression meter is doing. 0 idle, 1 hero pip, 2 henchman pip, 3 fill.</summary>
	public int ExpressionMode { get; private set; }

	/// <summary>Set when the player cancelled rather than committed.</summary>
	public bool ExpressionCanceled { get; private set; }

	/// <summary>Whether the last commit landed inside the target band.</summary>
	public bool ExpressionHit { get; private set; }

	/// <summary>The travelling pip, 0..100. Only meaningful in modes 1 and 2.</summary>
	public float PipPosition { get; private set; }

	/// <summary>Pip speed in units per second; its sign flips at each end.</summary>
	public float PipSpeed { get; private set; }

	/// <summary>True once a pip has latched — a second start does NOT move it.</summary>
	public bool PipLatched { get; private set; }

	/// <summary>The fill bar's width, 0..100 — this is what `GetFillPercent` returns.</summary>
	public float FillSize { get; private set; }

	/// <summary>The fill bar's centre, 0..100.</summary>
	public float FillPosition { get; private set; }

	private float _fillSizeRate;

	private float _fillSizeTarget;

	private float _fillPositionRate;

	private float _fillPositionTarget;

	/// <summary>An explicit colour from `TurnOnExpressionStaticFill`, or null to use the band.</summary>
	public (byte R, byte G, byte B)? FillColourOverride { get; private set; }

	/// <summary>0 low, 1 medium, 2 high.</summary>
	public int FillBand { get; private set; }

	/// <summary>The three band colours. The guest's defaults are green, yellow, red.</summary>
	public (byte R, byte G, byte B) LowColour { get; private set; } = (0, 255, 0);

	public (byte R, byte G, byte B) MedColour { get; private set; } = (255, 255, 0);

	public (byte R, byte G, byte B) HighColour { get; private set; } = (255, 0, 0);

	/// <summary>Band thresholds as percentages. The guest installs 50 and 80.</summary>
	public float LowToMedium { get; private set; } = 50f;

	public float MediumToHigh { get; private set; } = 80f;

	/// <summary>
	/// The flash element: high band AND a bar-job fill (mode 3) — a static or dynamic fill in
	/// the high band does not flash.
	/// </summary>
	public bool ExpressionFlashing => FillBand == 2 && ExpressionMode == 3;

	/// <summary>`GUI.TurnOnExpressionMeter()`.</summary>
	public void TurnOnExpressionMeter()
	{
		ExpressionCanceled = false;
		ExpressionHit = false;
		++Generation;
	}

	/// <summary>`GUI.TurnOffExpressionMeter()` — freezes both meters where they stand.</summary>
	public void TurnOffExpressionMeter()
	{
		ExpressionMode = 0;
		PipLatched = false;
		_fillSizeRate = 0f;
		_fillPositionRate = 0f;
		_fillSizeTarget = FillSize;
		_fillPositionTarget = FillPosition;
		++Generation;
	}

	/// <summary>
	/// `GUI.StartExpressionMeterPip(position, speed, isHero)`.
	/// </summary>
	/// <remarks>
	/// Only the first call latches: a second start arms the other channel but leaves position
	/// and speed untouched.
	/// </remarks>
	public void StartExpressionPip(float position, float speed, bool hero)
	{
		ExpressionMode = hero ? 1 : 2;
		ExpressionHit = false;
		if (!PipLatched)
		{
			PipLatched = true;
			PipPosition = position;
			PipSpeed = speed;
		}

		++Generation;
	}

	/// <summary>`GUI.StartBarJobFill(percentPerSecond)`.</summary>
	/// <remarks>
	/// The bar grows 0 -> 100 at |rate| while its centre travels 0 -> 50 at half that; together
	/// they make a left-anchored bar in a 0..100 space.
	/// </remarks>
	public void StartBarJobFill(float rate)
	{
		float magnitude = Math.Abs(rate);
		ExpressionMode = 3;
		FillColourOverride = null;
		FillSize = 0f;
		_fillSizeTarget = 100f;
		_fillSizeRate = magnitude;
		FillPosition = 0f;
		_fillPositionTarget = 50f;
		_fillPositionRate = magnitude * 0.5f;
		FillBand = BandFor(magnitude);
		++Generation;
	}

	/// <summary>`GUI.TurnOnExpressionStaticFill(position, size, r, g, b)` — no motion, own colour.</summary>
	public void StartStaticFill(float position, float size, byte r, byte g, byte b)
	{
		ExpressionHit = false;
		FillColourOverride = (r, g, b);
		SetFillImmediate(position, size);
		++Generation;
	}

	/// <summary>`GUI.TurnOnExpressionDynamicFill(from, to, position, rate)` — banded colour.</summary>
	public void StartDynamicFill(float from, float to, float position, float rate)
	{
		ExpressionHit = false;
		FillColourOverride = null;
		FillPosition = Math.Clamp(position, 0f, 100f);
		_fillPositionTarget = FillPosition;
		_fillPositionRate = 0f;
		FillSize = Math.Clamp(from, 0f, 100f);
		_fillSizeTarget = Math.Clamp(to, 0f, 100f);
		_fillSizeRate = rate;
		FillBand = BandFor(from);
		++Generation;
	}

	/// <summary>`GUI.SetLowFillColour` / `SetMedFillColour` / `SetHighFillColour`.</summary>
	/// <remarks>
	/// These do not repaint: the new colour lands at the next start or band change.
	/// </remarks>
	public void SetBandColour(int band, byte r, byte g, byte b)
	{
		switch (band)
		{
			case 0: LowColour = (r, g, b); break;
			case 1: MedColour = (r, g, b); break;
			default: HighColour = (r, g, b); break;
		}

		++Generation;
	}

	/// <summary>`GUI.SetFillColourPercentages(lowToMedium, mediumToHigh)`.</summary>
	public void SetBandThresholds(float lowToMedium, float mediumToHigh)
	{
		LowToMedium = lowToMedium;
		MediumToHigh = mediumToHigh;
		++Generation;
	}

	/// <summary>The colour the fill should draw with right now.</summary>
	public (byte R, byte G, byte B) FillColour =>
		FillColourOverride ?? (FillBand switch
		{
			0 => LowColour,
			1 => MedColour,
			2 => HighColour,
			_ => ((byte)255, (byte)255, (byte)255),
		});

	/// <summary>
	/// Commits the pip — the hit test, which is INCLUSIVE at both ends.
	/// </summary>
	/// <remarks>
	/// Differs from the golf meter's, which is strict at both ends.
	/// </remarks>
	public bool CommitExpressionPip()
	{
		ExpressionHit = PipPosition >= FillPosition - (FillSize * 0.5f) &&
			PipPosition <= FillPosition + (FillSize * 0.5f);
		PipLatched = false;
		ExpressionMode = 0;
		++Generation;
		return ExpressionHit;
	}

	/// <summary>The player cancelled — clears the results and takes the meter down.</summary>
	public void CancelExpressionMeter()
	{
		ExpressionCanceled = true;
		ExpressionHit = false;
		TurnOffExpressionMeter();
	}

	private void SetFillImmediate(float position, float size)
	{
		FillPosition = Math.Clamp(position, 0f, 100f);
		_fillPositionTarget = FillPosition;
		_fillPositionRate = 0f;
		FillSize = Math.Clamp(size, 0f, 100f);
		_fillSizeTarget = FillSize;
		_fillSizeRate = 0f;
	}

	private int BandFor(float value) =>
		value > MediumToHigh ? 2 : value > LowToMedium ? 1 : 0;

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
		bool changed = TickExpression(dt) | TickGolf(dt) | TickMiniGame();
		if (changed)
		{
			++Generation;
		}
	}

	private bool TickExpression(float dt)
	{
		bool changed = false;

		// The pip bounces rather than wrapping: clamped at each end with its speed negated.
		if (PipLatched && ExpressionMode is 1 or 2 && PipSpeed != 0f)
		{
			PipPosition += PipSpeed * dt;
			if (PipPosition > 100f)
			{
				PipPosition = 100f;
				PipSpeed = -PipSpeed;
			}
			else if (PipPosition < 0f)
			{
				PipPosition = 0f;
				PipSpeed = -PipSpeed;
			}

			changed = true;
		}

		if (Advance(ref _fillSizeRate, _fillSizeTarget, dt, FillSize) is { } size)
		{
			FillSize = size;
			changed = true;

			// The band is re-derived from the LIVE size, so a fill crossing a threshold
			// repaints itself mid-travel; an override colour ignores the band entirely.
			if (FillColourOverride is null)
			{
				FillBand = BandFor(size);
			}
		}

		if (Advance(ref _fillPositionRate, _fillPositionTarget, dt, FillPosition) is { } position)
		{
			FillPosition = position;
			changed = true;
		}

		return changed;
	}

	/// <summary>
	/// One meter step, or null when it is already settled.
	/// </summary>
	/// <remarks>
	/// Overshoot stops the meter: the rate is zeroed and the value snaps to the target, so every
	/// start path must set the rate as well as the target.
	/// </remarks>
	private static float? Advance(ref float rate, float target, float dt, float current)
	{
		if (rate == 0f || current == target)
		{
			return null;
		}

		float next = current + (rate * dt);
		if ((rate > 0f && next >= target) || (rate < 0f && next <= target))
		{
			rate = 0f;
			return target;
		}

		return next;
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
