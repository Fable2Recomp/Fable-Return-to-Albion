namespace Refii.Core;

/// <summary>
/// The guest's simulation clock, fitted once for the whole world.
/// </summary>
/// <remarks>
/// ONE CLOCK, OR THINGS DRAWN FROM IT DRIFT APART. `dword_83319650` is a single global counter
/// incremented at the tail of the world update, so every stated thing — a body's placement, the
/// camera, a character's bone pose — belongs to the same tick. Fitting a separate origin per
/// entity gives each its own approximation of that one clock: they converge, but they converge
/// at different rates from different sample counts, and a rig fitted from its own handful of
/// samples is drawn microseconds away from the body it is attached to. Sharing the fit removes
/// the disagreement by construction, and a fit fed by every entity in the world converges far
/// harder than one fed by a single villager.
///
/// The fit itself: a sample for tick T seen at `now` cannot have been stated later than `now`,
/// so `now - T*period` is an upper bound on the origin and the smallest one seen is the best
/// estimate. It creeps back toward larger candidates, so a guest running slower than 1/15 s a
/// tick does not leave the draw point permanently starved.
///
/// The world plays back at 1.0, a hard requirement. The draw point is
/// `(now - origin)/period - latency`, so its speed against real time is
/// `1 - d(origin)/d(now) - period*d(latency)/d(now)`: any movement of either term runs the whole
/// world fast or slow. Both are bounded as a fraction of elapsed real time (never per frame or
/// per sample), so the rate error has a ceiling independent of frame rate and entity count.
/// <see cref="PlaybackRate"/> reports the result and should read 1.000.
/// </remarks>
public static class Engine_TickClock
{
	/// <summary>The guest's tick period: a fixed 1/15 s.</summary>
	public const double TickUsec = 1e6 / 15.0;

	/// <summary>
	/// The guest's own display latency: it renders every HF tick (30 Hz) at alpha 0.5 then 1.0
	/// of the game tick, so the 360 showed the world one HF tick — half a game tick — behind.
	/// </summary>
	public const double GuestLatencyTicks = 0.5;

	/// <summary>
	/// How far behind the newest sample the world is drawn, in ticks. Slewed toward
	/// <see cref="StatedLatencyTicks"/>, the guest's own display-derived draw point, and pinned
	/// by <see cref="Pin"/> (`draw_latency_ticks`) when set. Under this, timelines starve — the
	/// draw point passes the newest sample and holds — and <see cref="Starved"/> counts it.
	/// </summary>
	public static double DrawLatencyTicks { get; private set; } = 1.0;

	/// <summary>`draw_latency_ticks`: a fixed latency; 0 or less follows <see cref="StatedLatencyTicks"/>.</summary>
	public static double Pin;

	/// <summary>
	/// The most the latency may move when adding latency, as a fraction of elapsed real time.
	/// Adding is the safe and urgent direction (it stops a slowdown becoming a freeze), so it
	/// may be quick, at the cost of a brief 2% slow motion.
	/// </summary>
	private const double LatencyRiseTolerance = 0.02;

	/// <summary>
	/// The most the latency may move when removing it: a tenth of the rise, since removing
	/// buys little responsiveness and risks starvation.
	/// </summary>
	private const double LatencyFallTolerance = 0.002;

	/// <summary>
	/// How fast the world is actually played back, 1.0 being real time, smoothed over about a
	/// second. Anything that is not 1.000 is visible as jitter.
	/// </summary>
	public static double PlaybackRate { get; private set; } = 1.0;

	/// <summary>The furthest <see cref="PlaybackRate"/> has strayed from 1.0 since the last reset.</summary>
	public static double PlaybackRateWorst { get; private set; }

	/// <summary>Clears the worst-case excursion so a measurement can start a fresh window.</summary>
	public static void ResetPlaybackRateWorst() => PlaybackRateWorst = 0.0;

	/// <summary>
	/// The frame duration the slack is sized against, in ticks: a high percentile of the recent
	/// frames, not the peak.
	/// </summary>
	/// <remarks>
	/// Not a peak-hold: moving the latency re-times everything drawn, so a rare hitch must not
	/// set the operating point. One pause in <see cref="FrameWindow"/> frames sits above the
	/// percentile and moves nothing (its own frame may starve); a sustained slowdown moves it.
	/// Sustained starvation is handled by <see cref="_slackMarginTicks"/>.
	/// </remarks>
	public static double FrameTicks { get; private set; } = 0.5;

	/// <summary>Frames the percentile is taken over — about a second of them.</summary>
	private const int FrameWindow = 64;

	/// <summary>Which percentile of the window sizes the slack.</summary>
	private const double FramePercentile = 0.90;

	private static readonly double[] _frameRing = new double[FrameWindow];
	private static readonly double[] _frameSorted = new double[FrameWindow];
	private static int _frameRingCount;
	private static int _frameRingAt;

	/// <summary>Draw points that passed a live timeline's newest sample and held — the latency was too short.</summary>
	public static long Starved;

	/// <summary>
	/// Slack over the frame time, in ticks, steered by what the timelines report: a starvation
	/// rate over one sample in 500 raises it, a rate under one in 5,000 lets it settle back.
	/// The walk is taken once per guest frame and a sample lands up to two host frames after
	/// its tick, so no fixed constant fits every frame rate.
	/// </summary>
	private static double _slackMarginTicks = 0.35;

	/// <summary>Timeline samples drawn; with <see cref="Starved"/> the starvation rate.</summary>
	public static long Sampled;

	private static long _rateStarved;
	private static long _rateSampled;
	private static int _rateFrames;
	private const double MinLatencyTicks = GuestLatencyTicks + 0.25;
	/// <summary>A slow frame needs its whole duration of slack, so the cap sits well above one tick.</summary>
	private const double MaxLatencyTicks = 4.0;
	private static double _frameStartUsec = double.NaN;

	/// <summary>Once per frame, first thing: updates the frame-time window, aligns and disciplines the clock, and slews the latency.</summary>
	public static void BeginFrame(double nowUsec)
	{
		bool first = double.IsNaN(_frameStartUsec);
		double elapsedUsec = first ? 0.0 : nowUsec - _frameStartUsec;

		if (!first)
		{
			double frame = elapsedUsec / TickUsec;

			// Over four ticks is a load or a breakpoint, not a frame, and it is not evidence
			// about how long frames take.
			if (frame > 0.0 && frame < 4.0)
			{
				_frameRing[_frameRingAt] = frame;
				_frameRingAt = (_frameRingAt + 1) % FrameWindow;
				if (_frameRingCount < FrameWindow)
				{
					++_frameRingCount;
				}

				System.Array.Copy(_frameRing, _frameSorted, _frameRingCount);
				System.Array.Sort(_frameSorted, 0, _frameRingCount);
				int at = (int)(FramePercentile * (_frameRingCount - 1));
				FrameTicks = _frameSorted[at];
			}
		}

		_frameStartUsec = nowUsec;
		if (++_rateFrames >= 60)
		{
			_rateFrames = 0;
			long sampled = Sampled - _rateSampled;
			long starved = Starved - _rateStarved;
			_rateSampled = Sampled;
			_rateStarved = Starved;
			if (sampled > 1000)
			{
				double rate = (double)starved / sampled;
				if (rate > 0.002)
				{
					_slackMarginTicks = System.Math.Min(_slackMarginTicks + 0.1, 1.5);
				}
				else if (rate < 0.0002)
				{
					_slackMarginTicks = System.Math.Max(_slackMarginTicks - 0.02, 0.1);
				}
			}
		}

		// Tie the host's own count to the guest's tick numbering before anything draws against
		// it; without this the playhead is in the wrong units and everything slides.
		AlignHostClock();

		// The clock itself: fit the guest's period, then discipline the playhead onto it.
		Discipline(nowUsec, elapsedUsec);

		// Slewed against elapsed time, not per frame: the latency's rate of change is a speed
		// error applied to the whole world, held here at a fixed fraction at any frame rate.
		double target = Pin > 0.0 ? Pin : StatedLatencyTicks();

		double latencyMoved;
		if (first || elapsedUsec <= 0.0)
		{
			latencyMoved = target - DrawLatencyTicks;
			DrawLatencyTicks = target;
		}
		else
		{
			double wanted = target - DrawLatencyTicks;
			double tolerance = wanted > 0.0 ? LatencyRiseTolerance : LatencyFallTolerance;
			double budgetTicks = (elapsedUsec / TickUsec) * tolerance;
			latencyMoved = System.Math.Clamp(wanted, -budgetTicks, budgetTicks);
			DrawLatencyTicks += latencyMoved;
		}

		// What the world actually ran at, measured at the draw point rather than summed from
		// its terms: how far the draw point moved against how far real time moved. Catches
		// anything that pins, steps or reverses the playhead.
		double drawNow = HostClockLive
			? _hostTicks + _hostTickOffset - DrawLatencyTicks
			: StatedLive
			? _statedAnchorTicks +
				((nowUsec - _statedAnchorUsec) / 1e6 * GameTicksPerSecond) - DrawLatencyTicks
			: double.IsNaN(_originUsec)
				? double.NaN
				: ((nowUsec - _originUsec) / _periodUsec) - DrawLatencyTicks;

		if (!first && elapsedUsec > 0.0 &&
			!double.IsNaN(drawNow) && !double.IsNaN(_prevDrawTick))
		{
			// A ratio of sums over a window, not a mean of per-frame ratios: the numerator
			// advances in discrete grant steps, and E[a/b] is not E[a]/E[b].
			_rateDrawnTicks += drawNow - _prevDrawTick;
			_rateRealTicks += elapsedUsec / TickUsec;
			if (_rateRealTicks >= RateWindowTicks)
			{
				PlaybackRate = _rateDrawnTicks / _rateRealTicks;
				double excursion = System.Math.Abs(PlaybackRate - 1.0);
				if (excursion > PlaybackRateWorst)
				{
					PlaybackRateWorst = excursion;
				}

				_rateDrawnTicks = 0.0;
				_rateRealTicks = 0.0;
			}
		}

		_prevDrawTick = drawNow;
		PlayheadVsNewest = double.IsNaN(drawNow) ? double.NaN : drawNow - _highestTick;
		_ = latencyMoved;
	}

	/// <summary>
	/// A timeline found the draw point past its newest sample. Counted only when that sample is
	/// current — within a tick of the newest tick seen anywhere — so a mover that has stopped,
	/// whose newest sample the draw point passes by design, is not read as starvation.
	/// </summary>
	public static void NoteStarved(double newestTick)
	{
		if (newestTick + 1.0 >= _highestTick)
		{
			++Starved;
		}
	}

	/// <summary>Ticks drawn, and real ticks elapsed, since the current rate window opened.</summary>
	private static double _rateDrawnTicks;

	private static double _rateRealTicks;

	/// <summary>How long a rate window is, in ticks — 15 is one second.</summary>
	private const double RateWindowTicks = 15.0;

	private static double _originUsec = double.NaN;
	private static double _prevDrawTick = double.NaN;
	private static uint _highestTick;

	/// <summary>
	/// Whether the fitted clock drives the playhead, or is only measured alongside the
	/// per-sample estimator. Off by default — `draw_clock_fit`; outranked by the host clock.
	/// </summary>
	/// <remarks>
	/// Toggled live with `{"op":"render","clockFit":false}`. A gross phase error at seeding is
	/// snapped rather than crawled (see <see cref="Discipline"/>).
	/// </remarks>
	public static bool Disciplined;

	/// <summary>
	/// Godot owns the clock. The playhead comes from the host's own fixed-step accumulator —
	/// the same one that grants the guest its time — rather than from anything measured,
	/// estimated or fitted. Outranks every other mode.
	/// </summary>
	/// <remarks>
	/// `GameSession` grants the time base one step at a time, so its accumulator's remainder is
	/// exactly how far between two guest steps the frame lies. The playback rate is 1.0 by
	/// construction, there is no origin, period or phase to estimate, and it is deterministic.
	/// Exact when the time base is driven; with a free-running guest the two drift by whatever
	/// the guest does on its own.
	/// </remarks>
	public static bool HostOwnsClock = true;

	/// <summary>Game ticks the host has granted, plus the fraction of the way to the next.</summary>
	private static double _hostTicks = double.NaN;

	/// <summary>
	/// What to add to the host's own count to express it in the guest's tick numbering.
	/// </summary>
	/// <remarks>
	/// The host counts the steps it has granted from zero at session start. The guest's counter
	/// is its own: reset to 1 on world teardown (0x8235075C) and restored from a save as
	/// "WorldFrame" (0x82350A34). Stamps are in the guest's numbering.
	///
	/// The offset is constant while the guest consumes grants in order, so it is low-passed —
	/// the raw difference sawtooths by a tick because the guest's counter steps while the host's
	/// position is continuous, and the mean of that is the answer. A large change is a world
	/// reset or a save load, and is taken at once.
	/// </remarks>
	private static double _hostTickOffset;
	private static bool _hostOffsetSeeded;

	/// <summary>The alignment between the host's count and the guest's, for diagnosis.</summary>
	public static double HostTickOffset => _hostTickOffset;

	/// <summary>
	/// How far the host's granted count leads the guest's executed count, in game ticks, and
	/// whether the grant is currently being withheld because of it.
	/// </summary>
	/// <remarks>
	/// Says whether the pacing loop is closed: it should hover under `MaxLeadGameTicks` with
	/// `HostGrantWithheld` flicking true at the ceiling. A lead climbing past that with nothing
	/// withheld means the cap is not firing.
	/// </remarks>
	public static double HostGrantLead { get; set; }

	/// <summary>Whether the last frame's grant was withheld to let the guest catch up.</summary>
	public static bool HostGrantWithheld { get; set; }

	/// <summary>
	/// How many times the alignment has snapped, and the largest jump it made, in ticks.
	/// </summary>
	/// <remarks>
	/// <see cref="AlignHostClock"/> adopts the raw difference outright when it moves more than
	/// <see cref="HostOffsetSnapTicks"/>, moving the draw point discontinuously. Correct for a
	/// world reset or save load; while the game is simply running it should not fire.
	/// <see cref="Restarts"/> counts the guest's counter going backwards, not this.
	/// </remarks>
	public static long HostOffsetSnaps { get; private set; }

	/// <summary>The largest single alignment jump, in ticks, since the last reset.</summary>
	public static double HostOffsetSnapWorst { get; private set; }

	/// <summary>
	/// Game ticks `dword_83319650` (<see cref="HostBridge.SimTick"/>) has lost against the
	/// guest's true game-tick counter.
	/// </summary>
	/// <remarks>
	/// `dword_83319650` is not a clean tick count:
	///
	///     821d46e4  cmplwi cr6, r5, 0
	///     821d46e8  beq    cr6, loc_821D4AC8   ; branch target is PAST the store below
	///     ...
	///     821d4ab8  lis    r10, dword_83319650@ha
	///     821d4abc  lwz    r11, dword_83319650@l(r10)
	///     821d4ac0  addi   r11, r11, 1
	///     821d4ac4  stw    r11, dword_83319650@l(r10)
	///     821d4ac8  addi   r1, r1, 0xD0        ; loc_821D4AC8 — the epilogue
	///
	/// so `sub_821D4628` skips the increment wholesale when its object is not the head element,
	/// and `sub_823D9898` increments the SAME global unconditionally from a second, independent
	/// path (`823d9904`). It is also reset to 1 on teardown (`8235075c`). It can therefore skip a
	/// tick and it can advance twice for one tick.
	///
	/// The guest's true game-tick counter is `*(*(0x83496AB8) + 0x0C) + 0xFC`, which
	/// `RefiiHostGetSimClock` already reads: zeroed once at construction (`82310630`), written by
	/// exactly one instruction thereafter (`821c9044`, +1), never reset, never restored from a
	/// save, and reached only on the game-tick boundary — 15 Hz by construction.
	///
	/// Both are read here in the same frame, so the comparison is exact: this counts the ticks
	/// `dword_83319650` lost, and <see cref="StampAheadTicks"/> the ones it gained.
	/// </remarks>
	public static long StampLostTicks { get; private set; }

	/// <summary>Game ticks the stamp counter has gained — the second increment path firing.</summary>
	public static long StampAheadTicks { get; private set; }

	/// <summary>
	/// The counter every stated sample is stamped with: the guest's true game tick.
	/// </summary>
	/// <remarks>
	/// Not `dword_83319650`, which `sub_821D4628` skips when its object is not the head element
	/// (`821d46e8 beq cr6, loc_821D4AC8`, past the stw at 821d4ac4); a skipped stamp puts two
	/// poses on one tick. One clock means one counter, shared with the pacing loop.
	///
	/// This is the counter the pacing loop already uses: `*(*(0x83496AB8) + 0x0C) + 0xFC`, one
	/// writer (`821c9044`, +1), zeroed once at construction, never reset and never restored from
	/// a save, reached only on the game-tick boundary. Published once a frame so every sample in
	/// a frame is stamped from ONE read of the guest, which is what stamping them together means.
	/// </remarks>
	public static uint GuestGameTick { get; private set; }

	/// <summary>
	/// The stamp, in game ticks, of the state the guest's last finished HF step left in memory:
	/// T − 0.5 after the step that ran tick T, T after the HF-only step that follows it. NaN until
	/// the tick step's parity has been seen.
	/// </summary>
	/// <remarks>
	/// `sub_82276D10` runs the game tick (`sub_821C8FB0`, 0x82277264) only on the step whose index
	/// crosses a multiple of two (`v17 / v5 > v8 / v5`, v5 = 30/15 = 2, 0x82277200), then the HF
	/// pass `sub_821A61B0` (0x822772a4) at alpha `(v17 % 2 + 1) / 2` (0x822771e4): 0.5 on that
	/// step, 1.0 on the next. The guest-frame counter counts that HF pass, one per step, so its
	/// parity on the step where the tick moved is the tick step's parity until the loop is
	/// re-entered. Learned from consecutive frames that straddle a tick change; two straight
	/// disagreements re-learn it.
	///
	/// A STATED STAMP, NOT AN ORDER. Stamping by the order the walk happened to see poses in
	/// (first under a counter = T − 0.5, next = T) is wrong whenever the walk misses a step or
	/// reads a step the session's tick read did not see — measured in res://debug/anim_timing_lab
	/// as a fifth of all frames holding or jumping at 120 Hz and above.
	/// </remarks>
	public static double FrameStamp { get; private set; } = double.NaN;

	private static uint _stampFrame;
	private static uint _stampFrameTick;
	private static int _tickParity = -1;
	private static int _parityDisagreements;

	/// <summary>
	/// Publishes the guest-frame counter and the game tick, read together at the start of the
	/// frame, and derives <see cref="FrameStamp"/>.
	/// </summary>
	public static void PublishGuestFrame(uint frame, uint tick)
	{
		if (tick == 0u)
		{
			FrameStamp = double.NaN;
			return;
		}

		if (_stampFrame != 0u && frame == _stampFrame + 1u && tick == _stampFrameTick + 1u)
		{
			int parity = (int)(frame & 1u);
			if (_tickParity < 0)
			{
				_tickParity = parity;
			}
			else if (parity != _tickParity)
			{
				if (++_parityDisagreements >= 2)
				{
					_tickParity = parity;
					_parityDisagreements = 0;
				}
			}
			else
			{
				_parityDisagreements = 0;
			}
		}

		_stampFrame = frame;
		_stampFrameTick = tick;
		FrameStamp = _tickParity < 0
			? double.NaN
			: tick - ((int)(frame & 1u) == _tickParity ? 0.5 : 0.0);
	}

	/// <summary>
	/// A guest frame (fractional: an instant between two HF steps) as a stamp on
	/// <see cref="FrameStamp"/>'s timeline: each HF step is half a game tick, counted from the frame
	/// the stamp was last derived for. NaN until that stamp exists.
	/// </summary>
	public static double StampOfGuestFrame(double frame) =>
		double.IsNaN(FrameStamp) || _stampFrame == 0u ? double.NaN : FrameStamp + ((frame - _stampFrame) * 0.5);

	/// <summary>Publishes the frame's game tick. Zero (no world) leaves the last one standing.</summary>
	public static void PublishGuestGameTick(uint tick)
	{
		if (tick == 0u || tick == GuestGameTick)
		{
			return;
		}

		// The counter does not swap the playhead: `Engine_DrawnFrame.NoteRestated` does, when a
		// stated value changes, which is not at the counter's own boundary. Swapping here too
		// would advance the anchor twice a tick. A counter going backwards (or jumping) is a new
		// world: the playhead is restarted rather than wound back.
		if (GuestGameTick != 0u && (tick < GuestGameTick || tick - GuestGameTick > 8u))
		{
			Pair.Reset();
		}

		GuestGameTick = tick;
	}

	/// <summary>
	/// The playhead for everything the guest states on its game tick — the camera and every
	/// entity's placement. One instance, so the camera and the body it follows share one clock.
	/// </summary>
	public static readonly Engine_PairPlayhead Pair = new(TickUsec, carry: true, periodOwner: true);

	/// <summary>
	/// How far through the current prev/live pair this frame falls — the fraction to blend the
	/// transform the guest latched at the tick's start toward the one it holds now.
	/// </summary>
	/// <remarks>
	/// See <see cref="Engine_PairPlayhead"/> for the anchoring rules. One read a frame: defers to
	/// <see cref="Refii.Entities.Engine_DrawnFrame.Alpha"/> so the camera and bodies are drawn
	/// at the same point.
	/// </remarks>
	public static double PairAlpha => UseHostPhase && HostClockLive && !double.IsNaN(_hostTicks)
		? HostPhase
		: Refii.Entities.Engine_DrawnFrame.Alpha;

	/// <summary>
	/// The phase the host intended, straight from the pacing accumulator — no observation, no
	/// estimate.
	/// </summary>
	/// <remarks>
	/// `_granted + _accumulated / period` is where Godot meant the world to be, continuous in real
	/// time. The closed pacing loop keeps its rate locked to the guest's; the guest's arrival
	/// still jitters around it (see <see cref="UseHostPhase"/>).
	/// </remarks>
	public static double HostPhase
	{
		get
		{
			double position = _hostTicks + _hostTickOffset;
			double phase = position - System.Math.Floor(position);
			return phase < 0.0 ? 0.0 : phase > 1.0 ? 1.0 : phase;
		}
	}

	/// <summary>
	/// Whether the drawn phase comes from the host's own schedule rather than from the anchored
	/// estimate. Off.
	/// </summary>
	/// <remarks>
	/// The schedule is regular but the guest's arrival is not: when a tick is late the phase has
	/// already wrapped to 0 while the pair is still the old one, and the drawn value snaps back.
	/// Anchoring to the arrival avoids that. Worth revisiting if arrival is made regular
	/// (sub-frame granting). `{"op":"metrics","hostPhase":true}` flips it live.
	/// </remarks>
	public static bool UseHostPhase;

	/// <summary>The anchored estimate, reported alongside so the two can be compared live.</summary>
	public static double PairAlphaAnchored => Pair.Alpha(Godot.Time.GetTicksUsec());

	/// <summary>The pair interval currently measured, against the nominal 1/15 s; reported.</summary>
	public static double PairPeriodTicks => Pair.PeriodRatio;

	/// <summary>Frames drawn past the stated pair, and frames that hit the ceiling and held.</summary>
	public static long PairExtrapolatedFrames => Pair.Extrapolated;

	public static long PairHeldFrames => Pair.Held;



	private static uint _prevStampTick;
	private static uint _prevGameTick;

	/// <summary>
	/// Folds one frame's pair of counters in: the stamp every sample carries, and the guest's
	/// true game tick. Both must advance together; this counts the frames where they did not.
	/// </summary>
	public static void ObserveCounterPair(uint stampTick, uint gameTick)
	{
		if (_prevStampTick != 0u && _prevGameTick != 0u &&
			stampTick >= _prevStampTick && gameTick >= _prevGameTick)
		{
			long stampMoved = stampTick - _prevStampTick;
			long gameMoved = gameTick - _prevGameTick;

			// A world teardown resets the stamp to 1 and a save load restores it, so a large
			// step either way is a re-base and says nothing about the gate.
			if (stampMoved < 64 && gameMoved < 64)
			{
				long drift = stampMoved - gameMoved;
				if (drift < 0)
				{
					StampLostTicks += -drift;
				}
				else if (drift > 0)
				{
					StampAheadTicks += drift;
				}
			}
		}

		_prevStampTick = stampTick;
		_prevGameTick = gameTick;
	}

	/// <summary>Whether the host has published a position this frame AND it is aligned.</summary>
	public static bool HostClockLive { get; private set; }

	/// <summary>A change past this many ticks is a world reset or a save load, not drift.</summary>
	private const double HostOffsetSnapTicks = 3.0;

	/// <summary>How fast the alignment follows — slow, so the per-tick sawtooth averages out.</summary>
	private const double HostOffsetFollow = 0.01;

	/// <summary>
	/// The host's own position on the simulation timeline, in GAME ticks: the steps it has
	/// granted plus the accumulator's remainder toward the next.
	/// </summary>
	/// <remarks>
	/// `steps` counts HIGH-FREQUENCY steps — the rate the guest's loop runs at, 30 a second —
	/// and a game tick is two of them, so the conversion is the guest's own
	/// `30 / 15 = 2` from 0x82276D94. `fraction` is the accumulator's remainder over its period,
	/// which is where between two steps this frame falls.
	/// </remarks>
	public static void PublishHostTicks(long steps, double fraction)
	{
		if (!HostOwnsClock || steps < 0 || double.IsNaN(fraction))
		{
			HostClockLive = false;
			return;
		}

		double clamped = System.Math.Clamp(fraction, 0.0, 1.0);
		double published = (steps + clamped) * 0.5;

		// MONOTONIC, ALWAYS. The playhead is where the world is drawn; moving it backwards
		// replays motion that has already been shown and reads as a jolt, which is worse than
		// any stall. The accumulator can legitimately go back — a withheld grant parks it, a
		// reset clears it — so this is the guard rather than an assertion about the caller.
		if (!double.IsNaN(_hostTicks) && published < _hostTicks)
		{
			published = _hostTicks;
		}

		_hostTicks = published;
		HostClockLive = _hostOffsetSeeded;
	}

	/// <summary>
	/// Ties the host's count to the guest's numbering, from the newest tick the guest has stated.
	/// </summary>
	private static void AlignHostClock()
	{
		if (!HostOwnsClock || double.IsNaN(_hostTicks) || _highestTick == 0u)
		{
			return;
		}

		double target = _highestTick - _hostTicks;

		if (!_hostOffsetSeeded || System.Math.Abs(target - _hostTickOffset) > HostOffsetSnapTicks)
		{
			// First sight, a world reset, or a save load: adopt it rather than crawl, because
			// until it is right nothing on screen is drawn against the right pair of samples.
			if (_hostOffsetSeeded)
			{
				double jump = System.Math.Abs(target - _hostTickOffset);
				++HostOffsetSnaps;
				if (jump > HostOffsetSnapWorst)
				{
					HostOffsetSnapWorst = jump;
				}
			}

			_hostTickOffset = target;
			_hostOffsetSeeded = true;
			HostClockLive = true;
			return;
		}

		// The raw difference sawtooths by a tick — the guest's counter steps, the host's position
		// is continuous — so the mean is the answer and a slow filter finds it without letting
		// the sawtooth into the playhead.
		_hostTickOffset += (target - _hostTickOffset) * HostOffsetFollow;
		HostClockLive = true;
	}

	/// <summary>
	/// The guest's OWN clock drives the playhead, instead of one reconstructed from arrival
	/// times. `draw_clock_stated`; outranks <see cref="Disciplined"/> when a clock is arriving.
	/// </summary>
	/// <remarks>
	/// The guest's rate is not a constant. `sub_82276D10` runs a wall-clock accumulator and
	/// advances its clock by exactly 1/30 s per high-frequency step — but more than one step
	/// behind, it takes one step and re-bases to now, discarding the backlog:
	///
	///     82276fa4  cmpwi cr6, r30, 1
	///     82276fb0  ble   cr6, loc_82276FC8   ; owed &lt;= 1 -&gt; normal
	///     82276fb4  addi  r29, r28, 1         ; advance exactly ONE
	///     82276fb8  fmadd f0, f27, f24, f31   ; f24 = 0.0, so f0 = NOW
	///     82276fbc  stfd  f0, 0x110(r31)      ; sim clock := NOW
	///
	/// So the rate is 15/s while it keeps up, lower under load, with discontinuous resets. The
	/// guest states its clock at `game+0x110` every iteration (0x82276F18).
	///
	/// Off by default: following that clock means following its re-bases, each a discontinuity.
	/// The fix is upstream — the paced time base keeps the guest from falling behind.
	/// </remarks>
	public static bool UseStatedClock;

	/// <summary>The guest's tick period as FITTED, in host microseconds. Nominal until a fit lands.</summary>
	public static double PeriodUsec => _periodUsec;

	/// <summary>Game ticks per second — `dbl_83319660`, stated 15.0 and constant for the process.</summary>
	public const double GameTicksPerSecond = 15.0;

	/// <summary>
	/// The guest's high-frequency step rate: two per game tick. `dbl_83319668`, set to 30.0 by
	/// `Game_InitDatabases` at `0x82310900`, against `dbl_83319660` = 15.0 at `0x8231091c`;
	/// `sub_82276D10` divides them at `0x82276d94` to get its steps-per-tick of exactly 2.
	/// </summary>
	public const double HighFrequencyStepsPerSecond = 30.0;

	/// <summary>Whether a stated clock has arrived and is driving the playhead.</summary>
	public static bool StatedLive { get; private set; }

	private static double _statedAnchorTicks = double.NaN;
	private static double _statedAnchorUsec = double.NaN;
	private static double _statedSeenSeconds = double.NaN;

	/// <summary>
	/// The guest's own simulation clock, in seconds, as read this frame. Push it before
	/// <see cref="BeginFrame"/>; pass NaN when the world is not up.
	/// </summary>
	/// <remarks>
	/// The anchor is re-taken on every NEW stated value and extrapolated with real time in
	/// between, because the guest advances this clock in 1/30 s steps and we draw far more often
	/// than that. A small disagreement is walked off under the phase tolerance — that bound is
	/// the playback-rate guarantee. A LARGE one is the guest re-basing, and following it exactly
	/// is correct.
	/// </remarks>
	public static void ObserveStatedClock(double seconds, double nowUsec)
	{
		if (!UseStatedClock || double.IsNaN(seconds) || seconds < 0.0)
		{
			StatedLive = false;
			return;
		}

		// The same value again: the guest has not stepped, so the anchor still holds and the
		// playhead keeps advancing on real time.
		if (seconds == _statedSeenSeconds)
		{
			return;
		}

		_statedSeenSeconds = seconds;
		double statedTicks = seconds * GameTicksPerSecond;

		if (!StatedLive || double.IsNaN(_statedAnchorTicks))
		{
			StatedLive = true;
			_statedAnchorTicks = statedTicks;
			_statedAnchorUsec = nowUsec;
			return;
		}

		double drawn = _statedAnchorTicks +
			((nowUsec - _statedAnchorUsec) / 1e6 * GameTicksPerSecond);
		double error = statedTicks - drawn;

		if (System.Math.Abs(error) > PhaseSnapTicks)
		{
			// The guest re-based, or we have just come back from a load. Follow it.
			_statedAnchorTicks = statedTicks;
			_statedAnchorUsec = nowUsec;
			return;
		}

		// Walk the disagreement off at the tolerance, so the playhead's speed stays inside the
		// guarantee rather than jumping to each newly stated value.
		double elapsed = nowUsec - _statedAnchorUsec;
		double budget = (elapsed / 1e6 * GameTicksPerSecond) * PhaseTolerance;
		_statedAnchorTicks = drawn + System.Math.Clamp(error, -budget, budget);
		_statedAnchorUsec = nowUsec;
	}

	/// <summary>
	/// Where the playhead sits relative to the newest tick the guest has stated, in ticks.
	/// Negative is healthy — how far behind the guest we draw, near minus the draw latency.
	/// Positive is starvation: rigs hold instead of interpolating.
	/// </summary>
	public static double PlayheadVsNewest { get; private set; } = double.NaN;

	private static double _periodUsec = TickUsec;

	/// <summary>Ticks kept for the fit — about six seconds of them.</summary>
	private const int FitTicks = 90;

	/// <summary>Below this many ticks in the window there is not enough span to fit a rate.</summary>
	private const int MinFitTicks = 8;

	/// <summary>How fast the period follows the fit. Slow: the fit is already stable.</summary>
	private const double PeriodFollow = 0.02;

	/// <summary>
	/// The most the playhead may be nudged against real time, as a fraction of elapsed time.
	/// This is the playback-rate guarantee — everything else holds the playhead still.
	/// </summary>
	private const double PhaseTolerance = 0.005;

	/// <summary>
	/// A phase error past this many ticks is a seeding error, not drift, and is corrected in one
	/// step. Under it the tolerance applies and the playback-rate guarantee holds.
	/// </summary>
	private const double PhaseSnapTicks = 0.75;

	private static bool _phaseSeeded;
	private static bool _lastDisciplined;

	private static readonly uint[] _fitTick = new uint[FitTicks];
	private static readonly double[] _fitArrival = new double[FitTicks];
	private static readonly bool[] _fitUsed = new bool[FitTicks];

	/// <summary>True once a sample has been seen and the draw point can be asked for.</summary>
	public static bool Ready => HostClockLive || StatedLive || !double.IsNaN(_originUsec);

	/// <summary>How many samples have refined the fit — reported, not used.</summary>
	public static long Samples { get; private set; }

	/// <summary>How many times the guest's counter went backwards and the fit restarted.</summary>
	public static int Restarts { get; private set; }

	/// <summary>Folds one stated sample into the fit.</summary>
	public static void Observe(uint tick, double nowUsec)
	{
		// BACKWARDS MEANS A NEW COUNTER, NOT A LATE SAMPLE. The guest's own contract is that
		// this value need not only grow — a level load may restart it — and the minimum rule
		// cannot recover from a poisoned origin on its own, because nothing later is smaller.
		if (tick + 64u < _highestTick)
		{
			_originUsec = double.NaN;
			_highestTick = 0u;
			_periodUsec = TickUsec;
			_prevDrawTick = double.NaN;
			_phaseSeeded = false;
			_hostOffsetSeeded = false;
			System.Array.Clear(_fitUsed);
			++Restarts;
		}

		if (tick > _highestTick)
		{
			_highestTick = tick;
		}

		++Samples;

		// Record the lower envelope: an arrival is only ever late, so the earliest arrival for a
		// tick is the best evidence of when it happened. One slot per tick, modulo the window, so
		// a new tick evicts the old one on its slot. Recorded whether or not the fit drives the
		// clock, so `fittedHz` can be read either way.
		int slot = (int)(tick % FitTicks);
		if (!_fitUsed[slot] || _fitTick[slot] != tick)
		{
			_fitUsed[slot] = true;
			_fitTick[slot] = tick;
			_fitArrival[slot] = nowUsec;
		}
		else if (nowUsec < _fitArrival[slot])
		{
			_fitArrival[slot] = nowUsec;
		}

		if (Disciplined)
		{
			if (double.IsNaN(_originUsec))
			{
				_originUsec = nowUsec - (tick * _periodUsec);
			}

			return;
		}

		// Fallback: the per-sample minimum against the nominal tick. It pins the playhead to the
		// integer tick counter (15 Hz stepping). The period returns to nominal with it, or
		// DrawTick would divide this origin by a period it was never measured against.
		_periodUsec = TickUsec;
		double candidate = nowUsec - (tick * TickUsec);
		_originUsec = double.IsNaN(_originUsec) || candidate < _originUsec
			? candidate
			: _originUsec + ((candidate - _originUsec) * 0.05);
	}

	/// <summary>
	/// Fits the guest's tick period as well as its origin, once per frame, and disciplines the
	/// playhead onto that fit.
	/// </summary>
	/// <remarks>
	/// The guest does not run at exactly 15 Hz, so an origin-only estimator has to chase a
	/// drifting residual and pins the playhead to the integer tick. Least squares over the lower
	/// envelope gives an unbiased slope; the intercept is biased late by the mean arrival delay,
	/// a constant offset. An ordinary clock discipline loop: the period follows the fit slowly,
	/// and the playhead is nudged toward the fit by at most <see cref="PhaseTolerance"/> of
	/// elapsed time — the playback-rate guarantee. `tools/tick_clock_fit_sim.py` models it.
	/// </remarks>
	private static void Discipline(double nowUsec, double elapsedUsec)
	{
		// A toggle re-seeds: the per-sample estimator keeps its origin on the nominal tick and
		// the fit on its own period, so one's origin is meaningless under the other's period.
		if (Disciplined != _lastDisciplined)
		{
			_lastDisciplined = Disciplined;
			_phaseSeeded = false;
		}

		if (!Disciplined || double.IsNaN(_originUsec) || elapsedUsec <= 0.0)
		{
			return;
		}

		int n = 0;
		double sx = 0.0, sy = 0.0, sxx = 0.0, sxy = 0.0;
		for (int i = 0; i < FitTicks; ++i)
		{
			if (!_fitUsed[i] || _fitTick[i] + FitTicks <= _highestTick)
			{
				continue;
			}

			double x = (double)_fitTick[i] - _highestTick;
			double y = _fitArrival[i];
			++n;
			sx += x;
			sy += y;
			sxx += x * x;
			sxy += x * y;
		}

		// Too few ticks to fit a line through — a fresh level, or a guest that has just started.
		if (n < MinFitTicks)
		{
			return;
		}

		double den = (n * sxx) - (sx * sx);
		if (den <= 0.0)
		{
			return;
		}

		double slope = ((n * sxy) - (sx * sy)) / den;

		// A slope far from nominal is a torn read or a restart, not a tick rate.
		if (!(slope > 0.9 * TickUsec) || !(slope < 1.1 * TickUsec))
		{
			return;
		}

		double intercept = (sy - (slope * sx)) / n;
		double fitOrigin = intercept - (_highestTick * slope);

		// Frequency, held continuous. Changing the period alone would move the playhead, so the
		// origin is re-solved to leave it exactly where it was this instant.
		double playhead = (nowUsec - _originUsec) / _periodUsec;
		_periodUsec += (slope - _periodUsec) * PeriodFollow;
		_originUsec = nowUsec - (playhead * _periodUsec);

		// Phase: the only term allowed to move the playhead against real time. A cold start
		// seeds the origin from a single sample, and a multi-tick seed error would take minutes
		// to walk off at the tolerance, so a gross error (or the first fit) snaps at once; only
		// a small error is walked off under the tolerance.
		double wanted = (nowUsec - fitOrigin) / slope;
		double error = wanted - playhead;

		if (!_phaseSeeded || System.Math.Abs(error) > PhaseSnapTicks)
		{
			_phaseSeeded = true;
			_originUsec -= error * _periodUsec;
			return;
		}

		double budget = (elapsedUsec / _periodUsec) * PhaseTolerance;
		_originUsec -= System.Math.Clamp(error, -budget, budget) * _periodUsec;
	}

	/// <summary>
	/// The point on the guest's timeline a frame at <paramref name="nowUsec"/> draws, in ticks
	/// and fractions of one. NaN until the fit has a sample.
	/// </summary>
	public static double DrawTick(double nowUsec)
	{
		++Sampled;

		if (HostClockLive)
		{
			return _hostTicks + _hostTickOffset - DrawLatencyTicks;
		}

		if (StatedLive)
		{
			return _statedAnchorTicks +
				((nowUsec - _statedAnchorUsec) / 1e6 * GameTicksPerSecond) - DrawLatencyTicks;
		}

		return double.IsNaN(_originUsec)
			? double.NaN
			: ((nowUsec - _originUsec) / _periodUsec) - DrawLatencyTicks;
	}

	/// <summary>
	/// How far behind the newest state the world is drawn, in game ticks — derived from the
	/// guest's own formula, not fitted.
	/// </summary>
	/// <remarks>
	/// Fable 2's render thread (`sub_82388380`, the "3D Engine" thread) computes the time it
	/// draws at as
	///
	///     drawTime = predictedPresent - 1/15 - 2*presentPeriod - 1/refresh      (seconds)
	///     82388420  fdiv   f30, f31, f0    ; 1.0 / 15.0
	///     82388474  fdiv   f5,  f31, f6    ; presentPeriod = interval / refresh
	///     82388478  fnmsub f4,  f5, f12, f7 ; - 2 * presentPeriod
	///     8238847C  fsub   f3,  f4, f11    ; - 1 / refresh
	///     82388480  fsel   f30, f3, f3, f29 ; clamped >= 0
	///
	/// clamped at zero, with `predictedPresent` coming from the GPU's swap counters
	/// (`sub_82B6FCE8`). One game tick, two present periods and one refresh period — every term
	/// a stated constant, none of them measured.
	///
	/// Translated rather than copied: the 360 presented every second vblank at 60 Hz,
	/// we present at the host's own refresh and interval, so those are the numbers used. The
	/// result changes only when the display mode does.
	/// </remarks>
	public static double StatedLatencyTicks()
	{
		double refresh = RefreshHz > 1.0 ? RefreshHz : 60.0;
		double presentPeriod = PresentInterval / refresh;
		double seconds = (1.0 / GameTicksPerSecond) + (2.0 * presentPeriod) + (1.0 / refresh);
		return seconds * GameTicksPerSecond;
	}

	/// <summary>The display's refresh rate; the guest read it from `XGetVideoMode`.</summary>
	public static double RefreshHz = 60.0;

	/// <summary>Vblanks per present. The guest shipped 2 (`dword_83319638`); we present every one.</summary>
	public static double PresentInterval = 1.0;

	/// <summary>The newest guest tick any sample has carried.</summary>
	public static uint HighestTick => _highestTick;

	/// <summary>
	/// The playhead: where this frame sits on the guest's tick timeline, in the guest's own
	/// numbering, with no latency subtracted. Every draw point in the engine is this minus its
	/// own lag, so it is the number to compare them against.
	/// </summary>
	public static double Playhead =>
		!HostClockLive || double.IsNaN(_hostTicks) ? double.NaN : _hostTicks + _hostTickOffset;

	/// <summary>
	/// Which of the guest's two HF steps within the current game tick last published, as the
	/// guest numbers them, or -1 when it has not stated a clock.
	/// </summary>
	/// <remarks>
	/// A pose is published inside an HF step, and the step states the time it runs at: `sub_82276D10`
	/// stores `(stepIndex - base)/30 + origin` to `game+0x110` at `0x82276f18`, before the
	/// update and before the publish. The pose that step publishes is the world at the END of
	/// it — `sub_8220DC78` composes `lerp(prev, live, alpha)` with the step's own alpha, and
	/// `0x822771e4` makes that alpha exactly `(i + 1) / 2` — so the pose represents
	/// `game+0x110 + 1/30 s`, not an arrival-order phase.
	///
	/// Not yet established: how the guest's step numbering lines up with
	/// <see cref="GuestGameTick"/>, the stamp's counter. Reported alongside it to settle that.
	/// </remarks>
	/// <param name="seconds">
	/// The guest's sim clock as read this frame — `RefiiHostGetSimClock`, not
	/// <see cref="ObserveStatedClock"/>'s record of it, which is only written when
	/// <see cref="UseStatedClock"/> is on.
	/// </param>
	public static int StatedStepPhase(double seconds)
	{
		if (double.IsNaN(seconds))
		{
			return -1;
		}

		double step = System.Math.Floor((seconds * HighFrequencyStepsPerSecond) + 0.5);
		return (int)(((long)step % 2L + 2L) % 2L);
	}

	/// <summary>The guest's sim clock expressed in game ticks, for comparison with the stamps.</summary>
	public static double StatedTicks(double seconds) =>
		double.IsNaN(seconds) ? double.NaN : seconds * GameTicksPerSecond;

	/// <summary>The slack currently over the frame time, in ticks; reported.</summary>
	public static double SlackMarginTicks => _slackMarginTicks;
}
